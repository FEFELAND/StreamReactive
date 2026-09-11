using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;

namespace StreamReactive;

internal sealed class TwitchChatReader : IDisposable
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Thread? _thread;
    private volatile bool _running;
    private string? _channel;
    private readonly Dictionary<string, EmoteCache.EmoteInfo> _emotes;

    internal TwitchChatReader(Dictionary<string, EmoteCache.EmoteInfo> emotes)
    {
        _emotes = emotes;
    }

    internal void Connect(string channel)
    {
        if (_running) Disconnect();
        _channel = channel.ToLowerInvariant();
        _running = true;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "StreamReactive IRC" };
        _thread.Start();
    }

    internal void Disconnect()
    {
        _running = false;
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _writer = null;
        _reader = null;
        _client = null;
        // Deliberately NOT joining the worker thread: Disconnect runs both from
        // the main thread (a settings change) and from the worker thread itself.
        // Joining would freeze the UI for up to 3s, or deadlock on a self-join.
        // The thread exits on its own once _running is false / its stream closes.
    }

    private void ReadLoop()
    {
        try
        {
            Plugin.Log.Info("TwitchChatReader: connecting to irc.chat.twitch.tv:6697...");
            _client = new TcpClient();
            _client.Connect("irc.chat.twitch.tv", 6697);
            var ssl = new SslStream(_client.GetStream(), false, (_, _, _, _) => true);
            ssl.AuthenticateAsClient("irc.chat.twitch.tv");
            _reader = new StreamReader(ssl);
            _writer = new StreamWriter(ssl) { AutoFlush = true };

            var nick = "justinfan" + new Random().Next(10000, 99999);
            _writer.WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");
            _writer.WriteLine("CAP END");
            _writer.WriteLine($"NICK {nick}");
            _writer.WriteLine($"JOIN #{_channel}");
            Plugin.Log.Info($"TwitchChatReader: connected as {nick}, joining #{_channel}.");

            while (_running && _reader.ReadLine() is { } line)
            {
                if (line.StartsWith("PING"))
                {
                    _writer.WriteLine("PONG :tmi.twitch.tv");
                    continue;
                }

                if (!line.Contains("PRIVMSG")) continue;

                var user = ParseUser(line);

                // Always forward the full chat message (for the Chat panel), in
                // addition to any emote effects triggered below.
                var message = ParseMessage(line);
                if (!string.IsNullOrEmpty(message))
                    RuntimeHooks.EnqueueChatMessage(user, message, ParseUserColor(line), ParseBadges(line));

                var emotes = ParseEmotes(line);
                if (emotes.Length > 0)
                {
                    NoteCosmeticController.VerboseLog($"TwitchChatReader: emote(s) from '{user}': {string.Join(", ", emotes)}");
                    RuntimeHooks.EnqueueEmoteEvent(user, emotes);
                }
            }

            Plugin.Log.Info("TwitchChatReader: read loop ended.");
        }
        catch (Exception ex)
        {
            if (_running)
                Plugin.Log.Warn($"TwitchChatReader: connection failed/lost: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    private string[] ParseEmotes(string line)
    {
        var found = new List<string>();

        // Name-based detection for 7TV / BTTV / FFZ emotes. Those aren't in
        // Twitch's native emote tag, so they're matched by chat word. Matching
        // is case-sensitive (emote names are case-sensitive on every platform).
        var colon = line.LastIndexOf(':');
        var message = colon >= 0 ? line.Substring(colon + 1) : line;
        var words = message.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (_emotes.ContainsKey(word))
                found.Add(word);
        }

        // Twitch-native emotes (global + any channel's subscriber emotes) are
        // listed in the IRC `emotes` tag as numeric IDs. Map each to Twitch's
        // CDN so they render without any catalog or credentials.
        var privIdx = line.IndexOf("PRIVMSG", StringComparison.Ordinal);
        var tagSection = privIdx > 0 ? line.Substring(0, privIdx) : line;
        var emotesTag = ExtractTag(tagSection, "emotes");
        if (!string.IsNullOrEmpty(emotesTag))
        {
            // Twitch separates distinct emotes with '/' and multiple ranges of
            // the same emote with ',': emotes=25:0-4/1900:7-11,13-17
            foreach (var part in emotesTag!.Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                var id = part.Split(':')[0];
                if (string.IsNullOrEmpty(id)) continue;
                var code = EmoteCache.Instance.EnsureTwitchEmote(id);
                NoteCosmeticController.VerboseLog($"TwitchChatReader: detected Twitch emote id '{id}' -> code '{code}'");
                if (!found.Contains(code)) found.Add(code);
            }
        }

        // Optional Unicode emoji rendering (off by default).
        if (PluginConfig.Instance?.EmojiSupportEnabled == true)
        {
            foreach (var key in ExtractEmojis(message))
            {
                var code = EmoteCache.Instance.EnsureEmojiEmote(key);
                if (!found.Contains(code)) found.Add(code);
            }
        }

        return found.ToArray();
    }

    private static string ParseUser(string line)
    {
        var privIdx = line.IndexOf("PRIVMSG", StringComparison.Ordinal);
        var head = privIdx > 0 ? line.Substring(0, privIdx) : line;
        var c = head.LastIndexOf(':');
        var e = c >= 0 ? head.IndexOf('!', c) : -1;
        return (c >= 0 && e > c) ? head.Substring(c + 1, e - c - 1) : "unknown";
    }

    // The PRIVMSG payload is everything after the last ':' (which separates the
    // message text from the prefix/tags): ":user!user@user PRIVMSG #chan :hello".
    private static string ParseMessage(string line)
    {
        var colon = line.LastIndexOf(':');
        return colon >= 0 ? line.Substring(colon + 1) : string.Empty;
    }

    // The viewer's Twitch chat color arrives as a hex tag (`color=#FF0000`) when
    // they've set one. Returns "#RRGGBB" or null when unset/unavailable.
    private static string? ParseUserColor(string line)
    {
        var privIdx = line.IndexOf("PRIVMSG", StringComparison.Ordinal);
        var tagSection = privIdx > 0 ? line.Substring(0, privIdx) : line;
        var color = ExtractTag(tagSection, "color");
        if (color == null) return null;
        if (color.Length < 7 || color == "#") return null;
        return color;
    }

    // Builds a short badge label from the IRC `badges` tag (e.g.
    // "broadcaster/1,moderator/1,vip/1,subscriber/1"). Seen as plain text tags;
    // the full image badges are not fetched here.
    private static string ParseBadges(string line)
    {
        var privIdx = line.IndexOf("PRIVMSG", StringComparison.Ordinal);
        var tagSection = privIdx > 0 ? line.Substring(0, privIdx) : line;
        var badges = ExtractTag(tagSection, "badges");
        if (string.IsNullOrEmpty(badges))
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var pair in badges!.Split(','))
        {
            var name = pair.Split('/')[0];
            switch (name)
            {
                case "broadcaster": sb.Append("[STREAMER]"); break;
                case "moderator": sb.Append("[MOD]"); break;
                case "vip": sb.Append("[VIP]"); break;
                case "subscriber":
                case "founder": sb.Append("[SUB]"); break;
                case "turbo": sb.Append("[TURBO]"); break;
                case "staff": sb.Append("[STAFF]"); break;
                case "admin": sb.Append("[ADMIN]"); break;
                case "global_mod": sb.Append("[MOD]"); break;
            }
        }
        return sb.ToString();
    }

    // Ranges that contain emoji codepoints (used to detect emoji in chat).
    private static bool IsEmojiBase(int r) =>
        (r >= 0x1F000 && r <= 0x1FAFF) ||
        (r >= 0x2600 && r <= 0x27BF) ||
        (r >= 0x2B00 && r <= 0x2BFF) ||
        (r >= 0x2300 && r <= 0x23FF) ||
        (r >= 0x1F1E6 && r <= 0x1F1FF);

    // Scans a chat message for emoji and returns their Twemoji asset keys
    // (e.g. "1f600", "1f468-200d-1f469-200d-1f467", "1f1fa-1f1f8").
    private static List<string> ExtractEmojis(string message)
    {
        var keys = new List<string>();
        int i = 0;
        int len = message.Length;
        while (i < len)
        {
            int r = char.IsHighSurrogate(message[i])
                ? char.ConvertToUtf32(message[i], message[i + 1])
                : message[i];
            int charLen = char.IsHighSurrogate(message[i]) ? 2 : 1;

            if (!IsEmojiBase(r)) { i += charLen; continue; }

            var runes = new List<int> { r };
            i += charLen;
            bool zwj = false;
            while (i < len)
            {
                int nr = char.IsHighSurrogate(message[i])
                    ? char.ConvertToUtf32(message[i], message[i + 1])
                    : message[i];
                int ncl = char.IsHighSurrogate(message[i]) ? 2 : 1;

                if (zwj)
                {
                    if (IsEmojiBase(nr) || (nr >= 0x1F3FB && nr <= 0x1F3FF))
                    {
                        runes.Add(nr); i += ncl; zwj = false; continue;
                    }
                    break;
                }
                if (nr == 0x200D) { runes.Add(nr); i += ncl; zwj = true; continue; }
                if (nr == 0xFE0F) { runes.Add(nr); i += ncl; continue; }
                if (nr >= 0x1F3FB && nr <= 0x1F3FF) { runes.Add(nr); i += ncl; continue; }
                if (r >= 0x1F1E6 && r <= 0x1F1FF && nr >= 0x1F1E6 && nr <= 0x1F1FF)
                {
                    runes.Add(nr); i += ncl; break;
                }
                break;
            }

            keys.Add(string.Join("-", runes.ConvertAll(x => x.ToString("x2"))));
        }
        return keys;
    }

    private static string? ExtractTag(string section, string name)
    {
        var key = name + "=";
        var idx = section.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + key.Length;
        var end = section.IndexOfAny(new[] { ';', ' ' }, start);
        return end < 0 ? section.Substring(start) : section.Substring(start, end - start);
    }

    public void Dispose()
    {
        Disconnect();
    }
}

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

    // Last-seen ROOMSTATE values (slow / followers-only / subs-only / emote-only
    // / r9k). The very first ROOMSTATE after a join only seeds the baseline so
    // we never announce the whole chat config on connect.
    private readonly Dictionary<string, string> _roomState = new(StringComparer.Ordinal);
    private bool _awaitingFirstRoomState = true;

    // When someone gift-bombs (a "mystery gift"), Twitch sends ONE submysterygift
    // notice followed by a per-recipient subgift notice for every gifted sub. That
    // would double-fire the gift effect. We remember the giver (or "~anon" for
    // anonymous) and fold the EFFECTS of those follow-up subgift notices into the
    // single mass event; their chat-panel lines still show per recipient. Entries
    // expire shortly after, so they can never suppress unrelated gifts.
    private readonly Dictionary<string, DateTime> _pendingMassGift = new(StringComparer.OrdinalIgnoreCase);

    internal TwitchChatReader(Dictionary<string, EmoteCache.EmoteInfo> emotes)
    {
        _emotes = emotes;
    }

    internal void Connect(string channel)
    {
        if (_running) Disconnect();
        _channel = channel.ToLowerInvariant();
        _awaitingFirstRoomState = true;
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
        var attempt = 0;
        while (_running)
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

                _awaitingFirstRoomState = true;
                var nick = "justinfan" + new Random().Next(10000, 99999);
                _writer.WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");
                _writer.WriteLine("CAP END");
                _writer.WriteLine($"NICK {nick}");
                _writer.WriteLine($"JOIN #{_channel}");
                Plugin.Log.Info($"TwitchChatReader: connected as {nick}, joining #{_channel}.");

                // A connection that completed its handshake is worth instant
                // retries later: reset the reconnect-backoff counter.
                attempt = 0;
                var reconnectRequested = false;

                while (_running && _reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;

                    if (line.StartsWith("PING"))
                    {
                        _writer.WriteLine("PONG :tmi.twitch.tv");
                        continue;
                    }

                    if (line.Contains(" PRIVMSG ")) { HandlePrivMsg(line); continue; }
                    if (line.Contains(" USERNOTICE ")) { HandleUserNotice(line); continue; }
                    // CLEARMSG first: single-delete lines echo the deleted message
                    // text, which could legitimately contain the "CLEARCHAT" token.
                    if (line.Contains(" CLEARMSG ")) { HandleClearMsg(line); continue; }
                    if (line.Contains(" CLEARCHAT ")) { HandleClearChat(line); continue; }
                    if (line.Contains(" ROOMSTATE ")) { HandleRoomState(line); continue; }
                    // JOIN / PART / NOTICE / MOTD / CAP / 001 etc. are ignored.

                    // Twitch asks us to reconnect before it terminates the
                    // connection; the time until the server closes is
                    // indeterminate, so reconnect proactively instead of
                    // waiting on the socket to die.
                    if (line.EndsWith(" RECONNECT", StringComparison.Ordinal))
                    {
                        Plugin.Log.Info("TwitchChatReader: reconnect requested; reconnecting.");
                        reconnectRequested = true;
                        break;
                    }
                }

                if (!_running) break;
                if (!reconnectRequested)
                    Plugin.Log.Info("TwitchChatReader: connection closed by server.");
            }
            catch (Exception ex)
            {
                if (_running)
                    Plugin.Log.Warn($"TwitchChatReader: connection failed/lost: {ex.Message}");
            }
            finally
            {
                try { _writer?.Dispose(); } catch { }
                try { _reader?.Dispose(); } catch { }
                try { _client?.Dispose(); } catch { }
                _writer = null;
                _reader = null;
                _client = null;
            }

            if (!_running) break;

            // Auto-reconnect with a capped backoff so a persistent outage
            // doesn't hammer Twitch, while normal blips recover quickly.
            attempt++;
            var delaySeconds = Math.Min(30, 3 + (attempt - 1) * 2);
            Plugin.Log.Warn($"TwitchChatReader: reconnecting in {delaySeconds}s (attempt {attempt}).");
            for (var i = 0; i < delaySeconds * 10 && _running; i++)
                Thread.Sleep(100);
        }
    }

    // ---- PRIVMSG: a normal chat message (also carries cheers/bits) ----
    private void HandlePrivMsg(string line)
    {
        var user = ParseUser(line);
        var message = ParseMessage(line);
        if (string.IsNullOrEmpty(message)) return;

        var tags = ParseTags(line);
        var isShared = IsSharedChat(tags);

        // A Cheer is a PRIVMSG with a `bits` tag. Show it as ONE styled event
        // line ("X cheered 100 bits"), appending the viewer's text when they
        // typed any. The raw chat line is not also shown, so a cheer never
        // appears twice.
        var bits = ReadTag(tags, "bits");
        if (bits != null && bits.Length > 0 && long.TryParse(bits, out var bitAmount) && bitAmount > 0)
        {
            var displayName = FirstNonEmpty(ReadTag(tags, "display-name"), user);
            var text = message.Trim();
            var suffix = text.Length > 0 ? $": {text}" : string.Empty;
            RuntimeHooks.EnqueueSystemEvent($"{displayName} cheered {bitAmount} bits{suffix}", null, isShared);
            // Cheers also drive the bits effect pipeline when the IRC "Cheers"
            // toggle is on (checked in IrcEventRouter on the main thread).
            if (bitAmount <= int.MaxValue)
                RuntimeHooks.EnqueueIrcEvent("cheer", displayName, (int)bitAmount);
        }
        else
        {
            RuntimeHooks.EnqueueChatMessage(user, message, ParseUserColor(line), ReadTag(tags, "badges") ?? "", ReadTag(tags, "id") ?? "", isShared,
                ParseEmoteSpans(message, ReadTag(tags, "emotes")));

            // Chat bomb command: a message whose first word exactly matches the
            // configured command (e.g. "!bomb"). The panel line above still shows
            // normally; the router (main thread) applies the toggle + cooldown.
            // Viewer text after the command is passed along only when allowed.
            if (IrcEventRouter.MatchesBombCommand(message, out var bombPayload))
            {
                if (!(PluginConfig.Instance?.IrcBombAllowTextEnabled ?? true))
                    bombPayload = string.Empty;
                RuntimeHooks.EnqueueIrcEvent("bomb", user, 1, bombPayload);
            }

            // Chat throw command: same first-word rule. An optional count after
            // the command ("!throw 5") is parsed by the router (main thread).
            if (IrcEventRouter.MatchesThrowCommand(message, out var throwPayload))
                RuntimeHooks.EnqueueIrcEvent("throw", user, 1, throwPayload);
        }

        // Emote effects still run for both plain messages and cheers.
        var emotes = ParseEmotes(line);
        if (emotes.Length > 0)
        {
            if (PluginConfig.Instance?.VerboseLogging == true)
                NoteCosmeticController.VerboseLog($"TwitchChatReader: emote(s) from '{user}': {string.Join(", ", emotes)}");
            RuntimeHooks.EnqueueEmoteEvent(user, emotes);
        }
    }

    // ---- USERNOTICE: system notifications (subs, gifts, raids, streaks...) ----
    private void HandleUserNotice(string line)
    {
        var tags = ParseTags(line);
        if (tags == null) return;

        var msgId = ReadTag(tags, "msg-id");
        if (string.IsNullOrEmpty(msgId)) return;

        var name = FirstNonEmpty(ReadTag(tags, "display-name"), ReadTag(tags, "login"), "A viewer");
        var systemMsg = DecodeTagText(ReadTag(tags, "system-msg"));
        var isShared = IsSharedChat(tags);

        // The trailing parameter is the event's optional chat message - the
        // viewer's own text for resubs / watch streaks where it can be absent
        // just as easily as present, and empty for message-less notices. Show
        // it indented under the event line whenever present. (Announcements are
        // the exception: their trailing IS the announcement body, composed into
        // the main line already.)
        var trailing = ParseNoticeMessage(line).Trim();
        string? detail = null;
        if (trailing.Length > 0 && !string.Equals(msgId, "announcement", StringComparison.OrdinalIgnoreCase))
            detail = trailing;

        var text = msgId switch
        {
            "sub" => ComposeSub(name, tags),
            "resub" => ComposeResub(name, tags),
            "subgift" => ComposeSubGift(name, tags, false),
            "anonsubgift" => ComposeSubGift(name, tags, true),
            "submysterygift" => ComposeMysteryGift(name, tags, false),
            "anonsubmysterygift" => ComposeMysteryGift(name, tags, true),
            "giftpaidupgrade" => ComposeGiftUpgrade(name, tags, true),
            "anongiftpaidupgrade" => ComposeGiftUpgrade(name, tags, false),
            "raid" => ComposeRaid(name, tags),
            "bitsbadgetier" => ComposeBitsBadgeTier(name, tags),
            "modiversary" => ComposeModiversary(name, tags),
            "viewermilestone" => ComposeViewerMilestone(name, tags),
            "announcement" => ComposeAnnouncement(line),
            _ => null
        };

        if (text == null)
        {
            // Unknown or rare notice types (rewardgift, ritual, payforward, ...):
            // only show Twitch's own system message when it supplied one.
            if (systemMsg.Length == 0) return;
            text = systemMsg;
        }

        RuntimeHooks.EnqueueSystemEvent(text, detail, isShared);

        EnqueueNoticeEffect(msgId, name, tags);

        NoteCosmeticController.VerboseLog($"TwitchChatReader: notice '{msgId}' shared={isShared} detail={(detail ?? "").Length > 0}.");
    }

    // Maps a USERNOTICE onto the effect pipeline (bot-less IRC events). The
    // reader owns detection (it has the tags); the enable toggles and cooldowns
    // live in IrcEventRouter on the main thread, so this only enqueues when the
    // notice corresponds to a triggerable effect type.
    private void EnqueueNoticeEffect(string? msgId, string name, Dictionary<string, string>? tags)
    {
        if (string.IsNullOrEmpty(msgId) || tags == null)
            return;

        string? effectId = null;
        int amount = 1;

        switch (msgId)
        {
            case "sub":
                effectId = "sub";
                break;

            case "resub":
                effectId = "sub";
                var cumulative = ReadTag(tags, "msg-param-cumulative-months");
                if (cumulative != null && long.TryParse(cumulative, out var months) && months > 0)
                    amount = (int)Math.Min(months, int.MaxValue);
                break;

            case "subgift":
            case "anonsubgift":
                // A mass/mystery gift already fired ONE event; the per-recipient
                // subgift notices it spawns are folded into it, so skip them.
                if (ConsumeMassGift(tags))
                    return;
                effectId = "subscription_gift";
                break;

            case "giftpaidupgrade":
            case "anongiftpaidupgrade":
                effectId = "subscription_gift";
                break;

            case "submysterygift":
            case "anonsubmysterygift":
                effectId = "subscription_gift";
                var count = ReadTag(tags, "msg-param-mass-gift-count");
                if (count != null && long.TryParse(count, out var giftCount) && giftCount > 0)
                    amount = (int)Math.Min(giftCount, int.MaxValue);
                // Every per-recipient subgift notice from the same giver inside
                // the fold window belongs to this bomb; ConsumeMassGift swallows
                // their individual effects.
                TrackMassGift(tags);
                break;

            case "raid":
                effectId = "raid";
                var viewers = ReadTag(tags, "msg-param-viewerCount");
                if (viewers != null && long.TryParse(viewers, out var viewerCount) && viewerCount > 0)
                    amount = (int)Math.Min(viewerCount, int.MaxValue);
                break;
        }

        if (effectId != null)
            RuntimeHooks.EnqueueIrcEvent(effectId, name, amount);
    }

    private void TrackMassGift(Dictionary<string, string> tags)
    {
        var key = MassGiftKey(tags);
        if (key == null) return;
        PruneExpiredMassGifts();
        // Folding is window-based: every subgift from the same giver inside the
        // window is part of the bomb. The count tag is only used for the amount
        // on the mass event, never trusted to predict how many follow-ups come.
        _pendingMassGift[key] = DateTime.UtcNow.AddSeconds(60);
    }

    private bool ConsumeMassGift(Dictionary<string, string> tags)
    {
        var key = MassGiftKey(tags);
        if (key == null) return false;

        if (!_pendingMassGift.TryGetValue(key, out var expiry))
            return false;
        if (expiry < DateTime.UtcNow)
        {
            _pendingMassGift.Remove(key);
            return false;
        }
        return true;
    }

    // Giver identity used to match a mystery gift to its recipient notices:
    // the sender's login when present, else a shared key for anonymous gifts.
    // (Senderless singles only ever collide with an active anonymous bomb
    // window, which is rare and harmless.)
    private static string? MassGiftKey(Dictionary<string, string> tags)
    {
        var sender = ReadTag(tags, "msg-param-sender-login");
        if (!string.IsNullOrEmpty(sender))
            return sender;
        return "~anon";
    }

    private void PruneExpiredMassGifts()
    {
        if (_pendingMassGift.Count < 8) return;
        var now = DateTime.UtcNow;
        foreach (var key in new List<string>(_pendingMassGift.Keys))
        {
            if (_pendingMassGift[key] < now)
                _pendingMassGift.Remove(key);
        }
    }

    private static string ComposeSub(string name, Dictionary<string, string> tags)
    {
        var plan = PlanName(ReadTag(tags, "msg-param-sub-plan"));
        return plan.Length > 0 ? $"{name} just subscribed ({plan})!" : $"{name} just subscribed!";
    }

    private static string ComposeResub(string name, Dictionary<string, string> tags)
    {
        var plan = PlanName(ReadTag(tags, "msg-param-sub-plan"));
        var months = ReadTag(tags, "msg-param-cumulative-months");
        if (months != null && long.TryParse(months, out var m) && m > 0)
            return plan.Length > 0
                ? $"{name} resubscribed! ({plan}, {m} months)"
                : $"{name} resubscribed! ({m} months)";
        return plan.Length > 0 ? $"{name} resubscribed! ({plan})" : $"{name} resubscribed!";
    }

    private static string ComposeSubGift(string name, Dictionary<string, string> tags, bool anonymous)
    {
        // Twitch's own example messages use msg-param-recipient-name, while the
        // tag table documents msg-param-recipient-user-name; cover both.
        var recipient = FirstNonEmpty(
            ReadTag(tags, "msg-param-recipient-display-name"),
            ReadTag(tags, "msg-param-recipient-user-name"),
            ReadTag(tags, "msg-param-recipient-name"));
        var plan = PlanName(ReadTag(tags, "msg-param-sub-plan"));
        var who = anonymous ? "An anonymous viewer" : name;
        var tier = plan.Length > 0 ? $" ({plan})" : string.Empty;
        var to = recipient.Length > 0 ? $" to {recipient}" : string.Empty;
        return $"{who} gifted a sub{to}{tier}!";
    }

    private static string ComposeMysteryGift(string name, Dictionary<string, string> tags, bool anonymous)
    {
        var count = ReadTag(tags, "msg-param-mass-gift-count");
        var n = count != null && long.TryParse(count, out var c) ? c : 0;
        var who = anonymous ? "An anonymous viewer" : name;
        return n > 0 ? $"{who} is gifting {n} subs to the community!" : $"{who} is gifting subs to the community!";
    }

    private static string ComposeGiftUpgrade(string name, Dictionary<string, string> tags, bool senderVisible)
    {
        if (senderVisible)
        {
            var sender = FirstNonEmpty(ReadTag(tags, "msg-param-sender-name"), ReadTag(tags, "msg-param-sender-login"));
            return sender.Length > 0
                ? $"{name} upgraded their gifted sub (from {sender})!"
                : $"{name} upgraded their gifted sub to a paid sub!";
        }
        return $"{name} upgraded their gifted sub to a paid sub!";
    }

    private static string ComposeRaid(string name, Dictionary<string, string> tags)
    {
        var display = FirstNonEmpty(ReadTag(tags, "msg-param-displayName"), name);
        var count = ReadTag(tags, "msg-param-viewerCount");
        if (count != null && long.TryParse(count, out var n) && n > 0)
            return $"{display} is raiding with {n} viewers!";
        return $"{display} is raiding!";
    }

    private static string ComposeBitsBadgeTier(string name, Dictionary<string, string> tags)
    {
        var threshold = ReadTag(tags, "msg-param-threshold");
        if (threshold != null && long.TryParse(threshold, out var n) && n > 0)
            return $"{name} hit the {n} bits badge tier!";
        return $"{name} reached a new bits badge tier!";
    }

    // The msg-param-months tag is the total number of months the user has been
    // a moderator in this channel ("108 months as a moderator"). Often absent
    // for first-month milestones, so fall back to plain text.
    private static string ComposeModiversary(string name, Dictionary<string, string> tags)
    {
        var months = ReadTag(tags, "msg-param-months");
        if (months != null && long.TryParse(months, out var m) && m > 0)
            return $"It's {name}'s modiversary! ({m} months as a moderator)";
        return $"It's {name}'s modiversary!";
    }

    // Watch streaks arrive as msg-id=viewermilestone with category=watch-streak,
    // so "viewer milestones" and "watch streaks" are the same IRC event.
    private static string? ComposeViewerMilestone(string name, Dictionary<string, string> tags)
    {
        var category = ReadTag(tags, "msg-param-category");
        var value = ReadTag(tags, "msg-param-value");
        if (string.Equals(category, "watch-streak", StringComparison.OrdinalIgnoreCase)
            && value != null && long.TryParse(value, out var streak) && streak > 0)
            return $"{name} is on a {streak} stream watch streak!";
        return null;
    }

    private static string? ComposeAnnouncement(string line)
    {
        var msg = ParseNoticeMessage(line).Trim();
        return msg.Length > 0 ? $"Announcement: {msg}" : null;
    }

    private static string PlanName(string? plan) => plan switch
    {
        "1000" => "Tier 1",
        "2000" => "Tier 2",
        "3000" => "Tier 3",
        "Prime" => "Prime",
        _ => string.Empty
    };

    // ---- CLEARCHAT: a timeout, a ban, or a full chat-room clear ----
    private void HandleClearChat(string line)
    {
        var tags = ParseTags(line);

        // Twitch distinguishes a whole-room /clear from a user timeout/ban via
        // the target-user-id tag: /clear carries none. There is NO user hostmask
        // to look for - even user-targeted actions arrive with the tmi.twitch.tv
        // prefix, so "no '!' in the prefix" is NOT a room clear.
        if (string.IsNullOrEmpty(ReadTag(tags, "target-user-id")))
        {
            // Whole-room clear: the chat panel wipes in response.
            RuntimeHooks.EnqueueSystemEvent("Chat was cleared by a moderator.");
            RuntimeHooks.RunOnMainThread(() => ChatPanelController.Instance?.ClearChat());
            return;
        }

        // Single-user timeout (ban-duration tag) or ban. The affected user's
        // login is the parameter after the channel token.
        var target = ParseClearTarget(line);
        if (target.Length == 0) return;

        var duration = ReadTag(tags, "ban-duration");
        if (duration != null && long.TryParse(duration, out var seconds) && seconds > 0)
            RuntimeHooks.EnqueueSystemEvent($"{target} was timed out ({FormatDuration(seconds)}).");
        else
            RuntimeHooks.EnqueueSystemEvent($"{target} has been banned.");

        RuntimeHooks.RunOnMainThread(() => ChatPanelController.Instance?.RemoveMessagesByLogin(target));
    }

    // CLEARCHAT payload after the channel token: the targeted user's login
    // ("" for a whole-room /clear). Space-split instead of LastIndexOf(':'),
    // because shared-chat room ids embed colons ("#chatrooms:123:456").
    private static string ParseClearTarget(string line)
    {
        var parts = line.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 1 && parts[i][0] == '#')
                return i + 1 < parts.Length ? parts[i + 1].TrimStart(':').Trim() : string.Empty;
        }
        return string.Empty;
    }

    // ---- CLEARMSG: a single chat message was deleted by a mod ----
    private void HandleClearMsg(string line)
    {
        var tags = ParseTags(line);
        var id = ReadTag(tags, "target-msg-id");
        if (string.IsNullOrEmpty(id)) return;
        RuntimeHooks.RunOnMainThread(() => ChatPanelController.Instance?.RemoveMessageById(id!));
    }

    private static readonly string[] RoomStateKeys = { "slow", "followers-only", "subs-only", "emote-only", "r9k" };

    // ---- ROOMSTATE: chat settings changed (slow/followers/subs/emote/r9k) ----
    private void HandleRoomState(string line)
    {
        var tags = ParseTags(line);
        if (tags == null) return;

        if (_awaitingFirstRoomState)
        {
            _awaitingFirstRoomState = false;
            foreach (var key in RoomStateKeys)
            {
                var v = ReadTag(tags, key);
                if (v != null) _roomState[key] = v;
            }
            return;
        }

        CheckRoomSetting(tags, "slow", v => v == "0" ? "Slow mode is now off." : "Slow mode is now on.");
        CheckRoomSetting(tags, "followers-only", v => v == "-1" ? "Followers-only mode is now off." : "Followers-only mode is now on.");
        CheckRoomSetting(tags, "subs-only", v => v == "0" ? "Subscribers-only mode is now off." : "Subscribers-only mode is now on.");
        CheckRoomSetting(tags, "emote-only", v => v == "0" ? "Emote-only mode is now off." : "Emote-only mode is now on.");
        CheckRoomSetting(tags, "r9k", v => v == "0" ? "Unique-chat (R9K) mode is now off." : "Unique-chat (R9K) mode is now on.");
    }

    private void CheckRoomSetting(Dictionary<string, string> tags, string key, Func<string, string> compose)
    {
        var value = ReadTag(tags, key);
        if (value == null) return;

        if (_roomState.TryGetValue(key, out var prev) && string.Equals(prev, value, StringComparison.Ordinal))
            return;

        _roomState[key] = value;
        RuntimeHooks.EnqueueSystemEvent(compose(value));
    }

    // Parses the leading "@tag=value;tag=value ..." section of an IRC line into
    // a dictionary. Returns null when the line has no tags section.
    private static Dictionary<string, string>? ParseTags(string line)
    {
        if (line.Length == 0 || line[0] != '@') return null;
        var space = line.IndexOf(' ');
        if (space <= 1) return null;

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in line.Substring(1, space - 1).Split(';'))
        {
            if (raw.Length == 0) continue;
            var eq = raw.IndexOf('=');
            if (eq < 0) { dict[raw] = string.Empty; continue; }
            dict[raw.Substring(0, eq)] = raw.Substring(eq + 1);
        }
        return dict;
    }

    private static string? ReadTag(Dictionary<string, string>? tags, string key)
    {
        if (tags != null && tags.TryGetValue(key, out var value)) return value;
        return null;
    }

    // Twitch Shared Chat combines partner channels; messages relayed from a
    // partner room arrive in our room tagged with source-room-id / source-id
    // (PRIVMSG) or source-room-id / source-msg-id (USERNOTICE). IMPORTANT: in a
    // shared-chat-enabled room EVERY message carries those source tags - the
    // messages sent in the room itself have a source-room-id that EQUALS the
    // room's own room-id tag. Checking tag presence alone therefore flags every
    // message (the broadcaster's own included) as "shared". The distinguishing
    // test is that source-room-id DIFFERS from this room's room-id: only then
    // was the message actually relayed in from another channel.
    private static bool IsSharedChat(Dictionary<string, string>? tags)
    {
        if (tags == null) return false;
        if (!tags.TryGetValue("source-room-id", out var sourceRoomId) || sourceRoomId.Length == 0)
            return false;
        var roomId = ReadTag(tags, "room-id");
        return roomId == null || !string.Equals(sourceRoomId, roomId, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrEmpty(v)) return v!;
        return string.Empty;
    }

    // Twitch encodes spaces in some tag values (e.g. system-msg) as \s, plus
    // regular percent-encoding. Neither appears in the raw message body.
    private static string DecodeTagText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var s = value!.Replace("\\s", " ");
        try { s = Uri.UnescapeDataString(s); } catch { }
        return s;
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m";
        if (seconds < 86400) return $"{seconds / 3600}h";
        return $"{seconds / 86400}d";
    }

    private string[] ParseEmotes(string line)
    {
        var found = new List<string>();

        // Name-based detection for 7TV / BTTV / FFZ emotes. Those aren't in
        // Twitch's native emote tag, so they're matched by chat word. Matching
        // is case-sensitive (emote names are case-sensitive on every platform).
        var message = ParseMessage(line);
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
                if (PluginConfig.Instance?.VerboseLogging == true)
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

    // Emote spans for the CHAT PANEL: each (start, length, code) covers the
    // range of the raw message that renders as an emote image. Word-based
    // (7TV/BTTV/FFZ) matches are whole tokens; Twitch-native emotes come from
    // the `emotes` tag's exact ranges and replace a name collision on the same
    // range; emoji (when enabled) are scanned last and yield to any overlap.
    // The spans are resolved against the raw message; the panel re-finds each
    // word in its sanitized copy so index shifts can never misplace a decal.
    private ChatPanelController.EmoteSpan[]? ParseEmoteSpans(string message, string? emotesTag)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var spans = new List<ChatPanelController.EmoteSpan>();

        // Whole-token word matches, offsets kept by walking the split delimiters.
        var tokens = message.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.None);
        var cursor = 0;
        foreach (var token in tokens)
        {
            if (token.Length > 0 && _emotes.ContainsKey(token))
                spans.Add(new ChatPanelController.EmoteSpan(cursor, token.Length, token));
            cursor += token.Length + 1;
        }

        // Twitch-native emotes: the tag lists each emote's exact range(s), which
        // is the only way to know where a native emote sits in the message (its
        // word need not be a whole token, e.g. "Kappa123 spongebob"). Any word
        // match overlapping a tag range is dropped in favor of the tag.
        if (!string.IsNullOrEmpty(emotesTag))
        {
            foreach (var part in emotesTag!.Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                var colon = part.IndexOf(':');
                if (colon <= 0 || colon >= part.Length - 1) continue;
                var id = part.Substring(0, colon);
                var code = EmoteCache.Instance.EnsureTwitchEmote(id);
                var ranges = part.Substring(colon + 1).Split(',');
                foreach (var range in ranges)
                {
                    var dash = range.IndexOf('-');
                    if (dash <= 0 || dash >= range.Length - 1) continue;
                    if (!int.TryParse(range.Substring(0, dash), out var s)) continue;
                    if (!int.TryParse(range.Substring(dash + 1), out var e)) continue;
                    if (s < 0 || e < s || e >= message.Length) continue;
                    ReplaceOverlapping(spans, s, e - s + 1, code);
                }
            }
        }

        // Optional Unicode emoji (same toggle as emote rain).
        if (PluginConfig.Instance?.EmojiSupportEnabled == true)
        {
            foreach (var range in ScanEmojiRanges(message))
            {
                if (OverlapsAny(spans, range.Start, range.Length)) continue;
                var code = EmoteCache.Instance.EnsureEmojiEmote(range.Key);
                spans.Add(new ChatPanelController.EmoteSpan(range.Start, range.Length, code));
            }
        }

        return spans.Count > 0 ? spans.ToArray() : null;
    }

    private static void ReplaceOverlapping(List<ChatPanelController.EmoteSpan> spans, int start, int length, string code)
    {
        var end = start + length;
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var s = spans[i];
            if (s.Start < end && s.Start + s.Length > start)
                spans.RemoveAt(i);
        }
        spans.Add(new ChatPanelController.EmoteSpan(start, length, code));
    }

    private static bool OverlapsAny(List<ChatPanelController.EmoteSpan> spans, int start, int length)
    {
        var end = start + length;
        foreach (var s in spans)
            if (s.Start < end && s.Start + s.Length > start)
                return true;
        return false;
    }

    // Same emoji-run scanner as ExtractEmojis, but ALSO reporting the char
    // offsets so the chat panel knows exactly where each glyph sits.
    private static List<(int Start, int Length, string Key)> ScanEmojiRanges(string message)
    {
        var result = new List<(int Start, int Length, string Key)>();
        int i = 0;
        int len = message.Length;
        while (i < len)
        {
            int r = char.IsHighSurrogate(message[i])
                ? char.ConvertToUtf32(message[i], message[i + 1])
                : message[i];
            int charLen = char.IsHighSurrogate(message[i]) ? 2 : 1;

            if (!IsEmojiBase(r)) { i += charLen; continue; }

            var start = i;
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

            result.Add((start, i - start, string.Join("-", runes.ConvertAll(x => x.ToString("x2")))));
        }
        return result;
    }

    private static string ParseUser(string line)
    {
        var privIdx = line.IndexOf("PRIVMSG", StringComparison.Ordinal);
        var head = privIdx > 0 ? line.Substring(0, privIdx) : line;
        var c = head.LastIndexOf(':');
        var e = c >= 0 ? head.IndexOf('!', c) : -1;
        return (c >= 0 && e > c) ? head.Substring(c + 1, e - c - 1) : "unknown";
    }

    // The PRIVMSG payload is everything after the FIRST ' :' that follows the
    // command: IRC delimits the trailing parameter that way, and the message
    // itself may legitimately contain colons (URLs, "GG :(" ...), so taking the
    // text after the line's LAST ':' would truncate it.
    private static string ParseMessage(string line)
    {
        var privIdx = line.IndexOf("PRIVMSG", StringComparison.Ordinal);
        var sep = privIdx >= 0 ? line.IndexOf(" :", privIdx, StringComparison.Ordinal) : -1;
        return sep >= 0 ? line.Substring(sep + 2) : string.Empty;
    }

    // The USERNOTICE trailing parameter is OPTIONAL ("USERNOTICE #chan [:message]",
// per Twitch's command reference): resubs and watch-streak messages carry the
// viewer's text, but most notices (new subs, gifts, raids, upgrades...) have no
// trailing at all. The tricky bit is that tagged lines look like
// "@tags... :tmi.twitch.tv USERNOTICE #chan [:message]" - the first " :" there
// is the TAGS/prefix boundary, not a message. So drop the leading tag section
// (tags never contain spaces) before searching; message-less notices then
// correctly yield an empty string.
private static string ParseNoticeMessage(string line)
{
    var tagEnd = line.IndexOf(' ');
    var body = tagEnd >= 0 ? line.Substring(tagEnd + 1) : line;
    var messageColon = body.IndexOf(" :", StringComparison.Ordinal);
    return messageColon >= 0 ? body.Substring(messageColon + 2) : string.Empty;
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

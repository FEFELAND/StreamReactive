using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace StreamReactive;

internal sealed class EmoteCache
{
    private static EmoteCache? _instance;
    internal static EmoteCache Instance => _instance ??= new EmoteCache();

    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D[]> _animatedFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _frameDelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmoteInfo> _emotes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fetching = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fetchingAnimation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<string, Texture2D>>> _pendingByCode = new(StringComparer.OrdinalIgnoreCase);
    private string? _channelName;
    private bool _loaded;

    private const string UserAgent = "StreamReactive/1.0";
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruMap =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _emoteBytes = new(StringComparer.OrdinalIgnoreCase);
    private long _estimatedBytes;

    internal IReadOnlyDictionary<string, EmoteInfo> Emotes => _emotes;
    internal Dictionary<string, EmoteInfo> EmotesDict => _emotes;

    internal bool TryGetAnimatedFrames(string code, out Texture2D[] frames, out float frameDelay)
    {
        if (_animatedFrames.TryGetValue(code, out frames!) && _frameDelays.TryGetValue(code, out frameDelay))
            return true;

        // On-demand: kick off background GIF fetch if this is an animated emote
        // we haven't started fetching yet. Next time the emote fires it'll have frames.
        if (_emotes.TryGetValue(code, out var info) && info.Animated
            && !_fetchingAnimation.Contains(code))
        {
            RuntimeHooks.RunCoroutine(FetchAnimatedFramesOnly(code, info));
        }

        frames = Array.Empty<Texture2D>();
        frameDelay = 0.1f;
        return false;
    }

    internal struct EmoteInfo
    {
        internal string Id;
        internal string Name;
        internal bool Animated;
        internal int Width;
        internal int Height;
        internal string StaticUrl;
        internal string? GifUrl;
        internal string Provider;
    }

    internal void LoadChannel(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return;
        if (string.Equals(_channelName, channelName, StringComparison.OrdinalIgnoreCase) && _loaded) return;

        _channelName = channelName;
        _loaded = false;
        _emotes.Clear();
        foreach (var t in _textures.Values)
            if (t != null) UnityEngine.Object.Destroy(t);
        _textures.Clear();
        foreach (var frames in _animatedFrames.Values)
            if (frames != null)
                foreach (var f in frames)
                    if (f != null) UnityEngine.Object.Destroy(f);
        _animatedFrames.Clear();
        _frameDelays.Clear();
        _lru.Clear();
        _lruMap.Clear();
        _emoteBytes.Clear();
        _estimatedBytes = 0;

        Plugin.Log.Info($"EmoteCache: loading 7TV emotes for '{channelName}'.");
        RuntimeHooks.RunCoroutine(FetchEmoteSet(channelName));
    }

    private IEnumerator FetchEmoteSet(string channelName)
    {
        string? twitchId = null;
        var gqlQuery = $"{{ user(login: \"{channelName}\") {{ id }} }}";
        var gqlBody = "{\"query\":\"" + gqlQuery.Replace("\"", "\\\"") + "\"}";

        using (var gqlReq = new UnityEngine.Networking.UnityWebRequest("https://gql.twitch.tv/gql", "POST"))
        {
            var bodyRaw = Encoding.UTF8.GetBytes(gqlBody);
            gqlReq.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            gqlReq.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            gqlReq.SetRequestHeader("Content-Type", "application/json");
            gqlReq.SetRequestHeader("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            gqlReq.timeout = 10;
            yield return gqlReq.SendWebRequest();

            if (gqlReq.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Plugin.Log.Warn($"EmoteCache: failed to resolve Twitch user '{channelName}': {gqlReq.error}");
                yield break;
            }

            try
            {
                var gqlJson = JToken.Parse(gqlReq.downloadHandler.text);
                twitchId = gqlJson?["data"]?["user"]?["id"]?.ToString();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"EmoteCache: bad JSON from Twitch GQL: {ex.Message}");
                yield break;
            }
        }

        if (string.IsNullOrEmpty(twitchId))
        {
            Plugin.Log.Warn($"EmoteCache: Twitch user '{channelName}' not found.");
            yield break;
        }

        Plugin.Log.Info($"EmoteCache: resolved '{channelName}' → Twitch ID {twitchId}.");

        var userListUrl = $"https://7tv.io/v3/users/twitch/{twitchId}";

        using (var www = UnityEngine.Networking.UnityWebRequest.Get(userListUrl))
        {
            www.SetRequestHeader("User-Agent", UserAgent);
            www.timeout = 10;
            yield return www.SendWebRequest();

            if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Plugin.Log.Warn($"EmoteCache: failed to fetch 7TV user for '{channelName}': {www.error}");
                yield break;
            }

            var json = www.downloadHandler.text;
            JObject obj;
            try { obj = JObject.Parse(json); }
            catch (Exception ex) { Plugin.Log.Warn($"EmoteCache: bad JSON from 7TV: {ex.Message}"); yield break; }

            int total = 0;

            var emoteSet = obj["emote_set"];

            // 7TV v3 returns emote_set as an ID string; the emotes only exist
            // under https://7tv.io/v3/emote-sets/{id}. Older payloads embed the
            // set object directly - keep that path working as a fallback.
            if (emoteSet != null && emoteSet.Type == JTokenType.String)
            {
                var setId = emoteSet.Value<string>();
                if (!string.IsNullOrEmpty(setId))
                {
                    var setUrl = $"https://7tv.io/v3/emote-sets/{setId}";
                    using (var swebcall = UnityEngine.Networking.UnityWebRequest.Get(setUrl))
                    {
                        Plugin.Log.Info($"EmoteCache: fetching 7TV set {setId}...");
                        swebcall.SetRequestHeader("User-Agent", UserAgent);
                        swebcall.timeout = 10;
                        yield return swebcall.SendWebRequest();
                        if (swebcall.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                        {
                            try
                            {
                                var setObj = JObject.Parse(swebcall.downloadHandler.text);
                                var emotes = setObj["emotes"] as JArray;
                                if (emotes != null)
                                {
                                    foreach (var emote in emotes)
                                    {
                                        if (ParseEmote(emote)) total++;
                                    }
                                }
                            }
                            catch (Exception ex) { Plugin.Log.Warn($"EmoteCache: bad 7TV emote-set JSON: {ex.Message}"); }
                        }
                        else
                        {
                            Plugin.Log.Warn($"EmoteCache: failed to fetch 7TV emote set: {swebcall.error}");
                        }
                    }
                }
            }
            else if (emoteSet is JObject emoteSetObj)
            {
                var emotes = emoteSetObj["emotes"] as JArray;
                if (emotes != null)
                {
                    foreach (var emote in emotes)
                    {
                        if (ParseEmote(emote)) total++;
                    }
                }
            }

            var globalUrl = "https://7tv.io/v3/emote-sets/global";
            using (var gwww = UnityEngine.Networking.UnityWebRequest.Get(globalUrl))
            {
                gwww.SetRequestHeader("User-Agent", UserAgent);
                gwww.timeout = 10;
                yield return gwww.SendWebRequest();

                if (gwww.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var gObj = JObject.Parse(gwww.downloadHandler.text);
                        var gEmotes = gObj["emotes"] as JArray;
                        if (gEmotes != null)
                        {
                            foreach (var emote in gEmotes)
                            {
                                if (ParseEmote(emote)) total++;
                            }
                        }
                    }
                    catch { }
                }
            }

            // BTTV + FFZ: their own emotes (and they also mirror many Twitch
            // emotes). Kept in memory only. FetchJsonCoroutine swallows errors.
            yield return FetchJsonCoroutine("https://api.betterttv.net/3/cached/emotes/global",
                json => total += ParseBttvEmotes(JArray.Parse(json), "BTTV"));
            yield return FetchJsonCoroutine($"https://api.betterttv.net/3/cached/users/twitch/{twitchId}",
                json =>
                {
                    var root = JObject.Parse(json);
                    total += ParseBttvEmotes(root["channelEmotes"], "BTTV");
                    total += ParseBttvEmotes(root["sharedEmotes"], "BTTV");
                });
            yield return FetchJsonCoroutine("https://api.frankerfacez.com/v1/set/global",
                json => total += ParseFfzEmoticons(JObject.Parse(json), "FFZ"));
            yield return FetchJsonCoroutine($"https://api.frankerfacez.com/v1/room/id/{twitchId}",
                json => total += ParseFfzEmoticons(JObject.Parse(json), "FFZ"));

            Plugin.Log.Info($"EmoteCache: loaded {total} emotes for '{channelName}'.");
            _loaded = true;
        }
    }

    private static IEnumerator FetchJsonCoroutine(string url, Action<string> onSuccess)
    {
        using var www = UnityEngine.Networking.UnityWebRequest.Get(url);
        www.SetRequestHeader("User-Agent", UserAgent);
        www.timeout = 10;
        yield return www.SendWebRequest();
        if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            try { onSuccess(www.downloadHandler.text); }
            catch { }
        }
    }

    private bool ParseEmote(JToken emote)
    {
        var id = emote["id"]?.ToString();
        var name = emote["name"]?.ToString();
        if (id == null || name == null) return false;
        if (_emotes.ContainsKey(name)) return false;

        var data = emote["data"];
        var animated = data?["animated"]?.ToObject<bool>() ?? false;
        var width = data?["width"]?.ToObject<int>() ?? 28;
        var height = data?["height"]?.ToObject<int>() ?? 28;

        var host = data?["host"];
        var cdnUrl = host?["url"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(cdnUrl)) return false;
        var baseUrl = cdnUrl.StartsWith("//") ? "https:" + cdnUrl : cdnUrl;

        _emotes[name] = new EmoteInfo
        {
            Id = id,
            Name = name,
            Animated = animated,
            Width = width,
            Height = height,
            StaticUrl = baseUrl + "/2x.webp",
            GifUrl = animated ? baseUrl + "/2x.gif" : null,
            Provider = "7TV"
        };
        return true;
    }

    private bool AddEmote(string name, string staticUrl, string? gifUrl, bool animated, string provider)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(staticUrl)) return false;
        if (_emotes.ContainsKey(name)) return false;
        _emotes[name] = new EmoteInfo
        {
            Id = "",
            Name = name,
            Animated = animated,
            Width = 0,
            Height = 0,
            StaticUrl = staticUrl,
            GifUrl = gifUrl,
            Provider = provider
        };
        return true;
    }

    /// <summary>
    /// Registers a Twitch-native emote by its numeric IRC emote ID (from the
    /// chat `emotes` tag). The image is fetched straight from Twitch's CDN —
    /// no catalog or auth needed, so this covers global emotes and subscriber
    /// emotes from ANY channel, just by reading chat.
    /// </summary>
    internal string EnsureTwitchEmote(string twitchId)
    {
        var code = "tw:" + twitchId;
        if (!_emotes.ContainsKey(code))
        {
            var url = $"https://static-cdn.jtvnw.net/emoticons/v2/{twitchId}/default/dark/2.0";
            _emotes[code] = new EmoteInfo
            {
                Id = twitchId,
                Name = code,
                Animated = false,
                Width = 0,
                Height = 0,
                StaticUrl = url,
                GifUrl = null,
                Provider = "TWITCH"
            };
        }
        return code;
    }

    /// <summary>
    /// Registers a Unicode emoji by its Twemoji asset key (e.g. "1f600" or
    /// "1f468-200d-1f469-200d-1f467" for a ZWJ sequence). The glyph is fetched
    /// from the Twemoji CDN and rendered through the same pipeline as emotes.
    /// </summary>
    internal string EnsureEmojiEmote(string key)
    {
        var code = "emoji:" + key;
        if (!_emotes.ContainsKey(code))
        {
            var url = $"https://cdn.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/{key}.png";
            _emotes[code] = new EmoteInfo
            {
                Id = key,
                Name = code,
                Animated = false,
                Width = 0,
                Height = 0,
                StaticUrl = url,
                GifUrl = null,
                Provider = "EMOJI"
            };
        }
        return code;
    }

    private static string[] TwitchCdnCandidates(string code)
    {
        var id = code.StartsWith("tw:", StringComparison.OrdinalIgnoreCase)
            ? code.Substring(3)
            : code;
        return new[]
        {
            $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0",
            $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/3.0",
            $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/light/2.0",
            $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/light/3.0",
            $"https://static-cdn.jtvnw.net/emoticons/{id}/2.0",
            $"https://static-cdn.jtvnw.net/emoticons/{id}/3.0",
        };
    }

    private int ParseBttvEmotes(JToken? container, string provider)
    {
        if (container == null) return 0;
        var arr = container as JArray ?? container["emotes"] as JArray;
        if (arr == null) return 0;
        int n = 0;
        foreach (var e in arr)
        {
            var id = e["id"]?.ToString();
            var code = e["code"]?.ToString();
            if (id == null || code == null) continue;
            var animated = (e["imageType"]?.ToString() ?? "png") == "gif" || (e["animated"]?.ToObject<bool>() ?? false);
            var url = $"https://cdn.betterttv.net/emote/{id}/2x";
            if (AddEmote(code, url, animated ? url : null, animated, provider)) n++;
        }
        return n;
    }

    private int ParseFfzEmoticons(JToken? container, string provider)
    {
        if (container == null) return 0;
        // FFZ responses nest emoticons under sets: { sets: { "<id>": { emoticons: [ ... ] } } }
        var sets = container["sets"] as JObject ?? container as JObject;
        int n = 0;
        if (sets != null)
        {
            foreach (var setProp in sets.Properties())
            {
                var emoticons = setProp.Value["emoticons"] as JArray;
                if (emoticons == null) continue;
                foreach (var e in emoticons)
                {
                    var name = e["name"]?.ToString();
                    if (name == null) continue;
                    var animated = e["animated"]?.ToObject<bool>() ?? false;
                    var urls = e["urls"] as JObject;
                    var url = urls?["2"]?.ToString() ?? urls?["1"]?.ToString();
                    if (url == null) continue;
                    if (AddEmote(name, url, animated ? url : null, animated, provider)) n++;
                }
            }
        }
        return n;
    }

    internal bool TryGetTexture(string code, out Texture2D tex)
    {
        if (_textures.TryGetValue(code, out tex!))
        {
            Touch(code);
            return true;
        }

        if (_fetching.Contains(code)) return false;

        if (_emotes.TryGetValue(code, out var info))
        {
            _fetching.Add(code);
            RuntimeHooks.RunCoroutine(FetchEmoteTexture(code, info));
        }

        return false;
    }

    internal void GetTextureAsync(string code, Action<string, Texture2D> onReady)
    {
        if (_textures.TryGetValue(code, out var tex))
        {
            onReady(code, tex);
            return;
        }

        if (!_pendingByCode.TryGetValue(code, out var list))
        {
            list = new List<Action<string, Texture2D>>();
            _pendingByCode[code] = list;
        }
        list.Add(onReady);

        if (_fetching.Contains(code)) return;

        if (_emotes.TryGetValue(code, out var info))
        {
            _fetching.Add(code);
            RuntimeHooks.RunCoroutine(FetchEmoteTexture(code, info));
        }
    }

    private IEnumerator FetchEmoteTexture(string code, EmoteInfo info)
    {
        if (string.IsNullOrEmpty(info.StaticUrl))
        {
            _fetching.Remove(code);
            FirePending(code, null);
            yield break;
        }

        byte[]? imageBytes = null;
        var isGif = false;

        if (info.Animated && !string.IsNullOrEmpty(info.GifUrl))
        {
            // Animated: download GIF (ImageSharp decodes animated GIF natively)
            var gifUrl = info.GifUrl;
            using (var www = UnityEngine.Networking.UnityWebRequest.Get(gifUrl))
            {
                www.timeout = 10;
                yield return www.SendWebRequest();
                if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    imageBytes = www.downloadHandler.data;
                    isGif = true;
                }
                else
                {
                    Plugin.Log.Debug($"EmoteCache: GIF download failed for '{code}', trying static.");
                }
            }

            // Fallback: static first frame
            if (imageBytes == null)
            {
                using (var www = UnityEngine.Networking.UnityWebRequest.Get(info.StaticUrl))
                {
                    www.SetRequestHeader("User-Agent", UserAgent);
                    www.timeout = 10;
                    yield return www.SendWebRequest();
                    if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                        imageBytes = www.downloadHandler.data;
                }
            }
        }
        else
        {
            // Static: download one or more candidate URLs (Twitch has several
            // CDN shapes depending on emote age; try them in order).
            var candidates = info.Provider == "TWITCH"
                ? TwitchCdnCandidates(code)
                : new[] { info.StaticUrl! };

            foreach (var url in candidates)
            {
                using (var www = UnityEngine.Networking.UnityWebRequest.Get(url))
                {
                    www.SetRequestHeader("User-Agent", UserAgent);
                    www.timeout = 10;
                    yield return www.SendWebRequest();
                    if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        imageBytes = www.downloadHandler.data;
                        break;
                    }
                }
            }

            if (imageBytes == null)
                Plugin.Log.Warn($"EmoteCache: download failed for '{code}' (tried {candidates.Length} URL(s)).");
        }

        if (imageBytes == null)
        {
            Plugin.Log.Warn($"EmoteCache: all download attempts failed for '{code}'.");
            _fetching.Remove(code);
            FirePending(code, null);
            yield break;
        }

        // Heavy ImageSharp decode runs on a background thread so it never
        // blocks the main (render) thread and causes a stutter.
        var codeLocal = code;
        var bytesLocal = imageBytes;
        var gifLocal = isGif;
        _fetching.Add(code);
        System.Threading.Tasks.Task.Run(() =>
        {
            var output = DecodeOffMainThread(bytesLocal, gifLocal);
            RuntimeHooks.RunOnMainThread(() => FinalizeEmote(codeLocal, output));
        });
    }

    /// <summary>
    /// Result of a background-thread decode. The decoded pixel data is carried
    /// as PNG byte arrays so the colour pipeline stays identical to before;
    /// Texture2D creation (main-thread only) happens in FinalizeEmote.
    /// </summary>
    private sealed class DecodeOutput
    {
        internal bool IsAnimated;
        internal bool Failed;
        internal byte[]? StaticPng;
        internal byte[][]? FramePngs;
        internal float FrameDelay;
        internal int Width;
        internal int Height;
    }

    private static DecodeOutput DecodeOffMainThread(byte[] bytes, bool isGif)
    {
        var output = new DecodeOutput();
        try
        {
            using var image = Image.Load<Rgba32>(bytes);
            output.Width = image.Width;
            output.Height = image.Height;

            // Animation is detected from the actual frame count, not the source
            // format: Twitch serves animated emotes as APNG from the same static
            // URL, so a GIF-only check would drop every animated Twitch emote to
            // its first frame.
            if (image.Frames.Count > 1)
            {
                output.IsAnimated = true;
                var frameCount = image.Frames.Count;
                output.FramePngs = new byte[frameCount][];
                var delays = new int[frameCount];
                for (int i = 0; i < frameCount; i++)
                {
                    var frame = image.Frames[i];
                    output.FramePngs[i] = EncodeFramePng(frame);
                    delays[i] = GetFrameDelayMs(frame);
                }
                output.FrameDelay = ComputeModeDelay(delays);
            }
            else
            {
                output.IsAnimated = false;
                output.StaticPng = EncodePng(image);
            }
        }
        catch (Exception)
        {
            output.Failed = true;
            output.StaticPng = null;
            output.FramePngs = null;
            // Failure is reported on the main thread by FinalizeEmote.
        }
        return output;
    }

    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeFramePng(ImageFrame<Rgba32> frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        var img = new Image<Rgba32>(w, h);
        try
        {
            frame.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var src = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                        img[x, y] = src[x];
                }
            });
            return EncodePng(img);
        }
        finally
        {
            img.Dispose();
        }
    }

    /// <summary>
    /// GIF FrameDelay is in centiseconds (1/100s). Use the most common delay
    /// across frames; fall back to 10 centiseconds.
    /// </summary>
    // Frame delay in hundredths of a second (GIF uses this unit in ImageSharp's
    // metadata). APNG frame timing isn't exposed on frame metadata in this
    // ImageSharp version, so animated Twitch (APNG) emotes fall back to 100ms.
    private static int GetFrameDelayMs(ImageFrame<Rgba32> frame)
    {
        try
        {
            var gif = frame.Metadata.GetGifMetadata().FrameDelay;
            if (gif > 0) return gif;
        }
        catch { }
        return 10;
    }

    private static float ComputeModeDelay(int[] delays)
    {
        var counts = new Dictionary<int, int>();
        var best = 10;
        var bestCount = 0;
        foreach (var d in delays)
        {
            var key = d > 0 ? d : 10;
            var c = counts.TryGetValue(key, out var v) ? v + 1 : 1;
            counts[key] = c;
            if (c > bestCount)
            {
                bestCount = c;
                best = key;
            }
        }
        return best > 0 ? best / 100f : 0.1f;
    }

    private void Touch(string code)
    {
        if (_lruMap.TryGetValue(code, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        else
        {
            _lruMap[code] = _lru.AddFirst(code);
        }
    }

    /// <summary>
    /// Bound RAM by freeing least-recently-used decoded textures once the cache
    /// exceeds the configured cap. Emotes currently displayed on screen
    /// (tracked via EmoteHudEntry.ActiveCodes) are never evicted.
    /// </summary>
    private void EvictIfNeeded()
    {
        var cfg = PluginConfig.Instance;
        var capBytes = cfg != null ? (long)Math.Max(8, cfg.EmoteCacheMB) * 1024 * 1024 : 64L * 1024 * 1024;
        while (_estimatedBytes > capBytes)
        {
            var node = _lru.Last;
            string? victim = null;
            while (node != null)
            {
                if (!EmoteHudEntry.ActiveCodes.Contains(node.Value))
                {
                    victim = node.Value;
                    break;
                }
                node = node.Previous;
            }
            if (victim == null) break; // everything cached is on screen
            RemoveTextures(victim);
        }
    }

    private void RemoveTextures(string code)
    {
        if (_lruMap.TryGetValue(code, out var node))
        {
            _lru.Remove(node);
            _lruMap.Remove(code);
        }
        _textures.TryGetValue(code, out var shared);
        if (_animatedFrames.TryGetValue(code, out var frames))
        {
            foreach (var f in frames)
                if (f != null && f != shared)
                    UnityEngine.Object.Destroy(f);
            _animatedFrames.Remove(code);
            _frameDelays.Remove(code);
        }
        if (shared != null)
        {
            UnityEngine.Object.Destroy(shared);
            _textures.Remove(code);
        }
        if (_emoteBytes.TryGetValue(code, out var b))
        {
            _estimatedBytes -= b;
            _emoteBytes.Remove(code);
        }
    }

    /// <summary>
    /// Runs on the main thread once a background decode has completed. Builds
    /// the actual Texture2D objects and publishes them to the cache.
    /// </summary>
    private void FinalizeEmote(string code, DecodeOutput output)
    {
        Texture2D? tex = null;

        if (output.IsAnimated && output.FramePngs != null && output.FramePngs.Length > 0)
        {
            var frames = new Texture2D[output.FramePngs.Length];
            var ok = true;
            for (int i = 0; i < output.FramePngs.Length; i++)
            {
                var t = new Texture2D(2, 2);
                if (t.LoadImage(output.FramePngs[i]))
                    frames[i] = t;
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                tex = frames[0];
                _animatedFrames[code] = frames;
                _frameDelays[code] = output.FrameDelay;
                if (PluginConfig.Instance?.VerboseLogging == true)
                    Plugin.Log.Info($"EmoteCache: cached animated emote '{code}' ({frames.Length} frames, {output.Width}x{output.Height}, {output.FrameDelay * 1000f:F0}ms/frame).");
            }
        }

        if (tex == null && output.StaticPng != null)
        {
            var t = new Texture2D(2, 2);
            if (t.LoadImage(output.StaticPng))
                tex = t;
        }

        if (tex != null)
        {
            _textures[code] = tex;
            Touch(code);
            var bytes = (long)output.Width * output.Height * 4
                * (output.IsAnimated && output.FramePngs != null ? output.FramePngs.Length : 1);
            _emoteBytes[code] = bytes;
            _estimatedBytes += bytes;
            if (PluginConfig.Instance?.VerboseLogging == true)
                Plugin.Log.Info($"EmoteCache: cached emote '{code}' ({tex.width}x{tex.height}, ~{bytes / 1024}KB).");
        }
        else
        {
            Plugin.Log.Warn($"EmoteCache: decode produced no texture for '{code}' (failed={output.Failed}).");
        }

        EvictIfNeeded();
        _fetching.Remove(code);
        FirePending(code, tex);
    }

    private void FirePending(string code, Texture2D? tex)
    {
        if (!_pendingByCode.TryGetValue(code, out var list)) return;
        _pendingByCode.Remove(code);
        if (tex == null) return;
        foreach (var cb in list)
        {
            try { cb(code, tex); }
            catch (Exception ex) { Plugin.Log.Warn($"EmoteCache: pending callback failed for '{code}': {ex.Message}"); }
        }
    }

    /// <summary>
    /// Background-fetches GIF frames for an animated emote that was loaded
    /// from PNG disk cache. The PNG only has the first frame; this method
    /// downloads the full GIF so animated playback works.
    /// </summary>
    private IEnumerator FetchAnimatedFramesOnly(string code, EmoteInfo info)
    {
        if (string.IsNullOrEmpty(info.GifUrl)) yield break;

        _fetchingAnimation.Add(code);
        try
        {
            var gifUrl = info.GifUrl;
            byte[]? bytes = null;
            using (var www = UnityEngine.Networking.UnityWebRequest.Get(gifUrl))
            {
                www.SetRequestHeader("User-Agent", UserAgent);
                www.timeout = 10;
                yield return www.SendWebRequest();
                if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    yield break;
                bytes = www.downloadHandler.data;
            }

            var codeLocal = code;
            System.Threading.Tasks.Task.Run(() =>
            {
                var output = DecodeOffMainThread(bytes!, true);
                RuntimeHooks.RunOnMainThread(() => FinalizeAnimatedOnly(codeLocal, output));
            });
        }
        finally
        {
            _fetchingAnimation.Remove(code);
        }
    }

    /// <summary>
    /// Main-thread completion for FetchAnimatedFramesOnly: builds the animated
    /// frame textures and stores them for playback.
    /// </summary>
    private void FinalizeAnimatedOnly(string code, DecodeOutput output)
    {
        if (output.IsAnimated && output.FramePngs != null && output.FramePngs.Length > 0)
        {
            var frames = new Texture2D[output.FramePngs.Length];
            var ok = true;
            for (int i = 0; i < output.FramePngs.Length; i++)
            {
                var t = new Texture2D(2, 2);
                if (t.LoadImage(output.FramePngs[i]))
                    frames[i] = t;
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                _animatedFrames[code] = frames;
                _frameDelays[code] = output.FrameDelay;
                Touch(code);
                if (PluginConfig.Instance?.VerboseLogging == true)
                Plugin.Log.Info($"EmoteCache: fetched animated frames for '{code}' ({frames.Length} frames, {output.Width}x{output.Height}, {output.FrameDelay * 1000f:F0}ms/frame).");
            }
        }
        else
        {
            Plugin.Log.Warn($"EmoteCache: animated frame fetch produced no frames for '{code}' (failed={output.Failed}).");
        }
    }
}

internal sealed class EmoteAnimator : MonoBehaviour
{
    private EmoteHudEntry? _hudEntry;
    private Texture2D[]? _frames;
    private int _frameIndex;
    private float _frameDelay;
    private float _timer;

    internal void Initialize(Texture2D[] frames, float frameDelay, EmoteHudEntry hudEntry)
    {
        _frames = frames;
        _frameDelay = Mathf.Max(0.03f, frameDelay);
        _hudEntry = hudEntry;
        _frameIndex = 0;
        _timer = 0f;
        if (_frames.Length > 0)
            _hudEntry.CurrentTexture = _frames[0];
    }

    private void Update()
    {
        if (_frames == null || _frames.Length <= 1) return;

        _timer += Time.deltaTime;
        if (_timer < _frameDelay) return;
        _timer -= _frameDelay;

        _frameIndex = (_frameIndex + 1) % _frames.Length;

        if (_hudEntry != null)
            _hudEntry.CurrentTexture = _frames[_frameIndex];
    }
}

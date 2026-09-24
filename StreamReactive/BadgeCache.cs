using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace StreamReactive;

/// <summary>
/// Downloads and caches Twitch chat badge images (x1 resolution) so the chat
/// panel can render them next to usernames instead of textual tags.
///
/// Twitch retired its legacy badge catalog API (badges.twitch.tv) in 2023 and
/// its successor (Helix chat badges) requires an OAuth token, which this
/// plugin deliberately does not have - the IRC reader is anonymous. The badge
/// IMAGES, however, are long-lived hashed assets on static-cdn.jtvnw.net (the
/// same CDN that already serves this plugin's emote images), so this class
/// keeps a small, verified table of the official hashed URLs for the badge
/// sets the chat panel cares about. These hashes have been stable for years.
///
/// Only the "important" badge sets are rendered (broadcaster, moderator, VIP,
/// subscriber/founder, Prime, staff/partner, bits, prediction winners). Niche
/// sets like Dead By Daylight, NASA or Pokemon are ignored entirely. Every
/// badge image is downloaded once and kept in memory for the lifetime of the
/// session; there is no per-message fetching.
/// </summary>
internal static class BadgeCache
{
    private const string UserAgent = "StreamReactive/1.0";

    // set/version -> image hash on static-cdn.jtvnw.net. The hash (not the
    // whole URL) is stored so the x1/2/4 sizes can be derived.
    private static readonly Dictionary<string, string> Catalog = new(StringComparer.Ordinal)
    {
        ["broadcaster/1"] = "5527c58c-fb7d-422d-b71b-f309dcb85cc1",
        ["moderator/1"] = "3267646d-33f0-4b17-b3df-f923a41db1d0",
        ["vip/1"] = "b817aba4-fad8-49e2-b88a-7cc744dfa6ec",
        ["founder/1"] = "511b78a9-ab37-472f-9569-457753bbe7d3",
        ["subscriber/0"] = "5d9f2208-5dd8-11e7-8513-2ff4adfae661",
        ["staff/1"] = "d97c37bd-a6f5-4c38-8f57-4e4bef88af34",
        ["premium/1"] = "bbbe0db0-a598-423e-86d0-f9fb98ca1933",
        ["partner/1"] = "d12a2e27-16f6-41d0-ab77-b780518f00a3",
        ["predictions/1"] = "e33d8b46-f63b-4e67-996d-4a7dcec0ad33",
        ["predictions/2"] = "4b76d5f2-91cc-4400-adf2-908a1e6cfd1e",
        ["bits/1"] = "73b5c3fb-24f9-4a82-a852-2f475b59411c",
        ["bits/100"] = "09d93036-e7ce-431c-9a9e-7044297133f2",
        ["bits/1000"] = "0d85a29e-79ad-4c63-a285-3acd2c66f2ba",
        ["bits/5000"] = "57cd97fc-3e9e-4c6d-9d41-60147137234e",
        ["bits/10000"] = "68af213b-a771-4124-b6e3-9bb6d98aa732",
        ["bits/25000"] = "64ca5920-c663-4bd8-bfb1-751b4caea2dd",
        ["bits/50000"] = "62310ba7-9916-4235-9eba-40110d67f85d",
        ["bits/75000"] = "ce491fa4-b24f-4f3b-b6ff-44b080202792",
        ["bits/100000"] = "96f0540f-aa63-49e1-a8b3-259ece3bd098",
        ["bits/200000"] = "4a0b90c4-e4ef-407f-84fe-36b14aebdbb6",
        ["bits/300000"] = "ac13372d-2e94-41d1-ae11-ecd677f69bb6",
        ["bits/400000"] = "a8f393af-76e6-4aa2-9dd0-7dcc1c34f036",
        ["bits/500000"] = "f6932b57-6a6e-4062-a770-dfbd9f4302e5",
        ["bits/600000"] = "4d908059-f91c-4aef-9acb-634434f4c32e",
        ["bits/700000"] = "a1d2a824-f216-4b9f-9642-3de8ed370957",
        ["bits/800000"] = "5ec2ee3e-5633-4c2a-8e77-77473fe409e6",
        ["bits/900000"] = "088c58c6-7c38-45ba-8f73-63ef24189b84",
        ["bits/1000000"] = "494d1c8e-c3b2-4d88-8528-baff57c9bd3f",
    };

    // Fallbacks for versioned badge sets: channel-specific subscriber art and
    // month/founder tiers are per-channel and can't be known without an auth
    // token, so any version of these sets collapses to the official default
    // art. This keeps a badge - and its space - from silently disappearing.
    private static readonly Dictionary<string, string> DefaultVersions = new(StringComparer.Ordinal)
    {
        ["subscriber"] = "subscriber/0",
        ["founder"] = "founder/1",
        ["predictions"] = "predictions/1",
    };

    // "set/version" -> x1 image URL, derived once from Catalog + the CDN.
    private static readonly Dictionary<string, string> _urls = new(StringComparer.Ordinal);

    // Successfully decoded textures, keyed by "set/version".
    private static readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);

    // Badge downloads currently in flight, so a flood of identical messages
    // never kicks off duplicate downloads.
    private static readonly HashSet<string> _fetching = new(StringComparer.Ordinal);

    // Callbacks waiting for a specific badge to finish downloading.
    private static readonly Dictionary<string, List<Action<string, Texture2D>>> _pending = new(StringComparer.Ordinal);

    private static bool _catalogBuilt;

    /// <summary>True when the given badge set is one worth rendering as an image.</summary>
    internal static bool IsAllowed(string set)
        => !string.IsNullOrEmpty(set) && CatalogSetNames.Contains(set);

    // The set names present in the static catalog; keeping the two lists in
    // lockstep guarantees a parsed badge key can always resolve to a URL, so
    // rows never reserve space for a badge that cannot render.
    private static readonly HashSet<string> CatalogSetNames = new(StringComparer.Ordinal)
    {
        "broadcaster", "moderator", "vip", "founder", "subscriber",
        "staff", "premium", "partner", "predictions", "bits",
    };

    private static void EnsureCatalog()
    {
        if (_catalogBuilt) return;
        foreach (var kvp in Catalog)
            _urls[kvp.Key] = $"https://static-cdn.jtvnw.net/badges/v1/{kvp.Value}/1";
        _catalogBuilt = true;
    }

    /// <summary>
    /// Called when the IRC connection starts. The catalog is a fixed, verified
    /// table these days (Twitch's own badge catalog API is gone / token-gated),
    /// so this just warms it. The channel name is kept for interface parity.
    /// </summary>
    internal static void LoadChannel(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return;
        EnsureCatalog();
        if (_urls.Count > 0)
            Plugin.Log.Info($"BadgeCache: catalog ready ({_urls.Count} badge(s)); images for '{channelName}' download on demand.");
    }

    /// <summary>
    /// Returns the cached texture for a "set/version" key if it is already
    /// decoded, otherwise kicks off the download and returns false. Callers that
    /// can wait a frame should prefer <see cref="GetTextureAsync"/>.
    /// </summary>
    internal static bool TryGetTexture(string key, out Texture2D? texture)
    {
        if (_textures.TryGetValue(key, out texture!)) return true;
        texture = null;
        Request(key);
        return false;
    }

    /// <summary>
    /// Fires onReady(key, texture) once the badge is decoded. onReady runs on
    /// the main thread. If a key has no image (it shouldn't, given the catalog
    /// is fixed), onReady is never called; the caller leaves its placeholder
    /// hidden and size zero.
    /// </summary>
    internal static void GetTextureAsync(string key, Action<string, Texture2D> onReady)
    {
        if (_textures.TryGetValue(key, out var tex))
        {
            onReady(key, tex);
            return;
        }

        if (!_pending.TryGetValue(key, out var list))
        {
            list = new List<Action<string, Texture2D>>();
            _pending[key] = list;
        }
        list.Add(onReady);
        Request(key);
    }

    // Resolves a badge key to an image URL, applying the per-set default when
    // the specific version isn't in the catalog (e.g. subscriber/72 -> the
    // generic subscriber art).
    private static bool TryResolveUrl(string key, out string url)
    {
        if (_urls.TryGetValue(key, out url)) return true;
        var slash = key.IndexOf('/');
        if (slash > 0)
        {
            string? set = key.Substring(0, slash);
            if (set != null && DefaultVersions.TryGetValue(set, out var def) && _urls.TryGetValue(def, out url))
                return true;
        }
        url = string.Empty;
        return false;
    }

    private static void Request(string key)
    {
        if (_fetching.Contains(key)) return;
        if (!TryResolveUrl(key, out var url)) return;
        _fetching.Add(key);
        RuntimeHooks.RunCoroutine(FetchBadgeImage(key, url));
    }

    private static IEnumerator FetchBadgeImage(string key, string url)
    {
        using var www = UnityWebRequest.Get(url);
        www.SetRequestHeader("User-Agent", UserAgent);
        www.timeout = 15;
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Plugin.Log.Warn($"BadgeCache: download failed for '{key}': {www.error}");
            _fetching.Remove(key);
            yield break;
        }

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (tex.LoadImage(www.downloadHandler.data))
        {
            _textures[key] = tex;
            Fire(key, tex);
        }
        else
        {
            UnityEngine.Object.Destroy(tex);
            Plugin.Log.Warn($"BadgeCache: failed to decode image for '{key}'.");
        }
        _fetching.Remove(key);
    }

    private static void Fire(string key, Texture2D tex)
    {
        if (!_pending.TryGetValue(key, out var list)) return;
        _pending.Remove(key);
        foreach (var cb in list)
        {
            try { cb(key, tex); }
            catch (Exception ex) { Plugin.Log.Warn($"BadgeCache: callback failed for '{key}': {ex.Message}"); }
        }
    }
}
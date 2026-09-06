using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace StreamReactive;

/// <summary>
/// Loads and plays OGG sound bites dropped in UserData/StreamReactive/Sounds.
/// The folder is rescanned whenever the settings menu opens, so newly added
/// .ogg files appear in the event sound dropdowns without restarting the game.
/// Clips load lazily via UnityWebRequest and are cached for the session.
/// </summary>
internal static class SoundManager
{
    /// <summary>Dropdown display text for "no sound". Maps to "" in config.</summary>
    internal const string NoneName = "None";

    private const float MinVolume = 0.001f;
    private const float ScanCooldownSeconds = 1.5f;

    private static string SoundsPath =>
        Path.Combine(Environment.CurrentDirectory, "UserData", "StreamReactive", "Sounds");

    private static readonly Dictionary<string, AudioClip> Clips = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Loading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<AudioClip?>>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _available = new();
    private static float _lastScan;

    /// <summary>OGG filenames currently in the Sounds folder. Never contains NoneName.</summary>
    internal static List<string> AvailableSounds
    {
        get
        {
            if (_available.Count == 0 || Time.realtimeSinceStartup - _lastScan > ScanCooldownSeconds)
                RefreshAvailableSounds();
            return _available;
        }
    }

    /// <summary>Dropdown entries: "None" for silence plus every sound file.</summary>
    internal static List<object> SoundChoices()
    {
        var choices = new List<object> { NoneName };
        choices.AddRange(AvailableSounds);
        return choices;
    }

    /// <summary>Config value ("" or filename) -> dropdown display text.</summary>
    internal static string DisplayName(string fileName) =>
        string.IsNullOrEmpty(fileName) ? NoneName : fileName;

    /// <summary>Dropdown display text -> config value ("" for None).</summary>
    internal static string ConfigName(string display) =>
        string.Equals(display, NoneName, StringComparison.Ordinal) ? "" : (display ?? "");

    /// <summary>
    /// Pre-loads the configured event sound clips (async via the existing
    /// coroutine loader) so the first bits/sub/raid event never pays for the
    /// OGG download + decode on the frame it actually starts. Clips are cached
    /// for the session, so repeat calls (each scene load) are no-ops.
    /// </summary>
    internal static void Prewarm()
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null)
            return;

        WarmSound(cfg.RaidSoundFile);
        WarmSound(cfg.SubSoundFile);
        WarmSound(cfg.BombSoundFile);
        WarmSound(cfg.BitTier10000SoundFile);
        WarmSound(cfg.BitTier5000SoundFile);
        WarmSound(cfg.BitTier1000SoundFile);
        WarmSound(cfg.BitTier100SoundFile);
        WarmSound(cfg.BitTierDefaultSoundFile);
        WarmSound(cfg.ThrowHitSoundFile);
    }

    private static void WarmSound(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return;
        GetClip(fileName, _ => { });
    }

    internal static void RefreshAvailableSounds()
    {
        _lastScan = Time.realtimeSinceStartup;
        var found = new List<string>();
        try
        {
            var dir = SoundsPath;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.ogg", SearchOption.TopDirectoryOnly))
                    found.Add(Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"SoundManager: sounds folder scan failed: {ex.Message}");
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        _available = found;
    }

    /// <summary>
    /// Plays the sound mapped to a stream event. Bomb events play on cut
    /// (ProcessNoteAtCut), so nothing fires here. Bits resolve their sound from
    /// the tier of the cheer amount. Must run on the main thread (StartEvent /
    /// LateUpdate paths do).
    /// </summary>
    internal static void PlayEventSound(StreamEvent evt)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null || evt == null)
            return;

        switch (evt.Type)
        {
            case StreamEventType.Bits:
                var (file, volume) = BitTierSoundFor(evt.Amount, cfg);
                Play(file, volume);
                break;
            case StreamEventType.Sub:
                Play(cfg.SubSoundFile, cfg.SubSoundVolume);
                break;
            case StreamEventType.Raid:
                Play(cfg.RaidSoundFile, cfg.RaidSoundVolume);
                break;
        }
    }

    /// <summary>
    /// Plays a sound centered on both ears (always 2D; no spatial positioning).
    /// </summary>
    internal static void Play(string fileName, float volume)
    {
        if (string.IsNullOrEmpty(fileName))
            return;

        volume = Mathf.Clamp01(volume);
        if (volume < MinVolume)
            return;

        GetClip(fileName, clip =>
        {
            if (clip == null)
                return;
            Play2D(clip, volume);
        });
    }

    private static (string File, float Volume) BitTierSoundFor(int amount, PluginConfig cfg)
    {
        if (amount >= 10000) return (cfg.BitTier10000SoundFile, cfg.BitTier10000SoundVolume);
        if (amount >= 5000) return (cfg.BitTier5000SoundFile, cfg.BitTier5000SoundVolume);
        if (amount >= 1000) return (cfg.BitTier1000SoundFile, cfg.BitTier1000SoundVolume);
        if (amount >= 100) return (cfg.BitTier100SoundFile, cfg.BitTier100SoundVolume);
        return (cfg.BitTierDefaultSoundFile, cfg.BitTierDefaultSoundVolume);
    }

    private static void Play2D(AudioClip clip, float volume)
    {
        var go = new GameObject("SR_Sound2D");
        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.spatialBlend = 0f;
        src.volume = volume;
        src.playOnAwake = false;
        src.Play();
        RuntimeHooks.RunCoroutine(DestroyAfterPlaying(go, clip.length + 0.2f));
    }

    private static IEnumerator DestroyAfterPlaying(GameObject go, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (go != null)
            UnityEngine.Object.Destroy(go);
    }

    private static void GetClip(string fileName, Action<AudioClip?> callback)
    {
        if (Clips.TryGetValue(fileName, out var clip) && clip != null)
        {
            callback(clip);
            return;
        }

        if (Failed.Contains(fileName))
        {
            callback(null);
            return;
        }

        if (!Pending.TryGetValue(fileName, out var waiters))
        {
            waiters = new List<Action<AudioClip?>>();
            Pending[fileName] = waiters;
            RuntimeHooks.RunCoroutine(LoadCoroutine(fileName));
        }
        waiters.Add(callback);
    }

    private static IEnumerator LoadCoroutine(string fileName)
    {
        var path = Path.Combine(SoundsPath, fileName);
        UnityWebRequest request;
        try
        {
            request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
        }
        catch (Exception ex)
        {
            Failed.Add(fileName);
            Plugin.Log.Warn($"SoundManager: bad path for '{fileName}': {ex.Message}");
            yield break;
        }

        yield return request.SendWebRequest();

        AudioClip? result = null;
        try
        {
            if (request.result == UnityWebRequest.Result.Success
                && request.downloadHandler is DownloadHandlerAudioClip handler
                && handler.audioClip != null)
            {
                result = handler.audioClip;
                result.name = fileName;
                Clips[fileName] = result;
                Plugin.Log.Info($"SoundManager: loaded '{fileName}' ({result.length:F1}s).");
            }
            else
            {
                Plugin.Log.Warn($"SoundManager: failed to load '{fileName}': {request.error ?? request.result.ToString()}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"SoundManager: loading '{fileName}' threw: {ex.Message}");
        }
        finally
        {
            request.Dispose();
        }

        if (result == null)
            Failed.Add(fileName);

        if (!Pending.TryGetValue(fileName, out var waiters))
            yield break;

        Pending.Remove(fileName);
        foreach (var wait in waiters)
        {
            try { wait(result); }
            catch (Exception ex) { Plugin.Log.Debug($"SoundManager: play callback for '{fileName}' failed: {ex.Message}"); }
        }
    }
}
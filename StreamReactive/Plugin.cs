using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IPA;
using IPA.Config.Stores;
using HarmonyLib;
using BS_Utils.Utilities;
using System.Reflection;
using UnityEngine;
using Config = IPA.Config.Config;
using IPALogger = IPA.Logging.Logger;

namespace StreamReactive;

[Plugin(RuntimeOptions.SingleStartInit)]
public class Plugin
{
    internal static IPALogger Log { get; private set; } = null!;
    internal static Plugin Instance { get; private set; } = null!;
    internal static Harmony? HarmonyInstance { get; private set; }
    internal static bool NoteTweaksLoaded { get; private set; }

    // Detected once at startup; used to guard which SongCore-backed map-detection
    // paths we touch (SongCore is present in most installs, but we degrade
    // gracefully to no-op gating if it is missing at runtime).
    internal static bool SongCoreLoaded { get; private set; }

    // Detected once at startup; guards the SongDetailsCache-backed ranked-map
    // detection path (see RefreshCurrentMapInfo). SongDetailsCache is an optional
    // library mod, so we no-op ranked protection when it is absent.
    internal static bool SongDetailsLoaded { get; private set; }

    // Raised once SongDetailsCache finishes loading its local ranked database, so
    // the per-map lookups know to start resolving ranked status. Best-effort: if
    // the DB never finishes loading we simply never block on ranked maps.
    private SongDetailsCache.SongDetails? _songDetails;
    private bool _songDetailsInitStarted;

    // Per-map protection state, refreshed on each game scene load (see
    // RefreshCurrentMapInfo). Cached so the per-event gate is O(1) and never
    // does file I/O on the game loop.
    private string _currentLevelId = "";
    private bool _mapHasNoodle;
    private bool _mapHasVivify;
    private bool _mapIsWip;
    private bool _mapIsRanked;

    // Flashbangs received while a protected map is active are held here and
    // replayed at the start of the next non-protected map (see
    // HoldFlashbang / TryDrainHeldFlashbangs). Guarded so WebSocket/chat threads
    // can enqueue while the main thread drains.
    private readonly object _heldFlashbangLock = new();
    private readonly List<int> _heldFlashbangs = new();

    private WebSocketServer? _wsServer;
    private TwitchChatReader? _chatReader;
    private bool _inGame;

    // True between the game scene loading and the moment RefreshCurrentMapInfo has
    // obtained a levelId. During that window the map's protection flags are still
    // unknown (they read as "unprotected"), so queued menu events would otherwise
    // start on a protected map before detection finishes. While pending we treat
    // the map as protected to hold events until detection lands.
    private bool _mapInfoPending;

    [Init]
    public Plugin(IPALogger logger, Config config)
    {
        Instance = this;
        Log = logger;

        PluginConfig.Instance = config.Generated<PluginConfig>();
        if (PluginConfig.Instance.EmoteRainPreview)
            EmoteRainPreview.SetVisible(true);
        Log.Info("StreamReactive initialized.");
    }

    [OnStart]
    public void OnApplicationStart()
    {
        HarmonyInstance = new Harmony("com.fefeland.StreamReactive");
        try
        {
            HarmonyInstance!.PatchAll(Assembly.GetExecutingAssembly());
            Log.Info("Harmony patches applied.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Some Harmony patches failed: {ex.Message}");
        }

        ParticleSpawner.Init();

        _wsServer = new WebSocketServer(PluginConfig.Instance!.WebSocketPort);
        _wsServer.Start();

        BSEvents.gameSceneLoaded += OnGameSceneLoaded;
        BSEvents.menuSceneLoaded += OnMenuSceneLoaded;

        RuntimeHooks.EnsureCreated();
        CapsuleGuardController.EnsureCreated();
        FlashbangController.EnsureCreated();
        ProjectionController.EnsureCreated();

        var userDataDir = Path.Combine(Environment.CurrentDirectory, "UserData", "StreamReactive");
        try
        {
            Directory.CreateDirectory(Path.Combine(userDataDir, "Projections"));
            Directory.CreateDirectory(Path.Combine(userDataDir, "Sounds"));
        }
        catch (Exception ex) { Log.Warn($"Failed to create user data folders: {ex.Message}"); }

        MainConfigMenu.Init();
        ChatPanelController.Init();

        StartEmoteSystem();

        // Warm the emote sprite material (loads an embedded AssetBundle on the
        // main thread) now, during plugin init, so the first emote spawn never
        // pays that cost on a gameplay frame.
        EmoteVisualFactory.GetSpriteMaterial();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "NoteTweaks")
            {
                NoteTweaksLoaded = true;
                Log.Info("NoteTweaks detected — using flexible note-block detection for bomb visuals and throw cube mesh capture.");
                break;
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "SongCore")
            {
                SongCoreLoaded = true;
                Log.Info("SongCore detected — map-protection detection (Noodle/Vivify/WIP) enabled.");
                break;
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "SongDetailsCache")
            {
                SongDetailsLoaded = true;
                Log.Info("SongDetailsCache detected — ranked-map protection detection enabled.");
                break;
            }
        }

        Log.Info("StreamReactive started.");
    }

    [OnExit]
    public void OnApplicationQuit()
    {
        BSEvents.gameSceneLoaded -= OnGameSceneLoaded;
        BSEvents.menuSceneLoaded -= OnMenuSceneLoaded;

        _wsServer?.Stop();
        _wsServer = null;

        StopEmoteSystem();

        try { HarmonyInstance?.UnpatchSelf(); }
        catch { }

        Log.Info("StreamReactive unloaded.");
    }

    private void OnGameSceneLoaded()
    {
        _inGame = true;
        // Map-protection state is unknown until RefreshCurrentMapInfo runs below,
        // so gate events from the moment the scene loads (see _mapInfoPending).
        _mapInfoPending = true;
        // Defer map-info detection a few frames so BS_Utils has time to populate
        // its LevelData.GameplayCoreSceneSetupData. Reading it synchronously
        // during gameSceneLoaded returns an empty/invalid beatmapKey.
        RuntimeHooks.RunCoroutine(RefreshCurrentMapInfoDelayed());
        NoteCosmeticController.OnGameSceneLoaded();
        NoteCosmeticController.Prewarm();
        ProjectileThrower.OnSceneChanged();
        ProjectileThrower.Prewarm();
        ComboThrowController.ResetCombo();
        Log.Debug("Game scene loaded.");
    }

    private IEnumerator RefreshCurrentMapInfoDelayed()
    {
        // Retry over a short window so we capture the map info even if BS_Utils
        // finishes populating its level data a few frames after the scene loads.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            yield return null;
            if (!_inGame)
                yield break;
            if (!string.IsNullOrEmpty(HandleGetCurrentLevelId()))
                break;
        }
        RefreshCurrentMapInfo();

        // Once the next map's protection is known, replay any flashbangs that were
        // held while paused or on a protected map — but only if this new map isn't
        // protected too and the mod isn't still manually paused.
        if (_inGame && !IsDispatchBlockedByMapProtection() && !(PluginConfig.Instance?.Paused == true))
            TryDrainHeldFlashbangs();
    }

    private void OnMenuSceneLoaded()
    {
        _inGame = false;
        _mapInfoPending = false;
        NoteCosmeticController.OnMenuSceneLoaded();
        ProjectileThrower.OnSceneChanged();
        ProjectileThrower.Prewarm();
        Log.Debug("Menu scene loaded.");
    }

    /// <summary>
    /// Detects (once per map, off the gameplay loop) whether the currently loaded
    /// map is a NoodleExtensions / Vivify / WIP / Ranked map and caches it. All
    /// detection is best-effort and guarded so failures never affect gameplay.
    /// </summary>
    private void RefreshCurrentMapInfo()
    {
        _mapHasNoodle = false;
        _mapHasVivify = false;
        _mapIsWip = false;
        _mapIsRanked = false;
        _currentLevelId = "";

        try
        {
            _currentLevelId = HandleGetCurrentLevelId();
            if (string.IsNullOrEmpty(_currentLevelId))
            {
                Log.Debug("Map protection: could not obtain levelId (BS_Utils data not ready?).");
                return;
            }
            _mapInfoPending = false;

            var levelId = _currentLevelId;

            // Ranked status (BeatLeader / ScoreSaber) comes from the
            // SongDetailsCache mod's local database, keyed by the map's hash. It
            // only needs the levelId + a loaded SDC database, so it runs even when
            // SongCore is absent. Best-effort: if SDC isn't loaded or its database
            // isn't ready yet we just leave _mapIsRanked false for this map.
            if (SongDetailsLoaded)
            {
                _mapIsRanked = TryLookupRankedStatus(levelId);
            }

            // The Noodle/Vivify/WIP checks below are backed by SongCore.
            if (!SongCoreLoaded)
            {
                Log.Debug("Map protection: SongCore not present, skipping Noodle/Vivify/WIP detection.");
                return;
            }

            // SongCore marks WIP levels with a trailing " WIP" on the level ID
            // (e.g. "custom_level_<hash> WIP"). That suffix also breaks SongCore
            // lookups keyed by the plain level ID, so we keep a normalized copy
            // (suffix stripped) for those lookups below.
            string songDataLevelId = StripWipSuffix(levelId);
            bool isWipBySuffix = songDataLevelId.Length != levelId.Length;

            // WIP: the definitive signal is the level being a WIP song — SongCore
            // appends " WIP" to the ID — cross-checked against WIP song folders and
            // SongCore's WIP registries.
            string? folderPath = null;
            object? saveData = null;
            if (!isWipBySuffix)
            {
                saveData = HandleGetLoadedSaveData(songDataLevelId);
                if (saveData == null && songDataLevelId != levelId)
                    saveData = HandleGetLoadedSaveData(levelId);
                if (saveData != null)
                    folderPath = HandleGetMapFolderPath(saveData);
            }

            _mapIsWip = isWipBySuffix
                        || IsWipMapByFolder(levelId)
                        || IsWipMapByFolder(songDataLevelId)
                        || SongCore.Loader.CustomWIPLevels.ContainsKey(levelId)
                        || SongCore.Loader.CustomWIPLevels.ContainsKey(songDataLevelId)
                        || (!string.IsNullOrEmpty(folderPath)
                            && folderPath!.IndexOf("CustomWIPLevels", StringComparison.OrdinalIgnoreCase) >= 0);

            // Noodle / Vivify come from the map's declared requirements. We use
            // SongCore's public, documented API first (GetCustomLevelSongData),
            // then the raw Info.dat from disk as a robust fallback.
            var reqs = GetRequirementsViaSongData(levelId)
                       ?? GetRequirementsViaSongData(songDataLevelId);
            if (reqs == null && saveData != null)
                reqs = HandleGetSongRequirements(saveData);
            reqs ??= (folderPath == null ? null : ReadInfoDatRequirements(folderPath));

            if (reqs != null)
            {
                foreach (var r in reqs)
                {
                    if (r.IndexOf("Noodle", StringComparison.OrdinalIgnoreCase) >= 0)
                        _mapHasNoodle = true;
                    else if (r.IndexOf("Vivify", StringComparison.OrdinalIgnoreCase) >= 0)
                        _mapHasVivify = true;
                }
            }

            Log.Debug(
                $"Map protection: levelId={_currentLevelId} (wipSuffix={isWipBySuffix}) folder={folderPath ?? "(none)"} noodle={_mapHasNoodle} vivify={_mapHasVivify} wip={_mapIsWip} ranked={_mapIsRanked}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not refresh current-map info for protection: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves whether the currently-loaded map is ranked (BeatLeader, ScoreSaber,
    /// or both) using the SongDetailsCache database, matching the map hash embedded
    /// in the level ID. Ensures SDC is initialized (once) before querying, and is
    /// fully best-effort: returns false and logs a diagnostic if SDC isn't ready or
    /// the map isn't found so failures never affect gameplay.
    /// </summary>
    private bool TryLookupRankedStatus(string levelId)
    {
        try
        {
            EnsureSongDetailsInit();

            if (_songDetails == null)
            {
                Log.Debug("Map protection: ranked lookup skipped (SongDetailsCache database not ready yet).");
                return false;
            }

            string? hash = ExtractMapHash(levelId);
            if (hash == null)
            {
                Log.Debug("Map protection: ranked lookup skipped (no hash in levelId).");
                return false;
            }

            if (_songDetails.songs.FindByHash(hash, out var song))
            {
                var states = song.rankedStates;
                bool ranked = states.HasFlag(SongDetailsCache.Structs.RankedStates.ScoresaberRanked)
                              || states.HasFlag(SongDetailsCache.Structs.RankedStates.BeatleaderRanked);
                Log.Debug($"Map protection: ranked lookup for {hash} = {ranked} ({states})");
                return ranked;
            }

            Log.Debug($"Map protection: ranked lookup found no song for hash {hash}.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve ranked status for current map: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Kicks off (once) SongDetailsCache's database load off the main thread and
    /// marks the ranked lookup ready when it completes. Fire-and-forget; the
    /// per-map lookup degrades gracefully until this finishes.
    /// </summary>
    private void EnsureSongDetailsInit()
    {
        if (_songDetailsInitStarted || !SongDetailsLoaded)
            return;
        _songDetailsInitStarted = true;
        try
        {
            Task.Run(async () =>
            {
                try
                {
                    _songDetails = await SongDetailsCache.SongDetails.Init();
                    Log.Info("SongDetailsCache database loaded — ranked-map protection active.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"SongDetailsCache init failed ({ex.Message}); ranked-map protection stays off.");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start SongDetailsCache init ({ex.Message}).");
        }
    }

    /// <summary>
    /// Extracts the map's 40-char beatmap hash from a custom level ID of the form
    /// "custom_level_&lt;hash&gt;". Returns null if the ID isn't a custom-level ID.
    /// </summary>
    private static string? ExtractMapHash(string levelId)
    {
        const string prefix = "custom_level_";
        if (levelId.StartsWith(prefix, StringComparison.Ordinal))
        {
            string hash = levelId.Substring(prefix.Length);
            // WIP IDs carry a trailing " WIP" that must be stripped to get the hash.
            return StripWipSuffix(hash).ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Reads the <c>_requirements</c> array straight from the map's Info.dat on
    /// disk — the most reliable source for Noodle/Vivify detection.
    /// </summary>
    private static IEnumerable<string>? ReadInfoDatRequirements(string folderPath)
    {
        try
        {
            var infoPath = Path.Combine(folderPath, "Info.dat");
            if (!File.Exists(infoPath))
                return null;

            var raw = File.ReadAllText(infoPath);
            return ParseRequirementsFromJson(raw);
        }
        catch (Exception ex)
        {
            Log.Warn($"Map protection: could not read Info.dat at '{folderPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns the on-disk folder path the given map's save data was loaded from.</summary>
    private static string? HandleGetMapFolderPath(object saveData)
    {
        try
        {
            var folderInfo = ReadMemberValue(saveData, "customLevelFolderInfo");
            if (folderInfo == null)
                return null;
            return ReadMemberValue(folderInfo, "folderPath") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true when the given level is a member of a WIP song folder, using
    /// SongCore's public <c>SeparateSongFolders</c> list (each entry's
    /// <c>SongFolderEntry.WIP</c> flag + <c>Levels</c> map keyed by levelId).
    /// </summary>
    private static bool IsWipMapByFolder(string levelId)
    {
        try
        {
            foreach (var folder in SongCore.Loader.SeparateSongFolders)
            {
                if (folder?.SongFolderEntry?.WIP != true)
                    continue;
                if (folder.Levels != null && folder.Levels.ContainsKey(levelId))
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// Removes the trailing " WIP" marker SongCore appends to WIP level IDs,
    /// returning the plain level ID used by SongCore's data caches. If the input is
    /// not a WIP ID it is returned unchanged.
    /// </summary>
    private static string StripWipSuffix(string levelId)
    {
        const string suffix = " WIP";
        if (levelId.EndsWith(suffix, StringComparison.Ordinal))
            return levelId.Substring(0, levelId.Length - suffix.Length);
        return levelId;
    }

    /// <summary>
    /// Gets the currently-playing map's levelId from BS_Utils' captured scene
    /// setup data (public, version-stable accessor).
    /// </summary>
    private static string HandleGetCurrentLevelId()
    {
        var level = BS_Utils.Plugin.LevelData?.GameplayCoreSceneSetupData;
        if (level == null)
            return "";
        try
        {
            return level.beatmapKey.levelId ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Pulls the parsed song save data for a level from SongCore's cache. SongCore
    /// stores it in a private static dictionary keyed by levelId; we fish it out by
    /// reflection once per map (guarded, tries to stay resilient across versions).
    /// </summary>
    private static object? HandleGetLoadedSaveData(string levelId)
    {
        try
        {
            var loaderType = typeof(SongCore.Loader);
            var field = loaderType.GetField(
                "LoadedBeatmapSaveData",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                return null;

            if (field.GetValue(null) is not System.Collections.IDictionary dict)
                return null;

            return dict.Contains(levelId) ? dict[levelId] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads Noodle/Vivify-style requirements via SongCore's public
    /// <c>Collections.GetCustomLevelSongData(levelId)</c> — no reflection needed.
    /// Requirements are declared per-difficulty under <c>_difficulties[i]</c>.
    /// </summary>
    private static IEnumerable<string>? GetRequirementsViaSongData(string levelId)
    {
        try
        {
            var song = SongCore.Collections.GetCustomLevelSongData(levelId);
            if (song == null)
                return null;

            var result = new List<string>();
            var difficulties = ReadMemberValue(song, "_difficulties") as System.Collections.IEnumerable;
            if (difficulties == null)
                return result;

            foreach (var d in difficulties)
            {
                if (d == null)
                    continue;
                var reqData = ReadMemberValue(d, "additionalDifficultyData");
                if (reqData == null)
                    continue;
                var reqs = ReadMemberValue(reqData, "_requirements");
                if (reqs is System.Collections.IEnumerable seq)
                {
                    foreach (var item in seq)
                    {
                        var s = item?.ToString();
                        if (!string.IsNullOrEmpty(s) && !result.Contains(s!))
                            result.Add(s!);
                    }
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"Map protection: GetCustomLevelSongData failed for {levelId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the declared song requirements (<c>Info.dat</c>) from the parsed save
    /// data, using the stable accessors where they exist and falling back to a
    /// reflective read of the requirement list.
    /// </summary>
    private static IEnumerable<string>? HandleGetSongRequirements(object saveData)
    {
        try
        {
            // newer SongCore: LoadedSaveData.standardLevelInfoSaveData.songRequirements
            var info = ReadMemberValue(saveData, "standardLevelInfoSaveData");
            if (info != null)
            {
                var value = ReadMemberValue(info, "songRequirements");
                if (value is System.Collections.IEnumerable seq)
                {
                    var list = new List<string>();
                    foreach (var item in seq)
                        list.Add(item?.ToString() ?? "");
                    return list;
                }
            }

            // older SongCore: LoadedSaveData.customLevelFolderInfo / beatmapLevelSaveData
            var folderInfo = ReadMemberValue(saveData, "customLevelFolderInfo");
            var json = folderInfo == null ? null : ReadMemberValue(folderInfo, "levelInfoJsonString") as string;
            if (!string.IsNullOrEmpty(json))
                return ParseRequirementsFromJson(json!);
        }
        catch
        {
        }
        return null;
    }

    /// <summary>Reads a property or field (public or private) by name.</summary>
    private static object? ReadMemberValue(object target, string name)
    {
        var t = target.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var pi = t.GetProperty(name, flags);
        if (pi != null)
            return pi.GetValue(target);
        var fi = t.GetField(name, flags);
        return fi?.GetValue(target);
    }

    private static IEnumerable<string> ParseRequirementsFromJson(string json)
    {
        try
        {
            var token = Newtonsoft.Json.Linq.JObject.Parse(json);
            var arr = token["_requirements"] ?? token["requirements"];
            if (arr is Newtonsoft.Json.Linq.JArray ja)
            {
                var result = new List<string>();
                foreach (var item in ja.Values<string>())
                {
                    if (item != null && item.Length > 0)
                        result.Add(item);
                }
                return result;
            }
        }
        catch
        {
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns true when the current map-protection settings block event dispatch
    /// for the loaded map. Only consults the cached flags (no file I/O / reflection
    /// on the event gate).
    /// </summary>
    private bool IsDispatchBlockedByMapProtection()
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null)
            return false;
        if (cfg.PauseOnNoodleMaps && SongCoreLoaded && _mapHasNoodle)
            return true;
        if (cfg.PauseOnVivifyMaps && SongCoreLoaded && _mapHasVivify)
            return true;
        if (cfg.PauseOnWipMaps && SongCoreLoaded && _mapIsWip)
            return true;
        if (cfg.PauseOnRankedMaps && SongDetailsLoaded && _mapIsRanked)
            return true;
        return false;
    }

    /// <summary>
    /// True while a protected map (Noodle/Vivify/WIP/Ranked) is loaded AND its
    /// protection toggle is enabled, or while the current map's protection is
    /// still being detected (_mapInfoPending). Used anywhere to treat the mod as
    /// paused for that map.
    /// Only applies while actually inside a song: back in the menu the previous
    /// map's protection no longer gates anything, so menu-safe one-shot effects
    /// (flashbang, throw cube, projections, emotes) keep working between maps.
    /// Note/bits/sub/raid/bomb events stay buffered in the menu regardless of
    /// protection (they need notes to exist) and resume on the next game scene.
    /// </summary>
    internal static bool IsMapProtectionActive()
        => Instance != null && Instance._inGame
            && (Instance._mapInfoPending || Instance.IsDispatchBlockedByMapProtection());

    /// <summary>
    /// Called when a flashbang event arrives while the mod is paused or a protected
    /// map is active. The flash is always remembered and replayed later rather than
    /// dropped, so the viewer's bits/gift isn't wasted. Returns true when held.
    /// </summary>
    internal static bool HoldFlashbang()
    {
        var instance = Instance;
        if (instance == null)
            return false;
        lock (instance._heldFlashbangLock)
        {
            instance._heldFlashbangs.Add(1);
        }
        NoteCosmeticController.VerboseLog($"Flashbang deferred (paused/protected); held queue now {instance._heldFlashbangs.Count}.");
        return true;
    }

    /// <summary>
    /// Replays any flashbangs that were deferred while paused or a protected map
    /// was active. Runs only once we are in a playable scene and on the main thread.
    /// Flashbangs are spaced apart so several held ones don't stack a single
    /// endless blind.
    /// </summary>
    internal void TryDrainHeldFlashbangs()
    {
        int count;
        lock (_heldFlashbangLock)
        {
            count = _heldFlashbangs.Count;
            if (count == 0)
                return;
            _heldFlashbangs.Clear();
        }
        Log.Info($"Replaying {count} deferred flashbang(s).");
        RuntimeHooks.RunCoroutine(ReplayHeldFlashbangsCoroutine(count));
    }

    private System.Collections.IEnumerator ReplayHeldFlashbangsCoroutine(int count)
    {
        for (int i = 0; i < count; i++)
        {
            FlashbangController.Flash();
            // Gap of at least the configured flash length so consecutive deferred
            // flashbangs don't stack into one uninterruptible blind.
            float dur = Mathf.Clamp(PluginConfig.Instance?.FlashbangDuration ?? 4f, 0.5f, 10f);
            if (i < count - 1)
                yield return new WaitForSeconds(Mathf.Max(dur, 0.5f));
        }
    }

    internal static bool IsInGame => Instance?._inGame ?? false;

    internal static Color GetBitTierColor(int amount)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return Color.gray;
        if (amount >= 10000) return cfg.BitTier10000Color;
        if (amount >= 5000) return cfg.BitTier5000Color;
        if (amount >= 1000) return cfg.BitTier1000Color;
        if (amount >= 100) return cfg.BitTier100Color;
        return cfg.BitTierDefaultColor;
    }

    internal static void DispatchStreamEvent(string type, string user, int amount, Color? color = null, string message = "")
    {
        var cfg = PluginConfig.Instance;
        if (cfg?.Enabled != true)
            return;

        // Lazy one-shot: if detection hasn't finished yet (first few events before
        // the deferred coroutine fires), force an immediate refresh so the flags
        // are correct while a protected map loads. This never drops events — during
        // a protected map they are accepted into the queue and held (replayed on the
        // next valid scene), exactly like pausing.
        if (Instance != null
            && Instance._inGame
            && (SongCoreLoaded || SongDetailsLoaded)
            && string.IsNullOrEmpty(Instance._currentLevelId))
        {
            try { Instance.RefreshCurrentMapInfo(); } catch { }
        }

        if (!IsInGame)
            NoteCosmeticController.VerboseLog($"Stream event received while not in game; will queue for next map: {type} from {user}");

        // Apply configured colors (override any color from the WS payload so the
        // UI color pickers are what actually drive each event type). Bombs are the
        // exception: an explicit color in the payload is honored (otherwise the
        // BombColorMode setting is used).
        bool isBomb = string.Equals(type, "bomb", StringComparison.OrdinalIgnoreCase);
        bool isSub = string.Equals(type, "subscription", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "sub", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "subscription_gift", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "gift", StringComparison.OrdinalIgnoreCase);
        bool isGift = string.Equals(type, "subscription_gift", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "gift", StringComparison.OrdinalIgnoreCase);
        bool isRaid = string.Equals(type, "raid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "host", StringComparison.OrdinalIgnoreCase);

        // Configured colors are the defaults for bits/sub/raid, but an explicit
        // color sent with the event still wins so triggers can override on the
        // fly (e.g. a channel-point color picker). Only a null payload color
        // falls back to the configured/tier color. Note: the payload color
        // stays NULL until here - any earlier `?? Color.white` would destroy
        // the "was a color provided?" signal and white out the defaults.
        if (string.Equals(type, "bits", StringComparison.OrdinalIgnoreCase))
            color ??= GetBitTierColor(amount);
        else if (isSub)
            color ??= cfg.SubParticleColor;
        else if (isRaid)
            color ??= cfg.RaidParticleColor;
        else if (!isBomb)
            color ??= cfg.BitsColor;

        NoteCosmeticController.VerboseLog($"Stream event: type={type}, user={user}, amount={amount}, color={color}");

        Color resolvedColor = color ?? Color.white;

        if (isBomb)
        {
            Color bombColor;
            bool rainbow = false;
            if (color.HasValue)
            {
                bombColor = color.Value;
            }
            else
            {
                switch (cfg.BombColorMode)
                {
                    case BombColorMode.Random:
                        bombColor = NoteCosmeticController.RandomVibrantColor();
                        break;
                    case BombColorMode.Rainbow:
                        bombColor = NoteCosmeticController.RainbowColor(cfg.BombRainbowSpeed);
                        rainbow = true;
                        break;
                    default:
                        bombColor = cfg.BombParticleColor;
                        break;
                }
            }

            int noteCount = Mathf.Clamp(Mathf.Max(amount, 1), 1, 10000);
            var bombParticle = new ParticleConfig(
                cfg.BombParticleCount, cfg.BombParticleScale,
                cfg.BombParticleLifetime, cfg.BombParticleSpeed);

            string text = string.Empty;
            int textNoteCount = 0;
            {
                // Optional: strip rich-text modifiers so viewers can't style text.
                var viewMessage = cfg.BombTextStripTags
                    ? NoteCosmeticController.StripRichTextTags(message)
                    : message;
                // Word limit applies to the viewer's message only, before the
                // username is appended, so the name line is never truncated.
                var clampedMessage = NoteCosmeticController.ClampMessageWords(viewMessage, cfg.MaxMessageWords);
                // If the message ends with a rich-text tag, the author controls
                // styling themselves (e.g. a trailing <size=0> to hide the
                // username) - don't wrap the name in our own size tag then.
                var trimmedMessage = clampedMessage.TrimEnd();
                var userControlsStyling = trimmedMessage.EndsWith(">", StringComparison.Ordinal);
                text = !string.IsNullOrEmpty(trimmedMessage)
                    ? $"{trimmedMessage}\n{(userControlsStyling ? string.Empty : "<size=70%>")}{user}{(userControlsStyling ? string.Empty : "</size>")}"
                    : user;
                textNoteCount = 1;
            }
            var textConfig = new TextConfig(cfg.BombTextSize / PluginConfig.FontPointsPerUnit, cfg.BombTextLifetime);

            var evt = new StreamEvent(StreamEventType.Bomb, bombColor, bombParticle, user, noteCount,
                rainbow: rainbow, hasBombVisual: cfg.BombCosmeticEnabled,
                text: text, textConfig: textConfig, textNoteCount: textNoteCount);
            NoteCosmeticController.QueueStreamEvent(evt);
        }
        else if (isSub)
        {
            int noteCount = Mathf.Max(amount, 1);
            var subParticle = new ParticleConfig(
                cfg.SubParticleCount, cfg.SubParticleScale,
                cfg.SubParticleLifetime, cfg.SubParticleSpeed);

            string subDisplay = user;
            if (isGift && amount > 0)
                subDisplay = $"{user}\n<size=70%>gifted {amount} subscription{(amount == 1 ? string.Empty : "s")}</size>";
            else if (amount > 0)
                subDisplay = $"{user}\n<size=70%>subscribed for {amount} month{(amount == 1 ? string.Empty : "s")}</size>";

            var evt = new StreamEvent(StreamEventType.Sub, resolvedColor, subParticle, user, noteCount,
                subDisplayText: subDisplay, subTextSize: cfg.SubTextSize / PluginConfig.FontPointsPerUnit,
                subTimerDuration: cfg.SubTimerDuration);
            NoteCosmeticController.QueueStreamEvent(evt);
        }
        else if (isRaid)
        {
            int raidNoteCount = Mathf.Clamp(Mathf.Max(Mathf.FloorToInt(amount * cfg.RaidMultiplier), 1), 1, 10000);
            var raidParticle = new ParticleConfig(
                cfg.RaidParticleCount, cfg.RaidParticleScale,
                cfg.RaidParticleLifetime, cfg.RaidParticleSpeed);

            string raidDisplay = string.Empty;
            if (!string.IsNullOrEmpty(user))
                raidDisplay = $"{user}\n<size=70%>{amount} viewers</size>";

            var evt = new StreamEvent(StreamEventType.Raid, resolvedColor, raidParticle, user, raidNoteCount,
                subDisplayText: raidDisplay, subTextSize: cfg.RaidTextSize / PluginConfig.FontPointsPerUnit, subTrails: false,
                subTimerDuration: cfg.RaidTimerDuration);
            NoteCosmeticController.QueueStreamEvent(evt);
        }
        else
        {
            int noteCount = Mathf.Max(amount, 1);
            ParticleConfig bitsParticle;
            StreamEventType evtType;
            if (string.Equals(type, "bits", StringComparison.OrdinalIgnoreCase))
            {
                evtType = StreamEventType.Bits;
                noteCount = Mathf.Max(Mathf.FloorToInt(amount / (float)GetBitTierBlockRatio(amount)), 1);
                bitsParticle = GetBitTierParticleConfig(amount);
            }
            else
            {
                evtType = StreamEventType.Other;
                bitsParticle = new ParticleConfig(
                    cfg.BitTierDefaultCount, cfg.BitTierDefaultScale,
                    cfg.BitTierDefaultLifetime, cfg.BitTierDefaultSpeed);
            }

            noteCount = Mathf.Clamp(noteCount, 1, 10000);

            string text = string.Empty;
            int textNoteCount = 0;
            if (!string.IsNullOrEmpty(user))
            {
                text = user;
                textNoteCount = cfg.TextSpawnEveryEvent ? noteCount : 1;
            }
            var textConfig = new TextConfig(cfg.BitsTextSize / PluginConfig.FontPointsPerUnit, cfg.BitsTextLifetime);

            var evt = new StreamEvent(evtType, resolvedColor, bitsParticle, user, noteCount,
                text: text, textConfig: textConfig, textNoteCount: textNoteCount, amount: amount);
            NoteCosmeticController.QueueStreamEvent(evt);
        }
    }

    internal static int GetBitTierBlockRatio(int amount)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return 1;
        if (amount >= 10000) return cfg.BitTier10000BlockRatio;
        if (amount >= 5000) return cfg.BitTier5000BlockRatio;
        if (amount >= 1000) return cfg.BitTier1000BlockRatio;
        if (amount >= 100) return cfg.BitTier100BlockRatio;
        return cfg.BitTierDefaultBlockRatio;
    }

    internal static ParticleConfig GetBitTierParticleConfig(int amount)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null)
            return new ParticleConfig(10, 0.015f, 1.5f, 5f);
        if (amount >= 10000)
            return new ParticleConfig(cfg.BitTier10000Count, cfg.BitTier10000Scale, cfg.BitTier10000Lifetime, cfg.BitTier10000Speed);
        if (amount >= 5000)
            return new ParticleConfig(cfg.BitTier5000Count, cfg.BitTier5000Scale, cfg.BitTier5000Lifetime, cfg.BitTier5000Speed);
        if (amount >= 1000)
            return new ParticleConfig(cfg.BitTier1000Count, cfg.BitTier1000Scale, cfg.BitTier1000Lifetime, cfg.BitTier1000Speed);
        if (amount >= 100)
            return new ParticleConfig(cfg.BitTier100Count, cfg.BitTier100Scale, cfg.BitTier100Lifetime, cfg.BitTier100Speed);
        return new ParticleConfig(cfg.BitTierDefaultCount, cfg.BitTierDefaultScale, cfg.BitTierDefaultLifetime, cfg.BitTierDefaultSpeed);
    }

    internal void StartEmoteSystem()
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        StopEmoteSystem();

        var channel = cfg.TwitchChannelName?.Trim();
        if (string.IsNullOrEmpty(channel))
        {
            Log.Debug("Emote system skipped: no channel name configured.");
            return;
        }

        Log.Info($"Starting emote system for channel '{channel}'.");

        // The IRC reader always connects (it also feeds the Chat panel), but
        // the emote CATALOG is only worth downloading when at least one emote
        // effect is actually enabled. Otherwise we'd fetch 7TV/BTTV/FFZ lists
        // for nothing on every start.
        var wantsEmotes = cfg.EmoteThrowEnabled || cfg.EmoteRainEnabled;
        if (wantsEmotes)
            EmoteCache.Instance.LoadChannel(channel!);
        else
            Log.Info("Emote system: catalog load skipped (neither emote throw nor emote rain is enabled).");

        _chatReader = new TwitchChatReader(EmoteCache.Instance.EmotesDict);
        _chatReader.Connect(channel!);
    }

    internal void StopEmoteSystem()
    {
        if (_chatReader != null)
        {
            _chatReader.Dispose();
            _chatReader = null;
        }
    }

    internal void HandleEmoteDetected(string user, string[] emotes)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        // No emote effect is enabled, so there is nothing to do with the emote
        // and no reason to download/cache its texture. Skip before touching the
        // cache so emotes never get fetched for features that are off.
        if (!cfg.EmoteThrowEnabled && !cfg.EmoteRainEnabled)
            return;

        foreach (var emoteCode in emotes)
        {
            if (EmoteCache.Instance.TryGetTexture(emoteCode, out var tex))
            {
                FireEmoteEffects(user, emoteCode, tex, cfg);
            }
            else
            {
                // Texture still downloading — queue the effect for when it arrives.
                var capturedCode = emoteCode;
                EmoteCache.Instance.GetTextureAsync(capturedCode, (code, readyTex) =>
                {
                    var c = PluginConfig.Instance;
                    if (c != null)
                        FireEmoteEffects(user, code, readyTex, c);
                });
            }
        }
    }

    private void FireEmoteEffects(string user, string emoteCode, Texture2D tex, PluginConfig cfg)
    {
        // Protected map (Noodle/Vivify/WIP): suppress emote spawns (throws/rain),
        // matching the transient-event suppression. Emotes have no queue to hold.
        if (IsMapProtectionActive())
        {
            NoteCosmeticController.VerboseLog($"Emote suppressed by map protection: {emoteCode} from {user}");
            return;
        }

        if (cfg.EmoteThrowEnabled)
            ProjectileThrower.SpawnEmoteProjectile(user, emoteCode, tex);

        if (cfg.EmoteRainEnabled)
            SpawnEmoteRain(emoteCode, tex, cfg.EmoteRainIntensity);

        NoteCosmeticController.VerboseLog($"Emote '{emoteCode}' from '{user}' — dispatched.");
    }

    private void SpawnEmoteRain(string emoteCode, Texture2D tex, int count)
    {
        RuntimeHooks.RunCoroutine(EmoteRainCoroutine(emoteCode, tex, count));
    }

    private static System.Collections.IEnumerator EmoteRainCoroutine(string emoteCode, Texture2D tex, int count)
    {
        var aspect = (float)tex.width / Mathf.Max(1f, tex.height);
        var rainSize = PluginConfig.Instance?.EmoteRainSize ?? 0.4f;
        var worldWidth = rainSize * aspect;
        var worldHeight = rainSize;

        var zones = GetEnabledRainZones();
        if (zones.Count == 0)
            zones.Add("AroundPlayer");

        for (var i = 0; i < count; i++)
        {
            var go = new GameObject("StreamReactiveEmoteRain");

            var area = zones[UnityEngine.Random.Range(0, zones.Count)];
            go.transform.position = RainSpawnPoint(area);
            go.transform.localScale = new Vector3(worldWidth, worldHeight, 1f);
            go.transform.rotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-15f, 15f));

            // Standalone emote (follows this rain node; avoids the non-uniform parent
            // scale that would shear a billboarded child).
            var set = EmoteVisualFactory.CreateEmoteVisuals(go.transform, tex, rainSize);
            var hudEntry = set.EmoteObject!.AddComponent<EmoteHudEntry>();
            hudEntry.Init(set, tex, emoteCode, go.transform, rainSize);

            // Attach frame animator for animated emotes
            if (EmoteCache.Instance.TryGetAnimatedFrames(emoteCode, out var frames, out var delay))
            {
                var animator = go.AddComponent<EmoteAnimator>();
                animator.Initialize(frames, delay, hudEntry);
            }

            go.AddComponent<EmoteRainDrop>();

            if (i < count - 1)
                yield return new WaitForSeconds(0.08f);
        }
    }

    /// <summary>
    /// Returns the currently enabled rain spawn zones. Zones are anchored to the
    /// player (not the camera), so emotes always fall from fixed world positions
    /// regardless of where you look.
    /// </summary>
    internal static List<string> GetEnabledRainZones()
    {
        var cfg = PluginConfig.Instance;
        var zones = new List<string>();
        if (cfg == null) { zones.Add("AroundPlayer"); return zones; }
        if (cfg.RainZoneAroundPlayer) zones.Add("AroundPlayer");
        if (cfg.RainZoneFrontCenter) zones.Add("FrontCenter");
        if (cfg.RainZoneFrontLeft) zones.Add("FrontLeft");
        if (cfg.RainZoneFrontRight) zones.Add("FrontRight");
        return zones;
    }

    // Cached player rig so spawn math doesn't follow the head/camera.
    private static Transform? _playerRig;

    private static bool TryGetPlayerFrame(out Vector3 position, out Vector3 forward)
    {
        if (_playerRig == null)
        {
            var rig = GameObject.Find("LocalPlayer");
            _playerRig = rig != null ? rig.transform : null;
        }

        Vector3 pos;
        Vector3 fwd;
        if (_playerRig != null)
        {
            pos = _playerRig.position;
            fwd = _playerRig.forward;
        }
        else
        {
            var cam = CapsuleGuardController.GetViewCamera();
            if (cam == null) { position = Vector3.zero; forward = Vector3.forward; return false; }
            pos = cam.transform.position;
            fwd = cam.transform.forward;
        }

        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f)
            fwd = Vector3.forward;
        forward = fwd.normalized;
        position = pos;
        return true;
    }

    // Static world anchor for the "front" zones: the platform center and the
    // room's fixed forward (toward the player's front). These never follow the
    // player or the camera, so front-zone rain always falls from the same world
    // spots. +Z is the player's front in this build (flipped from -Z).
    private static readonly Vector3 WorldForward = new Vector3(0f, 0f, 1f);
    private static Vector3 WorldAnchor() => Vector3.zero;
    private static Vector3 WorldRight() => Vector3.Cross(WorldForward, Vector3.up).normalized;

    // Anchor (no jitter) for a spawn zone, in world space. Front zones are
    // world-anchored; Around follows the player (attached).
    internal static Vector3 RainSpawnAnchor(string area)
    {
        var up = Vector3.up;
        float dist = 5.5f;   // distance in front of the platform
        float side = 2.5f;   // left/right offset for front zones
        float height = 7f;    // spawn height above the platform

        switch (area)
        {
            case "FrontCenter":
                return WorldAnchor() + WorldForward * dist + up * height;
            case "FrontLeft":
                return WorldAnchor() + WorldRight() * side + WorldForward * dist + up * height;
            case "FrontRight":
                return WorldAnchor() - WorldRight() * side + WorldForward * dist + up * height;
            case "AroundPlayer":
            default:
                return PlayerAnchor() + up * 1.5f;
        }
    }

    private static Vector3 PlayerAnchor()
    {
        if (TryGetPlayerFrame(out var p, out _))
            return p;
        return Vector3.zero;
    }

    private static Vector3 RainSpawnPoint(string area)
    {
        var basePos = RainSpawnAnchor(area);
        if (area == "AroundPlayer")
            return basePos + new Vector3(
                UnityEngine.Random.Range(-3f, 3f),
                UnityEngine.Random.Range(-1f, 2f),
                UnityEngine.Random.Range(-3f, 3f));
        return basePos + new Vector3(
            UnityEngine.Random.Range(-0.8f, 0.8f),
            UnityEngine.Random.Range(-0.4f, 0.6f),
            UnityEngine.Random.Range(-0.8f, 0.8f));
    }
}

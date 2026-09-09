using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamReactive;

internal readonly struct ParticleConfig
{
    public readonly int Count;
    public readonly float Scale;
    public readonly float Lifetime;
    public readonly float Speed;
    public ParticleConfig(int count, float scale, float lifetime, float speed)
    {
        Count = count;
        Scale = scale;
        Lifetime = lifetime;
        Speed = speed;
    }
}

internal readonly struct TextConfig
{
    public readonly float Size;
    public readonly float Lifetime;
    public TextConfig(float size, float lifetime)
    {
        Size = size;
        Lifetime = lifetime;
    }
}

internal enum StreamEventType
{
    Bomb,
    Bits,
    Sub,
    Raid,
    Other
}

internal sealed class StreamEvent
{
    public readonly StreamEventType Type;
    public readonly Color Color;
    public readonly ParticleConfig ParticleConfig;
    public readonly bool Rainbow;
    public readonly string User;
    public readonly string Text;
    public readonly TextConfig TextConfig;
    public readonly int TextNoteCount;
    public readonly string SubDisplayText;
    public readonly float SubTextSize;
    public readonly bool SubTrails;
    public readonly float SubTimerDuration;
    public readonly bool HasBombVisual;
    public readonly int Amount;

    public int NotesRemaining;
    public int TextRemaining;
    public int BombVisualsRemaining;
    public readonly int BombVisualsTotal;
    public float SustainEndTime;

    public readonly HashSet<NoteController> NotesInFlight = new();
    public readonly Dictionary<NoteController, (Color Color, ParticleConfig Config, bool Rainbow)> NoteParticles = new();
    public readonly Dictionary<NoteController, string> NoteTexts = new();

    public StreamEvent(StreamEventType type, Color color, ParticleConfig particleConfig, string user,
        int noteCount, bool rainbow = false, bool hasBombVisual = false,
        string text = "", TextConfig textConfig = default, int textNoteCount = 0,
        string subDisplayText = "", float subTextSize = 2.5f, bool subTrails = true,
        float subTimerDuration = 10f, int amount = 0)
    {
        Type = type;
        Color = color;
        ParticleConfig = particleConfig;
        User = user ?? string.Empty;
        Rainbow = rainbow;
        HasBombVisual = hasBombVisual;
        Amount = amount;
        NotesRemaining = noteCount;
        Text = text ?? string.Empty;
        TextConfig = textConfig;
        TextNoteCount = textNoteCount;
        TextRemaining = textNoteCount;
        SubDisplayText = subDisplayText ?? string.Empty;
        SubTextSize = subTextSize;
        SubTrails = subTrails;
        SubTimerDuration = subTimerDuration;
        BombVisualsRemaining = hasBombVisual ? noteCount : 0;
        BombVisualsTotal = hasBombVisual ? noteCount : 0;
    }

    public bool IsBomb => Type == StreamEventType.Bomb;

    public bool IsComplete
    {
        get
        {
            if (NotesRemaining > 0 || NotesInFlight.Count > 0) return false;
            if (TextRemaining > 0) return false;
            if (BombVisualsRemaining > 0) return false;
            if (SustainEndTime > 0f) return false;
            return true;
        }
    }
}

internal static class NoteCosmeticController
{
    private static readonly List<StreamEvent> _eventQueue = new();
    private static readonly List<StreamEvent> _activeEvents = new();

    private static readonly HashSet<NoteController> _bombedNotes = new();
    private static readonly HashSet<NoteController> _cutBombNotes = new();
    private static readonly Dictionary<NoteController, List<GameObject>> _disabledByBomb = new();
    // The exact MeshRenderer bombed for a note. RestoreNoteVisuals needs the
    // SAME renderer that ApplyBombVisual swapped (keyed by its instance id in
    // _originalNoteMeshes/_originalNoteMaterials), but FindNoteBlockTransform
    // re-derives a candidate at restore time and scores differently once the
    // note carries the low-vert Bomb mesh - so it can pick a different child
    // (e.g. NoteArrow) whose id is never in the originals dicts. That silently
    // skips the restore and leaves the note stuck looking like a bomb forever.
    private static readonly Dictionary<NoteController, MeshRenderer> _bombedRenderersByNote = new();
    private static readonly Dictionary<NoteController, GameObject> _activeOutlineParticles = new();
    private static readonly Dictionary<NoteController, GameObject> _activeSubTrails = new();
    private static readonly List<GameObject> _activeSubDisplays = new();
    private static readonly List<GameObject> _activeFloatingTexts = new();

    private static float _subSustainEndTime;
    private static Color _subSustainColor;
    private static ParticleConfig _subSustainConfig;
    private static bool _subSustainTrailEnabled;
    private static StreamEvent? _sustainOwner;

    // The sub/raid display survives scene changes: when the scene is torn down the
    // display GameObject dies with it, so we remember what to re-create when the next
    // game scene loads (for the remaining sustain time).
    private static string _subDisplayText = string.Empty;
    private static Color _subDisplayColor;
    private static float _subDisplaySize;
    private static bool _subDisplayActive;

    // The sustain window pauses while outside the game scene (menu/restart), so the
    // sub/raid timer keeps its remaining time instead of counting down in the menu.
    private static bool _subSustainPaused;
    private static float _subSustainRemaining;

    internal static MethodBase? FindInheritedMethod(Type type, string name, Type[]? parameterTypes = null)
    {
        var t = type;
        while (t != null)
        {
            var m = parameterTypes == null
                ? t.GetMethod(name, BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic)
                : t.GetMethod(name, BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic, null, parameterTypes, null);
            if (m != null) return m;
            t = t.BaseType;
        }
        return null;
    }

    internal static void ClearQueues()
    {
        ResetAll();
    }

    internal static void SkipCurrentEvent()
    {
        StreamEvent? target;
        lock (_eventQueue)
        {
            if (_activeEvents.Count > 0)
                target = _activeEvents[0];
            else if (_eventQueue.Count > 0)
            {
                target = _eventQueue[0];
                _eventQueue.RemoveAt(0);
            }
            else
                return;
        }

        Plugin.Log.Debug($"SkipCurrentEvent: skipping {target.Type} user={target.User}");

        // Restore/destroy the visuals this event still owns.
        foreach (var note in target.NotesInFlight)
        {
            if (note == null) continue;
            DestroyNoteOutline(note);
            DestroySubTrail(note);
            if (_bombedNotes.Contains(note))
            {
                RestoreNoteVisuals(note);
                _bombedNotes.Remove(note);

            }
        }

        if (_sustainOwner == target)
        {
            DestroyAllSubDisplayGameObjects();
            _sustainOwner = null;
            _subSustainEndTime = 0f;
            _subSustainTrailEnabled = false;
            _subDisplayActive = false;
            _subDisplayText = string.Empty;
            _subSustainPaused = false;
            _subSustainRemaining = 0f;
        }

        lock (_eventQueue)
        {
            _activeEvents.Remove(target);
            target.NotesInFlight.Clear();
            target.NoteParticles.Clear();
            target.NoteTexts.Clear();
            target.NotesRemaining = 0;
            target.TextRemaining = 0;
            target.BombVisualsRemaining = 0;
            target.SustainEndTime = 0f;
        }
    }

    internal static void VerboseLog(string message)
    {
        if (PluginConfig.Instance?.VerboseLogging == true)
            Plugin.Log.Debug(message);
    }

    internal static void OnGameSceneLoaded()
    {
        // Scene torn down: drop scene-bound visuals, keep the effect state so it
        // continues in this new song instead of restarting.
        CleanupSceneBoundVisuals();

        // Resume a paused sustain window: re-arm its end time for the remaining
        // duration so the timer continues counting where it left off.
        if (_subSustainPaused && _subSustainRemaining > 0f)
        {
            _subSustainEndTime = Time.time + _subSustainRemaining;
            _subSustainPaused = false;
            _subSustainRemaining = 0f;
            Plugin.Log.Debug($"OnGameSceneLoaded: resumed sustain window ({_subSustainEndTime - Time.time:F2}s remaining)");
        }

        ResumeSubDisplay();
    }

    internal static void OnMenuSceneLoaded()
    {
        CleanupSceneBoundVisuals();
        Prewarm();
    }

    /// <summary>
    /// Resolves the once-per-process lazily-built assets (bomb deps, display
    /// deps, sub-trail deps, particle prefab, event sounds) during non-critical
    /// frames at scene load, so the first event of any type never pays for the
    /// expensive first-use work (scene-wide FindObjectsOfTypeAll scans, mesh
    /// builds, Shader.Find, OGG download+decode) on a live gameplay frame.
    /// Same first-use-hitch class of bug as the projectile prefab scan.
    /// </summary>
    internal static void Prewarm()
    {
        RuntimeHooks.RunCoroutine(PrewarmCoroutine());
    }

    private static System.Collections.IEnumerator PrewarmCoroutine()
    {
        yield return null;

        // Event sounds: kick off async OGG loads (cached for the session) so
        // the clip decode never lands on the frame a bits/sub/raid starts.
        try { SoundManager.Prewarm(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Sound prewarm failed: {ex.Message}"); }

        // Sub/raid display + sustain-window deps.
        try { EnsureDisplayPositionCache(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Display position prewarm failed: {ex.Message}"); }
        try { GetQuadMesh(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Quad mesh prewarm failed: {ex.Message}"); }
        try { GetOrCreateTrailMaterial(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Trail material prewarm failed: {ex.Message}"); }

        // Note outline / bomb deps (formerly the only prewarmed caches).
        try { EnsureRoundedCubeMesh(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Rounded-cube mesh prewarm failed: {ex.Message}"); }
        try { EnsureGlowMaterial(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Bomb prewarm glow material failed: {ex.Message}"); }
        try { EnsureBombMeshCache(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Bomb prewarm mesh cache failed: {ex.Message}"); }

        // Particle prefab for cut bursts.
        try { ParticleSpawner.PrewarmPrefab(); }
        catch (System.Exception ex) { Plugin.Log.Debug($"Particle prefab prewarm failed: {ex.Message}"); }
    }

    private static void ResetAll()
    {
        lock (_eventQueue)
        {
            _eventQueue.Clear();
        }
        _activeEvents.Clear();
        RestoreAllDisabledByBomb();
        _bombedNotes.Clear();
        _cutBombNotes.Clear();
        _bombedRenderersByNote.Clear();
        _sustainOwner = null;
        _subSustainEndTime = 0f;
        _subSustainConfig = default;
        _subSustainTrailEnabled = false;
        _subDisplayActive = false;
        _subDisplayText = string.Empty;
        _subSustainPaused = false;
        _subSustainRemaining = 0f;
        _originalNoteMeshes.Clear();
        _originalNoteMaterials.Clear();
        _originalNoteRendererEnabled.Clear();
        _originalNoteProperties.Clear();
        _bombMeshCache = null;
        _bombMaterialCache = null;
        _cachedEnergyPanel = null;
        DestroyAllOutlineParticles();
        DestroyAllSubTrails();
        DestroyAllSubDisplayGameObjects();
        DestroyAllFloatingTexts();
    }

    // Called on scene changes (menu and game load). Destroys every scene-bound visual
    // and drops references to dead note/asset objects, but PRESERVES the event queues,
    // active events, and the sustain window so effects survive going to the menu or
    // restarting the song.
    private static void CleanupSceneBoundVisuals()
    {
        VerboseLog("CleanupSceneBoundVisuals: destroying scene-bound visuals, preserving event state");

        DestroyAllOutlineParticles();
        DestroyAllSubTrails();
        DestroyAllSubDisplayGameObjects();
        DestroyAllFloatingTexts();

        RestoreAllDisabledByBomb();
        _bombedNotes.Clear();
        _cutBombNotes.Clear();
        _bombedRenderersByNote.Clear();
        _originalNoteMeshes.Clear();
        _originalNoteMaterials.Clear();
        _originalNoteRendererEnabled.Clear();
        _originalNoteProperties.Clear();
        // Bomb mesh/material can reference the previous scene's AssetBundles
        // (e.g. Vivify), so re-cache them per scene instead of holding a dead
        // reference. The glow material is safe core content and stays.
        _bombMeshCache = null;
        _bombMaterialCache = null;
        _cachedEnergyPanel = null;

        lock (_eventQueue)
        {
            foreach (var evt in _activeEvents)
            {
                // Note-bound claims died with the scene: give the counts back so the
                // event finishes in the next song instead of silently losing them.
                evt.NotesRemaining += evt.NoteParticles.Count;
                evt.NoteParticles.Clear();
                evt.TextRemaining += evt.NoteTexts.Count;
                evt.NoteTexts.Clear();
                if (evt.HasBombVisual)
                    evt.BombVisualsRemaining = evt.BombVisualsTotal;
                evt.NotesInFlight.Clear();
                evt.NotesInFlight.Clear();
            }
        }

        // Sustain window handling on scene change: if there's still time left, PAUSE it
        // so the timer doesn't count down in the menu; if it expired while we were in
        // the scene, close it out (the display coroutine that normally does this died
        // with the scene).
        if (_subSustainEndTime > 0f)
        {
            var remaining = _subSustainEndTime - Time.time;
            if (remaining <= 0f)
            {
                _subSustainEndTime = 0f;
                _subSustainTrailEnabled = false;
                _subDisplayActive = false;
                _subDisplayText = string.Empty;
                if (_sustainOwner != null)
                    _sustainOwner.SustainEndTime = 0f;
                _sustainOwner = null;
            }
            else
            {
                _subSustainRemaining = remaining;
                _subSustainPaused = true;
                _subSustainEndTime = 0f;
                Plugin.Log.Debug($"CleanupSceneBoundVisuals: paused sustain window ({remaining:F2}s remaining)");
            }
        }
    }

    internal static void QueueStreamEvent(StreamEvent evt)
    {
        if (evt == null) return;
        lock (_eventQueue)
        {
            _eventQueue.Add(evt);
        }
        Plugin.Log.Debug($"QueueStreamEvent: queued {evt.Type} user={evt.User} notes={evt.NotesRemaining} text={evt.TextRemaining} bombVisual={evt.BombVisualsRemaining} display={evt.SubDisplayText}");
    }

    internal static void ProcessPendingStreamEvents()
    {
        lock (_eventQueue)
        {
            // Expire the sustain window the moment it runs out, independent of scene
            // (runs every frame; while paused _subSustainEndTime is 0 so it no-ops).
            if (_subSustainEndTime > 0f && Time.time >= _subSustainEndTime)
            {
                _subSustainEndTime = 0f;
                _subSustainTrailEnabled = false;
                _subDisplayActive = false;
                _subDisplayText = string.Empty;
                if (_sustainOwner != null)
                    _sustainOwner.SustainEndTime = 0f;
                _sustainOwner = null;
            }

            // Finalize completed events first, so events that finished (e.g. their
            // sustain window expired while in the menu) free up the queue immediately.
            for (int i = _activeEvents.Count - 1; i >= 0; i--)
            {
                if (_activeEvents[i].IsComplete)
                {
                    FinalizeEvent(_activeEvents[i]);
                    _activeEvents.RemoveAt(i);
                }
            }

            // Idle fast-path: nothing active and nothing queued, so there is
            // nothing to do. This is the common case every frame.
            if (_activeEvents.Count == 0 && _eventQueue.Count == 0)
                return;

            // Don't start anything while in the menu: displays would pop up there and
            // sustain windows would tick away before the next song. Events stay queued
            // and resume when the game scene loads.
            if (!Plugin.IsInGame)
                return;

            // Paused (General settings "Paused" / websocket pause) or a protected
            // map (Noodle/Vivify/WIP): the mod is still enabled and events keep
            // landing in _eventQueue, but nothing moves to _activeEvents until
            // unpaused / the protected map ends. They resume in order later.
            if (PluginConfig.Instance?.Paused == true || Plugin.IsMapProtectionActive())
                return;

            bool anyNonBomb = false, anyBomb = false;
            foreach (var evt in _activeEvents)
            {
                if (evt.IsBomb) anyBomb = true;
                else anyNonBomb = true;
            }

            // A running bits/sub/raid fully blocks everything until exhausted.
            if (anyNonBomb)
                return;

            if (anyBomb)
            {
                // Bombs coexist with other bombs: queued bombs may join, even
                // skipping over non-bomb events that arrived in between.
                for (int i = 0; i < _eventQueue.Count; i++)
                {
                    var evt = _eventQueue[i];
                    if (!evt.IsBomb) continue;
                    _eventQueue.RemoveAt(i);
                    i--;
                    _activeEvents.Add(evt);
                    StartEvent(evt);
                    if (evt.IsComplete)
                    {
                        FinalizeEvent(evt);
                        _activeEvents.Remove(evt);
                    }
                }
                return;
            }

            // Nothing active: start the next queued event in order.
            while (_eventQueue.Count > 0)
            {
                var evt = _eventQueue[0];
                _eventQueue.RemoveAt(0);
                _activeEvents.Add(evt);
                StartEvent(evt);
                if (evt.IsComplete)
                {
                    FinalizeEvent(evt);
                    _activeEvents.Remove(evt);
                    continue;
                }
                break;
            }
        }
    }

    private static void StartEvent(StreamEvent evt)
    {
        // Event sound first: plays when the event actually starts (even if the
        // event was queued while in the menu and only starts on the next map).
        // Bomb sounds play on cut instead (ProcessNoteAtCut), so they fire here
        // only once the player actually slices a bombed note.
        SoundManager.PlayEventSound(evt);

        // Subs and raids always start a sustain window (even without display text)
        // so their effects last at least the configured timer duration.
        if (evt.Type is StreamEventType.Sub or StreamEventType.Raid || !string.IsNullOrEmpty(evt.SubDisplayText))
            SpawnSubDisplay(evt.SubDisplayText, evt.Color, evt.SubTextSize, evt.SubTrails, evt);
        VerboseLog($"StartEvent: {evt.Type} user={evt.User} notes={evt.NotesRemaining} bombVisual={evt.BombVisualsRemaining} bombTotal={evt.BombVisualsTotal} display={evt.SubDisplayText}");
        Plugin.Log.Debug($"StartEvent: {evt.Type} user={evt.User} notes={evt.NotesRemaining} bombVisual={evt.BombVisualsRemaining} display={evt.SubDisplayText}");
    }

    private static void FinalizeEvent(StreamEvent evt)
    {
        Plugin.Log.Debug($"FinalizeEvent: {evt.Type} user={evt.User}");
        if (_sustainOwner == evt)
        {
            _sustainOwner = null;
            _subSustainEndTime = 0f;
            _subSustainTrailEnabled = false;
            _subDisplayActive = false;
            _subDisplayText = string.Empty;
            _subSustainPaused = false;
            _subSustainRemaining = 0f;
        }
    }

    internal static void ProcessNoteAtInit(NoteController note)
    {
        if (note == null || note.noteData == null)
            return;

        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        bool skipBombNote = note is BombNoteController && !cfg.IncludeBombNotes;

        // Assign the note to the active event that needs it (bomb visual, particles, text).
        if (!skipBombNote)
            TryAssignNoteToEvent(note);

        // During sub sustain window, apply trail and outline to every note so the
        // effect stays visible for the whole window, not just the notes the event
        // explicitly claimed.
        if (!skipBombNote && Time.time < _subSustainEndTime)
        {
            bool trailsActive = cfg.SubTrailEnabled && _subSustainTrailEnabled;
            if (!_activeSubTrails.ContainsKey(note) && trailsActive)
                SpawnSubTrail(note, _subSustainColor);
            if (!_activeOutlineParticles.ContainsKey(note))
                SpawnNoteOutline(note, _subSustainColor);
        }

        // Subscribe to the note's cut event set. The game raises cuts via
        // NoteController.SendNoteWasCutEvent (which iterates this set), and replay
        // systems call the set's subscribers directly (BeatLeader) or call
        // SendNoteWasCutEvent (ScoreSaber), so this covers every cut path exactly once.
        try
        {
            var cutEvents = note.noteWasCutEvent;
            if (cutEvents != null)
                cutEvents.Add(NoteCutForwarder.Instance);
        }
        catch (Exception e)
        {
            VerboseLog($"ProcessNoteAtInit: failed to subscribe to noteWasCutEvent: {e}");
        }

    }

    private static StreamEvent? FindOwningEvent(NoteController note)
    {
        foreach (var evt in _activeEvents)
        {
            if (evt.NoteParticles.ContainsKey(note) || evt.NoteTexts.ContainsKey(note)
                    || evt.NotesInFlight.Contains(note))
                return evt;
        }
        return null;
    }

    private static void TryAssignNoteToEvent(NoteController note)
    {
        if (FindOwningEvent(note) != null)
            return;

        bool isRealBomb = note is BombNoteController;

        // 1. Bomb visual claim takes priority: an active bomb event turns this
        //    note into a bomb (it also consumes one particle burst + text).
        //    Real bomb notes can never be transformed again.
        if (!isRealBomb)
        {
            foreach (var evt in _activeEvents)
            {
                if (!evt.HasBombVisual || evt.BombVisualsRemaining <= 0)
                    continue;

                VerboseLog($"TryAssignNoteToEvent: BOMB claim note={note.GetInstanceID()}, BombVisualsRemaining={evt.BombVisualsRemaining}, NotesRemaining={evt.NotesRemaining}");
                evt.BombVisualsRemaining--;
                if (evt.NotesRemaining > 0) evt.NotesRemaining--;
                _bombedNotes.Add(note);
                RuntimeHooks.RunCoroutine(ApplyBombVisualDeferred(note, evt.Color, evt.Rainbow));
                evt.NotesInFlight.Add(note);

                if (evt.NoteParticles != null)
                    evt.NoteParticles[note] = (evt.Color, evt.ParticleConfig, evt.Rainbow);
                if (evt.TextRemaining > 0)
                {
                    evt.TextRemaining--;
                    evt.NoteTexts[note] = evt.Text;
                }
                return;
            }
        }

        // 2. Particle/text claim from the first active event that still needs notes.
        foreach (var evt in _activeEvents)
        {
            if (evt.NotesRemaining <= 0)
                continue;

            VerboseLog($"TryAssignNoteToEvent: PARTICLE claim note={note.GetInstanceID()}, NotesRemaining={evt.NotesRemaining}, IsBomb={evt.IsBomb}");
            evt.NotesRemaining--;
            evt.NoteParticles[note] = (evt.Color, evt.ParticleConfig, evt.Rainbow);
            evt.NotesInFlight.Add(note);

            SpawnNoteOutline(note, evt.Color);

            if (evt.TextRemaining > 0)
            {
                evt.TextRemaining--;
                evt.NoteTexts[note] = evt.Text;
            }
            return;
        }
    }

    internal static void RestoreNoteIfNeeded(NoteController note)
    {
        if (note == null)
            return;

        var evt = FindOwningEvent(note);
        if (evt != null)
        {
            if (evt.NoteParticles.TryGetValue(note, out _))
            {
                evt.NoteParticles.Remove(note);
                evt.NotesRemaining++;
            }
            if (evt.NoteTexts.TryGetValue(note, out _))
            {
                evt.NoteTexts.Remove(note);
                evt.TextRemaining++;
            }
            // If a bombed note is recycled without being cut (missed),
            // give back the bomb visual so it can re-claim on the next note.
            if (_bombedNotes.Contains(note) && !_cutBombNotes.Contains(note))
            {
                VerboseLog($"RestoreNoteIfNeeded: giving back bomb visual, BombVisualsRemaining={evt.BombVisualsRemaining}->{evt.BombVisualsRemaining + 1}");
                evt.BombVisualsRemaining++;
            }
            else if (_bombedNotes.Contains(note) && _cutBombNotes.Contains(note))
            {
                VerboseLog($"RestoreNoteIfNeeded: cut bomb note, NOT giving back bomb visual");
            }
            evt.NotesInFlight.Remove(note);
        }

        if (_bombedNotes.Contains(note))
        {
            VerboseLog($"RestoreNoteIfNeeded: Restoring bombed note {note.GetInstanceID()}");
            RestoreNoteVisuals(note);
            _bombedNotes.Remove(note);
            // Cut status is per note LIFE, not per note instance: notes are
            // pooled, so without this a note that was cut-as-bomb once would
            // keep _cutBombNotes forever, and a later life in which it is
            // re-bombed then MISSED would never refund BombVisualsRemaining —
            // that bomb would silently vanish.
            _cutBombNotes.Remove(note);
        }

        DestroyNoteOutline(note);
        DestroySubTrail(note);
    }

    internal static void ProcessNoteAtCut(NoteController note)
    {
        if (note == null || note.noteData == null)
            return;

        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        bool skipBombNote = note is BombNoteController && !cfg.IncludeBombNotes;

        // Notes spawned before the sub started have no trail but still get cut during
        // the sustain window; bursting on them makes subscriber particles appear
        // before the first trail note reaches the player. Only burst on trail notes.
        bool hadSustainTrail = _activeSubTrails.ContainsKey(note);

        DestroyNoteOutline(note);
        DestroySubTrail(note);

        var evt = FindOwningEvent(note);
        if (evt != null)
        {
            if (evt.NoteParticles.TryGetValue(note, out var entry))
            {
                evt.NoteParticles.Remove(note);
                SpawnParticlesOnNote(note, entry.Color, entry.Config, entry.Rainbow);
                // Always-on trace (not gated by VerboseLogging): proves the cut
                // reached the particle spawner, so a missing explosion can be
                // separated from a missed cut event when diagnosing.
                Plugin.Log.Debug($"ProcessNoteAtCut: {evt.Type} note {note.GetInstanceID()} cut -> burst count={entry.Config.Count} at {note.transform.position}");
            }
            if (evt.NoteTexts.TryGetValue(note, out var text))
            {
                evt.NoteTexts.Remove(note);
                SpawnTextAtPosition(note.transform.position, text, evt.Color, evt.TextConfig);
            }
            // Mark cut bomb notes so RestoreNoteIfNeeded won't give back the
            // bomb visual (the user hit it — it's consumed).
            if (_bombedNotes.Contains(note))
            {
                VerboseLog($"ProcessNoteAtCut: note {note.GetInstanceID()} was bombed, adding to _cutBombNotes");
                _cutBombNotes.Add(note);
                // Bomb sound plays on the cut, when the cosmetic bomb actually
                // lands on the player's saber — not when the note spawned.
                SoundManager.Play(cfg.BombSoundFile, cfg.BombSoundVolume);
            }
            evt.NotesInFlight.Remove(note);
        }

        // Keep the note in _bombedNotes after a cut; RestoreNoteIfNeeded restores its
        // original mesh/material when the pooled note is re-initialized.

        // Burst particles only on notes that carry the sustain trail: the sub effect
        // (and its particles) should start when the first trail note is hit, not on
        // unrelated notes that were already in flight when the sub triggered.
        if (!skipBombNote && hadSustainTrail && Time.time < _subSustainEndTime && _subSustainConfig.Count > 0)
            SpawnParticlesOnNote(note, _subSustainColor, _subSustainConfig);
    }

    private static void SpawnNoteOutline(NoteController note, Color color)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null || !cfg.NoteOutlineEnabled)
            return;

        var go = new GameObject("StreamReactiveParticleOutline");
        var noteCube = FindNoteBlockTransform(note);
        go.transform.SetParent(noteCube ?? note.transform, false);
        go.transform.localPosition = Vector3.zero;

        var ps = go.AddComponent<ParticleSystem>();

        // Outline visibility knobs: the size slider scales the particle dots,
        // density controls how packed the outline is, and opacity dims the
        // whole effect (0 = invisible, 1 = full alpha as configured above).
        var sizeMult = Mathf.Clamp(cfg.NoteOutlineSize, 0.001f, 0.1f) / 0.02f;
        var density = Mathf.Clamp(cfg.NoteOutlineDensity, 10, 2000);
        var opacity = Mathf.Clamp01(cfg.NoteOutlineOpacity);
        color.a *= opacity;

        var main = ps.main;
        main.loop = true;
        main.prewarm = true;
        main.playOnAwake = true;
        main.startLifetime = 2f;
        main.startSpeed = 0.02f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.012f * sizeMult, 0.03f * sizeMult);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = color;
        main.maxParticles = Mathf.Max(50, density * 3);
        main.simulationSpace = ParticleSystemSimulationSpace.Local;

        var emission = ps.emission;
        emission.rateOverTime = density;

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = new ParticleSystem.MinMaxCurve(0.015f, 0.04f);
        noise.frequency = 0.6f;
        noise.scrollSpeed = 1f;
        noise.damping = true;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        var pulse = new AnimationCurve();
        pulse.AddKey(0f, 0.3f);
        pulse.AddKey(0.15f, 1f);
        pulse.AddKey(0.4f, 0.5f);
        pulse.AddKey(0.7f, 0.8f);
        pulse.AddKey(1f, 0f);
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, pulse);

        var shape = ps.shape;
        var outlineScale = Mathf.Clamp(cfg.NoteOutlineScale, 0.2f, 3f);
        EnsureRoundedCubeMesh();
        if (_roundedCubeMesh != null)
        {
            shape.shapeType = ParticleSystemShapeType.Mesh;
            shape.mesh = _roundedCubeMesh;
            shape.meshShapeType = ParticleSystemMeshShapeType.Vertex;
            shape.scale = Vector3.one * outlineScale;
        }
        else
        {
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = Vector3.one * 0.6f * outlineScale;
        }

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        if (ParticleSpawner.ParticleMaterial != null)
            renderer.sharedMaterial = ParticleSpawner.ParticleMaterial;

        ps.Play();

        _activeOutlineParticles[note] = go;
        VerboseLog($"SpawnNoteOutline: created outline on note {note.GetInstanceID()}");
    }

    private static void DestroyNoteOutline(NoteController note)
    {
        if (_activeOutlineParticles.TryGetValue(note, out var go))
        {
            _activeOutlineParticles.Remove(note);
            if (go != null)
            {
                var ps = go.GetComponent<ParticleSystem>();
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
            }
        }
    }

    private static void DestroyAllOutlineParticles()
    {
        foreach (var go in _activeOutlineParticles.Values)
        {
            if (go != null)
            {
                var ps = go.GetComponent<ParticleSystem>();
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
            }
        }
        _activeOutlineParticles.Clear();
    }

    private static Material? _trailMaterial;

    private static Material? GetOrCreateTrailMaterial()
    {
        if (_trailMaterial != null) return _trailMaterial;

        var mat = new Material(Shader.Find("Sprites/Default"));
        if (mat == null) return null;
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.One);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        mat.color = Color.white;
        _trailMaterial = mat;
        return mat;
    }

    private static void SpawnSubTrail(NoteController note, Color color)
    {
        var noteCube = FindNoteBlockTransform(note);
        if (noteCube == null)
        {
            VerboseLog($"SpawnSubTrail: no note block found on note {note.GetInstanceID()}, skipping");
            return;
        }

        // Multiple wiggly ribbon trails from behind the block
        int trailCount = 4;
        VerboseLog($"SpawnSubTrail: creating {trailCount} trails on note {note.GetInstanceID()}, pos={note.transform.position}");
        var root = new GameObject("StreamReactiveSubTrailRoot");
        root.transform.SetParent(noteCube, false);
        root.transform.localPosition = Vector3.zero;

        var trailTransforms = new Transform[trailCount];
        for (int i = 0; i < trailCount; i++)
        {
            var go = new GameObject($"StreamReactiveSubTrail_{i}");
            go.transform.SetParent(root.transform, false);
            // Offset slightly behind the note with random spread
            float xOff = (i - (trailCount - 1) * 0.5f) * 0.08f;
            float yOff = ((i % 2 == 0) ? 1 : -1) * 0.04f;
            go.transform.localPosition = new Vector3(xOff, yOff, 0.9f);

            var trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.6f;
            trail.startWidth = 0.05f;
            trail.endWidth = 0f;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(0.5f, 0f), new GradientAlphaKey(0.2f, 0.3f), new GradientAlphaKey(0f, 1f) }
            );
            trail.colorGradient = gradient;

            var mat = GetOrCreateTrailMaterial();
            if (mat != null)
                trail.sharedMaterial = mat;

            trailTransforms[i] = go.transform;
        }

        // Wiggle coroutine for organic flutter
        RuntimeHooks.RunCoroutine(AnimateTrailWiggle(trailTransforms, trailCount));

        _activeSubTrails[note] = root;
    }

    private static System.Collections.IEnumerator AnimateTrailWiggle(Transform[] children, int trailCount)
    {
        var t = 0f;
        while (true)
        {
            t += Time.deltaTime * 8f;
            bool anyAlive = false;
            for (int i = 0; i < trailCount; i++)
            {
                var child = children[i];
                if (child == null) continue;
                anyAlive = true;
                float phase = i * 1.3f;
                float wiggleX = Mathf.Sin(t + phase) * 0.12f;
                float wiggleY = Mathf.Cos(t * 0.7f + phase) * 0.08f;
                float xOff = (i - (trailCount - 1) * 0.5f) * 0.08f;
                float yOff = ((i % 2 == 0) ? 1 : -1) * 0.04f;
                child.localPosition = new Vector3(xOff + wiggleX, yOff + wiggleY, 0.9f);
            }
            if (!anyAlive) yield break;
            yield return null;
        }
    }

    private static void DestroySubTrail(NoteController note)
    {
        if (_activeSubTrails.TryGetValue(note, out var go))
        {
            VerboseLog($"DestroySubTrail: destroying trail on note {note.GetInstanceID()}");
            _activeSubTrails.Remove(note);
            if (go != null)
            {
                // Kill rendering NOW. UnityEngine.Object.Destroy is deferred to end of
                // frame, and the note is teleported to the pool and re-spawned within
                // that same frame; a still-live trail would record the teleport and
                // render a long streak from the pool up to the next spawn point.
                foreach (var tr in go.GetComponentsInChildren<TrailRenderer>(true))
                    tr.Clear();
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
            }
        }
    }

    private static void DestroyAllSubTrails()
    {
        int count = _activeSubTrails.Count;
        if (count > 0)
            VerboseLog($"DestroyAllSubTrails: destroying {count} trails");
        foreach (var go in _activeSubTrails.Values)
        {
            if (go != null)
            {
                foreach (var tr in go.GetComponentsInChildren<TrailRenderer>(true))
                    tr.Clear();
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
            }
        }
        _activeSubTrails.Clear();
    }

    // Missed notes are returned to the object pool (far below the play area) with
    // their outline/trail still attached, which makes trails draw a long streak from
    // below during replays. Destroy the visuals the moment a note is despawned.
    internal static void OnNoteDespawned(NoteController note)
    {
        if (note == null)
            return;

        VerboseLog($"OnNoteDespawned: cleaning up visuals on note {note.GetInstanceID()}, name={note.name}");
        DestroyNoteOutline(note);
        DestroySubTrail(note);
    }

    private static void SpawnSubDisplay(string text, Color color, float fontSize, bool trails, StreamEvent owner)
    {
        var cfg = PluginConfig.Instance;
        var duration = owner?.SubTimerDuration > 0f ? owner.SubTimerDuration : cfg?.SubTimerDuration ?? 10f;

        _subSustainEndTime = Time.time + duration;
        _subSustainColor = color;
        _subSustainTrailEnabled = trails;
        _subSustainConfig = owner?.ParticleConfig ?? default;
        _sustainOwner = owner;
        if (owner != null)
            owner.SustainEndTime = _subSustainEndTime;

        // Remember the display so it can be re-created after a scene change.
        _subDisplayActive = true;
        _subDisplayText = text ?? string.Empty;
        _subDisplayColor = color;
        _subDisplaySize = fontSize;

        // No display text: the sustain window still runs for the full duration.
        // Expiry is handled by the per-frame check in ProcessPendingStreamEvents.
        if (string.IsNullOrEmpty(text))
            return;

        CreateSubDisplay(text!, color, fontSize, owner, duration);
    }

    private static void CreateSubDisplay(string text, Color color, float fontSize, StreamEvent? owner, float duration)
    {
        var root = new GameObject("StreamReactiveSubDisplay");
        root.transform.position = FindDisplayPosition();

        var tmp = root.AddComponent<TextMeshPro>();
        tmp.text = ClampRichTextSizes(text, fontSize);
        tmp.fontSize = fontSize;
        tmp.color = new Color(color.r, color.g, color.b, 0f);
        tmp.alignment = TextAlignmentOptions.Center;

        // Scale the underline together with the text so it stays proportional to
        // the font size. 2.5 is the default sub text size (10 points / FontPointsPerUnit);
        // a larger/smaller configured size scales the bar the same way.
        var barScale = Mathf.Max(0.1f, fontSize / 2.5f);

        // Subtle underline timer near the text
        var barGO = new GameObject("StreamReactiveSubTimer");
        barGO.transform.SetParent(root.transform, false);
        barGO.transform.localPosition = new Vector3(0, -0.3f * barScale, 0);

        var barFilter = barGO.AddComponent<MeshFilter>();
        barFilter.sharedMesh = GetQuadMesh();
        var barRenderer = barGO.AddComponent<MeshRenderer>();
        var barMat = new Material(Shader.Find("Sprites/Default"));
        if (barMat != null)
        {
            barMat.mainTexture = Texture2D.whiteTexture;
            barMat.color = new Color(color.r, color.g, color.b, 0f);
            barRenderer.material = barMat;
        }
        barGO.transform.localScale = new Vector3(2.5f * barScale, 0.035f * barScale, 1f);

        _activeSubDisplays.Add(root);

        RuntimeHooks.RunCoroutine(AnimateSubDisplay(root, barGO, tmp, barMat!, color, duration, owner));
    }

    // Called on game scene load: re-creates the sub/raid display for the time that
    // remains in the sustain window, so the effect continues across scene changes.
    private static void ResumeSubDisplay()
    {
        if (!_subDisplayActive || _subSustainEndTime <= 0f)
            return;

        var remaining = _subSustainEndTime - Time.time;
        Plugin.Log.Debug($"ResumeSubDisplay: sustain remaining={remaining:F2}s (end={_subSustainEndTime:F2}, now={Time.time:F2})");
        if (remaining <= 0f)
            return;

        if (string.IsNullOrEmpty(_subDisplayText))
            return;

        VerboseLog($"ResumeSubDisplay: re-creating display for {remaining:F1}s more");
        CreateSubDisplay(_subDisplayText, _subDisplayColor, _subDisplaySize, _sustainOwner, remaining);
    }

    private static GameObject? _cachedEnergyPanel;

    // Scans for the EnergyPanel UI game object and caches the reference. The
    // scan (Resources.FindObjectsOfTypeAll of every GameObject in the scene) is
    // the expensive part; it runs once per scene during Prewarm, not on the
    // frame the first sub/raid display needs a position.
    private static void EnsureDisplayPositionCache()
    {
        if (_cachedEnergyPanel != null)
            return;
        _cachedEnergyPanel = Resources.FindObjectsOfTypeAll<GameObject>()
            .FirstOrDefault(g => g.name == "EnergyPanel");
    }

    private static Vector3 FindDisplayPosition()
    {
        EnsureDisplayPositionCache();
        if (_cachedEnergyPanel != null)
        {
            var ep = _cachedEnergyPanel.transform.position;
            // Center horizontally on the panel, on the same plane, slightly above it
            return new Vector3(ep.x, ep.y + 0.6f, ep.z - 0.08f);
        }

        var cam = Camera.main;
        if (cam != null)
            return cam.transform.position + cam.transform.forward * 2.5f + cam.transform.up * 0.5f;
        return new Vector3(0, 1.5f, 4f);
    }

    private static System.Collections.IEnumerator AnimateSubDisplay(GameObject root, GameObject bar, TextMeshPro tmp, Material barMat, Color color, float duration, StreamEvent? owner)
    {
        var elapsed = 0f;
        var startScale = bar.transform.localScale;
        var fadeInDuration = 0.3f;
        var fadeOutStart = Mathf.Max(fadeInDuration, duration - 0.5f);

        while (elapsed < duration)
        {
            if (root == null || tmp == null) yield break;

            elapsed += Time.deltaTime;

            // Alpha is recomputed every frame so the text is ALWAYS fully visible in the
            // middle of the window. A large first deltaTime (heavy scene-load frame) used
            // to skip the fade-in entirely, leaving alpha stuck at 0 until the fade-out.
            float alpha;
            if (elapsed >= fadeOutStart)
                alpha = Mathf.Lerp(1f, 0f, (elapsed - fadeOutStart) / 0.5f);
            else if (elapsed < fadeInDuration)
                alpha = elapsed / fadeInDuration;
            else
                alpha = 1f;

            alpha = Mathf.Clamp01(alpha);
            tmp.color = new Color(color.r, color.g, color.b, alpha);
            if (barMat != null)
                barMat.color = new Color(color.r, color.g, color.b, alpha * 0.7f);

            // Shrink the underline from both sides toward center
            var t = elapsed / duration;
            var scaleX = Mathf.Lerp(startScale.x, 0f, t);
            bar.transform.localScale = new Vector3(scaleX, startScale.y, startScale.z);

            yield return null;
        }

        _activeSubDisplays.Remove(root);
        if (root != null)
            UnityEngine.Object.Destroy(root);

        if (owner != null)
        {
            owner.SustainEndTime = 0f;
            if (_sustainOwner == owner)
            {
                _sustainOwner = null;
                _subSustainEndTime = 0f;
            }
        }
    }

    private static void DestroyAllSubDisplayGameObjects()
    {
        foreach (var go in _activeSubDisplays)
        {
            if (go != null)
                UnityEngine.Object.Destroy(go);
        }
        _activeSubDisplays.Clear();
    }

    private static void SpawnParticlesOnNote(NoteController note, Color color, ParticleConfig config, bool rainbow = false)
    {
        int particleCount = Mathf.Clamp(config.Count, 1, 50000);
        Vector3 position = note.transform.position;
        ParticleSpawner.SpawnParticles(position, particleCount, color, config.Scale, config.Lifetime, config.Speed, rainbow);
        VerboseLog($"SpawnParticlesOnNote: spawned {particleCount} particles on note {note.GetInstanceID()}");
    }

    private static Mesh? _quadMeshCache;
    private static Texture2D? _roundedRectTexture;

    private static Texture2D? GetRoundedRectTexture()
    {
        if (_roundedRectTexture != null) return _roundedRectTexture;

        var tex = new Texture2D(64, 16, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        int w = tex.width, h = tex.height;
        int r = 4;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool inside = true;
                if (x < r && y < r)
                    inside = (x - r) * (x - r) + (y - r) * (y - r) <= r * r;
                else if (x >= w - r && y < r)
                    inside = (x - (w - 1 - r)) * (x - (w - 1 - r)) + (y - r) * (y - r) <= r * r;
                else if (x < r && y >= h - r)
                    inside = (x - r) * (x - r) + (y - (h - 1 - r)) * (y - (h - 1 - r)) <= r * r;
                else if (x >= w - r && y >= h - r)
                    inside = (x - (w - 1 - r)) * (x - (w - 1 - r)) + (y - (h - 1 - r)) * (y - (h - 1 - r)) <= r * r;
                tex.SetPixel(x, y, inside ? Color.white : Color.clear);
            }
        }
        tex.Apply();
        _roundedRectTexture = tex;
        return tex;
    }

    private static Mesh GetQuadMesh()
    {
        if (_quadMeshCache != null) return _quadMeshCache;

        var builtin = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        if (builtin != null)
        {
            _quadMeshCache = builtin;
            return builtin;
        }

        // Create a simple quad procedurally as fallback
        var mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f)
        };
        mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f)
        };
        mesh.RecalculateNormals();
        _quadMeshCache = mesh;
        return mesh;
    }

    internal static Color RandomVibrantColor()
    {
        return Color.HSVToRGB(UnityEngine.Random.value, 1f, 1f);
    }

    private static void SpawnTextAtPosition(Vector3 worldPos, string text, Color color, TextConfig config)
    {
        if (string.IsNullOrEmpty(text)) return;

        var go = new GameObject("StreamReactiveFloatingText");
        go.transform.position = worldPos + Vector3.up * 0.5f;

        var tmp = go.AddComponent<TextMeshPro>();
        var wrapped = WordWrap(ClampRichTextSizes(text, config.Size), 40);
        tmp.text = wrapped;
        tmp.fontSize = config.Size;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSizeMin = 0.1f;
        tmp.fontSizeMax = config.Size;
        tmp.enableAutoSizing = false;
        tmp.enableWordWrapping = true;

        var cfg = PluginConfig.Instance;
        if (cfg != null)
            tmp.lineSpacing = Mathf.Clamp(cfg.BombTextLineSpacing, -30f, 100f);

        var rect = go.GetComponent<RectTransform>();
        if (rect == null)
            rect = go.AddComponent<RectTransform>();
        // Roughly 50 characters per line (average glyph ~0.5em wide) so
        // a 150-char message wraps to about 3 lines regardless of text size.
        float wrapWidth = config.Size * 50f * 0.5f;
        rect.sizeDelta = new Vector2(wrapWidth, config.Size * 4f);
        tmp.ForceMeshUpdate(true, true);
        VerboseLog($"SpawnTextAtPosition: len={wrapped.Length}, lines={wrapped.Split('\n').Length}, font={config.Size}, wrapWidth={wrapWidth}, rectW={rect.rect.width}");

        _activeFloatingTexts.Add(go);
        RuntimeHooks.RunCoroutine(FloatAndFadeText(tmp, go, config.Lifetime));
        VerboseLog($"SpawnTextAtPosition: spawned text '{text}' at {worldPos}");
    }

    /// <summary>
    /// Normalizes and caps size tags in viewer message text. Viewer-supplied
    /// numbers are interpreted as "font points" - the same scale as the
    /// settings sliders, where 12 is the classic look - so a small number
    /// renders small instead of huge (TMP treats raw &lt;size=N&gt; as absolute
    /// points against our tiny base font). Every absolute tag is rewritten to
    /// a percent of the base size; anything above MaxTextSize (also in font
    /// points) is clamped to it. Percent tags are capped by the same ceiling.
    /// 0 = no limit (text passes through untouched).
    /// </summary>
    private static string ClampRichTextSizes(string text, float baseSize)
    {
        var cfg = PluginConfig.Instance;
        var maxPoints = cfg?.MaxTextSize ?? 0f;
        if (maxPoints <= 0f || string.IsNullOrEmpty(text) || !text.Contains("<size=", StringComparison.OrdinalIgnoreCase))
            return text;

        var basePoints = Mathf.Max(0.01f, baseSize * PluginConfig.FontPointsPerUnit);
        var maxPct = maxPoints / basePoints * 100f;

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<size=(\d+(?:\.\d+)?)\s*(%)?>",
            m =>
            {
                var value = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var pct = m.Groups[2].Success ? value : value / basePoints * 100f;
                if (pct > maxPct)
                    pct = maxPct;
                return "<size=" + pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%>";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Removes Unity rich-text tags (e.g. &lt;size&gt;, &lt;color&gt;, &lt;b&gt;)
    /// from viewer message text so viewers can't style bomb text. Nested and
    /// malformed tags are handled by scanning for balanced angle brackets.
    /// </summary>
    internal static string StripRichTextTags(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                int close = text.IndexOf('>', i + 1);
                if (close > i)
                {
                    var inner = text.Substring(i + 1, close - i - 1);
                    if (!inner.Contains('<'))
                    {
                        // Looks like a tag: consume it.
                        i = close;
                        continue;
                    }
                    // Otherwise '<' is literal text - fall through and append.
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Truncates viewer message text to at most <paramref name="maxWords"/>
    /// words (0 = no limit). A "word" is a whitespace-separated run of non-
    /// whitespace characters; rich-text tags count as zero width and stick to
    /// the word that follows them, so leading tags like &lt;color=red&gt; are
    /// never orphaned. Words past the cap are dropped and the remaining text is
    /// re-joined with single spaces. Trailing standalone tags (e.g. a final
    /// &lt;size=0&gt;) don't count as words and are preserved when the message
    /// stays within its budget.
    /// </summary>
    internal static string ClampMessageWords(string text, int maxWords)
    {
        if (maxWords <= 0 || string.IsNullOrEmpty(text))
            return text;

        // Tokenize into words where leading tags stay attached; whitespace
        // between words is discarded. A token is a word only if it contains
        // at least one non-tag character (tag-only tokens only occur at the
        // end, e.g. a trailing <size=0>).
        var words = new List<(string Token, bool IsWord)>(16);
        var cur = new System.Text.StringBuilder();
        var curHasText = false;
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '<')
            {
                var end = text.IndexOf('>', i + 1);
                if (end < 0)
                {
                    cur.Append(c);
                    curHasText = true;
                    i++;
                    continue;
                }
                cur.Append(text, i, end - i + 1);
                i = end + 1;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0)
                {
                    words.Add((cur.ToString(), curHasText));
                    cur.Length = 0;
                    curHasText = false;
                }
                i++;
                continue;
            }
            cur.Append(c);
            curHasText = true;
            i++;
        }
        if (cur.Length > 0)
            words.Add((cur.ToString(), curHasText));

        int realWords = 0;
        foreach (var (_, isWord) in words)
            if (isWord) realWords++;

        if (realWords <= maxWords)
            return text;

        var result = new System.Text.StringBuilder(text.Length);
        var emitted = 0;
        foreach (var (token, isWord) in words)
        {
            if (isWord && emitted >= maxWords)
                break;
            if (emitted > 0)
                result.Append(' ');
            result.Append(token);
            if (isWord)
                emitted++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Greedy word wrap that understands TMP rich text: tags (&lt;...&gt;) count
    /// as zero width, never split across a break, and glue to the word that
    /// follows them. Explicit newlines in the input are preserved.
    /// </summary>
    private static string WordWrap(string text, int maxCharsPerLine)
    {
        if (string.IsNullOrEmpty(text) || maxCharsPerLine <= 0)
            return text;

        var result = new System.Text.StringBuilder(text.Length + 16);
        var line = new System.Text.StringBuilder(maxCharsPerLine + 8);
        var lineVisible = 0;

        void EndLine()
        {
            result.Append(line);
            result.Append('\n');
            line.Clear();
            lineVisible = 0;
        }

        var segments = text.Split('\n');
        for (var s = 0; s < segments.Length; s++)
        {
            if (s > 0)
                EndLine();

            var seg = segments[s];
            var i = 0;
            while (i < seg.Length)
            {
                // Tags ride along free of charge and glue to the next word.
                while (i < seg.Length && seg[i] == '<')
                {
                    var close = seg.IndexOf('>', i);
                    if (close < 0)
                    {
                        // Unterminated tag: keep verbatim.
                        line.Append(seg, i, seg.Length - i);
                        i = seg.Length;
                        break;
                    }
                    line.Append(seg, i, close - i + 1);
                    i = close + 1;
                }

                var start = i;
                while (i < seg.Length && seg[i] != ' ' && seg[i] != '<')
                    i++;

                if (i > start)
                {
                    var wordLen = i - start;
                    if (lineVisible > 0 && lineVisible + 1 + wordLen > maxCharsPerLine)
                    {
                        EndLine();
                        line.Append(seg, start, wordLen);
                        lineVisible = wordLen;
                    }
                    else
                    {
                        if (lineVisible > 0)
                        {
                            line.Append(' ');
                            lineVisible++;
                        }
                        line.Append(seg, start, wordLen);
                        lineVisible += wordLen;
                    }
                }

                if (i < seg.Length && seg[i] == ' ')
                    i++;
            }
        }

        if (line.Length > 0)
            result.Append(line);

        // A wrap landing directly beside an author-provided <br> would render
        // a blank line (the huge-gap bug), so collapse those doubles.
        return result.ToString()
            .Replace("\n<br>", "<br>")
            .Replace("<br>\n", "<br>")
            .Replace("\n<br/>", "<br/>")
            .Replace("<br/>\n", "<br/>");
    }

    private static System.Collections.IEnumerator FloatAndFadeText(TextMeshPro tmp, GameObject go, float lifetime)
    {
        var elapsed = 0f;
        var startPos = go.transform.position;
        // Always drift away from the player (into the map). The player faces +z,
        // so +z is the one direction that is consistently "away" in every map,
        // in normal play and in replays. Relying on Camera.main is unreliable
        // (null in normal play, present during replay playback) and produced
        // inconsistent directions.
        var drift = Vector3.forward;

        while (elapsed < lifetime)
        {
            if (tmp == null) yield break;
            elapsed += Time.deltaTime;
            var t = elapsed / lifetime;

            // Fly away from the player as it fades
            go.transform.position = startPos + drift * Mathf.Lerp(0f, 2.5f, t);

            // Ease-in: slow start, fast finish (t^2)
            var scale = 1f - (t * t);
            tmp.transform.localScale = Vector3.one * scale;
            tmp.color = new Color(tmp.color.r, tmp.color.g, tmp.color.b, Mathf.Lerp(1f, 0f, t));

            yield return null;
        }

        _activeFloatingTexts.Remove(go);
        if (go != null)
            UnityEngine.Object.Destroy(go);
    }

    private static void DestroyAllFloatingTexts()
    {
        for (int i = _activeFloatingTexts.Count - 1; i >= 0; i--)
        {
            var go = _activeFloatingTexts[i];
            if (go != null)
                UnityEngine.Object.Destroy(go);
        }
        _activeFloatingTexts.Clear();
    }

    private static Material? _glowMaterial;
    private static Mesh? _bombMeshCache;
    // The actual material a real game bomb renders with (usually a custom
    // glowing material textured so stock bombs always show). Preferred over the
    // bare Custom/Glowing fallback, which has no texture and can render as a
    // barely-visible bloom shell - the "invisible bomb" on modded maps.
    private static Material? _bombMaterialCache;
    private static Mesh? _roundedCubeMesh;
    private static readonly int _colorPropertyId = Shader.PropertyToID("_Color");
    private static readonly Dictionary<int, Mesh?> _originalNoteMeshes = new();
    private static readonly Dictionary<int, Material?> _originalNoteMaterials = new();
    // Original Renderer.enabled for the bomb body. Vivify (and similar) can
    // render the note body with the renderer disabled and the GameObject left
    // active, so the bomb swap would land on an invisible renderer; we force it
    // on and restore it on unwind.
    private static readonly Dictionary<int, bool> _originalNoteRendererEnabled = new();
    // Original per-renderer property block (color MPB). The bomb apply writes an
    // HDR _Color override into the block renderer's property block; without
    // restoring this block the note keeps glowing in the bomb color after it is
    // un-bombed ("a random note changed color after a missed bomb").
    private static readonly Dictionary<int, MaterialPropertyBlock> _originalNoteProperties = new();
    // Renderer components disabled (instead of their GameObjects) because they
    // are ANCESTORS of the bomb body: SetActive(false) on an ancestor would make
    // the bomb body itself inactive in the hierarchy (CellNote/Vivify nest their
    // parts under the note cube), hiding the bomb entirely. Keys are the
    // Renderer instances, values the original enabled state.
    private static readonly Dictionary<UnityEngine.Renderer, bool> _ancestorRendererEnabledByBomb = new();
    private const int ActiveBlockBonus = 10000;

    /// <summary>
    /// Finds the note's main block renderer flexibly. Stock notes still use
    /// "NoteCube", but modded notes can rename or restructure the body. We
    /// prefer body-like names and de-prioritize helper meshes such as depth
    /// clears, outlines, glows and dot objects that are visually black or
    /// transparent.
    /// </summary>
    internal static Transform? FindNoteBlockTransform(NoteController note)
    {
        if (note == null) return null;
        return FindPreferredNoteBlockTransform(note.transform);
    }

    internal static Transform? FindPreferredNoteBlockTransform(Transform root)
    {
        if (root == null) return null;

        // Scene-attached (real spawned note or thrown clone) hierarchies obey
        // activeInHierarchy; asset templates aren't part of any scene and every
        // child reports inactive, so they must stay eligible regardless.
        var scene = root.gameObject.scene;
        var inLiveScene = scene.IsValid() && scene.isLoaded;

        Transform? best = null;
        var bestScore = int.MinValue;
        var bestVerts = 0;

        foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf?.sharedMesh == null || r.sharedMaterial == null) continue;

            // A renderer that is NOT active - or whose renderer component is
            // disabled - in a live hierarchy is invisible, even though its
            // GameObject may be active (Vivify toggles the visible body via
            // Renderer.enabled). Picking it (as the bomb target, say) would
            // produce nothing on screen. Skip it outright; only scene-attached
            // checks matter.
            if (inLiveScene && (!r.gameObject.activeInHierarchy || !r.enabled))
                continue;

            var name = r.gameObject.name;
            var score = ScoreNoteBlockCandidate(name);
            var verts = mf.sharedMesh.vertexCount;
            score += Mathf.Min(verts / 8, 250);

            // A renderer that is actually active in a live hierarchy is far more
            // likely to be the visible block than a stock "NoteCube" a mod has
            // deactivated while parenting its own model (Vivify).
            if (r.gameObject.activeSelf || r.gameObject.activeInHierarchy)
                score += ActiveBlockBonus;

            // The biggest active renderer is the likeliest block body: modded
            // notes (Vivify) parent a large custom model that name heuristics
            // rank poorly (or that carries an unhelpful name) next to small
            // helper meshes labeled "Arrow"/"Glow". Bounds volume outranks
            // those name penalties, so the bomb reliably lands on the visible
            // body instead of the arrow.
            var bounds = r.bounds;
            var boundsDiag = bounds.size.x * bounds.size.x
                + bounds.size.y * bounds.size.y + bounds.size.z * bounds.size.z;
            score += Mathf.Min((int)(boundsDiag * 1000f), 1000);

            if (score > bestScore || (score == bestScore && verts > bestVerts))
            {
                bestScore = score;
                bestVerts = verts;
                best = r.transform;
            }
        }

        if (best != null)
        {
            VerboseLog(
                $"FindPreferredNoteBlockTransform: root='{root.name}' picked '{best.name}' " +
                $"(score {bestScore}, verts {bestVerts}, activeInHierarchy={best.gameObject.activeInHierarchy}).");
        }

        return best;
    }

    private static int ScoreNoteBlockCandidate(string name)
    {
        static bool Has(string value, string token) =>
            value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        var score = 0;

        if (Has(name, "NoteCube")) score += 1000;
        if (Has(name, "NoteHD")) score += 950;
        if (Has(name, "NoteBlock")) score += 900;
        if (Has(name, "NormalGameNote")) score += 250;
        if (Has(name, "ProModeGameNote")) score += 225;
        if (Has(name, "BurstSlider")) score += 150;

        if (Has(name, "DepthClear")) score -= 700;
        if (Has(name, "AccDot")) score -= 600;
        if (Has(name, "Outline")) score -= 500;
        if (Has(name, "Glow")) score -= 400;
        if (Has(name, "Arrow")) score -= 300;
        if (Has(name, "Circle")) score -= 300;
        if (Has(name, "Bomb")) score -= 1000;

        return score;
    }

    internal static bool IsNoteBombed(NoteController note)
    {
        return note != null && _bombedNotes.Contains(note);
    }

    internal static (MeshRenderer? renderer, MeshFilter? filter, Transform transform)? FindNoteBlock(NoteController note)
    {
        var t = FindNoteBlockTransform(note);
        if (t == null) return null;
        return (t.GetComponent<MeshRenderer>(), t.GetComponent<MeshFilter>(), t);
    }

    internal static void EnsureRoundedCubeMesh()
    {
        if (_roundedCubeMesh != null) return;

        float size = 0.6f;
        float exponent = 4f;
        float half = size * 0.5f;
        int segments = 6;

        var mesh = new Mesh();
        var verts = new List<Vector3>();
        var tris = new List<int>();

        Vector3[] normals = {
            Vector3.right, Vector3.left,
            Vector3.up, Vector3.down,
            Vector3.forward, Vector3.back
        };
        Vector3[][] tangents = {
            new[] { Vector3.up, Vector3.forward },
            new[] { Vector3.up, Vector3.forward },
            new[] { Vector3.right, Vector3.forward },
            new[] { Vector3.right, Vector3.forward },
            new[] { Vector3.right, Vector3.up },
            new[] { Vector3.right, Vector3.up },
        };

        for (int f = 0; f < 6; f++)
        {
            var n = normals[f];
            var t1 = tangents[f][0];
            var t2 = tangents[f][1];

            int baseIndex = verts.Count;

            for (int j = 0; j <= segments; j++)
            {
                for (int i = 0; i <= segments; i++)
                {
                    float u = i * 2f / segments - 1f;
                    float v = j * 2f / segments - 1f;

                    var dir = n + t1 * u + t2 * v;
                    float ax = Mathf.Pow(Mathf.Abs(dir.x), exponent);
                    float ay = Mathf.Pow(Mathf.Abs(dir.y), exponent);
                    float az = Mathf.Pow(Mathf.Abs(dir.z), exponent);
                    float r = Mathf.Pow(ax + ay + az, 1f / exponent);

                    verts.Add(dir * (half / r));
                }
            }

            for (int j = 0; j < segments; j++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = baseIndex + j * (segments + 1) + i;
                    int b = a + 1;
                    int c = baseIndex + (j + 1) * (segments + 1) + i;
                    int d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
        }

        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        _roundedCubeMesh = mesh;
    }

    internal static void EnsureGlowMaterial()
    {
        if (_glowMaterial != null) return;

        // 1. Try active FloatingScreenHandle
        var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (var h in allGo)
        {
            if (h == null || !h.activeInHierarchy || h.name != "FloatingScreenHandle") continue;
            var r = h.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null
                && r.sharedMaterial.shader.name == "Custom/Glowing")
            {
                _glowMaterial = r.sharedMaterial;
                Plugin.Log.Info($"EnsureGlowMaterial: cached from FloatingScreenHandle");
                return;
            }
        }

        // 2. Search all materials
        var allMaterials = Resources.FindObjectsOfTypeAll<Material>();
        foreach (var m in allMaterials)
        {
            if (m != null && m.shader != null && m.shader.name == "Custom/Glowing")
            {
                _glowMaterial = m;
                Plugin.Log.Info($"EnsureGlowMaterial: found via material search ({m.name})");
                return;
            }
        }

        // 3. Try Shader.Find
        var shader = Shader.Find("Custom/Glowing");
        if (shader != null)
        {
            try
            {
                _glowMaterial = new Material(shader);
                _glowMaterial.name = "StreamReactiveGlowMat";
                Plugin.Log.Info($"EnsureGlowMaterial: created from Shader.Find");
            }
            catch { }
        }
    }

    private static void EnsureBombMeshCache()
    {
        if (_bombMeshCache != null) return;

        var bombNotes = Resources.FindObjectsOfTypeAll<BombNoteController>();
        foreach (var bomb in bombNotes)
        {
            if (bomb == null) continue;
            var mf = bomb.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                _bombMeshCache = mf.sharedMesh;
                var r = mf.GetComponent<Renderer>();
                if (r == null) r = mf.GetComponentInParent<Renderer>();
                if (r != null && r.sharedMaterial != null && _bombMaterialCache == null)
                    _bombMaterialCache = r.sharedMaterial;
                Plugin.Log.Info($"EnsureBombMeshCache: cached bomb mesh '{mf.sharedMesh.name}' and material " +
                    $"'{_bombMaterialCache?.name ?? "<?>"}' from '{bomb.name}'.");
                break;
            }
        }
    }

    private static System.Collections.IEnumerator ApplyBombVisualDeferred(NoteController note, Color color, bool rainbow)
    {
        // Wait one frame so all Init postfixes (including NoteTweaks) finish
        // before we overwrite the note visual with the bomb look.
        yield return null;
        if (note != null && _bombedNotes.Contains(note))
            ApplyBombVisual(note, color, rainbow);
    }

    internal static void ApplyBombVisual(NoteController note, Color color, bool rainbow = false)
    {
        if (note == null || note is BombNoteController)
            return;

        VerboseLog($"ApplyBombVisual: Starting for note {note.GetInstanceID()}, name={note.name}");

        EnsureGlowMaterial();

        // Cache bomb mesh
        EnsureBombMeshCache();

        if (_bombMeshCache == null)
        {
            Plugin.Log.Warn("ApplyBombVisual: Could not find bomb mesh");
            return;
        }

        if (_glowMaterial == null)
        {
            Plugin.Log.Warn("ApplyBombVisual: No glow material available");
            return;
        }

        var noteCubeTransform = FindNoteBlockTransform(note);
        if (noteCubeTransform == null)
        {
            Plugin.Log.Warn("ApplyBombVisual: Could not find note block renderer (NoteCube or equivalent)");
            return;
        }

        VerboseLog(
            $"ApplyBombVisual: selected block '{noteCubeTransform.name}' " +
            $"activeInHierarchy={noteCubeTransform.gameObject.activeInHierarchy}, " +
            $"mat='{noteCubeTransform.GetComponent<MeshRenderer>()?.sharedMaterial?.name}'.");

        var noteCubeRenderer = noteCubeTransform.GetComponent<MeshRenderer>();
        var noteCubeMeshFilter = noteCubeTransform.GetComponent<MeshFilter>();
        if (noteCubeRenderer == null || noteCubeMeshFilter == null)
        {
            Plugin.Log.Warn("ApplyBombVisual: NoteCube has no MeshRenderer or MeshFilter");
            return;
        }

        int rendererId = noteCubeRenderer.GetInstanceID();
        // Track the exact renderer we bombed so RestoreNoteVisuals restores THIS
        // renderer, not a re-derived candidate (which can differ once the note
        // carries the Bomb mesh).
        _bombedRenderersByNote[note] = noteCubeRenderer;
        // Only store originals on the first bomb-visual application. Subsequent
        // calls must not overwrite them, otherwise RestoreNoteVisuals would
        // restore the bomb mesh/material instead of the real note originals.
        if (!_originalNoteMeshes.ContainsKey(rendererId))
            _originalNoteMeshes[rendererId] = noteCubeMeshFilter.sharedMesh;
        if (!_originalNoteMaterials.ContainsKey(rendererId))
            _originalNoteMaterials[rendererId] = noteCubeRenderer.sharedMaterial;
        if (!_originalNoteRendererEnabled.ContainsKey(rendererId))
            _originalNoteRendererEnabled[rendererId] = noteCubeRenderer.enabled;

        // Swap material: the glowy HDR bloom shell is what makes a StreamReactive
        // bomb read as a bomb (matches the stable release). The cached real-bomb
        // material is only a fallback in case the glow material is unavailable -
        // its texture draws arrows on the shell, which the user explicitly dislikes.
        var baseMat = _glowMaterial ?? _bombMaterialCache;
        if (baseMat == null)
        {
            Plugin.Log.Warn("ApplyBombVisual: No bomb material available");
            return;
        }

        // Swap mesh to bomb
        noteCubeMeshFilter.sharedMesh = _bombMeshCache;

        // The block renderer may have been disabled while its note visuals were
        // shown by another renderer (Vivify). Force it on so the bomb body we
        // just swapped in actually draws.
        if (!noteCubeRenderer.enabled)
            noteCubeRenderer.enabled = true;

        // Swap material to glow material (instance for per-note color)
        try
        {
            noteCubeRenderer.sharedMaterial = baseMat;
            var instanceMat = noteCubeRenderer.material; // creates instance (required for bloom)
            // Boost color into HDR range for strong bloom (alpha=1 matches backup that worked)
            var glowBrightness = Mathf.Max(0f, PluginConfig.Instance?.BombGlowBrightness ?? 3f);
            float brightness = glowBrightness;
            var hdrColor = new Color(color.r * brightness, color.g * brightness, color.b * brightness, 1f);
            instanceMat.color = hdrColor;
            // Custom/Glowing-family shaders may read a glow-tint property instead
            // of _Color; poke the common ones so the shell isn't pitch black.
            foreach (var propName in new[] { "_GlowColor", "_SimpleGlowColor", "_TintColor", "_Color" })
            {
                if (instanceMat.HasProperty(propName))
                    instanceMat.SetColor(propName, hdrColor);
            }
            instanceMat.name = "BombGlow (instance)";
            // Override color in MPB so note's original color doesn't fight us.
            // Capture the pre-bomb block first so RestoreNoteVisuals can put it
            // back - otherwise the note keeps the HDR bomb color after restore.
            var mpb = new MaterialPropertyBlock();
            noteCubeRenderer.GetPropertyBlock(mpb);
            if (!_originalNoteProperties.ContainsKey(rendererId))
            {
                var origMpb = new MaterialPropertyBlock();
                noteCubeRenderer.GetPropertyBlock(origMpb);
                _originalNoteProperties[rendererId] = origMpb;
            }
            mpb.SetColor(_colorPropertyId, hdrColor);
            noteCubeRenderer.SetPropertyBlock(mpb);
            VerboseLog($"  Applied bomb material '{baseMat.name}' (shader {baseMat.shader?.name ?? "?"}), hdrColor=({hdrColor.r:F2},{hdrColor.g:F2},{hdrColor.b:F2})");

            if (rainbow)
                RuntimeHooks.RunCoroutine(AnimateRainbowBombNote(note, noteCubeRenderer, instanceMat, mpb, GetRainbowSpeed()));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"  Could not instance glow material: {ex.Message}");
            noteCubeRenderer.sharedMaterial = baseMat;
        }

        // Hide arrows
        var arrow = noteCubeTransform.Find("NoteArrow");
        if (arrow != null) arrow.gameObject.SetActive(false);
        var arrowGlow = noteCubeTransform.Find("NoteArrowGlow");
        if (arrowGlow != null) arrowGlow.gameObject.SetActive(false);
        var circleGlow = noteCubeTransform.Find("NoteCircleGlow");
        if (circleGlow != null) circleGlow.gameObject.SetActive(false);

        // Disable every other active renderer so nothing but the bomb body stays
        // visible. Any mesh the note still shows competes with the bomb look
        // (e.g. a Vivify arrow that was swapped to the block's bomb material,
        // or a stock outline), so the safe rule is: hide all other active
        // Renderer components (Mesh AND Skinned) except the bomb target's own
        // GameObject.
        if (!_disabledByBomb.TryGetValue(note, out var disabledList))
        {
            disabledList = new List<GameObject>();
            _disabledByBomb[note] = disabledList;
        }
        foreach (var childRenderer in note.GetComponentsInChildren<Renderer>(true))
        {
            if (childRenderer == null) continue;
            if (childRenderer.gameObject == noteCubeTransform.gameObject) continue;
            if (!childRenderer.gameObject.activeSelf) continue;
            // Never disable an ANCESTOR of the bomb body via SetActive: the
            // bomb body is a child of the note cube (CellNote/Vivify nest their
            // parts underneath it), so turning off the ancestor GameObject would
            // make the bomb body inactive in the hierarchy and invisible. For
            // ancestors just disable the Renderer component - the note parts the
            // ancestor would draw are hidden, while the subtree (and thus the
            // bomb body placed inside it) stays active.
            if (noteCubeTransform.IsChildOf(childRenderer.transform))
            {
                if (!_ancestorRendererEnabledByBomb.ContainsKey(childRenderer))
                {
                    _ancestorRendererEnabledByBomb[childRenderer] = childRenderer.enabled;
                    childRenderer.enabled = false;
                }
                continue;
            }
            childRenderer.gameObject.SetActive(false);
            disabledList.Add(childRenderer.gameObject);
        }
        if (disabledList.Count > 0)
        {
            VerboseLog($"ApplyBombVisual: disabled {disabledList.Count} extra child renderer(s)");
        }

        // After hiding, the ONLY renderers still ACTUALLY DRAWING should be the
        // bomb body itself. Count enabled renderers on active GameObjects (a GO
        // whose renderer.enabled was turned off - e.g. a NoteCube ancestor - has
        // its GO still active and was previously misreported as visible).
        var stillActive = new System.Collections.Generic.List<string>();
        foreach (var cr in note.GetComponentsInChildren<Renderer>(true))
        {
            if (cr == null || cr.gameObject == noteCubeTransform.gameObject)
                continue;
            if (cr.gameObject.activeInHierarchy && cr.enabled)
                stillActive.Add(cr.gameObject.name);
        }
        VerboseLog(
            $"ApplyBombVisual: bomb body='{noteCubeTransform.name}' " +
            $"activeInHierarchy={noteCubeTransform.gameObject.activeInHierarchy} " +
            $"still-visible renderers={stillActive.Count}: {string.Join(",", stillActive.ToArray())}");
        if (!noteCubeTransform.gameObject.activeInHierarchy)
        {
            Plugin.Log.Warn("ApplyBombVisual: bomb body is NOT active in hierarchy - bomb will be invisible!");
        }

        VerboseLog($"ApplyBombVisual: Mesh+bomb, mesh={_bombMeshCache.name} verts={_bombMeshCache.vertexCount}, " +
            $"material={(_glowMaterial ?? _bombMaterialCache)?.name ?? "?"} shader={(_glowMaterial ?? _bombMaterialCache)?.shader?.name ?? "?"}, " +
            $"enabled={noteCubeRenderer.enabled}");
    }

    private static System.Collections.IEnumerator AnimateRainbowBombNote(NoteController note, MeshRenderer renderer, Material instanceMat, MaterialPropertyBlock mpb, float speed)
    {
        while (note != null && _bombedNotes.Contains(note))
        {
            var c = RainbowColor(speed);
            var glowBrightness = Mathf.Max(0f, PluginConfig.Instance?.BombGlowBrightness ?? 3f);
            var hdr = new Color(c.r * glowBrightness, c.g * glowBrightness, c.b * glowBrightness, 1f);
            instanceMat.SetColor(_colorPropertyId, hdr);
            mpb.SetColor(_colorPropertyId, hdr);
            renderer.SetPropertyBlock(mpb);
            yield return null;
        }
    }

    internal static float GetRainbowSpeed()
    {
        return PluginConfig.Instance?.BombRainbowSpeed ?? 0.5f;
    }

    internal static Color RainbowColor(float speed)
    {
        float hue = (Time.time * speed) % 1f;
        if (hue < 0f) hue += 1f;
        return Color.HSVToRGB(hue, 1f, 1f);
    }

    private static void RestoreAllDisabledByBomb()
    {
        foreach (var kvp in _disabledByBomb)
        {
            foreach (var go in kvp.Value)
            {
                if (go != null) go.SetActive(true);
            }
        }
        _disabledByBomb.Clear();

        // Re-enable any ancestor renderers we disabled instead of their
        // GameObjects (bomb body subtrees are left active - only their own
        // note-body renderer was turned off so the bomb could show).
        foreach (var kvp in _ancestorRendererEnabledByBomb)
        {
            if (kvp.Key != null) kvp.Key.enabled = kvp.Value;
        }
        _ancestorRendererEnabledByBomb.Clear();
    }

    internal static void RestoreNoteVisuals(NoteController note)
    {
        if (note == null) return;

        // Restore the EXACT renderer we bombed. Re-deriving it with
        // FindNoteBlockTransform is unsafe: while the note still shows the Bomb
        // mesh its scoring changes, so it can return a different child whose
        // instance id is not in the originals dicts - which silently skips the
        // restore and leaves the note stuck looking like a bomb ("keeps
        // respawning/stuck to notes" on reuse).
        if (_bombedRenderersByNote.TryGetValue(note, out var noteCubeRenderer) && noteCubeRenderer != null)
        {
            RestoreNoteRendererVisuals(noteCubeRenderer.transform, noteCubeRenderer);
            _bombedRenderersByNote.Remove(note);
        }
        else
        {
            var noteCubeTransform = FindNoteBlockTransform(note);
            if (noteCubeTransform != null)
            {
                var renderer = noteCubeTransform.GetComponent<MeshRenderer>();
                RestoreNoteRendererVisuals(noteCubeTransform, renderer);
            }
        }

        // Re-enable any child objects we disabled (NoteTweaks outlines, etc.)
        if (_disabledByBomb.TryGetValue(note, out var disabledList))
        {
            foreach (var go in disabledList)
            {
                if (go != null) go.SetActive(true);
            }
            _disabledByBomb.Remove(note);
        }

        // Re-enable ancestor renderers we'd disabled for this bombed note too.
        var renderersToRestore = new List<UnityEngine.Renderer>();
        foreach (var kvp in _ancestorRendererEnabledByBomb)
        {
            if (kvp.Key == null) continue;
            if (!kvp.Key.transform.IsChildOf(note.transform) && kvp.Key.transform != note.transform)
                continue;
            renderersToRestore.Add(kvp.Key);
        }
        foreach (var r in renderersToRestore)
        {
            if (_ancestorRendererEnabledByBomb.TryGetValue(r, out var origEnabled))
            {
                r.enabled = origEnabled;
                _ancestorRendererEnabledByBomb.Remove(r);
            }
        }
    }

    private static void RestoreNoteRendererVisuals(Transform noteCubeTransform, MeshRenderer renderer)
    {
        if (noteCubeTransform != null)
        {
            var arrow = noteCubeTransform.Find("NoteArrow");
            if (arrow != null) arrow.gameObject.SetActive(true);
            var arrowGlow = noteCubeTransform.Find("NoteArrowGlow");
            if (arrowGlow != null) arrowGlow.gameObject.SetActive(true);
            var circleGlow = noteCubeTransform.Find("NoteCircleGlow");
            if (circleGlow != null) circleGlow.gameObject.SetActive(true);
        }

        var mf = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
        int id = renderer?.GetInstanceID() ?? 0;

        if (mf != null && _originalNoteMeshes.TryGetValue(id, out var origMesh))
        {
            mf.sharedMesh = origMesh;
            _originalNoteMeshes.Remove(id);
        }

        if (renderer != null && _originalNoteMaterials.TryGetValue(id, out var origMat))
        {
            renderer.sharedMaterial = origMat;
            _originalNoteMaterials.Remove(id);
        }

        if (renderer != null && _originalNoteRendererEnabled.TryGetValue(id, out var origEnabled))
        {
            renderer.enabled = origEnabled;
            _originalNoteRendererEnabled.Remove(id);
        }

        // Put back the block renderer's original color property block so the note
        // stops showing the bomb's HDR color after it is un-bombed.
        if (renderer != null && _originalNoteProperties.TryGetValue(id, out var origMpb))
        {
            renderer.SetPropertyBlock(origMpb);
            _originalNoteProperties.Remove(id);
        }
    }

}

internal sealed class NoteCutForwarder : INoteControllerNoteWasCutEvent
{
    public static readonly NoteCutForwarder Instance = new NoteCutForwarder();

    private NoteCutForwarder() { }

    public void HandleNoteControllerNoteWasCut(NoteController note, in NoteCutInfo noteCutInfo)
    {
        if (note == null || note.noteData == null)
            return;

        NoteCosmeticController.VerboseLog($"NoteCutForwarder: cut for note {note.GetInstanceID()}, name={note.name}");
        NoteCosmeticController.ProcessNoteAtCut(note);
    }
}

[HarmonyPatch]
internal static class NoteInitPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = NoteCosmeticController.FindInheritedMethod(typeof(NoteController), "Init");
        if (method != null)
        {
            Plugin.Log.Debug($"NoteInitPatch: Found Init method: {method.DeclaringType}.{method.Name}");
            yield return method;
        }
        else
        {
            Plugin.Log.Warn("NoteInitPatch: Could not find Init method on NoteController");
        }
    }

    private static void Prefix(NoteController __instance)
    {
        if (__instance == null)
            return;

        NoteCosmeticController.RestoreNoteIfNeeded(__instance);
    }

    private static void Postfix(NoteController __instance)
    {
        if (__instance == null || __instance.noteData == null)
            return;

        NoteCosmeticController.VerboseLog($"NoteInitPatch: Postfix called for note {__instance.GetInstanceID()}, name={__instance.name}");
        NoteCosmeticController.ProcessNoteAtInit(__instance);
        // Let NoteTweaks and any other Init postfix finish settling the note
        // renderers before we snapshot their color state for projectile clones.
        RuntimeHooks.RunCoroutine(CaptureProjectileMaterialsDeferred(__instance));
    }

    private static System.Collections.IEnumerator CaptureProjectileMaterialsDeferred(NoteController note)
    {
        // Let NoteTweaks (and other spawn-time mods) finish attaching their
        // renderers - especially the outline, which can be added a frame or two
        // after init. Snapshotting too early occasionally captures a note that
        // is missing its outline, and that stale capture then survives until a
        // color/material change. Waiting a few frames makes the outline reliably
        // present before we snapshot.
        for (var i = 0; i < 4; i++)
        {
            yield return null;
            if (note == null || note.noteData == null)
                yield break;
        }

        // Pre-warm the projectile visual source off the first-throw frame. Note
        // init happens on map load, so this spreads the expensive scene-wide
        // prefab scan (Resources.FindObjectsOfTypeAll) here instead of letting
        // the first throw do it synchronously and hitch. Runs after the capture
        // so the scan sees real material captures and prefers live clones.
        ProjectileThrower.CaptureLiveNoteMaterials(note);
        ProjectileThrower.WarmNotePrefab();
    }
}

[HarmonyPatch]
internal static class NoteDespawnPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = NoteCosmeticController.FindInheritedMethod(typeof(BeatmapObjectManager), "Despawn", new[] { typeof(NoteController) });
        if (method != null)
        {
            Plugin.Log.Debug($"NoteDespawnPatch: Found Despawn method: {method.DeclaringType}.{method.Name}");
            yield return method;
        }
        else
        {
            Plugin.Log.Warn("NoteDespawnPatch: Could not find Despawn method on BeatmapObjectManager");
        }
    }

    // Prefix (not Postfix): the note is moved to the object pool AFTER DespawnInternal
    // runs, so a postfix would fire too late to stop the trail rendering the pool
    // teleport. Clean up before the note is touched. The parameter name MUST match the
    // original method's parameter name ("noteController") or Harmony fails to bind it.
    private static void Prefix(NoteController noteController)
    {
        if (noteController == null)
            return;

        NoteCosmeticController.VerboseLog($"NoteDespawnPatch: Prefix called for note {noteController.GetInstanceID()}, name={noteController.name}");
        NoteCosmeticController.OnNoteDespawned(noteController);
    }
}

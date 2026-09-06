using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// Spawns simple cubes that fly at the guard capsule and bounce off it.
/// Uses manual kinematic integration instead of Rigidbody physics because
/// Beat Saber's layer collision matrix blocks most custom-object collisions.
/// Works in the menu too so it can be tested without entering a map.
/// </summary>
internal static class ProjectileThrower
{
    private const float StaggerSeconds = 0.25f;
    private const float SpawnDistance = 6f;
    private const float HorizonDistance = 30f;
    private const float CubeSize = 0.18f;
    private const float TwinOffset = 1.7f;

    private static readonly List<GameObject> Active = new();

    // Retired projectiles kept for reuse. Instantiating a full note clone per
    // throw causes a visible hitch in VR, so despawned ones are parked here
    // (deactivated) and recycled instead of destroyed.
    private static readonly Dictionary<string, List<GameObject>> Pool = new();
    private const int MaxPooledPerKey = 4;
    private static readonly string[] NoteColorProperties = { "_Color", "_SimpleColor", "_BaseColor", "_BlockColor" };

    // Persists across throw batches so consecutive single throws alternate
    // cannons instead of every batch starting on the same side.
    private static int _twinSideFlipper;

    // Last usable flat horizon forward. If the camera is looking steeply up/down
    // at throw time, flattening its forward collapses to ~zero, which makes the
    // twin-cannon cross product degenerate and throws land from a "random" spot.
    // Hold the last valid one and only refresh it when the new one is sane.
    private static Vector3 _lastGoodFlatForward = Vector3.forward;

    // Real note visual scavenged from cached gameplay prefabs, if any exist.
    private static GameObject? _noteVisualPrefab;
    private static bool _prefabIsAssetTemplate;

    // Best candidate accumulated across ConsiderChild calls (module-level so a
    // live note encountered during normal processing can upgrade the pick
    // without re-scanning the whole scene).
    private static GameObject? _bestPrefab;
    private static int _bestPrefabVerts;
    private static int _bestPrefabPref;
    private static bool _moddedRescanDone;
    private static float _lastNoteLookup;
    private static Material? _orangeMaterial;
    private static Color _noteColorA = new Color(1f, 0.235f, 0.235f); // fallback: BS default red
    private static Color _noteColorB = new Color(0.156f, 0.556f, 1f); // fallback: BS default blue
    private static bool _haveNoteColors;
    private static int _noteColorFlipper;
    private const float NoteLookupRetrySeconds = 5f;

    /// <summary>
    /// Preference tiers for scavenged note prefabs: normal arrow notes first,
    /// unknown variants next, pro-mode dots and burst-slider bits as a last
    /// resort. Higher wins.
    /// </summary>
    private static int NamePreference(string rootName)
    {
        var n = rootName.ToLowerInvariant();
        if (n.Contains("burstslider"))
            return 0;
        if (n.Contains("promode"))
            return 1;
        if (n.Contains("bomb"))
            return -1;
        if (n.Contains("normal"))
            return 3;
        return 2;
    }

    private static void EnsureNotePrefab()
    {
        if (_noteVisualPrefab != null)
        {
            // A template was cached before this map's material captures
            // existed; once they land, rescan once so mod-swapped clone
            // sources (Vivify customs) can take over for the rest of the map.
            if (!_moddedRescanDone && Plugin.IsInGame && _prefabIsAssetTemplate
                && (_liveBlockMpbs[0] != null || _liveBlockMpbs[1] != null))
            {
                _moddedRescanDone = true;
                ScanForNotePrefab();
                Plugin.Log.Debug(_prefabIsAssetTemplate
                    ? "Projectile visuals: rescan kept template source."
                    : "Projectile visuals: rescan switched to live-clone source (modded visuals).");
            }

            return;
        }

        // Prefabs get cached into memory at various points (menu decorations,
        // first map load), so retry periodically until we find one.
        var now = Time.realtimeSinceStartup;
        if (_lastNoteLookup > 0f && now - _lastNoteLookup < NoteLookupRetrySeconds)
            return;
        _lastNoteLookup = now;

        try
        {
            ScanForNotePrefab();
            Plugin.Log.Debug(_noteVisualPrefab != null
                ? "Projectile visuals: note prefab found."
                : "Projectile visuals: no cached note prefab found yet; using cube fallback.");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Warn($"Projectile visuals: note prefab lookup failed, using cube fallback. {ex}");
        }
    }

    private static void ScanForNotePrefab()
    {
        _bestPrefab = null;
        _bestPrefabVerts = 0;
        _bestPrefabPref = -1;
        var bestIsTemplate = false;

        // While inside an active map - and only once real material captures
        // exist for at least one side - live scene clones outrank templates so
        // mod-swapped visuals (e.g. Vivify custom note models) get thrown.
        // OnSceneChanged drains the pool and drops non-template sources on the
        // way out, so nothing outlives the map's AssetBundles; in menus the
        // pristine templates always win.
        var cloneBoost = Plugin.IsInGame && (_liveBlockMpbs[0] != null || _liveBlockMpbs[1] != null) ? 20 : 0;

        foreach (var controller in Resources.FindObjectsOfTypeAll<NoteController>())
        {
            if (controller == null)
                continue;

            var basePref = NamePreference(controller.name);
            if (basePref < 0)
                continue; // bombs and friends: never

            // Non-clone NoteControllers are asset templates - pristine core
            // objects that beat any scene clone outside of active gameplay.
            var isClone = controller.name.EndsWith("(Clone)", System.StringComparison.Ordinal);
            var pref = basePref + (isClone ? cloneBoost : 10);

            foreach (Transform child in controller.transform)
                ConsiderChild(child, $"{controller.name}/{child.name}", pref, !isClone,
                    ref _bestPrefab, ref _bestPrefabVerts, ref _bestPrefabPref, ref bestIsTemplate);
        }

        // Gameplay note prefabs are named exactly "N0"/"N1"/"N2"/"N3".
        foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
        {
            var root = filter.transform.root;
            var rootName = root.gameObject.name;
            if (rootName != "N0" && rootName != "N1" && rootName != "N2" && rootName != "N3")
                continue;

            foreach (Transform child in root)
                ConsiderChild(child, $"{rootName}/{child.name}",
                    NamePreference(rootName) + 10, true,
                    ref _bestPrefab, ref _bestPrefabVerts, ref _bestPrefabPref, ref bestIsTemplate);
        }

        _noteVisualPrefab = _bestPrefab;
        _prefabIsAssetTemplate = bestIsTemplate;
        if (_bestPrefab != null)
            TryCaptureNoteColors();
    }

    /// <summary>
    /// Upgrades the projectile visual source to a live note we already hold,
    /// without the scene-wide FindObjectsOfTypeAll scan that
    /// <see cref="ScanForNotePrefab"/> performs. Called during normal note
    /// processing (cheap: inspects only this controller's children) so the
    /// first throw never pays for the scan on its own critical frame. Mirrors
    /// the rescan branch in <see cref="EnsureNotePrefab"/>: only live clones
    /// outrank the pristine asset templates we already cached.
    /// </summary>
    private static void TryUpgradePrefabFromLiveNote(NoteController controller, int colorIndex)
    {
        // Only worth switching away from an asset template once real material
        // captures exist for at least one side (modded-note scenarios).
        if (!_prefabIsAssetTemplate || (_liveBlockMpbs[0] == null && _liveBlockMpbs[1] == null))
            return;

        var isClone = controller.name.EndsWith("(Clone)", System.StringComparison.Ordinal);
        if (!isClone)
            return;

        var basePref = NamePreference(controller.name);
        if (basePref < 0)
            return; // bombs, etc.: never

        var pref = basePref + 20; // cloneBoost, matching ScanForNotePrefab
        foreach (Transform child in controller.transform)
            ConsiderChild(child, $"{controller.name}/{child.name}", pref, !isClone,
                ref _bestPrefab, ref _bestPrefabVerts, ref _bestPrefabPref, ref _prefabIsAssetTemplate);

        // Lock in the live-clone source so the throw path's rescan is a no-op.
        // Only overwrite if we actually found a usable candidate; otherwise keep
        // the already-cached template prefab and just suppress the rescan.
        if (_bestPrefab != null)
            _noteVisualPrefab = _bestPrefab;
        _moddedRescanDone = true;
    }

    private static void ConsiderChild(
        Transform child,
        string label,
        int pref,
        bool isTemplate,
        ref GameObject? best,
        ref int bestVerts,
        ref int bestPref,
        ref bool bestIsTemplate)
    {
        // Must actually render something: mesh renderers with materials and
        // non-trivial meshes. Inactive renderers count too - the pristine
        // N0-N3 asset templates are not part of any scene, so everything in
        // them reports as inactive, and they are the safest source to clone.
        // Guards against picking empty shells that would clone invisibly.
        var renderers = child.GetComponentsInChildren<MeshRenderer>(true);
        var vertexCount = 0;
        foreach (var r in renderers)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (r.sharedMaterial != null && mf?.sharedMesh != null)
                vertexCount += mf.sharedMesh.vertexCount;
        }

        if (vertexCount < 100)
            return;

        if (pref > bestPref
            || (pref == bestPref && vertexCount > bestVerts))
        {
            best = child.gameObject;
            bestVerts = vertexCount;
            bestPref = pref;
            bestIsTemplate = isTemplate;
            Plugin.Log.Debug($"Projectile visuals: candidate '{label}' accepted (pref {pref}, {vertexCount} verts, template={isTemplate}).");
        }
    }

    /// <summary>
    /// Reads the player's actual left/right note colors from a cached
    /// ColorManager or ColorScheme so thrown notes match the player's scheme.
    /// Falls back to Beat Saber defaults. Uses reflection by type name because
    /// those game types clash with other referenced assemblies at compile time.
    /// </summary>
    private static void TryCaptureNoteColors()
    {
        if (_haveNoteColors)
            return;

        try
        {
            var managerType = ResolveGameType("ColorManager");
            if (managerType != null && typeof(UnityEngine.Object).IsAssignableFrom(managerType))
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(managerType))
                {
                    if (obj == null)
                        continue;

                    try
                    {
                        if (TryReadColor(obj, "RedColor", "_colorA", out var a)
                            | TryReadColor(obj, "BlueColor", "_colorB", out var b))
                        {
                            _noteColorA = a;
                            _noteColorB = b;
                            _haveNoteColors = true;
                            Plugin.Log.Info($"Projectile visuals: captured player note colors A={a} B={b}.");
                            return;
                        }
                    }
                    catch (System.Exception inner)
                    {
                        // One broken instance (prefab asset, mid-teardown object)
                        // must not kill the whole capture.
                        Plugin.Log.Debug($"Projectile visuals: ColorManager instance read failed: {inner.Message}");
                    }
                }
            }

            var schemeType = ResolveGameType("ColorScheme");
            if (schemeType != null && typeof(UnityEngine.Object).IsAssignableFrom(schemeType))
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(schemeType))
                {
                    if (obj == null)
                        continue;

                    try
                    {
                        if (TryReadColor(obj, "noteA", "_noteA", out var sa)
                            | TryReadColor(obj, "noteB", "_noteB", out var sb))
                        {
                            _noteColorA = sa;
                            _noteColorB = sb;
                            _haveNoteColors = true;
                            Plugin.Log.Info($"Projectile visuals: captured scheme note colors A={sa} B={sb}.");
                            return;
                        }
                    }
                    catch (System.Exception inner)
                    {
                        Plugin.Log.Debug($"Projectile visuals: ColorScheme instance read failed: {inner.Message}");
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile visuals: note color capture failed ({ex.Message}); using defaults.");
        }
    }

    private static System.Type? ResolveGameType(string typeName)
    {
        var direct = System.Type.GetType(typeName + ", Main");
        if (direct != null && typeof(UnityEngine.Object).IsAssignableFrom(direct))
            return direct;

        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            System.Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
            {
                // Resources.FindObjectsOfTypeAll rejects anything that is not a
                // UnityEngine.Object - other assemblies ship unrelated classes
                // with the same names.
                if (t.Name == typeName && typeof(UnityEngine.Object).IsAssignableFrom(t))
                    return t;
            }
        }

        return null;
    }

    private static bool TryReadColor(object target, string propertyName, string fieldName, out Color color)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var type = target.GetType();

        if (type.GetProperty(propertyName, flags)?.GetValue(target) is Color propValue)
        {
            color = propValue;
            return true;
        }

        if (type.GetField(fieldName, flags)?.GetValue(target) is Color fieldValue)
        {
            color = fieldValue;
            return true;
        }

        color = default;
        return false;
    }

    private static bool HasVisibleNoteColor(MaterialPropertyBlock block, Material? material)
    {
        foreach (var propName in NoteColorProperties)
        {
            var blockColor = block.GetColor(propName);
            if (IsVisibleNoteColor(blockColor))
                return true;

            if (material != null && material.HasProperty(propName))
            {
                var materialColor = material.GetColor(propName);
                if (IsVisibleNoteColor(materialColor))
                    return true;
            }
        }

        return false;
    }

    private static bool IsVisibleNoteColor(Color color) =>
        color.a > 0.001f && color.maxColorComponent > 0.001f;

    // A renderer whose name indicates it is the note's outline shell (matches
    // the "Outline" penalty in NoteCosmeticController's block scoring).
    private static bool IsOutlineRenderer(GameObject go)
    {
        var n = go.name;
        return n.IndexOf("Outline", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // True when the previously captured block color (from MPB or material) still
    // matches the current block. Used to detect pure color edits that keep the
    // same material instance (e.g. the player changes note colors in settings).
    private static bool MpbColorMatches(
        MaterialPropertyBlock oldBlock, Material? oldMat, MaterialPropertyBlock newBlock, Material? newMat)
    {
        foreach (var propName in NoteColorProperties)
        {
            var oldCol = oldBlock.GetColor(propName);
            if (oldMat != null && oldMat.HasProperty(propName))
                oldCol = oldMat.GetColor(propName);
            var newCol = newBlock.GetColor(propName);
            if (newMat != null && newMat.HasProperty(propName))
                newCol = newMat.GetColor(propName);
            if (!ApproxColor(oldCol, newCol))
                return false;
        }
        return true;
    }

    private static bool ApproxColor(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f &&
        Mathf.Abs(a.b - b.b) < 0.001f && Mathf.Abs(a.a - b.a) < 0.001f;

    internal static void Throw(int count, float? scaleOverride = null, string? originModeOverride = null, float? airTimeOverride = null)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null || !cfg.CapsuleGuardEnabled)
        {
            Plugin.Log.Debug("Throw ignored: guard capsule is disabled.");
            return;
        }

        count = Mathf.Clamp(count, 1, 1000);
        RuntimeHooks.RunCoroutine(ThrowSequence(count, scaleOverride, originModeOverride, airTimeOverride));
    }

    /// <summary>
    /// Resolves the projectile visual source and parks one pooled projectile
    /// per kind during a non-critical frame (scene load / intro), so the first
    /// real throw just reactivates a cached object instead of scanning the
    /// scene and instantiating under load. Called from both scene-loaded hooks.
    /// </summary>
    internal static void Prewarm()
    {
        RuntimeHooks.RunCoroutine(PrewarmCoroutine());
    }

    private static IEnumerator PrewarmCoroutine()
    {
        // One frame so the scene is settled and core prefabs are in memory.
        yield return null;

        try
        {
            EnsureNotePrefab();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile visuals: prewarm prefab lookup failed: {ex.Message}");
        }

        PrimePooled("noteA");
        PrimePooled("noteB");
        PrimePooled("cube");
    }

    private static void PrimePooled(string key)
    {
        try
        {
            GameObject go;
            if (key == "cube")
            {
                go = CreateCube();
            }
            else
            {
                var v = CreateVisual(key == "noteA" ? 0 : 1);
                if (v == null) return;
                go = v;
            }

            go.SetActive(false);
            Retire(go);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile visuals: prewarm pool prime for '{key}' failed: {ex.Message}");
        }
    }

    internal static void SpawnEmoteProjectile(string user, string code, Texture2D tex)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        CleanupDead();
        if (Active.Count >= Mathf.Clamp((int)cfg.ThrowMaxProjectiles, 1, 200))
            return;

        var target = CapsuleGuardController.CurrentAimPoint;
        var origin = ResolveOrigin(cfg.ThrowOriginMode);

        var go = new GameObject($"StreamReactiveEmote-{code}");

        go.transform.SetPositionAndRotation(origin, Quaternion.identity);
        var baseSize = Mathf.Clamp(cfg?.EmoteThrowSize ?? 0.5f, 0.1f, 2f);
        var aspect = (float)tex.width / Mathf.Max(1f, tex.height);
        var worldWidth = baseSize * aspect;
        var worldHeight = baseSize;
        go.transform.localScale = new Vector3(worldWidth, worldHeight, 1f);

        // Standalone emote (follows this projectile; avoids the non-uniform parent
        // scale that would shear a billboarded child).
        var set = EmoteVisualFactory.CreateEmoteVisuals(go.transform, tex, baseSize);
        var hudEntry = set.EmoteObject!.AddComponent<EmoteHudEntry>();
        hudEntry.Init(set, tex, code, go.transform, baseSize);

        // Attach frame animator for animated emotes
        if (EmoteCache.Instance.TryGetAnimatedFrames(code, out var frames, out var delay))
        {
            var animator = go.AddComponent<EmoteAnimator>();
            animator.Initialize(frames, delay, hudEntry);
        }

        var behaviour = go.AddComponent<ProjectileCube>();
        var velocity = ComputeLaunchVelocity(origin, target, cfg!, out var gravity);
        behaviour.Initialize(target, velocity, gravity);

        Active.Add(go);
    }

    internal static void StopAll()
    {
        foreach (var cube in Active)
        {
            if (cube != null)
                Object.Destroy(cube);
        }

        Active.Clear();

        foreach (var list in Pool.Values)
        {
            foreach (var go in list)
            {
                if (go != null)
                    Object.Destroy(go);
            }
            list.Clear();
        }
        Pool.Clear();
    }

    internal static void NotifyDestroyed(GameObject cube)
    {
        Active.Remove(cube);
    }

    /// <summary>
    /// Drops every cached visual on scene changes. Pooled note clones can hold
    /// meshes/materials from another map's AssetBundles (e.g. Vivify custom
    /// notes); once that map unloads them, touching the leftovers can take the
    /// graphics device down with it.
    /// </summary>
    internal static void OnSceneChanged()
    {
        EmoteHudEntry.CleanupAll();
        StopAll();
        _moddedRescanDone = false;
        // Clear the re-scan throttle so the prewarm one frame after a scene
        // change always rescans. Without this, a restart that lands inside a
        // throw-spam window inherits a still-warm _lastNoteLookup: the dropped
        // live-clone prefab below is never re-discovered, every throw renders
        // the placeholder cube, and the gate stays warm under continuous spam.
        _lastNoteLookup = 0f;
        // Asset templates are core game objects, safe to keep across scenes -
        // keeping them is what lets menu throws use real notes. Live clones
        // may reference another map's bundles and must go.
        if (!_prefabIsAssetTemplate)
            _noteVisualPrefab = null;
        // Live note material snapshots can hold bundle references (modded note
        // materials), so they must not survive a scene change. MPBs are plain
        // property bags and stay as-is (see below).
        _liveBlockMats[0] = null;
        _liveBlockMats[1] = null;
        _liveArrowMats[0] = null;
        _liveArrowMats[1] = null;
        _liveRenderers[0] = null;
        _liveRenderers[1] = null;
        _liveHasOutline[0] = false;
        _liveHasOutline[1] = false;
        // Captured material blocks are plain property bags copied from stock
        // game notes (even inside heavily-modded maps), so they carry no
        // bundle references - persisting them is what makes menu throws look
        // exactly like the last map instead of falling back to raw colors
        // that the NoteHD shader mostly ignores.
    }

    /// <summary>
    /// Parks a finished projectile for reuse instead of destroying it.
    /// Unknown or overflowing objects are destroyed as usual.
    /// </summary>
    internal static void Retire(GameObject go)
    {
        var key = PoolKeyFromName(go.name);
        if (key == null)
        {
            Object.Destroy(go);
            return;
        }

        // Release any cosmetic effects (bit outlines, sub trails) that ended
        // up riding this projectile, mirroring what happens when a real note
        // despawns - otherwise they stick around on the pooled object.
        StripAttachedEffects(go);

        go.SetActive(false);

        if (!Pool.TryGetValue(key, out var list))
        {
            list = new List<GameObject>();
            Pool[key] = list;
        }

        if (list.Count >= MaxPooledPerKey)
        {
            Object.Destroy(go);
            return;
        }

        list.Add(go);
    }

    private static string? PoolKeyFromName(string name)
    {
        if (name.EndsWith("-Cube", System.StringComparison.Ordinal))
            return "cube";
        if (name.EndsWith("-NoteA", System.StringComparison.Ordinal))
            return "noteA";
        if (name.EndsWith("-NoteB", System.StringComparison.Ordinal))
            return "noteB";
        return null;
    }

    /// <summary>
    /// Destroys StreamReactive cosmetic hierarchies (bit outlines, sub trails,
    /// floating text roots) that were cloned along with a decorated live note
    /// or attached to a projectile during flight. Only the outermost roots are
    /// destroyed; their children go with them.
    /// </summary>
    private static void StripAttachedEffects(GameObject go)
    {
        var toDestroy = new List<GameObject>();
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            if (t == go.transform || !t.name.StartsWith("StreamReactive", System.StringComparison.Ordinal))
                continue;
            // Skip children of an already-selected cosmetic root.
            var skip = false;
            var parent = t.parent;
            while (parent != null && parent != go.transform)
            {
                if (parent.name.StartsWith("StreamReactive", System.StringComparison.Ordinal))
                {
                    skip = true;
                    break;
                }
                parent = parent.parent;
            }

            if (!skip)
                toDestroy.Add(t.gameObject);
        }

        foreach (var obj in toDestroy)
            Object.Destroy(obj);
    }

    private static GameObject? TakeFromPool(string key)
    {
        if (!Pool.TryGetValue(key, out var list) || list.Count == 0)
            return null;

        var last = list.Count - 1;
        var go = list[last];
        list.RemoveAt(last);
        return go != null ? go : null;
    }

    private static IEnumerator ThrowSequence(int count, float? scaleOverride, string? originModeOverride, float? airTimeOverride)
    {
        for (var i = 0; i < count; i++)
        {
            SpawnOne(scaleOverride, originModeOverride, airTimeOverride);
            if (i < count - 1)
                yield return new WaitForSeconds(StaggerSeconds);
        }
    }

    private static void SpawnOne(float? scaleOverride, string? originModeOverride, float? airTimeOverride)
    {
        try
        {
            SpawnOneCore(scaleOverride, originModeOverride, airTimeOverride);
        }
        catch (System.Exception ex)
        {
            // One bad throw (corrupt visual source, torn-down scene object
            // during a restart) must never kill the whole coroutine batch —
            // especially under websocket spam across a scene change.
            Plugin.Log.Warn($"Projectile: throw failed, skipping ({ex.Message}).");
        }
    }

    private static void SpawnOneCore(float? scaleOverride, string? originModeOverride, float? airTimeOverride)
    {
        CleanupDead();
        var cfg = PluginConfig.Instance!;
        if (Active.Count >= Mathf.Clamp((int)cfg.ThrowMaxProjectiles, 1, 200))
            return;

        var target = CapsuleGuardController.CurrentAimPoint;
        var origin = ResolveOrigin(originModeOverride ?? cfg.ThrowOriginMode);

        if (PluginConfig.Instance?.VerboseLogging == true)
        {
            var camName = CapsuleGuardController.GetViewCamera()?.name ?? "<none>";
            NoteCosmeticController.VerboseLog(
                $"Projectile: origin=({origin.x:F2},{origin.y:F2},{origin.z:F2}) aim=(" +
                $"{target.x:F2},{target.y:F2},{target.z:F2}) cam='{camName}'");
        }

        _noteColorFlipper++;
        var colorIndex = _noteColorFlipper & 1;
        var explodeColor = colorIndex == 0 ? _noteColorA : _noteColorB;

        var visual = CreateVisual(colorIndex, scaleOverride);
        visual.transform.SetPositionAndRotation(origin, Quaternion.identity);
        if (!visual.activeSelf)
            visual.SetActive(true);

        var behaviour = visual.GetComponent<ProjectileCube>() ?? visual.AddComponent<ProjectileCube>();
        var velocity = ComputeLaunchVelocity(origin, target, cfg, out var gravity, airTimeOverride);
        behaviour.Initialize(target, velocity, gravity, explodeColor, scaleOverride);

        Active.Add(visual);
    }

    /// <summary>
    /// Creates a standalone note/cube visual (no physics behaviour, not in the
    /// throw pool) for external consumers such as the cube-animation system.
    /// Mirrors the throw path's prefer-real-note-else-cube behaviour; callers
    /// own the returned object's lifetime and scale.
    /// </summary>
    internal static GameObject CreateStandaloneNoteVisual(int colorIndex)
    {
        return CreateVisual(colorIndex);
    }

    /// <summary>
    /// Prefers a real game-note visual scavenged from cached prefabs; falls
    /// back to the classic orange cube otherwise. Behaviour scripts and
    /// colliders are stripped from cloned notes - our physics is manual.
    /// Retired projectiles are recycled from the pool, keyed by visual kind,
    /// so a volley doesn't instantiate a dozen note clones in one frame.
    /// </summary>
    private static GameObject CreateVisual(int colorIndex, float? scaleOverride = null)
    {
        var poolKey = colorIndex == 0 ? "noteA" : "noteB";
        var scale = ResolveNoteScale(scaleOverride);
        EnsureNotePrefab();

        GameObject go = null!;
        // Live scene clones are only trusted once this color side has captured
        // real material blocks - cloning earlier grabs whatever pooled note is
        // lying around, often reset to black and/or a dot variant. Pristine
        // asset templates (N0-N3) are always safe to clone, which keeps menu
        // throws working before any map has been played.
        // In "one saber" maps only a single note color actually spawns, so only
        // one side ever captures. Treat both sides as "safe to clone" when either
        // side has captured, so the thrown blocks match the single spawning note
        // instead of one side becoming the plain cube.
        var hasLiveVisuals = _liveBlockMpbs[colorIndex] != null || _liveBlockMpbs[1 - colorIndex] != null;
        if (_noteVisualPrefab != null && (_prefabIsAssetTemplate || hasLiveVisuals))
        {
            var pooled = TakeFromPool(poolKey);
            if (pooled != null && pooled.name.EndsWith("-Cube", System.StringComparison.Ordinal))
            {
                // A stale cube can sit in a note pool if it was primed before any
                // note capture existed (e.g. one-saber maps where only one side is
                // ever captured). It is not a real note, so discard it and clone a
                // fresh note visual instead of throwing the placeholder.
                Object.Destroy(pooled);
                pooled = null;
            }
            if (pooled != null)
            {
                pooled.SetActive(true);
                if (ApplyNoteVisuals(pooled, colorIndex))
                {
                    return pooled;
                }
                // This pooled clone came from a stock source (cached while the
                // source was still a template) and is missing the outline the
                // snapshot expects. Do NOT run the expensive synchronously-on-
                // frame rebuild here - that spikes ~80ms and hitches the game.
                // Just discard the stale clone and fall through to the fast fresh
                // instantiate below, which (per profiling) is ~0ms and produces a
                // correct outline-bearing clone once the live-note source is set.
                Plugin.Log.Debug("Projectile visuals: pooled clone missing outline; discarding and re-cloning fresh.");
                Object.Destroy(pooled);
                pooled = null;
            }

            try
            {
                go = Object.Instantiate(_noteVisualPrefab);
                go.name = $"StreamReactiveProjectile-Note{(colorIndex == 0 ? "A" : "B")}";
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    Object.Destroy(mb);
                foreach (var col in go.GetComponentsInChildren<Collider>(true))
                    Object.Destroy(col);

                // Clones inherit the source's active state - cached prefabs are
                // usually deactivated, so force the clone awake or it sits there
                // frozen and invisible.
                go.SetActive(true);

                if (!ApplyNoteVisuals(go, colorIndex))
                {
                    // Stock template source lacks the outline the current preset
                    // needs. Fall back to a live-note source which carries it.
                    Object.Destroy(go);
                    return CreateNoteVisualFromLiveSource(colorIndex);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.Warn($"Projectile visuals: cloning note visual failed ({ex.Message}); using cube fallback.");
                if (go != null)
                    Object.Destroy(go);
                return CreateCube();
            }

            var hasRenderer = go.GetComponentInChildren<MeshRenderer>(true) != null;
            if (!hasRenderer)
            {
                // Bad candidate - don't trust it again this session.
                Object.Destroy(go);
                _noteVisualPrefab = null;
                Plugin.Log.Warn("Projectile visuals: cloned note had no renderer; discarding and falling back to cube.");
                return CreateCube();
            }
        }
        else
        {
            var pooledCube = TakeFromPool("cube");
            if (pooledCube != null)
            {
                pooledCube.SetActive(true);
                return pooledCube;
            }

            go = CreateCube();
        }

        return go;
    }

    /// <summary>
    /// Resolves the effective note/projectile scale multiplier for a throw,
    /// honoring a per-message websocket override when present. The configured
    /// in-game slider value is clamped to the settings range, but a websocket
    /// override is passed through unclamped (well, an absurd hard safety cap)
    /// so a trigger can throw notes/cubes at literally any size.
    /// </summary>
    internal static float ResolveNoteScale(float? overrideScale)
    {
        var cfg = PluginConfig.Instance;
        var configured = cfg?.ThrowNoteScale ?? 1f;
        if (overrideScale.HasValue)
            return Mathf.Clamp(overrideScale.Value, 0.001f, 100000f);
        return Mathf.Clamp(configured, 0.2f, 3f);
    }

    /// <summary>
    /// Builds a projectile visual from a live-note source that carries the
    /// outline child the current preset needs, used when the cached prefab /
    /// pool turned out to be a stock template lacking it. Re-scans preferring
    /// live clones and re-attempts the normal clone path; degrades to the cube
    /// if no live source is available (so a fallback still throws).
    /// </summary>
    private static GameObject CreateNoteVisualFromLiveSource(int colorIndex)
    {
        try
        {
            _noteVisualPrefab = null;
            ScanForNotePrefab();
            if (_noteVisualPrefab == null || _prefabIsAssetTemplate)
            {
                _noteVisualPrefab = null;
                return CreateCube();
            }

            var go = Object.Instantiate(_noteVisualPrefab);
            go.name = $"StreamReactiveProjectile-Note{(colorIndex == 0 ? "A" : "B")}";
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                Object.Destroy(mb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Object.Destroy(col);
            go.SetActive(true);

            if (!ApplyNoteVisuals(go, colorIndex))
            {
                Object.Destroy(go);
                _noteVisualPrefab = null;
                return CreateCube();
            }

            if (go.GetComponentInChildren<MeshRenderer>(true) == null)
            {
                Object.Destroy(go);
                _noteVisualPrefab = null;
                return CreateCube();
            }

            return go;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Warn($"Projectile visuals: live-source rebuild failed ({ex.Message}); using cube fallback.");
            if (_noteVisualPrefab != null)
                _noteVisualPrefab = null;
            return CreateCube();
        }
    }

    private static GameObject CreateCube()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "StreamReactiveProjectile-Cube";
        go.transform.localScale = new Vector3(CubeSize, CubeSize, CubeSize);
        go.GetComponent<MeshRenderer>().sharedMaterial = GetOrangeMaterial();
        return go;
    }

    private static Material GetOrangeMaterial()
    {
        if (_orangeMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default");
            _orangeMaterial = new Material(shader != null ? shader : Shader.Find("Hidden/Internal-Colored"))
            {
                color = new Color(1f, 0.45f, 0.1f, 1f)
            };
        }

        return _orangeMaterial;
    }

    // Live note property blocks captured from real spawned notes right after
    // the game initializes them - exact shaders, textures and colors, no
    // guessing. Stored per scheme side (red/blue) so projectiles can alternate.
    private static readonly MaterialPropertyBlock?[] _liveBlockMpbs = new MaterialPropertyBlock?[2];
    private static readonly MaterialPropertyBlock?[] _liveArrowMpbs = new MaterialPropertyBlock?[2];
    // Live note material instances captured alongside the MPBs. NoteTweaks (and
    // similar) push most of their look into per-note material state (textures,
    // tint, outline params) that an MPB replay alone cannot carry, so we snapshot
    // the actual source material so the throw can instance-copy it onto the clone.
    private static readonly Material?[] _liveBlockMats = new Material?[2];
    private static readonly Material?[] _liveArrowMats = new Material?[2];
    // Full per-renderer snapshots in child order from a live note: (instanced
    // material, property block). Replayed against the clone's renderers in the
    // same order so every element - block, arrow, outline, glow - keeps its own
    // material AND its own colored property block (flat-black presets rely on a
    // separately-colored outline, which a single block/arrow split always loses).
    private static readonly List<(Material mat, MaterialPropertyBlock mpb)>?[] _liveRenderers = new List<(Material, MaterialPropertyBlock)>?[2];
    // Tracks whether the captured snapshot of each color included an outline
    // renderer. Used to re-capture if an earlier (too-early) snapshot missed the
    // outline but a later live note has one.
    private static readonly bool[] _liveHasOutline = new bool[2];
    private static bool _dumpedShaderProps;

    internal static void ResetCapturedLiveNoteMaterials()
    {
        _liveBlockMpbs[0] = null;
        _liveBlockMpbs[1] = null;
        _liveArrowMpbs[0] = null;
        _liveArrowMpbs[1] = null;
        _liveBlockMats[0] = null;
        _liveBlockMats[1] = null;
        _liveArrowMats[0] = null;
        _liveArrowMats[1] = null;
        _liveRenderers[0] = null;
        _liveRenderers[1] = null;
        _liveHasOutline[0] = false;
        _liveHasOutline[1] = false;
    }

    /// <summary>
    /// Pre-warms the projectile visual source so the first throw never pays for
    /// the expensive scene-wide prefab scan (+ color capture) on its own frame.
    /// Called from the note-init coroutine during map load, where the frame cost
    /// is spread out and unobservable, instead of synchronously on the first
    /// throw (which caused a multi-frame hitch). Safe to call repeatedly: the
    /// scan only actually runs when the prefab is unset or a modded-rescan is
    /// still pending.
    /// </summary>
    internal static void WarmNotePrefab()
    {
        try
        {
            EnsureNotePrefab();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile visuals: warm prefab scan failed: {ex.Message}");
        }
    }

    internal static void CaptureLiveNoteMaterials(NoteController controller)
    {
        if (controller == null)
            return;

        try
        {
            var colorIndex = (int)controller.noteData.colorType;
            if (colorIndex < 0 || colorIndex > 1)
                return;

            var blockTransform = NoteCosmeticController.FindPreferredNoteBlockTransform(controller.transform);
            var block = blockTransform != null ? blockTransform.GetComponent<MeshRenderer>() : null;

            if (block == null)
                return;

            // Snapshot every visible renderer (block, arrow, outline, glow) in
            // child order with each one's material and property block. This is
            // what lets flat-black / outline-based presets carry their color: the
            // block itself may be black while the OUTLINE renderer holds the
            // per-note color, so we must judge readiness across all renderers,
            // not just the block.
            var renderers = new List<(Material, MaterialPropertyBlock)>();
            var anyVisible = false;
            var hasOutline = false;
            foreach (var r in controller.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.sharedMaterial == null || r.GetComponent<MeshFilter>()?.sharedMesh == null)
                    continue;
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                if (HasVisibleNoteColor(mpb, r.sharedMaterial))
                    anyVisible = true;
                if (IsOutlineRenderer(r.gameObject))
                    hasOutline = true;
                renderers.Add((r.sharedMaterial, mpb));
            }

            if (!anyVisible || renderers.Count == 0)
            {
                // The note's color state isn't ready yet (e.g. just spawned, or a
                // preset whose colors are applied a frame later). Keep whatever we
                // already captured for this side - it matches the last known good
                // state - but DON'T let a stale snapshot survive a real change
                // below. Just skip this init and try the next note.
                Plugin.Log.Debug(
                    $"Projectile visuals: skipping premature capture for color {(colorIndex == 0 ? "A" : "B")} " +
                    $"from '{controller.name}/{block.name}' until its color state is ready.");
                return;
            }

            var blockMpb = new MaterialPropertyBlock();
            block.GetPropertyBlock(blockMpb);

            var currentBlockMat = block.sharedMaterial;
            var alreadyCaptured = _liveBlockMpbs[colorIndex] != null;
            var materialChanged = alreadyCaptured
                && !ReferenceEquals(_liveBlockMats[colorIndex], currentBlockMat);
            var colorChanged = alreadyCaptured && _liveBlockMpbs[colorIndex] != null && !MpbColorMatches(
                _liveBlockMpbs[colorIndex]!, _liveBlockMats[colorIndex], blockMpb, currentBlockMat);
            // A snapshot that was taken a hair too early can be missing the
            // outline (added a frame or two after init). If we now have an
            // outline we didn't have before, re-capture so it isn't lost.
            var outlineNowPresent = alreadyCaptured && hasOutline && !_liveHasOutline[colorIndex];

            // Re-capture when the source material OR its color changed (a new
            // preset/color was applied mid-session - e.g. changed in player
            // settings or a map overrides them), or when an outline appeared
            // that the earlier snapshot was missing. Without this the snapshot
            // freezes at whatever the game first loaded, which is exactly the
            // desync described: thrown blocks keep the old color after the
            // player changes it. Comparing the current block color catches pure
            // color edits that reuse the same material instance.
            if (!alreadyCaptured || materialChanged || colorChanged || outlineNowPresent)
            {
                MaterialPropertyBlock? arrowMpb = null;
                Material? arrowMat = null;
                foreach (var r in controller.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.sharedMaterial == null || r.GetComponent<MeshFilter>()?.sharedMesh == null)
                        continue;
                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);
                    if (arrowMpb == null && r != block)
                    {
                        arrowMpb = mpb;
                        arrowMat = r.sharedMaterial;
                    }
                }

                _liveBlockMpbs[colorIndex] = blockMpb;
                _liveArrowMpbs[colorIndex] = arrowMpb;
                _liveBlockMats[colorIndex] = currentBlockMat;
                _liveArrowMats[colorIndex] = arrowMat;
                _liveRenderers[colorIndex] = renderers;
                _liveHasOutline[colorIndex] = hasOutline;

                NoteCosmeticController.VerboseLog(
                    $"Projectile visuals: {(alreadyCaptured ? "re-" : "")}captured live note visuals for color {(colorIndex == 0 ? "A" : "B")} " +
                    $"(block '{currentBlockMat?.name}', arrow block {(arrowMpb != null ? "yes" : "no")}).");

                // Switch the projectile visual source to this live note now, during
                // an ordinary note-init frame, so the first miss/throw never triggers
                // the expensive scene-wide prefab rescan itself.
                TryUpgradePrefabFromLiveNote(controller, colorIndex);
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile visuals: live material capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Colors the cloned note. Preferred path: replay the captured live
    /// MaterialPropertyBlocks for the requested scheme side (exactly what the
    /// game does, per red/blue). Fallback: write player-matched colors into
    /// every color-ish material property, and dump the shader's properties
    /// once so failures are diagnosable from the log.
    /// </summary>
    /// <returns>False if the snapshot expects an outline renderer but the clone
    /// has none (the clone was made from a stock source that lacks the outline
    /// child). The caller should re-build from a live-note source; the outlines
    /// live on a separate child that only NoteTweaks-decorated notes carry.</returns>
    private static bool ApplyNoteVisuals(GameObject go, int colorIndex)
    {
        // Preferred: full per-renderer replay of a live note's materials and
        // property blocks in child order. This preserves every element of the
        // source note - block, arrow, outline, glow - with each keeping its own
        // material AND its own colored block, which is exactly what flat-black /
        // outline-based NoteTweaks presets need. Falls back to the block/arrow
        // split below if no live renderer snapshot exists (e.g. menu templates).
        var snapshot = _liveRenderers[colorIndex] ?? _liveRenderers[1 - colorIndex];
        if (snapshot != null && snapshot.Count > 0)
        {
            var cloneRenderers = new List<MeshRenderer>();
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.sharedMaterial != null && r.GetComponent<MeshFilter>()?.sharedMesh != null)
                    cloneRenderers.Add(r);
            }

            if (cloneRenderers.Count > 0)
            {
                // Outline presence is decided by which renderer children the clone
                // actually has. When the snapshot carries an outline but this clone
                // was built from a stock source, the outline child is absent and no
                // amount of material replay can conjure it - report it so the caller
                // can re-build from a live-note source that has one.
                var outlineExpected = snapshotHasOutline(snapshot);
                var outlinePresent = cloneHasOutline(go);
                if (outlineExpected && !outlinePresent)
                {
                    Plugin.Log.Debug(
                        $"Projectile visuals: clone '{go.name}' missing outline renderer; " +
                        $"requesting re-build from live source (color {(colorIndex == 0 ? "A" : "B")}).");
                    return false;
                }

                var n = Mathf.Min(cloneRenderers.Count, snapshot.Count);
                for (int i = 0; i < n; i++)
                {
                    var (srcMat, srcMpb) = snapshot[i];
                    var renderer = cloneRenderers[i];
                    // Fresh instance so we never share state with or mutate a real
                    // note; then replay that renderer's own property block on top.
                    renderer.sharedMaterial = new Material(srcMat);
                    renderer.SetPropertyBlock(srcMpb);
                }
                // Any extra clone renderers we couldn't snap for get the last
                // block applied so they never render with a flat stock material.
                if (cloneRenderers.Count > snapshot.Count)
                {
                    var (lastMat, lastMpb) = snapshot[snapshot.Count - 1];
                    for (int i = snapshot.Count; i < cloneRenderers.Count; i++)
                    {
                        var renderer = cloneRenderers[i];
                        renderer.sharedMaterial = new Material(lastMat);
                        renderer.SetPropertyBlock(lastMpb);
                    }
                }
                return true;
            }
        }

        MeshRenderer? blockRenderer = null;
        var bestVerts = 0;
        var others = new List<MeshRenderer>();

        var preferredBlock = NoteCosmeticController.FindPreferredNoteBlockTransform(go.transform);
        if (preferredBlock != null)
            blockRenderer = preferredBlock.GetComponent<MeshRenderer>();

        foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = renderer.GetComponent<MeshFilter>();
            if (mf?.sharedMesh == null || renderer.sharedMaterial == null)
                continue;

            if (renderer == blockRenderer)
            {
                bestVerts = mf.sharedMesh.vertexCount;
                continue;
            }

            if (blockRenderer == null && mf.sharedMesh.vertexCount > bestVerts)
            {
                bestVerts = mf.sharedMesh.vertexCount;
                if (blockRenderer != null)
                    others.Add(blockRenderer);
                blockRenderer = renderer;
            }
            else
            {
                others.Add(renderer);
            }
        }

        // In "one saber" maps only a single note color actually spawns, so only
        // that color's live MaterialPropertyBlock ever gets captured. Both projectile
        // sides must still look like notes, so fall back to the other color's capture
        // whenever the requested side has none yet.
        var blockMpb = _liveBlockMpbs[colorIndex] ?? _liveBlockMpbs[1 - colorIndex];
        var arrowMpb = _liveArrowMpbs[colorIndex] ?? _liveArrowMpbs[1 - colorIndex];
        var blockMat = _liveBlockMats[colorIndex] ?? _liveBlockMats[1 - colorIndex];
        var arrowMat = _liveArrowMats[colorIndex] ?? _liveArrowMats[1 - colorIndex];

        if (blockMat != null && blockRenderer != null)
        {
            // Instance-copy the live note's material so the full NoteTweaks/Vivify
            // material look (textures, tint, outline params) transfers. We clone a
            // fresh instance so the projectile never shares state with, or risks
            // touching, a real note on the field; the shared material itself stays
            // untouched. The MPB replay below still applies the correct red/blue.
            blockRenderer.sharedMaterial = new Material(blockMat);
            foreach (var other in others)
            {
                if (arrowMat != null)
                    other.sharedMaterial = new Material(arrowMat);
                else
                    other.sharedMaterial = new Material(blockMat);
            }
        }

        if (blockMpb != null && blockRenderer != null)
        {
            blockRenderer.SetPropertyBlock(blockMpb);
            foreach (var other in others)
            {
                if (arrowMpb != null)
                    other.SetPropertyBlock(arrowMpb);
            }
            return true;
        }

        // Fallback: manual colors.
        var color = colorIndex == 0 ? _noteColorA : _noteColorB;

        var targets = new List<MeshRenderer>(others.Count + 1);
        if (blockRenderer != null) targets.Add(blockRenderer);
        targets.AddRange(others);

        foreach (var renderer in targets)
        {
            var mat = renderer.sharedMaterial;
            if (mat == null)
                continue;

            DumpShaderPropertiesOnce(mat);

            var mpb = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb);
            foreach (var propName in new[] { "_Color", "_SimpleColor", "_BaseColor", "_BlockColor" })
            {
                if (mat.HasProperty(propName))
                    mpb.SetColor(propName, color);
            }
            renderer.SetPropertyBlock(mpb);
        }
        return true;
    }

    // True if any snapshot renderer material/name indicates an outline shell.
    private static bool snapshotHasOutline(List<(Material mat, MaterialPropertyBlock mpb)> snapshot)
    {
        foreach (var (m, _) in snapshot)
        {
            if (m != null && m.name.IndexOf("Outline", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // True if the given clone already contains an outline renderer child.
    private static bool cloneHasOutline(GameObject go)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (IsOutlineRenderer(r.gameObject))
                return true;
        }
        return false;
    }

    private static void DumpShaderPropertiesOnce(Material mat)
    {
        if (_dumpedShaderProps)
            return;
        _dumpedShaderProps = true;

        try
        {
            var shader = mat.shader;
            var sb = new System.Text.StringBuilder("Projectile visuals: fallback material '")
                .Append(mat.name).Append("' shader '").Append(shader.name).Append("' color props:");
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color)
                    continue;
                var propName = shader.GetPropertyName(i);
                sb.Append(' ').Append(propName).Append('=').Append(mat.GetColor(propName));
            }
            Plugin.Log.Info(sb.ToString());
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile visuals: shader dump failed: {ex.Message}");
        }
    }

    private static Vector3 ResolveOrigin(string mode)
    {
        var cam = CapsuleGuardController.GetViewCamera();
        var headPos = cam != null ? cam.transform.position : Vector3.zero;
        var flatForward = Vector3.zero;
        if (cam != null)
        {
            var f = cam.transform.forward;
            f.y = 0f;
            // Preserve the last good horizon forward if the camera is looking
            // almost straight up/down (flattened forward ~ zero -> cross with
            // up is unstable and throws scatter randomly).
            if (f.sqrMagnitude > 0.01f)
            {
                flatForward = f.normalized;
                _lastGoodFlatForward = flatForward;
            }
            else
            {
                flatForward = _lastGoodFlatForward;
            }
        }
        else
        {
            flatForward = _lastGoodFlatForward;
        }
        var right = Vector3.Cross(Vector3.up, flatForward);

        if (string.Equals(mode, "Random Around", System.StringComparison.OrdinalIgnoreCase))
        {
            var origin = headPos + Random.onUnitSphere * SpawnDistance;
            if (origin.y < headPos.y - 1.5f)
                origin.y = headPos.y - 1.5f;
            return origin;
        }

        if (string.Equals(mode, "Front Center", System.StringComparison.OrdinalIgnoreCase))
        {
            return headPos
                + flatForward * SpawnDistance
                + right * Random.Range(-0.6f, 0.6f)
                + Vector3.up * Random.Range(-0.2f, 0.4f);
        }

        // Horizon Front ("Far Away Front"): a single static point centered and
        // far out in front of the player, so notes appear to arrive from the
        // distance. Slightly elevated so the lofted arc reads as coming off the
        // horizon rather than out of the floor.
        if (string.Equals(mode, "Horizon Front", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Far Away Front", System.StringComparison.OrdinalIgnoreCase))
        {
            return headPos
                + flatForward * HorizonDistance
                + Vector3.up * 1.5f;
        }

        // Default: Twin Cannons - two static points ahead, alternating sides.
        _twinSideFlipper++;
        var side = (_twinSideFlipper & 1) == 0 ? -1f : 1f;
        return headPos
            + flatForward * SpawnDistance
            + right * (side * TwinOffset)
            + Vector3.up * Random.Range(-0.1f, 0.2f);
    }

    private static bool IsZeroG(PluginConfig cfg) =>
        string.Equals(cfg.ThrowTrajectory, "Zero-G", System.StringComparison.OrdinalIgnoreCase);

    private static Vector3 ComputeLaunchVelocity(Vector3 origin, Vector3 target, PluginConfig cfg, out float effectiveGravity, float? airTimeOverride = null)
    {
        var toTarget = target - origin;

        if (IsZeroG(cfg))
        {
            // Straight-line pinball shots; gravity stays off for these cubes.
            effectiveGravity = 0f;
            var distance = toTarget.magnitude;
            var direction = distance > 0.0001f ? toTarget / distance : Vector3.forward;
            return direction * Mathf.Clamp(cfg.ThrowSpeed, 3f, 30f);
        }

        // Lofted: pick the flight time (the "arc"), then solve the exact launch
        // velocity so the projectile still arrives at the aim point under the
        // configured gravity. Short air time = flat laser, long = high lob.
        var gravity = Mathf.Clamp(cfg.ThrowGravity, 0f, 20f);
        effectiveGravity = gravity;

        // A websocket "arc"/time override wins over the config value (which is
        // clamped to the slider range). The override is passed through with a
        // loose clamp so a trigger can request a dramatically flatter/loftier arc.
        float airTime;
        if (airTimeOverride.HasValue)
            airTime = Mathf.Max(0.05f, airTimeOverride.Value);
        else
            airTime = Mathf.Max(0.05f, Mathf.Clamp(cfg.ThrowAirTime, 0.4f, 20f));

        var flat = toTarget;
        flat.y = 0f;
        var flatDistance = flat.magnitude;
        var flatDirection = flatDistance > 0.0001f ? flat / flatDistance : Vector3.forward;

        var horizontalVelocity = flatDirection * (flatDistance / airTime);
        var verticalVelocity = toTarget.y / airTime + 0.5f * gravity * airTime;
        return horizontalVelocity + Vector3.up * verticalVelocity;
    }

    private static void CleanupDead()
    {
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            if (Active[i] == null)
                Active.RemoveAt(i);
        }
    }
}

/// <summary>
/// Per-cube behaviour: integrates motion manually, reflects off the guard
/// capsule segment and the estimated floor, despawns after enough bounces,
/// falling out of range, or its lifetime expiring.
/// </summary>
internal sealed class ProjectileCube : MonoBehaviour
{
    private const float CapsuleTangentialDamping = 0.7f;
    // Random tilt applied to the bounce normal so repeated hits don't all
    // follow the identical return path and pile up in the same spot.
    private const float CapsuleBounceScatter = 0.12f;
    private const int MaxCapsuleBounces = 8;
    private const float DefaultLifetimeSeconds = 14f;
    private const float KillDepthBelowFloor = 8f;
    private const float CubeSize = 0.18f;

    // Finite platform-sized floor: cubes past these half-extents fall off the edge
    // (defaults; overridden by ThrowFloorWidth/Depth config at spawn).
    private const float DefaultFloorWidth = 3f;
    private const float DefaultFloorDepth = 3f;

    private Vector3 _velocity;
    private float _gravity = 4.5f;
    private float _floorRestitution = 0.45f;
    private float _capsuleRestitution = 0.45f;
    private float _floorFriction = 5f;
    private float _elapsed;
    private int _capsuleBounces;
    private float _floorY;
    private Vector2 _floorCenterXZ;
    private float _floorHalfExtentX;
    private float _floorHalfExtentZ;
    private Vector3 _spinAxis = Vector3.up;
    private float _spinSpeed;
    private Transform _transform = null!;
    private Vector3 _prevPosition;

    // Explosion colour (for thrown blocks) and whether this projectile should
    // burst into particles when it dies. Emote projectiles leave this false.
    private bool _isBlock;
    private Color _explodeColor = Color.white;
    private bool _exploded;

    // End-of-life shrink-out ("dissolve") state.
    private bool _dying;
    private float _deathTimer;
    private float _fadeSeconds = 0.5f;
    private float _lifetimeSeconds = DefaultLifetimeSeconds;
    private Vector3 _originalScale;

    // The projectile's unscaled shape size (full note size for note clones,
    // CubeSize for cubes). Captured on first spawn and kept across pool reuse so
    // per-throw scale multipliers can be applied fresh each time.
    private Vector3 _baseScale = default;

    // Grow-in at the start of a throw: projectiles pop in at zero scale and
    // expand to full size over this many seconds (mirrors the end-of-life
    // shrink-out, in reverse).
    private float _growSeconds = 0.25f;
    private float _growTimer;
    private bool _growing = true;

    // When true, collision uses the projectile's scaled size (so a huge note
    // visibly bumps the capsule from the right distance) instead of the fixed
    // small CubeSize radius.
    private bool _matchCollisionScale;

    public void Initialize(Vector3 target, Vector3 initialVelocity, float gravity, Color? explodeColor = null, float? scaleOverride = null)
    {
        _isBlock = explodeColor.HasValue;
        _explodeColor = explodeColor ?? Color.white;

        var cfg = PluginConfig.Instance;
        _fadeSeconds = Mathf.Clamp(cfg?.ThrowFadeSeconds ?? 0.5f, 0f, 3f);
        _lifetimeSeconds = Mathf.Clamp(cfg?.ThrowLifetime ?? DefaultLifetimeSeconds, 1f, 120f);
        _matchCollisionScale = cfg?.ThrowMatchCollisionScale ?? false;
        _dying = false;
        _elapsed = 0f;
        _capsuleBounces = 0;
        _exploded = false;
        _prevPosition = transform.position;

        // Capture the unscaled base shape size once (first spawn), then apply
        // the configured/per-throw scale multiplier on top each time the pooled
        // projectile is reused.
        if (_baseScale == default)
            _baseScale = transform.localScale;

        var scaleMult = ProjectileThrower.ResolveNoteScale(scaleOverride);
        _originalScale = _baseScale * scaleMult;

        // Start every throw from zero scale and grow to full size, so pooled
        // projectiles that were shrunk away on retirement arrive with a visible
        // pop-in instead of popping in at nothing (or at a leftover shrink size).
        _growing = true;
        _growTimer = _growSeconds;
        transform.localScale = Vector3.zero;

        _velocity = initialVelocity;
        _gravity = Mathf.Max(0f, gravity);

        // Bounce floor is platform-centered: world origin, surface at y=0.
        _floorY = 0f;
        _floorCenterXZ = Vector2.zero;

        if (cfg != null)
        {
            _floorRestitution = Mathf.Clamp(cfg.ThrowBounciness, 0f, 0.95f);
            _capsuleRestitution = Mathf.Clamp(cfg.ThrowCapsuleBounciness, 0f, 0.95f);
            _floorFriction = Mathf.Max(0f, cfg.ThrowFloorFriction);
            _floorHalfExtentX = Mathf.Clamp(cfg.ThrowFloorWidth * 0.5f, 0.5f, 20f);
            _floorHalfExtentZ = Mathf.Clamp(cfg.ThrowFloorDepth * 0.5f, 0.5f, 20f);
        }
        else
        {
            _floorHalfExtentX = DefaultFloorWidth * 0.5f;
            _floorHalfExtentZ = DefaultFloorDepth * 0.5f;
        }
        _spinAxis = Random.onUnitSphere;
        _spinSpeed = Random.Range(120f, 260f);

        // Point the cube at what it is flying towards initially.
        var toTarget = target - transform.position;
        if (toTarget.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(toTarget.normalized);
    }

    private void Awake() => _transform = transform;

    private void Update()
    {
        var dt = Mathf.Min(Time.deltaTime, 0.05f);

        if (_dying)
        {
            _deathTimer -= dt;
            var k = _fadeSeconds > 0.001f ? Mathf.Clamp01(_deathTimer / _fadeSeconds) : 0f;
            // Ease-in so the shrink starts gently and finishes decisively.
            _transform.localScale = _originalScale * (k * k);
            if (_spinSpeed > 1f)
                _transform.Rotate(_spinAxis, _spinSpeed * dt, Space.Self);

            if (_deathTimer <= 0f)
                FinalizeDeath();
            return;
        }

        _elapsed += dt;
        if (_elapsed >= _lifetimeSeconds)
        {
            Expire();
            return;
        }

        // Grow-in at the start of the throw: expand from zero to full size over
        // _growSeconds (ease-out so it starts fast and settles gently).
        if (_growing)
        {
            _growTimer -= dt;
            var g = _growSeconds > 0.001f ? Mathf.Clamp01(1f - _growTimer / _growSeconds) : 1f;
            _transform.localScale = _originalScale * (1f - (1f - g) * (1f - g));
            if (_growTimer <= 0f)
            {
                _growing = false;
                _transform.localScale = _originalScale;
            }
        }

        _velocity.y -= _gravity * dt;
        _prevPosition = _transform.position;
        _transform.position += _velocity * dt;

        TryBounceOffCapsule();
        TryBounceOffFloor(dt);

        if (_transform.position.y < _floorY - KillDepthBelowFloor)
        {
            Expire();
            return;
        }

        if (_spinSpeed > 1f)
        {
            _transform.Rotate(_spinAxis, _spinSpeed * dt, Space.Self);
            _spinSpeed *= 1f - 0.6f * dt;
        }
    }

    private void TryBounceOffCapsule()
    {
        var collider = CapsuleGuardController.ActiveCollider;
        if (collider == null || !collider.enabled || _capsuleBounces >= MaxCapsuleBounces)
            return;

        var center = collider.transform.TransformPoint(collider.center);
        var radius = collider.radius * MaxAbsScale(collider.transform);
        var halfHeight = collider.height * 0.5f * MaxAbsScale(collider.transform);
        var cylinderHalf = Mathf.Max(0f, halfHeight - radius);

        var up = collider.transform.up;
        var pointA = center + up * cylinderHalf;
        var pointB = center - up * cylinderHalf;

        var combinedRadius = radius + CollisionRadius();

        // Swept test against this frame's movement so fast shots can't tunnel
        // straight through the capsule between frames (which made hits — and
        // thus the explosion — occasionally fail to register).
        var moveStart = _prevPosition;
        var moveEnd = _transform.position;
        var distSqr = ClosestPointsSegmentSegmentSqr(moveStart, moveEnd, pointA, pointB,
            out var blockContact, out var capContact);

        if (distSqr > combinedRadius * combinedRadius)
            return;

        NoteCosmeticController.VerboseLog(
            $"Projectile: capsule contact at ({_transform.position.x:F2},{_transform.position.y:F2},{_transform.position.z:F2}).");

        var offset = blockContact - capContact;
        var normal = offset.sqrMagnitude > 0.000001f
            ? offset.normalized
            : (-_velocity).normalized;
        if (normal.sqrMagnitude < 0.5f)
            normal = Vector3.up;

        // Tiny random deviation so volleys fan out instead of retracing the
        // exact same trajectory every time.
        normal = (normal + Random.onUnitSphere * CapsuleBounceScatter).normalized;

        // Split into normal + tangential parts and damp both, so bounces
        // visibly lose energy instead of reflecting at near-full speed.
        var normalComponent = Vector3.Dot(_velocity, normal) * normal;
        var tangentComponent = _velocity - normalComponent;
        _velocity = tangentComponent * CapsuleTangentialDamping - normalComponent * _capsuleRestitution;
        if (_capsuleRestitution < 0.05f)
            _velocity *= 0.2f; // dead stop: kill the leftover slide too
        _transform.position = capContact + normal * (combinedRadius + 0.02f);

        _capsuleBounces++;
        _spinSpeed = Random.Range(120f, 260f);
        _spinAxis = Random.onUnitSphere;

        // Bonk on capsule contact — plays regardless of the explosion setting.
        var hitCfg = PluginConfig.Instance;
        if (hitCfg != null)
            SoundManager.Play(hitCfg.ThrowHitSoundFile, hitCfg.ThrowHitSoundVolume);

        // Burst into particles and despawn the first time a block strikes the
        // capsule (only when the explosion feature is enabled). Other blocks
        // keep bouncing as before.
        if (_isBlock && !_exploded)
        {
            _exploded = true;
            var cfg = PluginConfig.Instance;
            if (cfg != null && cfg.ThrowExplodeEnabled)
            {
                NoteCosmeticController.VerboseLog("Projectile: explode branch reached, spawning explosion.");
                SpawnExplosion();
                ExpireQuick();
            }
            else
            {
                NoteCosmeticController.VerboseLog(
                    $"Projectile: hit capsule but explode NOT firing (isBlock={_isBlock}, enabled={(cfg != null && cfg.ThrowExplodeEnabled)}).");
            }
        }
    }

    /// <summary>
    /// Closest points between two segments and their squared distance. Used for
    /// swept capsule collision so fast projectiles can't pass through in one step.
    /// c1 lies on segment p1->q1 (the projectile path), c2 on p2->q2 (capsule core).
    /// </summary>
    private static float ClosestPointsSegmentSegmentSqr(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        const float eps = 1e-6f;
        float s, t;

        if (a <= eps && e <= eps)
        {
            s = 0f; t = 0f;
        }
        else if (a <= eps)
        {
            s = 0f;
            t = Mathf.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= eps)
            {
                t = 0f;
                s = Mathf.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > eps ? Mathf.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
        return (c1 - c2).sqrMagnitude;
    }

    private void SpawnExplosion()
    {
        try
        {
            var cfg = PluginConfig.Instance;
            if (cfg == null || !cfg.ThrowExplodeEnabled)
                return;

            var count = Mathf.Clamp(cfg.ThrowExplodeCount, 0, 5000);
            if (count <= 0)
                return;

            var color = cfg.ThrowExplodeUseNoteColor ? _explodeColor : cfg.ThrowExplodeColor;
            ParticleSpawner.SpawnParticles(_transform.position, count, color,
                cfg.ThrowExplodeScale, cfg.ThrowExplodeLifetime, cfg.ThrowExplodeSpeed);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.Debug($"Projectile explosion failed: {ex.Message}");
        }
    }

    private void TryBounceOffFloor(float dt)
    {
        var position = _transform.position;
        var floorContactY = _floorY + CollisionRadius();

        if (position.y > floorContactY)
            return;

        // Finite platform: cubes past the edge just keep falling.
        if (Mathf.Abs(position.x - _floorCenterXZ.x) > _floorHalfExtentX
            || Mathf.Abs(position.z - _floorCenterXZ.y) > _floorHalfExtentZ)
            return;

        position.y = floorContactY;
        _transform.position = position;

        if (_velocity.y < 0f)
            _velocity.y = -_velocity.y * _floorRestitution;
        // No tangential damping on floor contact on purpose: Floor Friction
        // alone governs sliding so the slider has a visible effect.

        // Rolling friction so cubes stop instead of sliding forever.
        var horizontal = new Vector3(_velocity.x, 0f, _velocity.z);
        var speed = horizontal.magnitude;
        if (speed > 0.01f)
        {
            var newSpeed = Mathf.Max(0f, speed - _floorFriction * dt);
            horizontal *= newSpeed / speed;
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;
        }

        _spinSpeed *= 1f - 3f * dt;
    }

    private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        var ab = b - a;
        var lengthSqr = ab.sqrMagnitude;
        if (lengthSqr < 0.000001f)
            return a;

        var t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSqr);
        return a + ab * t;
    }

    private static float MaxAbsScale(Transform t)
    {
        return Mathf.Max(Mathf.Abs(t.lossyScale.x),
            Mathf.Max(Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z)));
    }

    /// <summary>
    /// Effective collision radius in meters. When "match collision size" is on,
    /// this tracks the projectile's scaled dimensions so a huge note actually
    /// bumps the capsule/floor from the right distance; otherwise it uses the
    /// fixed small cube radius regardless of visual scale.
    /// </summary>
    private float CollisionRadius()
    {
        var baseR = CubeSize * 0.6f;
        if (!_matchCollisionScale)
            return baseR;
        var full = _originalScale;
        // Half the largest scaled dimension, floored at the small-cube radius so
        // a scaled-down note never tunnels into the floor.
        var scaled = Mathf.Max(Mathf.Abs(full.x), Mathf.Max(Mathf.Abs(full.y), Mathf.Abs(full.z))) * 0.5f;
        return Mathf.Max(baseR, scaled);
    }

    private void Expire()
    {
        // Free the spawn slot immediately, then shrink out over the
        // configured fade time (0 = pop out of existence instantly).
        ProjectileThrower.NotifyDestroyed(gameObject);

        if (_fadeSeconds <= 0.001f)
        {
            FinalizeDeath();
            return;
        }

        _dying = true;
        _deathTimer = _fadeSeconds;
        _velocity = Vector3.zero;
        if (_spinSpeed < 30f)
            _spinSpeed = Random.Range(60f, 140f); // gentle tumble while dissolving
    }

    /// <summary>
    /// Like <see cref="Expire"/> but with a quick shrink-out, used when a
    /// block bursts on capsule contact so it pops away fast instead of
    /// lingering through a long fade.
    /// </summary>
    private void ExpireQuick()
    {
        ProjectileThrower.NotifyDestroyed(gameObject);

        if (_fadeSeconds <= 0.001f)
        {
            FinalizeDeath();
            return;
        }

        _dying = true;
        _deathTimer = Mathf.Min(_fadeSeconds, 0.2f);
        _velocity = Vector3.zero;
        if (_spinSpeed < 30f)
            _spinSpeed = Random.Range(60f, 140f);
    }

    private void FinalizeDeath()
    {
        ProjectileThrower.Retire(gameObject);
    }
}

internal sealed class EmoteRainDrop : MonoBehaviour
{
    private float _fallSpeed = 2f;
    private float _lifetime = 5f;
    private bool _bounce = true;
    private const float MaxTumble = 60f;
    private const float KillDepth = 12f;
    private const float FloorY = 0f;
    private const float FloorHalfHeight = 0.2f;
    private const float Gravity = 9f;

    private float _elapsed;
    private Vector3 _spinAxis;
    private float _spinSpeed;
    private Vector3 _originalScale;
    private Vector3 _velocity;
    private Transform _transform = null!;

    private void Awake()
    {
        _transform = transform;
        _originalScale = _transform.localScale;
        var cfg = PluginConfig.Instance;
        _fallSpeed = cfg?.EmoteRainFallSpeed ?? 2f;
        _lifetime = cfg?.EmoteRainLifetime ?? 5;
        _bounce = cfg?.EmoteRainBounce ?? true;
        _velocity = Vector3.down * _fallSpeed;
        _spinAxis = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f)).normalized;
        _spinSpeed = UnityEngine.Random.Range(20f, MaxTumble);
    }

    private void Update()
    {
        var dt = Mathf.Min(Time.deltaTime, 0.05f);
        _elapsed += dt;

        if (_elapsed >= _lifetime || _transform.position.y < -KillDepth)
        {
            Object.Destroy(gameObject);
            return;
        }

        // With bouncing on, gravity makes impacts and rebounds look natural;
        // with it off the emote just falls at a constant speed.
        if (_bounce)
            _velocity.y -= Gravity * dt;

        _transform.position += _velocity * dt;
        if (_bounce)
            TryBounceOffFloor(dt);

        _transform.Rotate(_spinAxis, _spinSpeed * dt, Space.Self);

        // Gentle fade in the last second
        if (_elapsed > _lifetime - 1f)
        {
            var fade = Mathf.Clamp01(_lifetime - _elapsed);
            _transform.localScale = _originalScale * fade;
        }
    }

    // Rain emotes land on the platform surface (y = 0) and ricochet/bounce
    // instead of passing through it.
    private void TryBounceOffFloor(float dt)
    {
        var position = _transform.position;
        var contact = FloorY + FloorHalfHeight;
        if (position.y > contact)
            return;

        position.y = contact;
        _transform.position = position;

        if (_velocity.y < 0f)
            _velocity.y = -_velocity.y * 0.4f; // restitution

        // Horizontal friction so a bounced emote settles instead of sliding.
        var horizontal = new Vector3(_velocity.x, 0f, _velocity.z);
        var speed = horizontal.magnitude;
        if (speed > 0.01f)
        {
            var newSpeed = Mathf.Max(0f, speed - 3f * dt);
            horizontal *= newSpeed / speed;
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;
        }
    }
}

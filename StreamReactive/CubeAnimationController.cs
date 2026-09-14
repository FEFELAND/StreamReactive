using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// Self-contained animations for websocket-driven "cube animation" events.
/// The note animation is built from the throw system's note/cube visual
/// source and floats at the platform edge, peeks toward the player with a
/// name tag above it, then sinks and fades away. The eject animation shows
/// an Among Us crewmate sliding through a fixed world spot with optional
/// typewriter text. Everything runs on the DontDestroyOnLoad controller so
/// it works across scenes and menus.
/// </summary>
internal sealed class CubeAnimationController : MonoBehaviour
{
    private const string ObjectName = "StreamReactiveCubeAnimation";
    private const string RootName = "StreamReactiveCubeAnimationRoot";

    // World-space layout. The bounce floor (and platform surface) sits at
    // world y = 0, platform-centered at the origin.
    private const float PlatformSurfaceY = 0f;
    private const float SurfaceClearance = 0.04f;

    // Lurk timing / look behaviour.
    private const float RiseSeconds = 0.8f;
    private const float PeekHold = 5f;
    private const float PeekAmp = 0.13f;
    private const float PeekSwayAmp = 0.11f;
    private const float PeekSwayFreq = 3.2f;
    private const float PeekTiltDeg = 22f;
    private const float PeekTiltFreq = 3.2f;
    private const float AccentAlt = 0.09f;
    private const float SinkSeconds = 2.2f;

    // Spawn placement: the note rests at a fixed, configurable world position
    // (CubeAnimSpawnX/Z). Consecutive lurks fan out along a fixed sideways axis.
    private const int LaneCount = 5;
    private const float LaneSpacing = 1.8f;

    // Name tag riding the top face of the note. The text bottom is anchored right
    // at the top face with a tiny scale-aware gap, and font height scales with
    // the block's world height so larger notes get larger, still-unclipped tags.
    private const float TagGapRatio = 0.1f;
    private const float TagMinGap = 0.03f;
    private const float TagFontRatio = 4.0f;
    private const float TagFontMinWorld = 0.9f;
    private const float TagFontMaxWorld = 3.2f;

    private static CubeAnimationController? _instance;
    private static Transform? _root;
    private static readonly List<GameObject> _active = new();

    // Cycling lane index so back-to-back lurks fan out sideways instead of
    // stacking on top of each other.
    private static int _spawnLane;

    // Note color: the note visual carries its own look; only the name tag color
    // is supplied here so it reads as the player's own tag even in a dark map.
    private static readonly Color TagColor = new Color(1f, 1f, 1f);

    // Fixed world-space spawn for the lurk animation. Each animation is hardcoded
    // to a spot rather than exposing per-event config knobs; the note never
    // tracks the player. Yaw 0 = the arrow/front face points along world -Z.
    private const float SpawnX = 0f;
    private const float SpawnZ = 1.18f;
    private const float SpawnFaceDeg = 0f;

    // Rise/sink travel: the note emerges from well below the surface and
    // retreats just as deep, easing as it nears the resting height at the corner
    // of the platform facing the player.
    private const float RiseStartDepth = 2.2f;
    private const float SinkEndDepth = 2.4f;

    // Eject (Among Us) placement: the crewmate floats at a fixed spot in front
    // of and above the player. The character faces the player along -Z and the
    // typing text hangs below it.
    private const float EjectSpawnX = 0f;
    private const float EjectSpawnY = 3.8f;
    private const float EjectSpawnZ = 6f;

    // Eject timing / look behaviour.
    private const float EjectSlideIn = 1.0f;
    private const float EjectSlideOut = 1.5f;
    private const float EjectScaleIn = 0.8f;
    private const float EjectScaleOut = 1.2f;
    private const float EjectBobAmp = 0.03f;
    private const float EjectBobFreq = 2.4f;
    private const float EjectTypeSpeed = 34f;
    private const float EjectTypeHold = 1.5f;
    private const float EjectSlideDistance = 2.0f;

    // End-over-end rolls (upright -> upside-down -> upright per flip) spread
    // across the whole animation. 1 = the crewmate arrives back upright.
    private const float EjectRollFlips = 1f;

    // Crewmate world height (body + legs) before any per-event scaling, plus
    // the default overall size for the eject diorama.
    private const float EjectDefaultScale = 1.8f;

    // Eject text style. The text is a standalone world-space object centered at
    // the spawn position; it is NOT parented to the crewmate so it stays put
    // while the crewmate slides through. The font size is proportional to the
    // crewmate scale so the diorama grows/shrinks together; EjectTextRefScale
    // is the crewmate size the font was visually tuned against (a 2.8 crewmate
    // with a 3.2 font reads as harmonious).
    private const float EjectTextFontWorld = 3.2f;
    private const float EjectTextRefScale = 2.8f;
    private const float EjectTextLineGap = 0.15f;
    private const float EjectTextBelowY = -1.4f; // offset below the crewmate center, clear of the note lane
    private const float EjectTextShrinkSeconds = 0.6f;
    private static readonly Color EjectTextColor = new Color(0.6f, 0.66f, 0.75f);

    // Eject suit: the crewmate uses the same bloom-immune sprite material as
    // the emotes (ImageFactory Uber shader), so any color is safe — no luminance
    // clamp is needed. The ceiling exists only as a safety margin.
    private const float MaxSuitLuminance = 0.9f;
    private static readonly Color VisorColor = new Color(0.35f, 0.95f, 1f, 1f);

    // Crewmate palette - Among Us paint names mapped to their suits. "red" is
    // the default. Hex colors are also accepted via TryParseHex.
    private static readonly Dictionary<string, Color> CrewmatePalette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new Color(0.78f, 0.11f, 0.16f, 1f),
        ["blue"] = new Color(0.12f, 0.36f, 0.86f, 1f),
        ["green"] = new Color(0.16f, 0.72f, 0.34f, 1f),
        ["pink"] = new Color(0.92f, 0.51f, 0.76f, 1f),
        ["orange"] = new Color(0.96f, 0.51f, 0.09f, 1f),
        ["black"] = new Color(0.16f, 0.16f, 0.18f, 1f),
        ["white"] = new Color(0.92f, 0.92f, 0.92f, 1f),
        ["purple"] = new Color(0.48f, 0.24f, 0.68f, 1f),
        ["brown"] = new Color(0.47f, 0.29f, 0.16f, 1f),
        ["cyan"] = new Color(0.15f, 0.78f, 0.78f, 1f),
        ["yellow"] = new Color(0.98f, 0.82f, 0.12f, 1f),
    };

    internal static void EnsureCreated()
    {
        if (_instance != null)
            return;

        var go = new GameObject(ObjectName);
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<CubeAnimationController>();
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
        _root = null;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(this);
            return;
        }
        _instance = this;

        var rootGo = new GameObject(RootName);
        DontDestroyOnLoad(rootGo);
        _root = rootGo.transform;

        var ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycast >= 0)
            rootGo.layer = ignoreRaycast;
    }

    internal static void PlayLurk(string? userName, float? overrideScale, float? overrideDuration)
    {
        EnsureCreated();
        // Runs on the persistent RuntimeHooks object so it survives scene
        // changes while the animation is in flight.
        RuntimeHooks.RunCoroutine(_instance!.LurkLegend(userName, overrideScale, overrideDuration));
    }

    internal static void PlayEject(string? colorArg, string? line1, string? line2, float? overrideScale, float? overrideDuration)
    {
        EnsureCreated();
        RuntimeHooks.RunCoroutine(_instance!.EjectLegend(colorArg, line1, line2, overrideScale, overrideDuration));
    }

    internal static void StopAll()
    {
        for (var i = 0; i < _active.Count; i++)
        {
            var go = _active[i];
            if (go != null)
                UnityEngine.Object.Destroy(go);
        }
        _active.Clear();
    }

    private IEnumerator LurkLegend(string? userName, float? overrideScale, float? overrideDuration)
    {
        GameObject? visual = null;
        Transform? cube = null;
        TextMeshPro? tag = null;

        try
        {
            // Fixed lurk size: 0.4 (a small accent note). Only an explicit
            // per-message "scale" override can change it for one lurk.
            var scale = Mathf.Clamp(overrideScale ?? 0.4f, 0.05f, 3f);
            // How long the note hangs out before sinking back down.
            var holdSeconds = Mathf.Clamp(overrideDuration ?? PeekHold, 0.5f, 30f);

            visual = ProjectileThrower.CreateStandaloneNoteVisual(0);
            if (visual == null)
                yield break;
            visual.name = "StreamReactiveLurk-Note";
            visual.transform.SetParent(_root, false);
            _active.Add(visual);

            // The captured note prefab can carry its arrow children parked
            // inactive (the game hides NoteArrow/NoteArrowGlow/NoteCircleGlow on
            // live notes it processes, and in-game live clones outrank the
            // pristine template). The lurk is a static, inspectable prop, so
            // wake any arrow bits back up - the inverse of the game's hiding.
            EnsureArrowsVisible(visual);

            // Strip any throw behaviour the shared visual carried.
            var cb = visual.GetComponent<ProjectileCube>();
            if (cb != null)
                UnityEngine.Object.Destroy(cb);

            var baseScale = visual.transform.localScale;
            var worldScale = new Vector3(baseScale.x * scale, baseScale.y * scale, baseScale.z * scale);
            visual.transform.localScale = worldScale;
            cube = visual.transform;

            // Name tag parented to the note itself so it follows the block; it
            // is oriented once at build time and rides along for the whole
            // animation (no per-frame billboarding).
            tag = BuildTag(userName ?? "Anonymous", cube);

            // Hardcoded world-space spawn -- never depends on the player's camera,
            // position, or facing. The note always appears at the same spot,
            // e.g. alongside the chat wallanchor. Y rests the cube flat on the
            // ground surface.
            var targetY = PlatformSurfaceY + SurfaceClearance + worldScale.y * 0.5f;
            var edgePos = new Vector3(SpawnX, targetY, SpawnZ);

            // Hardcoded facing: the arrow/front of the note points at the room
            // rather than tracking the player. With yaw 0 the arrow (the note's
            // -Z face) points along world -Z.
            var facing = Quaternion.Euler(0f, SpawnFaceDeg, 0f);
            var arrowDir = -(facing * Vector3.forward);
            arrowDir.y = 0f;
            arrowDir.Normalize();
            var right = Vector3.Cross(Vector3.up, arrowDir);

            // Consecutive lurks fan out along a fixed sideways axis instead of
            // stacking on top of each other.
            var laneIndex = (_spawnLane % LaneCount) - (LaneCount / 2);
            _spawnLane++;
            edgePos += right * (laneIndex * worldScale.x * LaneSpacing);

            // Emerge from well below the surface so it looks like it swims up
            // out of the floor.
            var riseFrom = edgePos - Vector3.up * RiseStartDepth;

            cube.position = riseFrom;
            cube.rotation = facing;
            PositionTag(cube, tag);

            // Rise.
            var elapsed = 0f;
            var total = RiseSeconds;
            while (elapsed < total)
            {
                elapsed += Mathf.Min(Time.deltaTime, 0.05f);
                var t = Mathf.Clamp01(elapsed / total);
                var y = Mathf.Lerp(riseFrom.y, targetY, EaseOut(t));
                SetPos(cube, edgePos, y);
                yield return null;
            }

            // Hang out, bobbing with a dancy sway while tilting left and right in a
            // curious manner. The tilt is a roll around the note's front-to-back
            // axis on top of the fixed facing, so the arrow and tag still read
            // from the player.
            var basePos = cube.position;
            float hold = holdSeconds;
            float te = 0f;
            while (te < hold)
            {
                te += Mathf.Min(Time.deltaTime, 0.05f);
                var p = Mathf.Clamp01(te / hold);

                var bob = Mathf.Sin(p * Mathf.PI) * PeekAmp;
                var sway = Mathf.Sin(p * PeekSwayFreq) * PeekSwayAmp;
                var pos = basePos + arrowDir * bob + right * sway;
                SetPos(cube, pos, pos.y + AccentAlt * Mathf.Sin(p * Mathf.PI));
                cube.rotation = facing * Quaternion.Euler(0f, 0f, Mathf.Sin(p * PeekTiltFreq) * PeekTiltDeg);
                yield return null;
            }

            // Sink back down below the floor. The note is fully underground well before
            // the loop ends, so no per-frame alpha/fade work is needed; destroying
            // it below the surface reads as it sinking away.
            elapsed = 0f;
            total = SinkSeconds;
            while (elapsed < total)
            {
                elapsed += Mathf.Min(Time.deltaTime, 0.05f);
                var t = Mathf.Clamp01(elapsed / total);
                var y = Mathf.Lerp(targetY, targetY - SinkEndDepth, EaseIn(t));
                SetPos(cube, edgePos, y);
                yield return null;
            }
        }
        finally
        {
            if (visual != null)
            {
                _active.Remove(visual);
                // The tag is a child of the note, so destroying the note cleans
                // up the tag as well.
                UnityEngine.Object.Destroy(visual);
            }
        }
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);
    private static float EaseIn(float t) => t * t;

    /// <summary>
    /// Wakes the arrow children on a cloned note so the lurk reliably shows its
    /// arrow. The game's own processing can leave them deactivated on the source
    /// the clone came from; the lurk undoes that without touching throw visuals.
    /// </summary>
    private static void EnsureArrowsVisible(GameObject go)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            var name = t.name;
            if ((name == "NoteArrow" || name == "NoteArrowGlow" || name == "NoteCircleGlow")
                && !t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(true);
            }
        }
    }

    private static void SetPos(Transform cube, Vector3 edgePos, float y)
    {
        cube.position = new Vector3(edgePos.x, y, edgePos.z);
    }

    private static void PositionTag(Transform cube, TextMeshPro? tag)
    {
        if (tag == null || cube == null)
            return;

        // The tag is a child of the note, so it already follows the block. The tag is
        // Bottom-aligned, so anchoring its transform right at the top face puts
        // the glyphs just above the block with a tiny scale-aware gap - no part
        // of the name dips into the block, no matter the note size.
        var worldH = Mathf.Max(cube.lossyScale.y, 0.01f);
        var halfH = worldH * 0.5f;
        var gap = Mathf.Max(worldH * TagGapRatio, TagMinGap);
        tag.transform.position = cube.position + Vector3.up * (halfH + gap);
    }

    /// <summary>
    /// World font height for the tag. Scales with the block's world height so the
    /// name grows when the note does, clamped so degenerate sizes stay sane.
    /// </summary>
    private static float TagFontWorldHeight(Transform cube)
    {
        var worldH = Mathf.Max(cube.lossyScale.y, 0.01f);
        return Mathf.Clamp(worldH * TagFontRatio, TagFontMinWorld, TagFontMaxWorld);
    }

    private static TextMeshPro? BuildTag(string text, Transform cube)
    {
        // Create the object with a RectTransform up front; you cannot replace a
        // plain Transform with a RectTransform at runtime via AddComponent.
        //
        // The tag is parented to the NOTE and oriented ONCE here, so it follows
        // the block around with zero per-frame camera work. The note's +Z points
        // away from the viewer (its arrow/front face points along the hardcoded
        // SpawnFaceDeg heading), and TMP text reads correctly from its own -Z
        // side, so an identity local rotation lines the readable face up with
        // the viewer. No 180-degree spin is needed.
        var go = new GameObject("StreamReactiveLurk-Tag", typeof(RectTransform));
        go.transform.SetParent(cube, false);
        go.transform.localRotation = Quaternion.identity;

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = TagFontWorldHeight(cube) / Mathf.Max(cube.lossyScale.y, 0.01f);
        tmp.color = TagColor;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
        tmp.verticalAlignment = VerticalAlignmentOptions.Bottom;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;

        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(4f, 1f);
        tmp.ForceMeshUpdate(true, true);

        return tmp;
    }

    /// <summary>
    /// Among Us-style ejection: a colored crewmate sweeps left-to-right through
    /// a fixed world spot in one unbroken, constant-velocity pass while an
    /// optional message types out under it. The whole traverse reads as a slow
    /// lin-scan: it never stops at the center. The crewmate rolls a full
    /// end-over-end spin (upright -&gt; upside-down -&gt; upright) and appears /
    /// disappears purely through scale, never transparency. Text is fully
    /// opt-in - no lines supplied means a silent, flying crewmate.
    /// </summary>
    private IEnumerator EjectLegend(string? colorArg, string? line1, string? line2, float? overrideScale, float? overrideDuration)
    {
        GameObject? crewmate = null;
        GameObject? textGo = null;
        TextMeshPro? tmp = null;

        try
        {
            var color = ParseColor(colorArg);
            var scale = Mathf.Clamp(overrideScale ?? EjectDefaultScale, 0.05f, 5f);
            // The eject text is sized relative to the crewmate so the whole
            // diorama grows/shrinks together. The ratio comes from the
            // visually-tuned pairing (font 3.2 against a 2.8 crewmate); a
            // clamped font keeps tiny scales readable.
            var textFactor = scale / EjectTextRefScale;
            var ejectFont = Mathf.Clamp(EjectTextFontWorld * textFactor, 1f, 8f);

            var firstLine = string.IsNullOrWhiteSpace(line1) ? null : line1;
            var secondLine = string.IsNullOrWhiteSpace(line2) ? null : line2;

            var center = new Vector3(EjectSpawnX, EjectSpawnY, EjectSpawnZ);
            // The text gap below the crewmate scales with the size too, so the
            // text stays proportionally spaced, not glued to a fixed offset.
            var textCenter = center + Vector3.up * (EjectTextBelowY * textFactor);

            crewmate = BuildCrewmate(color);
            crewmate.name = "StreamReactiveEject-Crewmate";
            crewmate.transform.SetParent(_root, false);
            _active.Add(crewmate);

            var crew = crewmate.transform;

            textGo = BuildEjectText(firstLine, secondLine, textCenter, ejectFont, out tmp);
            if (textGo != null)
            {
                textGo.transform.SetParent(_root, false);
                _active.Add(textGo);
            }

            // One continuous sweep from the left edge to the right edge; the
            // text types out during the middle while the crewmate is roughly
            // circling the center. Scale-only appear/disappear so the character
            // stays fully opaque the whole time it is visible.
            var start = center - Vector3.right * EjectSlideDistance;
            var end = center + Vector3.right * EjectSlideDistance;
            var charCount = tmp != null ? Mathf.Max(tmp.textInfo.characterCount, 1) : 0;
            var defaultTotal = EjectSlideIn + charCount / EjectTypeSpeed + EjectTypeHold + EjectSlideOut;
            // An explicit per-message duration overrides the whole animation
            // length; the typewriter speed adapts so the text still finishes
            // inside the window. Guard a floor so the phases can't collide.
            var total = overrideDuration.HasValue
                ? Mathf.Max(overrideDuration.Value, EjectSlideIn + EjectTypeHold + EjectSlideOut + 0.2f)
                : defaultTotal;
            var typeWindow = Mathf.Max(total - EjectSlideIn - EjectTypeHold - EjectSlideOut, 0.05f);

            crew.position = start;
            crew.rotation = Quaternion.Euler(0f, 180f, 0f);
            crew.localScale = Vector3.zero;

            var elapsed = 0f;
            var typeElapsed = 0f;
            var typing = false;
            while (elapsed < total)
            {
                elapsed += Mathf.Min(Time.deltaTime, 0.05f);
                var t = Mathf.Clamp01(elapsed / total);

                // Constant-velocity dash, plus a gentle horizontal-roll bob on top.
                crew.position = Vector3.Lerp(start, end, t)
                    + Vector3.up * (Mathf.Sin(elapsed * EjectBobFreq) * EjectBobAmp);

                // Grow in over the opening, shrink out over the closing; fully
                // opaque everywhere in between.
                var inScale = Mathf.Clamp01(elapsed / EjectScaleIn);
                var outScale = Mathf.Clamp01((total - elapsed) / EjectScaleOut);
                crew.localScale = Vector3.one * (scale * Mathf.Min(inScale, outScale));

                // Roll end-over-end: at t=0.5 the crewmate is upside down, at
                // the end it is upright again. Yaw 180 keeps it facing the player.
                crew.rotation = Quaternion.Euler(0f, 180f, 360f * EjectRollFlips * t);

                if (!typing && tmp != null && elapsed >= EjectSlideIn)
                {
                    typing = true;
                    typeElapsed = 0f;
                }
                if (typing && tmp != null && charCount > 0)
                {
                    typeElapsed += Mathf.Min(Time.deltaTime, 0.05f);
                    tmp.maxVisibleCharacters = Mathf.RoundToInt(
                        Mathf.Clamp01(typeElapsed / typeWindow) * charCount);
                }

                yield return null;
            }

            // The crewmate is long gone; shrink the message away to nothing.
            if (textGo != null)
            {
                var textScale = textGo.transform;
                var tShrink = 0f;
                while (tShrink < EjectTextShrinkSeconds)
                {
                    tShrink += Mathf.Min(Time.deltaTime, 0.05f);
                    textScale.localScale = Vector3.one * (1f - Mathf.Clamp01(tShrink / EjectTextShrinkSeconds));
                    yield return null;
                }
                textScale.localScale = Vector3.zero;
            }
        }
        finally
        {
            if (crewmate != null)
            {
                _active.Remove(crewmate);
                UnityEngine.Object.Destroy(crewmate);
            }
            if (textGo != null)
            {
                _active.Remove(textGo);
                UnityEngine.Object.Destroy(textGo);
            }
        }
    }

    /// <summary>
    /// Builds the crewmate out of primitives (capsule body, two blocky legs,
    /// a backpack, and a rounded cyan visor) plus whatever suit color was
    /// chosen. Parts are positioned at unit scale 1; the caller scales the
    /// whole rig.
    /// </summary>
    private static GameObject BuildCrewmate(Color color)
    {
        var root = new GameObject("StreamReactiveEject-Crewmate");

        // Body: short, wide capsule — the rounded Among Us torso.
        var body = CreatePrimitive(PrimitiveType.Capsule, "Body", new Vector3(0.12f, 0.10f, 0.12f));
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.03f, 0f);

        // Legs: two taller, stubby capsules offset to each side.
        var legL = CreatePrimitive(PrimitiveType.Capsule, "Leg-L", new Vector3(0.05f, 0.06f, 0.06f));
        legL.transform.SetParent(root.transform, false);
        legL.transform.localPosition = new Vector3(-0.035f, -0.05f, 0f);

        var legR = CreatePrimitive(PrimitiveType.Capsule, "Leg-R", new Vector3(0.05f, 0.06f, 0.06f));
        legR.transform.SetParent(root.transform, false);
        legR.transform.localPosition = new Vector3(0.035f, -0.05f, 0f);

        // Backpack: small block on the back.
        var backpack = CreatePrimitive(PrimitiveType.Cube, "Backpack", new Vector3(0.08f, 0.08f, 0.05f));
        backpack.transform.SetParent(root.transform, false);
        backpack.transform.localPosition = new Vector3(0f, 0.02f, -0.08f);

        // Visor: flat pill lying horizontally across the front of the face. The
        // capsule primitive is already longest along its X axis, so no rotation
        // is applied — identity keeps the wide pill horizontal (Across Us style).
        var visor = CreatePrimitive(PrimitiveType.Capsule, "Visor", new Vector3(0.07f, 0.025f, 0.025f));
        visor.transform.SetParent(root.transform, false);
        visor.transform.localPosition = new Vector3(0f, 0.03f, 0.07f);
        visor.transform.localRotation = Quaternion.identity;

        ApplyCrewmateColor(root.transform, color);

        var ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycast >= 0)
            SetLayerRecursive(root, ignoreRaycast);

        return root;
    }

    private static GameObject CreatePrimitive(PrimitiveType type, string name, Vector3 scale)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.localScale = scale;
        var collider = go.GetComponent<Collider>();
        if (collider != null)
            UnityEngine.Object.Destroy(collider);
        return go;
    }

    private static void ApplyCrewmateColor(Transform crewmate, Color color)
    {
        foreach (var r in crewmate.GetComponentsInChildren<Renderer>(true))
        {
            var isVisor = r.gameObject.name == "Visor";
            r.material = MakeMaterial(isVisor ? VisorColor : color, isVisor ? float.MaxValue : MaxSuitLuminance);
        }
    }

    /// <summary>
    /// Flat, unlit material built from the SAME bloom-immune sprite material the
    /// emote system renders with (ImageFactory's "_Sprite" material from the
    /// embedded sprite.assetbundle). That is the one material confirmed to render
    /// true color with no scene bloom, so the crewmate avoids the white-bloom the
    /// plain Sprites/Default / Hidden/Internal-Colored shaders hit on Beat Saber
    /// 1.40.8. The suit shows its full chosen color; the visor keeps its full
    /// bright cyan.
    /// </summary>
    private static Material MakeMaterial(Color color, float maxLuminance)
    {
        var c = ClampLuminance(color, maxLuminance);
        var src = EmoteVisualFactory.GetSpriteMaterial();
        var mat = src != null
            ? new Material(src)
            : new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored"));
        mat.mainTexture = null;
        mat.SetFloat("_Glow", 0f);
        TintBeatSaberMaterial(mat, c);
        NoteCosmeticController.VerboseLog($"Eject material: shader={mat.shader?.name} color=({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})");
        return mat;
    }

    /// <summary>
    /// The ImageFactory sprite material runs on Beat Saber's Uber shader, whose
    /// tint property isn't a plain "_Color" — setting mat.color alone leaves it
    /// white. Mirror the throw system: sweep every Color-typed property the
    /// shader exposes and stamp the tint on the real one(s). Emission-ish
    /// properties are skipped so the visor doesn't recover its glow.
    /// </summary>
    private static void TintBeatSaberMaterial(Material mat, Color c)
    {
        var shader = mat.shader;
        if (shader == null)
        {
            mat.color = c;
            return;
        }
        for (var i = 0; i < shader.GetPropertyCount(); i++)
        {
            if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color)
                continue;
            var propName = shader.GetPropertyName(i);
            if (propName.IndexOf("Emiss", StringComparison.OrdinalIgnoreCase) >= 0
                || propName.IndexOf("Fresnel", StringComparison.OrdinalIgnoreCase) >= 0
                || propName.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            mat.SetColor(propName, c);
            NoteCosmeticController.VerboseLog($"Eject tint: {propName} <- ({c.r:F2},{c.g:F2},{c.b:F2})");
        }
        mat.SetFloat("_UseSimpleColor", 1f);
        mat.SetColor("_SimpleColor", c);
    }

    /// <summary>
    /// Uniformly scales a color down (same hue, same ratio) until its Rec.709
    /// luminance stays at or below the given ceiling.
    /// </summary>
    private static Color ClampLuminance(Color c, float maxLuminance)
    {
        var luminance = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        if (luminance <= maxLuminance)
            return c;
        var k = maxLuminance / luminance;
        return new Color(c.r * k, c.g * k, c.b * k, c.a);
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursive(child.gameObject, layer);
    }

    /// <summary>
    /// Accepts a named crewmate color ("red", "lime", ...) or a hex string
    /// ("#FF0000"/"FF0000"/"880000FF"). Unknown or missing inputs fall back to
    /// the classic red.
    /// </summary>
    private static Color ParseColor(string? colorArg)
    {
        if (string.IsNullOrWhiteSpace(colorArg))
            return CrewmatePalette["red"];

        var trimmed = colorArg!.Trim();
        if (CrewmatePalette.TryGetValue(trimmed, out var named))
            return named;
        if (EventDispatcher.TryParseHex(trimmed, out var hex))
            return hex;
        return CrewmatePalette["red"];
    }

    /// <summary>
    /// Eject message as one or two world-space TextMeshPro lines, centered at
    /// the spawn position. Returns null when nothing should be shown, so a
    /// payload without text produces a silent flying crewmate. The object is
    /// created unparented so it doesn't track the crewmate's slide.
    /// </summary>
    private static GameObject? BuildEjectText(string? line1, string? line2, Vector3 worldCenter, float fontWorld, out TextMeshPro? tmp)
    {
        tmp = null;
        var full = string.Empty;
        if (line1 != null) full = line1;
        if (line2 != null) full = full.Length > 0 ? full + "\n" + line2 : line2;
        if (full.Length == 0)
            return null;

        var go = new GameObject("StreamReactiveEject-Text", typeof(RectTransform));
        go.transform.position = worldCenter;

        var t = go.AddComponent<TextMeshPro>();
        t.text = full;
        t.fontSize = fontWorld;
        t.color = EjectTextColor;
        t.alignment = TextAlignmentOptions.Center;
        t.horizontalAlignment = HorizontalAlignmentOptions.Center;
        t.verticalAlignment = VerticalAlignmentOptions.Middle;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        t.lineSpacing = EjectTextLineGap / Mathf.Max(fontWorld, 0.01f);

        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(fontWorld * 10f, fontWorld * 3f);
        t.ForceMeshUpdate(true, true);
        t.maxVisibleCharacters = 0;

        tmp = t;
        return go;
    }
}
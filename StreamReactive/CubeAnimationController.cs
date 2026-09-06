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
/// name tag above it, then sinks and fades away. Everything runs on the
/// DontDestroyOnLoad controller so it works across scenes and menus.
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

    internal static void PlayLurk(string? userName, float? overrideScale)
    {
        EnsureCreated();
        // Runs on the persistent RuntimeHooks object so it survives scene
        // changes while the animation is in flight.
        RuntimeHooks.RunCoroutine(_instance!.LurkLegend(userName, overrideScale));
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

    private IEnumerator LurkLegend(string? userName, float? overrideScale)
    {
        GameObject? visual = null;
        Transform? cube = null;
        TextMeshPro? tag = null;

        try
        {
            // Fixed lurk size: 0.4 (a small accent note). Only an explicit
            // per-message "scale" override can change it for one lurk.
            var scale = Mathf.Clamp(overrideScale ?? 0.4f, 0.05f, 3f);

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
            float hold = PeekHold;
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
}
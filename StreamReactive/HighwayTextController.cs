using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// "Highway Scroll Text" effect. Spawns a world-space TextMeshPro laid flat on
/// the floor (readable face pointing up, +Y) far down the highway at world
/// X=0 / Y=0.05 / Z=30 and scrolls it past the player at a fixed speed. The
/// text fades in from nothing between Z=30 and Z=15, holds until the player
/// boundary (Z=0), then fades linearly to fully transparent by Z=-10 (10m
/// behind the player) and destroys itself. An optional 8-digit hex color
/// (#RRGGBBAA) caps the peak opacity.
/// WebSocket-only: every tunable (text, size, max width/wrap, scroll speed,
/// color) comes from the message payload; there are no in-game settings.
/// </summary>
internal sealed class HighwayTextController : MonoBehaviour
{
    private const string ObjectName = "StreamReactiveHighwayText";

    private static HighwayTextController? _instance;
    private static readonly List<HighwayTextScroll> Active = new();

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    internal static void EnsureCreated()
    {
        if (_instance != null) return;
        var go = new GameObject(ObjectName);
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<HighwayTextController>();
    }

    /// <summary>Creates one scrolling highway text (main thread only).</summary>
    internal static void Show(string text, float size, float maxWidth, float speed, Color color)
    {
        var go = new GameObject("StreamReactiveHighwayText-Scroll", typeof(RectTransform));
        var entry = go.AddComponent<HighwayTextScroll>();
        Active.Add(entry);
        entry.Init(text, size, maxWidth, speed, color);
        NoteCosmeticController.VerboseLog($"Highway text: '{text}' scrolling at {speed:F1} m/s.");
    }

    /// <summary>Destroys every active highway text (used by stop_all).</summary>
    internal static void StopAll()
    {
        foreach (var entry in new List<HighwayTextScroll>(Active))
        {
            if (entry != null)
            {
                try { Destroy(entry.gameObject); } catch { }
            }
        }
        Active.Clear();
    }

    internal static void Remove(HighwayTextScroll entry)
    {
        Active.Remove(entry);
    }
}

/// <summary>
/// One self-driving highway text: moves along -Z at a fixed speed, fades in
/// over the first stretch of the trip, keeps full peak opacity until it reaches
/// the player, fades out while passing 10m behind, then destroys itself.
/// </summary>
internal sealed class HighwayTextScroll : MonoBehaviour
{
    // Where the text spawns on the highway, world space: centered on X, resting
    // on the floor at Y=0.05, SpawnZ meters down the road.
    private const float FloorY = 0.05f;
    private const float SpawnZ = 30f;

    // Fade-in window: fully transparent at spawn (SpawnZ), linearly up to the
    // color's peak alpha by FadeInEndZ.
    private const float FadeInEndZ = 15f;

    // Fade-out window: peak alpha until FadeStartZ (the player boundary),
    // linearly to transparent by FadeEndZ (10m behind the player), then gone.
    private const float FadeStartZ = 0f;
    private const float FadeEndZ = -10f;

    // Vertical room reserved for wrapped text (world units). Word wrap needs a
    // finite rect height; this is a generous upper bound so overflow never clips.
    private const float WrapHeightFactor = 20f;

    private TextMeshPro? _tmp;
    private float _speed;
    private Color _baseColor;

    internal void Init(string text, float size, float maxWidth, float speed, Color color)
    {
        _speed = speed;
        _baseColor = color;

        transform.position = new Vector3(0f, FloorY, SpawnZ);
        // Flat on the floor, readable face up (+Y): TMP reads from its -Z side,
        // so Euler(90,0,0) turns that face toward the sky and glyph tops point
        // away down the road.
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        DontDestroyOnLoad(gameObject);

        _tmp = gameObject.AddComponent<TextMeshPro>();
        _tmp.text = text;
        _tmp.fontSize = size;
        _tmp.color = new Color(color.r, color.g, color.b, 0f);
        _tmp.alignment = TextAlignmentOptions.Center;
        _tmp.enableWordWrapping = true;
        _tmp.overflowMode = TextOverflowModes.Overflow;

        var rect = GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(Mathf.Max(0.5f, maxWidth), Mathf.Max(size, 0.05f) * WrapHeightFactor);
        _tmp.ForceMeshUpdate(true, true);
    }

    private void Update()
    {
        if (_tmp == null)
        {
            Destroy(gameObject);
            return;
        }

        var z = transform.position.z - _speed * Time.deltaTime;
        if (z <= FadeEndZ)
        {
            Destroy(gameObject);
            return;
        }

        transform.position = new Vector3(0f, FloorY, z);

        // The text's own alpha (from an 8-digit hex color) is the peak: the
        // fade-in builds up to it and the fade-out decays from it.
        var peak = _baseColor.a;
        float alpha;
        if (z >= FadeInEndZ)
            alpha = peak * Mathf.InverseLerp(SpawnZ, FadeInEndZ, z);
        else if (z <= FadeStartZ)
            alpha = peak * Mathf.InverseLerp(FadeEndZ, FadeStartZ, z);
        else
            alpha = peak;

        _tmp.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, Mathf.Clamp01(alpha));
    }

    private void OnDestroy()
    {
        HighwayTextController.Remove(this);
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// Emotes render as a <see cref="SpriteRenderer"/> with the SAME sprite material that
/// the ImageFactory mod ships (loaded from an embedded AssetBundle). This is the one
/// configuration the user has confirmed works in BOTH the headset and Camera2's desktop
/// mirror with true color and no bloom:
///  - The sprite goes on the default layer, which every world camera (HMD + Camera2's
///    non-stereo clone) already includes, so no CameraUtils registration / manual
///    cullingMask / extra overlay camera is required.
///  - ImageFactory's sprite material is not picked up by the scene bloom, so the emote
///    stays bloom-free in both views.
/// The emote is a STANDALONE object (NOT parented to the spawner). It copies only the
/// spawner's world POSITION each frame and applies its own UNIFORM local scale plus the
/// billboard rotation. This avoids inheriting the spawner's non-uniform + rotating scale
/// (which would shear/squish a billboarded child no matter how the scale is "cancelled",
/// because lossyScale cannot represent that shear). The texture's own pixel aspect gives
/// the correct rectangle shape. Lifecycle is tracked via a follow-target registry so
/// pooled/reused spawners don't leave stale emotes behind.
///
/// ATTRIBUTION: The embedded sprite material ("sprite.assetbundle", containing the
/// "_Sprite" Renderer material) and the overall rendering technique are derived from
/// ImageFactory by Auros Nexus / WentTheFox (MIT License, https://github.com/WentTheFox/ImageFactory).
/// See ThirdPartyNotices.md for the full license text. Permission to reuse under MIT is gratefully acknowledged.
/// </summary>
internal static class EmoteVisualFactory
{
    private static Material? _spriteMaterial;
    private static bool _matLoaded;

    // Sprites are cached per source Texture2D and owned here (not per-emote) so animated
    // emotes don't allocate/destroy a Sprite every frame. Cleared on scene cleanup.
    private static readonly Dictionary<Texture2D, Sprite> SpriteCache = new();

    private const float PixelsPerUnit = 100f;

    internal static Material? GetSpriteMaterial()
    {
        if (_matLoaded) return _spriteMaterial;
        _matLoaded = true;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("StreamReactive.Resources.sprite.assetbundle");
            if (stream == null)
            {
                Plugin.Log.Warn("[Emote] sprite.assetbundle embedded resource not found; using default sprite material.");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
#pragma warning disable CS0618 // LoadFromMemory is fine here
            var bundle = AssetBundle.LoadFromMemory(ms.ToArray());
#pragma warning restore CS0618
            var prefab = bundle.LoadAsset<GameObject>("_Sprite");
            _spriteMaterial = new Material(prefab.GetComponent<Renderer>().material);
            bundle.Unload(false);
            Plugin.Log.Info("[Emote] loaded ImageFactory sprite material for emotes.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"[Emote] failed to load ImageFactory sprite material, using default: {ex.Message}");
        }
        return _spriteMaterial;
    }

    /// <summary>
    /// Returns a Sprite for <paramref name="tex"/>, reusing a cached one when possible.
    /// Ownership stays with the cache; callers must NOT destroy the returned Sprite.
    /// </summary>
    internal static Sprite GetSprite(Texture2D tex)
    {
        if (tex == null) return null!;
        if (SpriteCache.TryGetValue(tex, out var cached) && cached != null)
            return cached;
        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), PixelsPerUnit);
        SpriteCache[tex] = sprite;
        return sprite;
    }

    /// <summary>
    /// Builds a STANDALONE emote visual (parent = null). Its uniform local scale is
    /// (<paramref name="baseSize"/> * PPU / tex.height) on both axes; the texture's pixel
    /// aspect then yields a correctly-proportioned rectangle. Follow target is wired up in Init.
    /// </summary>
    internal static EmoteVisualSet CreateEmoteVisuals(Transform followTarget, Texture2D tex, float baseSize)
    {
        var go = new GameObject("EmoteSprite");
        go.transform.SetParent(null);
        go.transform.position = followTarget != null ? followTarget.position : Vector3.zero;
        var sr = go.AddComponent<SpriteRenderer>();
        var mat = GetSpriteMaterial();
        if (mat != null) sr.material = mat;
        sr.sprite = GetSprite(tex);
        return new EmoteVisualSet { SpriteComp = sr, SpriteTransform = go.transform, EmoteObject = go };
    }

    internal static float UniformScaleFor(Texture2D tex, float baseSize)
    {
        if (tex == null || tex.height <= 0) return baseSize;
        return baseSize * PixelsPerUnit / tex.height;
    }

    /// <summary>
    /// Destroys all cached sprites. Textures themselves are owned by EmoteCache, so this
    /// only frees the Sprite wrappers (called on scene change / full cleanup).
    /// </summary>
    internal static void ClearSpriteCache()
    {
        foreach (var sprite in SpriteCache.Values)
        {
            if (sprite != null)
            {
                try { UnityEngine.Object.Destroy(sprite); } catch { }
            }
        }
        SpriteCache.Clear();
    }
}

internal sealed class EmoteVisualSet
{
    public SpriteRenderer? SpriteComp;
    public Transform? SpriteTransform;
    public GameObject? EmoteObject;
}

/// <summary>
/// Per-emote component (on the standalone emote): follows the spawner's world position,
/// billboards toward the active view camera, and keeps the sprite texture (and uniform
/// scale) current for animated emotes. A follow-target registry retires emotes whose
/// spawner is destroyed or pooled away, and prevents stacking when a pooled spawner is reused.
/// </summary>
internal sealed class EmoteHudEntry : MonoBehaviour
{
    public SpriteRenderer? SpriteComp;
    public Texture2D CurrentTexture = null!;

    internal string Code = "";
    private Camera? _cam;
    private Transform? _follow;
    private float _baseSize;
    private float _k;
    private Texture2D? _lastTex;

    private static readonly Dictionary<Transform, EmoteHudEntry> Followers = new();

    internal static readonly List<EmoteHudEntry> Active = new();
    internal static readonly HashSet<string> ActiveCodes = new(StringComparer.OrdinalIgnoreCase);

    internal void Init(EmoteVisualSet set, Texture2D tex, string code, Transform followTarget, float baseSize)
    {
        SpriteComp = set.SpriteComp;
        _baseSize = baseSize;
        CurrentTexture = tex;
        Code = code;

        // Retire any prior emote attached to this (pooled) spawner before taking over.
        if (followTarget != null && Followers.TryGetValue(followTarget, out var old))
        {
            if (old != null && old.gameObject != null)
            {
                try { UnityEngine.Object.Destroy(old.gameObject); } catch { }
            }
        }
        _follow = followTarget;
        if (followTarget != null) Followers[followTarget] = this;

        Active.Add(this);
        ActiveCodes.Add(code);
        ApplySprite(tex);
    }

    private void ApplySprite(Texture2D tex)
    {
        if (SpriteComp == null || tex == null) return;
        // Always refresh the uniform scale (and last-texture marker) even when the
        // sprite is already set, so a static emote's _k is correct on first Init.
        _k = EmoteVisualFactory.UniformScaleFor(tex, _baseSize);
        _lastTex = tex;
        if (SpriteComp.sprite != null && SpriteComp.sprite.texture == tex) return;
        // Sprite is owned by EmoteVisualFactory.SpriteCache (shared); do not destroy it here.
        SpriteComp.sprite = EmoteVisualFactory.GetSprite(tex);
    }

    private void LateUpdate()
    {
        // Spawner gone or pooled away -> retire the emote.
        if (_follow == null || !_follow.gameObject.activeInHierarchy)
        {
            UnityEngine.Object.Destroy(gameObject);
            return;
        }

        transform.position = _follow.position;

        if (CurrentTexture != null && CurrentTexture != _lastTex)
            ApplySprite(CurrentTexture);

        if (_cam == null || !_cam.isActiveAndEnabled)
            _cam = CapsuleGuardController.GetViewCamera() ?? Camera.main;
        if (_cam == null) return;

        // Billboard toward the active view camera. Uniform local scale + no rotated
        // non-uniform ancestor => no shear, correct aspect regardless of rotation.
        var toCam = _cam.transform.position - transform.position;
        if (toCam.sqrMagnitude < 1e-4f)
            toCam = Vector3.forward;
        var billboard = Quaternion.LookRotation(toCam);
        var spin = _follow.eulerAngles.z;
        transform.rotation = billboard * Quaternion.Euler(0f, 0f, spin);
        transform.localScale = new Vector3(_k, _k, 1f);
    }

    private void OnDestroy()
    {
        if (_follow != null && Followers.TryGetValue(_follow, out var self) && self == this)
            Followers.Remove(_follow);
        Active.Remove(this);
        ActiveCodes.Remove(Code);
        SpriteComp = null;
    }

    internal static void CleanupAll()
    {
        foreach (var entry in new List<EmoteHudEntry>(Active))
        {
            try
            {
                if (entry?.gameObject != null)
                    UnityEngine.Object.Destroy(entry.gameObject);
            }
            catch
            {
            }
        }
        Active.Clear();
        ActiveCodes.Clear();
        Followers.Clear();
        EmoteVisualFactory.ClearSpriteCache();
    }
}

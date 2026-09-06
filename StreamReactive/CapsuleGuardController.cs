using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// Owns the guard capsule: a translucent capsule collider that tracks the player's
/// viewpoint, an avatar bone, or the platform center. Projectiles thrown by
/// <see cref="ProjectileThrower"/> bounce off it.
/// </summary>
internal sealed class CapsuleGuardController : MonoBehaviour
{
    private const string CapsuleObjectName = "StreamReactiveGuardCapsule";
    private const float BoneRescanInterval = 2f;

    // World height of the platform surface the bounce floor rests on.
    private const float FloorSurfaceY = 0f;

    private static CapsuleGuardController? _instance;

    private GameObject? _capsuleObject;
    private Transform? _capsuleTransform;
    private Transform? _visualChild;
    private MeshRenderer? _visualRenderer;
    private CapsuleCollider? _collider;
    private Transform? _floorVisual;
    private MeshRenderer? _floorRenderer;

    private Transform? _cachedBone;
    private string _cachedBonePath = string.Empty;
    private float _nextBoneScanTime;

    internal static void EnsureCreated()
    {
        if (_instance != null)
            return;

        var go = new GameObject("StreamReactiveCapsuleGuard");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<CapsuleGuardController>();
    }

    /// <summary>Best available point to aim projectiles at (capsule center if alive).</summary>
    internal static Vector3 CurrentAimPoint
    {
        get
        {
            var instance = _instance;
            if (instance != null && instance._capsuleTransform != null)
                return instance._capsuleTransform.position;

            var cam = GetViewCamera();
            return cam != null ? cam.transform.position : Vector3.zero;
        }
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        _instance = this;
    }

    private void OnDestroy()
    {
        if (_capsuleObject != null)
            Destroy(_capsuleObject);

        if (_instance == this)
            _instance = null;
    }

    private void LateUpdate()
    {
        var cfg = PluginConfig.Instance;

        if (cfg == null || !cfg.CapsuleGuardEnabled || !cfg.Enabled)
        {
            SetCapsuleActive(false);
            SetFloorVisualActive(false);
            return;
        }

        EnsureCapsuleObject();

        if (_capsuleObject == null || _capsuleTransform == null)
            return;

        if (!_capsuleObject.activeSelf)
            _capsuleObject.SetActive(true);

        var anchor = ResolveAnchor();
        _capsuleTransform.position = anchor + new Vector3(0f, cfg.CapsuleVerticalOffset, 0f);

        ApplyDimensions(cfg.CapsuleHeight, cfg.CapsuleWidth);

        if (_visualRenderer != null)
        {
            // Preview visuals are only rendered while the Throw settings tab is
            // open, so normal gameplay/menu stays clean.
            _visualRenderer.enabled =
                cfg.CapsuleShowVisual && StreamReactiveSettingsViewController.ThrowPreviewVisible;
        }

        UpdateFloorPreview(cfg);
    }

    /// <summary>The live guard capsule collider, or null when not running.</summary>
    internal static CapsuleCollider? ActiveCollider => _instance?._collider;

    // Camera.main is null during normal gameplay (Beat Saber untags the gameplay
    // camera), so we resolve the real view camera once and refresh if it dies.
    private static Camera? _cachedViewCam;

    internal static Camera? GetViewCamera()
    {
        if (_cachedViewCam != null && _cachedViewCam.isActiveAndEnabled)
            return _cachedViewCam;

        var cam = Camera.main;
        if (cam != null && cam.isActiveAndEnabled)
        {
            _cachedViewCam = cam;
            return cam;
        }

        // Camera.main is unavailable (normal play): pick the camera that actually
        // renders the player's view. Prefer one named "MainCamera" (Beat Saber's
        // gameplay HMD camera), otherwise the highest-plausibility camera targeting
        // the main display that isn't a UI / menu / spectator / mirror / replay
        // camera, and that sits somewhere physically sensible (not at the origin
        // and not at an extreme world position, which filters out junk cameras).
        Camera? named = null;
        Camera? best = null;
        foreach (var candidate in Camera.allCameras)
        {
            if (!candidate.isActiveAndEnabled || candidate.targetDisplay != 0)
                continue;

            var n = candidate.name;
            if (n.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (n.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (n.IndexOf("Spectator", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (n.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (n.IndexOf("Cam2", StringComparison.OrdinalIgnoreCase) >= 0) continue;

            var p = candidate.transform.position;
            var plausible = Mathf.Abs(p.x) < 200f && Mathf.Abs(p.z) < 200f
                && p.y > -50f && p.y < 200f && candidate.farClipPlane > 1f;
            if (!plausible)
                continue;

            if (named == null && (n == "MainCamera" || n.IndexOf("MainCamera", StringComparison.OrdinalIgnoreCase) >= 0))
                named = candidate;

            if (best == null || candidate.depth > best.depth)
                best = candidate;
        }

        _cachedViewCam = named ?? best;
        return _cachedViewCam;
    }

    private void EnsureCapsuleObject()
    {
        if (_capsuleObject != null)
            return;

        _capsuleObject = new GameObject(CapsuleObjectName);
        DontDestroyOnLoad(_capsuleObject);
        _capsuleTransform = _capsuleObject.transform;

        // Keep the capsule off the raycast layers so it never intercepts the
        // VR laser pointer (menus) or other raycasts. The projectile bounce is a
        // manual distance check against the collider geometry, so this changes
        // nothing about how throws behave.
        var ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycast >= 0)
            _capsuleObject.layer = ignoreRaycast;

        var collider = _capsuleObject.AddComponent<CapsuleCollider>();
        collider.direction = 1; // Y axis
        collider.height = 1.8f;
        collider.radius = 0.4f;
        _collider = collider;

        // Visual child is scaled independently so physics dimensions stay exact.
        var visual = new GameObject("GuardCapsuleVisual");
        visual.transform.SetParent(_capsuleTransform, false);

        var tempPrimitive = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        var capsuleMesh = tempPrimitive.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tempPrimitive);

        var meshFilter = visual.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = capsuleMesh;

        _visualRenderer = visual.AddComponent<MeshRenderer>();
        if (ignoreRaycast >= 0)
            visual.layer = ignoreRaycast;
        var shader = Shader.Find("Sprites/Default");
        var material = new Material(shader != null ? shader : Shader.Find("Hidden/Internal-Colored"));
        material.color = new Color(0.12f, 0.45f, 0.6f, 0.14f);
        _visualRenderer.sharedMaterial = material;

        _visualChild = visual.transform;
    }

    private void SetCapsuleActive(bool active)
    {
        if (_capsuleObject != null && _capsuleObject.activeSelf != active)
            _capsuleObject.SetActive(active);
    }

    private void SetFloorVisualActive(bool active)
    {
        if (_floorVisual != null && _floorVisual.gameObject.activeSelf != active)
            _floorVisual.gameObject.SetActive(active);
    }

    /// <summary>
    /// Translucent floor rectangle matching the bounce-floor dimensions,
    /// pinned to the platform center: world origin XZ and platform surface
    /// height. Same visibility rules as the capsule visual.
    /// </summary>
    private void UpdateFloorPreview(PluginConfig cfg)
    {
        var show = cfg.CapsuleShowVisual && StreamReactiveSettingsViewController.ThrowPreviewVisible;
        SetFloorVisualActive(show);

        if (!show)
            return;

        EnsureFloorVisual();

        if (_floorVisual != null)
        {
            // Floor is platform-centered: fixed at the world origin, both XZ
            // and height. The platform surface sits at world y=0.
            _floorVisual.position = new Vector3(0f, FloorSurfaceY + 0.02f, 0f);
            _floorVisual.localScale = new Vector3(
                Mathf.Clamp(cfg.ThrowFloorWidth, 1f, 20f),
                Mathf.Clamp(cfg.ThrowFloorDepth, 1f, 20f),
                1f);
        }
    }

    private void EnsureFloorVisual()
    {
        if (_floorVisual != null)
            return;

        var floorGo = new GameObject("GuardFloorVisual");
        floorGo.transform.SetParent(transform, false); // root never moves; world coords via localPosition
        floorGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        var tempQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var quadMesh = tempQuad.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tempQuad);

        var meshFilter = floorGo.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = quadMesh;

        _floorRenderer = floorGo.AddComponent<MeshRenderer>();
        var shader = Shader.Find("Sprites/Default");
        var material = new Material(shader != null ? shader : Shader.Find("Hidden/Internal-Colored"));
        material.color = new Color(0.35f, 1f, 0.55f, 0.16f);
        _floorRenderer.sharedMaterial = material;

        _floorVisual = floorGo.transform;
    }

    private void ApplyDimensions(float height, float width)
    {
        height = Mathf.Clamp(height, 0.2f, 4f);
        width = Mathf.Clamp(width, 0.05f, 2f);

        var collider = _capsuleObject != null ? _capsuleObject.GetComponent<CapsuleCollider>() : null;
        if (collider != null)
        {
            collider.height = height + width; // total including caps, matching Unity capsule primitive proportions
            collider.radius = width * 0.5f;
        }

        if (_visualChild != null)
        {
            // Unity's default capsule mesh: radius 0.5, height 2.
            _visualChild.localScale = new Vector3(width, (height + width) * 0.5f, width);
        }
    }

    private Vector3 ResolveAnchor()
    {
        var cfg = PluginConfig.Instance!;
        var mode = cfg.CapsuleAttachMode ?? "Headset";

        if (string.Equals(mode, "Platform Center", StringComparison.OrdinalIgnoreCase))
            return Vector3.zero;

        if (string.Equals(mode, "Avatar Bone", StringComparison.OrdinalIgnoreCase))
        {
            var bone = ResolveBone(cfg.CapsuleBonePath);
            if (bone != null)
                return bone.position;
        }

        // Headset mode and avatar-bone fallback: track the view camera (HMD).
        var cam = GetViewCamera();
        return cam != null ? cam.transform.position : Vector3.zero;
    }

    private Transform? ResolveBone(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('/');
        if (trimmed.Length == 0)
            return null;

        if (_cachedBone != null)
        {
            var stillValid = _cachedBone.gameObject.activeInHierarchy
                && string.Equals(_cachedBonePath, trimmed, StringComparison.OrdinalIgnoreCase);
            if (stillValid)
                return _cachedBone;

            _cachedBone = null;
        }

        if (Time.unscaledTime < _nextBoneScanTime)
            return null;

        _nextBoneScanTime = Time.unscaledTime + BoneRescanInterval;

        var segments = trimmed.Split('/');
        Transform? best = null;

        var allTransforms = FindObjectsOfType<Transform>();
        foreach (var candidate in allTransforms)
        {
            if (!ChainMatches(candidate, segments))
                continue;

            best = candidate;
            if (candidate.root.name.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0)
                break; // prefer matches under an avatar root
        }

        if (best != null)
        {
            _cachedBone = best;
            _cachedBonePath = trimmed;
            Plugin.Log.Debug($"CapsuleGuard: attached to bone '{GetHierarchyPath(best)}'.");
        }

        return best;
    }

    private static bool ChainMatches(Transform candidate, string[] segments)
    {
        // Match the configured path as a suffix of the hierarchy chain,
        // e.g. "Hips/Spine/Chest" or just "Head".
        var chain = new List<string>();
        var current = candidate;
        while (current != null)
        {
            chain.Add(current.name);
            current = current.parent;
        }

        if (chain.Count < segments.Length)
            return false;

        for (var i = 0; i < segments.Length; i++)
        {
            var expected = segments[segments.Length - 1 - i];
            var actual = chain[i];
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string GetHierarchyPath(Transform t)
    {
        var path = t.name;
        var current = t.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }
}

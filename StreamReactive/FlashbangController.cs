using System;
using System.Collections;
using UnityEngine;
using TMPro;

namespace StreamReactive;

/// <summary>
/// Full-view white-out ("flashbang") effect. A small unlit white sphere is
/// parked around the player's head every frame (world space, never parented
/// to scene objects) and rendered by OUR OWN overlay camera whose pass sits
/// above every other camera - BSML/HMUI UI is drawn by separate higher-depth
/// cameras, so no material render queue can ever cover it; an overlay camera
/// can. The sphere lives on a layer no other camera renders, keeping it out
/// of all other passes. Being inside the sphere blinds every eye and any
/// desktop view alike (the overlay composites into the same eye textures),
/// independent of FOV/aspect/stereo quirks, and survives scene changes
/// because nothing references scene objects except a per-frame read of the
/// head pose. Timeline: snap to peak opacity, hold for Duration minus Fade
/// Out seconds, fade out across the tail; retriggering restarts instantly.
/// </summary>
internal sealed class FlashbangController : MonoBehaviour
{
    private const string ObjectName = "StreamReactiveFlashbang";

    // Head-centered sphere radius in meters. Any value safely above the
    // camera near clip (observed 0.05-0.10) reads identically to the wearer:
    // pure white in every direction. 0.3 leaves comfortable margin.
    private const float SphereRadius = 0.3f;

    private static FlashbangController? _instance;

    private Transform? _overlay;
    private Renderer? _overlayRenderer;
    private Material? _material;
    private Coroutine? _routine;
    private Camera? _overlayCamera;
    private int _exclusiveLayer = -1;
    private bool _cameraReady;

    private GameObject? _viewerText;
    private TextMeshPro? _viewerTextTmp;

    internal static void EnsureCreated()
    {
        if (_instance != null) return;

        var go = new GameObject(ObjectName);
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<FlashbangController>();
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    // WebSocket messages arrive off the main thread; Unity API calls there are
    // illegal, so both entry points only set flags and LateUpdate acts on them.
    private volatile bool _flashRequested;
    private volatile bool _stopRequested;

    /// <summary>Requests a flash restart using the current settings (thread-safe).</summary>
    internal static void Flash()
    {
        var self = _instance;
        if (self == null)
            return;
        self._stopRequested = false;
        self._flashRequested = true;
    }

    /// <summary>Kills any active flash immediately (used by stop_all, thread-safe).</summary>
    internal static void StopAll()
    {
        var self = _instance;
        if (self == null)
            return;
        self._flashRequested = false;
        self._stopRequested = true;
    }

    private void LateUpdate()
    {
        if (_stopRequested)
        {
            _stopRequested = false;
            EndRoutine();
            SetOverlayVisible(false, 0f);
            DestroyViewerText();
        }

        if (_flashRequested)
        {
            _flashRequested = false;
            Begin();
        }

        UpdateOverlayTransform();
    }

    private void Begin()
    {
        var cfg = PluginConfig.Instance;
        var duration = Mathf.Clamp(cfg?.FlashbangDuration ?? 4f, 0.5f, 10f);
        var maxAlpha = Mathf.Clamp(cfg?.FlashbangOpacity ?? 100f, 0f, 100f) / 100f;
        var fade = Mathf.Clamp(cfg?.FlashbangFadeOut ?? 1.5f, 0f, duration);

        EndRoutine();

        // Force the tracking log to fire on every flash so gameplay
        // misbehavior is always diagnosable from the log.
        _lastHeadName = null;

        Plugin.Log.Info($"Flashbang: blinding for {duration:F1}s at {maxAlpha * 100f:F0}% opacity.");
        _routine = StartCoroutine(RunFlash(duration, maxAlpha, fade));

        SpawnViewerText(cfg!, duration);
    }

    private IEnumerator RunFlash(float duration, float maxAlpha, float fade)
    {
        var solid = Mathf.Max(0f, duration - fade);
        var elapsed = 0f;

        SetOverlayVisible(true, maxAlpha);

        while (elapsed < solid)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (fade > 0.001f)
        {
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01((elapsed - solid) / fade);
                SetOverlayVisible(true, maxAlpha * (1f - t));
                yield return null;
            }
        }

        SetOverlayVisible(false, 0f);
        DestroyViewerText();
        _routine = null;
    }

    private void EndRoutine()
    {
        if (_routine == null) return;
        StopCoroutine(_routine);
        _routine = null;
    }

    private void DestroyViewerText()
    {
        if (_viewerText != null)
        {
            UnityEngine.Object.Destroy(_viewerText);
            _viewerText = null;
            _viewerTextTmp = null;
        }
    }

    private void SetOverlayVisible(bool visible, float alpha)
    {
        if (visible && _overlay == null)
            CreateOverlay();

        if (_overlay == null)
            return;

        if (visible)
        {
            if (!_cameraReady)
            {
                EnsureOverlayCamera();
                _cameraReady = true;
            }
        }
        else
        {
            DisableOverlayCamera();
            _cameraReady = false;
        }

        if (_overlay.gameObject.activeSelf != visible)
            _overlay.gameObject.SetActive(visible);

        if (visible && _material != null)
            _material.color = new Color(1f, 1f, 1f, alpha);

        // Sync viewer text alpha with the flash
        if (_viewerTextTmp != null)
            _viewerTextTmp.color = new Color(_viewerTextTmp.color.r, _viewerTextTmp.color.g, _viewerTextTmp.color.b, alpha);
    }

    private void CreateOverlay()
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(sphere.GetComponent<Collider>());
        sphere.name = "StreamReactiveFlashSphere";
        sphere.SetActive(false);

        // Live under our DontDestroyOnLoad root: scene-root objects die on
        // scene loads (which would orphan _overlay mid-flash), and this way
        // the sphere simply survives everything.
        sphere.transform.SetParent(transform, false);

        _overlayRenderer = sphere.GetComponent<MeshRenderer>();
        _overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _overlayRenderer.receiveShadows = false;

        var shader = Shader.Find("Sprites/Default");
        _material = new Material(shader != null ? shader : Shader.Find("Hidden/Internal-Colored"));
        _material.color = new Color(1f, 1f, 1f, 0f);
        // Queue 5000 (the maximum) draws the sphere AFTER every normal
        // transparent item - including HMUI/BSML world-space UI at queue
        // ~3000 that would otherwise render on top of the flash.
        _material.renderQueue = 5000;
        _overlayRenderer.sharedMaterial = _material;

        _overlay = sphere.transform;
    }

    private void SpawnViewerText(PluginConfig cfg, float duration)
    {
        DestroyViewerText();

        var text = cfg?.FlashbangViewerText;
        if (string.IsNullOrEmpty(text))
            return;

        var color = cfg?.FlashbangViewerTextColor ?? Color.white;
        var cam = ResolveViewCamera();
        var pos = cam != null
            ? cam.transform.position + cam.transform.forward * 1.5f + Vector3.up * 0.3f
            : new Vector3(0f, 1.8f, 1.5f);

        var go = new GameObject("StreamReactiveFlashViewerText", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        if (cam != null)
            go.transform.rotation = Quaternion.LookRotation(go.transform.position - cam.transform.position);

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = Mathf.Clamp(cfg?.FlashbangViewerTextSize ?? 2.5f, 0.5f, 10f);
        tmp.color = new Color(color.r, color.g, color.b, 1f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;

        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(6f, 0.8f);
        tmp.ForceMeshUpdate(true, true);

        _viewerText = go;
        _viewerTextTmp = tmp;

        StartCoroutine(FloatViewerText(go, tmp, duration));
    }

    private IEnumerator FloatViewerText(GameObject go, TextMeshPro tmp, float duration)
    {
        var elapsed = 0f;
        var startPos = go.transform.position;

        while (elapsed < duration)
        {
            if (tmp == null) yield break;
            elapsed += Time.deltaTime;
            var t = elapsed / duration;

            go.transform.position = startPos + Vector3.forward * Mathf.Lerp(0f, 2.5f, t);

            var scale = 1f - (t * t);
            tmp.transform.localScale = Vector3.one * scale;

            yield return null;
        }

        if (go != null)
            UnityEngine.Object.Destroy(go);
        _viewerText = null;
        _viewerTextTmp = null;
    }

    // HMUI/BSML UI (and several other overlays) render through their own
    // cameras whose passes run AFTER the main camera, so no material render
    // queue can ever cover them. The counter is a dedicated flash camera
    // with a higher depth that renders ONLY the sphere, on a layer no other
    // camera renders. It composites into the same eye textures, so VR eyes
    // and the desktop mirror both see the flash.
    private static int FindExclusiveLayer()
    {
        var othersMask = 0;
        foreach (var cam in Camera.allCameras)
            othersMask |= cam.cullingMask;

        for (var layer = 31; layer >= 18; layer--)
        {
            if ((othersMask & (1 << layer)) == 0)
                return layer;
        }

        return -1;
    }

    private void EnsureOverlayCamera()
    {
        _exclusiveLayer = FindExclusiveLayer();
        if (_exclusiveLayer < 0)
        {
            Plugin.Log.Warn("Flashbang: no free layer for the overlay camera; using normal rendering.");
            DisableOverlayCamera();
            return;
        }

        _overlay!.gameObject.layer = _exclusiveLayer;

        if (_overlayCamera == null)
        {
            var go = new GameObject("StreamReactiveFlashOverlayCamera");
            go.transform.SetParent(transform, false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.Depth;
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
            _overlayCamera = cam;
        }

        _overlayCamera.cullingMask = 1 << _exclusiveLayer;
        _overlayCamera.enabled = true;
    }

    private void DisableOverlayCamera()
    {
        if (_overlayCamera != null)
            _overlayCamera.enabled = false;
    }

    // Keep the overlay camera glued to the head and stacked above every
    // other active camera for this frame.
    private void SyncOverlayCamera(Camera head)
    {
        if (_overlayCamera == null || !_overlayCamera.isActiveAndEnabled || _exclusiveLayer < 0)
            return;

        var maxDepth = float.MinValue;
        foreach (var cam in Camera.allCameras)
            maxDepth = Mathf.Max(maxDepth, cam.depth);

        var oc = _overlayCamera;
        oc.transform.SetPositionAndRotation(head.transform.position, head.transform.rotation);
        oc.fieldOfView = head.fieldOfView;
        oc.nearClipPlane = head.nearClipPlane;
        oc.farClipPlane = head.farClipPlane;
        oc.aspect = head.aspect;
        oc.depth = maxDepth > float.MinValue ? maxDepth + 10f : 100f;
    }

    // Park the sphere on the head every frame while visible. World space, no
    // parenting: scene cameras may be disabled or destroyed mid-flash, and a
    // child would inherit their fate. Rotation is irrelevant for a sphere.
    private void UpdateOverlayTransform()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
            return;

        var cam = ResolveViewCamera();
        if (cam == null)
        {
            // During an active flash the camera can briefly return null
            // (e.g. HMUI camera transition). Keep the overlay where it is
            // instead of hiding it, which would cause a visible flicker
            // on the desktop mirror.
            if (_routine != null)
                return;
            Plugin.Log.Warn("Flashbang: no usable view camera found; hiding flash.");
            _overlay.gameObject.SetActive(false);
            return;
        }

        var t = cam.transform;
        _overlay.position = t.position;
        _overlay.localScale = new Vector3(SphereRadius * 2f, SphereRadius * 2f, SphereRadius * 2f);
        SyncOverlayCamera(cam);

        if (_lastHeadName != cam.gameObject.name)
        {
            _lastHeadName = cam.gameObject.name;
            Plugin.Log.Info(
                $"Flashbang: tracking head via camera '{cam.gameObject.name}' (near={cam.nearClipPlane:F3}).");
        }
    }

    private static string? _lastHeadName;

    /// <summary>
    /// Picks the camera most likely to sit at the player's head: the tagged
    /// MainCamera if alive, else any active enabled camera with a screen-ish
    /// name, else the first active enabled camera. Logged whenever it changes
    /// so mis-picks are diagnosable from the log.
    /// </summary>
    private static Camera? ResolveViewCamera()
    {
        var main = Camera.main;
        if (main != null && main.isActiveAndEnabled)
            return main;

        Camera? fallback = null;
        foreach (var candidate in Camera.allCameras)
        {
            if (!candidate.isActiveAndEnabled)
                continue;

            var n = candidate.gameObject.name;
            if (n.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0)
                return candidate;

            fallback ??= candidate;
        }

        return fallback;
    }
}

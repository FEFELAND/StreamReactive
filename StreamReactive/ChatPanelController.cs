using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.FloatingScreen;
using BS_Utils.Utilities;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StreamReactive;

/// <summary>
/// A world-space, scrollable Twitch chat overlay (BSML FloatingScreen + the
/// authentic round-rect-panel card look). Visible in BOTH the menu and during
/// songs, taller than wide by default, spawns ahead of the player / higher up /
/// centered, and is off unless explicitly enabled. Remembers the last ~100
/// chat messages, auto-scrolls to the newest line, and can be scrolled with the
/// game's laser (menu) or the scroll wheel.
///
/// Each message is fully sanitized so viewer text can never inject TMP rich-text
/// tags or control characters. Usernames are colored from the viewer's Twitch
/// chat color when they have one (unless forced), otherwise the configured name
/// color; message text uses the configured text color. Badges (mod/vip/sub/...)
/// are shown as small tags. A lock button toggles the grab handle so the panel
/// can be parked without being nudged.
///
/// The BSML grab handle is re-sculpted from its stock wide rectangle into a
/// small cube parked fully below the canvas bottom edge, so it never clips the
/// chat card.
/// </summary>
internal sealed class ChatPanelController : MonoBehaviour
{
    private const int MaxMessages = 100;
    private const int MaxBuildRetries = 1200;

    // Default spawn point/rotation. The Chat settings "Reset Position" button
    // writes these exact values, and PluginConfig's fresh-install defaults
    // reference them, so a brand-new config file lands in the same spot and
    // rotation as a reset.
    internal static readonly Vector3 DefaultPosition = new(-1.84f, 3.76f, 3.73f);
    internal static readonly Quaternion DefaultRotation = Quaternion.Euler(346.95f, 324.34f, 0f);
    private const float ContentPadding = 1.5f;

    // Grab handle is sculpted into a cube this size (canvas units; the floating
    // screen is world-scaled by 0.02, so 5 units = 0.1 world), parked this far
    // below the canvas bottom edge so it clears the chat card.
    private const float HandleCubeSize = 5f;
    private const float HandleGap = 3f;

    private static readonly string LockResource = "StreamReactive.Resources.Lock.png";
    private static readonly string UnlockResource = "StreamReactive.Resources.Unlock.png";

    private static readonly Regex InlineTagRegex = new(@"<[^>]{0,64}>", RegexOptions.Compiled);

    // BSML's DiContainer is null (and its CreateFloatingScreen NREs + leaks an
    // orphaned canvas) until Zenject has wired the menu container. There is no
    // public readiness flag, so peek at the private field - reading it does not
    // trigger BSML's own "DiContainer too early" error log.
    private static readonly FieldInfo? DiContainerField =
        typeof(BeatSaberUI).GetField("diContainer", BindingFlags.NonPublic | BindingFlags.Static);

    private static ChatPanelController? _instance;

    private readonly List<string> _history = new();
    private FloatingScreen? _screen;
    private RectTransform? _cardRect;
    private RectTransform? _rootRect;
    private RectTransform? _textRect;
    private RectTransform? _lockRect;
    private ScrollRect? _scrollRect;
    private TextMeshProUGUI? _text;
    private RawImage? _lockIcon;
    private Texture2D? _lockTex;
    private Texture2D? _unlockTex;
    private CanvasGroup? _lockGroup;
    private bool _pendingBuild;
    private int _buildRetries;
    private bool _gaveUp;
    private bool _wasEnabled;
    private Exception? _lastError;
    private bool _textDirty;
    private bool _locked;
    private bool _userScrolledUp;
    private float _builtWidth;
    private float _builtHeight;
    private float _builtFontSize;

    internal static ChatPanelController? Instance => _instance;

    internal static void Init()
    {
        if (_instance != null) return;

        var go = new GameObject("StreamReactiveChatPanel");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<ChatPanelController>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
#pragma warning disable CS0618
        BSEvents.menuSceneActive += OnSceneActive;
        BSEvents.gameSceneActive += OnSceneActive;
#pragma warning restore CS0618
    }

    private void OnDestroy()
    {
#pragma warning disable CS0618
        BSEvents.menuSceneActive -= OnSceneActive;
        BSEvents.gameSceneActive -= OnSceneActive;
#pragma warning restore CS0618
        DestroyPanel();
        DestroyIconTextures();
        if (_instance == this) _instance = null;
    }

    private void Start()
    {
        var scene = SceneManager.GetActiveScene().name;
        if (IsUsableScene(scene))
            RequestBuild();
    }

    private static bool IsUsableScene(string name)
    {
        return name.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("game", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // True once BSML's Zenject DiContainer is wired (its FloatingScreen/UI
    // helpers depend on it). Polymorphic fallback: MainTextFont is populated
    // shortly after, so a missing field still unblocks eventually.
    private static bool IsBsmlReady()
    {
        if (DiContainerField != null)
        {
            try { return DiContainerField.GetValue(null) != null; }
            catch { /* fall through */ }
        }
        return BeatSaberUI.MainTextFont != null;
    }

    // Rebuild on whichever scene becomes active (menu OR song) so the panel is
    // visible in both. Existing screen objects don't survive a scene load, so
    // clear any stale reference before rebuilding.
    private void OnSceneActive()
    {
        DestroyPanel();
        _gaveUp = false;
        RequestBuild();
    }

    private void Update()
    {
        var cfg = PluginConfig.Instance;
        var enabled = cfg?.ChatEnabled ?? false;

        // Fresh enable (or re-enable) clears any previous give-up so the panel
        // can build again.
        if (enabled != _wasEnabled)
        {
            if (enabled) _gaveUp = false;
            _wasEnabled = enabled;
        }

        if (enabled && _screen == null && !_pendingBuild && !_gaveUp)
            RequestBuild();
        if (!enabled && _screen != null)
            DestroyPanel();

        if (enabled && _screen != null && cfg != null)
        {
            if (cfg.ChatWidth != _builtWidth || cfg.ChatHeight != _builtHeight)
            {
                _builtWidth = Mathf.Max(10f, cfg.ChatWidth);
                _builtHeight = Mathf.Max(10f, cfg.ChatHeight);
                ApplyLayout();
                RefreshText();
            }

            var targetFontSize = Mathf.Max(1f, cfg.ChatFontSize);
            if (Math.Abs(targetFontSize - _builtFontSize) > 0.01f)
            {
                _builtFontSize = targetFontSize;
                if (_text != null)
                    _text.fontSize = _builtFontSize;
                RefreshText();
            }
        }

        // Apply freshly arrived chat lines once per frame. Scroll to the newest
        // line only if the user was already at the bottom (reading history is
        // never yanked); content height is re-measured first so the newest line
        // is not left partially below the clip mask.
        if (_textDirty)
        {
            _textDirty = false;
            if (_text != null)
            {
                var pinnedRect = _scrollRect;
                if (pinnedRect != null && pinnedRect.verticalNormalizedPosition <= 0.005f)
                    _userScrolledUp = false;
                _text.text = string.Join("\n", _history);
                if (!_userScrolledUp && pinnedRect != null)
                {
                    Canvas.ForceUpdateCanvases();
                    pinnedRect.StopMovement();
                    pinnedRect.verticalNormalizedPosition = 0f;
                }
            }
        }
        if (!_pendingBuild) return;

        // Don't waste attempts or spam the log while no menu/game scene is up.
        if (!IsUsableScene(SceneManager.GetActiveScene().name))
            return;

        // Wait silently for BSML/Zenject to be ready. Attempting earlier just
        // NREs inside CreateFloatingScreen AND leaks an orphaned canvas with
        // a VRGraphicRaycaster on every attempt - the source of the log floods.
        if (!IsBsmlReady())
            return;

        if (_buildRetries > MaxBuildRetries)
        {
            _pendingBuild = false;
            _gaveUp = true;
            Plugin.Log.Warn($"ChatPanel: gave up building after {MaxBuildRetries} attempts. Last error:\n{_lastError}");
            return;
        }
        _buildRetries++;
        try
        {
            BuildPanel();
            _pendingBuild = false;
            _gaveUp = false;
            _lastError = null;
            if (_history.Count > 0) RefreshText();
        }
        catch (Exception ex)
        {
            _lastError = ex;
            if (_buildRetries == 1 || _buildRetries % 120 == 0)
                Plugin.Log.Warn($"ChatPanel: build failed (attempt {_buildRetries}): {ex}");
        }
    }

    private void RequestBuild()
    {
        if (_screen != null) return;
        if (!(PluginConfig.Instance?.ChatEnabled ?? false)) return;
        _pendingBuild = true;
        _buildRetries = 0;
    }

    private void BuildPanel()
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return;
        _builtWidth = Mathf.Max(10f, cfg.ChatWidth);
        _builtHeight = Mathf.Max(10f, cfg.ChatHeight);
        _builtFontSize = Mathf.Max(1f, cfg.ChatFontSize);

        _locked = cfg.ChatLocked;
        _userScrolledUp = false;

        LoadIconTextures();

        _screen = FloatingScreen.CreateFloatingScreen(
            new Vector2(_builtWidth, _builtHeight),
            true,
            IsZero(cfg.ChatLastPos) ? DefaultPosition : cfg.ChatLastPos,
            GetInitialRotation(cfg));
        if (_screen == null) throw new InvalidOperationException("FloatingScreen.CreateFloatingScreen returned null.");

        _screen.gameObject.name = "StreamReactiveChatPanel";
        // FloatingScreen may add a Mask or RectMask2D that clips children to
        // canvas bounds. Remove both so the lock button can float outside the
        // right edge.
        var mask = _screen.GetComponent<RectMask2D>();
        if (mask != null) UnityEngine.Object.Destroy(mask);
        var mask2 = _screen.GetComponent<Mask>();
        if (mask2 != null) UnityEngine.Object.Destroy(mask2);
        _screen.ShowHandle = !_locked;
        _screen.HandleSide = FloatingScreen.Side.Bottom;
        _screen.HandleReleased += OnHandleReleased;

        CreateCard();
        CreateScrollArea();
        CreateLockButton();
    }

    private static bool IsZero(Vector3 v) => v == Vector3.zero;

    private void CreateCard()
    {
        if (PluginConfig.Instance == null) return;
        var card = new GameObject("Card", typeof(RectTransform));
        card.transform.SetParent(_screen!.transform, false);
        card.layer = 5;

        var bg = card.AddComponent<Backgroundable>();
        bg.ApplyBackground("round-rect-panel");

        _cardRect = card.GetComponent<RectTransform>();
        ApplyLayout();
    }

    private static float Margin(PluginConfig cfg)
    {
        return Mathf.Min(1.5f, cfg.ChatWidth * 0.06f, cfg.ChatHeight * 0.06f);
    }

    private void CreateScrollArea()
    {
        if (PluginConfig.Instance == null) return;

        var root = new GameObject("ScrollRoot", typeof(RectTransform), typeof(ScrollRect));
        root.transform.SetParent(_screen!.transform, false);
        root.layer = 5;

        var viewport = new GameObject("Viewport", typeof(RectTransform));
        viewport.transform.SetParent(root.transform, false);
        viewport.layer = 5;
        var viewportRt = (RectTransform)viewport.transform;
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.pivot = new Vector2(0.5f, 0.5f);
        viewportRt.offsetMin = Vector2.zero;
        viewportRt.offsetMax = Vector2.zero;

        var viewportImage = viewport.AddComponent<ImageView>();
        viewportImage.sprite = BeatSaberMarkupLanguage.Utilities.ImageResources.WhitePixel;
        viewportImage.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
        viewportImage.raycastTarget = true;
        viewportImage.color = new Color(1f, 1f, 1f, 0f);
        viewportImage.type = Image.Type.Simple;
        viewport.AddComponent<RectMask2D>();

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewport.transform, false);
        content.layer = 5;
        var contentRt = (RectTransform)content.transform;
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.anchoredPosition = Vector2.zero;
        contentRt.sizeDelta = new Vector2(0f, 0f);

        var contentLayout = content.AddComponent<VerticalLayoutGroup>();
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        // Unity layout padding is integer-only, so 1.5 is represented by the
        // nearest usable value here.
        var contentPadding = Mathf.RoundToInt(ContentPadding);
        contentLayout.padding = new RectOffset(contentPadding, contentPadding, contentPadding, contentPadding);

        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var text = BeatSaberUI.CreateText(
            contentRt,
            string.Empty,
            Vector2.zero,
            new Vector2(_builtWidth, 400f));
        text.fontSize = _builtFontSize;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        text.overflowMode = TextOverflowModes.Overflow;
        _text = text;
        _textRect = text.rectTransform;
        _rootRect = root.GetComponent<RectTransform>();
        ApplyLayout();

        var scrollRect = root.GetComponent<ScrollRect>();
        scrollRect.content = contentRt;
        scrollRect.viewport = viewportRt;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.135f;
        scrollRect.scrollSensitivity = 6f;
        _scrollRect = scrollRect;
        scrollRect.onValueChanged.AddListener(OnChatScrollChanged);
    }

    private void OnChatScrollChanged(Vector2 position)
    {
        // Normalized 0 = bottom (newest), 1 = top. Any user input that moves
        // the view away from the bottom means they're reading history; stop
        // auto-pinning until they're back at the bottom (or a new message
        // catches them up). Our own programmatic pins report ~0 and no-op.
        if (position.y > 0.02f)
            _userScrolledUp = true;
    }

    private void CreateLockButton()
    {
        var go = new GameObject("LockButton", typeof(RectTransform));
        go.transform.SetParent(_screen!.transform, false);
        go.layer = 5;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(5f, 1f);
        rt.sizeDelta = new Vector2(8f, 8f);
        _lockRect = rt;

        var bg = go.AddComponent<Image>();
        bg.sprite = BeatSaberMarkupLanguage.Utilities.ImageResources.WhitePixel;
        bg.type = Image.Type.Simple;
        bg.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
        bg.color = new Color(1f, 1f, 1f, 0.12f);
        bg.raycastTarget = true;

        var iconGo = new GameObject("Icon", typeof(RectTransform));
        iconGo.transform.SetParent(go.transform, false);
        iconGo.layer = 5;
        var iconRT = iconGo.GetComponent<RectTransform>();
        iconRT.anchorMin = Vector2.zero;
        iconRT.anchorMax = Vector2.one;
        iconRT.offsetMin = new Vector2(1.6f, 1.6f);
        iconRT.offsetMax = new Vector2(-1.6f, -1.6f);
        var icon = iconGo.AddComponent<RawImage>();
        icon.raycastTarget = false;
        _lockIcon = icon;

        var button = go.AddComponent<Button>();
        button.targetGraphic = bg;
        button.transition = Selectable.Transition.ColorTint;
        button.colors = new ColorBlock
        {
            normalColor = new Color(1f, 1f, 1f, 0.12f),
            highlightedColor = new Color(1f, 1f, 1f, 0.25f),
            pressedColor = new Color(1f, 1f, 1f, 0.35f),
            selectedColor = new Color(1f, 1f, 1f, 0.12f),
            disabledColor = new Color(1f, 1f, 1f, 0.1f),
            colorMultiplier = 1f,
            fadeDuration = 0.1f,
        };
        button.onClick.AddListener(ToggleLock);
        _lockGroup = go.AddComponent<CanvasGroup>();

        RefreshLockVisual();
    }

    private void ToggleLock()
    {
        _locked = !_locked;
        if (_screen != null)
        {
            _screen.ShowHandle = !_locked;
            // Turning the handle back on (re)creates the cube - re-sculpt it.
            ApplyHandleLayout();
        }
        if (PluginConfig.Instance != null) PluginConfig.Instance.ChatLocked = _locked;
        RefreshLockVisual();
        Plugin.Log.Debug($"ChatPanel: {( _locked ? "locked" : "unlocked" )}.");
    }

    private void RefreshLockVisual()
    {
        if (_lockIcon == null) return;
        _lockIcon.texture = _locked ? _lockTex : _unlockTex;
        _lockIcon.color = Color.white;
        if (_lockGroup != null)
            _lockGroup.alpha = _locked ? 0.25f : 1f;
    }

    /// <summary>
    /// Appends a chat message (called on the main thread). Uses the viewer's
    /// Twitch chat color for their name when they have one (unless forced),
    /// otherwise the configured name color; message text uses the configured
    /// text color. Badges (mod/vip/sub/...) are shown as small tags. Remembers
    /// up to <see cref="MaxMessages"/> and auto-scrolls to the newest line.
    /// </summary>
    internal void AddMessage(string user, string message, string? twitchColorHex = null, string badges = "")
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null || !cfg.ChatEnabled) return;

        var nameHex = (!cfg.ChatForceNameColor) ? (ParseHexColor(twitchColorHex) ?? ColorToHex(cfg.ChatNameColor)) : ColorToHex(cfg.ChatNameColor);
        var textHex = ColorToHex(cfg.ChatTextColor);

        var safeUser = Sanitize(user);
        var safeText = Sanitize(message);
        var safeBadges = Sanitize(badges);
        if (safeUser.Length == 0 && safeText.Length == 0) return;
        if (safeUser.Length == 0) safeUser = "?";
        if (safeText.Length == 0) safeText = "";

        var badgePart = safeBadges.Length > 0 ? $"<color=#999999>{safeBadges}</color> " : string.Empty;
        var line = $"{badgePart}<color=#{nameHex}>{safeUser}</color>: <color=#{textHex}>{safeText}</color>";
        _history.Add(line);

        var overflow = _history.Count - MaxMessages;
        if (overflow > 0)
            _history.RemoveRange(0, overflow);

        // Coalesced: the single Update pass applies the text and fixes the
        // scroll position once per frame.
        _textDirty = true;
    }

    private void RefreshText()
    {
        if (_text == null) return;
        _text.text = string.Join("\n", _history);
        if (_scrollRect != null)
        {
            // Content height only updates during the layout pass; scroll AFTER
            // that so the newest line sits fully inside the clip mask.
            Canvas.ForceUpdateCanvases();
            _scrollRect.verticalNormalizedPosition = 0f;
        }
    }

    private void ApplyLayout()
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        var inset = Margin(cfg);
        // No lift needed inside the canvas - the custom cube handle sits below
        // its bottom edge (see ApplyHandleLayout).
        var clearance = 0f;
        var innerW = _builtWidth - inset * 2f;
        var innerH = _builtHeight - inset * 2f - clearance;

        // Resize the canvas root to the configured size. Without this the root's
        // RectMask2D keeps clipping the card and chat at the build-time size, so
        // the panel visually stops growing while the text runs out of bounds.
        if (_screen != null)
            ((RectTransform)_screen.transform).sizeDelta = new Vector2(_builtWidth, _builtHeight);

        if (_cardRect != null)
        {
            _cardRect.anchorMin = new Vector2(0.5f, 0f);
            _cardRect.anchorMax = new Vector2(0.5f, 0f);
            _cardRect.pivot = new Vector2(0.5f, 0f);
            _cardRect.anchoredPosition = new Vector2(0f, clearance);
            _cardRect.sizeDelta = new Vector2(_builtWidth - inset * 2f, _builtHeight - inset * 2f - clearance);
        }

        if (_rootRect != null)
        {
            _rootRect.anchorMin = new Vector2(0.5f, 0f);
            _rootRect.anchorMax = new Vector2(0.5f, 0f);
            _rootRect.pivot = new Vector2(0.5f, 0f);
            _rootRect.anchoredPosition = new Vector2(0f, clearance);
            _rootRect.sizeDelta = new Vector2(innerW - 2f, innerH - 2f);
        }

        if (_lockRect != null)
        {
            _lockRect.anchorMin = new Vector2(1f, 0f);
            _lockRect.anchorMax = new Vector2(1f, 0f);
            _lockRect.pivot = new Vector2(1f, 0f);
            _lockRect.anchoredPosition = new Vector2(5f, clearance + 0.5f);
        }

        if (_textRect != null)
            _textRect.sizeDelta = new Vector2(innerW - 12f, 400f);

        ApplyHandleLayout();
        Canvas.ForceUpdateCanvases();
    }

    // Re-sculpt BSML's stock rectangle grab handle (width = 80% of screen, thin
    // bar) into a small cube parked below the canvas bottom edge so it never
    // overlaps the chat card. The handle keeps its collider + FloatingScreenHandle
    // grab logic, so dragging still just works. Runs whenever the handle is (re)
    // created - including the first unlock after a locked build - and on every
    // size change since the cube is positioned off the bottom edge.
    private void ApplyHandleLayout()
    {
        if (_screen == null) return;
        var handle = _screen.Handle;
        if (handle == null) return;

        var t = handle.transform;
        t.localScale = new Vector3(HandleCubeSize, HandleCubeSize, HandleCubeSize);
        t.localPosition = new Vector3(0f, -_builtHeight * 0.5f - HandleCubeSize * 0.5f - HandleGap, 0f);
    }

    // Strip anything that could drive TMP rendering or layout: rich-text tags
    // and control/escape characters. The rest is plain printable text, so a
    // viewer can never inject things like <size=40> or embedded newlines.
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\r' || c == '\n' || c == '\t')
            {
                sb.Append(' ');
                continue;
            }
            if (c < 0x20 || c == 0x7F)
                continue;
            sb.Append(c);
        }

        var cleaned = InlineTagRegex.Replace(sb.ToString(), string.Empty);

        // Escape any angle brackets that slipped through so they can never form
        // a tag, and ampersands so nothing is ever parsed as an entity.
        return cleaned
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static string? ParseHexColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var t = hex!.TrimStart('#');
        if (t.Length != 6) return null;
        for (var i = 0; i < t.Length; i++)
            if (!Uri.IsHexDigit(t[i])) return null;
        return t.ToUpperInvariant();
    }

    private static string ColorToHex(Color c)
    {
        var cc = (Color32)c;
        return cc.r.ToString("X2") + cc.g.ToString("X2") + cc.b.ToString("X2");
    }

    private void OnHandleReleased(object sender, FloatingScreenHandleEventArgs args)
    {
        if (PluginConfig.Instance != null)
        {
            PluginConfig.Instance.ChatLastPos = args.Position;
            PluginConfig.Instance.ChatLastRotation = _screen != null ? _screen.transform.rotation.eulerAngles : DefaultRotation.eulerAngles;
        }
    }

    internal void ResetPosition()
    {
        if (_screen != null)
            _screen.transform.SetPositionAndRotation(DefaultPosition, DefaultRotation);

        if (PluginConfig.Instance != null)
        {
            PluginConfig.Instance.ChatLastPos = DefaultPosition;
            PluginConfig.Instance.ChatLastRotation = DefaultRotation.eulerAngles;
        }

        _userScrolledUp = false;
        if (_scrollRect != null)
            _scrollRect.verticalNormalizedPosition = 0f;
    }

    private static Quaternion GetInitialRotation(PluginConfig cfg)
    {
        if (!IsZero(cfg.ChatLastRotation))
            return Quaternion.Euler(cfg.ChatLastRotation);

        var pos = IsZero(cfg.ChatLastPos) ? DefaultPosition : cfg.ChatLastPos;
        var toCenter = -pos;
        if (toCenter.sqrMagnitude < 0.0001f)
            return DefaultRotation;

        return Quaternion.LookRotation(toCenter.normalized, Vector3.up);
    }

    private void LoadIconTextures()
    {
        _lockTex ??= LoadPng(LockResource);
        _unlockTex ??= LoadPng(UnlockResource);
    }

    private static Texture2D? LoadPng(string resourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Plugin.Log.Warn($"ChatPanel: embedded resource '{resourceName}' not found.");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(ms.ToArray()))
                return tex;
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"ChatPanel: failed to load '{resourceName}': {ex.Message}");
            return null;
        }
    }

    private void DestroyPanel()
    {
        if (_screen != null)
            _screen.HandleReleased -= OnHandleReleased;
        if (_screen != null)
            Destroy(_screen.gameObject);
        _screen = null;
        _cardRect = null;
        _rootRect = null;
        _textRect = null;
        _scrollRect = null;
        _text = null;
        _lockIcon = null;
        _pendingBuild = false;
    }

    private void DestroyIconTextures()
    {
        if (_lockTex != null) { Destroy(_lockTex); _lockTex = null; }
        if (_unlockTex != null) { Destroy(_unlockTex); _unlockTex = null; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.FloatingScreen;
using BS_Utils.Utilities;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
/// are rendered as their actual Twitch badge images beside the username.
/// A lock button toggles the grab handle so the panel
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

    // Slim, purely visual scrollbar in the chat card's right gutter. It mirrors
    // the ScrollRect's position every frame but never feeds input back into it,
    // so it cannot interfere with dragging the feed or the auto-scroll logic.
    private const float ScrollbarWidth = 1.2f;
    private const float ScrollbarInset = 1.2f;
    private const float ScrollbarEdgePad = 2f;
    private const float ScrollbarMinHandle = 3f;
    // Badge/emote decals are tested against the viewport rect with full-exact
    // bounds tests. Emote-led wrapped lines are placed FLUSH, so their left
    // edge is built to land exactly on -halfW; the world round-trip leaves a
    // sub-pixel epsilon there that flips those (and only those) cells between
    // visible/culled every relayout. Slack keeps the boundary stable without
    // letting anything fallibly out-of-view show.
    private const float DecalClipSlack = 0.65f;

    // Auto-scroll re-arms when the feed is at (or this far above) the newest
    // line. Small but not demanding the exact pixel: 0.02 = 2% of content above
    // the bottom, roughly the newest line still in view.
    private const float AutoResumePosition = 0.02f;

    // Chat badges are rendered as small images beside the username. These values
    // (canvas units) control badge sizing relative to the configured font size.
    private const float BadgeGap = 0.6f;
    private const float BadgeTextGap = 0.8f;
    private const float BadgeZOffset = 0.02f;
    private const float RowGap = 0.45f;
    private const float LinePadding = 0.7f;
    private const float RowMaxTextHeight = 400f;

    private static readonly string LockResource = "StreamReactive.Resources.Lock.png";
    private static readonly string UnlockResource = "StreamReactive.Resources.Unlock.png";

    // BSML's DiContainer is null (and its CreateFloatingScreen NREs + leaks an
    // orphaned canvas) until Zenject has wired the menu container. There is no
    // public readiness flag, so peek at the private field - reading it does not
    // trigger BSML's own "DiContainer too early" error log.
    private static readonly FieldInfo? DiContainerField =
        typeof(BeatSaberUI).GetField("diContainer", BindingFlags.NonPublic | BindingFlags.Static);

    private static ChatPanelController? _instance;

    private readonly List<LineModel> _lines = new();
    // Live row views built from _lines. _lines survives scene loads so the panel
    // re-renders its backlog after a rebuild; _rows are the live GameObjects and
    // are recreated from _lines whenever the panel is rebuilt.
    private readonly List<RowHandle> _rows = new();
    private FloatingScreen? _screen;
    private RectTransform? _cardRect;
    private RectTransform? _rootRect;
    private RectTransform? _contentRt;
    private RectTransform? _viewportRt;
    private RectTransform? _lockRect;
    private RectTransform? _scrollTrackRect;
    private RectTransform? _scrollHandleRect;
    private ScrollRect? _scrollRect;
    private RawImage? _lockIcon;
    private Texture2D? _lockTex;
    private Texture2D? _unlockTex;
    private CanvasGroup? _lockGroup;
    private bool _pendingBuild;
    private int _buildRetries;
    private bool _gaveUp;
    private bool _wasEnabled;
    private Exception? _lastError;
    private bool _layoutDirty;
    private bool _measureAll;
    private bool _locked;
    private bool _userScrolledUp;
    private float _builtWidth;
    private float _builtHeight;
    private float _builtFontSize;
    private bool _textOrthographic;
    private float _emotePegAdvanceUnits;
    private bool _emotePegAdvanceMeasured;
    private float _emoteSpaceAdvanceUnits;
    private bool _emoteSpaceAdvanceMeasured;

    private bool _builtChatEmotes;
    private int _builtSortingOrder;

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

    // Badge/emote decals are world-space SpriteRenderers whose world positions
    // are driven by this canvas. ScrollRect.onValueChanged fires during the
    // ScrollRect's Update - a full frame before the canvas phase re-syncs the
    // children's world matrices - so a visibility decision made there can lag
    // one frame behind the row it follows and flash a decal into the feed's
    // empty padding while scrolling. Re-deriving clip state here, after the
    // canvas has settled, keeps the two in lockstep (cheap: a few dozen decals).
    private void LateUpdate()
    {
        UpdateBadgeVisibility();
        UpdateEmoteClip();
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
                RequestRelayout(true);
            }

            var targetFontSize = Mathf.Max(1f, cfg.ChatFontSize);
            if (Math.Abs(targetFontSize - _builtFontSize) > 0.01f)
            {
                _builtFontSize = targetFontSize;
                // Font size lives on every per-row TMP; recreate the rows so
                // they re-measure against the new size.
                RebuildRows();
                RequestRelayout(true);
            }

            if (cfg.ChatSortingOrder != _builtSortingOrder)
            {
                _builtSortingOrder = cfg.ChatSortingOrder;
                ApplySortingOrder(cfg.ChatSortingOrder);
            }

            // Toggling the emote setting applies to the panel live: turning it
            // ON renders emotes in NEW messages (existing lines never had their
            // spans resolved), turning it OFF drops the decals already shown.
            if (cfg.ChatEmotes != _builtChatEmotes)
            {
                _builtChatEmotes = cfg.ChatEmotes;
                if (!cfg.ChatEmotes)
                    ClearChatEmotes();
            }
        }

        // Apply layout changes (new/removed/resized rows) once per frame. Scroll to
        // the newest line only if the user was already at the bottom (reading
        // history is never yanked); content height is re-measured first so the
        // newest line is not left partially below the clip mask.
        if (_layoutDirty)
        {
            _layoutDirty = false;
            var measureAll = _measureAll;
            _measureAll = false;
            var pinnedRect = _scrollRect;
            if (pinnedRect != null && pinnedRect.verticalNormalizedPosition <= AutoResumePosition)
                _userScrolledUp = false;
            RelayoutRows(measureAll);
            if (!_userScrolledUp && pinnedRect != null)
            {
                Canvas.ForceUpdateCanvases();
                pinnedRect.StopMovement();
                pinnedRect.verticalNormalizedPosition = 0f;
            }
        }

        // Mirror the current scroll position into the slim visual scrollbar.
        if (_scrollTrackRect != null && _scrollHandleRect != null)
            UpdateScrollbar();

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
            if (_lines.Count > 0)
            {
                RebuildRows();
                RequestRelayout(true);
            }
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
        _builtSortingOrder = cfg.ChatSortingOrder;
        _builtChatEmotes = cfg.ChatEmotes;

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
        ApplySortingOrder(cfg.ChatSortingOrder);
        _screen.ShowHandle = !_locked;
        _screen.HandleSide = FloatingScreen.Side.Bottom;
        _screen.HandleReleased += OnHandleReleased;

        CreateCard();
        CreateScrollArea();
        CreateLockButton();
        CreateScrollbar();
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
        _viewportRt = viewportRt;

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
        contentRt.sizeDelta = Vector2.zero;
        _contentRt = contentRt;

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

        // Badge decals are world-space sprites (not canvas Graphics), so the
        // viewport's RectMask2D never clips them. Rather than polling every
        // frame, the ScrollRect reports when its position actually moves via
        // onValueChanged - badges are re-clipped (with a re-laid-out fallback)
        // only when the feed genuinely scrolls. An idle feed does nothing.
        scrollRect.onValueChanged.AddListener(_ =>
        {
            UpdateBadgeVisibility();
            UpdateEmoteClip();
        });

        // The monitor sits on the SAME object as the ScrollRect (never on the
        // viewport - that would shadow the ScrollRect and steal its drags). It
        // arms the "user is reading history" flag on any genuine drag or wheel
        // scroll; ScrollRect's layout-settle events never touch it, and our own
        // pins don't either, so auto-scroll keeps working through the fill
        // transition. Returning to the bottom re-arms on the next message (the
        // dirty handler samples the position first).
        var monitor = root.AddComponent<ScrollMonitor>();
        monitor.Owner = this;
    }

    // Arms the pause flag only on real input (drag start / wheel). Lives on the
    // ScrollRect's own GameObject so EventSystem still hands drags to both. The
    // flag is cleared only by sampling the position at the END of an action that
    // heads toward the newest line - wheel-scrolling down or releasing a drag at
    // the bottom - so auto-scroll re-arms event-driven, never per frame.
    private sealed class ScrollMonitor : MonoBehaviour, IBeginDragHandler, IScrollHandler, IEndDragHandler
    {
        public ChatPanelController? Owner;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Owner != null) Owner._userScrolledUp = true;
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Owner == null) return;
            Owner._userScrolledUp = true;
            // Wheel rolled down = moving toward the newest line: if that lands
            // at the bottom, re-arm immediately. Rolling back into history
            // leaves the flag armed and never samples.
            if (eventData.scrollDelta.y < 0f) Owner.SampleAutoResume();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (Owner != null) Owner.SampleAutoResume();
        }
    }

    // Event-driven auto-scroll re-arm. Callers (scroll drag end, wheel scrolling
    // toward the newest line) sample the feed's position ONLY when the user has
    // actually moved it - nothing runs per frame. The user is treated as "back
    // at the bottom" as soon as the feed is within AutoResumePosition of it, so
    // the next message re-pins without yanking readers who are still in history.
    private void SampleAutoResume()
    {
        if (_scrollRect != null && _scrollRect.verticalNormalizedPosition <= AutoResumePosition)
            _userScrolledUp = false;
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

    // Slim, purely visual scrollbar: a faint track plus a handle whose size
    // reflects the visible portion and whose position mirrors the ScrollRect.
    // It is a sibling of the card (not inside the viewport mask) so it is never
    // clipped, and it never accepts input - a representation only.
    private void CreateScrollbar()
    {
        if (_screen == null) return;

        var trackGo = new GameObject("Scrollbar", typeof(RectTransform));
        trackGo.transform.SetParent(_screen.transform, false);
        trackGo.layer = 5;
        var trackRt = (RectTransform)trackGo.transform;
        _scrollTrackRect = trackRt;

        var track = trackGo.AddComponent<Image>();
        track.sprite = BeatSaberMarkupLanguage.Utilities.ImageResources.WhitePixel;
        track.type = Image.Type.Simple;
        track.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
        track.color = new Color(1f, 1f, 1f, 0.06f);
        track.raycastTarget = false;

        var handleGo = new GameObject("Handle", typeof(RectTransform));
        handleGo.transform.SetParent(trackRt, false);
        handleGo.layer = 5;
        var handleRt = (RectTransform)handleGo.transform;
        handleRt.anchorMin = new Vector2(0.5f, 1f);
        handleRt.anchorMax = new Vector2(0.5f, 1f);
        handleRt.pivot = new Vector2(0.5f, 1f);
        handleRt.anchoredPosition = Vector2.zero;
        handleRt.sizeDelta = new Vector2(ScrollbarWidth, 0f);
        _scrollHandleRect = handleRt;

        var handle = handleGo.AddComponent<Image>();
        handle.sprite = BeatSaberMarkupLanguage.Utilities.ImageResources.WhitePixel;
        handle.type = Image.Type.Simple;
        handle.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
        handle.color = new Color(1f, 1f, 1f, 0.3f);
        handle.raycastTarget = false;

        LayoutScrollbar();
    }

    // Position / resize the scrollbar track so it sits flush inside the card's
    // right gutter, matching the card's bottom-anchored growth when the panel
    // height or font size changes.
    private void LayoutScrollbar()
    {
        if (_scrollTrackRect == null || _screen == null) return;

        var inset = Margin(PluginConfig.Instance!);
        var margin = inset + ScrollbarInset;
        var cardH = _builtHeight - inset * 2f;
        var barHeight = cardH - ScrollbarEdgePad * 2f;

        var trackRt = _scrollTrackRect;
        trackRt.anchorMin = new Vector2(1f, 0f);
        trackRt.anchorMax = new Vector2(1f, 0f);
        trackRt.pivot = new Vector2(1f, 0.5f);

        if (barHeight <= 0f)
        {
            trackRt.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            return;
        }

        trackRt.sizeDelta = new Vector2(ScrollbarWidth, barHeight);
        // Bottom-anchored: the track bottom sits at ScrollbarEdgePad from the
        // card bottom, its center is half a barHeight above that.
        trackRt.anchoredPosition = new Vector2(-margin, ScrollbarEdgePad + barHeight * 0.5f);
    }

    // Re-derive handle size/position from the ScrollRect and the actual content
    // height (read after ForceUpdateCanvases, so it reflects the latest lines).
    // Hidden while the feed still fits the viewport.
    private void UpdateScrollbar()
    {
        if (_scrollRect == null || _scrollHandleRect == null || _scrollTrackRect == null) return;
        if (_contentRt == null) return;

        var viewportH = _scrollRect.viewport?.rect.height ?? 0f;
        var contentH = _scrollRect.content?.rect.height ?? 0f;
        if (viewportH <= 0f)
        {
            _scrollHandleRect.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            return;
        }

        if (contentH <= viewportH + 0.01f)
        {
            _scrollHandleRect.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            return;
        }

        var trackH = _scrollTrackRect.rect.height;
        var handleH = Mathf.Max(ScrollbarMinHandle, Mathf.Min(trackH, trackH * viewportH / contentH));
        // This ScrollRect reports 1 = oldest (top), 0 = newest (bottom), so the
        // handle travels down the track as the feed grows.
        var y = Mathf.Clamp01(_scrollRect.verticalNormalizedPosition);
        _scrollHandleRect.sizeDelta = new Vector2(ScrollbarWidth, handleH);
        _scrollHandleRect.anchoredPosition = new Vector2(0f, -(1f - y) * (trackH - handleH));
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
    /// text color. badges is the raw IRC badge list ("moderator/1,vip/1"); the
    /// whitelisted ones are rendered as Twitch badge images beside the name.
    /// Remembers up to <see cref="MaxMessages"/> and auto-scrolls to the newest
    /// line.
    /// </summary>
    internal void AddMessage(string user, string message, string? twitchColorHex = null, string badges = "", string? messageId = null, bool isShared = false, EmoteSpan[]? emotes = null)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null || !cfg.ChatEnabled) return;
        if (isShared && !cfg.ChatPanelAllowSharedChat) return;

        var nameHex = (!cfg.ChatForceNameColor) ? (ParseHexColor(twitchColorHex) ?? ColorToHex(cfg.ChatNameColor)) : ColorToHex(cfg.ChatNameColor);
        var textHex = ColorToHex(cfg.ChatTextColor);

        var safeUser = Sanitize(user);
        var safeText = Sanitize(message);
        if (safeUser.Length == 0 && safeText.Length == 0) return;
        if (safeUser.Length == 0) safeUser = "?";

        // Emote spans (raw, from the reader) are re-found inside the sanitized
        // text: sanitize can shift indices (tags stripped, & < > escaped), and a
        // misplaced decal is worse than none, so each span searches forward from
        // the previous one - order is preserved, so identical consecutive emotes
        // still land correctly.
        var resolvedEmotes = (cfg.ChatEmotes && emotes != null && emotes.Length > 0)
            ? ResolveMessageSpans(safeText, message, emotes)
            : null;

        var model = new LineModel
        {
            IsSystem = false,
            User = user.ToLowerInvariant(),
            Id = messageId ?? "",
            DisplayUser = safeUser,
            Message = safeText,
            NameHex = nameHex,
            TextHex = textHex,
            Badges = Sanitize(badges),
            IsShared = isShared,
            EmoteSpans = resolvedEmotes
        };
        if (resolvedEmotes != null)
        {
            model.EmoteAspects = new List<float>(resolvedEmotes.Count);
            for (var i = 0; i < resolvedEmotes.Count; i++)
                model.EmoteAspects.Add(1f);
        }
        AppendLine(model);
    }

    // Re-finds each raw emote span in the sanitized copy of the message.
    private static List<EmoteSpan>? ResolveMessageSpans(string safeText, string rawMessage, EmoteSpan[] raw)
    {
        var result = new List<EmoteSpan>(raw.Length);
        var cursor = 0;
        foreach (var span in raw)
        {
            if (span.Length <= 0) continue;
            if (span.Start < 0 || span.Start + span.Length > rawMessage.Length) continue;
            var text = rawMessage.Substring(span.Start, span.Length);
            if (text.Length == 0) continue;
            var pos = safeText.IndexOf(text, cursor, StringComparison.Ordinal);
            if (pos < 0) continue;
            result.Add(new EmoteSpan(pos, text.Length, span.Code));
            cursor = pos + text.Length;
        }
        return result.Count > 0 ? result : null;
    }

    // Twitch system event (sub, gift, raid, watch streak, timeout, ban, chat
    // mode change...): a single colored line, distinct from ordinary messages.
    // detail (when present) is the viewer's own text sent with the event (e.g. a
    // resub or watch-streak message) and is shown indented underneath. Toggled
    // by ChatPanelShowSystemEvents.
    internal void AddSystemEvent(string text, string? detail = null, bool isShared = false)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null || !cfg.ChatEnabled || !cfg.ChatPanelShowSystemEvents) return;
        if (isShared && !cfg.ChatPanelAllowSharedChat) return;

        var safeMain = Sanitize(text);
        if (safeMain.Length == 0) return;

        var model = new LineModel
        {
            IsSystem = true,
            User = "",
            Id = "",
            SystemMain = safeMain,
            SystemDetail = Sanitize(detail ?? string.Empty),
            SystemHex = ColorToHex(cfg.ChatSystemEventColor),
            TextHex = ColorToHex(cfg.ChatTextColor),
            IsShared = isShared
        };
        AppendLine(model);
    }

    // Removes the line with the given IRC message id (CLEARMSG / message deleted).
    internal void RemoveMessageById(string id)
    {
        if (PluginConfig.Instance == null || !PluginConfig.Instance.ChatEnabled) return;
        if (id.Length == 0) return;

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Id == id)
            {
                RemoveRowAt(i);
                return;
            }
        }
    }

    // Removes every line from the given user (timeout / ban). login is the
    // lowercased IRC login.
    internal void RemoveMessagesByLogin(string login)
    {
        if (PluginConfig.Instance == null || !PluginConfig.Instance.ChatEnabled) return;
        if (login.Length == 0) return;

        var needle = login.ToLowerInvariant();
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].User == needle)
                RemoveRowAt(i);
        }
    }

    // Clears the whole feed (CLEARCHAT for the whole room).
    internal void ClearChat()
    {
        if (PluginConfig.Instance == null || !PluginConfig.Instance.ChatEnabled) return;
        foreach (var row in _rows)
            if (row != null && row.Rect != null)
                Destroy(row.Rect.gameObject);
        _rows.Clear();
        _lines.Clear();
        _userScrolledUp = false;
        RequestRelayout(true);
    }

    // Adds one line model to the backlog and, when the panel is built, creates
    // its live row. Trims the oldest lines past MaxMessages, keeping _lines and
    // _rows in sync.
    private void AppendLine(LineModel model)
    {
        _lines.Add(model);
        if (_contentRt != null)
        {
            var row = CreateRow(model);
            _rows.Add(row);
            // A texture that was already cached applies its aspect DURING row
            // creation, before the row is in _rows - UpdateEmoteAspect bails on
            // the missing row and the aspect never reaches the model, so a wide
            // emote would keep a square cell forever. Re-run it now that the row
            // exists (no-op when the aspect already matched).
            SyncRowEmoteAspects(row);
            while (_lines.Count > MaxMessages)
                RemoveRowAt(0);
            // Lay the feed out SYNCHRONOUSLY so the very first frame that
            // renders this row already shows it in its final spot (measured
            // height, stacked position, correct text width, grown content).
            // Deferring to Update() left a window of up to a frame where the
            // new message existed but with stale dimensions, flashing at the
            // wrong place before snapping. Appends are rare enough that the
            // extra measure (only the new row, NeedsMeasure) is negligible.
            // The flags are left set so Update()'s own pass this/next frame is
            // idempotent (same layout, no visual movement).
            RelayoutRows(_measureAll);
            if (!_userScrolledUp && _scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                _scrollRect.StopMovement();
                _scrollRect.verticalNormalizedPosition = 0f;
            }
        }
        else
        {
            while (_lines.Count > MaxMessages)
                _lines.RemoveAt(0);
        }
    }

    // Destroys a live row and removes its model. The remaining rows keep their
    // cached heights; only positions need to be re-derived. The row's GameObject
    // is deactivated BEFORE the deferred Destroy so it never renders a stale
    // frame at its old slot after the stack has already moved on.
    private void RemoveRowAt(int idx)
    {
        var row = idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
        if (idx >= 0 && idx < _rows.Count) _rows.RemoveAt(idx);
        if (idx >= 0 && idx < _lines.Count) _lines.RemoveAt(idx);
        if (row != null && row.Rect != null)
        {
            row.Rect.gameObject.SetActive(false);
            Destroy(row.Rect.gameObject);
        }
        RequestRelayout(false);
    }

    // Recreates every live row from the stored models (used after a scene load
    // rebuilds the panel, and when the font size changes).
    private void RebuildRows()
    {
        foreach (var row in _rows)
            if (row != null && row.Rect != null)
                Destroy(row.Rect.gameObject);
        _rows.Clear();
        if (_contentRt == null) return;
        for (var i = 0; i < _lines.Count; i++)
            _rows.Add(CreateRow(_lines[i]));
    }

    // Flag a pending layout pass. measureAll forces every row to re-measure its
    // text height; otherwise only rows marked NeedsMeasure are re-measured.
    internal void RequestRelayout(bool measureAll)
    {
        if (measureAll) _measureAll = true;
        _layoutDirty = true;
    }

    // Re-measures (when asked) and re-stacks every row, then resizes the content
    // scroll area to fit. Called once per frame from Update.
    private void RelayoutRows(bool measureAll)
    {
        if (_contentRt == null) return;

        var innerW = ContentWidth();
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row == null || row.Rect == null) continue;
            if (measureAll || row.NeedsMeasure)
            {
                // Reserve the scrollbar gutter (inset + track) so wrapped text
                // never runs underneath the slim scrollbar at the right edge.
                row.TextWidth = Mathf.Max(10f, innerW - ScrollbarInset - ScrollbarWidth);
                if (row.TextRect != null)
                    row.TextRect.sizeDelta = new Vector2(row.TextWidth, RowMaxTextHeight);
                MeasureRow(row);
            }
        }

        var y = ContentPadding;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row == null || row.Rect == null) continue;
            row.Rect.anchoredPosition = new Vector2(0f, -y);
            y += row.Height + RowGap;
        }

        _contentRt.sizeDelta = new Vector2(0f, Mathf.Max(0f, y - RowGap + ContentPadding));

        // Badge decals are positioned in row-local units that depend on the row
        // width, which only settles after the stacking pass above.
        UpdateBadgePositions();

        // Emote decals derive their placement from the TMP layout, which is
        // final once every row above was measured and stacked.
        PlaceChatEmotes();

        // Relayouts can shift rows under the clip without a scroll event (new
        // message while pinned, resizes), so refresh the decal clip state here
        // as well. Runs on layout change only - not every frame.
        UpdateBadgeVisibility();
        UpdateEmoteClip();
    }

    // Measures the wrapped height of a row's text, caches it and sizes the row.
    private void MeasureRow(RowHandle row)
    {
        var fallback = _builtFontSize * 1.4f;
        var h = MeasureTextHeight(row.Text, fallback);
        row.Height = Mathf.Max(h, fallback) + LinePadding;
        if (row.Rect != null)
            row.Rect.sizeDelta = new Vector2(0f, row.Height);
        row.NeedsMeasure = false;
    }

    // Height of the rendered (wrapped) text in canvas units. The authoritative
    // measure is the generated layout itself: after ForceMeshUpdate, textInfo.line
    // sum the per-line lineHeights, which is exactly how tall the wrapped text
    // really is. textBounds / world mesh bounds are only fallbacks when the line
    // info is unavailable. The result is clamped only very generously, so a
    // pathological measurement can never stretch a row to the full 400-unit text
    // rect (which would overlap the whole feed). Long messages must NOT be
    // truncated here: if they were clamped low, their wrapped lines would be
    // taller than the measured row and the NEXT message would draw on top of
    // them (the "message below clips into the one above" bug).
    private static float MeasureTextHeight(TextMeshProUGUI? text, float fallback)
    {
        if (text == null) return fallback;
        try
        {
            text.ForceMeshUpdate();
            var h = 0f;
            var ti = text.textInfo;
            if (ti != null && ti.lineCount > 0)
            {
                var n = Mathf.Min(ti.lineCount, ti.lineInfo.Length);
                for (var i = 0; i < n; i++)
                    h += ti.lineInfo[i].lineHeight;
            }
            if (h <= 0f) h = text.textBounds.size.y;
            if (h <= 0f)
            {
                var scale = text.rectTransform.lossyScale.y;
                if (scale > 0.0001f) h = text.bounds.size.y / scale;
            }
            if (h > 0f) return Mathf.Clamp(h, 0f, fallback * 40f);
        }
        catch { }
        return fallback;
    }

    // The usable width for a row. The content rect is stretched to the viewport,
    // but before the first canvas layout runs it reads 0, so fall back to the
    // configured size minus the card margins.
    private float ContentWidth()
    {
        if (_contentRt != null)
        {
            var w = _contentRt.rect.width;
            if (w >= 10f) return w;
        }
        var cfg = PluginConfig.Instance;
        var inset = cfg != null ? Margin(cfg) : 1.5f;
        return Mathf.Max(10f, _builtWidth - inset * 2f - 2f);
    }

    // Creates a live row for a model: badge images first (when any are allowed),
    // then the TMP "name: message" text. The text height is measured by the next
    // RelayoutRows pass, not here, so first-frame height is not dependent on
    // stale canvas state.
    private RowHandle CreateRow(LineModel model)
    {
        var go = new GameObject(model.IsSystem ? "SystemRow" : "ChatRow", typeof(RectTransform));
        go.layer = 5;
        go.transform.SetParent(_contentRt!, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;

        var handle = new RowHandle { Rect = rt, Model = model };

        var text = BeatSaberUI.CreateText(
            rt,
            string.Empty,
            Vector2.zero,
            new Vector2(10f, RowMaxTextHeight));
        var textRt = text.rectTransform;
        textRt.anchorMin = new Vector2(0f, 1f);
        textRt.anchorMax = new Vector2(0f, 1f);
        textRt.pivot = new Vector2(0f, 1f);
        // The text spans the full row width (minus the scrollbar gutter). Set it
        // at creation (not just on relayout) so even the pre-layout frame wraps
        // at the real width instead of a narrow column; RelayoutRows refines it
        // against the settled content width.
        var createWidth = Mathf.Max(10f, ContentWidth() - ScrollbarInset - ScrollbarWidth);
        textRt.sizeDelta = new Vector2(createWidth, RowMaxTextHeight);
        handle.TextWidth = createWidth;
        // The username on line 1 is indented past the badges via a <pos> mark
        // in ComposeText, while wrapped lines continue at the full panel width.
        textRt.anchoredPosition = Vector2.zero;
        text.fontSize = _builtFontSize;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        // TMP scales em-unit <space>/<mspace> tag values by (isOrthographic ? 1 : 0.1)
        // at parse time, and the cell math in EmoteCellUnits needs to know which
        // side of that factor this text lands on. Read the object's own flag (it
        // defaults to false in the canvased Beat Saber UI, so the 10x branch is
        // the norm).
        _textOrthographic = text.isOrthographic;

        // The 🔗 shared marker renders at the row's FAR left, BEFORE the badges.
        // Measure its advance in the text's units so the badges below can be
        // pushed right past it and nothing overlaps. The marker is a bare glyph
        // (no trailing space in the composed text), so this measures exactly the
        // visible advance; the badge/name gaps after it use BadgeGap/BadgeTextGap
        // so its spacing matches the badges' own margins.
        float sharedMarkerWidth = 0f;
        if (model.IsShared)
        {
            try { sharedMarkerWidth = text.GetPreferredValues("<color=#999999>🔗</color>").x; }
            catch (Exception) { sharedMarkerWidth = 0f; }
        }

        float badgeArea = 0f;
        var markerGap = model.IsShared ? BadgeGap : 0f;
        if (!model.IsSystem)
        {
            var keys = ParseBadgeKeys(model.Badges);
            if (keys.Count > 0)
            {
                var size = BadgeSize();
                var x = sharedMarkerWidth + markerGap;
                foreach (var key in keys)
                {
                    CreateBadge(rt, key, x, size);
                    x += size + BadgeGap;
                }
                // End of the last badge measured from the row's left edge (which
                // includes the marker and its gap once); TextX = that + BadgeTextGap.
                badgeArea = Mathf.Max(0f, x - BadgeGap);
            }
        }
        handle.BadgeArea = badgeArea;
        handle.TextX = badgeArea > 0f
            ? badgeArea + BadgeTextGap
            : sharedMarkerWidth + (model.IsShared ? BadgeTextGap : 0f);

        text.text = ComposeText(text, model, handle.TextX, model.EmoteSpans, handle.EmoteRichStarts);
        handle.Text = text;
        handle.TextRect = textRt;
        handle.NeedsMeasure = true;
        CreateChatEmotes(handle);
        return handle;
    }

    // One-line row: a 🔗 (shared) marker sits at the panel's FAR left, then badges
    // (separate sprite decals, pushed past the marker's width), then the
    // "Name: message" text. The <pos> mark indents only the FIRST line so it
    // starts past the markers/badges; when the message wraps it snaps back to the
    // full-width left edge, using the space under them.
    //
    // Emote words become fixed-width text cells: a <space> tag reserves exactly as
    // much horizontal layout as the emote's box needs (square like a badge,
    // widened for wide emotes), followed by a U+200B zero-width space ANCHOR - a
    // character whose origin in the generated layout is the pen AFTER the gap, so
    // the decal can place the image exactly on the reserved box. On its own that
    // run can NEVER wrap - the tag advances the pen without emitting a glyph, and
    // TMP only breaks a line when an overflowing BASE GLYPH triggers a restore (a
    // space is a break candidate but never a cause), so a run of cells stayed one
    // unbreakable line that Overflow mode drew off the window's right edge.
    //
    // Every cell therefore also carries an invisible FULL-SIZE "peg" - the base
    // glyphs 'H' + 'g' tinted alpha-0, wrapped between two <space> tags:
    //   <space=<K+peg>em><color=#00000000>Hg</color><space=<K>-peg>em> 
    // The pegs do both jobs a bare <space> tag cannot:
    //  - Wrap trigger: real base glyphs AFTER the box's <space> tag, so TMP's
    //    wrap logic fires exactly when the next cell's pen would pass the line
    //    width and that cell lands on a new line.
    //  - Line height: TMP derives each line's y-offset from the glyphs ON it
    //    (offset = prev descender + ascender + line gap), so a line whose only
    //    glyphs are tiny collapses toward the line gap and the second line of a
    //    pure-emote run draws on top of the first. 'H' (capital ascender) + 'g'
    //    (a descender) give the line the same box as normal text, so wrapped
    //    all-emote lines stack at the proper height.
    // The peg's own advance (measured once per session) is pushed by the +peg
    // <space> tag and pulled back by the -peg one, keeping the pen's landing
    // point - and so every cell's box - exactly cellW wide. For every emote span
    // the string index of that anchor character is appended to emoteRichStarts
    // (aligned 1:1 with the span list), which the decal uses to locate its box
    // in the generated character layout.
    private string ComposeText(TextMeshProUGUI text, LineModel model, float nameIndent, List<EmoteSpan>? emoteSpans, List<int> emoteRichStarts)
    {
        // The shared marker is a bare glyph with NO trailing space - its advance
        // is measured exactly (see CreateRow) and the badges/name get a proper
        // BadgeGap before them, matching the marker's margin to the badges.
        var sharedPart = model.IsShared ? "<color=#999999>🔗</color>" : string.Empty;
        if (model.IsSystem)
        {
            var line = $"{sharedPart}{(model.IsShared ? " " : string.Empty)}<color=#{model.SystemHex}>» {model.SystemMain}</color>";
            if (model.SystemDetail.Length > 0)
                line += $"\n\u00A0\u00A0<color=#{model.TextHex}>{model.SystemDetail}</color>";
            return line;
        }

        var indent = nameIndent > 0.01f
            ? $"<pos={nameIndent.ToString("0.##", CultureInfo.InvariantCulture)}>"
            : string.Empty;

        var sb = new StringBuilder();
        // The shared marker is composed BEFORE the <pos> indent so it sits at the
        // row's far left, and the badges are laid out from its width (see
        // CreateRow), pushing the name past both.
        sb.Append(sharedPart).Append(indent);
        sb.Append("<color=#").Append(model.NameHex).Append('>').Append(model.DisplayUser).Append(":</color> ");
        sb.Append("<color=#").Append(model.TextHex).Append('>');

        if (emoteSpans == null || emoteSpans.Count == 0 || model.Message.Length == 0)
        {
            sb.Append(model.Message);
        }
        else
        {
            var prev = 0;
            for (var i = 0; i < emoteSpans.Count; i++)
            {
                var span = emoteSpans[i];
                if (span.Start < prev)
                {
                    // Defensive: a span that lost its slot (cannot happen with
                    // resolved spans, which are sorted and non-overlapping) -
                    // emit nothing but keep the 1:1 alignment.
                    emoteRichStarts.Add(-1);
                    continue;
                }
                if (span.Start > prev)
                {
                    // Between two emote cells, whitespace-only separators are
                    // dropped: the cell's own anchor must stay the ONLY wrap
                    // point, because TMP restores a wrapped line to the last
                    // saved whitespace and REPLAYS it on the new line - the
                    // anchor self-cancels via its trailing <space=-space>, but a
                    // literal separator would re-add its width and indent every
                    // wrapped emote line one space from the left. Non-whitespace
                    // gaps stay, so "Kappa hello Kappa" keeps its word breaks.
                    var gapWhitespace = true;
                    for (var j = prev; j < span.Start; j++)
                    {
                        if (!char.IsWhiteSpace(model.Message[j])) { gapWhitespace = false; break; }
                    }
                    if (!gapWhitespace)
                        sb.Append(model.Message, prev, span.Start - prev);
                }
                // The emote word is replaced by a fixed-width transparent box, an
                // invisible full-size peg, its advance-compensating <space> tag,
                // a zero-width space anchor, and (only when a font falls U+200B
                // back to a real space glyph) a trailing tag cancelling its width:
                //   <space=cellW><color=#00000000>Hg</color><space=-peg>\u200B<space=-space>
                // The box <space> tag moves the pen exactly cellW (see
                // EmoteCellUnits); the peg follows as a real BASE GLYPH pair that
                //  - triggers TMP's word-wrap: when the NEXT cell's pen would pass
                //    the text width, the overflow check fires at a peg glyph and
                //    TMP restores to the last saved whitespace (the anchor of the
                //    PREVIOUS cell), starting the overflowing cell on a new line.
                //    Without a base glyph a run of <space> tags can never wrap -
                //    TMP only breaks at overflowing base characters, tag advances
                //    never qualify - so emote runs streamed unbroken off the edge.
                //  - gives the line normal height: TMP offsets each new line from
                //    the glyphs already on it (descender + ascender + line gap), so
                //    a 1%-sized sentinel collapses a pure-emote line to ~the line
                //    gap and the wrapped second line draws on top of the first.
                //    'H' (a capital ascender) + 'g' (a descender) make every cell
                //    line add the same ascender/descender as normal text. The
                //    alpha-0 tint makes the pegs invisible.
                // The -peg <space> tag pulls back the peg's own glyph advance
                // (negative <space> values are supported), so the pen - and the
                // anchor right after it - lands exactly cellW past the box start.
                // The anchor is a U+200B zero-width space: a REAL character whose
                // origin sits on that pen (the box's right edge), so the decal can
                // anchor on the true reserved box and the wrap restore has its
                // break point after every cell. Its own advance is 0, so cells
                // follow edge-to-edge AND a wrapped line that TMP restores to it
                // and replays gets nothing re-added - wrapped emote lines land
                // flush left in every TMP build. Some TMP/font fallbacks give it a
                // plain-space glyph; the measured <space=-space> cancel (see
                // GetEmoteSpaceAdvanceUnits) neutralizes that width exactly.
                // (Whitespace separators between emote cells are dropped so this
                // anchor is the ONLY wrap point; a literal space would be the
                // last saved break and get replayed one width wide.)
                var aspect = model.EmoteAspects != null && i < model.EmoteAspects.Count
                    ? model.EmoteAspects[i]
                    : 1f;
                var cellUnits = EmoteCellUnits(aspect);
                var pegUnits = GetEmotePegAdvanceUnits(text);
                if (pegUnits > 0f)
                {
                    sb.Append("<space=").Append(cellUnits.ToString("0.##", CultureInfo.InvariantCulture)).Append("em>");
                    sb.Append("<color=#00000000>Hg</color>");
                    sb.Append("<space=-").Append(pegUnits.ToString("0.##", CultureInfo.InvariantCulture)).Append("em>");
                }
                else
                {
                    sb.Append("<space=").Append(cellUnits.ToString("0.##", CultureInfo.InvariantCulture)).Append("em>");
                    sb.Append("<color=#00000000>Hg</color>");
                }
                emoteRichStarts.Add(sb.Length); // the anchor char's index - the box's right edge
                // The anchor is a ZERO-WIDTH space (U+200B), not a regular space.
                // It still gives TMP a wrap point (both save it in word-wrap state)
                // and still has a real origin marking the box's right edge, but its
                // own advance is 0 - so when TMP restores a wrapped line to it and
                // REPLAYS it on the new line, nothing is re-added to the pen and
                // wrapped emote lines land flush left. A regular space anchor's
                // advance was replayed relative to the pen state at the restore
                // point, which some TMP builds fail to reset, leaving one space of
                // gap at every wrapped line start. The 200B has no advance to cancel
                // either way; if a font falls U+200B back to a space-width glyph the
                // measured cancel below still neutralizes it exactly like before.
                sb.Append('\u200B');
                var spaceUnits = GetEmoteSpaceAdvanceUnits(text);
                if (spaceUnits > 0f)
                    sb.Append("<space=-").Append(spaceUnits.ToString("0.##", CultureInfo.InvariantCulture)).Append("em>");
                prev = span.Start + span.Length;
            }
            if (prev < model.Message.Length)
                sb.Append(model.Message, prev, model.Message.Length - prev);
        }

        sb.Append("</color>");
        return sb.ToString();
    }

    // "moderator/1,vip/1" -> list of "set/version" keys that we actually render
    // as images (whitelisted sets only).
    private static List<string> ParseBadgeKeys(string badges)
    {
        var keys = new List<string>();
        if (string.IsNullOrEmpty(badges)) return keys;

        foreach (var pair in badges.Split(','))
        {
            if (pair.Length == 0) continue;
            var slash = pair.IndexOf('/');
            if (slash <= 0 || slash >= pair.Length - 1) continue;
            var set = pair.Substring(0, slash);
            var ver = pair.Substring(slash + 1);
            if (ver.Length == 0) continue;
            if (!BadgeCache.IsAllowed(set)) continue;
            keys.Add(pair);
        }
        return keys;
    }

    private float BadgeSize() => Mathf.Max(0.5f, _builtFontSize * 1.15f);

    // Chat emotes use the same default box height as badges: a little taller
    // than the font, roughly the line's ascender-to-descender span.
    private float EmoteSize() => Mathf.Max(0.5f, _builtFontSize * 1.15f);

    // The emote's fixed-width text cell follows its aspect ratio (clamped so a
    // normal square emote renders at EmoteSize and only genuinely wide emotes
    // reserve a wider box - a plot of the width never grows past 3x the font).
    private float EmoteCellWidth(float aspect, float emoteSize) => emoteSize * Mathf.Clamp(aspect, 0.75f, 3f);

    // <space> / <mspace> tag values are expressed in em units (1em = the current
    // font size), and the whole tag value is scaled by (isOrthographic ? 1 : 0.1)
    // at parse time - so a box EmoteCellWidth canvas-units wide needs a tag value
    // of (width / fontSize) * 10 for the non-orthographic UI text Beat Saber
    // uses, and (width / fontSize) for orthographic text. EmoteCellUnits returns
    // that tag value; the decal derives the box from the anchor's real origin
    // afterwards.
    private float EmoteCellUnits(float aspect) => EmoteCellWidth(aspect, EmoteSize()) / Mathf.Max(0.01f, _builtFontSize) * (_textOrthographic ? 1f : 10f);

    // The invisible 'Hg' peg every emote cell carries must reserve its own width
    // via the +peg / -peg <space> tag pair, so we need its real advance expressed
    // in the same em tag units EmoteCellUnits uses. Measured once per session via
    // GetPreferredValues (runs the real layout path for the text's font/size) and
    // cached; every row shares the built font and font size.
    private float GetEmotePegAdvanceUnits(TextMeshProUGUI text)
    {
        if (_emotePegAdvanceMeasured) return _emotePegAdvanceUnits;
        _emotePegAdvanceMeasured = true;
        try
        {
            var px = text != null ? text.GetPreferredValues("<color=#00000000>Hg</color>").x : 0f;
            if (px > 0f)
                _emotePegAdvanceUnits = px / Mathf.Max(0.01f, _builtFontSize) * (_textOrthographic ? 1f : 10f);
        }
        catch (Exception)
        {
            _emotePegAdvanceUnits = 0f;
        }
        return _emotePegAdvanceUnits;
    }

    // The anchor after every emote cell is a REAL glyph; a regular space's own
    // advance would push the next cell a full space further right than the
    // reserved width. The cell now uses a U+200B zero-width space whose native
    // advance is 0 (no cancel needed), but a font that falls U+200B back to a
    // space-width glyph still needs its advance cancelled. This measures that
    // advance in the same em tag units EmoteCellUnits uses (cached per session,
    // like the peg) so ComposeText can cancel it with a trailing <space=-space>
    // tag and keep consecutive emote cells edge-to-edge.
    private float GetEmoteSpaceAdvanceUnits(TextMeshProUGUI text)
    {
        if (_emoteSpaceAdvanceMeasured) return _emoteSpaceAdvanceUnits;
        _emoteSpaceAdvanceMeasured = true;
        try
        {
            var px = text != null ? text.GetPreferredValues("\u200B").x : 0f;
            if (px > 0f)
                _emoteSpaceAdvanceUnits = px / Mathf.Max(0.01f, _builtFontSize) * (_textOrthographic ? 1f : 10f);
        }
        catch (Exception)
        {
            _emoteSpaceAdvanceUnits = 0f;
        }
        return _emoteSpaceAdvanceUnits;
    }

    // Fits the emote image inside the fixed cell (cellW wide, EmoteSize tall)
    // preserving aspect: a wide emote spans the full cell width, a taller one is
    // capped by EmoteSize.
    private static (float Width, float Height) EmoteContained(float aspect, float cellW, float emoteSize)
    {
        if (emoteSize * aspect <= cellW)
            return (emoteSize * aspect, emoteSize);
        return (cellW, cellW / aspect);
    }

    // A decal badge: a world-space SpriteRenderer carrying the badge texture,
    // parented under its row so it scrolls and turns with the panel exactly
    // like a sticker. Renderer.enabled is re-derived per frame against the
    // viewport, because a SpriteRenderer (unlike canvas Graphics) is not
    // clipped by the ScrollRect's RectMask2D.
    private readonly List<BadgeDecal> _badges = new();

    // Chat emote decals: one sprite per emote word, parenting under its row and
    // positioned over the (transparent) word in the TMP layout. Clipped against
    // the viewport like badges; LRU-protected while on screen.
    private readonly List<ChatEmoteDecal> _chatEmotes = new();

    private struct BadgeDecal
    {
        public Transform Transform;
        public SpriteRenderer Renderer;
        public float ColumnX;
        public float Size;
    }

    // One badge decal, pegged to the top-left of the row beside the username.
    // The texture is filled in from BadgeCache immediately when cached or
    // asynchronously when the download is still in flight; failed/undefined
    // badges simply stay invisible.
    private void CreateBadge(Transform parent, string key, float x, float size)
    {
        var go = new GameObject("BadgeDecal");
        go.transform.SetParent(parent, false);

        // Rows live in the canvas's unit space, so a local position of (x, 0)
        // and a sprite scaled in those same units sits exactly where the old
        // canvas Image did. The row's pivot is at its top-center, so the
        // left-relative badge x is shifted by half the (measured) row width.
        // The small +Z nudge lifts the decal a hair off the panel plane toward
        // the viewer so it never depth-fights the card background, while
        // staying coplanar enough to read as part of the UI.
        var rowWidth = parent is RectTransform rowRt && rowRt.rect.width >= 10f
            ? rowRt.rect.width
            : ContentWidth();
        go.transform.localPosition = new Vector3(x - rowWidth * 0.5f, 0f, -BadgeZOffset);

        var sr = go.AddComponent<SpriteRenderer>();
        var mat = GetBadgeMaterial();
        if (mat != null) sr.material = mat;
        sr.sprite = null;
        sr.enabled = false;
        sr.sortingOrder = 10;

        var decal = new BadgeDecal { Transform = go.transform, Renderer = sr, ColumnX = x, Size = size };
        _badges.Add(decal);

        if (BadgeCache.TryGetTexture(key, out var tex))
            ApplyBadgeTexture(sr, tex, size);
        else
            BadgeCache.GetTextureAsync(key, (_, ready) => ApplyBadgeTexture(sr, ready, size));
    }

    // Applies a downloaded badge texture to the decal (sprite scaled in canvas
    // units, preserving aspect ratio) with the bloom-immune material. Unity's
    // destroyed-object test keeps stale callbacks (after a scene reload)
    // harmless. null textures (a badge that failed to download, or a version
    // our fixed catalog doesn't cover) leave the decal disabled and invisible.
    private static void ApplyBadgeTexture(SpriteRenderer sr, Texture2D? tex, float size)
    {
        if (sr == null) return;
        if (tex == null)
        {
            sr.sprite = null;
            sr.enabled = false;
            return;
        }
        sr.sprite = GetBadgeSprite(tex);
        var s = size * 100f / Mathf.Max(1f, tex.height);
        sr.transform.localScale = new Vector3(s, s, 1f);
        sr.color = Color.white;
        sr.enabled = true;
    }

    // Re-derives each decal's x position from its row's CURRENT width. Rows are
    // re-laid out on every message and whenever the panel is resized, so this
    // keeps badges glued to the text even when the chat width changes after the
    // message already existed (and self-corrects any stale first-frame width).
    private void UpdateBadgePositions()
    {
        for (var i = _badges.Count - 1; i >= 0; i--)
        {
            var decal = _badges[i];
            if (decal.Transform == null)
            {
                _badges.RemoveAt(i);
                continue;
            }
            var parent = decal.Transform.parent as RectTransform;
            var rowWidth = parent != null && parent.rect.width >= 10f
                ? parent.rect.width
                : ContentWidth();
            decal.Transform.localPosition = new Vector3(
                decal.ColumnX - rowWidth * 0.5f,
                0f,
                -BadgeZOffset);
        }
    }

    // Re-derives each decal's clip state against the viewport. Runs only when
    // the ScrollRect reports actual movement (onValueChanged) or after a
    // relayout - never in a per-frame loop, so an idle feed costs nothing. A
    // decal shows while its full sprite fits inside the visible band (no slack),
    // so rows scrolled out of the panel drop their badges at the same edge the
    // text gets masked.
    private void UpdateBadgeVisibility()
    {
        if (_badges.Count == 0) return;
        var viewport = _viewportRt;
        if (viewport == null) return;

        var halfH = viewport.rect.height * 0.5f;
        for (var i = _badges.Count - 1; i >= 0; i--)
        {
            var decal = _badges[i];
            if (decal.Transform == null || decal.Renderer == null)
            {
                _badges.RemoveAt(i);
                continue;
            }
            // Pivot is top-left, so the decal's position is its top edge and
            // the sprite spans Size units downward.
            var top = viewport.InverseTransformPoint(decal.Transform.position).y;
            var slack = DecalClipSlack;
            var visible = top <= halfH + slack && top - decal.Size >= -halfH - slack;
            if (decal.Renderer.enabled != visible)
                decal.Renderer.enabled = visible;
        }
    }

    // One sprite decal per emote word in the row. Sprite fills in async from
    // the shared emote cache (on demand, LRU-capped); placement happens on the
    // next RelayoutRows when the TMP layout for this row is final.
    private void CreateChatEmotes(RowHandle row)
    {
        var spans = row.Model.EmoteSpans;
        if (spans == null || spans.Count == 0) return;
        var starts = row.EmoteRichStarts;
        for (var i = 0; i < spans.Count; i++)
        {
            var richStart = i < starts.Count ? starts[i] : -1;
            if (richStart < 0) continue;
            var span = spans[i];
            if (span.Length <= 0 || span.Code.Length == 0) continue;
            CreateChatEmote(row, span, richStart, i);
        }
    }

    private void CreateChatEmote(RowHandle row, EmoteSpan span, int richStart, int spanIndex)
    {
        var go = new GameObject("ChatEmote");
        go.transform.SetParent(row.Rect, false);
        go.transform.localPosition = new Vector3(0f, 0f, -BadgeZOffset);

        var sr = go.AddComponent<SpriteRenderer>();
        var mat = GetBadgeMaterial(); // same bloom-immune sprite renderer as badges
        if (mat != null) sr.material = mat;
        sr.sprite = null;
        sr.enabled = false;
        sr.sortingOrder = 10;

        var decal = go.AddComponent<ChatEmoteDecal>();
        decal.Init(this, span.Code, richStart, span.Length, spanIndex);
        decal.Sr = sr;
        decal.Tx = go.transform;
        decal.RowRt = row.Rect;
        decal.Text = row.Text;
        _chatEmotes.Add(decal);

        if (EmoteCache.Instance.TryGetTexture(span.Code, out var tex))
            decal.ApplyTexture(tex);
        else
            EmoteCache.Instance.GetTextureAsync(span.Code, (_, ready) => decal.ApplyTexture(ready));
    }

    // Drops every chat emote decal (setting toggled off). Rows and their
    // children survive; only the emote visuals + their LRU registrations go.
    private void ClearChatEmotes()
    {
        foreach (var d in _chatEmotes)
        {
            if (d == null) continue;
            d.SetVisible(false);
            if (d.gameObject != null)
                Destroy(d.gameObject);
        }
        _chatEmotes.Clear();
    }

    // Re-derives every emote decal's size and position from the row's CURRENT
    // TMP layout. Runs at the end of RelayoutRows (row widths and heights are
    // final there), and is re-requested whenever a texture finishes downloading.
    private void PlaceChatEmotes()
    {
        // Per text: the first character INDEX of every line (an emote cell that
        // starts a line has its anchor there) and, when such a line is led by an
        // emote, how far its cells must shift left to land flush against the
        // line's true left edge. Some TMP builds restore the FIRST wrapped line
        // onto a pen still advanced by the row's leading <pos=nameIndent> tag, so
        // cells that lead that line are born a username-width to the right while
        // every later wrapped line correctly restarts at 0 (measured as the
        // leader cell's cellLeft) and corrected here by shifting the whole
        // emote-led line flush.
        TextMeshProUGUI? cacheText = null;
        int[] lineLeader = Array.Empty<int>();
        float[] lineShift = Array.Empty<float>();

        for (var i = _chatEmotes.Count - 1; i >= 0; i--)
        {
            var d = _chatEmotes[i];
            if (d == null || d.Tx == null || d.Text == null || d.RowRt == null || d.Sr == null)
            {
                _chatEmotes.RemoveAt(i);
                continue;
            }
            if (d.TexWidth <= 0 || d.TexHeight <= 0) continue; // texture not ready yet

            var chars = d.Text.textInfo.characterInfo;
            if (chars == null || chars.Length == 0) continue;

            if (!ReferenceEquals(d.Text, cacheText))
            {
                cacheText = d.Text;
                // 1) first char index per line
                var maxLine = 0;
                for (var k = 0; k < chars.Length; k++)
                {
                    if (chars[k].lineNumber > maxLine) maxLine = chars[k].lineNumber;
                }
                lineLeader = new int[maxLine + 1];
                for (var k = 0; k < lineLeader.Length; k++) lineLeader[k] = -1;
                for (var k = 0; k < chars.Length; k++)
                {
                    var ln = chars[k].lineNumber;
                    if (ln < 0 || ln >= lineLeader.Length) continue;
                    var ci = chars[k].index;
                    if (lineLeader[ln] < 0 || ci < lineLeader[ln]) lineLeader[ln] = ci;
                }
                // 2) shift per emote-led wrapped line = the leader cell's cellLeft.
                //    When TMP wraps the FIRST line it may restore the new pen at a
                //    position still carrying the row's <pos=nameIndent>, so the cells
                //    that lead that wrapped line are born a username-width right of
                //    their true flush origin; emote-led later wrapped lines are born
                //    flush (TMP resets those correctly), giving them shift 0. A line
                //    is emote-led iff its very FIRST character belongs to an emote
                //    cell: the leader cell is the first cell whose anchor sits at or
                //    after the line's first char, and the char itself is one of an
                //    emote cell's glyphs (the peg 'H'/'g' ~25 source indices before
                //    its anchor, or the anchor U+200B itself) or falls within the
                //    cell's source span.
                lineShift = new float[lineLeader.Length];
                for (var ln = 1; ln < lineLeader.Length; ln++)
                {
                    var lead = lineLeader[ln];
                    if (lead < 0) continue;
                    ChatEmoteDecal? leadCell = null;
                    for (var p = 0; p < _chatEmotes.Count; p++)
                    {
                        var dp = _chatEmotes[p];
                        if (dp == null || dp.Text == null || !ReferenceEquals(dp.Text, cacheText)) continue;
                        if (dp.TexWidth <= 0 || dp.TexHeight <= 0) continue;
                        if (dp.RichStart >= lead && (leadCell == null || dp.RichStart < leadCell.RichStart))
                            leadCell = dp;
                    }
                    if (leadCell == null) continue;
                    var emoteChar = false;
                    for (var k = 0; k < chars.Length; k++)
                    {
                        if (chars[k].index != lead) continue;
                        var ch = chars[k].character;
                        emoteChar = ch == 'H' || ch == 'g' || ch == '\u200B';
                        break;
                    }
                    if (!emoteChar && lead < leadCell.RichStart - 44) continue;
                    var sp = FindEmoteCell(chars, leadCell.RichStart);
                    if (sp < 0) continue;
                    var sc0 = chars[sp];
                    if (sc0.lineNumber != ln) continue;
                    var spaspect = (float)leadCell.TexWidth / Mathf.Max(1f, leadCell.TexHeight);
                    // the other cells on the line share its pen, so shift them all
                    // uniformly (lineShift slides the whole emote-led run flush).
                    lineShift[ln] = sc0.origin - EmoteCellWidth(spaspect, EmoteSize());
                }
            }

            // The emote's box = [anchor.origin - cellW, anchor.origin]: a <space> tag
            // advances the pen by exactly EmoteCellWidth (rounded to the tag's
            // printed precision), and the U+200B anchor after it is a REAL layout
            // character whose origin lands on the pen right AFTER that gap. The
            // anchor's own glyph metrics never enter the math - that was the bug
            // in the older cell: whitespace origins get shifted by glyph-centering
            // terms that vary per font asset, which made margins inconsistent,
            // images undersized, right-shifted (overlapping the following text)
            // and even spilling past the wrapped line.
            var idx = FindEmoteCell(chars, d.RichStart);
            if (idx < 0) continue;
            var c0 = chars[idx];

            var aspect = (float)d.TexWidth / Mathf.Max(1f, d.TexHeight);
            var cellW = EmoteCellWidth(aspect, EmoteSize());
            var cellLeft = c0.origin - cellW;
            var (imgW, imgH) = EmoteContained(aspect, cellW, EmoteSize());

            // Vertical anchor is the line's own box: the emote centers on the
            // midpoint between the line ascender and descender, not on any single
            // glyph's metrics, so it no longer floats high (or low) in the line.
            var lineNumber = c0.lineNumber;
            // On an emote-led wrapped line the cells are born offset (see the
            // comment above); shift that line's cells flush to their text origin.
            var shiftX = lineShift.Length > lineNumber && lineNumber >= 0 ? lineShift[lineNumber] : 0f;
            var lineInfo = d.Text.textInfo.lineInfo;
            float lineTop, lineBottom;
            if (lineInfo != null && lineNumber >= 0 && lineNumber < lineInfo.Length)
            {
                lineTop = lineInfo[lineNumber].ascender;
                lineBottom = lineInfo[lineNumber].descender;
            }
            else
            {
                lineTop = c0.topLeft.y;
                lineBottom = c0.topLeft.y - EmoteSize();
            }

            d.Tx.localScale = new Vector3(
                imgW * 100f / Mathf.Max(1f, d.TexWidth),
                imgH * 100f / Mathf.Max(1f, d.TexHeight),
                1f);

            // Center of the cell's text box, converted into row-local space
            // through the world (handles any TMP pivot/alignment convention).
            var centerLocal = new Vector3(cellLeft + cellW * 0.5f - shiftX, (lineTop + lineBottom) * 0.5f, 0f);
            var world = d.Text.transform.TransformPoint(centerLocal);
            var rowLocal = d.RowRt.InverseTransformPoint(world);
            d.Tx.localPosition = new Vector3(rowLocal.x, rowLocal.y, -BadgeZOffset);

            d.CellW = imgW;
            d.CellH = imgH;

            // Animated emotes: attach the frame-swap animator once the GIF
            // frames land in the cache (they download on demand after the
            // static texture, so the check simply retries on later visits).
            if (d.GetComponent<ChatEmoteAnimator>() == null
                && EmoteCache.Instance.TryGetAnimatedFrames(d.Code, out var frames, out var delay)
                && frames.Length > 1)
            {
                var animator = d.gameObject.AddComponent<ChatEmoteAnimator>();
                animator.Init(d.Sr!, frames, delay, d.Code);
            }
        }
    }

    // Locates the box's anchor character (the U+200B anchor trailing the emote's
    // <space> tag) by source index. RichStart IS that character's index - the
    // first character at or after it - so the window scan is just a tolerant way
    // to do an exact lookup across TMP builds that shift whitespace indices by a
    // tag or two. No xAdvance check: the anchor is a zero-width space.
    private static int FindEmoteCell(TMP_CharacterInfo[] chars, int richStart)
    {
        if (richStart < 0) return -1;
        var limit = richStart + 64;
        for (var j = 0; j < chars.Length; j++)
        {
            var c = chars[j];
            if (c.index < richStart) continue;
            if (c.index >= limit) break;
            return j;
        }
        return -1;
    }

    // Called when an emote texture lands (async). Keeps each span's last-known
    // aspect - the cell is composed from it - so when the real image confirms a
    // different aspect the row is re-composed and the cell re-sized to match;
    // otherwise the existing layout just re-places the decal.
    internal void UpdateEmoteAspect(ChatEmoteDecal d, float aspect)
    {
        if (d == null || d.SpanIndex < 0 || aspect <= 0f)
        {
            RequestRelayout(false);
            return;
        }
        RowHandle? row = null;
        var rowRt = d.RowRt;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Rect == rowRt) { row = _rows[i]; break; }
        }
        var aspects = row?.Model?.EmoteAspects;
        if (row == null || row.Rect == null || aspects == null || d.SpanIndex >= aspects.Count)
        {
            RequestRelayout(false);
            return;
        }
        var old = aspects[d.SpanIndex];
        if (old <= 0f) old = 1f;
        if (Math.Abs(old - aspect) > 0.01f)
        {
            aspects[d.SpanIndex] = aspect;
            RecomposeRow(row);
        }
        else
        {
            RequestRelayout(false);
        }
    }

    // Replays the aspect sync for a row's decals once the row is registered.
    // Used right after _rows.Add, because the synchronous cache-hit path inside
    // CreateChatEmote calls UpdateEmoteAspect while the row is still absent and
    // that probe bails early - leaving the model's aspect at its 1f default and
    // the composed cell square even for a wide emote.
    private void SyncRowEmoteAspects(RowHandle row)
    {
        if (row?.Rect == null) return;
        for (var i = 0; i < _chatEmotes.Count; i++)
        {
            var d = _chatEmotes[i];
            if (d == null || d.RowRt != row.Rect || d.TexWidth <= 0 || d.TexHeight <= 0) continue;
            UpdateEmoteAspect(d, (float)d.TexWidth / Mathf.Max(1f, d.TexHeight));
        }
    }

    // Re-runs a row's rich-text composition after an emote's aspect changed its
    // cell width, refreshes every emote decal's tag position (string indices of
    // later tags shift when an earlier cell changes size), and re-measures.
    private void RecomposeRow(RowHandle row)
    {
        var text = row.Text;
        if (text == null) return;
        row.EmoteRichStarts.Clear();
        text.text = ComposeText(text, row.Model, row.TextX, row.Model.EmoteSpans, row.EmoteRichStarts);
        for (var i = 0; i < _chatEmotes.Count; i++)
        {
            var d = _chatEmotes[i];
            if (d == null || d.RowRt != row.Rect) continue;
            d.RichStart = d.SpanIndex >= 0 && d.SpanIndex < row.EmoteRichStarts.Count
                ? row.EmoteRichStarts[d.SpanIndex]
                : -1;
        }
        row.NeedsMeasure = true;
        RequestRelayout(true);
    }

    // Clips emote decals against the viewport like badges. Runs on scroll/layout
    // events AND every LateUpdate (see the LateUpdate comment) because the decals
    // are SpriteRenderers, not canvas Graphics: the ScrollRect's RectMask2D only
    // vertical AND horizontal tests run here because the decals are
    // SpriteRenderers, not canvas Graphics: the ScrollRect's RectMask2D only
    // masks UI elements, so anything positioned past the text's wrapped width
    // (long unbreakable content) or wider than the window would otherwise render
    // outside the panel. Emotes also (de)register their LRU protection here:
    // while visible their code can't be evicted by EmoteCache's memory cap.
    private void UpdateEmoteClip()
    {
        if (_chatEmotes.Count == 0) return;
        var viewport = _viewportRt;
        if (viewport == null) return;

        var halfH = viewport.rect.height * 0.5f;
        var halfW = viewport.rect.width * 0.5f;
        for (var i = _chatEmotes.Count - 1; i >= 0; i--)
        {
            var d = _chatEmotes[i];
            if (d == null || d.Tx == null || d.Sr == null)
            {
                _chatEmotes.RemoveAt(i);
                continue;
            }
            if (d.CellH <= 0f)
            {
                d.SetVisible(false);
                continue;
            }
            var pos = viewport.InverseTransformPoint(d.Tx.position);
            var slack = DecalClipSlack;
            var visible = pos.y + d.CellH * 0.5f <= halfH + slack && pos.y - d.CellH * 0.5f >= -halfH - slack
                && pos.x + d.CellW * 0.5f <= halfW + slack && pos.x - d.CellW * 0.5f >= -halfW - slack;
            d.SetVisible(visible);
        }
    }

    // Sprites are cached per source texture (badges are re-created whenever a
    // new message arrives, so this avoids a Sprite allocation per message).
    private static readonly Dictionary<Texture2D, Sprite> BadgeSprites = new();

    private static Sprite GetBadgeSprite(Texture2D tex)
    {
        if (BadgeSprites.TryGetValue(tex, out var sprite) && sprite != null) return sprite;
        // Top-left pivot (0,1): the decal is anchored at its top-left corner,
        // matching how the old canvas badge was laid out. A centered pivot made
        // the sprite straddle the anchor, poking half its width out the panel's
        // left edge and half its height above the username line.
        var created = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0f, 1f));
        BadgeSprites[tex] = created;
        return created;
    }

    // Clone of EmoteVisualFactory's bloom-immune ImageFactory sprite material,
    // shared by every badge. Glow is forced off so the badge only shows its own
    // texture (the Uber shader ignores plain material.color/UI vertex tints).
    private static Material? _badgeMaterial;

    private static Material? GetBadgeMaterial()
    {
        if (_badgeMaterial != null) return _badgeMaterial;
        var src = EmoteVisualFactory.GetSpriteMaterial();
        if (src == null)
        {
            Plugin.Log.Warn("Badge material unavailable; falling back to default UI sprite material.");
            return null;
        }
        _badgeMaterial = new Material(src);
        _badgeMaterial.SetFloat("_Glow", 0f);
        _badgeMaterial.mainTexture = null;
        return _badgeMaterial;
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

        // Each row sizes itself against the content rect, so nothing width-specific
        // needs updating here; the pending relayout re-measures everything.

        // The scrollbar track is the one sibling that grows with the card, so
        // it needs the same bottom-anchored relayout as the width/height change.
        LayoutScrollbar();

        ApplyHandleLayout();
        Canvas.ForceUpdateCanvases();
        RequestRelayout(true);
    }

    // Lower BSML's default floating-screen sort order (4) so other UI canvases
    // draw on top of the panel when they overlap, instead of the panel always
    // rendering over them regardless of where it is physically placed.
    private void ApplySortingOrder(int order)
    {
        if (_screen == null) return;
        var chatCanvas = _screen.GetComponent<Canvas>();
        if (chatCanvas != null)
            chatCanvas.sortingOrder = Mathf.Clamp(order, -20, 20);
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

    // Neutralize exactly the text TMP would otherwise swallow, and nothing else.
    // With rich text on, TMP's tag scanner parses ANY '<'...'>' span of up to
    // 128 chars that contains no inner '<' - executing real tags (<size=40>)
    // and silently deleting unknown ones. A '<' is safe the moment no '>' closes
    // it inside that window, so it prints verbatim (a lone "<3" renders as-is);
    // '&', '"' and '\'' are plain text to TMP (it never parses entities), and a
    // bare '>' triggers nothing on its own. So only the opening '<' of a span
    // that WOULD parse as a tag has to go: the span's content and '>' then print
    // plainly and no tag can ever form. Control/escape characters are stripped
    // the same way as before.
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\r' || c == '\n' || c == '\t')
            {
                sb.Append(' ');
                i++;
                continue;
            }
            if (c < 0x20 || c == 0x7F)
            {
                i++;
                continue;
            }
            if (c == '<')
            {
                var closesAt = -1;
                var limit = Math.Min(s.Length, i + 1 + 128);
                for (var j = i + 1; j < limit; j++)
                {
                    var d = s[j];
                    if (d == '<' || d == '\r' || d == '\n') break;
                    if (d == '>')
                    {
                        closesAt = j;
                        break;
                    }
                }
                if (closesAt >= 0)
                {
                    i++;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
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
        _contentRt = null;
        _scrollRect = null;
        _lockIcon = null;
        _pendingBuild = false;
        // The panel's GameObject tree is destroyed with the screen; live row
        // references drop away while the _lines models survive for a rebuild.
        // Drop the decal lists too - their GameObjects just died, and leaving
        // the destroyed entries in place would make them linger (re-buffed
        // duplicates on the next rebuild) until a layout pass prunes them.
        _rows.Clear();
        _badges.Clear();
        _chatEmotes.Clear();
    }

    private void DestroyIconTextures()
    {
        if (_lockTex != null) { Destroy(_lockTex); _lockTex = null; }
        if (_unlockTex != null) { Destroy(_unlockTex); _unlockTex = null; }
    }

    /// <summary>
    /// The sanitized, render-ready data behind one chat line. _lines stores
    /// these across scene loads so the panel can re-render its backlog after a
    /// rebuild; the badges field carries the raw IRC badge list.
    /// </summary>
    private sealed class LineModel
    {
        internal bool IsSystem;
        internal string User = "";
        internal string Id = "";
        internal string DisplayUser = "";
        internal string Message = "";
        internal string NameHex = "";
        internal string TextHex = "";
        internal string Badges = "";
        internal bool IsShared;
        internal string SystemMain = "";
        internal string SystemDetail = "";
        internal string SystemHex = "";
        // Emote words rendered as images inside the line. Start/Length index the
        // SANITIZED model.Message (resolved by ResolveMessageSpans); EmoteAspects
        // keeps the last-known width/height ratio per span so the fixed-width
        // emote cell agrees with the downloaded image (1.0 until it lands).
        // Aligned 1:1 with EmoteSpans.
        internal List<EmoteSpan>? EmoteSpans;
        internal List<float>? EmoteAspects;
    }

    /// <summary>
    /// One (start, length, code) range of a chat message that renders as an
    /// emote image. Produced by the IRC reader against the raw message, then
    /// re-find in the sanitized text by AddMessage.
    /// </summary>
    internal readonly struct EmoteSpan
    {
        internal readonly int Start;
        internal readonly int Length;
        internal readonly string Code;

        internal EmoteSpan(int start, int length, string code)
        {
            Start = start;
            Length = length;
            Code = code;
        }
    }

    /// <summary>
    /// The live UI for one <see cref="LineModel"/>: badge images plus the TMP
    /// text, its measured height and the horizontal offset where text starts
    /// (after the badges).
    /// </summary>
    private sealed class RowHandle
    {
        internal RectTransform Rect = null!;
        internal TextMeshProUGUI Text = null!;
        internal RectTransform TextRect = null!;
        internal LineModel Model = null!;
        internal float Height;
        internal float BadgeArea;
        internal float TextX;
        internal float TextWidth;
        internal bool NeedsMeasure = true;
        // One rich-text string index per model.EmoteSpans entry (the char where
        // the emote word begins in the composed text), or -1 when a span did not
        // survive composition. Aligned 1:1 so decals can find their chars.
        internal List<int> EmoteRichStarts = new();
    }
}

/// <summary>
/// One inline chat emote: a sprite decal parented under its row. Holds the
/// metadata needed to place it over the (transparent) word in the TMP layout,
/// applies the downloaded texture, toggles visibility against the viewport, and
/// keeps its code in EmoteHudEntry.ActiveCodes while visible so EmoteCache's
/// LRU cap never evicts an emote the viewer can actually see.
/// </summary>
internal sealed class ChatEmoteDecal : MonoBehaviour
{
    internal SpriteRenderer Sr = null!;
    internal Transform Tx = null!;
    internal RectTransform RowRt = null!;
    internal TextMeshProUGUI Text = null!;
    internal string Code = "";
    internal int RichStart;
    internal int Length;
    internal int SpanIndex = -1;
    internal int TexWidth;
    internal int TexHeight;
    internal float CellW;
    internal float CellH;
    internal bool Visible;

    private ChatPanelController? _panel;

    internal void Init(ChatPanelController panel, string code, int richStart, int length, int spanIndex = -1)
    {
        _panel = panel;
        Code = code;
        RichStart = richStart;
        Length = length;
        SpanIndex = spanIndex;
    }

    // Called on the main thread from EmoteCache when the texture lands (or
    // fails). Placement (size + position over the word) is re-derived on the
    // next relayout, which ApplyTexture requests.
    internal void ApplyTexture(Texture2D? tex)
    {
        if (Sr == null) return;
        if (tex == null)
        {
            Sr.sprite = null;
            Sr.enabled = false;
            TexWidth = 0;
            TexHeight = 0;
            return;
        }
        TexWidth = tex.width;
        TexHeight = tex.height;
        Sr.sprite = EmoteVisualFactory.GetSprite(tex);
        Sr.color = Color.white;
        // The cell's width depends on the emote's aspect ratio. When the landed
        // texture confirms a different aspect than the cell was composed for,
        // UpdateEmoteAspect re-composes the row (and re-places everything);
        // otherwise the existing layout just re-places this decal.
        if (_panel != null && RowRt != null && Text != null)
        {
            var aspect = (float)tex.width / Mathf.Max(1f, tex.height);
            _panel.UpdateEmoteAspect(this, aspect);
        }
        else
        {
            _panel?.RequestRelayout(false);
        }
    }

    internal void SetVisible(bool visible)
    {
        if (Visible == visible) return;
        Visible = visible;
        if (Sr != null) Sr.enabled = visible;
        if (visible)
        {
            EmoteHudEntry.ActiveCodes.Add(Code);
            // The texture may have been evicted while the decal was scrolled
            // out of view: re-fetch on demand the moment it comes back.
            if (Sr == null || Sr.sprite == null || Sr.sprite.texture == null)
                EmoteCache.Instance.GetTextureAsync(Code, (_, tex) => ApplyTexture(tex));
        }
        else
        {
            EmoteHudEntry.ActiveCodes.Remove(Code);
        }
    }

    private void OnDestroy()
    {
        EmoteHudEntry.ActiveCodes.Remove(Code);
    }
}

/// <summary>
/// Swaps an inline chat emote's sprite through EmoteCache's animated frames
/// (mirrors EmoteAnimator, which is bound to EmoteHudEntry instead). Added only
/// to animated chat emotes once their GIF frames land; runs only while such a
/// decal exists.
/// </summary>
internal sealed class ChatEmoteAnimator : MonoBehaviour
{
    private Texture2D[] _frames = Array.Empty<Texture2D>();
    private float _delay = 0.1f;
    private float _phaseStart;
    private SpriteRenderer? _sr;

    // All instances of one animated emote share a phase anchor (see
    // EmoteCache.GetAnimatedPhaseStart), so a repeated emote plays in perfect
    // lockstep instead of each decal rolling its own timer from frame 0 - which
    // made a message that used the same emote later look like a fresh, out-of-sync
    // instance. It also means an animator attached AFTER the GIF frames landed
    // snaps straight to the frame the emote should be on, rather than restarting.
    internal void Init(SpriteRenderer sr, Texture2D[] frames, float delay, string code)
    {
        _sr = sr;
        _frames = frames ?? Array.Empty<Texture2D>();
        _delay = Mathf.Max(0.03f, delay);
        _phaseStart = EmoteCache.GetAnimatedPhaseStart(code);
        var frame = CurrentFrame();
        if (frame != null)
            _sr.sprite = EmoteVisualFactory.GetSprite(frame);
    }

    private void Update()
    {
        if (_frames.Length <= 1 || _sr == null) return;
        var frame = CurrentFrame();
        if (frame == null) return;
        if (_sr.sprite == null || _sr.sprite.texture != frame)
            _sr.sprite = EmoteVisualFactory.GetSprite(frame);
    }

    private Texture2D? CurrentFrame()
    {
        if (_frames.Length == 0) return null;
        if (_frames.Length == 1) return _frames[0];
        var index = Mathf.FloorToInt((Time.time - _phaseStart) / _delay);
        if (index < 0) index = 0;
        return _frames[index % _frames.Length];
    }
}

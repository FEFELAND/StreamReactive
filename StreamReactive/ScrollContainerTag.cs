using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Tags;
using HMUI;
using UnityEngine;
using UnityEngine.UI;

namespace StreamReactive;

/// <summary>
/// Custom BSML tags providing reliable scroll containers.
/// Beat Saber Markup Language's built-in scroll-view clones an HMUI template
/// whose sizing fights layout groups; this instead hand-builds a plain Unity
/// ScrollRect (viewport + mask + top-anchored auto-sizing content), following
/// the approach proven by the AccSaber Reloaded plugin's
/// My2DScrollableContainer (GPL-3.0, credited in REFERENCE_NOTES.md).
/// Children declared inside the tag are parented into the scrolling content.
/// Two heights exist because BSML tags cannot receive attributes: regular
/// (sr-scroll, fits under a tab-selector strip) and tall (sr-scroll-tall,
/// for full-height pages without tabs).
/// </summary>
internal abstract class ScrollContainerTagBase : BSMLTag
{
    private readonly float _height;
    private readonly string[] _aliases;

    protected ScrollContainerTagBase(float height, string[] aliases)
    {
        _height = height;
        _aliases = aliases;
    }

    public override string[] Aliases => _aliases;

    public override GameObject CreateObject(Transform parent)
    {
        var root = new GameObject("StreamReactiveScrollContainer", typeof(RectTransform), typeof(ScrollRect));
        root.transform.SetParent(parent, false);

        var rootRt = (RectTransform)root.transform;
        rootRt.anchorMin = new Vector2(0.5f, 0.5f);
        rootRt.anchorMax = new Vector2(0.5f, 0.5f);
        rootRt.pivot = new Vector2(0.5f, 0.5f);
        rootRt.anchoredPosition = Vector2.zero;
        rootRt.sizeDelta = new Vector2(0f, _height);

        // Cover both layout possibilities: if the parent controls heights it
        // uses this preferred height; if not, the sizeDelta above holds.
        var rootLayout = root.AddComponent<LayoutElement>();
        rootLayout.preferredHeight = _height;
        rootLayout.minHeight = _height;

        var viewport = new GameObject("Viewport", typeof(RectTransform));
        viewport.transform.SetParent(root.transform, false);

        var viewportRt = (RectTransform)viewport.transform;
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.pivot = new Vector2(0.5f, 0.5f);
        viewportRt.offsetMin = Vector2.zero;
        viewportRt.offsetMax = Vector2.zero;

        // Invisible but raycastable image so pointer/wheel events reach the
        // ScrollRect (alpha 0 still receives raycasts).
        var viewportImage = viewport.AddComponent<ImageView>();
        viewportImage.sprite = Utilities.ImageResources.WhitePixel;
        viewportImage.material = Utilities.ImageResources.NoGlowMat;
        viewportImage.raycastTarget = true;
        viewportImage.color = new Color(1f, 1f, 1f, 0f);
        viewportImage.type = Image.Type.Simple;

        // RectMask2D clips without stencil tricks and cannot silently hide
        // everything the way a Mask + material mismatch can.
        viewport.AddComponent<RectMask2D>();

        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewport.transform, false);

        var contentRt = (RectTransform)contentGo.transform;
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.anchoredPosition = Vector2.zero;
        contentRt.sizeDelta = new Vector2(0f, 0f);

        var contentLayout = contentGo.AddComponent<VerticalLayoutGroup>();
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        contentLayout.spacing = 1f;
        contentLayout.padding = new RectOffset(1, 3, 1, 2);

        var fitter = contentGo.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scrollRect = root.GetComponent<ScrollRect>();
        scrollRect.content = contentRt;
        scrollRect.viewport = viewportRt;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.135f;
        scrollRect.scrollSensitivity = 6f;
        scrollRect.horizontalScrollbar = null;

        // Draggable scrollbar on the right edge; doubles as the "you can
        // scroll" affordance. AutoHideAndExpandViewport hides it (and grows
        // the viewport back) when the content fits without scrolling.
        var scrollbarGo = new GameObject("Scrollbar", typeof(RectTransform));
        scrollbarGo.transform.SetParent(root.transform, false);

        var sbRt = (RectTransform)scrollbarGo.transform;
        sbRt.anchorMin = new Vector2(1f, 0f);
        sbRt.anchorMax = new Vector2(1f, 1f);
        sbRt.pivot = new Vector2(1f, 0.5f);
        sbRt.sizeDelta = new Vector2(3f, 0f);
        sbRt.anchoredPosition = Vector2.zero;

        var trackImage = scrollbarGo.AddComponent<ImageView>();
        trackImage.sprite = Utilities.ImageResources.WhitePixel;
        trackImage.material = Utilities.ImageResources.NoGlowMat;
        trackImage.type = Image.Type.Simple;
        trackImage.raycastTarget = false;
        trackImage.color = new Color(0f, 0f, 0f, 0.35f);

        var handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(scrollbarGo.transform, false);

        var handleRt = (RectTransform)handle.transform;
        handleRt.anchorMin = Vector2.zero;
        handleRt.anchorMax = Vector2.one;
        handleRt.offsetMin = new Vector2(0.5f, 0f);
        handleRt.offsetMax = new Vector2(-0.5f, 0f);

        var handleImage = handle.AddComponent<ImageView>();
        handleImage.sprite = Utilities.ImageResources.WhitePixel;
        handleImage.material = Utilities.ImageResources.NoGlowMat;
        handleImage.type = Image.Type.Simple;
        handleImage.raycastTarget = true;
        handleImage.color = new Color(1f, 1f, 1f, 0.55f);

        var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
        scrollbar.handleRect = handleRt;
        scrollbar.targetGraphic = handleImage;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.value = 1f;

        scrollRect.verticalScrollbar = scrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
        scrollRect.verticalScrollbarSpacing = 0.5f;

        return contentGo;
    }
}

internal sealed class ScrollContainerTag : ScrollContainerTagBase
{
    public ScrollContainerTag() : base(58f, new[] { "sr-scroll" }) { }
}

/// <summary>
/// Same scroll container, taller: for pages that have no tab-selector strip
/// above them (General, Bomb, Subs, Raid, About), so the dead space under a
/// 58-unit container gets used.
/// </summary>
internal sealed class ScrollContainerTallTag : ScrollContainerTagBase
{
    public ScrollContainerTallTag() : base(68f, new[] { "sr-scroll-tall" }) { }
}
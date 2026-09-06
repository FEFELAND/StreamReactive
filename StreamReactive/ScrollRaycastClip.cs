using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StreamReactive;

/// <summary>
/// Prevents scrolled-out rows inside a <c>ScrollRect</c> from swallowing clicks
/// aimed at content above/around them (e.g. a <c>tab-selector</c> strip that
/// scrolled rows move up underneath). <see cref="RectMask2D"/> clips rendering
/// only, so masked-out rows still receive raycasts; this component flips each
/// row's <c>raycastTarget</c> off as soon as it scrolls out of the viewport and
/// back on when it returns.
///
/// Not a per-frame toggle: it reacts to scroll-position changes and to
/// enablement/layout events only, so there is no per-frame churn. It is attached
/// only to the bits/throw tab content (the right-hand main column), never the
/// left sidebar whose region the BSML modal keyboard overlaps - both of the
/// failure modes that sank the earlier per-frame ScrollRaycastGate.
///
/// The pages mount as ABLE inactive (the default tab is General), so world rects
/// are meaningless until the container is shown. Everything therefore re-captures
/// and re-refreshes on <see cref="OnEnable"/> and on layout, in addition to on
/// scroll.
/// </summary>
internal sealed class ScrollRaycastClip : MonoBehaviour
{
    private ScrollRect? _scrollRect;
    private readonly List<Graphic> _rows = new();

    internal void Init(ScrollRect scrollRect)
    {
        if (_scrollRect == scrollRect)
            return;

        if (_scrollRect != null)
            _scrollRect.onValueChanged.RemoveAllListeners();

        _scrollRect = scrollRect;
        if (_scrollRect == null)
            return;

        _scrollRect.onValueChanged.AddListener(_ => Refresh());
        Refresh();
    }

    private void OnEnable()
    {
        Refresh();
        StartCoroutine(SettleRaycasts());
    }

    private void OnDidApplyAnimationProperties()
    {
        Refresh();
    }

    // Row positions and the viewport size settle a frame or two after the parse
    // and after the page is shown. Refresh a handful of times over a short window
    // so first-paint raycastTargets are correct, then stop.
    private System.Collections.IEnumerator SettleRaycasts()
    {
        for (var i = 0; i < 4; i++)
        {
            yield return new WaitForEndOfFrame();
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_scrollRect == null || !isActiveAndEnabled)
            return;

        var viewport = _scrollRect.viewport;
        if (viewport == null)
            return;

        CaptureRows();
        var clip = WorldRect(viewport);

        // Tolerance: a row resting exactly on an edge shouldn't flicker on/off.
        var tol = clip.size * 0.002f;
        clip.xMin -= tol.x;
        clip.xMax += tol.x;
        clip.yMin -= tol.y;
        clip.yMax += tol.y;

        foreach (var g in _rows)
        {
            if (g == null)
                continue;
            var rc = g.rectTransform;
            if (rc == null)
                continue;

            var row = WorldRect(rc);
            // A row is only clickable while it sits FULLY inside the viewport.
            // If any part sticks above the top or below the bottom, it is a
            // scrolled-out row whose overhang would swallow clicks aimed at
            // whatever it now covers (the tab-selector strip above, or the
            // scrollbar/below).
            g.raycastTarget = row.xMin >= clip.xMin && row.xMax <= clip.xMax
                              && row.yMin >= clip.yMin && row.yMax <= clip.yMax;
        }
    }

    private void CaptureRows()
    {
        _rows.Clear();
        if (_scrollRect?.content == null)
            return;

        foreach (RectTransform child in _scrollRect.content)
        {
            var graphics = child.GetComponentsInChildren<Graphic>(true);
            if (graphics.Length > 0)
                _rows.AddRange(graphics);
        }
    }

    private static Rect WorldRect(RectTransform rt)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        var min = corners[0];
        var max = corners[2];
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private void OnDestroy()
    {
        if (_scrollRect != null)
            _scrollRect.onValueChanged.RemoveAllListeners();
    }
}
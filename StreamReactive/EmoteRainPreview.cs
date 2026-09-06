using System.Collections.Generic;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// Draws a small marker at every enabled rain spawn zone so you can see where
/// emotes will fall. Anchored to the player (not the camera), so markers sit at
/// fixed world positions instead of floating in front of your view.
/// </summary>
internal sealed class EmoteRainPreview : MonoBehaviour
{
    private static EmoteRainPreview? Instance;
    private readonly List<GameObject> _markers = new();

    private void LateUpdate()
    {
        var areas = Plugin.GetEnabledRainZones();
        if (areas.Count == 0)
            areas.Add("AroundPlayer");

        if (_markers.Count != areas.Count)
            RefreshMarkers(areas.Count);

        for (var i = 0; i < _markers.Count && i < areas.Count; i++)
            _markers[i].transform.position = Plugin.RainSpawnAnchor(areas[i]);
    }

    private void RefreshMarkers(int count)
    {
        foreach (var m in _markers)
            if (m != null) Object.Destroy(m);
        _markers.Clear();

        for (var i = 0; i < count; i++)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"StreamReactiveRainPreviewMarker{i}";
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            var renderer = marker.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Sprites/Default");
                var material = new Material(shader != null ? shader : Shader.Find("Hidden/Internal-Colored"));
                material.color = new Color(0f, 1f, 1f, 0.6f);
                renderer.sharedMaterial = material;
            }

            Object.Destroy(marker.GetComponent<Collider>());
            _markers.Add(marker);
        }
    }

    public static void SetVisible(bool visible)
    {
        if (visible)
        {
            if (Instance != null)
                return;
            var go = new GameObject("StreamReactiveEmoteRainPreview");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<EmoteRainPreview>();
        }
        else if (Instance != null)
        {
            Object.Destroy(Instance.gameObject);
            Instance = null;
        }
    }
}

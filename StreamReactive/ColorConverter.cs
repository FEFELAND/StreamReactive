using System;
using System.Globalization;
using IPA.Config.Data;
using IPA.Config.Stores;
using UnityEngine;

namespace StreamReactive;

public sealed class ColorConverter : ValueConverter<Color>
{
    public override Value? ToValue(Color obj, object parent)
    {
        var r = Mathf.RoundToInt(Mathf.Clamp01(obj.r) * 255f);
        var g = Mathf.RoundToInt(Mathf.Clamp01(obj.g) * 255f);
        var b = Mathf.RoundToInt(Mathf.Clamp01(obj.b) * 255f);
        var a = Mathf.RoundToInt(Mathf.Clamp01(obj.a) * 255f);

        var hex = $"#{r:X2}{g:X2}{b:X2}";
        if (a < 255)
            hex += $"{a:X2}";
        return new Text(hex);
    }

    public override Color FromValue(Value? value, object parent)
    {
        switch (value)
        {
            case Text text when !string.IsNullOrEmpty(text.Value):
                return EventDispatcher.TryParseHex(text.Value, out var c) ? c : Color.white;
            case Map map:
                return FromLegacyMap(map);
            default:
                return Color.white;
        }
    }

    private static Color FromLegacyMap(Map map)
    {
        static float Get(Map m, string key, float fallback)
        {
            if (!m.TryGetValue(key, out var value))
                return fallback;

            return value switch
            {
                FloatingPoint fp => (float)fp.Value,
                Integer i => (float)i.Value,
                Text t => float.TryParse(t.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback,
                _ => fallback
            };
        }

        return new Color(Get(map, "r", 0f), Get(map, "g", 0f), Get(map, "b", 0f), Get(map, "a", 1f));
    }
}

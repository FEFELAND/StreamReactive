using System;
using System.Globalization;
using IPA.Config.Data;
using IPA.Config.Stores;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// Serializes a <see cref="Vector3"/> as comma-separated text ("x,y,z").
/// Used to persist the floating panel's world position so it stays where the
/// user left it across menu loads.
/// </summary>
public sealed class Vector3Converter : ValueConverter<Vector3>
{
    public override Value? ToValue(Vector3 obj, object parent)
    {
        return new Text(string.Format(CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", obj.x, obj.y, obj.z));
    }

    public override Vector3 FromValue(Value? value, object parent)
    {
        if (value is Text text && !string.IsNullOrEmpty(text.Value))
        {
            var parts = text.Value.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return new Vector3(x, y, z);
        }

        return Vector3.zero;
    }
}

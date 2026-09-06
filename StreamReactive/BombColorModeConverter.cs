using System;
using IPA.Config.Data;
using IPA.Config.Stores;

namespace StreamReactive;

/// <summary>
/// Serializes BombColorMode as a plain number ("BombColorMode": 1) instead of
/// the nested map BSIPA's default emits for enums ({"value__": 1}). Also reads
/// legacy values: an old {"value__": n} map, a bare integer, or an enum name.
/// </summary>
public sealed class BombColorModeConverter : ValueConverter<BombColorMode>
{
    public override Value? ToValue(BombColorMode obj, object parent) => new Integer((long)obj);

    public override BombColorMode FromValue(Value? value, object parent)
    {
        switch (value)
        {
            case Integer i:
                return ToEnum(i.Value);
            case Text t when int.TryParse(t.Value, out var n):
                return ToEnum(n);
            case Text t when Enum.TryParse<BombColorMode>(t.Value, true, out var mode):
                return mode;
            case Map map when TryReadLegacy(map, out var legacy):
                return legacy;
            default:
                return BombColorMode.Static;
        }
    }

    private static BombColorMode ToEnum(long raw) => Enum.IsDefined(typeof(BombColorMode), (int)raw)
        ? (BombColorMode)raw
        : BombColorMode.Static;

    private static bool TryReadLegacy(Map map, out BombColorMode result)
    {
        result = BombColorMode.Static;
        if (map.TryGetValue("value__", out var value) && value is Integer i && Enum.IsDefined(typeof(BombColorMode), (int)i.Value))
        {
            result = (BombColorMode)i.Value;
            return true;
        }

        return false;
    }
}
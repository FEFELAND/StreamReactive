using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StreamReactive;

internal static class EventDispatcher
{
    internal static void ProcessRaw(string json)
    {
        if (string.IsNullOrEmpty(json))
            return;

        var root = JObject.Parse(json);

        var type = root["type"]?.Value<string>() ?? root["event"]?.Value<string>() ?? "unknown";

        if (string.Equals(type, "stop_all", StringComparison.OrdinalIgnoreCase))
        {
            ClearAllEffects();
            Plugin.Log.Info("Stop all effects requested.");
            return;
        }

        var cfg = PluginConfig.Instance;

        // "socket" control: turns the whole plugin on/off — identical to the
        // Enabled switch on the General settings page. Accepts an explicit
        // boolean (data.on / data.enabled / data.value / root on); if the field
        // is missing it toggles from the current state.
        if (string.Equals(type, "socket", StringComparison.OrdinalIgnoreCase))
        {
            if (cfg == null) return;
            var socketData = root["data"] as JObject ?? root;
            var socketOn = socketData["on"]?.Value<bool?>()
                           ?? socketData["enabled"]?.Value<bool?>()
                           ?? socketData["value"]?.Value<bool?>()
                           ?? root["on"]?.Value<bool?>();
            cfg.Enabled = socketOn ?? !cfg.Enabled;
            Plugin.Log.Info($"Socket control: plugin {(cfg.Enabled ? "ON" : "OFF")}.");
            if (!cfg.Enabled)
            {
                ClearAllEffects();
                Plugin.Log.Info("Disable: cleared queues and stopped all effects (socket stays open).");
            }
            return;
        }

        // "pause" / "unpause" (alias "resume"): mod stays enabled and keeps
        // accepting events into the queue, but nothing plays until unpaused.
        if (string.Equals(type, "pause", StringComparison.OrdinalIgnoreCase))
        {
            if (cfg != null) cfg.Paused = true;
            Plugin.Log.Info("Paused: events are queued but nothing plays until unpause.");
            return;
        }
        if (string.Equals(type, "unpause", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "resume", StringComparison.OrdinalIgnoreCase))
        {
            if (cfg != null) cfg.Paused = false;
            Plugin.Log.Info("Unpaused: queued events resume.");
            // Any flashbangs saved while paused replay now (unpause arrives from the
            // socket/chat thread, so marshal the drain onto the main thread).
            if (cfg != null && !Plugin.IsMapProtectionActive())
                RuntimeHooks.RunOnMainThread(() => Plugin.Instance?.TryDrainHeldFlashbangs());
            return;
        }

        // Global kill switch: while disabled only the pure control messages above
        // (stop_all/socket/pause/unpause) still act. Every actual effect — the
        // transient one-shots below AND the queued note events — is ignored, so
        // disabling via the settings toggle or "socket on:false" makes the mod
        // truly inert. The WebSocket stays open so it can always be re-enabled.
        if (cfg?.Enabled == false)
        {
            Plugin.Log.Debug($"Disabled: ignoring {type}.");
            return;
        }

        // While paused — or on a protected map (Noodle/Vivify/WIP) treated as
        // paused — only transient one-shot effects are skipped: they have no queue,
        // so there is nowhere to hold them. Note event types (bomb/bits/sub/raid)
        // pass through to DispatchStreamEvent so they are ACCEPTED into
        // NoteCosmeticController's queue, where the ticker's pause gate holds them
        // and starts them later.
        if (IsFlashbangType(type))
        {
            // A flashbang can't blind the player mid-map while the mod is manually
            // paused or a protected (Noodle/Vivify/WIP) map is active. In both cases
            // it's held and replayed at the start of the next playable map rather
            // than dropped, so the viewer's bits/gift isn't wasted.
            if (cfg?.Paused == true || Plugin.IsMapProtectionActive())
            {
                Plugin.HoldFlashbang();
                return;
            }
            FlashbangController.Flash();
            return;
        }

        if (IsProjectionType(type))
        {
            if (cfg?.Paused == true || Plugin.IsMapProtectionActive())
            {
                Plugin.Log.Debug($"Paused: ignoring {type}.");
                return;
            }
            var projData = root["data"] as JObject ?? root;
            var projName = projData["name"]?.Value<string>() ?? "star";
            var projColorStr = projData["color"]?.Value<string>() ?? string.Empty;
            var projDuration = projData["duration"]?.Value<float?>();
            var projSize = projData["size"]?.Value<float?>();
            var projDistance = projData["distance"]?.Value<float?>();
            var projPositionX = GetNullableFloat(projData, "positionX", "posX", "x");
            var projPositionY = GetNullableFloat(projData, "positionY", "posY", "y");
            var projPositionZ = GetNullableFloat(projData, "positionZ", "posZ", "z");
            var projRotationX = GetNullableFloat(projData, "rotationX", "rotX", "rx");
            var projRotationY = GetNullableFloat(projData, "rotationY", "rotY", "ry");
            var projRotationZ = GetNullableFloat(projData, "rotationZ", "rotZ", "rz");

            Color? projColor = null;
            if (!string.IsNullOrEmpty(projColorStr) && TryParseHex(projColorStr, out var parsedProjColor))
                projColor = parsedProjColor;

            ProjectionController.Show(
                projName, projColor, projDuration, projSize, projDistance,
                projPositionX, projPositionY, projPositionZ,
                projRotationX, projRotationY, projRotationZ);
            return;
        }

        if (IsThrowType(type))
        {
            if (cfg?.Paused == true || Plugin.IsMapProtectionActive())
            {
                Plugin.Log.Debug($"Paused: ignoring {type}.");
                return;
            }
            var throwData = root["data"] as JObject ?? root;
            var throwCount = throwData["amount"]?.Value<int>() ??
                             throwData["count"]?.Value<int>() ?? 1;

            // Optional per-message overrides: a scale multiplier, a spawn
            // point mode, and an arc/flight-time (seconds). These let a
            // trigger pick its own defaults while the websocket overwrites
            // them for a single throw (e.g. launch from the horizon, bigger
            // notes, a flatter/lofiter arc).
            float? throwScale = null;
            var throwScaleToken = throwData["scale"];
            if (throwScaleToken != null && throwScaleToken.Type != JTokenType.Null)
                throwScale = GetNullableFloat(throwData, "scale", "size");

            string? throwOrigin = null;
            var throwOriginToken = throwData["spawn"] ?? throwData["origin"];
            if (throwOriginToken != null && throwOriginToken.Type != JTokenType.Null)
                throwOrigin = throwOriginToken.Value<string>();

            float? throwAirTime = null;
            var throwAirTimeToken = throwData["arc"] ?? throwData["time"] ?? throwData["airTime"];
            if (throwAirTimeToken != null && throwAirTimeToken.Type != JTokenType.Null)
                throwAirTime = GetNullableFloat(throwData, "arc", "time", "airTime");

            ProjectileThrower.Throw(throwCount, throwScale, throwOrigin, throwAirTime);
            Plugin.Log.Debug($"Throw event: launching {Mathf.Max(1, throwCount)} projectile(s).");
            return;
        }

        if (IsCubeAnimationType(type))
        {
            if (cfg?.Paused == true || Plugin.IsMapProtectionActive())
            {
                Plugin.Log.Debug($"Paused: ignoring {type}.");
                return;
            }
            var animData = root["data"] as JObject ?? root;
            var animAction = animData["action"]?.Value<string>()
                             ?? animData["animation"]?.Value<string>() ?? "lurk";
            var animUser = animData["user"]?.Value<string>()
                           ?? animData["username"]?.Value<string>()
                           ?? animData["displayName"]?.Value<string>()
                           ?? animData["display_name"]?.Value<string>()
                           ?? "Anonymous";
            var animScale = GetNullableFloat(animData, "scale", "size");

            if (string.Equals(animAction, "lurk", StringComparison.OrdinalIgnoreCase))
            {
                CubeAnimationController.PlayLurk(animUser, animScale);
                Plugin.Log.Info($"Cube animation: {animUser} lurks.");
            }
            else
            {
                Plugin.Log.Debug($"Cube animation: unknown action '{animAction}' (only 'lurk' is implemented).");
            }
            return;
        }

        if (string.Equals(type, "skip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "skip_current", StringComparison.OrdinalIgnoreCase))
        {
            NoteCosmeticController.SkipCurrentEvent();
            Plugin.Log.Info("Skip current event requested.");
            return;
        }

        if (string.Equals(type, "bomb", StringComparison.OrdinalIgnoreCase))
        {
            var bombData = root["data"] as JObject ?? root;
            var bombUser = bombData["user"]?.Value<string>() ??
                           bombData["username"]?.Value<string>() ??
                           bombData["displayName"]?.Value<string>() ??
                           bombData["display_name"]?.Value<string>() ??
                           "You";
            var bombAmount = bombData["amount"]?.Value<int>() ??
                             bombData["bits"]?.Value<int>() ??
                             bombData["points"]?.Value<int>() ??
                             1;
            var bombMessage = bombData["message"]?.Value<string>() ??
                              bombData["chatMessage"]?.Value<string>() ??
                              string.Empty;
            var bombRawColor = bombData["color"]?.Value<string>() ?? string.Empty;
            Color? bombColorArg = null;
            if (!string.IsNullOrEmpty(bombRawColor) && TryParseHex(bombRawColor, out var bombParsedColor))
                bombColorArg = bombParsedColor;
            Plugin.DispatchStreamEvent("bomb", bombUser, bombAmount, bombColorArg, bombMessage);
            return;
        }
        var data = root["data"] as JObject ?? root;

        var user = data["user"]?.Value<string>() ??
                   data["username"]?.Value<string>() ??
                   data["displayName"]?.Value<string>() ??
                   data["display_name"]?.Value<string>() ??
                   "Anonymous";

        var amount = data["amount"]?.Value<int>() ??
                     data["bits"]?.Value<int>() ??
                     data["points"]?.Value<int>() ??
                     0;

        var rawColor = data["color"]?.Value<string>() ?? string.Empty;

        var message = data["message"]?.Value<string>() ??
                      data["chatMessage"]?.Value<string>() ?? string.Empty;

        // Only an explicit payload color is forwarded - otherwise null, so
        // DispatchStreamEvent applies the configured/tier color per event type.
        Color? color = null;
        if (!string.IsNullOrEmpty(rawColor) && TryParseHex(rawColor, out var parsedColor))
            color = parsedColor;

        Plugin.DispatchStreamEvent(type, user, amount, color, message);
    }

    internal static void ClearAllEffects()
    {
        NoteCosmeticController.ClearQueues();
        ParticleSpawner.StopAll();
        ProjectileThrower.StopAll();
        FlashbangController.StopAll();
        ProjectionController.StopAll();
        CubeAnimationController.StopAll();
    }

    private static bool IsThrowType(string type)
    {
        return string.Equals(type, "throw", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "throw_cube", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "cube", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCubeAnimationType(string type)
    {
        return string.Equals(type, "cubeanimation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "cube_animation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "cube_anim", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "lurk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlashbangType(string type)
    {
        return string.Equals(type, "flashbang", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "flash_bang", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "flash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectionType(string type)
    {
        return string.Equals(type, "projection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "project", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "proj", StringComparison.OrdinalIgnoreCase);
    }

    private static float? GetNullableFloat(JToken data, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = data[key];
            if (token == null || token.Type == JTokenType.Null)
                continue;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<float>();
            var s = token.Value<string>();
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                return f;
        }
        return null;
    }

    internal static bool TryParseHex(string hex, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrEmpty(hex))
            return false;

        hex = hex.TrimStart('#');
        if (hex.Length != 6 && hex.Length != 8)
            return false;

        try
        {
            var r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber) / 255f;
            var g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber) / 255f;
            var b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber) / 255f;
            var a = hex.Length == 8 ? int.Parse(hex.Substring(6, 2), NumberStyles.HexNumber) / 255f : 1f;

            color = new Color(r, g, b, a);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

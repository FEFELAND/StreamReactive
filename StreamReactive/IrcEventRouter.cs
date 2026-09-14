using System;

namespace StreamReactive;

/// <summary>
/// Routes Twitch IRC events into the same effect pipeline the WebSocket uses.
/// TwitchChatReader (which runs on a background thread) detects events and calls
/// RuntimeHooks.EnqueueIrcEvent; RuntimeHooks pumps those onto the main thread
/// and hands them to <see cref="Handle"/>. Every trigger has its own enable
/// toggle in the Config settings (all default off), so IRC events act as a
/// bot-less alternative without changing any WebSocket-driven behavior. Cooldowns
/// are global (across all chat users) because effects play world-wide anyway.
/// </summary>
internal static class IrcEventRouter
{
    private static readonly object _commandCooldownLock = new();
    private static double _bombLastFiredUtc;
    private static double _throwLastFiredUtc;

    /// <summary>
    /// Applies a single detected IRC event to the effect pipeline. msgId is the
    /// stable id TwitchChatReader assigned (cheer / sub / subscription_gift /
    /// raid / watchstreak / bomb); it is NOT the raw Twitch msg-id tag, so this
    /// class stays decoupled from IRC specifics.
    /// </summary>
    internal static void Handle(string msgId, string user, int amount, string message)
    {
        var cfg = PluginConfig.Instance;
        if (cfg == null) return;

        switch (msgId)
        {
            case "cheer":
                if (!cfg.IrcBitsEnabled) return;
                Plugin.DispatchStreamEvent("bits", user, Math.Max(amount, 1));
                break;

            case "sub":
                // Covers fresh subs (amount 1) and resubs (amount = months so the
                // "subscribed for N months" display reads correctly).
                if (!cfg.IrcSubEnabled) return;
                Plugin.DispatchStreamEvent("subscription", user, Math.Max(amount, 1));
                break;

            case "subscription_gift":
                // Single gifts (1) and mystery/mass gifts (the count).
                if (!cfg.IrcGiftEnabled) return;
                Plugin.DispatchStreamEvent("subscription_gift", user, Math.Max(amount, 1));
                break;

            case "raid":
                if (!cfg.IrcRaidEnabled) return;
                Plugin.DispatchStreamEvent("raid", user, Math.Max(amount, 1));
                break;

            case "bomb":
                if (!cfg.IrcBombEnabled) return;
                if (!CommandCooldownReady(cfg.IrcBombCooldown, ref _bombLastFiredUtc))
                {
                    NoteCosmeticController.VerboseLog($"IRC bomb command ignored (cooldown): {user}");
                    return;
                }
                Plugin.DispatchStreamEvent("bomb", user, 1, message: message);
                break;

            case "throw":
                if (!cfg.IrcThrowEnabled) return;
                if (!CommandCooldownReady(cfg.IrcThrowCooldown, ref _throwLastFiredUtc))
                {
                    NoteCosmeticController.VerboseLog($"IRC throw command ignored (cooldown): {user}");
                    return;
                }
                // Mirror the websocket throw path: it bypasses the stream-event
                // queue and is suppressed while paused / on a protected map. Always
                // throws exactly one block (any text after the command is ignored).
                if (cfg.Paused || Plugin.IsMapProtectionActive())
                {
                    NoteCosmeticController.VerboseLog($"Paused: ignoring {msgId}.");
                    return;
                }
                ProjectileThrower.Throw(1, null, null, null);
                NoteCosmeticController.VerboseLog($"IRC throw command: {user} launched (cooldown gated).");
                break;
        }
    }

    /// <summary>
    /// True when the chat message's FIRST word exactly matches the configured bomb
    /// command (e.g. "!bomb"). The '!' is required - a message of just "bomb" does
    /// not trigger. payload receives any words after the command (shown as the
    /// bomb's viewer text, subject to the Allow Viewer Text toggle upstream).
    /// </summary>
    internal static bool MatchesBombCommand(string message, out string payload)
    {
        return MatchesCommand(message, PluginConfig.Instance?.IrcBombCommand, out payload);
    }

    /// <summary>
    /// True when the chat message's FIRST word exactly matches the configured throw
    /// command (e.g. "!throw"). Always throws exactly one block regardless of any
    /// text after the command.
    /// </summary>
    internal static bool MatchesThrowCommand(string message, out string payload)
    {
        return MatchesCommand(message, PluginConfig.Instance?.IrcThrowCommand, out payload);
    }

    private static bool MatchesCommand(string message, string? command, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(command)) return false;

        var firstWord = FirstWord(message, out payload);
        return string.Equals(firstWord, command!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstWord(string message, out string payload)
    {
        payload = string.Empty;
        var messageTrimmed = message.Trim();
        var space = messageTrimmed.IndexOf(' ');
        if (space > 0)
        {
            payload = messageTrimmed.Substring(space + 1).Trim();
            return messageTrimmed.Substring(0, space);
        }
        return messageTrimmed;
    }

    private static bool CommandCooldownReady(int cooldownSeconds, ref double lastFiredUtc)
    {
        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        lock (_commandCooldownLock)
        {
            var elapsed = now - lastFiredUtc;
            if (cooldownSeconds > 0 && elapsed < cooldownSeconds)
                return false;
            lastFiredUtc = now;
            return true;
        }
    }
}
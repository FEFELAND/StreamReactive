using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace StreamReactive;

internal sealed class RuntimeHooks : MonoBehaviour
{
    private static RuntimeHooks? _instance;

    // Thread-safe queue for events arriving from background threads (IRC reader).
    private static readonly ConcurrentQueue<(string user, string[] emotes)> _pendingEmotes = new();

    // Thread-safe queue for full chat messages arriving from the IRC reader.
    // colorHex is the viewer's Twitch chat color (hex from the IRC color tag),
    // or null when the viewer hasn't set one. badges is the RAW IRC badge list
    // (e.g. "moderator/1,vip/1") so the chat panel can look up image badges;
    // empty when the viewer has none. id is the
    // IRC message id, used to remove the line when Twitch deletes the message.
    // isShared marks messages relayed in from a Shared Chat partner channel.
    // emotes carries the emote word spans (7TV/BTTV/FFZ, Twitch native, emoji)
    // detected on the raw message, used by the chat panel to render them inline.
    private static readonly ConcurrentQueue<(string user, string message, string? colorHex, string badges, string id, bool isShared, ChatPanelController.EmoteSpan[]? emotes)> _pendingChat = new();

    // Queue for Twitch system events (subs, raids, watch streaks, timeouts,
    // bans, chat-mode changes...) rendered as styled lines in the chat panel.
    // detail is optional user text (e.g. a resub message) shown indented under
    // the event line. isShared marks events relayed via a Shared Chat partner.
    private static readonly ConcurrentQueue<(string text, string? detail, bool isShared)> _pendingSystem = new();

    // Queue for IRC-detected events mapped to WebSocket-style effects (cheers,
    // subs, gifted subs, raids, watch streaks, chat bomb commands). Filled from
    // the IRC background thread, routed on the main thread via IrcEventRouter so
    // config toggles/cooldowns and Plugin.DispatchStreamEvent all run on Unity's
    // thread (matching how WebSocket messages are handled).
    private static readonly ConcurrentQueue<(string msgId, string user, int amount, string message)> _pendingIrcEffects = new();

    // Queue for work that must run on the main thread (e.g. creating textures
    // after a background-thread decode completes).
    private static readonly ConcurrentQueue<Action> _mainThreadActions = new();

    internal static void EnqueueIrcEvent(string msgId, string user, int amount = 0, string message = "")
    {
        if (string.IsNullOrEmpty(msgId)) return;
        _pendingIrcEffects.Enqueue((msgId, user, amount, message));
    }

    internal static void EnqueueEmoteEvent(string user, string[] emotes)
    {
        _pendingEmotes.Enqueue((user, emotes));
    }

    internal static void EnqueueChatMessage(string user, string message, string? colorHex = null, string badges = "", string id = "", bool isShared = false, ChatPanelController.EmoteSpan[]? emotes = null)
    {
        _pendingChat.Enqueue((user, message, colorHex, badges, id, isShared, emotes));
    }

    internal static void EnqueueSystemEvent(string text, string? detail = null, bool isShared = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        _pendingSystem.Enqueue((text, detail, isShared));
    }

    internal static void RunOnMainThread(Action action) => _mainThreadActions.Enqueue(action);

    internal static void EnsureCreated()
    {
        if (_instance != null)
            return;

        var go = new GameObject("StreamReactiveRuntimeHooks");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<RuntimeHooks>();
    }

    internal static void RunCoroutine(IEnumerator coroutine)
    {
        EnsureCreated();
        _instance!.StartCoroutine(coroutine);
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        _instance = this;
    }

    private void LateUpdate()
    {
        // Pump main-thread actions FIRST so emote finalization (which depends on
        // this queue) can never be blocked by an exception thrown while handling
        // the other event sources below.
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: main-thread action failed: {ex.Message}"); }
        }

        try { NoteCosmeticController.ProcessPendingStreamEvents(); }
        catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: stream events failed: {ex.Message}"); }

        ProcessPendingEmotes();
        ProcessPendingChat();
        ProcessPendingSystem();
        ProcessPendingIrcEffects();

        try { Plugin.TryTickPendingFlashbangs(); }
        catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: flashbang timers failed: {ex.Message}"); }
    }

    private void ProcessPendingChat()
    {
        while (_pendingChat.TryDequeue(out var item))
        {
            try { ChatPanelController.Instance?.AddMessage(item.user, item.message, item.colorHex, item.badges, item.id, item.isShared, item.emotes); }
            catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: chat event failed for '{item.user}': {ex.Message}"); }
        }
    }

    private void ProcessPendingSystem()
    {
        while (_pendingSystem.TryDequeue(out var item))
        {
            try { ChatPanelController.Instance?.AddSystemEvent(item.text, item.detail, item.isShared); }
            catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: system event failed: {ex.Message}"); }
        }
    }

    private void ProcessPendingEmotes()
    {
        while (_pendingEmotes.TryDequeue(out var item))
        {
            try { Plugin.Instance?.HandleEmoteDetected(item.user, item.emotes); }
            catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: emote event failed for '{item.user}': {ex.Message}"); }
        }
    }

    private void ProcessPendingIrcEffects()
    {
        while (_pendingIrcEffects.TryDequeue(out var item))
        {
            try { IrcEventRouter.Handle(item.msgId, item.user, item.amount, item.message); }
            catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: IRC effect '{item.msgId}' failed: {ex.Message}"); }
        }
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }
}

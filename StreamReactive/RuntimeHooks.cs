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
    // or null when the viewer hasn't set one. badges is preformatted badge text
    // (e.g. "[MOD] [VIP]") from the IRC badges tag, empty when none.
    private static readonly ConcurrentQueue<(string user, string message, string? colorHex, string badges)> _pendingChat = new();

    // Queue for work that must run on the main thread (e.g. creating textures
    // after a background-thread decode completes).
    private static readonly ConcurrentQueue<Action> _mainThreadActions = new();

    internal static void EnqueueEmoteEvent(string user, string[] emotes)
    {
        _pendingEmotes.Enqueue((user, emotes));
    }

    internal static void EnqueueChatMessage(string user, string message, string? colorHex = null, string badges = "")
    {
        _pendingChat.Enqueue((user, message, colorHex, badges));
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
    }

    private void ProcessPendingChat()
    {
        while (_pendingChat.TryDequeue(out var item))
        {
            try { ChatPanelController.Instance?.AddMessage(item.user, item.message, item.colorHex, item.badges); }
            catch (Exception ex) { Plugin.Log.Warn($"RuntimeHooks: chat event failed for '{item.user}': {ex.Message}"); }
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

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }
}

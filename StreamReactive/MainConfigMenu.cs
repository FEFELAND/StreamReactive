using System;
using System.Collections;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;
using BS_Utils.Utilities;
using HMUI;
using UnityEngine;

namespace StreamReactive;

internal sealed class MainConfigMenu : MonoBehaviour
{
    private static MainConfigMenu? _instance;
    private MenuButton? _menuButton;
    private StreamReactiveFlowCoordinator? _flowCoordinator;
    private Coroutine? _menuButtonRegistrationCoroutine;
    private bool _menuHooksSubscribed;
    private bool _buttonRegistered;

    internal static void Init()
    {
        if (_instance != null) return;

        var go = new GameObject("StreamReactiveMainConfigMenu");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<MainConfigMenu>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        SubscribeMenuHooks();
    }

    private void OnDestroy()
    {
        UnsubscribeMenuHooks();
    }

    private void SubscribeMenuHooks()
    {
        if (_menuHooksSubscribed) return;

#pragma warning disable CS0618
        BSEvents.menuSceneLoadedFresh += OnMenuSceneLoadedFresh;
#pragma warning restore CS0618
        BSEvents.gameSceneLoaded += OnGameSceneLoaded;
        _menuHooksSubscribed = true;
    }

    private void UnsubscribeMenuHooks()
    {
        if (!_menuHooksSubscribed) return;

#pragma warning disable CS0618
        BSEvents.menuSceneLoadedFresh -= OnMenuSceneLoadedFresh;
#pragma warning restore CS0618
        BSEvents.gameSceneLoaded -= OnGameSceneLoaded;
        _menuHooksSubscribed = false;
    }

    private void OnMenuSceneLoadedFresh()
    {
        _buttonRegistered = false;
        // Start registering our custom BSML tags immediately (retries each frame
        // until the container is up) so they exist way before the settings
        // screen could be parsed.
        if (_tagRegistrationCoroutine == null)
            _tagRegistrationCoroutine = StartCoroutine(RegisterTagsWhenReady());

        if (_menuButtonRegistrationCoroutine != null) return;

        _menuButtonRegistrationCoroutine = StartCoroutine(RegisterButtonWhenReady());
    }

    private void OnGameSceneLoaded()
    {
        _buttonRegistered = false;

        if (_menuButtonRegistrationCoroutine != null)
        {
            StopCoroutine(_menuButtonRegistrationCoroutine);
            _menuButtonRegistrationCoroutine = null;
        }
    }

    private Coroutine? _tagRegistrationCoroutine;

    // Cheap: exits the moment registration succeeds. The per-parser guard in
    // TryRegisterBsmlTags makes later runs no-ops.
    private IEnumerator RegisterTagsWhenReady()
    {
        for (var attempt = 1; attempt <= 120 && !BsmlTagsRegistered; attempt++)
        {
            if (TryRegisterBsmlTags())
                break;
            yield return null;
        }

        if (!BsmlTagsRegistered)
            Plugin.Log.Warn("MainConfigMenu: BSML tag registration did not complete in time; retrying when settings opens.");

        _tagRegistrationCoroutine = null;
    }

    private IEnumerator RegisterButtonWhenReady()
    {
        _menuButton = new MenuButton(
            "StreamReactive",
            "Configure how your Stream Reacts!",
            ShowSettings,
            true);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            yield return null;

            try
            {
                MenuButtons.Instance.RegisterButton(_menuButton);
                _buttonRegistered = true;
                break;
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"MainConfigMenu: unexpected error while registering menu button: {ex}");
                break;
            }
        }

        if (!_buttonRegistered)
            Plugin.Log.Warn("MainConfigMenu: menu button registration timed out");

        _menuButtonRegistrationCoroutine = null;
    }

    private static void ShowSettings()
    {
        var instance = _instance;
        if (instance == null) return;

        // Never parse the settings BSML before our custom <sr-scroll> tags are
        // registered. Registering and parsing must hit the same BSMLParser
        // instance (its tag registry lives on the instance), and the parser is
        // unavailable until the menu's Zenject container finishes installing.
        // The eager menu-load retry below usually has it ready already; this
        // just waits a short while when a click lands mid-install.
        if (BsmlTagsRegistered)
        {
            instance.PresentSettings();
        }
        else if (instance._settingsCoroutine == null)
        {
            instance._settingsCoroutine = instance.StartCoroutine(instance.PresentSettingsWhenReady());
        }
    }

    private Coroutine? _settingsCoroutine;

    private IEnumerator PresentSettingsWhenReady()
    {
        for (var attempt = 1; attempt <= 120 && !BsmlTagsRegistered; attempt++)
        {
            TryRegisterBsmlTags();
            yield return null;
        }

        if (!BsmlTagsRegistered)
        {
            Plugin.Log.Error("MainConfigMenu: custom BSML tags never registered; not presenting settings to avoid parsing a broken screen.");
            _settingsCoroutine = null;
            yield break;
        }

        _settingsCoroutine = null;
        PresentSettings();
    }

    private void PresentSettings()
    {
        Plugin.Log.Debug("MainConfigMenu: presenting StreamReactive settings flow");

        if (_flowCoordinator == null)
            _flowCoordinator = BeatSaberUI.CreateFlowCoordinator<StreamReactiveFlowCoordinator>();

        try
        {
            BeatSaberUI.MainFlowCoordinator.PresentFlowCoordinator(_flowCoordinator);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"MainConfigMenu: failed to present settings (BSML parse error?): {ex}");
        }
    }

    /// <summary>
    /// Custom tags are registered on the current <see cref="BSMLParser"/>
    /// instance, keyed by the parser itself so registration is idempotent per
    /// instance (RegisterTag's Dictionary.Add throws on duplicate aliases) and
    /// re-runs automatically if the menu container ever builds a fresh parser.
    /// This retries every frame from menu load because BSMLParser.Instance
    /// throws "Tried getting BSMLParser too early!" until Zenject installs the
    /// menu container - and the settings screen must NEVER be parsed before
    /// these tags exist, or BSML fails the whole screen with "Invalid BSML:
    /// Failed to parse element sr-scroll".
    /// </summary>
    private static BSMLParser? _registeredParser;

    private static bool BsmlTagsRegistered => _registeredParser != null;

    private static bool TryRegisterBsmlTags()
    {
        BSMLParser parser;
        try
        {
            parser = BSMLParser.Instance;
        }
        catch (InvalidOperationException)
        {
            // Container not installed yet; caller should retry later.
            return false;
        }

        if (_registeredParser == parser)
            return true;

        var ok = RegisterTagSafely(parser, new ScrollContainerTag());
        ok &= RegisterTagSafely(parser, new ScrollContainerTallTag());
        if (ok)
            _registeredParser = parser;
        return ok;
    }

    private static bool RegisterTagSafely(BSMLParser parser, BeatSaberMarkupLanguage.Tags.BSMLTag tag)
    {
        try
        {
            parser.RegisterTag(tag);
            return true;
        }
        catch (ArgumentException)
        {
            // Alias already present on this parser instance - just as good.
            Plugin.Log.Debug($"MainConfigMenu: tag '{tag.Aliases[0]}' already registered.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"MainConfigMenu: failed to register tag '{tag.Aliases[0]}': {ex}");
            return false;
        }
    }
}

public sealed class StreamReactiveFlowCoordinator : FlowCoordinator
{
    private StreamReactiveSettingsViewController? _settingsViewController;

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        if (!firstActivation) return;

        SetTitle("StreamReactive");
        showBackButton = true;
        _settingsViewController ??= BeatSaberUI.CreateViewController<StreamReactiveSettingsViewController>();
        ProvideInitialViewControllers(_settingsViewController);
    }

    protected override void BackButtonWasPressed(ViewController topViewController)
    {
        BeatSaberUI.MainFlowCoordinator.DismissFlowCoordinator(this);
    }
}

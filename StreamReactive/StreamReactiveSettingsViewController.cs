using System;
using System.Collections.Generic;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.Parser;
using BeatSaberMarkupLanguage.ViewControllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StreamReactive;

[ViewDefinition("StreamReactive.Resources.Settings.bsml")]
public sealed class StreamReactiveSettingsViewController : BSMLAutomaticViewController
{
    private const string DefaultPage = "general";
    private static readonly string[] PageKeys = { "general", "bomb", "bits", "subs", "raid", "flashbang", "projection", "throw", "emotes", "chat", "about" };

    /// <summary>True only while the Throw settings page is on screen; drives the capsule preview visual.</summary>
    internal static bool ThrowPreviewVisible { get; private set; }
    private static readonly Color ActiveNavColor = Color.white;
    private static readonly Color InactiveNavColor = new(1f, 1f, 1f, 0.45f);

    private string _selectedPage = DefaultPage;

    // Tracked so Update can detect when the Enabled/Paused toggles change from
    // outside the settings UI (websocket socket/pause/unpause events) and push a
    // PropertyChanged so the bind-value="true" toggle controls refresh their
    // displayed state.
    private bool _syncedEnabled = true;
    private bool _syncedPaused;

    // GitHub update banner (top of the General page). Checked in the background
    // on the main thread via a coroutine: fire-and-forget, best-effort, and
    // unable to throw out of the method - any failure just leaves a neutral
    // "couldn't check" message.
    private const string UpdateCheckUrl = "https://api.github.com/repos/FEFELAND/StreamReactive/releases/latest";
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(10);
    private string _updateStatus = "";
    private bool _updateCheckInProgress;
    private DateTime _lastUpdateCheckUtc;

    private PluginConfig Config => PluginConfig.Instance!;

    [UIObject("page-general")]
    private GameObject? _pageGeneral = null!;

    [UIObject("page-bomb")]
    private GameObject? _pageBomb = null!;

    [UIObject("page-bits")]
    private GameObject? _pageBits = null!;

    [UIObject("page-subs")]
    private GameObject? _pageSubs = null!;

    [UIObject("page-raid")]
    private GameObject? _pageRaid = null!;

    [UIObject("page-flashbang")]
    private GameObject? _pageFlashbang = null!;

    [UIObject("page-projection")]
    private GameObject? _pageProjection = null!;

    [UIObject("page-throw")]
    private GameObject? _pageThrow = null!;

    [UIObject("page-emotes")]
    private GameObject? _pageEmotes = null!;

    [UIObject("page-chat")]
    private GameObject? _pageChat = null!;

    [UIObject("page-about")]
    private GameObject? _pageAbout = null!;

    [UIObject("nav-general")]
    private GameObject? _navGeneral = null!;

    [UIObject("nav-bomb")]
    private GameObject? _navBomb = null!;

    [UIObject("nav-bits")]
    private GameObject? _navBits = null!;

    [UIObject("nav-subs")]
    private GameObject? _navSubs = null!;

    [UIObject("nav-raid")]
    private GameObject? _navRaid = null!;

    [UIObject("nav-flashbang")]
    private GameObject? _navFlashbang = null!;

    [UIObject("nav-projection")]
    private GameObject? _navProjection = null!;

    [UIObject("nav-throw")]
    private GameObject? _navThrow = null!;

    [UIObject("nav-emotes")]
    private GameObject? _navEmotes = null!;

    [UIObject("nav-chat")]
    private GameObject? _navChat = null!;

    [UIObject("nav-about")]
    private GameObject? _navAbout = null!;

    [UIObject("bomb-particle-color")]
    private GameObject? _bombParticleColorObject = null!;

    [UIObject("bomb-rainbow-speed")]
    private GameObject? _bombRainbowSpeedObject = null!;

    [UIObject("capsule-bone-path")]
    private GameObject? _capsuleBonePathObject = null!;

    [UIObject("throw-air-time-row")]
    private GameObject? _throwAirTimeObject = null!;

    [UIObject("throw-gravity-row")]
    private GameObject? _throwGravityObject = null!;

    [UIObject("throw-speed-row")]
    private GameObject? _throwSpeedObject = null!;

    [UIObject("throw-explode-color-row")]
    private GameObject? _throwExplodeColorRow = null!;

    [UIObject("throw-explode-section")]
    private GameObject? _throwExplodeSection = null!;

    [UIComponent("bomb-sound")]
    private DropDownListSetting? _bombSoundDropdown = null!;

    [UIComponent("bit-tier-default-sound")]
    private DropDownListSetting? _bitTierDefaultSoundDropdown = null!;

    [UIComponent("bit-tier-100-sound")]
    private DropDownListSetting? _bitTier100SoundDropdown = null!;

    [UIComponent("bit-tier-1000-sound")]
    private DropDownListSetting? _bitTier1000SoundDropdown = null!;

    [UIComponent("bit-tier-5000-sound")]
    private DropDownListSetting? _bitTier5000SoundDropdown = null!;

    [UIComponent("bit-tier-10000-sound")]
    private DropDownListSetting? _bitTier10000SoundDropdown = null!;

    [UIComponent("sub-sound")]
    private DropDownListSetting? _subSoundDropdown = null!;

    [UIComponent("raid-sound")]
    private DropDownListSetting? _raidSoundDropdown = null!;

    [UIComponent("throw-hit-sound")]
    private DropDownListSetting? _throwHitSoundDropdown = null!;

    [UIParams]
    private BSMLParserParams _parserParams = null!;

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
        _syncedEnabled = Config?.Enabled ?? true;
        _syncedPaused = Config?.Paused ?? false;
        if (firstActivation)
        {
            PrepareNavButtons();
            ClipTabScrollRaycasts();
        }
        ApplyPageSelection();
        UpdateConditionalVisibility();
        RefreshSoundDropdowns();

        // Check for a newer GitHub release whenever the settings open, but never
        // more often than every 10 minutes per session. Best-effort: hitting the
        // net before the repo is public / has a release just yields a neutral line.
        if (!_updateCheckInProgress
            && DateTime.UtcNow - _lastUpdateCheckUtc > UpdateCheckInterval)
        {
            StartCoroutine(CheckForUpdatesCoroutine());
        }

        // #post-parse only fires once per view instance (creating this settings
        // view). Re-entering the settings reuses the cached view, so the disclaimer
        // wouldn't re-show after backing out without acknowledging. Firing here on
        // every activation makes it reappear each time the user opens the settings,
        // until they click "I understand".
        if (Config != null && !Config.ClickedDisclaimer && _parserParams != null)
            StartCoroutine(ShowDisclaimerWhenActive());
    }

    private System.Collections.IEnumerator ShowDisclaimerWhenActive()
    {
        // This settings screen is presented inside a FlowCoordinator, and presenting
        // it deactivates the MainMenuViewController behind it. Showing a BSML modal
        // reparents BSMLModal; doing that while the MainMenuViewController is still
        // mid-deactivation throws "Cannot set the parent ... while activating or
        // deactivating" and closes the modal instantly. The deactivation (and the
        // flow presentation animation) is transient, so wait real time well past it
        // before showing the modal, so the reparent is safe.
        while (transform == null || !isActiveAndEnabled)
            yield return null;

        var started = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - started < 0.5f)
            yield return null;

        if (Config != null && !Config.ClickedDisclaimer)
            _parserParams?.EmitEvent("show-disclaimer");
    }

    // Shown once on the settings panel until acknowledged; the flag persists in the
    // config so it never appears again after "I understand" is clicked.
    [UIAction("accept-disclaimer")]
    private void AcceptDisclaimer()
    {
        if (Config != null)
            Config.ClickedDisclaimer = true;
        _parserParams?.EmitEvent("hide-disclaimer");
    }

    [UIValue("update-status-text")]
    public string UpdateStatusText => _updateStatus;

    /// <summary>
    /// Fetches the latest GitHub release tag (async, main-thread safe via
    /// UnityWebRequest) and compares it to the running assembly version. Always
    /// degrades to a neutral message - it must never throw or block the UI, and
    /// it still works even while the repo is private (GitHub answers 404 for the
    /// anonymous releases API, which we treat as "no public release yet").
    /// </summary>
    private System.Collections.IEnumerator CheckForUpdatesCoroutine()
    {
        _updateCheckInProgress = true;

        // Unity's IL2CPP / Mono runtime forbids yield inside try+catch, so we
        // yield the request outside of any error handling block.
        using (var req = UnityEngine.Networking.UnityWebRequest.Get(UpdateCheckUrl))
        {
            req.SetRequestHeader("User-Agent", "StreamReactive");
            req.timeout = 10;
            yield return req.SendWebRequest();
            _lastUpdateCheckUtc = DateTime.UtcNow;

            try
            {
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(req.downloadHandler.text);
                    var tag = root["tag_name"]?.ToString() ?? "";
                    var latest = ParseVersionTag(tag);
                    var current = CurrentAssemblyVersion;
                    if (latest != null && current != null)
                    {
                        _updateStatus = latest > current
                            ? "<color=#fbbf24>Update available: " + tag + "</color>"
                            : "<color=#4ade80>Up to date: " + FormatVersion(current) + "</color>";
                    }
                    else
                    {
                        _updateStatus = "<color=#9ca3af>Couldn't read update info</color>";
                    }
                }
                else if (req.responseCode == 404)
                {
                    // Private repo or no release published yet.
                    _updateStatus = "<color=#9ca3af>No GitHub release yet - nothing to update</color>";
                }
                else
                {
                    _updateStatus = "<color=#9ca3af>Couldn't check for updates</color>";
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"Update check: {ex.Message}");
                _updateStatus = "<color=#9ca3af>Couldn't check for updates</color>";
            }
        }

        _updateCheckInProgress = false;
        NotifyPropertyChanged(nameof(UpdateStatusText));
    }

    private static readonly Version? CurrentAssemblyVersion =
        typeof(StreamReactiveSettingsViewController).Assembly.GetName().Version;

    private static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>
    /// Parses "v1.2.0", "1.2.0", "1.2.0-beta1", etc. into a comparable Version.
    /// Returns null when the tag has no usable version so callers degrade quietly.
    /// </summary>
    private static Version? ParseVersionTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var s = tag.Trim();
        var start = 0;
        while (start < s.Length && !char.IsDigit(s[start])) start++;
        s = s.Substring(start);
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// Kills the built-in hover/press color animations on the nav buttons -
    /// their tweens fight our manual label coloring and leave labels stuck
    /// highlighted after un-hovering.
    /// </summary>
    private void PrepareNavButtons()
    {
        foreach (var key in PageKeys)
        {
            var nav = GetNavButton(key);
            if (nav == null)
                continue;

            foreach (var selectable in nav.GetComponents<Selectable>())
                selectable.transition = Selectable.Transition.None;

            var animationController = nav.GetComponent("UIButtonAnimationController") as Behaviour;
            if (animationController != null)
                animationController.enabled = false;
        }
    }

    /// <summary>
    /// The tide tab pages (Bits tiers, Throw sub-pages) use a <c>tab-selector</c>
    /// followed by <c>sr-scroll</c> content. Scrolling moves rows up over the tab
    /// strip; <see cref="RectMask2D"/> clips them visually but leaves them
    /// raycastable, so off-screen rows swallow clicks aimed at the tab buttons.
    /// Attach a <see cref="ScrollRaycastClip"/> to each such scroll content so
    /// scrolled-out rows stop receiving clicks. Scoped to these two pages only -
    /// the left sidebar (which the BSML modal keyboard overlaps) is left alone.
    /// </summary>
    private void ClipTabScrollRaycasts()
    {
        var pages = new GameObject?[] { _pageBits, _pageThrow };
        foreach (var page in pages)
        {
            if (page == null)
                continue;

            foreach (var scrollRect in page.GetComponentsInChildren<ScrollRect>(true))
            {
                if (scrollRect.content == null)
                    continue;
                if (scrollRect.content.GetComponent<ScrollRaycastClip>() != null)
                    continue;
                scrollRect.content.gameObject.AddComponent<ScrollRaycastClip>().Init(scrollRect);
            }
        }
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        ThrowPreviewVisible = false;
    }

    [UIAction("select-general")]
    private void SelectGeneral() => SelectPage("general");

    [UIAction("select-bomb")]
    private void SelectBomb() => SelectPage("bomb");

    [UIAction("select-bits")]
    private void SelectBits() => SelectPage("bits");

    [UIAction("select-subs")]
    private void SelectSubs() => SelectPage("subs");

    [UIAction("select-raid")]
    private void SelectRaid() => SelectPage("raid");

    [UIAction("select-flashbang")]
    private void SelectFlashbang() => SelectPage("flashbang");

    [UIAction("select-projection")]
    private void SelectProjection() => SelectPage("projection");

    [UIAction("select-throw")]
    private void SelectThrow() => SelectPage("throw");

    [UIAction("select-emotes")]
    private void SelectEmotes() => SelectPage("emotes");

    [UIAction("select-chat")]
    private void SelectChat() => SelectPage("chat");

    [UIAction("select-about")]
    private void SelectAbout() => SelectPage("about");

    [UIAction("reset-chat-position")]
    private void ResetChatPosition()
    {
        if (Config != null)
            Config.ChatLastPos = Vector3.zero;

        ChatPanelController.Instance?.ResetPosition();
    }

    [UIAction("open-dashboard")]
    private void OpenDashboard()
    {
        var port = Config?.WebSocketPort ?? 41243;
        try
        {
            System.Diagnostics.Process.Start($"http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Settings: failed to open test dashboard in browser: {ex}");
        }
    }

    private void SelectPage(string page)
    {
        if (Array.IndexOf(PageKeys, page) < 0) return;

        _selectedPage = page;
        ApplyPageSelection();
    }

    private void ApplyPageSelection()
    {
        foreach (var key in PageKeys)
        {
            var isActive = key == _selectedPage;

            var page = GetPage(key);
            if (page != null && page.activeSelf != isActive)
                page.SetActive(isActive);
        }

        ApplyNavLabelColors();
        ThrowPreviewVisible = _selectedPage == "throw";
    }

    private void ApplyNavLabelColors()
    {
        foreach (var key in PageKeys)
        {
            var nav = GetNavButton(key);
            if (nav == null) continue;

            var label = nav.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
                label.color = key == _selectedPage ? ActiveNavColor : InactiveNavColor;
        }
    }

    // Re-assert label colors every frame so any hover tween that slips past
    // PrepareNavButtons is immediately corrected. Also watches the Enabled/Paused
    // config values so a websocket socket/pause/unpause event changes the on-screen
    // toggles live (via the bind-value="true" mechanism) even while settings is open.
    private void Update()
    {
        ApplyNavLabelColors();

        var cfg = Config;
        if (cfg == null) return;
        if (cfg.Enabled != _syncedEnabled)
        {
            _syncedEnabled = cfg.Enabled;
            NotifyPropertyChanged(nameof(Enabled));
        }
        if (cfg.Paused != _syncedPaused)
        {
            _syncedPaused = cfg.Paused;
            NotifyPropertyChanged(nameof(Paused));
        }
    }

    private GameObject? GetPage(string key)
    {
        return key switch
        {
            "general" => _pageGeneral,
            "bomb" => _pageBomb,
            "bits" => _pageBits,
            "subs" => _pageSubs,
            "raid" => _pageRaid,
            "flashbang" => _pageFlashbang,
            "projection" => _pageProjection,
            "throw" => _pageThrow,
            "emotes" => _pageEmotes,
            "chat" => _pageChat,
            "about" => _pageAbout,
            _ => null
        };
    }

    private GameObject? GetNavButton(string key)
    {
        return key switch
        {
            "general" => _navGeneral,
            "bomb" => _navBomb,
            "bits" => _navBits,
            "subs" => _navSubs,
            "raid" => _navRaid,
            "flashbang" => _navFlashbang,
            "projection" => _navProjection,
            "throw" => _navThrow,
            "emotes" => _navEmotes,
            "chat" => _navChat,
            "about" => _navAbout,
            _ => null
        };
    }

    private void UpdateConditionalVisibility()
    {
        var mode = Config?.BombColorMode ?? StreamReactive.BombColorMode.Static;
        if (_bombParticleColorObject != null)
            _bombParticleColorObject.SetActive(mode == StreamReactive.BombColorMode.Static);
        if (_bombRainbowSpeedObject != null)
            _bombRainbowSpeedObject.SetActive(mode == StreamReactive.BombColorMode.Rainbow);

        var attachMode = Config?.CapsuleAttachMode ?? "Headset";
        if (_capsuleBonePathObject != null)
            _capsuleBonePathObject.SetActive(string.Equals(attachMode, "Avatar Bone", StringComparison.OrdinalIgnoreCase));

        // Trajectory mode: Lofted uses Air Time + Gravity, Zero-G uses Speed.
        var zeroG = string.Equals(Config?.ThrowTrajectory ?? "Lofted", "Zero-G", StringComparison.OrdinalIgnoreCase);
        if (_throwAirTimeObject != null)
            _throwAirTimeObject.SetActive(!zeroG);
        if (_throwGravityObject != null)
            _throwGravityObject.SetActive(!zeroG);
        if (_throwSpeedObject != null)
            _throwSpeedObject.SetActive(zeroG);

        var explodeEnabled = Config?.ThrowExplodeEnabled ?? false;
        if (_throwExplodeSection != null)
            _throwExplodeSection.SetActive(explodeEnabled);

        var useNoteColor = Config?.ThrowExplodeUseNoteColor ?? true;
        if (_throwExplodeColorRow != null)
            _throwExplodeColorRow.SetActive(explodeEnabled && !useNoteColor);
    }

    /// <summary>
    /// Re-scans the Sounds folder and refreshes every event-sound dropdown so
    /// newly dropped OGG files show up in the menu without a restart. The view
    /// controller is cached across opens, so this must run on every activation.
    /// </summary>
    private void RefreshSoundDropdowns()
    {
        SoundManager.RefreshAvailableSounds();
        var choices = SoundManager.SoundChoices();
        RefreshSoundDropdown(_bombSoundDropdown, choices, BombSound);
        RefreshSoundDropdown(_bitTierDefaultSoundDropdown, choices, BitTierDefaultSound);
        RefreshSoundDropdown(_bitTier100SoundDropdown, choices, BitTier100Sound);
        RefreshSoundDropdown(_bitTier1000SoundDropdown, choices, BitTier1000Sound);
        RefreshSoundDropdown(_bitTier5000SoundDropdown, choices, BitTier5000Sound);
        RefreshSoundDropdown(_bitTier10000SoundDropdown, choices, BitTier10000Sound);
        RefreshSoundDropdown(_subSoundDropdown, choices, SubSound);
        RefreshSoundDropdown(_raidSoundDropdown, choices, RaidSound);
        RefreshSoundDropdown(_throwHitSoundDropdown, choices, ThrowHitSound);
    }

    private static void RefreshSoundDropdown(DropDownListSetting? dropdown, List<object> choices, object? current)
    {
        if (dropdown == null || current == null)
            return;

        dropdown.Values = choices;
        dropdown.UpdateChoices();
        // Re-select the configured value; falls back to the first entry ("None")
        // if the file was removed from the folder without clearing the config.
        dropdown.Value = current;
    }

    [UIValue("sound-file-options")]
    public List<object> SoundFileOptions => SoundManager.SoundChoices();

    [UIValue("bomb-sound-file")]
    public string BombSound
    {
        get => SoundManager.DisplayName(Config?.BombSoundFile ?? "");
        set { if (Config != null) Config.BombSoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("bomb-sound-volume")]
    public float BombSoundVolume
    {
        get => Config?.BombSoundVolume ?? 0.8f;
        set { if (Config != null) Config.BombSoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("bit-tier-default-sound-file")]
    public string BitTierDefaultSound
    {
        get => SoundManager.DisplayName(Config?.BitTierDefaultSoundFile ?? "");
        set { if (Config != null) Config.BitTierDefaultSoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("bit-tier-default-sound-volume")]
    public float BitTierDefaultSoundVolume
    {
        get => Config?.BitTierDefaultSoundVolume ?? 0.8f;
        set { if (Config != null) Config.BitTierDefaultSoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("bit-tier-100-sound-file")]
    public string BitTier100Sound
    {
        get => SoundManager.DisplayName(Config?.BitTier100SoundFile ?? "");
        set { if (Config != null) Config.BitTier100SoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("bit-tier-100-sound-volume")]
    public float BitTier100SoundVolume
    {
        get => Config?.BitTier100SoundVolume ?? 0.8f;
        set { if (Config != null) Config.BitTier100SoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("bit-tier-1000-sound-file")]
    public string BitTier1000Sound
    {
        get => SoundManager.DisplayName(Config?.BitTier1000SoundFile ?? "");
        set { if (Config != null) Config.BitTier1000SoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("bit-tier-1000-sound-volume")]
    public float BitTier1000SoundVolume
    {
        get => Config?.BitTier1000SoundVolume ?? 0.8f;
        set { if (Config != null) Config.BitTier1000SoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("bit-tier-5000-sound-file")]
    public string BitTier5000Sound
    {
        get => SoundManager.DisplayName(Config?.BitTier5000SoundFile ?? "");
        set { if (Config != null) Config.BitTier5000SoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("bit-tier-5000-sound-volume")]
    public float BitTier5000SoundVolume
    {
        get => Config?.BitTier5000SoundVolume ?? 0.8f;
        set { if (Config != null) Config.BitTier5000SoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("bit-tier-10000-sound-file")]
    public string BitTier10000Sound
    {
        get => SoundManager.DisplayName(Config?.BitTier10000SoundFile ?? "");
        set { if (Config != null) Config.BitTier10000SoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("bit-tier-10000-sound-volume")]
    public float BitTier10000SoundVolume
    {
        get => Config?.BitTier10000SoundVolume ?? 0.8f;
        set { if (Config != null) Config.BitTier10000SoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("sub-sound-file")]
    public string SubSound
    {
        get => SoundManager.DisplayName(Config?.SubSoundFile ?? "");
        set { if (Config != null) Config.SubSoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("sub-sound-volume")]
    public float SubSoundVolume
    {
        get => Config?.SubSoundVolume ?? 0.8f;
        set { if (Config != null) Config.SubSoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("raid-sound-file")]
    public string RaidSound
    {
        get => SoundManager.DisplayName(Config?.RaidSoundFile ?? "");
        set { if (Config != null) Config.RaidSoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("raid-sound-volume")]
    public float RaidSoundVolume
    {
        get => Config?.RaidSoundVolume ?? 0.8f;
        set { if (Config != null) Config.RaidSoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("throw-hit-sound-file")]
    public string ThrowHitSound
    {
        get => SoundManager.DisplayName(Config?.ThrowHitSoundFile ?? "");
        set { if (Config != null) Config.ThrowHitSoundFile = SoundManager.ConfigName(value); }
    }

    [UIValue("throw-hit-sound-volume")]
    public float ThrowHitSoundVolume
    {
        get => Config?.ThrowHitSoundVolume ?? 0.8f;
        set { if (Config != null) Config.ThrowHitSoundVolume = Mathf.Clamp01(value); }
    }

    [UIValue("enabled")]
    public bool Enabled
    {
        get => Config?.Enabled ?? true;
        set
        {
            if (Config != null) Config.Enabled = value;
            _syncedEnabled = value;
            // Match the socket "on:false" control: clearing effects immediately
            // kills anything already playing. Emotes/chat are a separate system
            // driven by their own toggles, so they are left untouched here.
            if (!value)
                EventDispatcher.ClearAllEffects();
        }
    }

    [UIValue("paused")]
    public bool Paused
    {
        get => Config?.Paused ?? false;
        set
        {
            if (Config != null) Config.Paused = value;
            _syncedPaused = value;
        }
    }

    [UIValue("include-bomb-notes")]
    public bool IncludeBombNotes
    {
        get => Config?.IncludeBombNotes ?? false;
        set { if (Config != null) Config.IncludeBombNotes = value; }
    }

    [UIValue("ws-port")]
    public string WebSocketPort
    {
        get => (Config?.WebSocketPort ?? 41243).ToString();
        set
        {
            if (Config != null && int.TryParse(value, out var port))
                Config.WebSocketPort = port;
        }
    }

    [UIValue("bomb-cosmetic-enabled")]
    public bool BombCosmeticEnabled
    {
        get => Config?.BombCosmeticEnabled ?? true;
        set { if (Config != null) Config.BombCosmeticEnabled = value; }
    }

    [UIValue("bomb-text-size")]
    public float BombTextSize
    {
        get => Config?.BombTextSize ?? 12f;
        set { if (Config != null) Config.BombTextSize = Mathf.Clamp(value, 0.4f, 40f); }
    }

    [UIValue("bomb-text-lifetime")]
    public float BombTextLifetime
    {
        get => Config?.BombTextLifetime ?? 3f;
        set { if (Config != null) Config.BombTextLifetime = value; }
    }

    [UIValue("bomb-color-mode-options")]
    public List<object> BombColorModeOptions { get; } = new() { "Static", "Random", "Rainbow" };

    [UIValue("bomb-color-mode")]
    public string BombColorMode
    {
        get => (Config?.BombColorMode ?? StreamReactive.BombColorMode.Static).ToString();
        set
        {
            if (Config != null && Enum.TryParse<StreamReactive.BombColorMode>(value, true, out var mode))
            {
                Config.BombColorMode = mode;
                UpdateConditionalVisibility();
            }
        }
    }

    [UIValue("bomb-rainbow-speed")]
    public float BombRainbowSpeed
    {
        get => Config?.BombRainbowSpeed ?? 0.5f;
        set { if (Config != null) Config.BombRainbowSpeed = value; }
    }

    [UIValue("bomb-particle-color")]
    public Color BombParticleColor
    {
        get => Config?.BombParticleColor ?? new Color(1f, 0.5f, 0f, 1f);
        set { if (Config != null) Config.BombParticleColor = value; }
    }

    [UIValue("bomb-particle-count")]
    public int BombParticleCount
    {
        get => Config?.BombParticleCount ?? 10000;
        set { if (Config != null) Config.BombParticleCount = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("bomb-particle-scale")]
    public float BombParticleScale
    {
        get => Config?.BombParticleScale ?? 0.015f;
        set { if (Config != null) Config.BombParticleScale = value; }
    }

    [UIValue("bomb-particle-lifetime")]
    public float BombParticleLifetime
    {
        get => Config?.BombParticleLifetime ?? 1.5f;
        set { if (Config != null) Config.BombParticleLifetime = value; }
    }

    [UIValue("bomb-particle-speed")]
    public float BombParticleSpeed
    {
        get => Config?.BombParticleSpeed ?? 5f;
        set { if (Config != null) Config.BombParticleSpeed = value; }
    }

    [UIValue("bits-text-size")]
    public float BitsTextSize
    {
        get => Config?.BitsTextSize ?? 8f;
        set { if (Config != null) Config.BitsTextSize = Mathf.Clamp(value, 0.4f, 40f); }
    }

    [UIValue("bits-text-lifetime")]
    public float BitsTextLifetime
    {
        get => Config?.BitsTextLifetime ?? 3f;
        set { if (Config != null) Config.BitsTextLifetime = value; }
    }

    [UIValue("text-spawn-every-event")]
    public bool TextSpawnEveryEvent
    {
        get => Config?.TextSpawnEveryEvent ?? false;
        set { if (Config != null) Config.TextSpawnEveryEvent = value; }
    }

    [UIValue("bit-tier-10000-block-ratio")]
    public int BitTier10000BlockRatio
    {
        get => Config?.BitTier10000BlockRatio ?? 1;
        set { if (Config != null) Config.BitTier10000BlockRatio = value; }
    }

    [UIValue("bit-tier-5000-block-ratio")]
    public int BitTier5000BlockRatio
    {
        get => Config?.BitTier5000BlockRatio ?? 1;
        set { if (Config != null) Config.BitTier5000BlockRatio = value; }
    }

    [UIValue("bit-tier-1000-block-ratio")]
    public int BitTier1000BlockRatio
    {
        get => Config?.BitTier1000BlockRatio ?? 1;
        set { if (Config != null) Config.BitTier1000BlockRatio = value; }
    }

    [UIValue("bit-tier-100-block-ratio")]
    public int BitTier100BlockRatio
    {
        get => Config?.BitTier100BlockRatio ?? 1;
        set { if (Config != null) Config.BitTier100BlockRatio = value; }
    }

    [UIValue("bit-tier-default-block-ratio")]
    public int BitTierDefaultBlockRatio
    {
        get => Config?.BitTierDefaultBlockRatio ?? 1;
        set { if (Config != null) Config.BitTierDefaultBlockRatio = value; }
    }

    [UIValue("bit-tier-10000-color")]
    public Color BitTier10000Color
    {
        get => Config?.BitTier10000Color ?? new Color(0.913f, 0.098f, 0.086f);
        set { if (Config != null) Config.BitTier10000Color = value; }
    }

    [UIValue("bit-tier-10000-count")]
    public int BitTier10000Count
    {
        get => Config?.BitTier10000Count ?? 20000;
        set { if (Config != null) Config.BitTier10000Count = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("bit-tier-10000-scale")]
    public float BitTier10000Scale
    {
        get => Config?.BitTier10000Scale ?? 0.015f;
        set { if (Config != null) Config.BitTier10000Scale = value; }
    }

    [UIValue("bit-tier-10000-lifetime")]
    public float BitTier10000Lifetime
    {
        get => Config?.BitTier10000Lifetime ?? 1.5f;
        set { if (Config != null) Config.BitTier10000Lifetime = value; }
    }

    [UIValue("bit-tier-10000-speed")]
    public float BitTier10000Speed
    {
        get => Config?.BitTier10000Speed ?? 5f;
        set { if (Config != null) Config.BitTier10000Speed = value; }
    }

    [UIValue("bit-tier-5000-color")]
    public Color BitTier5000Color
    {
        get => Config?.BitTier5000Color ?? new Color(0.125f, 0.635f, 0.969f);
        set { if (Config != null) Config.BitTier5000Color = value; }
    }

    [UIValue("bit-tier-5000-count")]
    public int BitTier5000Count
    {
        get => Config?.BitTier5000Count ?? 5000;
        set { if (Config != null) Config.BitTier5000Count = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("bit-tier-5000-scale")]
    public float BitTier5000Scale
    {
        get => Config?.BitTier5000Scale ?? 0.015f;
        set { if (Config != null) Config.BitTier5000Scale = value; }
    }

    [UIValue("bit-tier-5000-lifetime")]
    public float BitTier5000Lifetime
    {
        get => Config?.BitTier5000Lifetime ?? 1.5f;
        set { if (Config != null) Config.BitTier5000Lifetime = value; }
    }

    [UIValue("bit-tier-5000-speed")]
    public float BitTier5000Speed
    {
        get => Config?.BitTier5000Speed ?? 5f;
        set { if (Config != null) Config.BitTier5000Speed = value; }
    }

    [UIValue("bit-tier-1000-color")]
    public Color BitTier1000Color
    {
        get => Config?.BitTier1000Color ?? new Color(0.000f, 0.925f, 0.396f);
        set { if (Config != null) Config.BitTier1000Color = value; }
    }

    [UIValue("bit-tier-1000-count")]
    public int BitTier1000Count
    {
        get => Config?.BitTier1000Count ?? 2000;
        set { if (Config != null) Config.BitTier1000Count = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("bit-tier-1000-scale")]
    public float BitTier1000Scale
    {
        get => Config?.BitTier1000Scale ?? 0.015f;
        set { if (Config != null) Config.BitTier1000Scale = value; }
    }

    [UIValue("bit-tier-1000-lifetime")]
    public float BitTier1000Lifetime
    {
        get => Config?.BitTier1000Lifetime ?? 1.5f;
        set { if (Config != null) Config.BitTier1000Lifetime = value; }
    }

    [UIValue("bit-tier-1000-speed")]
    public float BitTier1000Speed
    {
        get => Config?.BitTier1000Speed ?? 5f;
        set { if (Config != null) Config.BitTier1000Speed = value; }
    }

    [UIValue("bit-tier-100-color")]
    public Color BitTier100Color
    {
        get => Config?.BitTier100Color ?? new Color(0.569f, 0.278f, 1.000f);
        set { if (Config != null) Config.BitTier100Color = value; }
    }

    [UIValue("bit-tier-100-count")]
    public int BitTier100Count
    {
        get => Config?.BitTier100Count ?? 500;
        set { if (Config != null) Config.BitTier100Count = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("bit-tier-100-scale")]
    public float BitTier100Scale
    {
        get => Config?.BitTier100Scale ?? 0.015f;
        set { if (Config != null) Config.BitTier100Scale = value; }
    }

    [UIValue("bit-tier-100-lifetime")]
    public float BitTier100Lifetime
    {
        get => Config?.BitTier100Lifetime ?? 1.5f;
        set { if (Config != null) Config.BitTier100Lifetime = value; }
    }

    [UIValue("bit-tier-100-speed")]
    public float BitTier100Speed
    {
        get => Config?.BitTier100Speed ?? 5f;
        set { if (Config != null) Config.BitTier100Speed = value; }
    }

    [UIValue("bit-tier-default-color")]
    public Color BitTierDefaultColor
    {
        get => Config?.BitTierDefaultColor ?? new Color(0.592f, 0.612f, 0.624f);
        set { if (Config != null) Config.BitTierDefaultColor = value; }
    }

    [UIValue("bit-tier-default-count")]
    public int BitTierDefaultCount
    {
        get => Config?.BitTierDefaultCount ?? 200;
        set { if (Config != null) Config.BitTierDefaultCount = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("bit-tier-default-scale")]
    public float BitTierDefaultScale
    {
        get => Config?.BitTierDefaultScale ?? 0.015f;
        set { if (Config != null) Config.BitTierDefaultScale = value; }
    }

    [UIValue("bit-tier-default-lifetime")]
    public float BitTierDefaultLifetime
    {
        get => Config?.BitTierDefaultLifetime ?? 1.5f;
        set { if (Config != null) Config.BitTierDefaultLifetime = value; }
    }

    [UIValue("bit-tier-default-speed")]
    public float BitTierDefaultSpeed
    {
        get => Config?.BitTierDefaultSpeed ?? 5f;
        set { if (Config != null) Config.BitTierDefaultSpeed = value; }
    }

    [UIValue("sub-timer-duration")]
    public float SubTimerDuration
    {
        get => Config?.SubTimerDuration ?? 10f;
        set { if (Config != null) Config.SubTimerDuration = value; }
    }

    [UIValue("sub-text-size")]
    public float SubTextSize
    {
        get => Config?.SubTextSize ?? 10f;
        set { if (Config != null) Config.SubTextSize = Mathf.Clamp(value, 0.5f, 40f); }
    }

    [UIValue("sub-trail-enabled")]
    public bool SubTrailEnabled
    {
        get => Config?.SubTrailEnabled ?? true;
        set { if (Config != null) Config.SubTrailEnabled = value; }
    }

    [UIValue("sub-particle-color")]
    public Color SubParticleColor
    {
        get => Config?.SubParticleColor ?? new Color(1f, 0.412f, 0.706f, 1f);
        set { if (Config != null) Config.SubParticleColor = value; }
    }

    [UIValue("sub-particle-count")]
public int SubParticleCount
    {
        get => Config?.SubParticleCount ?? 200;
        set { if (Config != null) Config.SubParticleCount = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("sub-particle-scale")]
    public float SubParticleScale
    {
        get => Config?.SubParticleScale ?? 0.015f;
        set { if (Config != null) Config.SubParticleScale = value; }
    }

    [UIValue("sub-particle-lifetime")]
    public float SubParticleLifetime
    {
        get => Config?.SubParticleLifetime ?? 1.5f;
        set { if (Config != null) Config.SubParticleLifetime = value; }
    }

    [UIValue("sub-particle-speed")]
    public float SubParticleSpeed
    {
        get => Config?.SubParticleSpeed ?? 5f;
        set { if (Config != null) Config.SubParticleSpeed = value; }
    }

    [UIValue("raid-timer-duration")]
    public float RaidTimerDuration
    {
        get => Config?.RaidTimerDuration ?? 10f;
        set { if (Config != null) Config.RaidTimerDuration = value; }
    }

    [UIValue("raid-multiplier")]
    public int RaidMultiplier
    {
        get => Config?.RaidMultiplier ?? 1;
        set { if (Config != null) Config.RaidMultiplier = value; }
    }

    [UIValue("raid-text-size")]
    public float RaidTextSize
    {
        get => Config?.RaidTextSize ?? 8f;
        set { if (Config != null) Config.RaidTextSize = Mathf.Clamp(value, 0.5f, 40f); }
    }

    [UIValue("note-outline-enabled")]
    public bool NoteOutlineEnabled
    {
        get => Config?.NoteOutlineEnabled ?? true;
        set { if (Config != null) Config.NoteOutlineEnabled = value; }
    }

    [UIValue("note-outline-size")]
    public float NoteOutlineSize
    {
        get => Config?.NoteOutlineSize ?? 0.02f;
        set { if (Config != null) Config.NoteOutlineSize = Mathf.Clamp(value, 0.001f, 0.1f); }
    }

    [UIValue("note-outline-density")]
    public int NoteOutlineDensity
    {
        get => Config?.NoteOutlineDensity ?? 200;
        set { if (Config != null) Config.NoteOutlineDensity = Mathf.Clamp(value, 10, 2000); }
    }

    [UIValue("note-outline-opacity")]
    public float NoteOutlineOpacity
    {
        get => Config?.NoteOutlineOpacity ?? 1f;
        set { if (Config != null) Config.NoteOutlineOpacity = Mathf.Clamp01(value); }
    }

    [UIValue("note-outline-scale")]
    public float NoteOutlineScale
    {
        get => Config?.NoteOutlineScale ?? 1f;
        set { if (Config != null) Config.NoteOutlineScale = Mathf.Clamp(value, 0.2f, 3f); }
    }

    [UIValue("pause-on-noodle-maps")]
    public bool PauseOnNoodleMaps
    {
        get => Config?.PauseOnNoodleMaps ?? false;
        set { if (Config != null) Config.PauseOnNoodleMaps = value; }
    }

    [UIValue("pause-on-vivify-maps")]
    public bool PauseOnVivifyMaps
    {
        get => Config?.PauseOnVivifyMaps ?? false;
        set { if (Config != null) Config.PauseOnVivifyMaps = value; }
    }

    [UIValue("pause-on-wip-maps")]
    public bool PauseOnWipMaps
    {
        get => Config?.PauseOnWipMaps ?? true;
        set { if (Config != null) Config.PauseOnWipMaps = value; }
    }

    [UIValue("pause-on-ranked-maps")]
    public bool PauseOnRankedMaps
    {
        get => Config?.PauseOnRankedMaps ?? false;
        set { if (Config != null) Config.PauseOnRankedMaps = value; }
    }

    [UIValue("flashbang-duration")]
    public float FlashbangDuration
    {
        get => Config?.FlashbangDuration ?? 4f;
        set { if (Config != null) Config.FlashbangDuration = Mathf.Clamp(value, 0.5f, 10f); }
    }

    [UIValue("flashbang-opacity")]
    public float FlashbangOpacity
    {
        get => Config?.FlashbangOpacity ?? 100f;
        set { if (Config != null) Config.FlashbangOpacity = Mathf.Clamp(value, 0f, 100f); }
    }

    [UIValue("flashbang-fadeout")]
    public float FlashbangFadeOut
    {
        get => Config?.FlashbangFadeOut ?? 1.5f;
        set
        {
            if (Config != null)
                Config.FlashbangFadeOut = Mathf.Clamp(value, 0f, Mathf.Max(0f, Config.FlashbangDuration));
        }
    }

    [UIValue("flashbang-viewer-text")]
    public string FlashbangViewerText
    {
        get => Config?.FlashbangViewerText ?? "streamer was blinded";
        set { if (Config != null) Config.FlashbangViewerText = value ?? ""; }
    }

    [UIValue("flashbang-viewer-text-size")]
    public float FlashbangViewerTextSize
    {
        get => Config?.FlashbangViewerTextSize ?? 2.5f;
        set { if (Config != null) Config.FlashbangViewerTextSize = Mathf.Clamp(value, 0.5f, 10f); }
    }

    [UIValue("flashbang-viewer-text-color")]
    public Color FlashbangViewerTextColor
    {
        get => Config?.FlashbangViewerTextColor ?? Color.white;
        set { if (Config != null) Config.FlashbangViewerTextColor = value; }
    }

    [UIValue("projection-duration")]
    public float ProjectionDuration
    {
        get => Config?.ProjectionDuration ?? 8f;
        set { if (Config != null) Config.ProjectionDuration = Mathf.Clamp(value, 0.5f, 30f); }
    }

    [UIValue("projection-fade-in")]
    public float ProjectionFadeIn
    {
        get => Config?.ProjectionFadeIn ?? 1f;
        set { if (Config != null) Config.ProjectionFadeIn = Mathf.Clamp(value, 0f, 5f); }
    }

    [UIValue("projection-fade-out")]
    public float ProjectionFadeOut
    {
        get => Config?.ProjectionFadeOut ?? 2f;
        set { if (Config != null) Config.ProjectionFadeOut = Mathf.Clamp(value, 0f, 8f); }
    }

    [UIValue("projection-size")]
    public float ProjectionSize
    {
        get => Config?.ProjectionSize ?? 3f;
        set { if (Config != null) Config.ProjectionSize = Mathf.Clamp(value, 0.5f, 10f); }
    }

    [UIValue("projection-distance")]
    public float ProjectionDistance
    {
        get => Config?.ProjectionDistance ?? 4f;
        set { if (Config != null) Config.ProjectionDistance = Mathf.Clamp(value, 1f, 15f); }
    }

    [UIValue("projection-particle-size")]
    public float ProjectionParticleSize
    {
        get => Config?.ProjectionParticleSize ?? 0.03f;
        set { if (Config != null) Config.ProjectionParticleSize = Mathf.Clamp(value, 0.005f, 0.1f); }
    }

    [UIValue("projection-particle-count")]
    public int ProjectionParticleCount
    {
        get => Config?.ProjectionParticleCount ?? 3000;
        set { if (Config != null) Config.ProjectionParticleCount = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("projection-color")]
    public Color ProjectionColor
    {
        get => Config?.ProjectionColor ?? Color.white;
        set { if (Config != null) Config.ProjectionColor = value; }
    }

    [UIValue("raid-particle-color")]
    public Color RaidParticleColor
    {
        get => Config?.RaidParticleColor ?? new Color(1f, 0.27f, 0f, 1f);
        set { if (Config != null) Config.RaidParticleColor = value; }
    }

    [UIValue("raid-particle-count")]
    public int RaidParticleCount
    {
        get => Config?.RaidParticleCount ?? 150;
        set { if (Config != null) Config.RaidParticleCount = Mathf.Clamp(value, 0, 50000); }
    }

    [UIValue("raid-particle-scale")]
    public float RaidParticleScale
    {
        get => Config?.RaidParticleScale ?? 0.015f;
        set { if (Config != null) Config.RaidParticleScale = value; }
    }

    [UIValue("raid-particle-lifetime")]
    public float RaidParticleLifetime
    {
        get => Config?.RaidParticleLifetime ?? 1.5f;
        set { if (Config != null) Config.RaidParticleLifetime = value; }
    }

    [UIValue("raid-particle-speed")]
    public float RaidParticleSpeed
    {
        get => Config?.RaidParticleSpeed ?? 5f;
        set { if (Config != null) Config.RaidParticleSpeed = value; }
    }

    [UIValue("capsule-guard-enabled")]
    public bool CapsuleGuardEnabled
    {
        get => Config?.CapsuleGuardEnabled ?? true;
        set { if (Config != null) Config.CapsuleGuardEnabled = value; }
    }

    [UIValue("capsule-attach-mode-options")]
    public List<object> CapsuleAttachModeOptions { get; } = new() { "Headset", "Avatar Bone", "Platform Center" };

    [UIValue("capsule-attach-mode")]
    public string CapsuleAttachMode
    {
        get => Config?.CapsuleAttachMode ?? "Headset";
        set
        {
            if (Config != null)
            {
                Config.CapsuleAttachMode = value;
                UpdateConditionalVisibility();
            }
        }
    }

    [UIValue("capsule-bone-path")]
    public string CapsuleBonePath
    {
        get => Config?.CapsuleBonePath ?? "Head";
        set { if (Config != null) Config.CapsuleBonePath = value; }
    }

    [UIValue("capsule-height")]
    public float CapsuleHeight
    {
        get => Config?.CapsuleHeight ?? 1.8f;
        set { if (Config != null) Config.CapsuleHeight = Mathf.Clamp(value, 0.2f, 4f); }
    }

    [UIValue("capsule-width")]
    public float CapsuleWidth
    {
        get => Config?.CapsuleWidth ?? 0.4f;
        set { if (Config != null) Config.CapsuleWidth = Mathf.Clamp(value, 0.05f, 2f); }
    }

    [UIValue("capsule-vertical-offset")]
    public float CapsuleVerticalOffset
    {
        get => Config?.CapsuleVerticalOffset ?? 0f;
        set { if (Config != null) Config.CapsuleVerticalOffset = Mathf.Clamp(value, -3f, 3f); }
    }

    [UIValue("capsule-show-visual")]
    public bool CapsuleShowVisual
    {
        get => Config?.CapsuleShowVisual ?? true;
        set { if (Config != null) Config.CapsuleShowVisual = value; }
    }

    [UIValue("throw-origin-mode-options")]
    public List<object> ThrowOriginModeOptions { get; } = new() { "Twin Cannons", "Random Around", "Front Center", "Horizon Front" };

    [UIValue("throw-trajectory-options")]
    public List<object> ThrowTrajectoryOptions { get; } = new() { "Lofted", "Zero-G" };

    [UIValue("throw-origin-mode")]
    public string ThrowOriginMode
    {
        get => Config?.ThrowOriginMode ?? "Twin Cannons";
        set { if (Config != null) Config.ThrowOriginMode = value; }
    }

    [UIValue("throw-note-scale")]
    public float ThrowNoteScale
    {
        get => Config?.ThrowNoteScale ?? 1f;
        set { if (Config != null) Config.ThrowNoteScale = Mathf.Clamp(value, 0.2f, 3f); }
    }

    [UIValue("throw-match-collision-scale")]
    public bool ThrowMatchCollisionScale
    {
        get => Config?.ThrowMatchCollisionScale ?? false;
        set { if (Config != null) Config.ThrowMatchCollisionScale = value; }
    }

    [UIValue("throw-speed")]
    public float ThrowSpeed
    {
        get => Config?.ThrowSpeed ?? 11f;
        set { if (Config != null) Config.ThrowSpeed = Mathf.Clamp(value, 3f, 30f); }
    }

    [UIValue("throw-gravity")]
    public float ThrowGravity
    {
        get => Config?.ThrowGravity ?? 4.5f;
        set { if (Config != null) Config.ThrowGravity = Mathf.Clamp(value, 0f, 20f); }
    }

    [UIValue("throw-floor-width")]
    public float ThrowFloorWidth
    {
        get => Config?.ThrowFloorWidth ?? 3f;
        set { if (Config != null) Config.ThrowFloorWidth = Mathf.Clamp(value, 1f, 20f); }
    }

    [UIValue("throw-floor-depth")]
    public float ThrowFloorDepth
    {
        get => Config?.ThrowFloorDepth ?? 3f;
        set { if (Config != null) Config.ThrowFloorDepth = Mathf.Clamp(value, 1f, 20f); }
    }

    [UIValue("throw-bounciness")]
    public float ThrowBounciness
    {
        get => Config?.ThrowBounciness ?? 0.45f;
        set { if (Config != null) Config.ThrowBounciness = Mathf.Clamp(value, 0f, 0.95f); }
    }

    [UIValue("throw-capsule-bounciness")]
    public float ThrowCapsuleBounciness
    {
        get => Config?.ThrowCapsuleBounciness ?? 0.45f;
        set { if (Config != null) Config.ThrowCapsuleBounciness = Mathf.Clamp(value, 0f, 0.95f); }
    }

    [UIValue("throw-trajectory")]
    public string ThrowTrajectory
    {
        get => Config?.ThrowTrajectory ?? "Lofted";
        set
        {
            if (Config != null)
            {
                Config.ThrowTrajectory = value;
                UpdateConditionalVisibility();
            }
        }
    }

    [UIValue("throw-air-time")]
    public float ThrowAirTime
    {
        get => Config?.ThrowAirTime ?? 1.1f;
        set { if (Config != null) Config.ThrowAirTime = Mathf.Clamp(value, 0.4f, 20f); }
    }

    [UIValue("throw-floor-friction")]
    public float ThrowFloorFriction
    {
        get => Config?.ThrowFloorFriction ?? 5f;
        set { if (Config != null) Config.ThrowFloorFriction = Mathf.Clamp(value, 0f, 30f); }
    }

    [UIValue("throw-max-projectiles")]
    public float ThrowMaxProjectiles
    {
        get => Config?.ThrowMaxProjectiles ?? 12f;
        set { if (Config != null) Config.ThrowMaxProjectiles = Mathf.Clamp(value, 1f, 200f); }
    }

    [UIValue("throw-fade-seconds")]
    public float ThrowFadeSeconds
    {
        get => Config?.ThrowFadeSeconds ?? 0.5f;
        set { if (Config != null) Config.ThrowFadeSeconds = Mathf.Clamp(value, 0f, 3f); }
    }

    [UIValue("throw-lifetime")]
    public float ThrowLifetime
    {
        get => Config?.ThrowLifetime ?? 6f;
        set { if (Config != null) Config.ThrowLifetime = Mathf.Clamp(value, 1f, 120f); }
    }

    [UIValue("combo-throw-enabled")]
    public bool ComboThrowEnabled
    {
        get => Config?.ComboThrowEnabled ?? false;
        set { if (Config != null) Config.ComboThrowEnabled = value; }
    }

    [UIValue("combo-throw-threshold")]
    public int ComboThrowThreshold
    {
        get => Config?.ComboThrowThreshold ?? 10;
        set { if (Config != null) Config.ComboThrowThreshold = Mathf.Clamp(value, 0, 100); }
    }

    [UIValue("throw-explode-enabled")]
    public bool ThrowExplodeEnabled
    {
        get => Config?.ThrowExplodeEnabled ?? false;
        set
        {
            if (Config != null)
            {
                Config.ThrowExplodeEnabled = value;
                UpdateConditionalVisibility();
            }
        }
    }

    [UIValue("throw-explode-count")]
    public int ThrowExplodeCount
    {
        get => Config?.ThrowExplodeCount ?? 14;
        set { if (Config != null) Config.ThrowExplodeCount = Mathf.Clamp(value, 0, 200); }
    }

    [UIValue("throw-explode-speed")]
    public float ThrowExplodeSpeed
    {
        get => Config?.ThrowExplodeSpeed ?? 0.5f;
        set { if (Config != null) Config.ThrowExplodeSpeed = Mathf.Clamp(value, 0.05f, 5f); }
    }

    [UIValue("throw-explode-scale")]
    public float ThrowExplodeScale
    {
        get => Config?.ThrowExplodeScale ?? 0.005f;
        set { if (Config != null) Config.ThrowExplodeScale = Mathf.Clamp(value, 0.005f, 0.1f); }
    }

    [UIValue("throw-explode-lifetime")]
    public float ThrowExplodeLifetime
    {
        get => Config?.ThrowExplodeLifetime ?? 0.6f;
        set { if (Config != null) Config.ThrowExplodeLifetime = Mathf.Clamp(value, 0.1f, 3f); }
    }

    [UIValue("throw-explode-use-note-color")]
    public bool ThrowExplodeUseNoteColor
    {
        get => Config?.ThrowExplodeUseNoteColor ?? true;
        set
        {
            if (Config != null)
            {
                Config.ThrowExplodeUseNoteColor = value;
                UpdateConditionalVisibility();
            }
        }
    }

    [UIValue("throw-explode-color")]
    public Color ThrowExplodeColor
    {
        get => Config?.ThrowExplodeColor ?? Color.white;
        set { if (Config != null) Config.ThrowExplodeColor = value; }
    }

    [UIValue("bomb-text-line-spacing")]
    public float BombTextLineSpacing
    {
        get => Config?.BombTextLineSpacing ?? 0f;
        set { if (Config != null) Config.BombTextLineSpacing = Mathf.Clamp(value, -30f, 100f); }
    }

    [UIValue("max-text-size")]
    public float MaxTextSize
    {
        get => Config?.MaxTextSize ?? 0f;
        set { if (Config != null) Config.MaxTextSize = Mathf.Clamp(value, 0f, 60f); }
    }

    [UIValue("max-message-words")]
    public int MaxMessageWords
    {
        get => Config?.MaxMessageWords ?? 0;
        set { if (Config != null) Config.MaxMessageWords = Mathf.Clamp(value, 0, 200); }
    }

    [UIValue("twitch-channel-name")]
    public string TwitchChannelName
    {
        get => Config?.TwitchChannelName ?? "";
        set
        {
            if (Config == null) return;
            var old = Config.TwitchChannelName;
            Config.TwitchChannelName = value ?? "";
            if (!string.Equals(old, Config.TwitchChannelName, System.StringComparison.OrdinalIgnoreCase))
                Plugin.Instance?.StartEmoteSystem();
        }
    }

    [UIValue("chat-enabled")]
    public bool ChatEnabled
    {
        get => Config?.ChatEnabled ?? false;
        set { if (Config != null) Config.ChatEnabled = value; }
    }

    [UIValue("chat-width")]
    public float ChatWidth
    {
        get => Config?.ChatWidth ?? 40f;
        set { if (Config != null) Config.ChatWidth = Mathf.Clamp(value, 20f, 120f); }
    }

    [UIValue("chat-height")]
    public float ChatHeight
    {
        get => Config?.ChatHeight ?? 70f;
        set { if (Config != null) Config.ChatHeight = Mathf.Clamp(value, 20f, 160f); }
    }

    [UIValue("chat-font-size")]
    public float ChatFontSize
    {
        get => Config?.ChatFontSize ?? 2.5f;
        set { if (Config != null) Config.ChatFontSize = Mathf.Clamp(value, 1f, 10f); }
    }

    [UIValue("chat-name-color")]
    public Color ChatNameColor
    {
        get => Config?.ChatNameColor ?? new Color(0.396f, 0.792f, 1f);
        set { if (Config != null) Config.ChatNameColor = value; }
    }

    [UIValue("chat-text-color")]
    public Color ChatTextColor
    {
        get => Config?.ChatTextColor ?? Color.white;
        set { if (Config != null) Config.ChatTextColor = value; }
    }

    [UIValue("chat-force-name-color")]
    public bool ChatForceNameColor
    {
        get => Config?.ChatForceNameColor ?? false;
        set { if (Config != null) Config.ChatForceNameColor = value; }
    }

    [UIValue("emote-throw-enabled")]
    public bool EmoteThrowEnabled
    {
        get => Config?.EmoteThrowEnabled ?? true;
        set
        {
            if (Config == null) return;
            var old = Config.EmoteThrowEnabled;
            Config.EmoteThrowEnabled = value;
            if (old != value)
                Plugin.Instance?.StartEmoteSystem();
        }
    }

    [UIValue("emote-throw-size")]
    public float EmoteThrowSize
    {
        get => Config?.EmoteThrowSize ?? 0.5f;
        set { if (Config != null) Config.EmoteThrowSize = Mathf.Clamp(value, 0.1f, 2f); }
    }

    [UIValue("emote-rain-size")]
    public float EmoteRainSize
    {
        get => Config?.EmoteRainSize ?? 0.4f;
        set { if (Config != null) Config.EmoteRainSize = Mathf.Clamp(value, 0.1f, 2f); }
    }

    [UIValue("emote-rain-enabled")]
    public bool EmoteRainEnabled
    {
        get => Config?.EmoteRainEnabled ?? true;
        set
        {
            if (Config == null) return;
            var old = Config.EmoteRainEnabled;
            Config.EmoteRainEnabled = value;
            if (old != value)
                Plugin.Instance?.StartEmoteSystem();
        }
    }

    [UIValue("emote-rain-intensity")]
    public int EmoteRainIntensity
    {
        get => Config?.EmoteRainIntensity ?? 5;
        set { if (Config != null) Config.EmoteRainIntensity = Mathf.Clamp(value, 1, 50); }
    }

    [UIValue("emote-cache-mb")]
    public int EmoteCacheMB
    {
        get => Config?.EmoteCacheMB ?? 64;
        set { if (Config != null) Config.EmoteCacheMB = Mathf.Clamp(value, 8, 512); }
    }

    [UIValue("emoji-support-enabled")]
    public bool EmojiSupportEnabled
    {
        get => Config?.EmojiSupportEnabled ?? false;
        set { if (Config != null) Config.EmojiSupportEnabled = value; }
    }

    [UIValue("rain-zone-around-player")]
    public bool RainZoneAroundPlayer
    {
        get => Config?.RainZoneAroundPlayer ?? true;
        set { if (Config != null) Config.RainZoneAroundPlayer = value; }
    }

    [UIValue("rain-zone-front-center")]
    public bool RainZoneFrontCenter
    {
        get => Config?.RainZoneFrontCenter ?? false;
        set { if (Config != null) Config.RainZoneFrontCenter = value; }
    }

    [UIValue("rain-zone-front-left")]
    public bool RainZoneFrontLeft
    {
        get => Config?.RainZoneFrontLeft ?? false;
        set { if (Config != null) Config.RainZoneFrontLeft = value; }
    }

    [UIValue("rain-zone-front-right")]
    public bool RainZoneFrontRight
    {
        get => Config?.RainZoneFrontRight ?? false;
        set { if (Config != null) Config.RainZoneFrontRight = value; }
    }

    [UIValue("emote-rain-fall-speed")]
    public float EmoteRainFallSpeed
    {
        get => Config?.EmoteRainFallSpeed ?? 2f;
        set { if (Config != null) Config.EmoteRainFallSpeed = value; }
    }

    [UIValue("emote-rain-lifetime")]
    public int EmoteRainLifetime
    {
        get => Config?.EmoteRainLifetime ?? 5;
        set { if (Config != null) Config.EmoteRainLifetime = value; }
    }

    [UIValue("emote-rain-bounce")]
    public bool EmoteRainBounce
    {
        get => Config?.EmoteRainBounce ?? true;
        set { if (Config != null) Config.EmoteRainBounce = value; }
    }

    [UIValue("emote-rain-preview")]
    public bool EmoteRainPreview
    {
        get => Config?.EmoteRainPreview ?? false;
        set
        {
            if (Config != null) Config.EmoteRainPreview = value;
            global::StreamReactive.EmoteRainPreview.SetVisible(value);
        }
    }



    [UIValue("about-description")]
    public string AboutDescription =>
        "Reacts to stream events, simple. " +
        "Connect your favorite bot (NoBot, Streamer.bot, Firebot, etc) to the websocket and send any events you want. " +
        "Read the docs to find what events are available.\n" +
        "The classic !bomb command, bit events, subscribe events or anything else offered by the mod.\n" +
        "- FEFELAND";

    [UIValue("watermark-text")]
    public string WatermarkText
    {
        get
        {
            var v = typeof(StreamReactiveSettingsViewController).Assembly.GetName().Version;
            var vs = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
            return $"Version: {vs} | Vibe-coded mod";
        }
    }
}

using System.Collections.Generic;
using IPA.Config.Stores;
using IPA.Config.Stores.Attributes;
using UnityEngine;

namespace StreamReactive;

public enum BombColorMode
{
    Static,
    Random,
    Rainbow
}

public class PluginConfig
{
    internal static PluginConfig? Instance { get; set; }

    public virtual bool Enabled { get; set; } = true;
    // Paused: mod stays enabled and keeps accepting events into the queue, but
    // nothing plays until unpaused. Toggled by the General settings "Paused"
    // switch and the pause/unpause websocket commands.
    public virtual bool Paused { get; set; } = false;
    public virtual int WebSocketPort { get; set; } = 41243;
    public virtual bool IncludeBombNotes { get; set; } = false;
    public virtual bool VerboseLogging { get; set; } = false;
    // First-time disclaimer: shown once on the settings panel until acknowledged.
    public virtual bool ClickedDisclaimer { get; set; } = false;

    // Emotes
    public virtual string TwitchChannelName { get; set; } = "";
    // Cap on decoded emote texture memory (megabytes). Older, unused emotes are
    // freed once this is exceeded. (Active on-screen emotes are always kept.)
    public virtual int EmoteCacheMB { get; set; } = 64;
    public virtual bool EmoteThrowEnabled { get; set; } = false;
    public virtual bool EmojiSupportEnabled { get; set; } = false;
    public virtual float EmoteThrowSize { get; set; } = 0.5f;
    public virtual float EmoteRainSize { get; set; } = 0.4f;
    public virtual bool EmoteRainEnabled { get; set; } = false;
    public virtual int EmoteRainIntensity { get; set; } = 5;

    // Rain spawn zones (player-anchored, not camera-relative). Each rain drop
    // picks a random enabled zone. Enable one or many.
    public virtual bool RainZoneAroundPlayer { get; set; } = true;
    public virtual bool RainZoneFrontCenter { get; set; } = false;
    public virtual bool RainZoneFrontLeft { get; set; } = false;
    public virtual bool RainZoneFrontRight { get; set; } = false;
    // Vertical fall speed of rain emotes (world units / second).
    public virtual float EmoteRainFallSpeed { get; set; } = 2f;
    // How long a rain emote lives before despawning (seconds).
    public virtual int EmoteRainLifetime { get; set; } = 5;
    // Rain emotes bounce off the floor instead of passing through it.
    public virtual bool EmoteRainBounce { get; set; } = true;
    public virtual bool EmoteRainPreview { get; set; } = false;

    // Text sizes are stored as familiar "font points" (12 = classic bomb look).
    // Divide by FontPointsPerUnit before handing to TMP fontSize.
    public const float FontPointsPerUnit = 4f;

    // Bomb
    public virtual bool BombCosmeticEnabled { get; set; } = true;
    public virtual float BombTextSize { get; set; } = 12f;
    public virtual float BombTextLifetime { get; set; } = 3f;
    // When enabled, strips rich-text modifiers (<color>, <size>, <br>, etc.)
    // out of viewer bomb messages so viewers can't control text styling.
    public virtual bool BombTextStripTags { get; set; } = false;
    public virtual int BombParticleCount { get; set; } = 10000;
    public virtual float BombParticleScale { get; set; } = 0.015f;
    public virtual float BombParticleLifetime { get; set; } = 1.5f;
    public virtual float BombParticleSpeed { get; set; } = 5f;
    [UseConverter(typeof(BombColorModeConverter))]
    public virtual BombColorMode BombColorMode { get; set; } = BombColorMode.Random;
    public virtual float BombRainbowSpeed { get; set; } = 0.7f;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color BombParticleColor { get; set; } = new Color(1f, 0.5f, 0f, 1f);
    // HDR brightness multiplier for the bomb glow. Boosts color into HDR range
    // so higher values bloom more. 0 dims the bomb to invisible-level flatness.
    public virtual float BombGlowBrightness { get; set; } = 3f;

    // Bits global
    public virtual bool TextSpawnEveryEvent { get; set; } = true;
    public virtual float BitsTextSize { get; set; } = 8f;
    public virtual float BitsTextLifetime { get; set; } = 3f;

    // Bit tiers: ≥10000
    [UseConverter(typeof(ColorConverter))]
    public virtual Color BitTier10000Color { get; set; } = new Color(0.913f, 0.098f, 0.086f);
    public virtual int BitTier10000Count { get; set; } = 20000;
    public virtual int BitTier10000BlockRatio { get; set; } = 1;
    public virtual float BitTier10000Scale { get; set; } = 0.015f;
    public virtual float BitTier10000Lifetime { get; set; } = 1.5f;
    public virtual float BitTier10000Speed { get; set; } = 7f;

    // Bit tiers: ≥5000
    [UseConverter(typeof(ColorConverter))]
    public virtual Color BitTier5000Color { get; set; } = new Color(0.125f, 0.635f, 0.969f);
    public virtual int BitTier5000Count { get; set; } = 5000;
    public virtual int BitTier5000BlockRatio { get; set; } = 1;
    public virtual float BitTier5000Scale { get; set; } = 0.015f;
    public virtual float BitTier5000Lifetime { get; set; } = 1.5f;
    public virtual float BitTier5000Speed { get; set; } = 5f;

    // Bit tiers: ≥1000
    [UseConverter(typeof(ColorConverter))]
    public virtual Color BitTier1000Color { get; set; } = new Color(0.000f, 0.925f, 0.396f);
    public virtual int BitTier1000Count { get; set; } = 2000;
    public virtual int BitTier1000BlockRatio { get; set; } = 1;
    public virtual float BitTier1000Scale { get; set; } = 0.015f;
    public virtual float BitTier1000Lifetime { get; set; } = 1f;
    public virtual float BitTier1000Speed { get; set; } = 3f;

    // Bit tiers: ≥100
    [UseConverter(typeof(ColorConverter))]
    public virtual Color BitTier100Color { get; set; } = new Color(0.569f, 0.278f, 1.000f);
    public virtual int BitTier100Count { get; set; } = 500;
    public virtual int BitTier100BlockRatio { get; set; } = 1;
    public virtual float BitTier100Scale { get; set; } = 0.015f;
    public virtual float BitTier100Lifetime { get; set; } = 1f;
    public virtual float BitTier100Speed { get; set; } = 1.5f;

    // Bit tiers: <100
    [UseConverter(typeof(ColorConverter))]
    public virtual Color BitTierDefaultColor { get; set; } = new Color(0.592f, 0.612f, 0.624f);
    public virtual int BitTierDefaultCount { get; set; } = 200;
    public virtual int BitTierDefaultBlockRatio { get; set; } = 1;
    public virtual float BitTierDefaultScale { get; set; } = 0.015f;
    public virtual float BitTierDefaultLifetime { get; set; } = 1f;
    public virtual float BitTierDefaultSpeed { get; set; } = 0.5f;

    // Sub
    public virtual float SubTimerDuration { get; set; } = 20f;
    public virtual float SubTextSize { get; set; } = 10f;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color SubParticleColor { get; set; } = new Color(1f, 0.412f, 0.706f, 1f);
    public virtual int SubParticleCount { get; set; } = 200;
    public virtual float SubParticleScale { get; set; } = 0.015f;
    public virtual float SubParticleLifetime { get; set; } = 1.5f;
    public virtual float SubParticleSpeed { get; set; } = 5f;
    public virtual bool SubTrailEnabled { get; set; } = true;

    // Raid
    public virtual int RaidMultiplier { get; set; } = 1;
    public virtual float RaidTimerDuration { get; set; } = 20f;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color RaidParticleColor { get; set; } = new Color(0.902f, 0.439f, 0.263f, 1f);
    public virtual float RaidTextSize { get; set; } = 8f;
    public virtual int RaidParticleCount { get; set; } = 150;
    public virtual float RaidParticleScale { get; set; } = 0.015f;
    public virtual float RaidParticleLifetime { get; set; } = 1f;
    public virtual float RaidParticleSpeed { get; set; } = 3f;

    // Note outline: the particle "skeleton" that hugs the shape of claimed
    // notes during bit/sub/raid events. Shared across all three event types.
    public virtual bool NoteOutlineEnabled { get; set; } = true;
    public virtual float NoteOutlineSize { get; set; } = 0.02f;
    public virtual int NoteOutlineDensity { get; set; } = 200;
    public virtual float NoteOutlineOpacity { get; set; } = 1f;
    // Multiplier to compensate for player NoteScale (notes drawn smaller/larger
    // than the 100% default). 1 = default sizing, sized for full-size blocks.
    public virtual float NoteOutlineScale { get; set; } = 1f;

    // Per-map protections (all off by default). When enabled, all StreamReactive
    // events are paused while the currently-playing map uses NoodleExtensions or
    // Vivify, or is a Work-in-Progress (CustomWIPLevels) map. These just gate
    // event dispatch to avoid clashing with mod-heavy / unfinished maps.
    public virtual bool PauseOnNoodleMaps { get; set; } = false;
    public virtual bool PauseOnVivifyMaps { get; set; } = true;
    public virtual bool PauseOnWipMaps { get; set; } = true;
    // When enabled, all StreamReactive events are paused while the currently
    // playing map is ranked on BeatLeader, ScoreSaber, or both. Ranked status is
    // read from the SongDetailsCache mod's local database (best-effort; see
    // Plugin.RefreshCurrentMapInfo).
    public virtual bool PauseOnRankedMaps { get; set; } = false;

    // Guard capsule (projectile bounce target)
    public virtual bool CapsuleGuardEnabled { get; set; } = true;
    public virtual string CapsuleAttachMode { get; set; } = "Headset";
    public virtual string CapsuleBonePath { get; set; } = "Spine/Chest/Neck/Head";
    public virtual float CapsuleHeight { get; set; } = 0.4f;
    public virtual float CapsuleWidth { get; set; } = 0.55f;
    public virtual float CapsuleVerticalOffset { get; set; } = -0.2f;
    public virtual bool CapsuleShowVisual { get; set; } = false;
    public virtual string ThrowOriginMode { get; set; } = "Twin Cannons";
    public virtual string ThrowTrajectory { get; set; } = "Lofted";
    // Multiplier on the thrown note's size relative to the game's 100% note
    // scale. 1 = the projectile matches a full-size note; lower makes it
    // visually smaller than a regular note (the old fixed 0.55x behavior).
    public virtual float ThrowNoteScale { get; set; } = 1f;
    // When true, capsule/floor collision uses the projectile's actual scaled
    // size instead of the fixed small cube radius, so a huge note bounces off
    // the capsule from far away (matching what the player sees).
    public virtual bool ThrowMatchCollisionScale { get; set; } = false;
    public virtual float ThrowAirTime { get; set; } = 1.8f;
    public virtual float ThrowSpeed { get; set; } = 10f;
    public virtual float ThrowGravity { get; set; } = 4f;
    public virtual float ThrowFloorWidth { get; set; } = 3f;

    public virtual float ThrowFloorDepth { get; set; } = 2f;

    public virtual float ThrowBounciness { get; set; } = 0.4f;

    public virtual float ThrowCapsuleBounciness { get; set; } = 0.1f;

    public virtual float ThrowFloorFriction { get; set; } = 1.5f;

    public virtual float ThrowMaxProjectiles { get; set; } = 50f;

    public virtual float ThrowFadeSeconds { get; set; } = 0.5f;

    public virtual float ThrowLifetime { get; set; } = 6f;

    // Combo Throw: when enabled, a block is auto-thrown at the player if their
    // combo breaks from at/above ComboThrowThreshold. Requires the guard capsule.
    public virtual bool ComboThrowEnabled { get; set; } = false;
    public virtual int ComboThrowThreshold { get; set; } = 10;

    // Throw projectile explosion: when a thrown block strikes the guard
    // capsule it bursts into a small particle puff. Off by default.
    public virtual bool ThrowExplodeEnabled { get; set; } = false;
    public virtual int ThrowExplodeCount { get; set; } = 50;
    public virtual float ThrowExplodeSpeed { get; set; } = 0.5f;
    public virtual float ThrowExplodeScale { get; set; } = 0.005f;
    public virtual float ThrowExplodeLifetime { get; set; } = 0.6f;
    public virtual bool ThrowExplodeUseNoteColor { get; set; } = true;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color ThrowExplodeColor { get; set; } = new Color(1f, 1f, 1f, 1f);

    public virtual float BombTextLineSpacing { get; set; } = 0f;

    // Caps <size=N> rich-text tags in viewer message text. 0 = no limit.
    public virtual float MaxTextSize { get; set; } = 0f;
    // Caps the number of words in viewer message text. Words are whitespace-
    // separated runs (rich-text tags don't count); trailing words are dropped.
    // 0 = no limit. A few very long words is valid.
    public virtual int MaxMessageWords { get; set; } = 0;

    // Flashbang
    public virtual float FlashbangDuration { get; set; } = 4f;
    public virtual float FlashbangOpacity { get; set; } = 100f;
    public virtual float FlashbangFadeOut { get; set; } = 1.5f;
    public virtual string FlashbangViewerText { get; set; } = "streamer was blinded";
    public virtual float FlashbangViewerTextSize { get; set; } = 2.5f;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color FlashbangViewerTextColor { get; set; } = Color.white;

    // Projection
    public virtual float ProjectionDuration { get; set; } = 8f;
    public virtual float ProjectionFadeIn { get; set; } = 1f;
    public virtual float ProjectionFadeOut { get; set; } = 2f;
    public virtual float ProjectionSize { get; set; } = 2.4f;
    public virtual float ProjectionDistance { get; set; } = 10f;
    public virtual float ProjectionParticleSize { get; set; } = 0.03f;
    public virtual int ProjectionParticleCount { get; set; } = 10000;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color ProjectionColor { get; set; } = Color.white;

    [UseConverter(typeof(ColorConverter))]
    public virtual Color BitsColor { get; set; } = new Color(1f, 0.843f, 0f);

    // Event sound effects: OGG files dropped in UserData/StreamReactive/Sounds.
    // Empty sound file = no sound for that event ("default: none").
    // All sounds play centered on both ears (2D).
    public virtual string BombSoundFile { get; set; } = "";
    public virtual float BombSoundVolume { get; set; } = 0.8f;
    public virtual string SubSoundFile { get; set; } = "";
    public virtual float SubSoundVolume { get; set; } = 0.8f;
    public virtual string RaidSoundFile { get; set; } = "";
    public virtual float RaidSoundVolume { get; set; } = 0.8f;

    // Bit tier sounds: each tier has its own sound, resolved from the cheer amount.
    public virtual string BitTier10000SoundFile { get; set; } = "";
    public virtual float BitTier10000SoundVolume { get; set; } = 0.8f;
    public virtual string BitTier5000SoundFile { get; set; } = "";
    public virtual float BitTier5000SoundVolume { get; set; } = 0.8f;
    public virtual string BitTier1000SoundFile { get; set; } = "";
    public virtual float BitTier1000SoundVolume { get; set; } = 0.8f;
    public virtual string BitTier100SoundFile { get; set; } = "";
    public virtual float BitTier100SoundVolume { get; set; } = 0.8f;
    public virtual string BitTierDefaultSoundFile { get; set; } = "";
    public virtual float BitTierDefaultSoundVolume { get; set; } = 0.8f;

    // Bonk when a thrown projectile bounces off the guard capsule. Independent
    // of ThrowExplodeEnabled.
    public virtual string ThrowHitSoundFile { get; set; } = "";
    public virtual float ThrowHitSoundVolume { get; set; } = 0.8f;

    // Chat panel: a world-space, scrollable Twitch chat overlay visible in both
    // the menu and during songs. Off by default; shorter in width than height.
    public virtual bool ChatEnabled { get; set; } = false;
    public virtual float ChatWidth { get; set; } = 60f;
    public virtual float ChatHeight { get; set; } = 80f;
    public virtual float ChatFontSize { get; set; } = 2.6f;
    // Chat panel spawn defaults match the Chat settings "Reset Position" button
    // (ChatPanelController.DefaultPosition/DefaultRotation), so a fresh config
    // file places the panel identically to a reset instead of the fallback
    // "face the player" LookRotation path.
    [UseConverter(typeof(Vector3Converter))]
    public virtual Vector3 ChatLastPos { get; set; } = ChatPanelController.DefaultPosition;
    [UseConverter(typeof(Vector3Converter))]
    public virtual Vector3 ChatLastRotation { get; set; } = ChatPanelController.DefaultRotation.eulerAngles;
    public virtual bool ChatLocked { get; set; } = false;
    [UseConverter(typeof(ColorConverter))]
    public virtual Color ChatNameColor { get; set; } = new Color(0.396f, 0.792f, 1f);
    [UseConverter(typeof(ColorConverter))]
    public virtual Color ChatTextColor { get; set; } = Color.white;
    // When true, always use ChatNameColor for usernames instead of the color
    // each viewer set in their Twitch chat.
    public virtual bool ChatForceNameColor { get; set; } = false;

    public virtual void Changed()
    {
    }
}

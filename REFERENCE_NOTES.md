# StreamReactive

> **Authoring note:** This file is **AI-generated / AI-edited** — produced with the
> assistance of an AI coding assistant under the author's direction, not handwritten.
> It records architecture and hard-won runtime lessons; treat it as a working
> document and verify against the code where accuracy matters.

A Beat Saber mod that reacts to stream events (Twitch bits, subs, raids, and a custom "bomb" event) during gameplay with configurable particle effects, cosmetic note replacements, and floating text.

## Architecture

```
StreamReactive.sln
StreamReactive/
  Plugin.cs                            — BSIPA entry point, BSEvents, Harmony init, DispatchStreamEvent, bit-tier helpers
  PluginConfig.cs                      — Config store (IPA auto-persisted)
  Directory.Build.props                — Assembly/branding metadata; Version + the InformationalVersion suffix suppression
  WebSocketServer.cs                   — HttpServer (websocket-sharp) serving /stream WebSocket + test dashboard over HTTP
  EventDispatcher.cs                   — Parses incoming JSON events, resolves colors, handles control commands + the global Enabled gate
  ParticleSpawner.cs                   — Unity ParticleSystem manager with pooling, custom textures + mask
  SoundManager.cs                      — Lazy OGG loading + playback for event / bonk sounds (Session cache)
  NoteCosmeticController.cs            — Note cut tracking, bomb cosmetics, sub/raid sustain windows, text, Harmony patches
  RuntimeHooks.cs                      — Singleton MonoBehaviour coroutine runner + LateUpdate event processing
  CapsuleGuardController.cs            — Guard capsule (headset/bone/platform anchor), preview visual, aim point
  ProjectileThrower.cs                 — Manual-kinematic bouncing projectiles (throw event), trajectory modes
  ComboThrowController.cs              — Auto-throws a block when the note combo breaks at/above the configured threshold
  FlashbangController.cs               — Full-screen white flash + viewer-facing overlay text
  ProjectionController.cs              — OBJ-file / preset particle projections (star, spiral, ...)
  CubeAnimationController.cs           — One-shot floating "Lurk" cube animation with name tag
  TwitchChatReader.cs                  — Anonymous `justinfan` IRC client feeding the chat panel + emote detection
  ChatPanelController.cs               — Custom in-game Twitch chat overlay panel
  EmoteCache.cs                        — Twitch/BTTV/FFZ/7TV emote catalog + GIF/APNG decode (SixLabors.ImageSharp)
  EmoteHud.cs                          — SpriteRenderer emote visuals (ImageFactory material) + emote throw/rain HUD
  EmoteRainPreview.cs                  — Settings-page preview markers for enabled rain zones
  MainConfigMenu.cs                    — MenuButton + FlowCoordinator for settings entry
  ScrollContainerTag.cs                — Custom `sr-scroll` BSML tag (plain ScrollRect, RectMask2D viewport)
  StreamReactiveSettingsViewController.cs — BSML settings UI bindings + GitHub update check banner
  ColorConverter.cs                    — Config store converter for Unity Color values
  Resources/Settings.bsml              — BSML markup for the settings page
  Resources/test-dashboard.html        — Web dashboard for sending test events
  Resources/generator.html             — Message generator for test payloads
  REFERENCE_NOTES.md                   — This file
  StreamReactive.csproj                — Project targeting net472
  StreamReactive.csproj.user           — Beat Saber install path (local-only, NOT committed)
  ext/_latest.log                      — Latest runtime log (local-only, NOT committed)
```

## Event Format

Send JSON to `ws://localhost:41243/stream`:

```json
{
  "type": "bits",
  "data": {
    "amount": 100,
    "user": "streamer"
  }
}
```

The type field may also be named `event` (e.g. `{"event": "bomb", ...}`) — both are accepted.

Event types:

- `bomb` — custom explosion effect (uses `message` for the floating text)
- `bits` — tiered effects based on amount (≥100, ≥1000, ≥5000, ≥10000)
- `subscription` / `sub` — sub sustain effect
- `subscription_gift` / `gift` — gifted sub sustain effect (shows gift count)
- `raid` / `host` — raid sustain effect (shows viewer count)
- `stop_all` — control: stops all active particle/event processing immediately (also clears thrown cubes)
- `socket` — control: turns the whole plugin on/off, identical to the General "Enabled" switch. `data.on` / `data.enabled` / `data.value` (or root `on`) = boolean; if absent it toggles from the current state.
- **Global kill switch (`Enabled`)** — while `cfg.Enabled == false` ONLY the pure control messages above (`stop_all`/`socket`/`pause`/`unpause`) still act. Every effect type is ignored, including the transient one-shots (flashbang/projection/throw/lurk) — the gate sits just after the controls, before any effect handling (`EventDispatcher`), and note events are not queued either. Disabling also clears currently-playing effects (both in `EventDispatcher` on `socket on:false`, and from the settings toggle's setter). The WebSocket server stays up so it can be re-enabled remotely. **The chat panel and emote system (Twitch channel-driven) are deliberately independent of `Enabled`/`Paused`** — they keep running as long as a channel is configured.
- `pause` / `unpause` (alias `resume`) — control: pause keeps the mod *enabled* and accepting events, but nothing plays until unpause. Note event types (bomb/bits/sub/raid) are held in the `NoteCosmeticController.ProcessPendingStreamEvents` queue and start on unpause; **flashbang is held and replayed on unpause** (`TryDrainHeldFlashbangs`); projection/throw/lurk have no queue and are ignored while paused. Mirrors the General "Paused" switch (config `Paused`).
- `skip` / `skip_current` — control: skips the current event's sustain window
 - `throw` / `throw_cube` / `cube` — control: throws projectiles at the guard capsule; they bounce off it. Works in the menu too (not gated by gameplay). `amount` = count (1–10, max 12 concurrent). Optional per-throw overrides in `data`: `scale`/`size` (multiplier), `spawn`/`origin` (spawn-point mode), `arc`/`time`/`airTime` (flight time in seconds).
 - `flashbang` / `flash_bang` / `flash` — one-shot: full-screen white flash. While paused or on a protected map it is **held and replayed** at the start of the next playable map instead of dropped (the viewer's bits/gift isn't wasted).
 - `projection` / `project` / `proj` — one-shot: shows a floating projection shape. `data.name` = `star`, `nuke`, `pulse`, `spiral`, or an OBJ filename (default `star`); optional `data.color` (hex), `duration`, `size`, `distance`, and position/rotation fields (`positionX`/`posX`/`x`, …).
 - `cubeanimation` / `cube_animation` / `cube_anim` / `lurk` — one-shot: the "Lurk" cube animation with floating viewer text. `data.user` (default "Anonymous"), `data.action`/`data.animation` = animation name (only `lurk` implemented), `data.scale`/`size` optional.

Any unrecognized `type` (e.g. `channelpoint`, `donation`) falls back to the generic "other" effect, which reuses the default bit-tier particle config.

Event flow: `bomb`, `bits`, `sub`/`raid` are ALL queued in `NoteCosmeticController._eventQueue` and processed in order by `ProcessPendingStreamEvents`. A running Bits/Sub/Raid blocks everything, including queued bombs (`if (anyNonBomb) return;`). While a Bomb is active, other queued bombs may join it (and skip past queued non-bomb events); otherwise events start strictly in arrival order. Nothing starts while out-of-game (`if (!Plugin.IsInGame) return;` — queued events wait for the next map, sustain windows pause across scene changes) or while `Config.Paused` is true. Only flashbang/projection/throw/lurk dispatch immediately and bypass the queue (projection/throw/lurk are ignored while paused; flashbang is held and replayed on unpause). `bomb` also differs in that its visuals attach to note cuts and its sound fires on cut.

Field notes:

- `user` is required for the floating text display.
- `amount` drives particle/note counts (and bit tier selection for `bits`).
- `message` is only displayed for `bomb` events; it is read but ignored for other types.
- `color` (hex) — an explicit payload color is honored for **every** event type (websocket args always win). Without one: `bomb` uses `BombColorMode`, `bits` uses the tier color for the amount, `subscription`/`raid` use their configured color, and unrecognized/other types use the default. `message` overrides go the same way.

## Chat Panel & Twitch Integration

- The Twitch connection is fully **anonymous read-only**: `TwitchChatReader.Connect` opens TLS to `irc.chat.twitch.tv:6697` with a generated `justinfan<random>` NICK and **no PASS line at all** (`TwitchChatReader.cs:61-66`) — nothing to authenticate, nothing to leak into the build.
- The reader runs on a background thread and forwards every PRIVMSG onto the main thread via `RuntimeHooks.EnqueueChatMessage` → `ChatPanelController.AddMessage` (chat overlay) and `Plugin.HandleEmoteDetected` (emote effects), where each is guarded by its own toggles.
- **Chat overlay** (`ChatPanelController`): a customizable in-game panel — name/text colors, force-name-color, badges, max messages, auto-scroll — fed by the same IRC stream. Off when `ChatEnabled` is off.
- **Emote catalog** (`EmoteCache`): resolves codes from Twitch, BTTV, FFZ and 7TV. GIF/APNG frames are decoded **off the main thread** with SixLabors.ImageSharp (a loose `Libs` dll — see the ImageSharp runtime note at the bottom). Effects consume codes as **emote rain** or **emote-throw projectiles**.
- **Emote rendering** (`EmoteHud`): `SpriteRenderer`s on the default layer using ImageFactory's `_Sprite` material (sprite assetbundle redistributed in our DLL, MIT — see `ThirdPartyNotices.md`) — the one configuration confirmed bloom-free in the headset AND the desktop mirror (see the External Mod References section for the HSV dead-end).
- Chat/emotes ignore `Enabled`/`Paused`, but the **map-protection gate still suppresses emote spawns** (`Plugin.cs`), and the catalog only downloads when a Twitch channel is configured.

## Sounds

- OGG files dropped into `UserData/StreamReactive/Sounds` (created by `Plugin.cs`) become per-event sound choices. Folders are rescanned whenever the settings menu opens (`RefreshSoundDropdowns` in `DidActivate`), so new files show up without a restart.
- Settings per event: a **Sound File** dropdown plus a **Sound Volume** slider (0–1). Bomb, Subs, and Raid each get one (Sound sections sit at the bottom of those pages). **Bits sounds are per tier** — each tier tab (<100 / ≥100 / ≥1k / ≥5k / ≥10k) has its own sound + volume, resolved from the cheer amount (`SoundManager.BitTierSoundFor`). Throw Projectile keeps a separate **Capsule Hit Sound** that plays when a thrown block bounces off the guard capsule — independent of Explosion On Capsule Contact. Emotes, flashbang, and projections have no sound options.
- SoundManager maps the dropdown's `"None"` display name to `""` in the config; volume < 0.001 is treated as muted.
- **All sounds are 2D only.** Every playback is a transient `AudioSource` with `spatialBlend = 0`, centered on both ears (the original `PlayClipAtPoint` at the head panned one-ear). The old spatial/3D toggle was removed entirely: `PluginConfig.SpatialSound`, the "3D Sound" toggle, the `spatial-sound` UIValue, and `SoundManager.PlayAt`'s world-anchor `PlayClipAtPoint` branch are all gone. `SoundManager.Play(file, volume)` (was `PlayAt(file, volume, position)`) ignores world position; the bomb-cut sound still fires from `ProcessNoteAtCut` (sliced note), the bonk from the capsule impact, bits/sub/raid from the persistent head, but all play centered. The transient 2D source keeps a reference on `RuntimeHooks` (persistent) and self-destroys after `clip.length + 0.2s`.
- The **bomb sound plays only on cut**, not on spawn: it fires from `ProcessNoteAtCut` when the note was transformed by a bomb event (`_bombedNotes.Contains(note)`), so it syncs with the player slicing the cursed note. Bits tier sounds require the raw cheer amount, which rides in from `DispatchStreamEvent` via `StreamEvent.Amount`.
- Clips load lazily via `UnityWebRequestMultimedia.GetAudioClip` (`AudioType.OGGVORBIS`, main-thread coroutine via `RuntimeHooks.RunCoroutine`), decoded through `DownloadHandlerAudioClip`, and cached for the session (`Clips`); failed loads land in a `Failed` set so they don't retry. All playbacks run on the main thread (event sounds in `NoteCosmeticController.StartEvent` via the LateUpdate dispatch, bonk in `ProjectileThrower.Update`, bomb in the cut patch).
- Note: `yield return` inside a try/catch is a compile error (CS1626) — the load coroutine yields outside the guarded region.

## UI / Settings Menu

- Settings menu uses a master-detail layout: left sidebar = category buttons, right = per-category page (one active at a time, switched via `SetActive` in `StreamReactiveSettingsViewController`).
- Layout/structure reference: **[AccSaber Reloaded plugin](https://github.com/not-dexter/accsaber-reloaded-plugin)** (GPL-3.0) by not-dexter & the AccSaber team. Credit for the explicitly-sized two-column pattern (`pref-width`/`pref-height` on both columns, no expand tricks). No code copied — BSML structure inspiration only.
- Ranked-map detection reference (kept from the previously-removed ranked feature, now used again): **[SongRankedBadge](https://github.com/qe201020335/SongRankedBadge)** (MIT) by qe201020335 — reads ranked status from the SongDetailsCache mod's local database via song-level `rankedStates` flags (ScoresaberRanked / BeatleaderRanked), using `await SongDetails.Init()` then `songs.FindByHash(lowercaseHash)`. No code copied — concept/API-surface inspiration only.

### BSML Gotchas (learned the hard way)

- There is **no `text-button` tag** — plain `<button>` is the text button.
- `~bindings` only resolve when the attribute value *starts* with `~`; mid-string is literal text.
- Avoid `<scroll-view>` inside layout-group containers — it clones a `TextPageScrollView` template and its sizing fights layout groups (breaks rendering). Prefer explicit sizing that fits.
- BSML parses at game runtime only; build errors won't catch invalid tags. Check `_latest.log`.
- The settings VC's comfortable visible height is ~40 BSML units (not the 60 the root claims) — oversized `pref-height` panels poke into the menu floor. Sidebar/pages use 42.
- Non-default pages are declared `active="false"` in markup as a safety net; code-behind switching toggles them via `SetActive`.
- Long word-wrapped `<text>` renders taller than layout predicts and pushes following elements past the visible area ("under the ground"). Put critical UI *above* long text (About page version box does this).
- Setting controls squeezed into half-width `<horizontal>` pairs render their label **one letter per line** — keep settings full-width. For pages with too many rows, use the `tab-selector`/`tab` sub-page pattern (`tab-tag`, proven by the Bits tier tabs) instead of trying to scroll.
- `<scroll-view>` clones HMUI's `TextPageScrollView` template (from EulaDisplayViewController) whose sizing fights layout groups — it killed the whole menu when tried inside the dynamic pages host. **Working alternative**: a custom BSML tag (`ScrollContainerTag.cs`, alias `sr-scroll`) that hand-builds a plain Unity `ScrollRect` (viewport with raycastable ImageView + `RectMask2D` clip, top-anchored ContentSizeFitter content, draggable auto-hiding scrollbar, wheel-driven). Pattern proven by AccSaber Reloaded's `  My2DScrollableContainer` (GPL-3.0) — credited above in the UI / Settings Menu section. **Must be registered lazily** (we do it right before presenting settings) — `BSMLParser.Instance` throws "Tried getting BSMLParser too early!" during `OnApplicationStart`.
 - The Throw page uses two sub-tabs: "Projectile" (launch pattern, trajectory mode w/ conditional rows, floor size, bounciness, friction) and "Capsule" (enable, visuals toggle, attach mode, bone path, dimensions).

### GitHub Update Check

- The General tab banner (`~update-status-text`) checks `https://api.github.com/repos/FEFELAND/StreamReactive/releases/latest` — anonymous `UnityWebRequest`, 10 s timeout, User-Agent `StreamReactive` — and compares the latest release tag against the running assembly version.
- Runs on every settings `DidActivate`, throttled to once per 10 minutes per session; always degrades to a neutral message, never throws or blocks the UI. The C# coroutine respects the old-runtime `yield`-outside-`try/catch` rule.
- Because the repo is currently **private**, GitHub answers the anonymous releases API with **HTTP 404**, which is indistinguishable from "no release yet" — both render the gray "No GitHub release yet - nothing to update". Making the repo public and tagging a release flips this to "Update available: <tag>".
- Tag parsing (`ParseVersionTag`) tolerates `v1.1.0` / `1.1.0` / `1.1.0-beta1`. For the banner to show "Update available", the tag must be **newer than the DLL version** in `Directory.Build.props`; a tag equal to it shows "Up to date: 1.1.0".

## Build & Deploy

```powershell
# Build (auto-copies DLL to game Plugins folder)
dotnet build StreamReactive/StreamReactive.csproj
```

- Assembly/product version lives in `Directory.Build.props` (`Version`, currently 1.1.0). `IncludeSourceRevisionInInformationalVersion=false` keeps the built informational/product version clean (no `+hash` suffix), so the DLL's version matches plain tags like `v1.1.0`.

**WARNING**: `TreatWarningsAsErrors=true` in csproj — nullable warnings are build-fatal.

---

## Beat Saber Runtime Notes

### Physics / Custom Colliders

- Beat Saber's **layer collision matrix disables most layer pairs** (including Default↔Default for arbitrary objects) — Rigidbody-based collisions between custom objects are unreliable.
- Workaround used by ProjectileThrower: **manual kinematic physics** — integrate position each frame, apply gravity with ballistic aim compensation, and do explicit capsule-segment vs cube distance tests. Immune to scene/layer settings.
- Trajectory modes (ThrowTrajectory): **Lofted** solves launch velocity from Air Time + Gravity so projectiles always arrive at the capsule (Air Time doubles as the arc control: short = flat laser, long = high lob); **Zero-G** fires straight shots at Throw Speed with gravity off — pure pinball ricochets. Twin Cannons alternation uses a static flipper so consecutive single throws alternate cannons across batches.
- Projectile visuals prefer a **real game note**: periodically (5 s retry) scans cached `NoteController` prefabs and `N0`–`N3` MeshFilter roots, scored by name preference (NormalGameNote > unknown > ProMode dot > BurstSlider; "bomb" excluded entirely after BombNoteController once threw literal bombs). Clones inherit the source's deactivated state — they must be `SetActive(true)`d or they freeze invisibly; behaviour scripts + colliders are stripped. A **procedural note** (rounded box + chevron from primitives) was tried as a menu-session fallback and rejected as ugly — the plain orange cube stays the fallback until a prefab appears in memory; fully textured notes in a fresh session would need bundled assets, which we deliberately avoid (game asset redistribution). **Coloring**: the game colors notes via MaterialPropertyBlocks, not material properties (writing `_Color` on the raw material leaves the block black), so the `NoteInitPatch` postfix captures live notes' property blocks per **color type** (`ProjectileThrower.CaptureLiveNoteMaterials`) the first time each red/blue note initializes, and clones replay those exact MPBs — this also makes projectiles alternate red/blue. Before capture exists, fallback writes player-matched colors into known color props via MPB. Player colors come from cached `ColorManager`/`ColorScheme` via reflection (compile-time type references clash with other assemblies).
- Bounces split velocity into normal + tangential components and damp each separately; **Bounciness (Capsule)** slider governs capsule-bounce energy (0 = dead stop, with extra velocity kill), while **Bounciness (Floor)** governs floor bounces and **Floor Friction** alone governs sliding (floor contact deliberately applies NO tangential damping, otherwise friction has no visible effect). Capsule bounces add a small random tilt to the reflection normal (`CapsuleBounceScatter` ≈ 7°) so volleys fan out instead of piling up in the same landing spot. The floor is a **finite platform-sized square pinned to the world origin (XZ and height, surface at y=0)** — cubes past the edge keep falling and despawn below kill depth. The green floor preview quad matches these bounds exactly.
- Bomb text: username line is `\n<size=70%>user</size>` by default, but if the message ends with a rich-text tag (`>`), the plugin skips its own wrapper so author styling like trailing `<size=0>` reaches the username. BombTextLineSpacing config applies TMP `lineSpacing` (-30..100) for tightening lines inflated by emoji/fallback fonts.
- Projectile lifecycle is config-driven: **ThrowLifetime** (1–120 s, default 14) then a **Despawn Fade** shrink-out (ThrowFadeSeconds 0–3 s, 0 = instant pop; dying projectiles spin gently and ease-in shrink to zero scale, freeing the spawn slot immediately). Finished projectiles are **pooled, not destroyed** (dictionary keyed by visual kind `-Cube`/`-NoteA`/`-NoteB` name suffix, max 4 per key): re-instantiating full note clones every throw hitched VR, so despawned ones are deactivated and recycled; `Initialize` resets physics + restores scale (originals captured on first use). **ThrowMaxProjectiles** slider (1–200, default 12) gates spawns. A user rejected an earlier procedural note fallback (primitives-built rounded box + chevron) as ugly — orange cube stays the no-prefab fallback. Note clones inherit whatever cosmetics (bit trails etc.) ride the source note at clone time — user LIKES that ("funny"), so cloning never strips them; `StripAttachedEffects` runs only in `Retire` to release stuck effects at despawn. Two related bugs fixed after a bad session: (1) `CreateVisual` only clones real note visuals once `_liveBlockMpbs[colorIndex]` exists — earlier it cached a reset/black and/or dot-variant live `NoteCube` mid-map before per-side captures landed, and pooling kept that broken look all session; menu throws stay orange cubes until a map's notes get captured. (2) `ResolveGameType` now requires types assignable from `UnityEngine.Object` — another assembly ships an unrelated `ColorManager`, which made `Resources.FindObjectsOfTypeAll` throw and disabled player-color capture for the fallback path.
- **Rank Protection (ranked-map pausing) is implemented** as a 4th Map Protection toggle ("Pause on Ranked Maps", off by default) alongside Noodle/Vivify/WIP (defaults: **WIP ON**, Noodle/Vivify/Ranked OFF — see `PluginConfig`). It blocks events while the playing map is ranked on BeatLeader, ScoreSaber, OR both (single toggle — no per-leaderboard split). Detection runs in the same once-per-map `RefreshCurrentMapInfo()` that feeds Noodle/Vivify/WIP and only needs BS_Utils' `beatmapKey.levelId` + the SongDetailsCache mod (so it works even when SongCore is absent). The hash is stripped from `custom_level_<hash>` (also handling the trailing ` WIP` suffix) and looked up via `SongDetails.songs.FindByHash`; the map is "ranked" when `rankedStates.HasFlag(ScoresaberRanked) || HasFlag(BeatleaderRanked)`. SongDetailsCache is detected by assembly scan at startup (`SongDetailsLoaded`, same pattern as `SongCoreLoaded`), its `SongDetails.Init()` DB load is kicked off once, fire-and-forget, off the main thread, and every lookup is best-effort with per-map debug logging (hash found? states flags? DB not ready yet?) — the post-mortem lesson from the old attempt was to instrument FIRST. No compile-time dependency beyond the `SongDetailsCache.dll` reference in the csproj; without it the toggle simply never gates.
  - **History**: rank protection was originally attempted and then REMOVED at the user's request after two failed test rounds. The old blockers no longer apply: BS_Utils' `GameplayCoreSceneSetupData` instance accessor works for reading `beatmapKey.levelId` (that's how the whole protection system gets the current map), and the old "SDC Init never reported ready" mystery is handled by explicit `_songDetails != null` readiness checks + per-map logging instead of assuming readiness.
  - **Map-protection timing / `_mapInfoPending`**: `Plugin._inGame` flips true the instant the game scene loads, but the per-map protection flags (`_mapIsWip`/`_mapHasNoodle`/`_mapHasVivify`/`_mapIsRanked`) aren't computed until `RefreshCurrentMapInfo()` runs — deferred by up to 6 frames in the `RefreshCurrentMapInfoDelayed` coroutine (waiting on BS_Utils to populate `beatmapKey.levelId`). In that window protection read as inactive, so events queued while in the menu (bits/bomb/sub/raid) started playing on the new map even when it was protected — at least the first one. Fix: `_mapInfoPending` is set true in `OnGameSceneLoaded`, `IsMapProtectionActive()` returns true while it's set (treating the not-yet-known map as protected), and `RefreshCurrentMapInfo()` clears it the moment it obtains a non-empty levelId (all flags are computed synchronously on the main thread right after, so events can never observe a half-resolved state). `OnMenuSceneLoaded` also clears it as a safety net.
- **CRASH LESSON — never clone modded note visuals:** a Vivify map replaced `colorNotes` with custom AssetBundle prefabs (`AssignTrackPrefab`, load mode `Single`); the prefab scan cached the live `NormalGameNote(Clone)/NoteCube` (4592 verts vs ~550 stock), threw cloned it, and when Vivify's bundle/components went away the pooled clone's dangling GPU references took the graphics device down (`Graphics device is null`, hard crash). Protections added: pristine `N0`–`N3` **asset templates** now beat any live `(Clone)` candidate by +10 preference so Vivify-swapped notes are never cloned; `ProjectileThrower.OnSceneChanged()` (wired to both scene-loaded hooks) drains the pool and drops `_noteVisualPrefab` ONLY when it was a live-clone source (asset templates are core objects, kept across scenes). Captured per-side MPBs are deliberately NOT wiped on scene changes — they're plain property bags copied from stock game notes (verified: `block 'NoteHD'` even inside Vivify maps), so persisting them lets menu throws replay the last map's exact look; wiping them forced the raw-color fallback in menu, and the `Custom/NoteHD` shader ignores the fallback color props (only `_SimpleColor` exists and writing it leaves the block black while arrow materials recolor fine). Clone path wrapped in try/catch → orange cube fallback. The capture-gate for cloning applies ONLY to live-clone sources (`_prefabIsAssetTemplate` set by an explicit per-candidate `isTemplate` flag — NOT by score thresholds): asset templates clone freely so menu throws keep working before/without any map's material captures. **Modded-visual restoration (user request):** while `Plugin.IsInGame` AND at least one side has captured MPBs, live clones get +20 pref so Vivify-swapped custom note models win the scan and get thrown (this was empirically safe once the pool drain existed — the single historical crash session also had the broken ColorManager path, so blame-sharing between clone-instantiation and that bug was never settled); a one-shot `_moddedRescanDone` upgrade rescan in `EnsureNotePrefab` switches template→clone mid-map after captures land, since first throws always precede captures. On scene exit the clone source is dropped and menu rescans pick templates again. Template detection GOTCHAS that each cost a debugging round: (1) prefab assets live in no scene, ALL their objects are inactive-in-hierarchy, so `GetComponentsInChildren<MeshRenderer>(false)` scores them 0 verts — include inactive renderers; (2) in this game version the N0–N3 MeshFilter-root loop never matches, templates actually come through the NoteController loop as e.g. `NormalGameNote/NoteCube` (no `(Clone)` suffix), so template-ness must be derived from the candidate's own origin (`name.EndsWith("(Clone)")`), never from preference score — and non-clone sources get +10 pref so vert-count tiebreaks can't hand the win to a modded 4592-vert clone again.
- ScrollRect-in-layout-group gotchas (all bit us once): a scroll container child of a BSML tab/layout must **declare a `LayoutElement` with preferred/min height** — parent groups that control heights collapse children without one to 0 height (invisible content, no errors); use centered anchors + explicit sizeDelta as well (stretch anchors + non-zero sizeDelta.y turns height into an offset and inflates the rect). The viewport uses **`RectMask2D`** for clipping, NOT a stencil `Mask` — `RectMask2D` clips *rendering only* and its clipped-out content stays raycastable, so scrolled-above rows sat invisibly over the bits/throw **tab-selector strip and swallowed clicks** (clicking there edited unseen settings). A stencil `Mask` would filter raycasts to the viewport too (Unity's own ScrollRect template uses one), BUT swapping it in hid the entire settings menu (BSML `ImageView` material lacks the expected stencil props, so masked content rendered nothing) — do not reintroduce it. A **`ScrollRaycastGate`** was added to solve the masked-out-row clicks by toggling each content graphic's `raycastTarget` every frame, but it **BROKE the BSML modal keyboard** (its left keys — the ones over the sidebar — stopped clicking), and was **REMOVED** (`ScrollContainerTag.cs` is the plain gate-free version again). Lesson: never mutate `raycastTarget` per frame in a settings canvas that hosts (or sits under) BSML modals over a keyboard region — the game keyboard hit-testing is raycast-aware and per-frame raycastTarget churn broke it where the keys overlap the scroll container's graphics. If masked-out-row clicks ever return, solve it in BSML markup/geometry (e.g. shorter pages, `tab-selector` sub-pages), not by raycast juggling. **Any `<vertical>` inside a `<tab>` must carry `horizontal-fit="PreferredSize" pref-width="~page-width"` or it collapses to zero width** — BSML tabs themselves are 0-height and rely on children overflowing unclipped, which is why plain settings render but anything mask-clipped (a scroller) shows nothing. (The temporary `ScrollDiagnostics` rect-dump component has since been removed.)
- Settings nav buttons: built-in hover/press tweens (`Selectable.transition` + `UIButtonAnimationController`) fight manual TMP label coloring and leave labels stuck highlighted after un-hover — disabled at first activation, plus label colors are re-asserted every `Update` as a safety net.
- The guard capsule still carries a real `CapsuleCollider` (harmless, and available for future physics use).

### Note Lifecycle (Pool Recycling)

- Notes are **pooled and recycled** — the same GameObject is reused for different notes.
- `NoteController.Init()` fires each time a note spawns (recycled or fresh).
- `noteData` is **NULL before `Init()` runs** — cannot check note type in a Prefix hook.
- Log confirms same instance IDs being reused repeatedly.

### Particles (pooling)

- `ParticleSpawner` reuses a small pool of `ParticleSystem`s. A recycled system could resume with a stale "dead" playback state where `Play()` emits nothing — the classic flake behind intermittent missing bursts (the per-note outline systems, freshly created, always rendered). `SpawnParticles` now hard-resets on reuse: `Stop(true, StopEmittingAndClear)` → `Clear()` → `time = 0` → `emission.enabled = true` → configure → `Play()`.
- The pool is membership-guarded (`_pooled` `HashSet`) at both enqueue sites (`ReturnToPoolAfter` and `StopAll`), because a scene change can make both run for the same instance and hand ONE system to TWO concurrent explosions — the second config overwrites the first and a burst silently vanishes.
- **Diagnosing cut→burst:** `ProcessNoteAtCut` logs an always-on `Plugin.Log.Debug` `"ProcessNoteAtCut: <Type> note X cut -> ..."` (NOT gated by `VerboseLogging`), so a missing explosion can be separated from a missed cut when reading logs.

### Harmony Patching

- `NoteInitPatch`: Prefix (`RestoreNoteIfNeeded`) + Postfix (`ProcessNoteAtInit`) on the inherited `Init` method of `NoteController`.
- `NoteCutPatch`: Postfix (`ProcessNoteAtCut`) on note cut.
- `NoteDespawnPatch`: Postfix on `BeatmapObjectManager.Despawn(NoteController)` — refunds pending claims when notes despawn early.
- Harmony ID: `com.fefeland.StreamReactive`

### Note Hierarchy (bomb cosmetic)

```
NormalGameNote(Clone)
  NoteCube
    NoteArrow          (SetActive(false) for bomb)
    NoteArrowGlow      (SetActive(false) for bomb)
    NoteCircleGlow     (SetActive(false) for bomb)
```

The cosmetic bomb swap changes the `NoteCube` mesh to the bomb mesh and hides the arrow/glow children. All components stay alive and active; the original mesh is stored and restored when the note is recycled.

---

## External Mod References

### ReadieFur/BSDataPuller (MIT License)
- https://github.com/ReadieFur/BSDataPuller

**Consulted for (ComboThrowController.cs):**

- Confirmed that Beat Saber 1.40.8's `ScoreController` exposes NO combo event
  or property (only `scoreDidChangeEvent`, `multiplierDidChangeEvent`,
  `scoringForNoteStartedEvent`, `scoringForNoteFinishedEvent`). Combo is
  derived from scoring elements, not a tracked field.
- Adopted BSDataPuller's combo-tracking method: increment a counter on each
  good cut via a Harmony postfix on
  `BeatmapObjectExecutionRatingsRecorder.HandleScoringForNoteDidFinish`
  (when `scoringElement is GoodCutScoringElement`), and reset it on a break by
  hooking `BeatmapObjectManager.HandleNoteControllerNoteWasMissed` (real note
  miss, guarded by `noteData.colorType != ColorType.None`) and
  `HandleNoteControllerNoteWasCut` (when `!noteCutInfo.allIsOK`, i.e. bad cuts
  and bomb hits). Our `ComboThrowController` then throws a block when the combo
  breaks from at/above the configured threshold. This mirrors BSDataPuller's
  `MapEvents` logic (its combo increment lives in a Harmony patch on the
  scoring finish; `NoteWasCutEvent` guards on `allIsOK`, `NoteWasMissedEvent`
  resets on real note misses) rather than relying on a (non-existent) combo
  event.

---

### ErisApps/HitScoreVisualizer (GPL-3.0 License)
- https://github.com/ErisApps/HitScoreVisualizer

**Consulted for (early emote VR rendering investigation):**

- We reviewed HSV's `HsvFlyingEffect.CreatePrefab` (sets
  `textObject.layer = LayerMask.NameToLayer("UI")`) and its `BloomFontProvider`
  (swaps the TMP material shader to `TextMeshPro/Distance Field` for the bloomed
  font variant). This informed the investigation, but the **UI-layer bloom
  exclusion did NOT work on Beat Saber 1.40.8** — emotes placed on the UI layer
  still bloom there. The dedicated bloom-free overlay-camera approach it
  suggested was prototyped (`EmoteCameraRig`) and then **fully removed**; the
  shipping emote renderer (see ImageFactory below) owes it nothing. **No
  HitScoreVisualizer code is distributed in the build.**

### WentTheFox/ImageFactory (MIT License)
- https://github.com/WentTheFox/ImageFactory

**Used by (EmoteHud.cs — emote rendering):**

- The shipping emote visuals are `SpriteRenderer`s using ImageFactory's sprite
  material, loaded from its embedded `sprite.assetbundle` (the `_Sprite` Renderer
  material). This is the one configuration confirmed to render true-color and
  bloom-free in both the headset and Camera2's desktop mirror (default layer, no
  CameraUtils registration / overlay camera). The assetbundle is redistributed
  inside our DLL; the full MIT license text is in `ThirdPartyNotices.md`.

---

### NoteTweaks (runtime adaptation — repo never consulted)

`Plugin.cs` detects `NoteTweaks` at startup by assembly-name scan
(`Plugin.NoteTweaksLoaded`). Everything the mod does around NoteTweaks was
worked out by **trial and error at runtime** — the NoteTweaks repo was never
opened, so there is no code copied, no URL to cite, and no license to claim.

What the mod actually adapts:

- **Flexible note-block detection** (`NoteCosmeticController.ScoreNoteBlockCandidate`,
  line 1589) — when the stock `NoteCube` child is missing/renamed by a NoteTweaks
  preset, bomb visuals and throw-cube mesh capture instead pick the best MeshFilter
  under the note by name scoring (`NoteCube`/`NoteHD`/`NoteBlock` bonuses,
  `Outline`/`Glow`/`Arrow` penalties).
- **Settling window** — `ApplyBombVisualDeferred` waits one frame so NoteTweaks'
  Init postfix finishes before the bomb look overwrites the note (line 1759); the
  Init-related patches at lines 2004/2011 do the same.
- **Outline renderers** — `ApplyBombVisual`/`RestoreNoteVisuals` disable and
  re-enable any extra child renderers a NoteTweaks note carries (lines 1851/1943).
- **Throw-cube capture** — `ProjectileThrower` instance-copies live note materials
  so NoteTweaks/Vivify-decorated notes keep their look when cloned (lines 1151/1257).

All of it was reverse-engineered from live gameplay, not documentation.

---

## Required Mods (dependencies)

This mod requires the following mods installed in Beat Saber:

- **BSIPA** (^4.3.0) — the mod loader/framework. Provides the plugin
  lifecycle and `IPA.Config.Stores` for settings persistence. It also places
  the shared assemblies our plugin binds against in the game's `Libs` folder:
  **HarmonyLib** (`0Harmony.dll`, runtime patching) and **Newtonsoft.Json**
  (used by the config store). These are resolved from `Libs` at runtime, not
  embedded in our DLL.
- **BS_Utils** — provides `BSEvents` (game/menu scene-load events) used by
  `Plugin.cs` to drive in-game behavior and combo resets.
- **BeatSaberMarkupLanguage (BSML)** — provides the in-game settings UI
  (`BSMLAutomaticViewController`, custom `sr-scroll` tag). The settings menu
  will not load without it.

- **Optional feature dependencies** — SongDetailsCache is only referenced by the ranked-map Map Protection toggle and is *optional* (detected at runtime by assembly scan; the toggle just never gates if absent). SongCore is only used for Noodle/Vivify/WIP detection and is likewise optional-with-guard.

### Required runtime library: websocket-sharp
`StreamReactive` uses **websocket-sharp** (`websocket-sharp.dll`) for its
WebSocket/HTTP server. It is a loose assembly in the game's `Libs` folder (not a
manifest mod) and must be present for the mod to load — note it as a requirement
alongside BSIPA, BS_Utils and BSML. A baseline modded install does not always
include it, so ensure `Libs\websocket-sharp.dll` is present.

### Required runtime library: SixLabors.ImageSharp

`EmoteCache.cs` decodes GIF/APNG emote frames off the main thread with
**SixLabors.ImageSharp** (`SixLabors.ImageSharp.dll`, v2.0.0). It is a loose
assembly in the game's `Libs` folder — a compile-time reference from the dev
install only (`StreamReactive.csproj`, `<Reference Include="SixLabors.ImageSharp">`),
never embedded in our DLL or copied on build. Something must provide it at runtime
or animated/GIF emotes fail to decode (the decode is try/caught and logged per
emote; static PNG emotes are unaffected).

**License:** v2.0.0 predates Six Labors' June-2022 license change, so it is plain
**Apache-2.0**. Newer ImageSharp releases use the *Six Labors Split License*: still
free under Apache-2.0 for open-source/source-available consumers, transitive
dependencies, and small businesses/nonprofits, but requiring a commercial license
otherwise — and starting with 4.0.0 a paid license file is even required to *build*
direct dependencies. The runtime binds whichever copy the game's Libs/Plugins
provides, so if a newer version gets resolved, its terms apply.
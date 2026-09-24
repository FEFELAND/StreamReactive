# 1.2.1
- Chat Panel: It now displays badges properly instead of text. (🔗 = Shared chat)
- Chat Panel: There is now a toggle to enable emotes in the chat panel.
- Added event: Highway text scroll

# 1.2.0
- **Big feature**: You may now use the mod without the need of a bot if you want. Only your twitch channel name. (Though I still recommend the websocket setup instead) (Thanks qlulezz for the idea)
- Twitch channel name method supports: Subs, Bits, Raids, !bomb and !throw
- Each category listed can now be individually turned off. If you want to momentarily turn off something even if you have it set up via websocket or irc. (Thanks qlulezz for the idea)
- Bit events have new default costs: `<100: 1` - `≥100:5` - `≥1000: 25` - `≥5000: 50` - `≥10000: 50`. Also, it will always round up.
- Potential fix: In some setups. When "throw" was used after restarting a map, it would throw the block ahead of the user. That shouldn't happen anymore. (Thanks for test Sehria)
- Chat panel: LOTS of events now show in chat. Like subs, bits, watch streaks, etc. No follows or channel points though.
- Emote Throw/Rain: Fixed a smol bug with sizes other than default. It would spawn at default size and resize a frame later. 
- Flashbang: Any that queue (from the mod being paused or while playing a "protected map") will no longer play instantly in the next valid map. They will now have a random 20 - 60 second wait before activating. Now you can't prepare for them :P
- Flashbang: It now has a sound selector, goes of when flash activates.
- CubaAnimation: New addition `Eject`. Among Us-like eject animation.
- Test Dashboard: `generator.html` now gets copied to `\UserData\StreamReactive`. Now you can open the page/configure your bot without having to open the game.

# 1.1.5
- Dissolved (invisible) notes in noodle maps should no longer have effects applied to them. Like the Paradigm gimmick

# 1.1.4
### Vivify saga part 2
- Vastly improved vivify compatibility.
- Bomb event should now work in pretty much any map.
- Throw event still has a little bit less of a success rate but it is MUCH better than before.
- Further testing required and probably some more tweaks for edge cases. A certain [Kitchen](https://beatsaver.com/maps/43a4a) map is not fully playing nice.
- [End Times](https://beatsaver.com/maps/43a24) have been tamed though! (from what I can see/test)

# 1.1.3
### Vivify fixes attempt
- Bomb no longer makes notes invisible on most maps. But still an issue on fancy ones like [End Times](https://beatsaver.com/maps/43a24)
- Throw should mostly correctly copy the notes if they have been modified by vivify. Still a little odd. Need to poke at further.
- Map protection/pause for Vivify is now on by default since it has a few issues. Again, might need to poke at more later.

# 1.1.2
- Bombs: Added "Strip Message Tags" toggle. (makes things like `<color=#ffffff>` `<size=40>` not work)
- Bombs: Added "Glow Brightness" slider.

# 1.1.1
- Added settings for note particle outlines (the outline effect for gift subs and raids)
- Added a basic chat window
- Improved NoteTweaks compatibility for the throw feature. Notes now should look exactly as your preset.
- Fixed flashbang custom text not applying
- Hopefully fixed multi throw bug that could crash your game.
- Maybe fixed "throw" feature from breaking if it is being spammed while you restart a map. (Thank you Sehria's chat)
- Added protection (pausing the mod) for Ranked, Vivify, Noodle and/or WIP maps. It saves/queues most events. (Thanks Sehria for idea)
- Lurk animation. (Thanks binglepringl for idea)
- Word limit on the bomb command.
- Update notifier in general tab.
- Small tooltip changes in menus.

# 1.0.0
### Initial semi-public release featuring:
- Bomb event. (the classic !bomb command)
- Subs/Gift subs event.
- Raid event.
- Bits event.
- Throw item event. Currently supporting cubes.
- Flashbang event.
- "Projections" Custom obj spawn as particles + a few preset animations/shapes.
- Enable/Disable/Pause/Unpause.
- Queue system for Bits/Subs/Bombs/Raids.
- Flashbang/Throw/Projections work on their own at any time.
- Twitch channel connection + Emote support for Twitch, BTTV, FFZ & 7TV emotes.
- Along with emotes: Emote rain and emote throw.
- Lots of configuration for pretty much everything. (menuphobia warning!)
- Testing dashboard + websocket json/code generator for easy setup and testing.
- I did not keep track of how many versions or how many fixes before this point.

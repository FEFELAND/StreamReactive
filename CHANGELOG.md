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

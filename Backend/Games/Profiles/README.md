# Kill feed profiles

One JSON file per game, describing where that game draws its kill feed. Used by the
post-processing kill scan (`Backend/Media/KillFeedScanner.cs`) for games VPULSE has no native
integration for — Delta Force and the like, which expose no local match data to read.

Games with a native integration (PUBG, GTA, Rocket League, …) detect kills live while recording
and do not need a profile. The one shipped for PUBG is a worked example, and covers the case where
someone turns its integration off.

## Two places a profile can live

| Location | Written by | Wins |
|---|---|---|
| `Backend/Games/Profiles/` (this folder) | contributors, shipped with the app | — |
| `%APPDATA%/VPULSE/game-profiles/` | the user, by calibrating | ✅ |

A user's own calibration always takes priority, so shipping a profile for a game someone has
already calibrated will not disturb them, and an app update cannot overwrite their work.

## Adding a game

Calibrate it once in the app (session card → **Find Kills**), then copy the resulting file from
`%APPDATA%/VPULSE/game-profiles/` into this folder. Blank out `playerName` first — see below.

The file name must be the game name lowercased with every run of non-alphanumeric characters
replaced by a single dash: `PUBG: BATTLEGROUNDS` → `pubg-battlegrounds.json`. This has to match
`KillFeedProfileStore.ToSlug`, which is what resolves a recording's game to a file.

## Fields

| Field | Meaning |
|---|---|
| `gameName` | The real name, since the slug cannot be turned back into it |
| `regionX`, `regionY`, `regionWidth`, `regionHeight` | The kill feed area, relative to the frame (0-1) |
| `playerName` | The player's own name — **leave empty in shipped profiles** |
| `scanFramesPerSecond` | How often to sample the recording |
| `includeDeaths` | Whether deaths are worth marking for this game, or only kills |

The region is relative rather than in pixels so a profile calibrated at 1080p applies unchanged at
1440p, and to anyone else's monitor. That is what makes these files worth sharing.

`playerName` is the one field that does not transfer between people. Leave it empty here; the app
asks for it during calibration and stores it in the user's own copy. A profile with no player name
is treated as a region-only starting point, not a complete profile — the scan cannot tell a kill
from a death without knowing who to look for.

`scanFramesPerSecond` defaults to 1. Measured against a session with known kill times, halving it
to 0.5 missed two kills out of three: a feed row stays on screen for several seconds but is only
*legible* for the first two or three, so the usable rate is set by readability, not visibility.
Raising it above 1 only costs scan time.

## What the shipped profiles cover

`battlefield-6.json` is taller than a single row on purpose: Battlefield stacks several entries and
the top one is often under a stream overlay, so the region has to reach far enough down to catch a
row that has scrolled. Validated against a 56-second clip — six events, every one confirmed against
the frame it came from.

Battlefield also puts a `▸` marker immediately before the player's own name, on both kill and death
rows. It is dropped along with every other token that has no letters, so it does not disturb the
left-to-right ordering.

`delta-force.json` covers the same top-right feed, validated against a live session.

Delta Force is why the scanner reads every frame twice — once raw, once with redness mapped to
brightness. Its feed draws enemy names in dark red that plain OCR cannot read, so a kill row would
surface with only one name and be discarded as unclear. Measured on a session full of kills, the
raw pass alone found none of them; the dual pass recovers the red name, completes the pair, and the
kills come back. (The second pass only ever adds words — nothing the raw pass read is discarded.)

**Known limitation for Delta Force:** the feed uses the same row shape for reviving a teammate as
for killing an enemy — `LordWaffl3 ⟳ Zosazi` reads identically to a kill, and the scan reports it as
one. The two differ only by an icon, which OCR does not see. Confirmed on a session where the player
had no kills at all and the single reported kill was a revive. The thumbnail beside each candidate
is what makes this obvious at a glance, and it can be unchecked before the bookmarks are written.

## Drawing the region

Include the whole feed row — the killer on the left, the victim on the right — with margin on both
sides. That horizontal position is what separates a kill from a death, so a region tight enough to
clip a row destroys the distinction: with a single name visible the player is both first and last,
and the scan reports the row as unclear rather than guessing.

Erring wide costs nothing. A region too wide only feeds the OCR extra background, which it ignores.

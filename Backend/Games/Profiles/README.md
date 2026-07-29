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

## Drawing the region

Include the whole feed row — the killer on the left, the victim on the right — with margin on both
sides. That horizontal position is what separates a kill from a death, so a region tight enough to
clip a row destroys the distinction: with a single name visible the player is both first and last,
and the scan reports the row as unclear rather than guessing.

Erring wide costs nothing. A region too wide only feeds the OCR extra background, which it ignores.

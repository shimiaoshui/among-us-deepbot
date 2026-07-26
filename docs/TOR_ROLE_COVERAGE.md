# The Other Roles 4.6.0 coverage

DeepBot audits the `RoleId` values shipped by The Other Roles 4.6.0. The current adapter recognizes all 44 custom primary roles, the two base-game identities, and all 11 stackable modifiers. The startup audit reports `missing=0` and `extra=0`.

## Primary roles

| Alignment | Recognized roles |
|---|---|
| Crew | Mayor, Portalmaker, Engineer, Sheriff, Deputy, Lighter, Detective, TimeMaster, Medic, Swapper, Seer, Hacker, Tracker, Snitch, Spy, SecurityGuard, NiceGuesser, Medium, Trapper |
| Impostor | Godfather, Mafioso, Janitor, Morphling, Camouflager, Vampire, Eraser, Trickster, Cleaner, Warlock, BountyHunter, EvilGuesser, Witch, Ninja, Bomber, Yoyo |
| Neutral | Jester, Jackal, Sidekick, Arsonist, Vulture, Lawyer, Prosecutor, Pursuer, Thief |

The base-game `Crewmate` and `Impostor` identities are read from the game's native role type.

## Stackable modifiers

`Lover`, `Bait`, `Bloody`, `AntiTeleport`, `Tiebreaker`, `Sunglasses`, `Mini`, `Vip`, `Invert`, `Chameleon`, and `Shifter` are stored separately from the primary role. A player may have one primary role plus valid modifiers; DeepBot does not create several mutually exclusive primary roles for one player.

## Rule ownership

TOR remains authoritative for role assignment, cooldowns, charges, target legality, death resolution, task eligibility, and win conditions. DeepBot selects an intention and interacts through TOR's native state and RPC paths. A model response cannot skip a stage or bypass a lobby option.

Examples:

- Engineer vent access and limits come from TOR and the lobby settings.
- Vampire uses the native bite sequence. The victim dies at the bite position after the configured delay; the Vampire is not teleported to the victim.
- Morphling samples first and morphs only in the legal second stage.
- Trickster places the required boxes before using the box network and lights-out mechanics.
- Warlock curses first, waits for a legal second target, and then resolves the native second stage.
- Bomber planting, arming, disarming, blast range, and deaths are confirmed by TOR.
- Arsonist channels one legal douse at a time and ignites only after every required living target is doused.
- Vulture may consume a body instead of reporting it when that is the role-correct action.
- Godfather can perform ordinary kills; Mafioso waits for TOR succession while the Godfather is alive.
- Sheriff, Deputy, Guesser, Swapper, Mayor, and other meeting roles operate only in their legal phase.

## Private information

Role-specific information is private to the bot that legally received it. Detective footprints, Seer souls, Tracker updates, Trapper triggers, Security Guard camera or DoorLog observations, witnessed kills, vents, and morphs enter that bot's own memory. They are not copied into a shared bot memory.

## Current support boundary

The Skeld is the release-qualified navigation map. MIRA HQ integration remains experimental and is not claimed as complete in this release. Base-game and TOR rule compatibility can still depend on the exact Among Us and TOR build used by every participant.

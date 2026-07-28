# 0.10.4-client-visibility (release candidate)

- Fixed host-created bots existing only in the host's local `ClientData` roster after a LAN peer joined. Passive clients now bind reserved bot player records to the authoritative host-spawned `PlayerControl` objects and create local presentation-only client proxies.
- Fixed the passive-client runtime boundary returning before presentation maintenance could execute. Clients still cannot run bot movement, decisions, abilities, meetings, role state, or camera control.
- Added throttled client diagnostics reporting reserved bot records, matched network controls, presentation proxies, and total client entries.
- Fixed unstable lobby-name collision detection that repeatedly changed every bot between its configured name and a suffixed name, producing an RPC flood and eventual heartbeat timeout.
- Bot identity now survives host-owned network/physics synchronization without falling through to the human input path.
- Rebuilt and clean-install tested all four Windows installers/uninstallers with matching TOR, Reactor, and DeepBot fingerprints. No API key is included.

## Previous release: 0.10.3-authority-handshake

- Fixed passive clients participating in DeepBot's runtime control loop. A strict host-authority boundary now blocks every bot movement, physics, role-state, meeting, camera, memory, and world-control callback on clients.
- Fixed bots appearing to mirror a human client's movement after installing the client package. Clients now render host-synchronized state only and log `DeepBot passive client mode active` when world control is disabled.
- Fixed Steam-root installation choosing an unrelated vanilla folder named `Among Us` while the actively used TOR copy lived elsewhere in the same library. Existing matching receipts, compatibility manifests, TOR + DeepBot pairs, and recent runtime activity now outrank the folder name.
- Added a multi-install regression test that builds a fake Steam library with both vanilla and TOR installations, verifies that installation targets TOR, and verifies that uninstallation selects and cleans the same target.
- TOR's native Local/LAN version handshake retry now begins as soon as the local player exists and repeats once per second. TOR 4.6.0 version and module GUID validation remains authoritative.
- Rebuilt the Host Installer, Client Installer, Host Uninstaller, and Client Uninstaller from one `0.10.3` source and compatibility fingerprint.
- Fixed role-ability routes repeatedly steering toward obstructed live room-center transforms, including the Skeld Storage fuel/crate corner. Placement stages now operate from the runtime grid's reachable projected endpoint, and a physically abandoned ability target observes its unreachable cooldown instead of being assigned again immediately.
- Verified both direct-folder and Steam-root installs, the complete launcher boot chain, matching host/client TOR-Reactor-DeepBot SHA-256 values, API-key storage boundaries, and clean uninstallation.

## Previous release: 0.10.2-lan-handshake-api-setup

- Fixed Local/LAN clients being removed with “The host has no or a different version of The Other Roles” when TOR's initial host handshake arrived before the joining peer was ready. DeepBot now retries TOR's own reliable version handshake every two seconds while the LAN lobby is active.
- The retry does not spoof or bypass compatibility checks. TOR still rejects a genuinely different version or module build.
- Host and Client packages embed the same compatibility ID plus SHA-256 fingerprints for TOR, Reactor, and DeepBot. Both the installer and guarded launcher stop with a clear error if installed files do not match the release.
- Added API base URL and masked API-key fields directly to the Host installer. The endpoint is written to the host configuration; the key is stored only in `%LOCALAPPDATA%\AmongUsDeepSeekBots\api-key.txt` and is excluded from logs, receipts, source, packages, and release assets.
- Added an automated clean-install matrix for Host/Client installation, compatibility parity, guarded launcher validation, API configuration, key boundaries, and complete uninstallation.
- No API key is included.

# 0.10.1-installer-runtime-fix

- Fixed clean-computer installations opening vanilla Among Us because the installer omitted the private `dotnet\coreclr.dll` runtime required by Doorstop/BepInEx IL2CPP.
- Host and Client payloads now include the complete private CoreCLR runtime from the validated TOR build.
- Added embedded-payload and post-install boot-chain validation. An incomplete package can no longer report a successful installation.
- Desktop shortcuts now use the guarded DeepBot launcher, which identifies missing runtime or plugin files before starting the game.
- Matching uninstallers track and reverse the newly installed runtime files through the existing receipt and backup system.
- No API key is included.

## Previous release: 0.10.0-skeld-native-tor

- Added separate self-contained Windows host and client installers. Both accept a Steam root or exact game directory and back up overwritten mod files before installation.
- Added matching self-contained host and client uninstallers. New installs record file hashes and original backups so removal can restore pre-install files while preserving user-modified files.
- Fixed an unvalidated local-avoidance fallback that could push a living bot through an outer wall when every tested steering direction was blocked.
- Preserved TOR-managed Ninja and Chameleon concealment instead of treating low opacity as a render fault, and excluded genuinely invisible players from bot sight, pursuit, witnessed actions, and meeting evidence.
- Added a deterministic meeting guard that prevents impostors, Jackal partners, Lovers, and Lawyers from publicly exposing or voting for protected allies even when a model proposes a sacrificial or "trial" accusation; complete model responses are now checked against the authoritative legal vote set.
- Fixed later meetings remaining open after every living player voted by recognizing TOR ghost roles and continuing native completion checks until the meeting ends.
- Cancelled every ordinary-kill pursuit for Vampire and other role-gated killers, leaving Vampire elimination entirely to TOR's native bite and delayed-death sequence.
- Added legal LLM decision checkpoints between native stages for Morphling, Trickster, Ninja, Warlock, Vampire, Bomber, and Yoyo without allowing stage skipping or target replacement.
- Added private Security Guard camera and MIRA DoorLog observations, Detective footprints, Seer soul locations, base-game shapeshift witnessing, and host-side client vent witnessing.
- Strengthened meeting speaker attribution and silence rules so a bot cannot invent a statement for another player or fill an evidence-free turn with generic procedural language.
- Retains host-authoritative lobby bot count, appearance customization, room-rule synchronization, private memories, emergency response, native TOR roles, and deduplicated post-match learning.
- The Skeld is release-qualified. MIRA HQ remains experimental.
- Release installers, source, and documentation contain no API key.

# 0.9.11-lobby-identity-meeting

- Added TOR lobby controls for each bot's name, color, outfit, and nameplate.
- Replaced visible-name bot detection with reserved virtual-client identity, so custom names remain fully functional.
- Automatically disambiguates duplicate bot names to prevent two overlapping players from appearing to have multiple primary roles.
- Isolated meeting memory per bot and classified other speakers' statements as public claims rather than shared eyewitness facts.
- Added a code-level evidence guard for unsupported kill claims and unnatural "murder route" prompts.
- Prevented recursive bot-to-bot model calls while preserving direct replies and full reconsideration after human statements.
- Prevented impostors from publicly leaking private murder location, timing, victim-route, or ability-kill details before those facts are disclosed in the meeting.
- Release archives and source contain no API key.

## Previous release: 0.9.10-tor46-strict-role-rules

- Vampire bites now resolve after the configured delay without moving the attacker to the victim.
- Bombs use host-authoritative range checks and record a bomb death only after TOR confirms the kill.
- TOR traps and Bait cover host-owned virtual bots. Deputy handcuffs prevent a bot from killing, sabotaging, using abilities, or venting.
- Fixed the match-wide movement twitch that occurred when a human player had reversed controls. A bot with the modifier now compensates its own input correctly.
- Ability cooldowns continue while venting, while meetings and genuine immobility states still pause them.
- Personally witnessed kills become hard local evidence. Vents and multiple TOR special actions enter private meeting memory and role deduction.
- Supportive crew reads are no longer parsed as accusations. Low-information filler is suppressed, and an inconclusive model response no longer overwrites a valid earlier vote.
- Known Lover partners are protected by both code and decision policy from intentional kills, hostile abilities, and votes.
- Neutral roles no longer enter the impostor opening fake-task flow. Only true impostors use impostor cover behavior and its local fallback.
- Ordinary crewmates interrupt their current goal to evade an approaching player only after personally witnessing a kill or carrying an explicit meeting suspicion.
- Action and ability response budgets were increased to reduce model outputs that end before returning their final JSON decision.
- TOR lobby settings now expose an `AI Bot Count` from 1 to 8. Bots remain host-created and host-driven.
- Preserves and extends recognition of all 44 TOR custom primary roles, 2 base identities, and 11 stackable modifiers.
- Release archives and source contain no API key.

## Previous release: 0.9.9-tor46-full-role-recognition

- Completed identity recognition for all 57 TOR v4.6.0 `RoleId` values, audited with `missing=0`.
- Added Prosecutor-specific identity, objective, and meeting strategy instead of treating it as Lawyer.
- Separated primary roles from all 11 modifiers so Role Exchanger no longer overwrites the real primary role.
- Added modifiers to private role prompts, ability decisions, and assignment logs.
- Retained the 0.9.8 role-ability arbitration, TOR validation, task and emergency handling, navigation, meeting reasoning, and post-game reflection systems.
- Release archives contain no game files, third-party mod distribution beyond the documented compatibility package, or API key.

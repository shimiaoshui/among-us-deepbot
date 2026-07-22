# 0.9.10-tor46-strict-role-rules

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

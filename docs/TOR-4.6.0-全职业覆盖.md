# TOR 4.6.0 全职业覆盖审计

审计基准是 The Other Roles v4.6.0 的 `RoleId` 枚举。`0.9.9` 对 57 个枚举项逐项比对，结果为 `missing=0`、`extra=0`。

## 主职业（44）

| 阵营 | 已识别职业 |
|---|---|
| 船员 | Mayor、Portalmaker、Engineer、Sheriff、Deputy、Lighter、Detective、TimeMaster、Medic、Swapper、Seer、Hacker、Tracker、Snitch、Spy、SecurityGuard、NiceGuesser、Medium、Trapper |
| 内鬼 | Godfather、Mafioso、Janitor、Morphling、Camouflager、Vampire、Eraser、Trickster、Cleaner、Warlock、BountyHunter、EvilGuesser、Witch、Ninja、Bomber、Yoyo |
| 中立 | Jester、Jackal、Sidekick、Arsonist、Vulture、Lawyer、Prosecutor、Pursuer、Thief |

另有原版基础身份 `Crewmate`、`Impostor` 由游戏原生 `RoleType` 识别，共 46 种主身份来源。

## 附加职业（11）

`Lover、Bait、Bloody、AntiTeleport、Tiebreaker、Sunglasses、Mini、Vip、Invert、Chameleon、Shifter`。

这些附加职业与主职业分开记录，可以合法叠加，不会覆盖主职业。`Shifter` 的主动换职决策由能力调度器单独读取，但在阵营、任务和胜利目标上仍以当前主职业为基础。

## 特别修正

- `Lawyer` 与 `Prosecutor` 共用 TOR 的 `Lawyer.lawyer` 所有者字段，现在额外读取 `Lawyer.isProsecutor`，不会再把检察官误识别成律师。
- 律师目标是保护委托人；检察官目标是让指定目标被投票放逐，会议提示和胜利目标已经分开。
- 中立职业不会计入船员任务分母，也不会因原生外观误提示而获得船员任务目标。
- 最终胜负、幼年 Mini 保护、警长误杀、豺狼阵营终局等仍交给 TOR 的原生规则校验器决定。

## 能力执行含义

“全职业识别”不等于每个职业都有一个随时可按的按钮。Mayor、Swapper、Guesser 等会议型职业通过会议证据和投票阶段生效；Detective、Seer、Hacker、Medium 等信息型职业主要向私有记忆和推理提供合法信息。具有实体效果的技能会在房主端经过冷却、距离、目标合法性、房间设置和 TOR 自身规则校验后执行。

当前已接入主动实体行为包括：Portalmaker、Engineer、Sheriff、Deputy、TimeMaster、Medic、Morphling、Camouflager、Tracker、Vampire、Jackal、Sidekick、Eraser、Trickster、Cleaner/Janitor、Warlock、SecurityGuard、Arsonist、Vulture、Pursuer、Witch、Ninja、Thief、Trapper、Bomber、Yoyo、Shifter，以及合法通风管使用。纯信息/会议技能不会伪造世界状态或绕过 TOR UI。


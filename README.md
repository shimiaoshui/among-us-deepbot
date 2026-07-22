# Among Us DeepBot

面向 Among Us 本地/LAN 房间的房主权威 AI Bot 插件。当前发布版为 `0.9.9-tor46-full-role-recognition`，支持原版模式，并可作为 The Other Roles v4.6.0 汉化版的兼容附加插件。

## 这一版包含什么

- 由房主创建并同步的 DeepBot，客端不自行生成 Bot。
- The Skeld 路径规划、卡死恢复、避障、任务与紧急事件处理。
- 独立的每 Bot 对局记忆、会议讨论、证据推理、投票与赛后去重反思。
- 原版职业能力和 TOR 主动职业能力的情境决策。
- TOR 4.6.0 `RoleId` 全量识别：44 个自定义主职业、2 个原版基础身份、11 种可叠加附加职业，共 57 项；审计结果 `missing=0`、`extra=0`。
- 主职业与附加职业分离：例如豺狼 + 反向操作合法共存，但不会再把职业交换者误当成第二个主职业。
- 房间人数、内鬼数、击杀冷却、任务数和职业规则优先读取实际房间/TOR 设置。

完整安装、房主/客端联机和故障排查见 [完整使用教程](docs/完整使用教程.md)。全职业清单见 [TOR 4.6.0 全职业覆盖](docs/TOR-4.6.0-全职业覆盖.md)。

## 下载选择

- `AmongUs-DeepBot-0.9.9-Standalone.zip`：原版 Among Us + BepInEx 6。
- `AmongUs-DeepBot-0.9.9-TOR46-Compatibility.zip`：已经装好 The Other Roles v4.6.0 汉化版的所有玩家使用。

压缩包不包含 Among Us、BepInEx、The Other Roles 或 API 密钥。请合法取得游戏与第三方依赖。

## 从源码构建

先让 BepInEx 为目标游戏版本生成 `interop` 文件，然后执行：

```powershell
dotnet build .\src\AmongUsDeepSeekBots.csproj -c Release /p:AmongUsDir="D:\steam\steamapps\common\Among Us"
```

`AmongUsDir` 必须指向含 `Among Us.exe`、`BepInEx\core` 和 `BepInEx\interop` 的目录。

## API 密钥

源码和发布包只有密钥读取逻辑，没有预置密钥。用户运行配置脚本后，密钥永久保存在当前 Windows 用户的本地应用数据目录；无需每次输入。没有密钥时 Bot 仍可使用本地后备逻辑，但会议语言能力会下降。

## 兼容与边界

全职业“识别”表示 Bot 能区分身份、阵营、胜利目标、可见信息与附加职业规则。TOR 自己负责最终胜负判定与底层技能合法性；DeepBot 不替换 TOR 的判定器。会议型/信息型能力主要改变推理与投票，带实体效果的能力通过 TOR RPC/规则校验执行。详见覆盖表中的状态说明。


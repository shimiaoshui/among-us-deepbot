# Among Us DeepBot user guide

## 1. Choose the correct installer

Download the installer for this computer and keep its matching uninstaller from the same GitHub Release version:

- `AmongUs-DeepBot-Host-Installer.exe` is for the player who creates the Local/LAN lobby. It installs TOR 4.6.0, DeepBot, host configuration, and the local API-key setup tool.
- `AmongUs-DeepBot-Client-Installer.exe` is for every other human player. It installs the matching runtime and TOR build, but sets `BotCount = 0` and disables local bot decisions.
- `AmongUs-DeepBot-Host-Uninstaller.exe` removes the host package and restores files that existed before installation.
- `AmongUs-DeepBot-Client-Uninstaller.exe` removes the client package and restores files that existed before installation.

Every participant must use the same Among Us, BepInEx, TOR, and DeepBot versions. Do not mix the host and client packages from different releases.

## 2. One-click installation

1. Close Among Us.
2. Run the correct installer.
3. Select either your Steam folder, such as `D:\steam\`, or the exact folder containing `Among Us.exe`.
4. Review the detected target and choose **Install**.
5. Keep the desktop-shortcut option enabled if you want a direct game shortcut.

The installer searches only a small bounded area under `steamapps\common`. It does not install or redistribute the Among Us game itself. Before overwriting an existing mod file, it creates a timestamped copy under `DeepBot Installer Backups` inside the selected game folder. It also writes a local install receipt containing relative paths and SHA-256 hashes; the receipt never contains an API key.

Version 0.10.1 and newer also installs the private `dotnet\coreclr.dll` runtime required by BepInEx IL2CPP and verifies the complete boot chain before reporting success. Use the generated DeepBot desktop shortcut: it checks the runtime and plugins first, so a removed or incomplete mod installation produces a clear error instead of silently launching the vanilla game.

## 3. Host setup

The host should run `Configure-DeepBot-Key.cmd` once from the game folder and enter an OpenAI-compatible API key. The key is stored only at:

```text
%LOCALAPPDATA%\AmongUsDeepSeekBots\api-key.txt
```

It is not stored in the game folder, plugin configuration, installer, repository, or release archive. Clients do not need an API key. DeepBot can run without a key, but meetings and high-level strategy use conservative local fallback behavior.

Start the game, create a Local lobby, and open the TOR lobby options. Set **AI Bot Count** from 1 to 8. Optional per-bot settings allow the host to change each bot's name, color, outfit, and nameplate.

## 4. Lobby rules are authoritative

DeepBot reads the actual room settings. Kill cooldown, impostor count, task counts, Engineer vent limits, role probabilities, role cooldowns, charges, modifiers, and TOR win conditions are not replaced by a second hidden rule set.

Only one primary role is valid for a player. Stackable modifiers such as Lover, Bait, Mini, or Invert may coexist with that primary role.

## 5. Playing over Local/LAN

1. The host creates the Local lobby.
2. Client players install the matching client package and join through the game's Local mode.
3. Only the host creates and drives bots. Clients receive the synchronized players and world state.
4. Start only when every human player reports the same mod versions.

The current release-qualified map is The Skeld. MIRA HQ remains experimental.

## 6. Configuration

The generated plugin configuration is:

```text
BepInEx\config\local.amongus.deepseekbots.cfg
```

Important values include:

- `BotCount`: fallback bot count; the TOR lobby setting takes priority in the compatibility build.
- `Model`: model name for the OpenAI-compatible endpoint.
- `ApiBaseUrl`: endpoint base URL.
- `MeetingUseDeepSeek`: enables model-backed meeting reasoning on the host.
- `BotUseRoleAbilities`: allows bots to choose legal native TOR abilities.
- `PostMatchReflection`: stores deduplicated local lessons after a match.
- `SpeedMultiplier`: movement speed multiplier. Very high values reduce navigation stability.

Never add an API key to this configuration file.

## 7. Verify the installation

After launching once, open `BepInEx\LogOutput.log`. A correct host installation should show:

- `Among Us DeepSeek Bots 0.10.0-skeld-native-tor loaded`
- `customRoles=44, modifiers=11`
- startup self-tests with `level=ok`
- one primary role and a separate modifier list for each bot

The log must never contain the API-key value.

## 8. Troubleshooting

**The game does not start:** remove duplicate or old plugin DLLs from `BepInEx\plugins`, then verify that all players use the same versions.

**Bots do not move:** preserve the complete `BepInEx\LogOutput.log` and note the map, lobby settings, roles, and location. Look for `no route`, `stuck`, `replanning`, or an exception.

**Meeting replies are limited:** check the host endpoint quota and HTTP status in the log. An HTTP 429 means the configured account is rate-limited; local safeguards continue, but model-backed discussion is unavailable until the quota recovers.

**A role ability is unavailable:** confirm that the lobby enabled the role and ability, the native cooldown or charge is ready, the target is legal and in range, and the correct skill stage is active.

**An installer upgrade changed a file:** restore it from the newest timestamped folder under `DeepBot Installer Backups`.

## 9. Remove or roll back

1. Close Among Us.
2. Run the uninstaller that matches the installed package: Host or Client.
3. Select the same Steam root or exact game directory used during installation.
4. Review the detected target and choose **Uninstall**.

The uninstaller reads the local installation receipt. If an installed file replaced an older file, that older file is restored from `DeepBot Installer Backups`. If the file did not previously exist, it is removed. A file changed by the user after installation is preserved and reported instead of being deleted. When no trustworthy receipt exists, the legacy fallback removes only DeepBot-owned files and leaves shared TOR/BepInEx runtime files untouched.

The Host uninstaller offers a separate unchecked option to delete `%LOCALAPPDATA%\AmongUsDeepSeekBots\api-key.txt`. Post-match evolution data is not deleted automatically. To roll back without uninstalling, restore the desired files from `DeepBot Installer Backups` or from your own full game backup.

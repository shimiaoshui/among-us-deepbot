# Security

- The repository, installers, release archives, and project website must never contain a real API key.
- A host enters a key locally with `Configure-DeepBot-Key.cmd`. It is stored at `%LOCALAPPDATA%\AmongUsDeepSeekBots\api-key.txt`.
- That file is outside the game directory and must not be attached to archives, screenshots, logs, or bug reports.
- If a key was ever exposed publicly, revoke it with the provider immediately and create a new one.
- Security reports should include only a redacted `BepInEx\LogOutput.log`.

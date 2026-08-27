# Among Us DeepBot 0.10.22

This release ships a matched Host/Client set for the fingerprinted TOR 4.6.0 compatibility build.

## What changed

- Meeting chat is model-authored only. Empty, unavailable, or safety-rejected model output stays silent instead of falling back to a stock sentence.
- Hidden hostile bots can use audited tactical deception, including soft frames, false routes, false visible actions, and limited false-witness plays. Native rules and ally/privacy guards remain authoritative.
- Passive clients no longer create duplicate local client rows for host-created bots, preventing TOR duplicate-key update failures and heartbeat starvation.
- Installers fingerprint both `Among Us.exe` and `GameAssembly.dll` and reject a mismatched base-game build before changing files.
- The Client remains presentation-only with `BotCount = 0`; only the Host controls bot simulation.
- Includes accumulated fixes for local-player ownership, remote kill presentation, role-button isolation, late joins, native TOR abilities, delayed Vampire death, meeting continuity, and voice dictation.

## Downloads

- `AmongUs-DeepBot-Host-Installer.exe` — install on the computer that creates the lobby and runs the bots.
- `AmongUs-DeepBot-Client-Installer.exe` — install on every joining computer.
- The two matching uninstallers remove their respective package and restore recorded originals where available.

Host and Client must use this same release and the same Among Us base build. Fully close the game before installing, then launch through the mode-specific desktop shortcut created by the installer.

The Host installer accepts an OpenAI-compatible API endpoint and key. The key is stored only in the current Windows user's local application-data folder. No API key is embedded in these files.

## SHA-256

```text
3EEB0C48951B397A567A5F8202AD1126BA138D8244B69373604A5B8BD7E62874  AmongUs-DeepBot-Host-Installer.exe
BA926FB9F3D40BE1255BE5E11280D3F43C3DC1091885F5A3638F5F33237A80DC  AmongUs-DeepBot-Host-Uninstaller.exe
F4A76F8A28505D72C93A6F14FB1029201155E98C01573E92F020796B2F85A883  AmongUs-DeepBot-Client-Installer.exe
AC3A2418D1221035BED9BB6703639C17DF3F3B97F90DA5D49DE2F0084A42FFC8  AmongUs-DeepBot-Client-Uninstaller.exe
```

Validation completed: source build, clean Host/Client install, guarded launcher checks, Host/Client fingerprint parity, wrong-base-build rejection, API-key boundary, Steam-root target selection, and clean uninstall.

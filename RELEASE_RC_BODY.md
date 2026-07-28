This prerelease fixes two LAN regressions found during two-machine testing:

- Host-created bots are now visible on passive clients. The client reconstructs presentation-only entries for reserved bot clients and binds them to the host-spawned network objects.
- Bot identity and lobby appearance updates are stable, preventing human-input mirroring, repeated name RPCs, and heartbeat timeouts.

Install both Host and Client from this same prerelease. Existing `0.10.3` clients must upgrade for bot visibility.

Validation performed before publishing:

- DeepBot build: 0 errors, 0 warnings
- Host and Client compatibility fingerprints match
- Clean Host and Client installation succeeds
- Guarded launcher validation succeeds
- Steam-root selection prefers the active TOR installation
- Host and Client uninstallation leaves no managed files
- API endpoint and API-key boundary tests pass

Runtime promotion condition: the client log should report `reservedInfos=8`, `matchedControls=8`, and `proxyClients=8`, followed by visible host-synchronized bots. This asset remains a prerelease until that two-machine check passes.

No API key is included in the repository or release assets.

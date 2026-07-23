using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class SafeLocalBotSpawner
{
    private static readonly MethodInfo? CreatePlayerMethod =
        AccessTools.Method(typeof(AmongUsClient), "CreatePlayer");

    private static readonly string[] SpawnNodeIds =
    [
        "CAF_SPAWN",
        "CAF_TABLE_N",
        "CAF_UL",
        "CAF_UR",
        "CAF_BOTTOM"
    ];

    private readonly ManualLogSource _log;
    private readonly List<TrackedBotClient> _tracked = [];
    private readonly HashSet<byte> _visibilityRestored = [];
    private readonly HashSet<byte> _renderDiagnosticsLogged = [];
    private readonly HashSet<byte> _disabledBotLightIds = [];
    private readonly Dictionary<int, string> _appliedLobbyAppearances = [];
    private float _nextSpawnAt;
    private float _nextStatusAt;
    private float _nextGuestStatusAt;
    private bool _spawnBlocked;
    private bool _hostLightRepairLogged;
    private PlayerControl? _hostPlayer;

    public SafeLocalBotSpawner(ManualLogSource log)
    {
        _log = log;
    }

    public void Tick(PluginConfig config)
    {
        var client = AmongUsClient.Instance;
        if (IsPassiveLanGuest(client))
        {
            if (Time.time >= _nextGuestStatusAt)
            {
                _nextGuestStatusAt = Time.time + 15f;
                _log.LogInfo($"DeepBot LAN guest passive mode: clientId={client.ClientId}, hostId={client.HostId}, clients={client.allClients.Count}. Host controls AI bots.");
            }

            ResetTransientState();
            return;
        }

        if (!IsLocalHostReady(client, out var reason))
        {
            if (Time.time >= _nextStatusAt)
            {
                _nextStatusAt = Time.time + 8f;
                _log.LogInfo($"DeepBot spawn preflight waiting: {reason}");
            }

            ResetTransientState();
            return;
        }

        var targetCount = TorRoleAdapter.GetLobbyConfiguredBotCount(config.LocalBotCount.Value);
        CaptureHostPlayer(client);
        var existingCount = CountManagedClients(client);
        if (client.GameState != InnerNetClient.GameStates.Started && existingCount > targetCount)
        {
            PruneExcessLobbyBots(client, targetCount);
            existingCount = CountManagedClients(client);
        }
        ConfigureTrackedClients(client);
        if (client.GameState != InnerNetClient.GameStates.Started)
        {
            RefreshLobbyAppearances(client);
        }
        EnsureHostLocalPlayer(client, "spawner tick");

        if (Time.time >= _nextStatusAt)
        {
            _nextStatusAt = Time.time + 8f;
            _log.LogInfo($"DeepBot spawn preflight ok: dryRun={config.DryRun.Value}, target={targetCount}, existing={existingCount}, clients={client.allClients.Count}, gameState={client.GameState}.");
        }

        if (config.DryRun.Value || _spawnBlocked || existingCount >= targetCount || Time.time < _nextSpawnAt)
        {
            return;
        }

        _nextSpawnAt = Time.time + 0.75f;
        TryCreateOne(client, FindNextBotIndex(client, targetCount));
    }

    private void PruneExcessLobbyBots(AmongUsClient client, int targetCount)
    {
        var managed = new List<(int ClientListIndex, ClientData Client, int BotIndex)>();
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is null || candidate.Id == client.ClientId)
            {
                continue;
            }

            if (DeepBotIdentity.TryGetBotIndex(candidate, out var botIndex))
            {
                managed.Add((i, candidate, botIndex));
            }
        }

        foreach (var item in managed
                     .OrderByDescending(item => item.BotIndex)
                     .ThenByDescending(item => item.ClientListIndex)
                     .Take(Math.Max(0, managed.Count - targetCount))
                     .OrderByDescending(item => item.ClientListIndex))
        {
            if (item.Client.Character)
            {
                UnityEngine.Object.Destroy(item.Client.Character.gameObject);
            }
            client.allClients.RemoveAt(item.ClientListIndex);
            _tracked.RemoveAll(tracked => tracked.ClientId == item.Client.Id);
            _appliedLobbyAppearances.Remove(item.Client.Id);
            _log.LogInfo($"DeepBot lobby roster reduced: removed=DeepBot {item.BotIndex + 1}, target={targetCount}.");
        }
    }

    public void MaintainHostLocalView()
    {
        var client = AmongUsClient.Instance;
        if (client is null || !client || !client.AmHost || client.ClientId < 0)
        {
            return;
        }

        CaptureHostPlayer(client);
        EnsureHostLocalPlayer(client, "runtime frame");
        EnsureHostCameraTarget(client, "runtime frame");
        EnsureHostVisionLight(client, "runtime frame");
    }

    private void TryCreateOne(AmongUsClient client, int botIndex)
    {
        if (CreatePlayerMethod is null)
        {
            _spawnBlocked = true;
            _log.LogError("DeepBot spawn blocked: AmongUsClient.CreatePlayer was not found.");
            return;
        }

        var clientId = FindAvailableClientId(client);
        if (clientId < 0)
        {
            _spawnBlocked = true;
            _log.LogError("DeepBot spawn blocked: no free reserved client id in 64..95.");
            return;
        }

        var appearance = TorRoleAdapter.GetLobbyAppearance(botIndex);
        var displayName = ResolveUniqueLobbyName(botIndex, appearance.NameSelection);
        var platform = new PlatformSpecificData
        {
            Platform = Platforms.StandaloneSteamPC,
            PlatformName = "DeepSeek Bot"
        };

        var botClient = new ClientData(clientId, displayName, platform, 5u, string.Empty, string.Empty)
        {
            InScene = true,
            IsReady = true,
            IsBeingCreated = false
        };

        try
        {
            client.allClients.Add(botClient);
            var created = CreatePlayerMethod.Invoke(client, [botClient]);
            StartCreatePlayerCoroutine(client, created, botClient);

            // Unity starts a coroutine immediately up to its first yield. The
            // game's CreatePlayer routine can therefore replace LocalPlayer and
            // the camera target before the next runtime Update. Restore both in
            // the same call stack so no bot-view frame reaches the renderer.
            EnsureHostLocalPlayer(client, $"queued {displayName}");
            EnsureHostCameraTarget(client, $"queued {displayName}");

            _tracked.Add(new TrackedBotClient(clientId, botIndex, botClient));
            _log.LogInfo($"DeepBot queued local bot: index={botIndex + 1}, clientId={clientId}, hostClientId={client.ClientId}, clients={client.allClients.Count}.");
        }
        catch (Exception ex)
        {
            client.allClients.Remove(botClient);
            _spawnBlocked = true;
            _log.LogError($"DeepBot spawn failed for {displayName} ({clientId}): {ex}");
        }
    }

    private void ConfigureTrackedClients(AmongUsClient client)
    {
        for (var i = _tracked.Count - 1; i >= 0; i--)
        {
            var tracked = _tracked[i];
            var character = tracked.Client.Character;
            if (!character || character.Data is null || !character.MyPhysics || !character.NetTransform)
            {
                continue;
            }

            var appearance = TorRoleAdapter.GetLobbyAppearance(tracked.Index);
            var name = ResolveUniqueLobbyName(tracked.Index, appearance.NameSelection);
            tracked.Client.InScene = true;
            tracked.Client.IsReady = true;
            tracked.Client.IsBeingCreated = false;
            tracked.Client.PlayerName = name;

            character.isDummy = false;
            character.Data.PlayerName = name;
            character.Data.Disconnected = false;
            character.Data.IsDead = false;
            character.SetName(name);
            character.RpcSetName(name);

            var color = DeepBotAppearance.ResolveColor(tracked.Index, appearance.ColorSelection);
            character.SetColor(color);
            character.RpcSetColor((byte)color);
            character.NetTransform.SnapTo(GetSpawnPoint(tracked.Index));

            AssignRuntimeOwnership(client, character, tracked.ClientId);
            EnsureHostLocalPlayer(client, $"configured {name}");
            EnsureHostCameraTarget(client, $"configured {name}");

            _log.LogInfo(
                $"DeepBot local bot ready: player={character.PlayerId}, client={tracked.ClientId}, " +
                $"owner={character.OwnerId}, netOwner={character.NetTransform.OwnerId}, physicsOwner={character.MyPhysics.OwnerId}, name={character.Data.PlayerName}.");

            _tracked.RemoveAt(i);
        }
    }

    private void RefreshLobbyAppearances(AmongUsClient client)
    {
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (!DeepBotIdentity.TryGetBotIndex(candidate, out var botIndex) ||
                candidate.Character is not { } character ||
                !character ||
                character.Data is null)
            {
                continue;
            }

            var appearance = TorRoleAdapter.GetLobbyAppearance(botIndex);
            var name = ResolveUniqueLobbyName(botIndex, appearance.NameSelection);
            var color = DeepBotAppearance.ResolveColor(botIndex, appearance.ColorSelection);
            var gameId = appearance.OutfitSelection == 3 || appearance.NamePlateSelection == 3 ? client.GameId : 0;
            var signature = $"{name}:{appearance.NameSelection}:{appearance.ColorSelection}:{appearance.OutfitSelection}:{appearance.NamePlateSelection}:{gameId}";
            if (_appliedLobbyAppearances.TryGetValue(candidate.Id, out var applied) && applied == signature)
            {
                continue;
            }

            candidate.PlayerName = name;
            character.Data.PlayerName = name;
            character.SetName(name);
            character.RpcSetName(name);
            character.SetColor(color);
            character.RpcSetColor((byte)color);
            DeepBotAppearance.ApplyOutfit(character, _hostPlayer, botIndex, appearance.OutfitSelection, _log);
            DeepBotAppearance.ApplyNamePlate(character, _hostPlayer, botIndex, appearance.NamePlateSelection, _log);
            _appliedLobbyAppearances[candidate.Id] = signature;
            _log.LogInfo(
                $"DeepBot lobby appearance applied: bot={botIndex + 1}, client={candidate.Id}, " +
                $"name={name}, color={color}, outfit={appearance.OutfitSelection}, nameplate={appearance.NamePlateSelection}.");
        }
    }

    private static string ResolveUniqueLobbyName(int botIndex, int nameSelection)
    {
        var configuredName = DeepBotAppearance.ResolveName(botIndex, nameSelection);
        var collidesWithEarlierBot = false;
        for (var earlierIndex = 0; earlierIndex < botIndex; earlierIndex++)
        {
            var earlierAppearance = TorRoleAdapter.GetLobbyAppearance(earlierIndex);
            var earlierName = DeepBotAppearance.ResolveName(earlierIndex, earlierAppearance.NameSelection);
            if (string.Equals(earlierName, configuredName, StringComparison.OrdinalIgnoreCase))
            {
                collidesWithEarlierBot = true;
                break;
            }
        }

        var collidesWithHuman = false;
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player &&
                player.Data is not null &&
                !DeepBotIdentity.IsBot(player) &&
                string.Equals(player.Data.PlayerName, configuredName, StringComparison.OrdinalIgnoreCase))
            {
                collidesWithHuman = true;
                break;
            }
        }
        return collidesWithEarlierBot || collidesWithHuman
            ? $"{configuredName} {botIndex + 1}"
            : configuredName;
    }

    private static void StartCreatePlayerCoroutine(AmongUsClient client, object? created, ClientData botClient)
    {
        if (created is Il2CppSystem.Collections.IEnumerator il2CppEnumerator)
        {
            client.StartCoroutine(il2CppEnumerator);
            return;
        }

        if (created is System.Collections.IEnumerator enumerator)
        {
            client.StartCoroutine(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(enumerator));
            return;
        }

        throw new InvalidOperationException($"CreatePlayer returned unsupported value for {botClient.Id}: {created?.GetType().FullName ?? "null"}.");
    }

    private static bool IsLocalHostReady(AmongUsClient? client, out string reason)
    {
        if (client is null || !client)
        {
            reason = "AmongUsClient missing";
            return false;
        }

        if (client.NetworkMode != NetworkModes.LocalGame)
        {
            reason = $"networkMode={client.NetworkMode}";
            return false;
        }

        if (!GameRuleSettings.IsSkeldMap())
        {
            reason = $"mapId={GameRuleSettings.GetMapId()} (Skeld only)";
            return false;
        }

        if (!client.AmHost || client.ClientId < 0 || client.HostId != client.ClientId)
        {
            reason = $"not local host: amHost={client.AmHost}, clientId={client.ClientId}, hostId={client.HostId}";
            return false;
        }

        if (!PlayerControl.LocalPlayer || PlayerControl.LocalPlayer.Data is null || !PlayerControl.LocalPlayer.MyPhysics || !PlayerControl.LocalPlayer.NetTransform)
        {
            reason = "local PlayerControl not ready";
            return false;
        }

        if (!GameData.Instance || !GameManager.Instance)
        {
            reason = "GameData/GameManager not ready";
            return false;
        }

        if (!LobbyBehaviour.Instance && client.GameState != InnerNetClient.GameStates.Started)
        {
            reason = $"lobby not ready and gameState={client.GameState}";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool IsPassiveLanGuest(AmongUsClient? client)
    {
        return client is not null &&
            client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            !client.AmHost &&
            client.ClientId >= 0;
    }

    private void CaptureHostPlayer(AmongUsClient client)
    {
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is null ||
                candidate.Id != client.ClientId ||
                !candidate.Character ||
                candidate.Character.Data is null ||
                DeepBotIdentity.IsBot(candidate.Character))
            {
                continue;
            }

            _hostPlayer = candidate.Character;
            return;
        }

        var local = PlayerControl.LocalPlayer;
        if (local &&
            local.Data is not null &&
            !DeepBotIdentity.IsBot(local) &&
            local.OwnerId == client.ClientId)
        {
            _hostPlayer = local;
        }
    }

    private void EnsureHostLocalPlayer(AmongUsClient client, string reason)
    {
        var host = _hostPlayer;
        if (host is null ||
            !host ||
            host.Data is null ||
            host.OwnerId != client.ClientId ||
            DeepBotIdentity.IsBot(host))
        {
            CaptureHostPlayer(client);
            host = _hostPlayer;
        }

        if (host is null || !host || PlayerControl.LocalPlayer == host)
        {
            return;
        }

        var replaced = PlayerControl.LocalPlayer;
        PlayerControl.LocalPlayer = host;
        _log.LogWarning(
            $"DeepBot restored host LocalPlayer: reason={reason}, " +
            $"from={replaced?.Data?.PlayerName ?? "missing"}({replaced?.PlayerId.ToString() ?? "none"}), " +
            $"to={host.Data?.PlayerName ?? "host"}({host.PlayerId}), hostClientId={client.ClientId}.");
    }

    private void EnsureHostCameraTarget(AmongUsClient client, string reason)
    {
        var host = _hostPlayer;
        if (host is null || !host || host.Data is null || host.OwnerId != client.ClientId)
        {
            return;
        }

        if (!DestroyableSingleton<HudManager>.InstanceExists)
        {
            return;
        }

        var camera = DestroyableSingleton<HudManager>.Instance.PlayerCam;
        if (!camera)
        {
            return;
        }

        var replaced = camera.Target;
        var replacedPlayer = replaced ? replaced.TryCast<PlayerControl>() : null;
        var recoverMissingOrBotTarget =
            !replaced ||
            (replacedPlayer is not null &&
             replacedPlayer &&
             replacedPlayer.Data is not null &&
                DeepBotIdentity.IsBot(replacedPlayer));
        if (!recoverMissingOrBotTarget)
        {
            return;
        }

        camera.SetTarget(host);
        var unlockRecoveredCamera =
            client.GameState == InnerNetClient.GameStates.Started &&
            ShipStatus.Instance &&
            camera.Locked;
        if (unlockRecoveredCamera)
        {
            camera.Locked = false;
        }

        camera.SnapToTarget();
        _log.LogWarning(
            $"DeepBot restored host camera: reason={reason}, recoveredMissingOrBotTarget={recoverMissingOrBotTarget}, unlockedRecoveredCamera={unlockRecoveredCamera}, " +
            $"from={DescribeCameraTarget(replaced)}, to={host.Data.PlayerName}({host.PlayerId}).");
    }

    private static string DescribeCameraTarget(MonoBehaviour? target)
    {
        if (!target)
        {
            return "missing";
        }

        var activeTarget = target!;
        var player = activeTarget.TryCast<PlayerControl>();
        if (player is not null && player && player.Data is { } data)
        {
            return $"{data.PlayerName}({player.PlayerId})";
        }

        return activeTarget.GetType().Name;
    }

    private void EnsureHostVisionLight(AmongUsClient client, string reason)
    {
        if (client.GameState != InnerNetClient.GameStates.Started || !ShipStatus.Instance)
        {
            _disabledBotLightIds.Clear();
            _hostLightRepairLogged = false;
            return;
        }

        var host = _hostPlayer;
        if (host is null ||
            !host ||
            host.Data is null ||
            host.OwnerId != client.ClientId ||
            DeepBotIdentity.IsBot(host))
        {
            return;
        }

        var lightWasMissing = !host.lightSource;
        if (lightWasMissing)
        {
            host.AdjustLighting();
        }

        var hostLight = host.lightSource;
        if (hostLight)
        {
            var targetTransform = host.transform;
            var lightTransform = hostLight.transform;
            var parentChanged = lightTransform.parent != targetTransform;
            var wasInactive = !hostLight.gameObject.activeSelf || !hostLight.enabled;
            var expectedWorldPosition = targetTransform.TransformPoint(hostLight.LightOffset);
            var positionDrifted = Vector3.Distance(lightTransform.position, expectedWorldPosition) > 0.08f;

            if (parentChanged)
            {
                lightTransform.SetParent(targetTransform, false);
            }

            if (parentChanged || positionDrifted)
            {
                lightTransform.localPosition = hostLight.LightOffset;
            }

            if (wasInactive)
            {
                hostLight.gameObject.SetActive(true);
                hostLight.enabled = true;
            }

            // Do not call AdjustLighting every frame. Reinitializing the native
            // shadow mesh while crossing a doorway can leave a stale wide wedge.
            // Only rebuild a broken binding, then keep the radius aligned with
            // ShipStatus so room vision settings and a live lights sabotage win.
            if (parentChanged || wasInactive)
            {
                host.AdjustLighting();
                hostLight = host.lightSource;
                if (!hostLight)
                {
                    return;
                }
            }

            var expectedRadius = ShipStatus.Instance.CalculateLightRadius(host.Data);
            var actualRadius = hostLight ? hostLight.ViewDistance : float.NaN;
            var radiusRepaired = hostLight &&
                BotBehaviorPolicy.ShouldRepairVisionDistance(actualRadius, expectedRadius);
            if (radiusRepaired)
            {
                hostLight.SetViewDistance(expectedRadius);
            }

            if (!_hostLightRepairLogged &&
                (lightWasMissing || parentChanged || wasInactive || positionDrifted || radiusRepaired))
            {
                _hostLightRepairLogged = true;
                _log.LogWarning(
                    $"DeepBot restored host vision light: reason={reason}, player={host.Data.PlayerName}({host.PlayerId}), " +
                    $"missing={lightWasMissing}, parentChanged={parentChanged}, wasInactive={wasInactive}, " +
                    $"positionDrifted={positionDrifted}, radiusRepaired={radiusRepaired}, " +
                    $"radius={actualRadius:0.00}->{expectedRadius:0.00}, offset={hostLight.LightOffset}.");
            }
        }

        for (var i = 0; i < client.allClients.Count; i++)
        {
            var character = client.allClients[i]?.Character;
            if (character is null ||
                !character ||
                character == host ||
                character.Data is null ||
                !DeepBotIdentity.IsBot(character))
            {
                continue;
            }

            var botLight = character.lightSource;
            if (!botLight)
            {
                continue;
            }

            var wasActive = botLight.gameObject.activeSelf || botLight.enabled;
            botLight.enabled = false;
            botLight.gameObject.SetActive(false);

            if (wasActive && _disabledBotLightIds.Add(character.PlayerId))
            {
                _log.LogWarning(
                    $"DeepBot disabled bot vision light: player={character.Data.PlayerName}({character.PlayerId}), " +
                    $"host={host.Data.PlayerName}({host.PlayerId}).");
            }
        }
    }

    private void EnsureLivePlayersVisible(AmongUsClient client)
    {
        if (client.GameState != InnerNetClient.GameStates.Started || !ShipStatus.Instance)
        {
            _visibilityRestored.Clear();
            return;
        }

        for (var i = 0; i < client.allClients.Count; i++)
        {
            var character = client.allClients[i]?.Character;
            if (character is null ||
                !character ||
                character.Data is null ||
                character.Data.IsDead ||
                character.Data.Disconnected)
            {
                continue;
            }

            var legitimatePhantomInvisibility =
                character.Data.RoleType == RoleTypes.Phantom &&
                character.shouldAppearInvisible;
            if (legitimatePhantomInvisibility)
            {
                continue;
            }

            var cosmetics = character.cosmetics;
            var body = cosmetics ? cosmetics.currentBodySprite : null;
            var renderer = body?.BodySprite;
            var cosmeticsMissing = cosmetics is null || !cosmetics;
            var rendererMissing = renderer is null || !renderer;
            var phantomAlpha = 1f;
            if (!cosmeticsMissing)
            {
                try
                {
                    phantomAlpha = cosmetics!.GetPhantomRoleAlpha();
                }
                catch
                {
                    // Older game builds may not expose the phantom material state.
                }
            }

            if (_renderDiagnosticsLogged.Add(character.PlayerId))
            {
                var rendererState = rendererMissing
                    ? "missing"
                    : $"sprite={(renderer!.sprite ? renderer.sprite.name : "missing")},layer={renderer.gameObject.layer}," +
                      $"sorting={renderer.sortingLayerName}/{renderer.sortingOrder},pos={renderer.transform.position}," +
                      $"scale={renderer.transform.lossyScale},bounds={renderer.bounds.center}/{renderer.bounds.size}," +
                      $"active={renderer.gameObject.activeInHierarchy},enabled={renderer.enabled},alpha={renderer.color.a:0.00}";
                _log.LogWarning(
                    $"DeepBot live render diagnostic: player={character.Data.PlayerName}({character.PlayerId}), " +
                    $"role={character.Data.RoleType},owner={character.OwnerId},local={character == _hostPlayer}," +
                    $"rootPos={character.transform.position},truePos={character.GetTruePosition()},rootLayer={character.gameObject.layer}," +
                    $"scene={character.gameObject.scene.name},dummy={character.isDummy},notReal={character.notRealPlayer}," +
                    $"moveable={character.moveable},visible={character.Visible},invisibleFlag={character.shouldAppearInvisible}," +
                    $"bodyType={character.BodyType},cosmeticsVisible={(cosmeticsMissing ? "missing" : cosmetics!.Visible.ToString())}," +
                    $"cosmeticsBodyType={(cosmeticsMissing ? "missing" : cosmetics!.bodyType.ToString())},phantomAlpha={phantomAlpha:0.00}," +
                    $"normalBody={(cosmeticsMissing || cosmetics!.normalBodySprite is null ? "missing" : cosmetics.normalBodySprite.Visible.ToString())}," +
                    $"usingNormalBody={(cosmeticsMissing ? "missing" : (cosmetics!.currentBodySprite == cosmetics.normalBodySprite).ToString())}," +
                    $"bodyVisible={body?.Visible.ToString() ?? "missing"}," +
                    $"renderer=[{rendererState}].");
            }

            var needsRestore =
                !_visibilityRestored.Contains(character.PlayerId) ||
                !character.gameObject.activeInHierarchy ||
                !character.enabled ||
                !character.Visible ||
                character.shouldAppearInvisible ||
                (int)character.BodyType != 0 ||
                cosmeticsMissing ||
                !cosmetics!.gameObject.activeInHierarchy ||
                !cosmetics.enabled ||
                !cosmetics.Visible ||
                (int)cosmetics.bodyType != 0 ||
                phantomAlpha < 0.95f ||
                (cosmetics.normalBodySprite is not null && cosmetics.currentBodySprite != cosmetics.normalBodySprite) ||
                (cosmetics.normalBodySprite is not null && !cosmetics.normalBodySprite.Visible) ||
                body is null ||
                !body.Visible ||
                rendererMissing ||
                !renderer!.gameObject.activeInHierarchy ||
                !renderer.enabled ||
                renderer.color.a < 0.95f;
            if (!needsRestore)
            {
                continue;
            }

            var before =
                $"role={character.Data.RoleType},invisibleFlag={character.shouldAppearInvisible}," +
                $"playerActive={character.gameObject.activeInHierarchy},playerEnabled={character.enabled},playerVisible={character.Visible}," +
                $"cosmetics={(!cosmeticsMissing ? $"active={cosmetics!.gameObject.activeInHierarchy},enabled={cosmetics.enabled},visible={cosmetics.Visible}" : "missing")}," +
                $"body={(body is not null ? $"visible={body.Visible}" : "missing")}," +
                $"renderer={(!rendererMissing ? $"active={renderer!.gameObject.activeInHierarchy},enabled={renderer.enabled},alpha={renderer.color.a:0.00}" : "missing")}";

            RestoreAliveVisuals(character);

            _visibilityRestored.Add(character.PlayerId);
            _log.LogWarning(
                $"DeepBot restored live player rendering: player={character.Data.PlayerName}({character.PlayerId}), " +
                $"owner={character.OwnerId}, local={character == _hostPlayer}, before=[{before}].");
        }
    }

    private static void RestoreAliveVisuals(PlayerControl player)
    {
        player.gameObject.SetActive(true);
        player.enabled = true;
        player.Visible = true;
        player.SetInvisibility(false);
        player.SetRoleInvisibility(false, false, false);
        player.SetHatAndVisorAlpha(1f);

        var colorId = player.Data?.DefaultOutfit.ColorId ?? player.CurrentOutfit.ColorId;
        var cosmetics = player.cosmetics;
        if (cosmetics)
        {
            cosmetics.gameObject.SetActive(true);
            cosmetics.enabled = true;
            cosmetics.Visible = true;
            cosmetics.SetForcedVisible(true);
            cosmetics.SetBodyCosmeticsVisible(true);
            cosmetics.SetPhantomRoleAlpha(1f);
            cosmetics.SetColor(colorId);
            cosmetics.SetBodyColor(colorId);
            cosmetics.UpdateBodyMaterial();
            RestoreNormalBodySprite(cosmetics, colorId);
            cosmetics.UpdateVisibility();
        }

        if (player.MyPhysics)
        {
            player.MyPhysics.SetBodyType((PlayerBodyTypes)0);
            player.MyPhysics.ResetAnimState();
        }
    }

    private static void RestoreNormalBodySprite(CosmeticsLayer cosmetics, int colorId)
    {
        cosmetics.EnsureInitialized((PlayerBodyTypes)0);
        cosmetics.bodyType = (PlayerBodyTypes)0;
        cosmetics.alwaysDrawNormalPlayer = true;

        var normalBody = cosmetics.normalBodySprite;
        if (normalBody is null)
        {
            return;
        }

        cosmetics.currentBodySprite = normalBody;
        normalBody.Visible = true;
        var renderer = normalBody.BodySprite;
        if (renderer)
        {
            renderer.gameObject.SetActive(true);
            renderer.enabled = true;
            var color = renderer.color;
            color.a = 1f;
            renderer.color = color;
            PlayerMaterial.SetColors(colorId, renderer);
        }

        foreach (var candidate in cosmetics.bodySprites)
        {
            if (candidate is not null && candidate != normalBody)
            {
                candidate.Visible = false;
            }
        }

        cosmetics.UpdateBodyMaterial();
    }

    private int CountManagedClients(AmongUsClient client)
    {
        var managedIds = new HashSet<int>();
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is not null &&
                candidate.Id != client.ClientId &&
                DeepBotIdentity.IsBot(candidate))
            {
                managedIds.Add(candidate.Id);
            }
        }

        foreach (var tracked in _tracked)
        {
            managedIds.Add(tracked.ClientId);
        }

        return managedIds.Count;
    }

    private int FindNextBotIndex(AmongUsClient client, int targetCount)
    {
        var occupied = new HashSet<int>();
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is null || candidate.Id == client.ClientId)
            {
                continue;
            }

            if (DeepBotIdentity.TryGetBotIndex(candidate, out var index))
            {
                occupied.Add(index);
            }
        }

        foreach (var tracked in _tracked)
        {
            occupied.Add(tracked.Index);
        }

        for (var index = 0; index < targetCount; index++)
        {
            if (!occupied.Contains(index))
            {
                return index;
            }
        }

        return occupied.Count == 0 ? 0 : occupied.Max() + 1;
    }

    private static int FindAvailableClientId(AmongUsClient client)
    {
        for (var id = DeepBotIdentity.ReservedClientIdStart; id <= DeepBotIdentity.ReservedClientIdEnd; id++)
        {
            var used = false;
            for (var i = 0; i < client.allClients.Count; i++)
            {
                if (client.allClients[i]?.Id == id)
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                return id;
            }
        }

        return -1;
    }

    private static void AssignRuntimeOwnership(AmongUsClient client, PlayerControl player, int virtualClientId)
    {
        player.OwnerId = virtualClientId;
        player.NetTransform.OwnerId = client.ClientId;
        player.MyPhysics.OwnerId = client.ClientId;
    }

    private static Vector2 GetSpawnPoint(int index)
    {
        var nodeId = SpawnNodeIds[Mathf.Abs(index) % SpawnNodeIds.Length];
        return SkeldPathGraph.Instance.FindNode(nodeId)?.Position ?? SkeldPathGraph.Instance.NearestNode(Vector2.zero).Position;
    }

    private void ResetTransientState()
    {
        _tracked.Clear();
        _spawnBlocked = false;
        _nextSpawnAt = 0f;
        _hostPlayer = null;
        _visibilityRestored.Clear();
        _renderDiagnosticsLogged.Clear();
        _disabledBotLightIds.Clear();
        _appliedLobbyAppearances.Clear();
        _hostLightRepairLogged = false;
    }

    private sealed record TrackedBotClient(int ClientId, int Index, ClientData Client);
}

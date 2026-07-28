using System;
using System.Linq;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AmongUsDeepSeekBots;

public sealed class DeepBotRuntime : MonoBehaviour
{
    private ManualLogSource _log = null!;
    private SafeLocalBotSpawner _spawner = null!;
    private DeepSeekDecisionClient _deepSeek = null!;
    private BotEvolutionSkillStore _evolutionSkills = null!;
    private BotMatchMemory _memory = null!;
    private BotEvolutionDirector _evolution = null!;
    private BotActionDirector _director = null!;
    private BotSocialDirector _social = null!;
    private BotAbilityDirector _abilities = null!;
    private HostRoleControlGuard _hostRoleControls = null!;
    private float _nextTickAt;
    private float _nextHostUiIsolationLogAt;
    private string _lastScene = string.Empty;
    private bool _started;
    private bool _passiveClientLogged;

    public DeepBotRuntime(IntPtr ptr) : base(ptr)
    {
    }

    private void Start()
    {
        _log = Plugin.LogSource;
        _spawner = new SafeLocalBotSpawner(_log);
        _deepSeek = new DeepSeekDecisionClient(
            () => Plugin.Settings.Model.Value,
            () => Plugin.Settings.ApiBaseUrl.Value,
            DeepSeekDecisionClient.LoadHostApiKey,
            message => _log.LogWarning(message));
        _evolutionSkills = new BotEvolutionSkillStore(_log);
        _memory = new BotMatchMemory(_log, _evolutionSkills);
        _evolution = new BotEvolutionDirector(_log, _deepSeek, _memory, _evolutionSkills);
        _director = new BotActionDirector(_log, _deepSeek, _memory);
        _social = new BotSocialDirector(_log, _deepSeek, _memory);
        _abilities = new BotAbilityDirector(_log, _memory, _director, _deepSeek);
        TorRoleAdapter.Initialize(_log);
        TorRoleAdapter.LogRoleCoverageSelfTest(_log);
        _hostRoleControls = new HostRoleControlGuard(_log);
        Plugin.Runtime = this;
        _started = true;
        SkeldPathGraph.Instance.LogSupportedMapSelfTests(_log);
        SkeldPathGraph.Instance.LogStaticSelfTest(_log);
        BotBehaviorPolicy.LogSelfTest(_log);
        BotMatchMemory.LogSelfTest(_log);
        TorLocalRoleChatPolicy.LogSelfTest(_log);
        ObservedTorActionRpcPatch.LogSelfTest(_log);
        VampireDelayedDeathPositionPatch.LogSelfTest(_log);
        SafeLocalBotSpawner.LogLobbySpawnSelfTest(_log);
        BotActionDirector.LogMiraDeconRecoverySelfTest(_log);
        BotActionDirector.LogPostMeetingMovementRecoverySelfTest(_log);
        BotActionDirector.LogMurderPlanningSelfTest(_log);
        TorIntroPresentationPatch.LogSelfTest(_log);
        BotSocialDirector.LogSelfTest(_log);
        BotAbilityDirector.LogSelfTest(_log);
        DeepSeekDecisionClient.LogSelfTest(_log);
        SmoothTaskProgressPatch.LogSelfTest(_log);
        _log.LogInfo($"DeepBotRuntime started. graph={SkeldPathGraph.Instance.Summary}, hostKey={(DeepSeekDecisionClient.LoadHostApiKey() is null ? "missing" : "configured")}.");
    }

    private void Update()
    {
        if (!_started || !Plugin.Settings.Enabled.Value)
        {
            return;
        }

        // A LAN client is a renderer/receiver only.  Keeping the authority
        // check at the runtime boundary prevents any client-side subsystem
        // from changing bot physics, role state, position holds, meetings, or
        // camera ownership even if an individual adapter forgets its own
        // host guard.
        if (!HasHostAuthority())
        {
            // Passive LAN peers must never run bot decisions or physics, but
            // they still need a local ClientData/PlayerControl presentation for
            // host-created virtual clients that are not real transport peers.
            _spawner.MaintainPassiveClientView();
            if (!_passiveClientLogged && AmongUsClient.Instance)
            {
                _passiveClientLogged = true;
                _log.LogInfo(
                    $"DeepBot passive client mode active: clientId={AmongUsClient.Instance.ClientId}, " +
                    $"hostId={AmongUsClient.Instance.HostId}; world control disabled.");
            }
            return;
        }
        _passiveClientLogged = false;

        var now = Time.realtimeSinceStartup;
        _spawner.MaintainHostLocalView();
        TorIntroPresentationPatch.RefreshVisibleSpecificRole();
        VampireDelayedDeathPositionPatch.MaintainPositionHolds();
        try
        {
            _hostRoleControls.Update();
        }
        catch (Exception ex)
        {
            // Host HUD repair is optional presentation/control assistance. It
            // must never starve movement, abilities, memory, or meetings.
            if (Time.time >= _nextHostUiIsolationLogAt)
            {
                _nextHostUiIsolationLogAt = Time.time + 3f;
                _log.LogWarning($"DeepBot host UI subsystem isolated: {ex.Message}");
            }
        }
        _memory.Update(Plugin.Settings);
        _director.UpdateMovement(Plugin.Settings, Time.deltaTime);
        _abilities.Update(Plugin.Settings);
        TorRoleAdapter.Update(_memory.MatchSerial);
        _social.Update(Plugin.Settings);

        var interval = Math.Max(0.25f, Plugin.Settings.TickIntervalSeconds.Value);
        if (now < _nextTickAt)
        {
            return;
        }

        _nextTickAt = now + interval;
        Tick(now);
    }

    private void LateUpdate()
    {
        if (!_started || !Plugin.Settings.Enabled.Value || !HasHostAuthority())
        {
            return;
        }

        // CreatePlayer and other game coroutines can run after our Update and
        // temporarily retarget LocalPlayer/camera to a newly spawned bot. Repair
        // once more immediately before rendering to prevent visible lobby jumps.
        _spawner.MaintainHostLocalView();
    }

    private void Tick(float now)
    {
        var scene = SceneManager.GetActiveScene().name ?? string.Empty;
        if (!string.Equals(scene, _lastScene, StringComparison.Ordinal))
        {
            _lastScene = scene;
            _log.LogInfo($"DeepBot scene changed: {scene}");
        }

        _spawner.Tick(Plugin.Settings);
        TorRoleAdapter.AuditDeepBotAssignments();
        _director.TickDecision(Plugin.Settings);
        _social.Tick(Plugin.Settings);

        if (!Plugin.Settings.VerboseDiagnostics.Value)
        {
            return;
        }

        var dryRun = Plugin.Settings.DryRun.Value ? "dry-run" : "active";
        _log.LogInfo($"DeepBot runtime heartbeat: scene={scene}, mode={dryRun}, targetBots={Plugin.Settings.LocalBotCount.Value}, graph={SkeldPathGraph.Instance.Summary}");
    }

    internal void OnMeetingStarted()
    {
        if (_started && HasHostAuthority())
        {
            _director.OnMeetingStarted();
            _social.OnMeetingStarted();
        }
    }

    internal void OnMeetingEnded()
    {
        if (_started && HasHostAuthority())
        {
            _social.OnMeetingEnded();
            _director.OnMeetingEnded();
        }
    }

    internal void OnGameEnded(EndGameResult endGameResult)
    {
        if (_started && HasHostAuthority() && Plugin.Settings.PostMatchReflection.Value)
        {
            _evolution.OnGameEnded(endGameResult);
        }
    }

    internal void CaptureGameEnding()
    {
        if (_started && HasHostAuthority() && Plugin.Settings.PostMatchReflection.Value)
        {
            _evolution.CaptureGameEnding();
        }
    }

    internal void OnChat(PlayerControl source, string text)
    {
        if (_started && HasHostAuthority())
        {
            _memory.RecordPublicChat(source, text);
            _social.OnChat(source, text);
        }
    }

    internal void OnBodyReportRequested(PlayerControl reporter, NetworkedPlayerInfo victim)
    {
        if (_started && HasHostAuthority())
        {
            _social.RecordBodyReporter(reporter, victim);
        }
    }

    internal void ApplyPhysicsMovement(PlayerPhysics physics)
    {
        if (_started && HasHostAuthority())
        {
            _director.ApplyPhysicsMovement(physics);
        }
    }

    internal int CapturePotentialMurderWitnessMask(PlayerControl killer, PlayerControl victim)
    {
        if (!_started || !HasHostAuthority())
        {
            return 0;
        }

        var mask = 0;
        foreach (var playerId in _memory.CapturePotentialMurderWitnesses(killer, victim))
        {
            if (playerId < 32)
            {
                mask |= 1 << playerId;
            }
        }
        return mask;
    }

    internal void RecordObservedMurder(
        PlayerControl killer,
        PlayerControl victim,
        int preEventWitnessMask = 0)
    {
        if (!HasHostAuthority())
        {
            return;
        }

        var preEventWitnessIds = Enumerable.Range(0, 32)
            .Where(playerId => (preEventWitnessMask & (1 << playerId)) != 0)
            .Select(playerId => (byte)playerId)
            .ToArray();
        var witnessIds = _memory.RecordObservedMurder(killer, victim, preEventWitnessIds);
        if (witnessIds.Count > 0)
        {
            _director.OnWitnessedMurder(killer, victim, witnessIds);
        }
    }

    internal void RecordObservedSpecialAction(PlayerControl actor, string action, string inference)
    {
        if (HasHostAuthority())
        {
            _memory.RecordObservedSpecialAction(actor, action, inference);
        }
    }

    private static bool HasHostAuthority()
    {
        var client = AmongUsClient.Instance;
        return client &&
               client.NetworkMode == NetworkModes.LocalGame &&
               Plugin.AllowsWorldAuthority(client.AmHost);
    }
}

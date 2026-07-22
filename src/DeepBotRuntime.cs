using System;
using BepInEx.Logging;
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
    private string _lastScene = string.Empty;
    private bool _started;

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
        _hostRoleControls = new HostRoleControlGuard(_log);
        Plugin.Runtime = this;
        _started = true;
        SkeldPathGraph.Instance.LogStaticSelfTest(_log);
        BotBehaviorPolicy.LogSelfTest(_log);
        _log.LogInfo($"DeepBotRuntime started. graph={SkeldPathGraph.Instance.Summary}, hostKey={(DeepSeekDecisionClient.LoadHostApiKey() is null ? "missing" : "configured")}.");
    }

    private void Update()
    {
        if (!_started || !Plugin.Settings.Enabled.Value)
        {
            return;
        }

        var now = Time.realtimeSinceStartup;
        _spawner.MaintainHostLocalView();
        _hostRoleControls.Update();
        _memory.Update(Plugin.Settings);
        _director.UpdateMovement(Plugin.Settings, Time.deltaTime);
        _abilities.Update(Plugin.Settings);
        TorRoleAdapter.Update();
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
        if (!_started || !Plugin.Settings.Enabled.Value)
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
        if (_started)
        {
            _social.OnMeetingStarted();
        }
    }

    internal void OnMeetingEnded()
    {
        if (_started)
        {
            _social.OnMeetingEnded();
        }
    }

    internal void OnGameEnded(EndGameResult endGameResult)
    {
        if (_started && Plugin.Settings.PostMatchReflection.Value)
        {
            _evolution.OnGameEnded(endGameResult);
        }
    }

    internal void CaptureGameEnding()
    {
        if (_started && Plugin.Settings.PostMatchReflection.Value)
        {
            _evolution.CaptureGameEnding();
        }
    }

    internal void OnChat(PlayerControl source, string text)
    {
        if (_started)
        {
            _memory.RecordPublicChat(source, text);
            _social.OnChat(source, text);
        }
    }

    internal void ApplyPhysicsMovement(PlayerPhysics physics)
    {
        if (_started)
        {
            _director.ApplyPhysicsMovement(physics);
        }
    }

    internal void RecordObservedMurder(PlayerControl killer, PlayerControl victim)
    {
        _memory.RecordObservedMurder(killer, victim);
    }

    internal void RecordObservedSpecialAction(PlayerControl actor, string action, string inference)
    {
        _memory.RecordObservedSpecialAction(actor, action, inference);
    }
}

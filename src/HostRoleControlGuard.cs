using AmongUs.GameOptions;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class HostRoleControlGuard
{
    private readonly ManualLogSource _log;
    private RoleTypes? _boundRole;
    private bool _abilityButtonsInitialized;
    private int _lastVentTargetId = -1;
    private float _nextDiagnosticAt;
    private float _nextCooldownRepairLogAt;

    public HostRoleControlGuard(ManualLogSource log)
    {
        _log = log;
    }

    public void Update()
    {
        var client = AmongUsClient.Instance;
        if (!client ||
            client.NetworkMode != NetworkModes.LocalGame ||
            !client.AmHost ||
            client.GameState != InnerNetClient.GameStates.Started ||
            !ShipStatus.Instance ||
            IntroCutscene.Instance ||
            MeetingHud.Instance ||
            ExileController.Instance)
        {
            ResetTransientState();
            return;
        }

        var host = PlayerControl.LocalPlayer;
        if (!host ||
            host.Data is null ||
            host.Data.Disconnected ||
            host.Data.Role is null ||
            host.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal))
        {
            return;
        }

        var role = host.Data.Role;
        var roleChanged = _boundRole != host.Data.RoleType;
        if (roleChanged)
        {
            _boundRole = host.Data.RoleType;
            _abilityButtonsInitialized = false;
            _lastVentTargetId = -1;
            _nextDiagnosticAt = 0f;
        }

        if (role.Player != host)
        {
            var previous = role.Player;
            role.Player = host;
            _log.LogWarning(
                $"DeepBot repaired host role owner binding: role={host.Data.RoleType}, " +
                $"from={previous?.Data?.PlayerName ?? "missing"}({previous?.PlayerId.ToString() ?? "none"}), " +
                $"to={host.Data.PlayerName}({host.PlayerId}).");
        }

        if (DestroyableSingleton<HudManager>.InstanceExists)
        {
            var hud = DestroyableSingleton<HudManager>.Instance;
            EnsureAbilityButtons(host, role, hud);
            EnsureAbilityCooldownVisual(host, role, hud);
            EnsureVentTarget(host, role, hud);
            LogDiagnostics(host, role, hud, roleChanged);
        }
    }

    private void EnsureAbilityCooldownVisual(PlayerControl host, RoleBehaviour role, HudManager hud)
    {
        if (!_abilityButtonsInitialized ||
            !hud.AbilityButton ||
            !TryGetRoleCooldown(role, out var cooldownRemaining) ||
            cooldownRemaining > 0.05f ||
            IsRoleAbilityEffectActive(role))
        {
            return;
        }

        var abilityButton = hud.AbilityButton;
        if (!abilityButton.IsOnCooldown && !abilityButton.isCoolingDown)
        {
            return;
        }

        try
        {
            // Scientist owns an additional charge/fill state. Let the role
            // refresh that first, then clear only a cooldown flag that remains
            // stale after the role itself reports ready.
            var scientist = role.TryCast<ScientistRole>();
            scientist?.RefreshAbilityButton();
            if (abilityButton.IsOnCooldown || abilityButton.isCoolingDown)
            {
                abilityButton.SetCoolDown(0f, 1f);
            }

            if (role.TryCast<DetectiveRole>() is not null &&
                hud.SecondaryAbilityButton &&
                hud.SecondaryAbilityButton.IsOnCooldown)
            {
                hud.SecondaryAbilityButton.SetCoolDown(0f, 1f);
            }

            if (Time.time >= _nextCooldownRepairLogAt)
            {
                _nextCooldownRepairLogAt = Time.time + 2f;
                _log.LogInfo(
                    $"DeepBot repaired stale host ability cooldown visual: " +
                    $"player={host.Data?.PlayerName}({host.PlayerId}), role={host.Data?.RoleType}, " +
                    $"roleCooldown={cooldownRemaining:0.00}, buttonCooling={abilityButton.IsOnCooldown}, " +
                    $"buttonCanInteract={abilityButton.canInteract}.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                $"DeepBot host ability cooldown visual repair failed: role={host.Data?.RoleType}, error={ex.Message}");
        }
    }

    private static bool TryGetRoleCooldown(RoleBehaviour role, out float cooldownRemaining)
    {
        cooldownRemaining = 0f;
        if (role.TryCast<ScientistRole>() is { } scientist)
        {
            cooldownRemaining = Mathf.Max(0f, scientist.currentCooldown);
            return true;
        }

        if (role.TryCast<GuardianAngelRole>() is { } guardian)
        {
            cooldownRemaining = Mathf.Max(0f, guardian.cooldownSecondsRemaining);
            return true;
        }

        if (role.TryCast<ShapeshifterRole>() is { } shapeshifter)
        {
            cooldownRemaining = Mathf.Max(0f, shapeshifter.cooldownSecondsRemaining);
            return true;
        }

        if (role.TryCast<PhantomRole>() is { } phantom)
        {
            cooldownRemaining = Mathf.Max(0f, phantom.cooldownSecondsRemaining);
            return true;
        }

        if (role.TryCast<TrackerRole>() is { } tracker)
        {
            cooldownRemaining = Mathf.Max(0f, tracker.cooldownSecondsRemaining);
            return true;
        }

        if (role.TryCast<DetectiveRole>() is { } detective)
        {
            cooldownRemaining = Mathf.Max(0f, detective.cooldownSecondsRemaining);
            return true;
        }

        return false;
    }

    private static bool IsRoleAbilityEffectActive(RoleBehaviour role)
    {
        if (role.TryCast<ScientistRole>() is { } scientist && scientist.minigame)
        {
            return true;
        }

        if (role.TryCast<ShapeshifterRole>() is { } shapeshifter &&
            shapeshifter.durationSecondsRemaining > 0.05f)
        {
            return true;
        }

        if (role.TryCast<PhantomRole>() is { } phantom &&
            (phantom.durationSecondsRemaining > 0.05f || phantom.isInvisible || phantom.fading))
        {
            return true;
        }

        return role.TryCast<TrackerRole>() is { } tracker &&
            (tracker.durationSecondsRemaining > 0.05f ||
             tracker.delaySecondsRemaining > 0.05f ||
             tracker.isTrackingActive);
    }

    private void EnsureAbilityButtons(PlayerControl host, RoleBehaviour role, HudManager hud)
    {
        if (_abilityButtonsInitialized || !RoleUsesAbilityButton(host.Data.RoleType))
        {
            return;
        }

        try
        {
            role.buttonManager = hud.AbilityButton;
            role.InitializeAbilityButton();
            if (host.Data.RoleType == RoleTypes.Detective)
            {
                role.secondaryButtonManager = hud.SecondaryAbilityButton;
                role.InitializeSecondaryAbilityButton();
            }

            _abilityButtonsInitialized = true;
            _log.LogInfo(
                $"DeepBot host ability controls rebound: player={host.Data.PlayerName}({host.PlayerId}), " +
                $"role={host.Data.RoleType}, primary={(role.buttonManager ? "ready" : "missing")}, " +
                $"secondary={(role.secondaryButtonManager ? "ready" : "not-used")}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                $"DeepBot host ability control rebind failed: role={host.Data.RoleType}, error={ex.Message}");
        }
    }

    private void EnsureVentTarget(PlayerControl host, RoleBehaviour role, HudManager hud)
    {
        if (!role.CanVent || !host.moveable || host.inVent || host.walkingToVent || !hud.ImpostorVentButton)
        {
            return;
        }

        var position = host.GetTruePosition();
        Vent? bestVent = null;
        var bestDistance = float.MaxValue;
        foreach (var vent in UnityEngine.Object.FindObjectsOfType<Vent>())
        {
            if (!vent)
            {
                continue;
            }

            var distance = Vector2.Distance(position, vent.transform.position);
            if (distance >= bestDistance)
            {
                continue;
            }

            var usableDistance = vent.CanUse(host.Data, out _, out var couldUse);
            if (!couldUse || usableDistance > 1.75f)
            {
                continue;
            }

            bestVent = vent;
            bestDistance = distance;
        }

        if (bestVent is null)
        {
            return;
        }

        var engineer = role.TryCast<EngineerRole>();
        if (engineer is not null)
        {
            engineer.currentTarget = bestVent;
        }
        hud.ImpostorVentButton.SetTarget(bestVent);
        if (_lastVentTargetId != bestVent.Id)
        {
            _lastVentTargetId = bestVent.Id;
            _log.LogInfo(
                $"DeepBot host vent target rebound: player={host.Data.PlayerName}({host.PlayerId}), " +
                $"role={host.Data.RoleType}, vent={bestVent.Id}, distance={bestDistance:0.00}.");
        }
    }

    private void LogDiagnostics(PlayerControl host, RoleBehaviour role, HudManager hud, bool force)
    {
        if (!force && Time.time < _nextDiagnosticAt)
        {
            return;
        }

        _nextDiagnosticAt = Time.time + 8f;
        var abilityButton = hud.AbilityButton;
        var ventButton = hud.ImpostorVentButton;
        var hasRoleCooldown = TryGetRoleCooldown(role, out var roleCooldown);
        var roleEffectActive = IsRoleAbilityEffectActive(role);
        _log.LogInfo(
            $"DeepBot host control diagnostic: player={host.Data.PlayerName}({host.PlayerId}), " +
            $"role={host.Data.RoleType},rolePlayer={role.Player?.PlayerId.ToString() ?? "none"}," +
            $"moveable={host.moveable},inVent={host.inVent},walkingToVent={host.walkingToVent}," +
            $"canVent={role.CanVent},abilityManagerMatch={role.buttonManager == abilityButton}," +
            $"abilityActive={(abilityButton && abilityButton.gameObject.activeInHierarchy)}," +
            $"roleCooldown={(hasRoleCooldown ? roleCooldown.ToString("0.00") : "n/a")}," +
            $"roleEffectActive={roleEffectActive}," +
            $"abilityCooling={(abilityButton && abilityButton.IsOnCooldown)}," +
            $"abilityCanInteract={(abilityButton && abilityButton.canInteract)}," +
            $"ventButtonActive={(ventButton && ventButton.gameObject.activeInHierarchy)}," +
            $"ventTarget={(ventButton && ventButton.currentTarget ? ventButton.currentTarget.Id.ToString() : "none")}.");
    }

    private static bool RoleUsesAbilityButton(RoleTypes role)
    {
        return role is
            RoleTypes.Scientist or
            RoleTypes.GuardianAngel or
            RoleTypes.Shapeshifter or
            RoleTypes.Phantom or
            RoleTypes.Tracker or
            RoleTypes.Detective;
    }

    private void ResetTransientState()
    {
        _boundRole = null;
        _abilityButtonsInitialized = false;
        _lastVentTargetId = -1;
        _nextDiagnosticAt = 0f;
        _nextCooldownRepairLogAt = 0f;
    }
}

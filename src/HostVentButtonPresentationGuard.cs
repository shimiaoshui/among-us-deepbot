using HarmonyLib;
using InnerNet;

namespace AmongUsDeepSeekBots;

/// <summary>
/// TOR owns vent permissions, cooldowns, and vent entry. This patch only
/// removes a stale visible vent button after TOR has updated the HUD for a
/// local player who no longer has native vent permission.
/// </summary>
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
[HarmonyAfter("me.eisbison.theotherroles")]
internal static class HostVentButtonPresentationGuard
{
    private static bool _hiddenLogged;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(HudManager __instance)
    {
        var client = AmongUsClient.Instance;
        if (!client ||
            client.GameState != InnerNetClient.GameStates.Started ||
            IntroCutscene.Instance ||
            MeetingHud.Instance ||
            !__instance ||
            !__instance.ImpostorVentButton)
        {
            _hiddenLogged = false;
            return;
        }

        var local = DeepBotIdentity.FindLocalHumanPlayer() ?? PlayerControl.LocalPlayer;
        if (!local || local.Data is null || DeepBotIdentity.IsBot(local))
        {
            return;
        }

        if (TorRoleAdapter.HasNativeVentPermission(local))
        {
            _hiddenLogged = false;
            return;
        }

        if (!__instance.ImpostorVentButton.isActiveAndEnabled)
        {
            return;
        }

        __instance.ImpostorVentButton.Hide();
        if (_hiddenLogged)
        {
            return;
        }

        _hiddenLogged = true;
        Plugin.LogSource.LogInfo(
            $"DeepBot hid stale vent button for non-venting local role: " +
            $"player={local.Data.PlayerName}({local.PlayerId}), role={local.Data.RoleType}. " +
            "TOR remains authoritative for vent permission and cooldown rendering.");
    }
}

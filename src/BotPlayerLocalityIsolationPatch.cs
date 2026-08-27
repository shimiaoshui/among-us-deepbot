using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using InnerNet;

namespace AmongUsDeepSeekBots;

/// <summary>
/// A DeepBot's network objects remain host-owned so the host can replicate
/// movement and state to LAN guests.  PlayerControl.AmOwner, however, is also
/// used by vanilla and TOR as "this is the human at this keyboard".  Returning
/// true for every bot lets their role, vent, death and HUD updates overwrite
/// the real human's controls.  Isolate only the PlayerControl locality query;
/// NetTransform and PlayerPhysics keep their native host ownership unchanged.
/// </summary>
[HarmonyPatch(typeof(InnerNetObject), nameof(InnerNetObject.AmOwner), MethodType.Getter)]
internal static class BotPlayerLocalityIsolationPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(InnerNetObject __instance, ref bool __result)
    {
        if (!__result || !Plugin.Settings.Enabled.Value)
        {
            return;
        }

        var player = ((Il2CppObjectBase)__instance).TryCast<PlayerControl>();
        if (player && DeepBotIdentity.IsBot(player))
        {
            __result = false;
        }
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var botPlayerOwnerSuppressed = ResolvePlayerLocality(true, true, true) == false;
        var humanPlayerOwnerPreserved = ResolvePlayerLocality(true, false, true);
        var remotePlayerPreserved = ResolvePlayerLocality(false, true, true) == false;
        var physicsOwnershipUntouched = ResolvePlayerLocality(true, true, false);
        var level = botPlayerOwnerSuppressed && humanPlayerOwnerPreserved && remotePlayerPreserved &&
                    physicsOwnershipUntouched
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot player-locality isolation self-test: level={level}, " +
            $"botPlayerOwnerSuppressed={botPlayerOwnerSuppressed}, " +
            $"humanPlayerOwnerPreserved={humanPlayerOwnerPreserved}, remotePlayerPreserved={remotePlayerPreserved}, " +
            $"physicsOwnershipUntouched={physicsOwnershipUntouched}.");
    }

    private static bool ResolvePlayerLocality(bool nativeOwner, bool isBot, bool isPlayerControl) =>
        nativeOwner && !(isPlayerControl && isBot);
}

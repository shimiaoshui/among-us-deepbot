using System;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(ChatController), "AddChat")]
internal static class DeepBotChatAddPatch
{
    private static void Postfix(PlayerControl sourcePlayer, string chatText)
    {
        // TOR renders role-only information (trap logs, portal logs, medium
        // results, detective reports, etc.) through the normal chat UI by
        // calling AddChat locally. It looks like the player spoke, but no chat
        // RPC was sent. Never promote those private notices into public meeting
        // memory or let bots answer them as human testimony.
        if (TorLocalRoleChatPolicy.IsDirectTorRoleNotification())
        {
            return;
        }

        Plugin.Runtime?.OnChat(sourcePlayer, chatText);
    }
}

internal static class TorLocalRoleChatPolicy
{
    private const string TorAssemblyName = "TheOtherRoles";

    internal static bool IsDirectTorRoleNotification()
    {
        try
        {
            var frames = new StackTrace(false).GetFrames();
            if (frames is null)
            {
                return false;
            }

            foreach (var frame in frames)
            {
                var assemblyName = frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
                if (IsTorAssemblyName(assemblyName))
                {
                    return true;
                }
            }
        }
        catch
        {
            // If provenance cannot be determined, preserve normal chat rather
            // than risking the loss of a real player's statement.
        }

        return false;
    }

    internal static bool IsTorAssemblyName(string? assemblyName)
    {
        return string.Equals(assemblyName, TorAssemblyName, StringComparison.OrdinalIgnoreCase);
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var torDetected = IsTorAssemblyName("TheOtherRoles");
        var caseInsensitive = IsTorAssemblyName("theotherroles");
        var gameChatPreserved = !IsTorAssemblyName("Assembly-CSharp");
        var deepBotChatPreserved = !IsTorAssemblyName("AmongUsDeepSeekBots");
        var level = torDetected && caseInsensitive && gameChatPreserved && deepBotChatPreserved
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot TOR private role-chat provenance self-test: level={level}, " +
            $"torDetected={torDetected}, gameChatPreserved={gameChatPreserved}, " +
            $"deepBotChatPreserved={deepBotChatPreserved}.");
    }
}

[HarmonyPatch(typeof(MeetingHud), "Start")]
internal static class DeepBotMeetingStartPatch
{
    private static void Postfix()
    {
        Plugin.Runtime?.OnMeetingStarted();
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
internal static class DeepBotBodyReportCapturePatch
{
    private static void Prefix(PlayerControl __instance, NetworkedPlayerInfo target)
    {
        Plugin.Runtime?.OnBodyReportRequested(__instance, target);
    }
}

[HarmonyPatch(typeof(MeetingHud), "OnDestroy")]
internal static class DeepBotMeetingEndPatch
{
    private static void Prefix()
    {
        Plugin.Runtime?.OnMeetingEnded();
    }
}

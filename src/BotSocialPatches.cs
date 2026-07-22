using HarmonyLib;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(ChatController), "AddChat")]
internal static class DeepBotChatAddPatch
{
    private static void Postfix(PlayerControl sourcePlayer, string chatText)
    {
        Plugin.Runtime?.OnChat(sourcePlayer, chatText);
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

[HarmonyPatch(typeof(MeetingHud), "OnDestroy")]
internal static class DeepBotMeetingEndPatch
{
    private static void Prefix()
    {
        Plugin.Runtime?.OnMeetingEnded();
    }
}

using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AmongUsDeepSeekBots;

/// <summary>
/// F2 focuses the native free-chat field and opens Windows Voice Typing
/// (Win+H). Recognition text is therefore inserted by Windows directly into
/// Among Us; DeepBot never records or uploads microphone audio.
/// </summary>
[HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
internal static class VoiceChatDictationPatch
{
    private const byte VkLeftWindows = 0x5B;
    private const byte VkH = 0x48;
    private const uint KeyUp = 0x0002;
    private static float _nextAllowedAt;

    [DllImport("user32.dll", SetLastError = false)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ChatController __instance)
    {
        if (!OperatingSystem.IsWindows() ||
            !__instance ||
            !__instance.IsOpenOrOpening ||
            !Input.GetKeyDown(KeyCode.F2) ||
            Time.realtimeSinceStartup < _nextAllowedAt)
        {
            return;
        }

        var field = __instance.freeChatField;
        if (!field || !field.gameObject.activeInHierarchy)
        {
            return;
        }

        _nextAllowedAt = Time.realtimeSinceStartup + 1f;
        field.Focus();
        keybd_event(VkLeftWindows, 0, 0, UIntPtr.Zero);
        keybd_event(VkH, 0, 0, UIntPtr.Zero);
        keybd_event(VkH, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLeftWindows, 0, KeyUp, UIntPtr.Zero);
        Plugin.LogSource.LogInfo("DeepBot Windows voice typing opened for the active free-chat field (F2).");
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        log.LogInfo(
            $"DeepBot voice chat input self-test: level={(OperatingSystem.IsWindows() ? "ok" : "unsupported")}, " +
            "mode=Windows-Voice-Typing, hotkey=F2, audioUploadedByDeepBot=false.");
    }
}

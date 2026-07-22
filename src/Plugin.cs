using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.amongus.deepseekbots";
    public const string PluginName = "Among Us DeepSeek Bots";
    public const string PluginVersion = "0.9.9-tor46-full-role-recognition";

    private readonly Harmony _harmony = new(PluginGuid);

    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource LogSource { get; private set; } = null!;
    internal static PluginConfig Settings { get; private set; } = null!;
    internal static DeepBotRuntime? Runtime { get; set; }

    public override void Load()
    {
        Instance = this;
        LogSource = Log;
        Settings = PluginConfig.Bind(Config);

        _harmony.PatchAll(typeof(Plugin).Assembly);
        LogSpawnPatchTarget();

        ClassInjector.RegisterTypeInIl2Cpp<DeepBotRuntime>();

        var host = new GameObject("DeepBotRuntime");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<DeepBotRuntime>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled={Settings.Enabled.Value}, LocalBots={Settings.LocalBotCount.Value}, mode=rebuild-clean");
    }

    public override bool Unload()
    {
        _harmony.UnpatchSelf();
        return true;
    }

    private void LogSpawnPatchTarget()
    {
        var method = AccessTools.Method(
            typeof(InnerNetClient),
            "CreateSpawnMessage",
            [typeof(InnerNetObject), typeof(int), typeof(SpawnFlags)]);

        if (method is null)
        {
            Log.LogWarning("DeepBot spawn-owner patch target not found: InnerNetClient.CreateSpawnMessage(InnerNetObject, Int32, SpawnFlags).");
            return;
        }

        Log.LogInfo($"DeepBot spawn-owner patch target resolved: {method.DeclaringType?.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}).");
    }
}

internal sealed class PluginConfig
{
    public ConfigEntry<bool> Enabled { get; private init; } = null!;
    public ConfigEntry<int> LocalBotCount { get; private init; } = null!;
    public ConfigEntry<float> TickIntervalSeconds { get; private init; } = null!;
    public ConfigEntry<bool> DryRun { get; private init; } = null!;
    public ConfigEntry<bool> VerboseDiagnostics { get; private init; } = null!;
    public ConfigEntry<string> Model { get; private init; } = null!;
    public ConfigEntry<string> ApiBaseUrl { get; private init; } = null!;
    public ConfigEntry<float> BotSpeedMultiplier { get; private init; } = null!;
    public ConfigEntry<bool> SocialInteraction { get; private init; } = null!;
    public ConfigEntry<bool> AutoReportBodies { get; private init; } = null!;
    public ConfigEntry<bool> MeetingChat { get; private init; } = null!;
    public ConfigEntry<bool> MeetingVote { get; private init; } = null!;
    public ConfigEntry<bool> MeetingUseDeepSeek { get; private init; } = null!;
    public ConfigEntry<int> MaxMemoryEvents { get; private init; } = null!;
    public ConfigEntry<int> MeetingMemoryEvents { get; private init; } = null!;
    public ConfigEntry<bool> PostMatchReflection { get; private init; } = null!;
    public ConfigEntry<bool> BotUseRoleAbilities { get; private init; } = null!;

    public static PluginConfig Bind(ConfigFile config)
    {
        return new PluginConfig
        {
            Enabled = config.Bind("General", "Enabled", true, "Enable the rebuilt DeepBot controller."),
            LocalBotCount = config.Bind("Local", "BotCount", 5, "Number of host-authoritative Skeld bots to create in a local/LAN lobby."),
            TickIntervalSeconds = config.Bind("Runtime", "TickIntervalSeconds", 1.0f, "Controller decision tick interval. Movement and social checks are separately throttled."),
            DryRun = config.Bind("Runtime", "DryRun", false, "When true, only logs decisions and never spawns or controls bots."),
            VerboseDiagnostics = config.Bind("Diagnostics", "Verbose", true, "Write concise periodic game-state diagnostics."),
            Model = config.Bind("AI", "Model", "agnes-2.0-flash", "OpenAI-compatible model identifier."),
            ApiBaseUrl = config.Bind("AI", "ApiBaseUrl", "https://apihub.agnes-ai.com/v1", "OpenAI-compatible API base URL."),
            BotSpeedMultiplier = config.Bind("Movement", "SpeedMultiplier", 0.82f, "Bot movement speed multiplier."),
            SocialInteraction = config.Bind("Social", "Enabled", true, "Enable host-authoritative body reports, meeting chat, and voting."),
            AutoReportBodies = config.Bind("Social", "AutoReportBodies", true, "Allow crew bots to route to and report bodies they can actually see."),
            MeetingChat = config.Bind("Social", "MeetingChat", true, "Allow living bots to send short contextual meeting messages through the native chat RPC."),
            MeetingVote = config.Bind("Social", "MeetingVote", true, "Allow living bots to cast delayed votes through the native meeting RPC."),
            MeetingUseDeepSeek = config.Bind("AI", "MeetingUseDeepSeek", true, "Use the configured DeepSeek model as each bot's independent meeting discussion and voting brain."),
            BotUseRoleAbilities = config.Bind("Roles", "BotUseRoleAbilities", true, "Allow bots to use native role abilities, including vents, tracking, protection, shapeshifting, and Phantom vanish."),
            MaxMemoryEvents = config.Bind("Memory", "MaxEventsPerBot", 96, "Maximum verified match events retained independently for each bot."),
            MeetingMemoryEvents = config.Bind("Memory", "MeetingPromptEvents", 56, "Most recent private memory events sent to DeepSeek during a meeting."),
            PostMatchReflection = config.Bind("Memory", "PostMatchReflection", true, "Let every bot review its own match after the end screen and merge non-duplicate reusable lessons into the persistent core evolution skill store.")
        };
    }
}

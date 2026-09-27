using System.Reflection;
using HarmonyLib;
using InnerNet;

namespace AmongUsDeepSeekBots;

/// <summary>
/// Excludes host-created virtual bot records from the vanilla lobby readiness
/// check only.  The records remain in <c>allClients</c> for every other call.
/// </summary>
internal static class VirtualBotPlayerDataWaitPatch
{
    private const string VirtualBotPlatformName = "DeepSeek Bot";
    private static readonly PropertyInfo? PlatformDataProperty = AccessTools.Property(typeof(ClientData), "PlatformData");
    private static readonly FieldInfo? PlatformDataField = AccessTools.Field(typeof(ClientData), "PlatformData");
    private static readonly PropertyInfo? PlatformNameProperty = AccessTools.Property(typeof(PlatformSpecificData), "PlatformName");
    private static readonly FieldInfo? PlatformNameField = AccessTools.Field(typeof(PlatformSpecificData), "PlatformName");
    private static bool _applyAttempted;

    internal static void TryApply(Harmony harmony)
    {
        if (_applyAttempted)
        {
            return;
        }

        _applyAttempted = true;
        var target = AccessTools.Method(typeof(AmongUsClient), "AllPlayersHaveData");
        if (target is null)
        {
            Plugin.LogSource.LogWarning(
                "DeepBot virtual-player wait patch disabled: AmongUsClient.AllPlayersHaveData was not found.");
            return;
        }

        try
        {
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(VirtualBotPlayerDataWaitPatch), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(VirtualBotPlayerDataWaitPatch), nameof(Postfix)),
                finalizer: new HarmonyMethod(typeof(VirtualBotPlayerDataWaitPatch), nameof(Finalizer)));
            Plugin.LogSource.LogInfo(
                $"DeepBot virtual-player wait patch enabled: {target.DeclaringType?.Name}.{target.Name}.");
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogError(
                $"DeepBot virtual-player wait patch disabled: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Prefix(AmongUsClient __instance, out List<RemovedClient> __state)
    {
        __state = [];
        if (!CanApplyTo(__instance))
        {
            return;
        }

        try
        {
            for (var index = __instance.allClients.Count - 1; index >= 0; index--)
            {
                var candidate = __instance.allClients[index];
                if (!IsHostCreatedVirtualBot(candidate))
                {
                    continue;
                }

                __state.Add(new RemovedClient(index, candidate));
                __instance.allClients.RemoveAt(index);
            }
        }
        catch (Exception ex)
        {
            Restore(__instance, __state, "prefix failure");
            Plugin.LogSource.LogError(
                $"DeepBot virtual-player wait patch left vanilla roster intact after prefix failure: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Postfix(AmongUsClient __instance, List<RemovedClient> __state)
    {
        Restore(__instance, __state, "postfix");
    }

    private static Exception? Finalizer(AmongUsClient __instance, Exception? __exception, List<RemovedClient> __state)
    {
        Restore(__instance, __state, "finalizer");
        return __exception;
    }

    private static bool CanApplyTo(AmongUsClient? client)
    {
        return Plugin.Settings.Enabled.Value &&
               client is not null &&
               client &&
               client.AmHost &&
               client.NetworkMode == NetworkModes.LocalGame &&
               client.GameState == InnerNetClient.GameStates.Joined;
    }

    private static bool IsHostCreatedVirtualBot(ClientData? candidate)
    {
        if (candidate is null || !DeepBotIdentity.IsReservedClientId(candidate.Id))
        {
            return false;
        }

        // IDs 64..95 alone are not enough: an actual transport peer can use
        // one.  The spawner sets this platform tag only on its host-created
        // virtual ClientData records; guest presentation proxies use a
        // different tag and are therefore retained.
        var platform = PlatformDataProperty?.GetValue(candidate) ?? PlatformDataField?.GetValue(candidate);
        var platformName = platform is null
            ? null
            : PlatformNameProperty?.GetValue(platform) as string ?? PlatformNameField?.GetValue(platform) as string;
        return string.Equals(platformName, VirtualBotPlatformName, StringComparison.Ordinal);
    }

    private static void Restore(AmongUsClient? client, List<RemovedClient>? removed, string phase)
    {
        if (client is null || !client || removed is null || removed.Count == 0)
        {
            return;
        }

        try
        {
            removed.Sort(static (left, right) => left.Index.CompareTo(right.Index));
            foreach (var entry in removed)
            {
                var index = Math.Min(entry.Index, client.allClients.Count);
                client.allClients.Insert(index, entry.Client);
            }
            removed.Clear();
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogError(
                $"DeepBot virtual-player wait patch could not restore roster during {phase}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed record RemovedClient(int Index, ClientData Client);
}

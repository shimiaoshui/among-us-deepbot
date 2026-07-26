using HarmonyLib;
using System.Reflection;
using InnerNet;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.RpcEnterVent))]
internal static class ObservedVentEntryPatch
{
    private static void Prefix(PlayerPhysics __instance)
    {
        if (__instance && __instance.myPlayer)
        {
            Plugin.Runtime?.RecordObservedSpecialAction(
                __instance.myPlayer,
                "enter a vent",
                "the current role can vent; this is not automatic proof of base Impostor because TOR has crew and neutral vent-capable roles");
        }
    }
}

[HarmonyPatch]
internal static class TorNinjaTraceColorGuardPatch
{
    private static bool _latePatched;

    private static MethodBase? TargetMethod()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "TheOtherRoles", StringComparison.Ordinal));
        var displayClass = assembly?.GetType("TheOtherRoles.Objects.NinjaTrace+<>c__DisplayClass5_0", false);
        return displayClass?.GetMethod("<.ctor>b__0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    internal static void TryApplyLate(Harmony harmony)
    {
        if (_latePatched)
        {
            return;
        }

        var target = TargetMethod();
        if (target is null)
        {
            Plugin.LogSource.LogWarning("DeepBot TOR NinjaTrace color guard target unavailable.");
            return;
        }

        harmony.Patch(
            target,
            prefix: new HarmonyMethod(typeof(TorNinjaTraceColorGuardPatch), nameof(Prefix)));
        _latePatched = true;
        Plugin.LogSource.LogInfo("DeepBot TOR NinjaTrace end-of-round color guard applied.");
    }

    [HarmonyPrepare]
    private static bool Prepare() => TargetMethod() is not null;

    [HarmonyPrefix]
    private static bool Prefix()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, "TheOtherRoles", StringComparison.Ordinal));
        var root = assembly?.GetType("TheOtherRoles.TheOtherRoles", false);
        var ninjaType = root?.GetNestedType("Ninja", BindingFlags.Public | BindingFlags.NonPublic) ??
                        assembly?.GetType("TheOtherRoles.Ninja", false);
        return ninjaType?.GetField("ninja", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is PlayerControl owner && owner;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class ObservedTorActionRpcPatch
{
    private static void Prefix(PlayerControl __instance, byte callId)
    {
        if (!__instance || !TryDescribe(callId, out var action, out var inference))
        {
            return;
        }

        Plugin.Runtime?.RecordObservedSpecialAction(__instance, action, inference);
    }

    internal static bool TryDescribe(byte callId, out string action, out string inference)
    {
        (action, inference) = callId switch
        {
            107 => ("use a TOR vent", "the current role has room-enabled vent access"),
            123 => ("remove a nearby body", "possible Janitor, Cleaner, Vulture, or another body-removal role"),
            130 => ("morph into another appearance", "strong Morphling evidence if the transformation itself was visible"),
            134 => ("place garlic", "strong Vampire-rule object interaction evidence, but not proof that the placer is Vampire"),
            145 => ("place a portal", "strong Portalmaker evidence"),
            146 => ("use a portal", "the player used an existing portal; this does not identify its creator"),
            147 => ("place a Jack-in-the-box", "strong Trickster evidence if the box placement was personally visible"),
            149 => ("place a security camera", "strong SecurityGuard evidence"),
            150 => ("seal a vent", "strong SecurityGuard evidence"),
            160 => ("become invisible or visible again", "possible Ninja or another invisibility-capable role; compare timing and later events"),
            165 => ("perform a bomb placement action", "strong Bomber evidence only if the placement motion/object was personally visible"),
            166 => ("defuse a bomb", "the player interacted with a visible bomb; this is not hostile evidence"),
            169 => ("blink through a Yoyo return point", "strong Yoyo evidence if the teleport was personally visible"),
            _ => default
        };
        return !string.IsNullOrWhiteSpace(action);
    }

    internal static void LogSelfTest(BepInEx.Logging.ManualLogSource log)
    {
        var uncheckedVentMapped = TryDescribe(107, out var ventAction, out _) &&
                                  ventAction.Contains("vent", StringComparison.OrdinalIgnoreCase);
        var handshakeNotMisread = !TryDescribe(106, out _, out _);
        var visibleObjectsMapped = TryDescribe(145, out _, out _) &&
                                   TryDescribe(147, out _, out _) &&
                                   TryDescribe(165, out _, out _);
        var hiddenTrapNotExposed = !TryDescribe(162, out _, out _);
        var level = uncheckedVentMapped && handshakeNotMisread && visibleObjectsMapped && hiddenTrapNotExposed
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot TOR observed-action RPC self-test: level={level}, uncheckedVent107={uncheckedVentMapped}, " +
            $"versionHandshake106Ignored={handshakeNotMisread}, visibleObjects={visibleObjectsMapped}, hiddenTrapProtected={hiddenTrapNotExposed}.");
    }
}

[HarmonyPatch]
internal static class ObservedOutboundTorActionPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(InnerNetClient),
            nameof(InnerNetClient.StartRpcImmediately),
            [typeof(uint), typeof(byte), typeof(Hazel.SendOption), typeof(int)]);
    }

    private static void Prefix(uint __0, byte __1)
    {
        if (!ObservedTorActionRpcPatch.TryDescribe(__1, out var action, out var inference))
        {
            return;
        }

        var actor = PlayerControl.AllPlayerControls
            .ToArray()
            .FirstOrDefault(player => player && player.NetId == __0);
        if (actor)
        {
            Plugin.Runtime?.RecordObservedSpecialAction(actor!, action, inference);
        }
    }
}

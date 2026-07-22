using HarmonyLib;

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

    private static bool TryDescribe(byte callId, out string action, out string inference)
    {
        (action, inference) = callId switch
        {
            106 => ("use a TOR vent", "the current role has room-enabled vent access"),
            123 => ("remove a nearby body", "possible Janitor, Cleaner, Vulture, or another body-removal role"),
            130 => ("morph into another appearance", "strong Morphling evidence if the transformation itself was visible"),
            134 => ("place garlic", "strong Vampire-rule object interaction evidence, but not proof that the placer is Vampire"),
            145 => ("place a portal", "strong Portalmaker evidence"),
            146 => ("use a portal", "the player used an existing portal; this does not identify its creator"),
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
}

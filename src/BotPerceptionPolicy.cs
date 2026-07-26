using UnityEngine;

namespace AmongUsDeepSeekBots;

internal static class BotPerceptionPolicy
{
    private const float MurderWitnessEdgePadding = 0.35f;
    internal const float RecentPairVisibilitySeconds = 0.70f;

    internal static bool IsConcealedByVent(PlayerControl? player)
    {
        return player && IsConcealedByVent(player!.inVent, player.walkingToVent);
    }

    internal static bool IsConcealedByVent(bool inVent, bool walkingToVent)
    {
        return inVent || walkingToVent;
    }

    internal static bool CanBeOrdinarilyObserved(PlayerControl? player)
    {
        return player &&
               player!.Data is not null &&
               !player.Data.IsDead &&
               !player.Data.Disconnected &&
               !IsConcealedByVent(player);
    }

    internal static float GetCurrentVisionDistance(PlayerControl? observer)
    {
        if (observer && observer!.Data is not null && ShipStatus.Instance)
        {
            return Mathf.Max(0.1f, ShipStatus.Instance.CalculateLightRadius(observer.Data));
        }

        return observer && TorRoleAdapter.IsImpostorTeam(observer)
            ? GameRuleSettings.GetImpostorVision(1.5f) * 5f
            : GameRuleSettings.GetCrewVision(1f) * 5f;
    }

    internal static bool CanWitnessMurderGeometry(
        float killerDistance,
        float victimDistance,
        float visionDistance,
        bool killerBlockedByWall,
        bool victimBlockedByWall)
    {
        var radius = Mathf.Max(0.1f, visionDistance) + MurderWitnessEdgePadding;
        return killerDistance <= radius &&
               victimDistance <= radius &&
               (!killerBlockedByWall || !victimBlockedByWall);
    }

    internal static bool CanWitnessMurder(
        bool recentlySawKillerAndVictim,
        float cachedVisibilityAge,
        float killerDistance,
        float victimDistance,
        float visionDistance,
        bool killerBlockedByWall,
        bool victimBlockedByWall)
    {
        var recentPairVisible = recentlySawKillerAndVictim &&
            cachedVisibilityAge >= 0f &&
            cachedVisibilityAge <= RecentPairVisibilitySeconds;
        return recentPairVisible || CanWitnessMurderGeometry(
            killerDistance,
            victimDistance,
            visionDistance,
            killerBlockedByWall,
            victimBlockedByWall);
    }
}

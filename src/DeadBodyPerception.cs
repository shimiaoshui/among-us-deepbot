using UnityEngine;

namespace AmongUsDeepSeekBots;

internal static class DeadBodyPerception
{
    private const float VisibleAlphaThreshold = 0.08f;
    private const float FallbackReportDistance = 2.5f;
    private const float CloseRangeOcclusionOverride = 0.75f;

    public static bool IsVisibleAndReportable(DeadBody? body)
    {
        return IsVisibleAndReportable(body, out _);
    }

    public static bool IsVisibleAndReportable(DeadBody? body, out string diagnostic)
    {
        if (body is null || !body)
        {
            diagnostic = "body-null-or-destroyed";
            return false;
        }

        if (body.Reported)
        {
            diagnostic = "already-reported";
            return false;
        }

        if (!body.enabled)
        {
            diagnostic = "component-disabled";
            return false;
        }

        if (!body.gameObject || !body.gameObject.activeInHierarchy)
        {
            diagnostic = "game-object-inactive";
            return false;
        }

        // Viper corrosion can leave the networked DeadBody component alive
        // after its visible corpse has been removed. AI perception must follow
        // what players can actually see, not the lifetime of that stale object.
        //
        // DeadBody explicitly exposes every corpse renderer. GetComponentInChildren
        // returns only the first child and can select the disabled blood-splatter,
        // shadow, or cosmetic renderer while the actual corpse is plainly visible.
        var renderers = body.bodyRenderers;
        if (renderers is not null && renderers.Length > 0)
        {
            var visibleCount = 0;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (IsRendererVisible(renderers[i]))
                {
                    visibleCount++;
                }
            }

            diagnostic = $"body-renderers-visible={visibleCount}/{renderers.Length}";
            return visibleCount > 0;
        }

        // Defensive fallback for custom-role corpses that do not populate the
        // native bodyRenderers array. Ignore blood splatter because it can outlive
        // a fully corroded Viper corpse.
        var childRenderers = body.GetComponentsInChildren<SpriteRenderer>(true);
        var fallbackVisibleCount = 0;
        for (var i = 0; i < childRenderers.Length; i++)
        {
            var renderer = childRenderers[i];
            if (renderer != body.bloodSplatter && IsRendererVisible(renderer))
            {
                fallbackVisibleCount++;
            }
        }

        diagnostic = $"fallback-renderers-visible={fallbackVisibleCount}/{childRenderers.Length}";
        return fallbackVisibleCount > 0;
    }

    public static bool CanObserve(
        PlayerControl observer,
        DeadBody? body,
        float visionDistance,
        out float distance,
        out bool blockedByWall)
    {
        distance = float.MaxValue;
        blockedByWall = false;
        if (!observer || !IsVisibleAndReportable(body))
        {
            return false;
        }

        var observerPosition = observer.GetTruePosition();
        distance = Vector2.Distance(observerPosition, body!.TruePosition);
        if (!IsWithinRange(distance, visionDistance))
        {
            return false;
        }

        // Report perception should match what a player can see in the room.
        // Walls and closed room boundaries block discovery; tables and ordinary
        // props do not erase a corpse that is visibly lying beside them.
        blockedByWall = PhysicsHelpers.AnythingBetween(
            observerPosition,
            body.TruePosition,
            Constants.ShipOnlyMask,
            false);
        if (!blockedByWall)
        {
            return true;
        }

        // At touching/report-button distance a table edge or the corpse's own
        // surrounding collider can produce a false ShipOnly ray hit. This narrow
        // override cannot see across ordinary room-width walls, but guarantees
        // that a crew bot standing on a visible corpse can react to it.
        if (CanBypassCloseRangeOcclusion(distance, GetReportDistance(observer)))
        {
            blockedByWall = false;
            return true;
        }

        return false;
    }

    public static float GetReportDistance(PlayerControl player)
    {
        if (!player)
        {
            return FallbackReportDistance;
        }

        var nativeDistance = player.MaxReportDistance;
        return nativeDistance is >= 1f and <= 8f
            ? nativeDistance
            : FallbackReportDistance;
    }

    internal static bool IsWithinRange(float distance, float maximumDistance)
    {
        return distance >= 0f &&
               maximumDistance > 0f &&
               distance <= maximumDistance;
    }

    internal static bool CanBypassCloseRangeOcclusion(float distance, float reportDistance)
    {
        return IsWithinRange(
            distance,
            Mathf.Min(CloseRangeOcclusionOverride, Mathf.Max(0f, reportDistance)));
    }

    private static bool IsRendererVisible(SpriteRenderer? renderer)
    {
        return renderer is not null &&
               renderer &&
               renderer.enabled &&
               renderer.gameObject &&
               renderer.gameObject.activeInHierarchy &&
               renderer.sprite &&
               renderer.color.a > VisibleAlphaThreshold;
    }
}

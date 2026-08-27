using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace AmongUsDeepSeekBots;

/// <summary>
/// TOR keeps its own CachedPlayer.LocalPlayer in addition to
/// PlayerControl.LocalPlayer. Native CreatePlayer temporarily points the
/// vanilla field at each host-created bot while its coroutine starts. TOR's
/// PlayerControl.Start postfix can cache that temporary bot and leave every
/// role button, ghost check and local-only effect bound to it even after the
/// vanilla field has been restored. Keep both local-player authorities on the
/// real human character.
/// </summary>
internal static class TorCachedLocalPlayerGuard
{
    private static readonly Assembly? TorAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .FirstOrDefault(assembly => string.Equals(
            assembly.GetName().Name,
            "TheOtherRoles",
            StringComparison.OrdinalIgnoreCase));
    private static readonly Type? CachedPlayerType =
        TorAssembly?.GetType("TheOtherRoles.Players.CachedPlayer", false);
    private static readonly FieldInfo? LocalPlayerField =
        CachedPlayerType is null ? null : AccessTools.Field(CachedPlayerType, "LocalPlayer");
    private static readonly FieldInfo? AllPlayersField =
        CachedPlayerType is null ? null : AccessTools.Field(CachedPlayerType, "AllPlayers");
    private static readonly FieldInfo? CachedControlField =
        CachedPlayerType is null ? null : AccessTools.Field(CachedPlayerType, "PlayerControl");
    private static readonly FieldInfo? CachedDataField =
        CachedPlayerType is null ? null : AccessTools.Field(CachedPlayerType, "Data");
    private static readonly FieldInfo? CachedPlayerIdField =
        CachedPlayerType is null ? null : AccessTools.Field(CachedPlayerType, "PlayerId");
    private static readonly MethodInfo? NativeSetLocalPlayerMethod = AccessTools.Method(
        TorAssembly?.GetType("TheOtherRoles.Players.CachedPlayerPatches+CacheLocalPlayerPatch", false),
        "SetLocalPlayer");

    private static IntPtr _lastWrongPointer;
    private static float _nextFailureLogAt;
    private static bool _availabilityLogged;

    public static bool EnsureMatches(PlayerControl? authoritativeLocal, ManualLogSource log, string reason)
    {
        if (!_availabilityLogged)
        {
            _availabilityLogged = true;
            log.LogInfo(
                $"DeepBot TOR cached-local guard initialized: assembly={TorAssembly?.GetName().Name ?? "missing"}, " +
                $"cachedType={CachedPlayerType is not null}, nativeSetter={NativeSetLocalPlayerMethod is not null}.");
        }

        if (!authoritativeLocal || authoritativeLocal!.Data is null || CachedPlayerType is null)
        {
            return false;
        }

        try
        {
            var before = GetCachedControl();
            if (SamePlayer(before, authoritativeLocal))
            {
                return true;
            }

            // Prefer TOR's own synchronizer so its Data and PlayerId mirrors are
            // refreshed exactly as TOR expects.
            NativeSetLocalPlayerMethod?.Invoke(null, null);
            var after = GetCachedControl();
            if (!SamePlayer(after, authoritativeLocal))
            {
                RepairDirectly(authoritativeLocal);
                after = GetCachedControl();
            }

            var repaired = SamePlayer(after, authoritativeLocal);
            var wrongPointer = before && before!.Pointer != IntPtr.Zero ? before.Pointer : IntPtr.Zero;
            if (repaired && wrongPointer != _lastWrongPointer)
            {
                _lastWrongPointer = wrongPointer;
                log.LogWarning(
                    $"DeepBot restored TOR CachedPlayer.LocalPlayer: reason={reason}, " +
                    $"from={Describe(before)}, to={Describe(authoritativeLocal)}.");
            }
            else if (!repaired && UnityEngine.Time.realtimeSinceStartup >= _nextFailureLogAt)
            {
                _nextFailureLogAt = UnityEngine.Time.realtimeSinceStartup + 5f;
                log.LogError(
                    $"DeepBot could not restore TOR CachedPlayer.LocalPlayer: reason={reason}, " +
                    $"cached={Describe(after)}, authoritative={Describe(authoritativeLocal)}.");
            }

            return repaired;
        }
        catch (Exception ex)
        {
            if (UnityEngine.Time.realtimeSinceStartup >= _nextFailureLogAt)
            {
                _nextFailureLogAt = UnityEngine.Time.realtimeSinceStartup + 5f;
                log.LogError($"DeepBot TOR cached-local repair failed: reason={reason}, error={ex.GetBaseException().Message}");
            }
            return false;
        }
    }

    public static PlayerControl? GetCachedControl()
    {
        var cached = LocalPlayerField?.GetValue(null);
        return cached is null ? null : CachedControlField?.GetValue(cached) as PlayerControl;
    }

    private static void RepairDirectly(PlayerControl authoritativeLocal)
    {
        if (AllPlayersField?.GetValue(null) is not IEnumerable allPlayers)
        {
            return;
        }

        foreach (var cached in allPlayers)
        {
            if (cached is null || CachedControlField?.GetValue(cached) is not PlayerControl candidate ||
                !SamePlayer(candidate, authoritativeLocal))
            {
                continue;
            }

            CachedDataField?.SetValue(cached, authoritativeLocal.Data);
            CachedPlayerIdField?.SetValue(cached, authoritativeLocal.PlayerId);
            LocalPlayerField?.SetValue(null, cached);
            return;
        }
    }

    private static bool SamePlayer(PlayerControl? left, PlayerControl? right) =>
        left && right && left!.Pointer == right!.Pointer;

    private static string Describe(PlayerControl? player) =>
        player && player!.Data is not null
            ? $"{player.Data.PlayerName}({player.PlayerId},dead={player.Data.IsDead},owner={player.OwnerId})"
            : "missing";
}

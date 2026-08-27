using System.Reflection;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

/// <summary>
/// TOR normally sends its native version handshake once when the lobby starts
/// and again from OnPlayerJoined.  On a local/LAN join that notification can
/// run before the joining peer is ready to receive the host broadcast, leaving
/// the peer with no host entry until TOR's ten-second kick timer expires.
///
/// This patch deliberately calls TOR's own shareGameVersion method.  It does
/// not replace, spoof, or bypass TOR's version/GUID checks; it only retries the
/// same reliable native handshake at a low frequency while the LAN lobby UI is
/// alive.
/// </summary>
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
internal static class TorLanVersionHandshakeReliabilityPatch
{
    private const float RetrySeconds = 1.0f;
    private static MethodInfo? _shareGameVersion;
    private static float _nextRetryAt;
    private static int _lastClientId = int.MinValue;
    private static int _lastHostId = int.MinValue;
    private static bool _loggedReady;
    private static bool _loggedFailure;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        var client = AmongUsClient.Instance;
        if (client is null ||
            client.NetworkMode != NetworkModes.LocalGame ||
            PlayerControl.LocalPlayer is null)
        {
            ResetSessionIfNeeded(client);
            return;
        }

        var clientId = client.ClientId;
        var hostId = client.HostId;
        // TOR's own client-side GameStartManager patch already sends the
        // guest's version handshake.  Retrying shareGameVersion from a guest
        // can race the local PlayerControl/RPC ownership setup and throw a
        // TargetInvocationException, which adds noise and can interfere with
        // the join countdown.  Only the host needs the extra broadcast: it
        // is the side whose first handshake can be sent before a new guest is
        // ready to receive it.
        if (!Plugin.AllowsWorldAuthority(client.AmHost))
        {
            ResetSessionIfNeeded(client);
            return;
        }

        // Keep the public presentation roster available for guests that join
        // after the native initial snapshot or miss its immediately-following
        // custom packet. This does not grant guest-side AI authority.
        DeepBotGuestRosterSync.TickHost();
        if (clientId != _lastClientId || hostId != _lastHostId)
        {
            _lastClientId = clientId;
            _lastHostId = hostId;
            _nextRetryAt = 0f;
            _loggedReady = false;
            _loggedFailure = false;
        }

        var now = Time.realtimeSinceStartup;
        if (now < _nextRetryAt)
        {
            return;
        }
        _nextRetryAt = now + RetrySeconds;

        try
        {
            var torAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(
                    assembly.GetName().Name,
                    "TheOtherRoles",
                    StringComparison.OrdinalIgnoreCase));
            _shareGameVersion ??= AccessTools.Method(
                torAssembly?.GetType("TheOtherRoles.Helpers", false),
                "shareGameVersion");
            if (_shareGameVersion is null)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Plugin.LogSource.LogWarning("DeepBot could not resolve TOR's native shareGameVersion handshake method.");
                }
                return;
            }

            _shareGameVersion.Invoke(null, null);
            if (!_loggedReady)
            {
                _loggedReady = true;
                Plugin.LogSource.LogInfo(
                    $"DeepBot LAN TOR handshake retry active: clientId={clientId}, hostId={hostId}, interval={RetrySeconds:0.0}s.");
            }
        }
        catch (Exception ex)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.LogSource.LogWarning($"DeepBot TOR handshake retry failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void ResetSessionIfNeeded(AmongUsClient? client)
    {
        if (client is not null && client.ClientId == _lastClientId && client.HostId == _lastHostId)
        {
            return;
        }

        _lastClientId = client?.ClientId ?? int.MinValue;
        _lastHostId = client?.HostId ?? int.MinValue;
        _nextRetryAt = 0f;
        _loggedReady = false;
        _loggedFailure = false;
    }
}

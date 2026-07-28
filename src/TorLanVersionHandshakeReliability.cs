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
    private const float RetrySeconds = 2.0f;
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
            PlayerControl.LocalPlayer is null ||
            GameData.Instance is null)
        {
            ResetSessionIfNeeded(client);
            return;
        }

        var clientId = client.ClientId;
        var hostId = client.HostId;
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
            _shareGameVersion ??= AccessTools.Method(
                AccessTools.TypeByName("TheOtherRoles.Helpers"),
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

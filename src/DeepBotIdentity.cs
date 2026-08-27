using InnerNet;

namespace AmongUsDeepSeekBots;

internal static class DeepBotIdentity
{
    internal const int ReservedClientIdStart = 64;
    internal const int ReservedClientIdEnd = 95;

    internal static bool IsReservedClientId(int clientId)
    {
        return clientId is >= ReservedClientIdStart and <= ReservedClientIdEnd;
    }

    internal static bool IsBot(PlayerControl? player)
    {
        if (player is null || !player || player.Data is null)
        {
            return false;
        }

        // Runtime LAN bots deliberately transfer PlayerControl/physics ownership
        // to the host so their movement is replicated to guests. OwnerId is
        // therefore not a stable identity after creation. The native
        // NetworkedPlayerInfo keeps the reserved virtual client id and must be
        // the primary discriminator; otherwise vanilla FixedUpdate reads the
        // host's keyboard for every host-owned bot.
        if (IsReservedClientId(player.Data.ClientId) ||
            IsReservedClientId(player.OwnerId) ||
            player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal))
        {
            return true;
        }

        // Belt-and-suspenders fallback for the short native creation window in
        // which PlayerInfo can be replaced while ClientData already owns the
        // character. This is independent of the visible bot name.
        var client = AmongUsClient.Instance;
        if (!client)
        {
            return false;
        }

        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is not null &&
                IsReservedClientId(candidate.Id) &&
                candidate.Character &&
                candidate.Character.PlayerId == player.PlayerId)
            {
                return true;
            }
        }

        return false;
    }

    internal static PlayerControl? FindLocalHumanPlayer()
    {
        var client = AmongUsClient.Instance;
        if (client)
        {
            for (var index = 0; index < client.allClients.Count; index++)
            {
                var candidate = client.allClients[index];
                if (candidate is not null &&
                    candidate.Id == client.ClientId &&
                    candidate.Character &&
                    candidate.Character.Data is not null &&
                    candidate.Character.OwnerId == client.ClientId &&
                    candidate.Character.Data.ClientId == client.ClientId &&
                    !IsBot(candidate.Character))
                {
                    return candidate.Character;
                }
            }

            foreach (var player in PlayerControl.AllPlayerControls)
            {
                if (player &&
                    player.Data is not null &&
                    player.OwnerId == client.ClientId &&
                    player.Data.ClientId == client.ClientId &&
                    !IsBot(player))
                {
                    return player;
                }
            }
        }

        var local = PlayerControl.LocalPlayer;
        return local && local.Data is not null && !IsBot(local) ? local : null;
    }

    internal static bool IsBot(ClientData? client)
    {
        return client is not null &&
               (IsReservedClientId(client.Id) ||
                client.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal) ||
                IsBot(client.Character));
    }

    internal static bool IsBotPlayerId(byte playerId)
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player && player.PlayerId == playerId)
            {
                return IsBot(player);
            }
        }

        return false;
    }

    internal static bool TryGetBotIndex(ClientData? client, out int botIndex)
    {
        botIndex = -1;
        if (client is null)
        {
            return false;
        }

        if (IsReservedClientId(client.Id))
        {
            botIndex = client.Id - ReservedClientIdStart;
            return true;
        }

        return TryParseLegacyName(client.PlayerName, out botIndex) ||
               (client.Character && TryParseLegacyName(client.Character.Data?.PlayerName, out botIndex));
    }

    private static bool TryParseLegacyName(string? name, out int botIndex)
    {
        botIndex = -1;
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("DeepBot ", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(name["DeepBot ".Length..], out var oneBased) &&
               oneBased > 0 &&
               (botIndex = oneBased - 1) >= 0;
    }
}

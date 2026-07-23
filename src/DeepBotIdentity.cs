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
        return player is not null &&
               player &&
               player.Data is not null &&
               (IsReservedClientId(player.OwnerId) ||
                player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal));
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

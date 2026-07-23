using BepInEx.Logging;

namespace AmongUsDeepSeekBots;

internal readonly record struct DeepBotAppearanceSettings(
    int NameSelection,
    int ColorSelection,
    int OutfitSelection,
    int NamePlateSelection)
{
    internal static DeepBotAppearanceSettings Default => new(0, 0, 0, 0);
}

internal static class DeepBotAppearance
{
    private static readonly string[] Names =
    [
        "", "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Nova", "Luna", "Atlas",
        "Pixel", "Ghost", "Iris", "Milo", "Orion", "Vega", "Zenith", "小蓝", "小粉", "船长", "侦探", "小懒", "急急"
    ];

    internal static string ResolveName(int botIndex, int selection)
    {
        if (selection <= 0 || selection >= Names.Length)
        {
            return $"DeepBot {botIndex + 1}";
        }

        return Names[selection];
    }

    internal static int ResolveColor(int botIndex, int selection)
    {
        if (Palette.PlayerColors.Length <= 0)
        {
            return 0;
        }

        var requested = selection <= 0 ? botIndex + 1 : selection - 1;
        return Math.Abs(requested) % Palette.PlayerColors.Length;
    }

    internal static void ApplyOutfit(
        PlayerControl bot,
        PlayerControl? host,
        int botIndex,
        int outfitSelection,
        ManualLogSource log)
    {
        if (!bot || bot.Data is null || outfitSelection <= 0)
        {
            return;
        }

        try
        {
            if (outfitSelection == 2 && host is not null && host && host.Data is not null)
            {
                ApplyIds(bot, host.Data.DefaultOutfit.HatId, host.Data.DefaultOutfit.SkinId,
                    host.Data.DefaultOutfit.VisorId, host.Data.DefaultOutfit.PetId);
                return;
            }

            var manager = HatManager.Instance;
            if (!manager)
            {
                log.LogWarning($"DeepBot outfit held: bot={botIndex + 1}, HatManager unavailable.");
                return;
            }

            if (outfitSelection == 1)
            {
                ApplyIds(bot,
                    FindEmptyProduct(manager.GetUnlockedHats(), bot.Data.DefaultOutfit.HatId),
                    FindEmptyProduct(manager.GetUnlockedSkins(), bot.Data.DefaultOutfit.SkinId),
                    FindEmptyProduct(manager.GetUnlockedVisors(), bot.Data.DefaultOutfit.VisorId),
                    FindEmptyProduct(manager.GetUnlockedPets(), bot.Data.DefaultOutfit.PetId));
                return;
            }

            var seed = outfitSelection == 3
                ? (AmongUsClient.Instance?.GameId ?? 0) * 31 + botIndex * 101 + 17
                : (outfitSelection - 4) * 97 + botIndex * 13 + 29;
            ApplyIds(bot,
                SelectProduct(manager.GetUnlockedHats(), seed + 3, bot.Data.DefaultOutfit.HatId),
                SelectProduct(manager.GetUnlockedSkins(), seed + 5, bot.Data.DefaultOutfit.SkinId),
                SelectProduct(manager.GetUnlockedVisors(), seed + 7, bot.Data.DefaultOutfit.VisorId),
                SelectProduct(manager.GetUnlockedPets(), seed + 11, bot.Data.DefaultOutfit.PetId));
        }
        catch (Exception ex)
        {
            log.LogWarning($"DeepBot outfit apply failed: bot={botIndex + 1}, selection={outfitSelection}, error={ex.GetBaseException().Message}");
        }
    }

    internal static void ApplyNamePlate(
        PlayerControl bot,
        PlayerControl? host,
        int botIndex,
        int selection,
        ManualLogSource log)
    {
        if (!bot || bot.Data is null || selection <= 0)
        {
            return;
        }

        try
        {
            if (selection == 2 && host is not null && host && host.Data is not null)
            {
                bot.RpcSetNamePlate(host.Data.DefaultOutfit.NamePlateId ?? string.Empty);
                return;
            }

            var manager = HatManager.Instance;
            if (!manager)
            {
                log.LogWarning($"DeepBot nameplate held: bot={botIndex + 1}, HatManager unavailable.");
                return;
            }

            var namePlate = selection == 1
                ? FindEmptyProduct(manager.GetUnlockedNamePlates(), bot.Data.DefaultOutfit.NamePlateId)
                : SelectProduct(
                    manager.GetUnlockedNamePlates(),
                    selection == 3
                        ? (AmongUsClient.Instance?.GameId ?? 0) * 37 + botIndex * 103 + 19
                        : (selection - 4) * 89 + botIndex * 17 + 31,
                    bot.Data.DefaultOutfit.NamePlateId);
            bot.RpcSetNamePlate(namePlate ?? string.Empty);
        }
        catch (Exception ex)
        {
            log.LogWarning($"DeepBot nameplate apply failed: bot={botIndex + 1}, selection={selection}, error={ex.GetBaseException().Message}");
        }
    }

    private static void ApplyIds(PlayerControl bot, string hat, string skin, string visor, string pet)
    {
        bot.RpcSetHat(hat ?? string.Empty);
        bot.RpcSetSkin(skin ?? string.Empty);
        bot.RpcSetVisor(visor ?? string.Empty);
        bot.RpcSetPet(pet ?? string.Empty);
    }

    private static string SelectProduct<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T> items, int seed, string fallback)
        where T : CosmeticData
    {
        if (items is null || items.Length == 0)
        {
            return fallback;
        }

        var index = (seed & int.MaxValue) % items.Length;
        return items[index]?.ProductId ?? fallback;
    }

    private static string FindEmptyProduct<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T> items, string fallback)
        where T : CosmeticData
    {
        if (items is null || items.Length == 0)
        {
            return fallback;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var productId = items[i]?.ProductId;
            if (!string.IsNullOrWhiteSpace(productId) &&
                (productId.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
                 productId.Contains("none", StringComparison.OrdinalIgnoreCase) ||
                 productId.Contains("nohat", StringComparison.OrdinalIgnoreCase)))
            {
                return productId;
            }
        }

        return items[0]?.ProductId ?? fallback;
    }
}

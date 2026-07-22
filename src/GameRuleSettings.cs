using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal static class GameRuleSettings
{
    private static readonly Type? GameOptionsManagerType = typeof(PlayerControl).Assembly.GetType("GameOptionsManager", throwOnError: false);
    private static readonly Type? FloatOptionNamesType = typeof(PlayerControl).Assembly.GetType(
        "AmongUs.GameOptions.FloatOptionNames",
        throwOnError: false);
    private static readonly Type? Int32OptionNamesType = typeof(PlayerControl).Assembly.GetType(
        "AmongUs.GameOptions.Int32OptionNames",
        throwOnError: false);

    internal static bool OptionTypesResolved =>
        GameOptionsManagerType is not null &&
        FloatOptionNamesType is not null &&
        Int32OptionNamesType is not null;

    internal static float GetCrewVision(float fallback)
    {
        return Mathf.Max(0.1f, TryGetFloat(fallback, "CrewLightMod", "CrewmateVision", "CrewVision"));
    }

    internal static int GetNumImpostors(int fallback)
    {
        return Mathf.Clamp(TryGetInt(fallback, "NumImpostors"), 1, 3);
    }

    internal static float GetImpostorVision(float fallback)
    {
        return Mathf.Max(0.1f, TryGetFloat(fallback, "ImpostorLightMod", "ImpostorVision"));
    }

    internal static float GetKillDistance(float fallback)
    {
        var index = TryGetInt(-1, "KillDistance");
        return index switch
        {
            0 => 1.0f,
            1 => 1.8f,
            2 => 2.5f,
            _ => Mathf.Max(0.1f, fallback)
        };
    }

    internal static float GetKillCooldown(float fallback)
    {
        return Mathf.Max(0f, TryGetFloat(fallback, "KillCooldown"));
    }

    internal static float GetPlayerSpeed(float fallback)
    {
        return Mathf.Clamp(TryGetFloat(fallback, "PlayerSpeedMod"), 0.25f, 3f);
    }

    internal static int GetDiscussionTime(int fallback)
    {
        return Mathf.Max(0, TryGetInt(fallback, "DiscussionTime"));
    }

    internal static int GetVotingTime(int fallback)
    {
        return Mathf.Max(0, TryGetInt(fallback, "VotingTime"));
    }

    internal static int GetEmergencyCooldown(int fallback)
    {
        return Mathf.Max(0, TryGetInt(fallback, "EmergencyCooldown"));
    }

    internal static RoomRuleSnapshot CaptureSnapshot()
    {
        return new RoomRuleSnapshot(
            GetMapId(),
            GetNumImpostors(1),
            GetKillCooldown(30f),
            GetKillDistance(1.8f),
            GetPlayerSpeed(1f),
            GetCrewVision(1f),
            GetImpostorVision(1.5f),
            GetDiscussionTime(15),
            GetVotingTime(120),
            GetEmergencyCooldown(15),
            GetCrewmateTaskCount(0));
    }

    internal static int GetCrewmateTaskCount(int fallback)
    {
        var common = TryGetInt(-1, "NumCommonTasks", "CommonTasks");
        var longTasks = TryGetInt(-1, "NumLongTasks", "LongTasks");
        var shortTasks = TryGetInt(-1, "NumShortTasks", "ShortTasks");
        if (common >= 0 && longTasks >= 0 && shortTasks >= 0)
        {
            return Mathf.Max(0, common + longTasks + shortTasks);
        }

        return Mathf.Max(0, fallback);
    }

    internal static int GetMapId(int fallback = 0)
    {
        var currentOptions = GetCurrentOptions();
        if (currentOptions is null)
        {
            return fallback;
        }

        var type = currentOptions.GetType();
        var property = AccessTools.Property(type, "MapId") ?? AccessTools.Property(type, "mapId");
        if (property is not null)
        {
            try
            {
                return Convert.ToInt32(property.GetValue(currentOptions));
            }
            catch
            {
                return fallback;
            }
        }

        var field = AccessTools.Field(type, "MapId") ?? AccessTools.Field(type, "mapId");
        if (field is null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToInt32(field.GetValue(currentOptions));
        }
        catch
        {
            return fallback;
        }
    }

    internal static bool IsSkeldMap()
    {
        return GetMapId() == 0;
    }

    private static int TryGetInt(int fallback, params string[] optionNames)
    {
        var value = TryInvokeOptionGetter("GetInt", Int32OptionNamesType, optionNames);
        return value is int result ? result : fallback;
    }

    private static float TryGetFloat(float fallback, params string[] optionNames)
    {
        var value = TryInvokeOptionGetter("GetFloat", FloatOptionNamesType, optionNames);
        return value is float result ? result : fallback;
    }

    private static object? TryInvokeOptionGetter(string methodName, Type? enumType, IReadOnlyList<string> optionNames)
    {
        var currentOptions = GetCurrentOptions();
        if (currentOptions is null || enumType is null || !enumType.IsEnum)
        {
            return null;
        }

        var method = FindOptionMethod(currentOptions.GetType(), methodName, enumType);
        if (method is null)
        {
            return null;
        }

        for (var i = 0; i < optionNames.Count; i++)
        {
            var option = ParseEnumValue(enumType, optionNames[i]);
            if (option is null)
            {
                continue;
            }

            try
            {
                return method.Invoke(currentOptions, [option]);
            }
            catch
            {
                // Some game versions expose names but reject access before options are initialized.
            }
        }

        return null;
    }

    private static object? GetCurrentOptions()
    {
        var singleton = GetSingleton(GameOptionsManagerType);
        if (singleton is null)
        {
            return null;
        }

        var type = singleton.GetType();
        var property = AccessTools.Property(type, "CurrentGameOptions") ?? AccessTools.Property(type, "currentGameOptions");
        if (property is not null)
        {
            try
            {
                return property.GetValue(singleton);
            }
            catch
            {
                return null;
            }
        }

        var field = AccessTools.Field(type, "CurrentGameOptions") ?? AccessTools.Field(type, "currentGameOptions");
        if (field is null)
        {
            return null;
        }

        try
        {
            return field.GetValue(singleton);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetSingleton(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        var property = AccessTools.Property(type, "Instance");
        if (property is not null)
        {
            try
            {
                return property.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        var field = AccessTools.Field(type, "Instance");
        if (field is null)
        {
            return null;
        }

        try
        {
            return field.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? FindOptionMethod(Type optionsType, string methodName, Type enumType)
    {
        var methods = optionsType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var method in methods)
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == enumType)
            {
                return method;
            }
        }

        return null;
    }

    private static object? ParseEnumValue(Type enumType, string value)
    {
        try
        {
            return Enum.IsDefined(enumType, value) ? Enum.Parse(enumType, value) : null;
        }
        catch
        {
            return null;
        }
    }
}

internal readonly record struct RoomRuleSnapshot(
    int MapId,
    int NumImpostors,
    float KillCooldown,
    float KillDistance,
    float PlayerSpeed,
    float CrewVision,
    float ImpostorVision,
    int DiscussionTime,
    int VotingTime,
    int EmergencyCooldown,
    int CrewmateTaskCount)
{
    internal string Describe()
    {
        return $"map={MapId}, impostors={NumImpostors}, killCooldown={KillCooldown:0.0}s, " +
               $"killDistance={KillDistance:0.00}, speed={PlayerSpeed:0.00}x, " +
               $"crewVision={CrewVision:0.00}x, impostorVision={ImpostorVision:0.00}x, " +
               $"discussion={DiscussionTime}s, voting={VotingTime}s, emergencyCooldown={EmergencyCooldown}s, " +
               $"tasksPerCrew={CrewmateTaskCount}";
    }
}

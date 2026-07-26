using System;
using System.Collections.Generic;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal readonly record struct NavigationBounds(float MinX, float MaxX, float MinY, float MaxY)
{
    public bool Contains(Vector2 point)
    {
        return float.IsFinite(point.x) &&
               float.IsFinite(point.y) &&
               point.x >= MinX &&
               point.x <= MaxX &&
               point.y >= MinY &&
               point.y <= MaxY;
    }
}

internal sealed class MapNavigationProfile
{
    private MapNavigationProfile(
        int mapId,
        string name,
        string primarySpawnNodeId,
        NavigationBounds bounds,
        float minimumNamedNodeCoverage,
        IReadOnlyList<string> spawnNodeIds,
        IReadOnlyList<string> fakeTaskNodeIds)
    {
        MapId = mapId;
        Name = name;
        PrimarySpawnNodeId = primarySpawnNodeId;
        Bounds = bounds;
        MinimumNamedNodeCoverage = minimumNamedNodeCoverage;
        SpawnNodeIds = spawnNodeIds;
        FakeTaskNodeIds = fakeTaskNodeIds;
    }

    public int MapId { get; }
    public string Name { get; }
    public string PrimarySpawnNodeId { get; }
    public NavigationBounds Bounds { get; }
    public float MinimumNamedNodeCoverage { get; }
    public IReadOnlyList<string> SpawnNodeIds { get; }
    public IReadOnlyList<string> FakeTaskNodeIds { get; }

    public string EmergencyNode(TaskTypes type)
    {
        if (MapId == 1)
        {
            if (type is TaskTypes.ResetReactor or TaskTypes.ResetSeismic) return "MIRA_REACTOR_CORE";
            if (type == TaskTypes.RestoreOxy) return "MIRA_GREENHOUSE_O2";
            if (type == TaskTypes.FixComms) return "MIRA_COMMS_PANEL";
            if (type == TaskTypes.FixLights) return "MIRA_OFFICE_LIGHTS";
            return "MIRA_CAFE_BUTTON";
        }

        if (type is TaskTypes.ResetReactor or TaskTypes.ResetSeismic) return "REACTOR_MID";
        if (type == TaskTypes.RestoreOxy) return "O2_CENTER";
        if (type == TaskTypes.FixComms) return "COMMS_CENTER";
        if (type == TaskTypes.FixLights) return "ELEC_SWITCH";
        return "CAF_TABLE";
    }

    public string TaskNode(TaskTypes type)
    {
        var text = type.ToString();
        if (MapId == 1)
        {
            if (Contains(text, "Reactor") || Contains(text, "Manifold")) return "MIRA_REACTOR_MANIFOLD";
            if (Contains(text, "O2") || Contains(text, "Chute") || Contains(text, "Plant")) return "MIRA_GREENHOUSE_O2";
            if (Contains(text, "Navigation") || Contains(text, "Chart")) return "MIRA_ADMIN_CHART";
            if (Contains(text, "Weapon") || Contains(text, "Asteroid")) return "MIRA_BALCONY_ASTEROIDS";
            if (Contains(text, "Admin") || Contains(text, "Card") || Contains(text, "Id")) return "MIRA_ADMIN_TABLE";
            if (Contains(text, "Electrical") || Contains(text, "Wiring") || Contains(text, "Power")) return "MIRA_LOCKER_WIRES";
            if (Contains(text, "Med") || Contains(text, "Scan") || Contains(text, "Diagnostic")) return "MIRA_MED_SCAN";
            if (Contains(text, "Security") || Contains(text, "DoorLog")) return "MIRA_COMMS_PANEL";
            if (Contains(text, "Shield")) return "MIRA_ADMIN_TABLE";
            if (Contains(text, "Comms") || Contains(text, "Upload") || Contains(text, "Download")) return "MIRA_COMMS_UPLOAD";
            if (Contains(text, "Engine") || Contains(text, "Fuel")) return "MIRA_LAUNCHPAD_FUEL";
            if (Contains(text, "Vending")) return "MIRA_CAFE_VENDING";
            if (Contains(text, "Artifact") || Contains(text, "Sort")) return "MIRA_LAB_SORT";
            return "MIRA_STORAGE_CENTER";
        }

        if (Contains(text, "Reactor") || Contains(text, "Manifold")) return "REACTOR_MID";
        if (Contains(text, "O2") || Contains(text, "Chute")) return "O2_CENTER";
        if (Contains(text, "Navigation") || Contains(text, "Chart")) return "NAV_CENTER";
        if (Contains(text, "Weapon") || Contains(text, "Asteroid")) return "WEAP_CENTER";
        if (Contains(text, "Admin") || Contains(text, "Card")) return "ADMIN_CARD";
        if (Contains(text, "Electrical") || Contains(text, "Wiring")) return "ELEC_CENTER";
        if (Contains(text, "Med") || Contains(text, "Scan")) return "MED_SCAN";
        if (Contains(text, "Security")) return "SEC_CENTER";
        if (Contains(text, "Shield")) return "SHIELD_CENTER";
        if (Contains(text, "Comms") || Contains(text, "Upload")) return "COMMS_CENTER";
        if (Contains(text, "Engine") || Contains(text, "Fuel")) return "LOWER_ENGINE_M";
        return "STOR_CENTER";
    }

    public static bool IsSupported(int mapId)
    {
        return mapId is 0 or 1;
    }

    public static MapNavigationProfile ForMap(int mapId)
    {
        return mapId == 1 ? Mira : Skeld;
    }

    private static bool Contains(string text, string value)
    {
        return text.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly MapNavigationProfile Skeld = new(
        0,
        "The Skeld",
        "CAF_SPAWN",
        new NavigationBounds(-23.8f, 18.8f, -18.8f, 7.2f),
        0.82f,
        ["CAF_SPAWN", "CAF_TABLE_N", "CAF_UL", "CAF_UR", "CAF_BOTTOM"],
        [
            "WEAP_DOWNLOAD", "NAV_STEER", "NAV_DOWNLOAD", "O2_FILTER", "UPPER_FUEL",
            "LOWER_FUEL", "ELEC_WIRES", "ADMIN_CARD", "COMMS_UPLOAD", "MED_SAMPLE"
        ]);

    private static readonly MapNavigationProfile Mira = new(
        1,
        "MIRA HQ",
        "MIRA_LAUNCHPAD_SPAWN",
        new NavigationBounds(-7.5f, 29.5f, -5.5f, 28.5f),
        0.80f,
        [
            "MIRA_LAUNCHPAD_SPAWN", "MIRA_LAUNCHPAD_N", "MIRA_LAUNCHPAD_S",
            "MIRA_LAUNCHPAD_E", "MIRA_LAUNCHPAD_FUEL"
        ],
        [
            "MIRA_LAUNCHPAD_FUEL", "MIRA_MED_SCAN", "MIRA_LOCKER_WIRES",
            "MIRA_LAB_SORT", "MIRA_REACTOR_MANIFOLD", "MIRA_ADMIN_CHART",
            "MIRA_GREENHOUSE_O2", "MIRA_CAFE_VENDING", "MIRA_BALCONY_ASTEROIDS",
            "MIRA_COMMS_UPLOAD"
        ]);
}

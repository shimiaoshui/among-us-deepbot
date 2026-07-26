using System.Collections;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using UnityEngine;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.ShowRole))]
[HarmonyAfter("me.eisbison.theotherroles")]
internal static class TorIntroPresentationPatch
{
    private const float SpecificRoleRefreshWindowSeconds = 12f;
    private static IntroCutscene? _activeIntro;
    private static float _specificRoleRefreshUntil;
    private static string _lastSpecificApplied = string.Empty;
    private static string _lastFactionApplied = string.Empty;
    [HarmonyPatch(typeof(IntroCutscene), "BeginCrewmate")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void BeginCrewmatePostfix(IntroCutscene __instance)
    {
        BeginSpecificRoleRefresh(__instance);
        ApplyFaction(__instance);
        __instance.StartCoroutine(RefreshFactionAfterTor(__instance).WrapToIl2Cpp());
    }

    [HarmonyPatch(typeof(IntroCutscene), "BeginImpostor")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void BeginImpostorPostfix(IntroCutscene __instance)
    {
        BeginSpecificRoleRefresh(__instance);
        ApplyFaction(__instance);
        __instance.StartCoroutine(RefreshFactionAfterTor(__instance).WrapToIl2Cpp());
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(IntroCutscene __instance)
    {
        ApplySpecificRole(__instance);
        __instance.StartCoroutine(RefreshAfterTor(__instance).WrapToIl2Cpp());
    }

    private static IEnumerator RefreshAfterTor(IntroCutscene intro)
    {
        // TOR refreshes the role text for about one second. Virtual players can
        // also receive the custom assignment slightly later than the base role.
        // Re-read TOR's authoritative RoleInfo throughout the visible second
        // banner so the final value is the concrete role, not its faction.
        foreach (var delay in new[] { 0.15f, 0.25f, 0.40f, 0.55f, 0.75f })
        {
            yield return new WaitForSeconds(delay);
            ApplySpecificRole(intro);
        }
    }

    private static IEnumerator RefreshFactionAfterTor(IntroCutscene intro)
    {
        foreach (var delay in new[] { 0.10f, 0.25f, 0.45f })
        {
            yield return new WaitForSeconds(delay);
            ApplyFaction(intro);
        }
    }

    internal static void RefreshVisibleSpecificRole()
    {
        // In current Among Us builds ShowRole() is only the coroutine factory.
        // Harmony postfixes on that method run while the faction banner is still
        // visible, before the generated coroutine writes the base-game role on
        // the later concrete-role banner. Keep the authoritative TOR role bound
        // for the short interval where that later banner is actually visible.
        var intro = _activeIntro;
        if (intro is null || !intro || Time.realtimeSinceStartup > _specificRoleRefreshUntil)
        {
            return;
        }

        ApplySpecificRole(intro);
    }

    private static void BeginSpecificRoleRefresh(IntroCutscene intro)
    {
        _activeIntro = intro;
        _specificRoleRefreshUntil = Time.realtimeSinceStartup + SpecificRoleRefreshWindowSeconds;
        _lastSpecificApplied = string.Empty;
    }

    private static void ApplyFaction(IntroCutscene intro)
    {
        var rawLocal = PlayerControl.LocalPlayer;
        var local = DeepBotIdentity.FindLocalHumanPlayer();
        if (!intro || !local || !TorRoleAdapter.TryGetIntroRole(local, out var role))
        {
            return;
        }

        // IntroCutscene is an IL2CPP type. Its generated interop surface
        // exposes these native fields as managed properties, but Harmony's
        // AccessTools.Field cannot see them as ordinary managed FieldInfo
        // objects. Read the generated properties explicitly instead.
        var teamTitle = GetIntroMember(intro, "TeamTitle");
        if (teamTitle is null)
        {
            Plugin.LogSource.LogWarning("DeepBot TOR faction intro: IntroCutscene.TeamTitle is null.");
            return;
        }

        var factionLabel = ResolveFactionLabel(role.Alignment);
        SetMember(teamTitle, "text", factionLabel);
        if (string.Equals(role.Alignment, "neutral", StringComparison.Ordinal))
        {
            SetMember(teamTitle, "color", new Color(76f / 255f, 84f / 255f, 78f / 255f, 1f));
        }

        var applied = $"{local!.PlayerId}:{factionLabel}:{role.Name}";
        if (!string.Equals(applied, _lastFactionApplied, StringComparison.Ordinal))
        {
            _lastFactionApplied = applied;
            Plugin.LogSource.LogInfo(
                $"DeepBot TOR faction intro corrected: player={local.Data?.PlayerName}({local.PlayerId}), " +
                $"rawLocal={rawLocal?.Data?.PlayerName ?? "missing"}({rawLocal?.PlayerId.ToString() ?? "none"}), " +
                $"faction={role.Alignment}, specificRole={role.Name}, roleId={role.RoleId}.");
        }
    }

    internal static void ApplySpecificRole(IntroCutscene intro)
    {
        var rawLocal = PlayerControl.LocalPlayer;
        var local = DeepBotIdentity.FindLocalHumanPlayer();
        if (!intro || !local)
        {
            return;
        }

        if (!TorRoleAdapter.TryGetIntroRole(local, out var role))
        {
            return;
        }

        // See ApplyFaction: use generated IL2CPP properties instead of
        // AccessTools.Field, otherwise this path is a no-op on the live game.
        var roleText = GetIntroMember(intro, "RoleText");
        var roleBlurbText = GetIntroMember(intro, "RoleBlurbText");
        if (roleText is null || roleBlurbText is null)
        {
            Plugin.LogSource.LogWarning("DeepBot TOR specific-role intro: RoleText or RoleBlurbText is null.");
            return;
        }

        SetMember(roleText, "text", role.Name);
        SetMember(roleText, "color", role.Color);

        var existingBlurb = GetMember(roleBlurbText, "text") as string ?? string.Empty;
        var modifierSuffixIndex = existingBlurb.IndexOf('\n');
        var modifierSuffix = modifierSuffixIndex >= 0
            ? existingBlurb[modifierSuffixIndex..]
            : string.Empty;
        SetMember(roleBlurbText, "text", role.Description + modifierSuffix);
        SetMember(roleBlurbText, "color", role.Color);

        var actualRoleName = GetMember(roleText, "text") as string ?? string.Empty;

        var applied = $"{local!.PlayerId}:{role.Name}:{role.RoleId}";
        if (!string.Equals(applied, _lastSpecificApplied, StringComparison.Ordinal))
        {
            _lastSpecificApplied = applied;
            Plugin.LogSource.LogInfo(
                $"DeepBot TOR specific-role intro corrected: player={local.Data?.PlayerName}({local.PlayerId}), " +
                $"rawLocal={rawLocal?.Data?.PlayerName ?? "missing"}({rawLocal?.PlayerId.ToString() ?? "none"}), " +
                $"faction={role.Alignment}, specificRole={role.Name}, roleId={role.RoleId}, " +
                $"expected={role.Name}, actual={actualRoleName}.");
        }
    }

    internal static void LogSelfTest(BepInEx.Logging.ManualLogSource log)
    {
        var correct = ResolveFactionLabel("crewmate") == "船员" &&
                      ResolveFactionLabel("impostor") == "内鬼" &&
                      ResolveFactionLabel("neutral") == "中立";
        log.LogInfo(
            $"DeepBot TOR two-stage intro self-test: level={(correct ? "ok" : "error")}, " +
            $"factionStage=alignment, roleStage=authoritative-TOR-RoleInfo, " +
            $"introWindowRefresh={SpecificRoleRefreshWindowSeconds:F0}s.");
    }

    private static string ResolveFactionLabel(string alignment)
    {
        return alignment switch
        {
            "neutral" => "中立",
            "impostor" => "内鬼",
            _ => "船员"
        };
    }

    private static object? GetMember(object instance, string memberName)
    {
        var type = instance.GetType();
        var property = AccessTools.Property(type, memberName);
        if (property is not null)
        {
            return property.GetValue(instance);
        }

        return AccessTools.Field(type, memberName)?.GetValue(instance);
    }

    private static object? GetIntroMember(IntroCutscene intro, string memberName)
    {
        return AccessTools.Property(typeof(IntroCutscene), memberName)?.GetValue(intro) ??
               AccessTools.Field(typeof(IntroCutscene), memberName)?.GetValue(intro);
    }

    private static void SetMember(object instance, string memberName, object value)
    {
        var type = instance.GetType();
        var property = AccessTools.Property(type, memberName);
        if (property is not null && property.CanWrite)
        {
            property.SetValue(instance, CoerceMemberValue(value, property.PropertyType));
            return;
        }

        var field = AccessTools.Field(type, memberName);
        if (field is not null)
        {
            field.SetValue(instance, CoerceMemberValue(value, field.FieldType));
        }
    }

    private static object CoerceMemberValue(object value, Type targetType)
    {
        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(Color) && value is Color32 color32)
        {
            return (Color)color32;
        }

        if (targetType == typeof(Color32) && value is Color color)
        {
            return (Color32)color;
        }

        return value;
    }
}

/// <summary>
/// TOR does not rely solely on IntroCutscene.ShowRole. Its own SetRoleTexts
/// patch writes the concrete role immediately and again one second later. On
/// the IL2CPP build the ShowRole postfix is not consistently invoked, so hook
/// the actual TOR writer and apply our authoritative role after it.
/// </summary>
[HarmonyPatch]
internal static class TorAuthoritativeRoleTextPatch
{
    private static bool _latePatched;

    private static MethodBase? TargetMethod()
    {
        var torAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, "TheOtherRoles", StringComparison.Ordinal));
        var torPatchType = torAssembly?.GetType(
                "TheOtherRoles.Patches.IntroPatch+SetUpRoleTextPatch", false) ??
            torAssembly?.GetType(
                "TheOtherRoles.Patches.IntroPatch/SetUpRoleTextPatch", false);
        if (torPatchType is null && torAssembly is not null)
        {
            try
            {
                torPatchType = torAssembly.GetTypes().FirstOrDefault(type =>
                    string.Equals(type.Name, "SetUpRoleTextPatch", StringComparison.Ordinal));
            }
            catch (ReflectionTypeLoadException)
            {
                // The live IL2CPP bridge may expose unloadable helper types;
                // the two exact nested names above remain the safe path.
            }
        }

        return torPatchType is null
            ? null
            : AccessTools.Method(torPatchType, "SetRoleTexts", [typeof(IntroCutscene)]);
    }

    internal static void TryApplyLate(Harmony harmony)
    {
        if (_latePatched)
        {
            return;
        }

        var target = TargetMethod();
        if (target is null)
        {
            Plugin.LogSource.LogWarning(
                "DeepBot TOR role-text patch target still unavailable after TOR adapter initialization.");
            return;
        }

        harmony.Patch(
            target,
            postfix: new HarmonyMethod(
                typeof(TorAuthoritativeRoleTextPatch), nameof(Postfix)));
        _latePatched = true;
        Plugin.LogSource.LogInfo(
            $"DeepBot TOR role-text patch applied late: {target.DeclaringType?.FullName}.{target.Name}.");
    }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        var target = TargetMethod();
        if (target is null)
        {
            Plugin.LogSource.LogInfo(
                "DeepBot TOR role-text patch skipped: SetUpRoleTextPatch.SetRoleTexts not available.");
            return false;
        }

        Plugin.LogSource.LogInfo(
            $"DeepBot TOR role-text patch target resolved: {target.DeclaringType?.FullName}.{target.Name}.");
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    // SetRoleTexts is a static helper in TOR. Harmony's special __instance
    // parameter is therefore not populated for this target; the cutscene is
    // the method's first ordinary argument (__0).
    private static void Postfix(IntroCutscene __0)
    {
        TorIntroPresentationPatch.ApplySpecificRole(__0);
    }
}

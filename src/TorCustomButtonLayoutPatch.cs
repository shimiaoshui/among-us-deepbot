using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AmongUsDeepSeekBots;

/// <summary>
/// TOR owns every custom-role button and its native callbacks/cooldowns. This
/// patch changes only the final transform slot when an active native vent,
/// kill, ability or report button occupies the same HUD position.
/// </summary>
[HarmonyPatch]
internal static class TorCustomButtonLayoutPatch
{
    private static bool _latePatched;
    private static Type? _customButtonType;
    private static FieldInfo? _actionButtonField;
    private static FieldInfo? _buttonsField;
    private static readonly HashSet<int> RelocationLogged = [];

    private static MethodBase? TargetMethod()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                "TheOtherRoles",
                StringComparison.OrdinalIgnoreCase));
        _customButtonType = assembly?.GetType("TheOtherRoles.Objects.CustomButton", false);
        _actionButtonField = _customButtonType?.GetField(
            "actionButton",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _buttonsField = _customButtonType?.GetField(
            "buttons",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return _customButtonType?.GetMethod(
            "Update",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);
    }

    internal static void TryApplyLate(Harmony harmony)
    {
        if (_latePatched) return;
        var target = TargetMethod();
        if (target is null)
        {
            Plugin.LogSource.LogWarning("DeepBot TOR custom-button layout target unavailable.");
            return;
        }

        harmony.Patch(
            target,
            postfix: new HarmonyMethod(typeof(TorCustomButtonLayoutPatch), nameof(Postfix))
            {
                priority = Priority.Last
            });
        _latePatched = true;
        Plugin.LogSource.LogInfo(
            "DeepBot TOR custom-button collision layout guard applied; native TOR callbacks and cooldowns preserved.");
    }

    [HarmonyPrepare]
    private static bool Prepare() => TargetMethod() is not null;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance)
    {
        if (_actionButtonField?.GetValue(__instance) is not ActionButton button ||
            !button ||
            !button.gameObject.activeInHierarchy ||
            !DestroyableSingleton<HudManager>.InstanceExists)
        {
            return;
        }

        var hud = DestroyableSingleton<HudManager>.Instance;
        if (!hud.UseButton) return;

        var occupied = new List<Vector3>();
        AddNativeButton(occupied, hud.ImpostorVentButton, button);
        AddNativeButton(occupied, hud.KillButton, button);
        AddNativeButton(occupied, hud.AbilityButton, button);
        AddNativeButton(occupied, hud.SecondaryAbilityButton, button);
        AddNativeButton(occupied, hud.ReportButton, button);
        AddNativeButton(occupied, hud.UseButton, button);
        AddNativeButton(occupied, hud.PetButton, button);
        AddNativeButton(occupied, hud.SabotageButton, button);

        if (_buttonsField?.GetValue(null) is IEnumerable customButtons)
        {
            foreach (var item in customButtons)
            {
                if (ReferenceEquals(item, __instance)) break;
                if (item is null || _actionButtonField.GetValue(item) is not ActionButton previous ||
                    !previous || !previous.gameObject.activeInHierarchy)
                {
                    continue;
                }
                occupied.Add(previous.transform.localPosition);
            }
        }

        var current = button.transform.localPosition;
        if (!Overlaps(current, occupied)) return;

        var basePosition = hud.UseButton.transform.localPosition;
        var candidates = new[]
        {
            basePosition + new Vector3(-2f, -0.06f, 0f),
            basePosition + new Vector3(-3f, -0.06f, 0f),
            basePosition + new Vector3(-4f, -0.06f, 0f),
            basePosition + new Vector3(0f, 1f, 0f),
            basePosition + new Vector3(-1f, 1f, 0f),
            basePosition + new Vector3(-2f, 1f, 0f),
            basePosition + new Vector3(-3f, 1f, 0f)
        };
        var target = candidates
            .Where(candidate => !Overlaps(candidate, occupied))
            .OrderBy(candidate => Vector2.Distance(candidate, current))
            .FirstOrDefault(current);
        if (Vector2.Distance(target, current) < 0.05f) return;

        button.transform.localPosition = target;
        var identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);
        if (RelocationLogged.Add(identity))
        {
            Plugin.LogSource.LogInfo(
                $"DeepBot relocated overlapping TOR role button: from={current}, to={target}; " +
                "button logic remains TOR-native.");
        }
    }

    private static void AddNativeButton(List<Vector3> occupied, ActionButton? candidate, ActionButton current)
    {
        if (candidate && candidate != current && candidate!.gameObject.activeInHierarchy)
        {
            occupied.Add(candidate.transform.localPosition);
        }
    }

    private static bool Overlaps(Vector3 candidate, IEnumerable<Vector3> occupied) =>
        occupied.Any(position => Vector2.Distance(candidate, position) < 0.58f);
}

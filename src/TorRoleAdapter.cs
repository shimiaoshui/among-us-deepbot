using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Hazel;
using UnityEngine;

namespace AmongUsDeepSeekBots;

/// <summary>
/// Optional bridge to The Other Roles. The bridge deliberately uses reflection so
/// DeepBot can still be installed without TOR and so BepInEx can choose either
/// plugin load order without producing an assembly dependency failure.
/// </summary>
internal static class TorRoleAdapter
{
    private const string TorAssemblyName = "TheOtherRoles";
    private const string TorRootTypeName = "TheOtherRoles.TheOtherRoles";
    private const string TorRpcProcedureTypeName = "TheOtherRoles.RPCProcedure";
    private const string TorHelpersTypeName = "TheOtherRoles.Helpers";
    private const string TorEngineerVentRulesTypeName = "TheOtherRoles.Patches.EngineerVentRules";
    private const float VirtualTrapScanIntervalSeconds = 0.10f;

    private static readonly RoleSpec[] RoleSpecs =
    [
        new("Jester", "jester", "neutral", false, "Get suspected and voted out while avoiding an obvious self-report or confession."),
        new("Portalmaker", "portalmaker", "crewmate", true, "Place the two portals in separated, useful rooms so they shorten later task and emergency routes."),
        new("Mayor", "mayor", "crewmate", true, "Use voting influence and remote meetings only when evidence justifies it."),
        new("Engineer", "engineer", "crewmate", true, "Save limited instant repairs for a dangerous sabotage or a last-second rescue."),
        new("Godfather", "godfather", "impostor", false, "Coordinate kills and deception while preserving mafia cover."),
        new("Mafioso", "mafioso", "impostor", false, "Support the Godfather and kill only when the role permits it."),
        new("Janitor", "janitor", "impostor", true, "Clean a nearby unwitnessed body to erase reportable evidence."),
        new("Sheriff", "sheriff", "crewmate", true, "Shoot only a strongly evidenced hostile role because a wrong shot can be fatal."),
        new("Deputy", "deputy", "crewmate", true, "Handcuff a strongly suspicious nearby player when limiting their ability has clear value."),
        new("Lighter", "lighter", "crewmate", false, "Use improved vision to verify routes and danger during darkness."),
        new("Detective", "detective", "crewmate", false, "Follow physical evidence and report only observations personally available."),
        new("TimeMaster", "timeMaster", "crewmate", true, "Raise the time shield when nearby danger makes a rewind likely to save someone."),
        new("Medic", "medic", "crewmate", true, "Shield a useful or vulnerable player based on evidence, isolation, and likely attack risk."),
        new("Swapper", "swapper", "crewmate", false, "Swap meeting votes only when it improves an evidence-based outcome."),
        new("Seer", "seer", "crewmate", false, "Use soul information to strengthen later deductions without inventing sightings."),
        new("Morphling", "morphling", "impostor", true, "Copy a credible target while unseen before a planned frame, escape, or kill."),
        new("Camouflager", "camouflager", "impostor", true, "Camouflage players to conceal a planned kill or break reliable visual identification."),
        new("Hacker", "hacker", "crewmate", true, "Spend limited information charges when room occupancy or vitals resolves uncertainty."),
        new("Tracker", "tracker", "crewmate", true, "Track a trusted or suspicious player whose later route will provide useful evidence."),
        new("Vampire", "vampire", "impostor", true, "Delay-kill an isolated target when the bite will not immediately expose the attacker."),
        new("Snitch", "snitch", "crewmate", false, "Finish tasks while managing the danger created when hostile roles learn your identity."),
        new("Jackal", "jackal", "neutral", true, "First recruit a useful Sidekick when allowed; then isolate and kill all non-Jackal players while preserving both covers."),
        new("Sidekick", "sidekick", "neutral", true, "Support the Jackal, kill isolated legal opponents only if the room option allows it, and take over when the Jackal dies."),
        new("Eraser", "eraser", "impostor", true, "Schedule erasure of a high-value opposing role when evidence supports the target."),
        new("Spy", "spy", "crewmate", false, "Exploit impostor-facing information while maintaining a plausible route."),
        new("Trickster", "trickster", "impostor", true, "Build a separated Jack-in-the-box network, then use its darkness for a concrete escape or kill plan."),
        new("Cleaner", "cleaner", "impostor", true, "Clean a nearby unwitnessed body to remove evidence and delay a meeting."),
        new("Warlock", "warlock", "impostor", true, "Curse and redirect only when the forced kill can be controlled and concealed."),
        new("SecurityGuard", "securityGuard", "crewmate", true, "Spend room-configured screws on cameras at useful chokepoints where future information has real value."),
        new("Arsonist", "arsonist", "neutral", true, "Channel a full douse on every other living player one at a time; after all are doused, press ignite to win immediately."),
        new("BountyHunter", "bountyHunter", "impostor", false, "Prefer the bounty when safe but abandon it when pursuit would expose the role."),
        new("NiceGuesser", "niceGuesser", "crewmate", false, "Guess a role in a meeting only with strong evidence."),
        new("EvilGuesser", "evilGuesser", "impostor", false, "Use role knowledge to remove a dangerous opponent without exposing teammates."),
        new("Vulture", "vulture", "neutral", true, "Consume a nearby body only when it is safe and advances the independent win condition."),
        new("Medium", "medium", "crewmate", true, "Question a soul when its information can resolve an important uncertainty."),
        new("Prosecutor", "lawyer", "neutral", false, "Build an evidence-based case that gets the assigned target voted out without exposing the objective too early."),
        new("Lawyer", "lawyer", "neutral", false, "Defend the assigned client while building plausible alternative explanations."),
        new("Pursuer", "pursuer", "neutral", true, "Survive until a non-impostor victory; blank a dangerous nearby player's next kill or ability to protect survival."),
        new("Witch", "witch", "impostor", true, "Spell a high-value opponent whose delayed death will not directly reveal the Witch."),
        new("Ninja", "ninja", "impostor", true, "Mark, vanish, and strike only when the route provides a credible escape."),
        new("Thief", "thief", "neutral", true, "Kill an eligible hostile role to steal it and inherit that role's team objective; an ineligible target causes a fatal misfire."),
        new("Trapper", "trapper", "crewmate", true, "Place traps at informative chokepoints rather than arbitrary positions."),
        new("Bomber", "bomber", "impostor", true, "Plant a bomb where it creates a deliberate kill, split, or time-pressure plan."),
        new("Yoyo", "yoyo", "impostor", true, "Mark and return to a location to create a planned alibi or escape.")
    ];

    private static readonly ModifierSpec[] ModifierSpecs =
    [
        new("Lover", "Lovers", "lover1", false, "Keep the linked lover alive; their death can immediately decide your own fate."),
        new("Lover", "Lovers", "lover2", false, "Keep the linked lover alive; their death can immediately decide your own fate."),
        new("Bait", "Bait", "bait", true, "If killed, the killer may be forced to report; do not treat this as an active button."),
        new("Bloody", "Bloody", "bloody", true, "Your killer leaves a visible trail after killing you."),
        new("AntiTeleport", "AntiTeleport", "antiTeleport", true, "You resist role-driven teleports."),
        new("Tiebreaker", "Tiebreaker", "tiebreaker", false, "Your meeting vote resolves a tie, so avoid casual or random votes."),
        new("Sunglasses", "Sunglasses", "sunglasses", true, "Your vision is restricted; reason only from what this player could actually see."),
        new("Mini", "Mini", "mini", false, "You are protected while growing; survival rules change after reaching adulthood."),
        new("Vip", "Vip", "vip", true, "Your death produces a global signal that other players can use."),
        new("Invert", "Invert", "invert", true, "Movement input may be reversed by the modifier; navigation must compensate."),
        new("Chameleon", "Chameleon", "chameleon", true, "Standing still gradually conceals you; use or interpret that concealment carefully."),
        new("Shifter", "Shifter", "shifter", false, "Choose one strategically useful target and exchange roles according to TOR rules.")
    ];

    private static readonly HashSet<string> ImplementedActiveAbilityNames =
    [
        "Engineer", "Mayor", "Portalmaker", "Medic", "Sheriff", "Deputy", "Tracker", "TimeMaster",
        "Morphling", "Camouflager", "Hacker", "Medium", "Vampire", "Warlock", "Ninja", "Jackal",
        "Sidekick", "Arsonist", "Pursuer", "Thief", "Eraser", "Witch", "Shifter", "Cleaner",
        "Janitor", "Vulture", "Trapper", "Trickster", "SecurityGuard", "Bomber", "Yoyo"
    ];
    private static readonly HashSet<string> ImplementedMultiStageAbilityNames =
    [
        "Morphling", "Portalmaker", "Trickster", "Ninja", "Warlock", "Arsonist", "Vampire", "Witch", "Bomber", "Yoyo"
    ];

    private static readonly Dictionary<(string Type, string Field), FieldInfo?> FieldCache = [];
    private static Assembly? _assembly;
    private static Type? _rootType;
    private static Type? _rpcProcedureType;
    private static Type? _helpersType;
    private static MethodInfo? _checkMurderMethod;
    private static MethodInfo? _checkAndKillMethod;
    private static MethodInfo? _roleCanUseVentsMethod;
    private static ManualLogSource? _log;
    private static bool _availabilityLogged;
    private static readonly Dictionary<byte, string> LoggedAssignments = [];
    private static readonly Dictionary<byte, string> LoggedAssignmentConflicts = [];
    private static readonly Dictionary<byte, PendingDouse> PendingDouses = [];
    private static readonly Dictionary<byte, PendingVampireBite> PendingVampireBites = [];
    private static readonly Dictionary<byte, PendingWarlockCurse> PendingWarlockCurses = [];
    private static readonly Dictionary<byte, PendingWitchSpell> PendingWitchSpells = [];
    private static readonly Dictionary<byte, float> PendingRoleRoots = [];
    private static readonly Dictionary<byte, float> PendingNinjaReveals = [];
    private static readonly Dictionary<byte, float> PendingYoyoReturns = [];
    private static readonly Dictionary<byte, PendingPortalTeleport> PendingPortalTeleports = [];
    private static readonly Dictionary<byte, float> NextPortalUseAt = [];
    private static readonly Dictionary<(byte PlayerId, string Role), float> NextRoleAbilityAt = [];
    private static readonly Dictionary<(byte PlayerId, string Role), List<Vector2>> StrategicPlacements = [];
    private static Type? _trapType;
    private static bool _portalsWereEnabled;
    private static float _nextVirtualTrapScanAt;
    private static int _observedMatchSerial = -1;

    internal static bool IsAvailable => EnsureLoaded();

    internal static bool IsImpostorTeam(PlayerControl? player)
    {
        if (!player || player!.Data is null)
        {
            return false;
        }

        return TryGetRole(player, out var role)
            ? role.IsImpostorTeam
            : player.Data.Role?.IsImpostor == true;
    }

    internal static bool IsPendingVampireDelayedKill(PlayerControl? killer, PlayerControl? target)
    {
        if (!killer || !target || !EnsureLoaded())
        {
            return false;
        }

        var vampire = GetStaticField("Vampire", "vampire") as PlayerControl;
        var bitten = GetStaticField("Vampire", "bitten") as PlayerControl;
        return vampire &&
               bitten &&
               vampire!.PlayerId == killer!.PlayerId &&
               bitten!.PlayerId == target!.PlayerId &&
               killer.PlayerId != target.PlayerId;
    }

    internal static bool IsConcealedFromLivingObserver(PlayerControl? observer, PlayerControl? target)
    {
        if (!target || target!.Data is null || target.Data.IsDead || target.Data.Disconnected || !EnsureLoaded())
        {
            return false;
        }

        var ninja = GetStaticField("Ninja", "ninja") as PlayerControl;
        if (ninja &&
            ninja!.PlayerId == target.PlayerId &&
            (GetStaticBool("Ninja", "isInvisble") || GetStaticFloat("Ninja", "invisibleTimer") > 0.01f))
        {
            // TOR deliberately leaves a faint outline for dead players and
            // impostor teammates. Living opponents must not receive the
            // Ninja's position, route, actions, or murder identity.
            return !observer ||
                   observer!.Data is null ||
                   (!observer.Data.IsDead && !IsImpostorTeam(observer));
        }

        return TryGetChameleonVisibility(target!, out var visibility) &&
               visibility <= 0.251f;
    }

    internal static bool IsTorVisualConcealmentEffectActive(PlayerControl? target)
    {
        if (!target || !EnsureLoaded())
        {
            return false;
        }

        var ninja = GetStaticField("Ninja", "ninja") as PlayerControl;
        if (ninja &&
            ninja!.PlayerId == target!.PlayerId &&
            (GetStaticBool("Ninja", "isInvisble") || GetStaticFloat("Ninja", "invisibleTimer") > 0.01f))
        {
            return true;
        }

        // Chameleon fades progressively. Preserve the whole TOR-managed fade
        // instead of allowing the generic render-repair pass to force alpha=1.
        return TryGetChameleonVisibility(target!, out var visibility) && visibility < 0.995f;
    }

    private static bool TryGetChameleonVisibility(PlayerControl target, out float visibility)
    {
        visibility = 1f;
        if (GetStaticField("Chameleon", "chameleon") is not IEnumerable owners)
        {
            return false;
        }

        var ownsModifier = false;
        foreach (var value in owners)
        {
            if (value is PlayerControl owner && owner && owner.PlayerId == target.PlayerId)
            {
                ownsModifier = true;
                break;
            }
        }

        if (!ownsModifier)
        {
            return false;
        }

        try
        {
            visibility = Convert.ToSingle(InvokeRoleMethod("Chameleon", "visibility", target.PlayerId));
            return true;
        }
        catch
        {
            // If TOR changes the helper signature, preserving the renderer is
            // safer than falsely revealing a role-managed hidden player.
            visibility = GetStaticFloat("Chameleon", "minVisibility");
            return true;
        }
    }

    internal static int GetLobbyConfiguredBotCount(int fallback)
    {
        if (!EnsureLoaded() || _assembly is null)
        {
            return Mathf.Clamp(fallback, 1, 8);
        }

        try
        {
            var holder = _assembly.GetType("TheOtherRoles.CustomOptionHolder", false);
            var option = holder?.GetField("deepBotCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var getFloat = option?.GetType().GetMethod("getFloat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getFloat?.Invoke(option, null) is float value)
            {
                return Mathf.Clamp(Mathf.RoundToInt(value), 1, 8);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"DeepBot lobby bot-count option read failed; using config fallback: {ex.GetBaseException().Message}");
        }

        return Mathf.Clamp(fallback, 1, 8);
    }

    internal static DeepBotAppearanceSettings GetLobbyAppearance(int botIndex)
    {
        if (botIndex is < 0 or >= 8 || !EnsureLoaded() || _assembly is null)
        {
            return DeepBotAppearanceSettings.Default;
        }

        try
        {
            var holder = _assembly.GetType("TheOtherRoles.CustomOptionHolder", false);
            if (holder is null || ReadOptionSelection(holder, "deepBotAppearanceEnabled") <= 0)
            {
                return DeepBotAppearanceSettings.Default;
            }

            return new DeepBotAppearanceSettings(
                ReadArrayOptionSelection(holder, "deepBotNames", botIndex),
                ReadArrayOptionSelection(holder, "deepBotColors", botIndex),
                ReadArrayOptionSelection(holder, "deepBotOutfits", botIndex),
                ReadArrayOptionSelection(holder, "deepBotNamePlates", botIndex));
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"DeepBot lobby appearance read failed for bot {botIndex + 1}: {ex.GetBaseException().Message}");
            return DeepBotAppearanceSettings.Default;
        }
    }

    private static int ReadOptionSelection(Type holder, string fieldName)
    {
        var option = holder.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var getter = option?.GetType().GetMethod("getSelection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return getter?.Invoke(option, null) is int selection ? selection : 0;
    }

    private static int ReadArrayOptionSelection(Type holder, string fieldName, int index)
    {
        if (holder.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is not Array options ||
            index < 0 ||
            index >= options.Length)
        {
            return 0;
        }

        var option = options.GetValue(index);
        var getter = option?.GetType().GetMethod("getSelection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return getter?.Invoke(option, null) is int selection ? selection : 0;
    }

    /// <summary>
    /// Routes an ordinary bot kill through TOR's own murder validator.  This
    /// preserves modifier/role rules such as the ungrown Mini, Medic shield,
    /// Time Master rewind, Pursuer blank, transportation immunity, and first
    /// kill protection.  Returning true means TOR handled the attempt, even
    /// when it intentionally suppressed the kill.
    /// </summary>
    internal static bool TryExecuteRuleAwareMurder(
        PlayerControl killer,
        PlayerControl target,
        out bool killed,
        out string outcome,
        bool showAnimation = true)
    {
        killed = false;
        outcome = "TOR unavailable";
        if (!EnsureLoaded() || _helpersType is null)
        {
            return false;
        }

        try
        {
            var method = _checkAndKillMethod ??= FindTorHelperMethod("checkMurderAttemptAndKill", 6);
            if (method is null)
            {
                outcome = "TOR murder validator not found";
                return false;
            }

            var result = method.Invoke(
                null,
                new object[] { killer, target, false, showAnimation, false, false });
            var resultName = result?.ToString() ?? "unknown";
            killed = string.Equals(resultName, "PerformKill", StringComparison.Ordinal) ||
                     target.Data?.IsDead == true;
            outcome = $"TOR rule result={resultName}, killed={killed}";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            outcome = $"TOR murder validator threw: {ex.InnerException?.Message ?? ex.Message}";
            _log?.LogWarning($"DeepBot TOR murder validation failed: {ex.InnerException ?? ex}");
            return false;
        }
        catch (Exception ex)
        {
            outcome = $"TOR murder validator failed: {ex.Message}";
            _log?.LogWarning($"DeepBot TOR murder validation failed: {ex}");
            return false;
        }
    }

    private static bool TryCheckRuleAwareMurder(
        PlayerControl killer,
        PlayerControl target,
        out string resultName,
        bool blockRewind = false,
        bool ignoreBlank = false,
        bool ignoreIfKillerIsDead = false,
        bool ignoreMedic = false)
    {
        resultName = "TOR unavailable";
        if (!EnsureLoaded() || _helpersType is null)
        {
            return false;
        }

        try
        {
            var method = _checkMurderMethod ??= FindTorHelperMethod("checkMuderAttempt", 6);
            if (method is null)
            {
                resultName = "TOR murder precheck not found";
                return false;
            }

            var result = method.Invoke(
                null,
                new object[] { killer, target, blockRewind, ignoreBlank, ignoreIfKillerIsDead, ignoreMedic });
            resultName = result?.ToString() ?? "unknown";
            return true;
        }
        catch (Exception ex)
        {
            resultName = $"TOR murder precheck failed: {ex.GetBaseException().Message}";
            _log?.LogWarning($"DeepBot TOR murder precheck failed: {ex.GetBaseException()}");
            return false;
        }
    }

    private static MethodInfo? FindTorHelperMethod(string name, int parameterCount)
    {
        return _helpersType?
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                candidate.Name == name &&
                candidate.GetParameters().Length == parameterCount);
    }

    internal static void Initialize(ManualLogSource log)
    {
        _log = log;
        var available = EnsureLoaded();
        if (_availabilityLogged)
        {
            return;
        }

        _availabilityLogged = true;
        log.LogInfo(available
            ? $"DeepBot TOR adapter ready: customRoles={RoleSpecs.Length}, modifiers={ModifierSpecs.Select(modifier => modifier.Name).Distinct().Count()}, activeAbilities={RoleSpecs.Count(role => role.ActiveAbility) + 1}."
            : "DeepBot TOR adapter inactive: TheOtherRoles is not loaded; native roles remain available.");
        if (available)
        {
            // BepInEx loads DeepBot before TOR's plugin entry point, so the
            // first Harmony PatchAll cannot resolve TOR's nested intro writer.
            // At this point EnsureLoaded has obtained the live TOR assembly;
            // apply the role-text postfix now rather than silently skipping it.
            Plugin.ApplyLateTorRolePatches();
        }
    }

    internal static bool TryGetRole(PlayerControl? player, out TorRoleInfo role)
    {
        role = default;
        if (!player || !EnsureLoaded())
        {
            return false;
        }

        var matches = GetOwnedPrimaryRoleSpecs(player!);
        if (matches.Count == 0)
        {
            return false;
        }

        var spec = matches[0];
        if (matches.Count > 1 && TryGetAuthoritativePrimaryRoleKey(player!, out var authoritativeKey))
        {
            spec = matches.FirstOrDefault(candidate =>
                       string.Equals(candidate.Name, authoritativeKey, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(candidate.TypeName, authoritativeKey, StringComparison.OrdinalIgnoreCase)) ?? spec;
        }

        role = new TorRoleInfo(
            spec.Name,
            spec.Alignment,
            spec.ActiveAbility,
            BuildWinCondition(spec.Name, spec.Alignment),
            spec.AbilityPurpose);
        return true;
    }

    internal static bool TryGetIntroRole(PlayerControl? player, out TorIntroRoleInfo introRole)
    {
        introRole = default;
        // The intro is presentation owned by TOR itself. Do not gate it on
        // DeepBot's static role catalogue: a newly added TOR role (or a role
        // whose assignment arrives a few frames late) must still be shown by
        // its authoritative RoleInfo instead of falling back to the base
        // Crewmate/Impostor faction label.
        if (!player || !EnsureLoaded() || _assembly is null)
        {
            return false;
        }

        try
        {
            var roleInfoType = _assembly.GetType("TheOtherRoles.RoleInfo", false);
            var getRoles = roleInfoType?
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "getRoleInfoForPlayer" &&
                    method.GetParameters().Length == 2);
            var roles = getRoles?.Invoke(null, [player, true]);
            if (roles is null)
            {
                return false;
            }

            var rolesType = roles.GetType();
            var count = Convert.ToInt32(rolesType.GetProperty("Count")?.GetValue(roles) ?? 0);
            var getItem = rolesType.GetMethod(
                "get_Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(int)],
                null);
            for (var index = 0; index < count; index++)
            {
                var item = getItem?.Invoke(roles, [index]);
                if (item is null)
                {
                    continue;
                }

                var itemType = item.GetType();
                var isModifier = itemType.GetField("isModifier")?.GetValue(item) is bool modifier && modifier;
                if (isModifier)
                {
                    continue;
                }

                var name = itemType.GetField("name")?.GetValue(item) as string;
                var description = itemType.GetField("introDescription")?.GetValue(item) as string;
                var isNeutral = itemType.GetField("isNeutral")?.GetValue(item) is bool neutral && neutral;
                var roleId = itemType.GetField("roleId")?.GetValue(item)?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) ||
                    itemType.GetField("color")?.GetValue(item) is not Color color)
                {
                    continue;
                }

                var alignment = isNeutral
                    ? "neutral"
                    : IsImpostorTeam(player) ? "impostor" : "crewmate";

                introRole = new TorIntroRoleInfo(
                    name.Trim(),
                    string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim(),
                    color,
                    alignment,
                    roleId);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"DeepBot TOR intro-role lookup failed: {ex.GetBaseException().Message}");
        }

        return false;
    }

    internal static bool TryGetAbilityRole(PlayerControl? player, out TorRoleInfo role)
    {
        var roles = GetAbilityRoles(player);
        role = roles.Count > 0 ? roles[0] : default;
        return roles.Count > 0;
    }

    internal static IReadOnlyList<TorRoleInfo> GetAbilityRoles(PlayerControl? player)
    {
        if (!player || !EnsureLoaded())
        {
            return Array.Empty<TorRoleInfo>();
        }

        var roles = new List<TorRoleInfo>(2);
        if (TryGetRole(player, out var primary) &&
            ShouldExposeStrategicRole(primary.ActiveAbility, CanUseVents(player!, primary)))
        {
            roles.Add(primary);
        }

        if (HasModifier(player!, "Shifter") && GetStaticField("Shifter", "futureShift") is null)
        {
            roles.Add(new TorRoleInfo(
                "Shifter",
                "modifier",
                true,
                BuildWinCondition("Shifter", "modifier"),
                "Schedule a shift only when the target role is worth the risk and fits the current primary objective."));
        }

        return roles;
    }

    private static bool ShouldExposeStrategicRole(bool activeAbility, bool canUseVents)
    {
        return activeAbility || canUseVents;
    }

    internal static bool TryGetAbilityRole(PlayerControl? player, string roleName, out TorRoleInfo role)
    {
        role = GetAbilityRoles(player)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, roleName, StringComparison.Ordinal));
        return !string.IsNullOrWhiteSpace(role.Name);
    }

    internal static IReadOnlyList<TorModifierInfo> GetModifiers(PlayerControl? player)
    {
        if (!player || !EnsureLoaded())
        {
            return Array.Empty<TorModifierInfo>();
        }

        return ModifierSpecs
            .Where(spec => OwnsModifier(player!, spec))
            .GroupBy(spec => spec.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(spec => new TorModifierInfo(spec.Name, spec.Description))
            .ToArray();
    }

    internal static bool HasModifier(PlayerControl? player, string modifierName)
    {
        return player && GetModifiers(player).Any(modifier => string.Equals(modifier.Name, modifierName, StringComparison.Ordinal));
    }

    internal static void AuditDeepBotAssignments()
    {
        if (!EnsureLoaded() || PlayerControl.AllPlayerControls is null)
        {
            return;
        }

        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player || player.Data is null || player.Data.Disconnected)
            {
                continue;
            }

            var ownedPrimaryRoles = GetOwnedPrimaryRoleSpecs(player);
            if (ownedPrimaryRoles.Count > 1)
            {
                var conflict = string.Join(",", ownedPrimaryRoles.Select(candidate => candidate.Name).OrderBy(name => name, StringComparer.Ordinal));
                if (!LoggedAssignmentConflicts.TryGetValue(player.PlayerId, out var priorConflict) ||
                    !string.Equals(priorConflict, conflict, StringComparison.Ordinal))
                {
                    LoggedAssignmentConflicts[player.PlayerId] = conflict;
                    TryGetAuthoritativePrimaryRoleKey(player, out var authoritativeKey);
                    _log?.LogError(
                        $"DeepBot illegal TOR primary-role coexistence detected: player={Describe(player)}, " +
                        $"primaryRoles=[{conflict}], authoritativeRoleInfo={authoritativeKey ?? "unknown"}. " +
                        "Only modifiers may coexist; DeepBot will reason and act through one authoritative primary role.");
                }
            }
            else
            {
                LoggedAssignmentConflicts.Remove(player.PlayerId);
            }

            if (!DeepBotIdentity.IsBot(player))
            {
                continue;
            }

            var hasCustomRole = TryGetRole(player, out var role);
            var primaryName = hasCustomRole ? role.Name : player.Data.RoleType.ToString();
            var alignment = hasCustomRole
                ? role.Alignment
                : IsImpostorTeam(player) ? "impostor" : "crewmate";
            var activeAbility = hasCustomRole && role.ActiveAbility;
            var modifiers = GetModifiers(player);
            var modifierNames = modifiers.Count == 0 ? "none" : string.Join(",", modifiers.Select(modifier => modifier.Name));
            var signature = $"{primaryName}:{alignment}:{modifierNames}";
            if (LoggedAssignments.TryGetValue(player.PlayerId, out var prior) &&
                string.Equals(prior, signature, StringComparison.Ordinal))
            {
                continue;
            }

            LoggedAssignments[player.PlayerId] = signature;
            _log?.LogInfo(
                $"DeepBot TOR assignment observed: bot={Describe(player)}, primaryRole={primaryName}, alignment={alignment}, activeAbility={activeAbility}, modifiers={modifierNames}.");
        }
    }

    private static IReadOnlyList<RoleSpec> GetOwnedPrimaryRoleSpecs(PlayerControl player)
    {
        if (!player)
        {
            return Array.Empty<RoleSpec>();
        }

        return RoleSpecs
            .Where(spec =>
                !(spec.Name == "Prosecutor" && !GetStaticBool("Lawyer", "isProsecutor")) &&
                !(spec.Name == "Lawyer" && GetStaticBool("Lawyer", "isProsecutor")) &&
                GetStaticField(spec.TypeName, spec.OwnerField) is PlayerControl owner &&
                owner && owner.PlayerId == player.PlayerId)
            .ToArray();
    }

    private static bool TryGetAuthoritativePrimaryRoleKey(PlayerControl player, out string? roleKey)
    {
        roleKey = null;
        if (!player || !EnsureLoaded() || _assembly is null)
        {
            return false;
        }

        try
        {
            var roleInfoType = _assembly.GetType("TheOtherRoles.RoleInfo", false);
            var getRoles = roleInfoType?
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "getRoleInfoForPlayer" && method.GetParameters().Length == 2);
            var roles = getRoles?.Invoke(null, [player, true]);
            var count = Convert.ToInt32(roles?.GetType().GetProperty("Count")?.GetValue(roles) ?? 0);
            var getItem = roles?.GetType().GetMethod(
                "get_Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(int)],
                null);
            for (var index = 0; index < count; index++)
            {
                var item = getItem?.Invoke(roles, [index]);
                if (item is null)
                {
                    continue;
                }

                var itemType = item.GetType();
                if (itemType.GetField("isModifier")?.GetValue(item) is bool modifier && modifier)
                {
                    continue;
                }

                roleKey = itemType.GetField("roleId")?.GetValue(item)?.ToString() ??
                          itemType.GetField("name")?.GetValue(item) as string;
                return !string.IsNullOrWhiteSpace(roleKey);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"DeepBot authoritative TOR primary-role audit failed safely: {ex.GetBaseException().Message}");
        }

        return false;
    }

    internal static bool IsAbilityReady(PlayerControl bot, TorRoleInfo role)
    {
        if (!role.ActiveAbility ||
            !ImplementedActiveAbilityNames.Contains(role.Name) ||
            !bot ||
            bot.Data is null ||
            bot.Data.IsDead ||
            IsHandcuffed(bot))
        {
            return false;
        }

        if (PendingDouses.ContainsKey(bot.PlayerId) ||
            PendingVampireBites.ContainsKey(bot.PlayerId) ||
            PendingWarlockCurses.ContainsKey(bot.PlayerId) ||
            PendingWitchSpells.ContainsKey(bot.PlayerId) ||
            PendingNinjaReveals.ContainsKey(bot.PlayerId) ||
            PendingYoyoReturns.ContainsKey(bot.PlayerId) ||
            Time.time < NextRoleAbilityAt.GetValueOrDefault((bot.PlayerId, role.Name)))
        {
            return false;
        }

        return role.Name switch
        {
            "Engineer" => GetStaticInt("Engineer", "remainingFixes") > 0 && HasRepairableEmergency(bot),
            "Mayor" => GetStaticBool("Mayor", "meetingButton") &&
                       GetStaticInt("Mayor", "remoteMeetingsLeft") > 0 &&
                       !HasRepairableEmergency(bot),
            "Portalmaker" => GetStaticField("Portal", "secondPortal") is null,
            "Medic" => !GetStaticBool("Medic", "usedShield"),
            "Deputy" => GetStaticFloat("Deputy", "remainingHandcuffs") > 0.05f,
            "Tracker" => !GetStaticBool("Tracker", "usedTracker"),
            "TimeMaster" => !GetStaticBool("TimeMaster", "shieldActive"),
            "Camouflager" => GetStaticFloat("Camouflager", "camouflageTimer") <= 0.05f,
            "Vampire" => GetStaticField("Vampire", "bitten") is null,
            "Jackal" => true,
            "Sidekick" => GetStaticBool("Sidekick", "canKill"),
            "Arsonist" => true,
            "Pursuer" => GetStaticInt("Pursuer", "blanks") < GetStaticInt("Pursuer", "blanksNumber"),
            "Thief" => true,
            "Shifter" => GetStaticField("Shifter", "futureShift") is null,
            "Trapper" => GetStaticInt("Trapper", "charges") > 0,
            "Trickster" => !GetStaticBool("JackInTheBox", "boxesConvertedToVents") ||
                           GetStaticFloat("Trickster", "lightsOutTimer") <= 0.05f,
            "SecurityGuard" => GetStaticInt("SecurityGuard", "remainingScrews") >=
                               Mathf.Min(
                                   Mathf.Max(1, GetStaticInt("SecurityGuard", "ventPrice")),
                                   Mathf.Max(1, GetStaticInt("SecurityGuard", "camPrice"))),
            "Bomber" => !GetStaticBool("Bomber", "isPlanted"),
            "Yoyo" => true,
            "Warlock" => true,
            "Ninja" => true,
            "Hacker" => GetStaticInt("Hacker", "chargesAdminTable") > 0 ||
                        GetStaticInt("Hacker", "chargesVitals") > 0,
            "Medium" => GetStaticCollectionCount("Medium", "deadBodies") > 0,
            _ => true
        };
    }

    internal static bool TryGetAbilitySequencePlan(
        PlayerControl bot,
        TorRoleInfo role,
        out TorAbilitySequencePlan plan)
    {
        plan = default;
        if (!bot || bot.Data is null || bot.Data.IsDead)
        {
            return false;
        }

        if (role.Name == "Morphling" &&
            GetStaticField("Morphling", "sampledTarget") is PlayerControl invalidSample &&
            !IsSequenceTargetAvailable(invalidSample, allowVentConcealment: true))
        {
            SetStaticField("Morphling", "sampledTarget", null!);
            NextRoleAbilityAt[(bot.PlayerId, role.Name)] = Time.time + 0.35f;
            _log?.LogInfo(
                $"DeepBot Morphling sequence aborted safely: bot={Describe(bot)}, " +
                $"sample={Describe(invalidSample)}, reason=sample target died or disconnected.");
            return false;
        }

        if (role.Name == "Ninja" &&
            GetStaticField("Ninja", "ninjaMarked") is PlayerControl invalidMark &&
            !IsSequenceTargetAvailable(invalidMark, allowVentConcealment: true))
        {
            SetStaticField("Ninja", "ninjaMarked", null!);
            NextRoleAbilityAt[(bot.PlayerId, role.Name)] = Time.time + 0.35f;
            _log?.LogInfo(
                $"DeepBot Ninja sequence aborted safely: bot={Describe(bot)}, " +
                $"mark={Describe(invalidMark)}, reason=marked target died or disconnected.");
            return false;
        }

        switch (role.Name)
        {
            case "Morphling" when GetStaticFloat("Morphling", "morphTimer") > 0.05f:
                plan = new TorAbilitySequencePlan(
                    true,
                    true,
                    null,
                    "cover",
                    "morph is active; follow through with a plausible cover route instead of standing still after transforming",
                    0.89f,
                    2.0f);
                return true;
            case "Morphling" when GetStaticField("Morphling", "sampledTarget") is PlayerControl sampled && sampled:
            {
                var stageReady = IsConfiguredRoleStageReady(bot, role.Name);
                plan = new TorAbilitySequencePlan(
                    true,
                    true,
                    sampled.PlayerId,
                    stageReady ? "role" : "cover",
                    stageReady
                        ? $"continue sample-to-morph sequence using sampled target {Describe(sampled)} after reaching concealment"
                        : $"sampled {Describe(sampled)}; move into concealment while the native sample stage finishes",
                    0.94f,
                    stageReady ? 1.0f : RemainingConfiguredRoleStageSeconds(bot, role.Name));
                return true;
            }
            case "Portalmaker" when GetStaticField("Portal", "firstPortal") is not null &&
                                          GetStaticField("Portal", "secondPortal") is null:
            {
                var stageReady = IsConfiguredRoleStageReady(bot, role.Name);
                plan = new TorAbilitySequencePlan(
                    true,
                    true,
                    null,
                    stageReady ? "role" : "cover",
                    stageReady
                        ? "continue the two-portal plan by placing the separated second endpoint"
                        : "move toward a separated second endpoint while the room-configured portal cooldown runs",
                    0.91f,
                    stageReady ? 1.0f : RemainingConfiguredRoleStageSeconds(bot, role.Name));
                return true;
            }
            case "Trickster":
            {
                var boxes = GetStaticCollectionCount("JackInTheBox", "AllJackInTheBoxes");
                var limit = Mathf.Max(1, GetStaticInt("JackInTheBox", "JackInTheBoxLimit"));
                var converted = GetStaticBool("JackInTheBox", "boxesConvertedToVents");
                var darkness = GetStaticFloat("Trickster", "lightsOutTimer") > 0.05f;
                if (boxes > 0 && boxes < limit)
                {
                    var stageReady = IsConfiguredRoleStageReady(bot, role.Name);
                    plan = new TorAbilitySequencePlan(
                        true,
                        true,
                        null,
                        stageReady ? "role" : "cover",
                        stageReady
                            ? $"place the next separated box; progress={boxes}/{limit}"
                            : $"move toward the next separated box position while the room-configured placement cooldown runs; progress={boxes}/{limit}",
                        0.91f,
                        stageReady ? 1.0f : RemainingConfiguredRoleStageSeconds(bot, role.Name));
                    return true;
                }

                if (boxes >= limit && !converted)
                {
                    plan = new TorAbilitySequencePlan(
                        true,
                        false,
                        null,
                        "hold",
                        "box network is complete; wait for TOR's meeting conversion before using darkness or box vents",
                        0.92f,
                        2.5f);
                    return true;
                }

                if (converted && darkness && CanUseVents(bot, role))
                {
                    plan = new TorAbilitySequencePlan(
                        true,
                        true,
                        null,
                        "vent",
                        "darkness is active; use the converted box network for a concealed ambush or escape",
                        0.86f,
                        1.0f);
                    return true;
                }

                var visibleOpponents = CountPersonallyVisibleOpponents(bot);
                if (converted && !darkness && bot.killTimer <= 0.05f && visibleOpponents is >= 1 and <= 3)
                {
                    plan = new TorAbilitySequencePlan(
                        true,
                        true,
                        null,
                        "role",
                        $"box network is ready and {visibleOpponents} opponents are visible; start darkness for a concrete hostile play",
                        0.88f,
                        1.0f);
                    return true;
                }

                return false;
            }
            case "Ninja" when GetStaticField("Ninja", "ninjaMarked") is PlayerControl marked && marked:
            {
                var stageReady = IsConfiguredRoleStageReady(bot, role.Name);
                plan = new TorAbilitySequencePlan(
                    true,
                    stageReady,
                    marked.PlayerId,
                    stageReady ? "role" : "hold",
                    stageReady
                        ? $"continue mark-to-invisible-strike sequence against {Describe(marked)}"
                        : $"keep the mark on {Describe(marked)} and wait for the native second-stage timer",
                    0.91f,
                    stageReady ? 1.0f : RemainingConfiguredRoleStageSeconds(bot, role.Name));
                return true;
            }
            case "Warlock" when PendingWarlockCurses.TryGetValue(bot.PlayerId, out var pendingCurse):
            {
                var secondTarget = FindWarlockSecondTarget(bot, pendingCurse.VictimId);
                plan = new TorAbilitySequencePlan(
                    true,
                    secondTarget,
                    secondTarget ? secondTarget!.PlayerId : null,
                    secondTarget ? "role" : "hold",
                    secondTarget
                        ? $"curse carrier has approached legal second target {Describe(secondTarget)}; consciously complete the forced-kill stage"
                        : "curse carrier is active; wait for that carrier to approach a legal second target",
                    secondTarget ? 0.96f : 0.93f,
                    secondTarget ? 0.35f : 0.8f);
                return true;
            }
            case "Arsonist" when PendingDouses.ContainsKey(bot.PlayerId):
                plan = new TorAbilitySequencePlan(
                    true,
                    false,
                    null,
                    "hold",
                    "douse channel is active; remain close until the configured channel completes",
                    0.97f,
                    0.5f);
                return true;
            case "Witch" when PendingWitchSpells.TryGetValue(bot.PlayerId, out var pendingSpell):
                plan = new TorAbilitySequencePlan(
                    true,
                    false,
                    pendingSpell.TargetId,
                    "hold",
                    "spell channel is active; keep the original target in legal range and line of sight until TOR's configured cast time completes",
                    0.98f,
                    0.35f);
                return true;
            case "Vampire" when PendingVampireBites.ContainsKey(bot.PlayerId):
                plan = new TorAbilitySequencePlan(
                    true,
                    true,
                    null,
                    "evade",
                    "bite is pending; leave the bite scene while the victim later dies at their own position",
                    0.96f,
                    1.0f);
                return true;
            case "Bomber" when GetStaticBool("Bomber", "isPlanted"):
                plan = new TorAbilitySequencePlan(
                    true,
                    true,
                    null,
                    "evade",
                    "bomb is planted; clear the configured blast radius and build an alibi instead of waiting beside it",
                    0.95f,
                    1.0f);
                return true;
            case "Yoyo" when GetStaticField("Yoyo", "markedLocation") is not null &&
                                     !PendingYoyoReturns.ContainsKey(bot.PlayerId):
            {
                var visibleOpponents = CountPersonallyVisibleOpponents(bot);
                var strategicallyReady = visibleOpponents is >= 1 and <= 2 || bot.killTimer <= 0.05f;
                var ready = strategicallyReady && IsConfiguredRoleStageReady(bot, role.Name);
                plan = new TorAbilitySequencePlan(
                    true,
                    ready,
                    null,
                    ready ? "role" : "hold",
                    ready
                        ? "continue mark-to-blink sequence for an ambush, escape, or alibi"
                        : strategicallyReady
                            ? "return point is armed; wait for TOR's native post-mark timer before blinking"
                            : "return point is armed; wait for a concrete nearby ambush or escape opportunity",
                    ready ? 0.84f : 0.78f,
                    ready ? 1.0f : Mathf.Max(0.5f, Mathf.Min(2.0f, RemainingConfiguredRoleStageSeconds(bot, role.Name))));
                return true;
            }
            default:
                return false;
        }
    }

    internal static bool IsAbilitySequencePending(PlayerControl bot, TorRoleInfo role)
    {
        return TryGetAbilitySequencePlan(bot, role, out var plan) && plan.Active;
    }

    internal static bool CurrentAbilityStageRequiresCasterProximity(PlayerControl bot, TorRoleInfo role)
    {
        if (!bot)
        {
            return true;
        }

        var continuationActive = role.Name switch
        {
            "Morphling" => GetStaticField("Morphling", "sampledTarget") is not null,
            "Ninja" => GetStaticField("Ninja", "ninjaMarked") is not null,
            "Warlock" => PendingWarlockCurses.ContainsKey(bot.PlayerId),
            _ => false
        };
        return StageRequiresCasterProximity(role.Name, continuationActive);
    }

    private static bool IsSequenceTargetAvailable(PlayerControl? target, bool allowVentConcealment)
    {
        return IsSequenceTargetStateAvailable(
            target,
            target && target!.Data is not null,
            target && target!.Data?.IsDead == true,
            target && target!.Data?.Disconnected == true,
            target && BotPerceptionPolicy.IsConcealedByVent(target),
            allowVentConcealment);
    }

    private static bool StageRequiresCasterProximity(string roleName, bool continuationActive)
    {
        return !continuationActive || roleName is not ("Morphling" or "Ninja" or "Warlock");
    }

    private static bool IsSequenceTargetStateAvailable(
        bool targetExists,
        bool hasData,
        bool dead,
        bool disconnected,
        bool ventConcealed,
        bool allowVentConcealment)
    {
        return targetExists && hasData && !dead && !disconnected &&
               (allowVentConcealment || !ventConcealed);
    }

    private static bool IsConfiguredRoleStageReady(PlayerControl bot, string roleName)
    {
        return bot && IsRoleStageReadyAt(
            Time.time,
            NextRoleAbilityAt.GetValueOrDefault((bot.PlayerId, roleName)));
    }

    private static bool IsRoleStageReadyAt(float now, float readyAt)
    {
        return now >= readyAt;
    }

    private static float RemainingConfiguredRoleStageSeconds(PlayerControl bot, string roleName)
    {
        return !bot
            ? 0.5f
            : Mathf.Max(0.35f, NextRoleAbilityAt.GetValueOrDefault((bot.PlayerId, roleName)) - Time.time);
    }

    internal static bool TryGetAbilityStagingDestination(
        PlayerControl bot,
        TorRoleInfo role,
        out Vector2 position,
        out string stage,
        out float arrivalDistance)
    {
        position = default;
        stage = string.Empty;
        arrivalDistance = 0.95f;
        if (!bot || !GameRuleSettings.IsDeepBotSupportedMap())
        {
            return false;
        }

        if (role.Name == "SecurityGuard" && GameRuleSettings.IsMiraHqMap())
        {
            var vent = FindNearestUnsealedVent(bot, float.MaxValue);
            if (!vent)
            {
                return false;
            }

            position = vent!.transform.position;
            stage = $"security-guard-seal-vent-{vent.Id}";
            arrivalDistance = 1.15f;
            return true;
        }

        var progress = 0;
        var minimumSeparation = 5f;
        var preferCorner = false;
        var maximumTravel = float.MaxValue;
        List<Vector2> placements;
        switch (role.Name)
        {
            case "Morphling" when GetStaticFloat("Morphling", "morphTimer") > 0.05f:
                placements = [];
                minimumSeparation = 3f;
                maximumTravel = 9f;
                stage = "morphling-active-cover-route";
                break;
            case "Morphling" when GetStaticField("Morphling", "sampledTarget") is PlayerControl &&
                                     CountPersonallyVisibleOpponents(bot) > 0:
                placements = [];
                minimumSeparation = 7f;
                preferCorner = true;
                stage = "morphling-conceal-before-transform";
                break;
            case "Portalmaker" when GetStaticField("Portal", "firstPortal") is not null &&
                                         GetStaticField("Portal", "secondPortal") is null:
                placements = GetStrategicPlacements(bot, "Portalmaker", false);
                progress = placements.Count;
                minimumSeparation = 8f;
                stage = "portalmaker-separated-second-endpoint";
                break;
            case "Trickster" when !GetStaticBool("JackInTheBox", "boxesConvertedToVents") &&
                                      GetStaticCollectionCount("JackInTheBox", "AllJackInTheBoxes") <
                                      Mathf.Max(1, GetStaticInt("JackInTheBox", "JackInTheBoxLimit")):
                progress = GetStaticCollectionCount("JackInTheBox", "AllJackInTheBoxes");
                placements = GetStrategicPlacements(bot, "Trickster", progress == 0);
                minimumSeparation = 5.5f;
                stage = $"trickster-box-{progress + 1}";
                break;
            case "Trapper":
                progress = GetStaticInt("Trapper", "charges");
                placements = GetStrategicPlacements(bot, "Trapper", false);
                minimumSeparation = 4.5f;
                stage = "trapper-informative-chokepoint";
                break;
            case "SecurityGuard":
                progress = GetStaticInt("SecurityGuard", "placedCameras");
                placements = GetStrategicPlacements(bot, "SecurityGuard", false);
                minimumSeparation = 5.5f;
                stage = "security-camera-chokepoint";
                break;
            default:
                return false;
        }

        var current = bot.GetTruePosition();
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node =>
                SkeldPathGraph.Instance.IsNodeAllowed(node.Id) &&
                node.Kind is NodeKind.Corner or NodeKind.Door or NodeKind.Hall or NodeKind.Landmark &&
                Vector2.Distance(current, node.Position) >= 2.5f &&
                Vector2.Distance(current, node.Position) <= maximumTravel &&
                (placements.Count == 0 || placements.All(previous =>
                    Vector2.Distance(previous, node.Position) >= minimumSeparation)))
            .Select(node => new
            {
                Node = node,
                Score = ScoreAbilityStagingNode(bot.PlayerId, progress, node, current, placements, preferCorner)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (!SkeldPathGraph.Instance.TryResolveNavigationDestination(
                    current,
                    candidate.Node.Position,
                    out var reachablePosition))
            {
                continue;
            }

            position = reachablePosition;
            stage += $":{candidate.Node.Id}";
            return true;
        }

        return false;
    }

    internal static bool TryGetAbilityEscapeDestination(
        PlayerControl bot,
        TorRoleInfo role,
        out Vector2 position,
        out string stage,
        out float arrivalDistance)
    {
        position = default;
        stage = string.Empty;
        arrivalDistance = 1.0f;
        if (!bot || !GameRuleSettings.IsDeepBotSupportedMap())
        {
            return false;
        }

        Vector2 dangerPosition;
        float minimumClearance;
        switch (role.Name)
        {
            case "Vampire" when PendingVampireBites.TryGetValue(bot.PlayerId, out var bite):
                dangerPosition = bite.Origin;
                minimumClearance = 5.5f;
                stage = $"vampire-post-bite-cover-{bite.TargetId}";
                break;
            case "Bomber" when TryGetBombPosition(out var bombPosition):
                dangerPosition = bombPosition;
                minimumClearance = Mathf.Max(4.5f, GetStaticFloat("Bomber", "destructionRange") + 2.5f);
                stage = "bomber-clear-blast-radius";
                break;
            default:
                return false;
        }

        var current = bot.GetTruePosition();
        var candidate = SkeldPathGraph.Instance.Nodes
            .Where(node =>
                SkeldPathGraph.Instance.IsNodeAllowed(node.Id) &&
                node.Kind is NodeKind.Corner or NodeKind.Door or NodeKind.Hall or NodeKind.Landmark)
            .Select(node => new
            {
                Node = node,
                Travel = Vector2.Distance(current, node.Position),
                Clearance = Vector2.Distance(dangerPosition, node.Position),
                NearbyPlayers = PlayerControl.AllPlayerControls.ToArray().Count(player =>
                    player &&
                    player.PlayerId != bot.PlayerId &&
                    player.Data is not null &&
                    !player.Data.IsDead &&
                    !player.Data.Disconnected &&
                    Vector2.Distance(player.GetTruePosition(), node.Position) <= 2.5f)
            })
            .Where(item => item.Travel is >= 2.5f and <= 13f && item.Clearance >= minimumClearance)
            .OrderByDescending(item => item.Clearance - item.Travel * 0.42f - item.NearbyPlayers * 2.25f)
            .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        position = candidate.Node.Position;
        stage += $":{candidate.Node.Id}";
        return true;
    }

    private static float ScoreAbilityStagingNode(
        byte botId,
        int progress,
        NavNode node,
        Vector2 current,
        IReadOnlyList<Vector2> placements,
        bool preferCorner)
    {
        var currentDistance = Vector2.Distance(current, node.Position);
        var separation = placements.Count == 0
            ? currentDistance
            : placements.Min(previous => Vector2.Distance(previous, node.Position));
        var kindBonus = node.Kind switch
        {
            NodeKind.Door => 4.2f,
            NodeKind.Hall => 3.4f,
            NodeKind.Corner when preferCorner => 5.5f,
            NodeKind.Corner => 2.8f,
            NodeKind.Landmark => 2.2f,
            _ => 0f
        };
        var stableVariation = (StableTextHash(node.Id) + botId * 13 + progress * 29) % 17 * 0.11f;
        var excessiveTravelPenalty = Mathf.Max(0f, currentDistance - 18f) * 0.35f;
        return separation * 1.35f + kindBonus + stableVariation - excessiveTravelPenalty;
    }

    private static int StableTextHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value)
            {
                hash = hash * 31 + character;
            }
            return Math.Abs(hash == int.MinValue ? int.MaxValue : hash);
        }
    }

    private static int CountPersonallyVisibleOpponents(PlayerControl bot)
    {
        var position = bot.GetTruePosition();
        var vision = BotPerceptionPolicy.GetCurrentVisionDistance(bot);
        return PlayerControl.AllPlayerControls
            .ToArray()
            .Count(player =>
                player &&
                player.PlayerId != bot.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                !IsImpostorTeam(player) &&
                !BotPerceptionPolicy.IsConcealedByVent(player) &&
                Vector2.Distance(position, player.GetTruePosition()) <= vision &&
                !PhysicsHelpers.AnythingBetween(position, player.GetTruePosition(), Constants.ShipOnlyMask, false));
    }

    internal static bool HasExclusiveKillAbilityPending(PlayerControl? bot)
    {
        return bot && (PendingVampireBites.ContainsKey(bot!.PlayerId) || PendingWitchSpells.ContainsKey(bot.PlayerId));
    }

    internal static bool CanUseOrdinaryMurder(PlayerControl? bot, out string reason)
    {
        reason = string.Empty;
        if (!bot || !EnsureLoaded())
        {
            return true;
        }

        if (IsHandcuffed(bot))
        {
            reason = "player is handcuffed by the Deputy and all action buttons are disabled";
            return false;
        }

        if (IsRoleOwner(bot!, "Vampire"))
        {
            reason = "vampire must use the TOR bite button and delayed resolution";
            return false;
        }

        if (IsRoleOwner(bot!, "Janitor"))
        {
            reason = "janitor has no ordinary kill button under TOR rules";
            return false;
        }

        if (IsRoleOwner(bot!, "Mafioso"))
        {
            var godfather = GetStaticField("Godfather", "godfather") as PlayerControl;
            var godfatherExists = godfather;
            var godfatherAlive = godfather && godfather!.Data is not null && !godfather.Data.IsDead;
            var godfatherDisconnected = godfather && godfather!.Data is not null && godfather.Data.Disconnected;
            if (!IsMafiosoKillUnlocked(godfatherExists, godfatherAlive, godfatherDisconnected))
            {
                reason = "mafioso kill button remains locked while the godfather is alive";
                return false;
            }
        }

        if (IsRuleImmobilized(bot))
        {
            reason = "player is immobilized by a TOR trap or role rule";
            return false;
        }

        return true;
    }

    internal static bool IsMafiosoKillUnlocked(
        bool godfatherExists,
        bool godfatherAlive,
        bool godfatherDisconnected)
    {
        return !godfatherExists || !godfatherAlive || godfatherDisconnected;
    }

    internal static bool IsPreferredOrdinaryMurderTarget(PlayerControl killer, PlayerControl target)
    {
        if (!killer || !target || !TryGetRole(killer, out var role) || role.Name != "BountyHunter")
        {
            return false;
        }

        var bounty = GetStaticField("BountyHunter", "bounty") as PlayerControl;
        return bounty &&
               bounty!.PlayerId == target.PlayerId &&
               target.Data is not null &&
               !target.Data.IsDead &&
               !target.Data.Disconnected &&
               !AreLoverPartners(killer, target);
    }

    internal static float GetConfiguredOrdinaryMurderCooldown(
        PlayerControl killer,
        PlayerControl target,
        float roomCooldown)
    {
        if (!killer || !target || !TryGetRole(killer, out var role) || role.Name != "BountyHunter")
        {
            return roomCooldown;
        }

        return ResolveBountyHunterCooldown(
            IsPreferredOrdinaryMurderTarget(killer, target),
            roomCooldown,
            GetStaticFloat("BountyHunter", "bountyKillCooldown"),
            GetStaticFloat("BountyHunter", "punishmentTime"));
    }

    internal static float ResolveBountyHunterCooldown(
        bool killedBounty,
        float roomCooldown,
        float bountyCooldown,
        float punishmentSeconds)
    {
        return killedBounty
            ? Mathf.Max(0.05f, bountyCooldown)
            : Mathf.Max(0.05f, roomCooldown + Mathf.Max(0f, punishmentSeconds));
    }

    private static float ResolveWitchAbilityCooldown(float baseCooldown, float currentAddition, float configuredAddition)
    {
        return Mathf.Max(0.05f, baseCooldown) +
               Mathf.Max(0f, currentAddition + Mathf.Max(0f, configuredAddition));
    }

    private static float ResolveWitchKillCooldown(float roomCooldown, bool isMini, bool miniIsGrown)
    {
        var multiplier = !isMini ? 1f : miniIsGrown ? 0.66f : 2f;
        return Mathf.Max(0f, roomCooldown) * multiplier;
    }

    internal static void LogRoleCoverageSelfTest(ManualLogSource log)
    {
        var activePrimaryNames = RoleSpecs
            .Where(role => role.ActiveAbility)
            .Select(role => role.Name)
            .ToHashSet(StringComparer.Ordinal);
        var missingActiveBranches = activePrimaryNames
            .Where(roleName => !ImplementedActiveAbilityNames.Contains(roleName))
            .OrderBy(roleName => roleName, StringComparer.Ordinal)
            .ToArray();
        var unexpectedBranches = ImplementedActiveAbilityNames
            .Where(roleName => roleName != "Shifter" && !activePrimaryNames.Contains(roleName))
            .OrderBy(roleName => roleName, StringComparer.Ordinal)
            .ToArray();
        var godfatherRecognizedAsOrdinaryKiller = RoleSpecs.Any(role =>
            role.Name == "Godfather" && role.Alignment == "impostor" && !role.ActiveAbility);
        var mafiosoRecognizedAsSuccessionKiller = RoleSpecs.Any(role =>
            role.Name == "Mafioso" && role.Alignment == "impostor" && !role.ActiveAbility);
        var mafiaSuccessionRulesValid =
            !IsMafiosoKillUnlocked(true, true, false) &&
            IsMafiosoKillUnlocked(true, false, false) &&
            IsMafiosoKillUnlocked(true, true, true) &&
            IsMafiosoKillUnlocked(false, false, false);
        var requiredMultiStageRoles = new HashSet<string>(StringComparer.Ordinal)
        {
            "Morphling", "Portalmaker", "Trickster", "Ninja", "Warlock",
            "Arsonist", "Vampire", "Witch", "Bomber", "Yoyo"
        };
        var multiStageCoverageValid = ImplementedMultiStageAbilityNames.SetEquals(requiredMultiStageRoles) &&
                                      ImplementedMultiStageAbilityNames.All(roleName =>
                                          activePrimaryNames.Contains(roleName) && ImplementedActiveAbilityNames.Contains(roleName));
        var bountyCooldownRulesValid =
            Mathf.Approximately(ResolveBountyHunterCooldown(true, 30f, 5f, 15f), 5f) &&
            Mathf.Approximately(ResolveBountyHunterCooldown(false, 30f, 5f, 15f), 45f);
        var witchCastingRulesValid =
            Mathf.Approximately(ResolveWitchAbilityCooldown(30f, 0f, 10f), 40f) &&
            Mathf.Approximately(ResolveWitchAbilityCooldown(30f, 10f, 10f), 50f) &&
            Mathf.Approximately(ResolveWitchKillCooldown(30f, false, false), 30f) &&
            Mathf.Approximately(ResolveWitchKillCooldown(30f, true, true), 19.8f) &&
            Mathf.Approximately(ResolveWitchKillCooldown(30f, true, false), 60f);
        var stableStageSelection = StableTextHash("MIRA_Y_CENTER") == StableTextHash("MIRA_Y_CENTER") &&
                                   StableTextHash("MIRA_Y_CENTER") != StableTextHash("MIRA_LAUNCHPAD_E");
        var multiStageCooldownGateValid =
            !IsRoleStageReadyAt(10f, 10.01f) &&
            IsRoleStageReadyAt(10.01f, 10.01f) &&
            IsRoleStageReadyAt(11f, 10.01f);
        var remoteContinuationRulesValid =
            StageRequiresCasterProximity("Morphling", false) &&
            !StageRequiresCasterProximity("Morphling", true) &&
            !StageRequiresCasterProximity("Ninja", true) &&
            !StageRequiresCasterProximity("Warlock", true) &&
            StageRequiresCasterProximity("Vampire", true);
        var sequenceTargetAbortRulesValid =
            IsSequenceTargetStateAvailable(true, true, false, false, true, true) &&
            !IsSequenceTargetStateAvailable(true, true, true, false, false, true) &&
            !IsSequenceTargetStateAvailable(true, true, false, true, false, true) &&
            !IsSequenceTargetStateAvailable(false, false, false, false, false, true);
        var ventOnlyRoleRoutingValid =
            ShouldExposeStrategicRole(false, true) &&
            !ShouldExposeStrategicRole(false, false);
        var level = RoleSpecs.Length == 44 &&
                    missingActiveBranches.Length == 0 &&
                    unexpectedBranches.Length == 0 &&
                    godfatherRecognizedAsOrdinaryKiller &&
                     mafiosoRecognizedAsSuccessionKiller &&
                     mafiaSuccessionRulesValid &&
                     multiStageCoverageValid &&
                     bountyCooldownRulesValid &&
                     witchCastingRulesValid &&
                     stableStageSelection &&
                     multiStageCooldownGateValid &&
                     remoteContinuationRulesValid &&
                     sequenceTargetAbortRulesValid &&
                     ventOnlyRoleRoutingValid
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot TOR role coverage self-test: level={level}, recognizedPrimary={RoleSpecs.Length}, " +
            $"activePrimary={activePrimaryNames.Count}, implementedAbilityBranches={ImplementedActiveAbilityNames.Count}, " +
            $"missingActive=[{string.Join(",", missingActiveBranches)}], unexpected=[{string.Join(",", unexpectedBranches)}], " +
            $"godfatherOrdinaryKill={godfatherRecognizedAsOrdinaryKiller}, " +
            $"mafiosoSuccession={mafiosoRecognizedAsSuccessionKiller && mafiaSuccessionRulesValid}, " +
            $"multiStageCoverage={multiStageCoverageValid}, multiStageRoles=[{string.Join(",", ImplementedMultiStageAbilityNames.OrderBy(name => name, StringComparer.Ordinal))}], " +
            $"multiStageCooldownGate={multiStageCooldownGateValid}, " +
            $"remoteContinuationRules={remoteContinuationRulesValid}, sequenceTargetAbortRules={sequenceTargetAbortRulesValid}, " +
            $"ventOnlyRoleRouting={ventOnlyRoleRoutingValid}, " +
            $"bountyCooldownRules={bountyCooldownRulesValid}, " +
            $"witchCastingRules={witchCastingRulesValid}, " +
            $"stableStageSelection={stableStageSelection}.");
    }

    private static bool IsRoleOwner(PlayerControl bot, string roleName)
    {
        var spec = RoleSpecs.FirstOrDefault(candidate => string.Equals(candidate.Name, roleName, StringComparison.Ordinal));
        if (spec is null)
        {
            return false;
        }

        var owner = GetStaticField(spec.TypeName, spec.OwnerField) as PlayerControl;
        return owner && owner!.PlayerId == bot.PlayerId;
    }

    internal static float GetAbilityUseRange(string roleName)
    {
        return roleName switch
        {
            "Arsonist" => 2f,
            "Sheriff" or "Deputy" or "Vampire" or "Warlock" or "Ninja" or
            "Jackal" or "Sidekick" or "Pursuer" or "Thief" or "Eraser" or
            "Witch" or "Shifter" => GameRuleSettings.GetKillDistance(1.8f),
            _ => 3.5f
        };
    }

    internal static bool IsRuleImmobilized(PlayerControl? player)
    {
        if (!player || !EnsureLoaded())
        {
            return false;
        }

        if (PendingDouses.ContainsKey(player!.PlayerId) ||
            PendingWitchSpells.ContainsKey(player.PlayerId) ||
            PendingPortalTeleports.ContainsKey(player.PlayerId) ||
            PendingRoleRoots.TryGetValue(player.PlayerId, out var channelRootUntil) && Time.time < channelRootUntil)
        {
            return true;
        }

        var map = GetStaticField("Trap", "trapPlayerIdMap") as IDictionary;
        return map?.Contains(player!.PlayerId) == true ||
               PendingRoleRoots.TryGetValue(player!.PlayerId, out var rootedUntil) && Time.time < rootedUntil;
    }

    internal static bool IsPortalTeleportPending(PlayerControl? player)
    {
        return player && PendingPortalTeleports.ContainsKey(player!.PlayerId);
    }

    internal static IReadOnlyList<TorPortalTraversalOption> GetPortalTraversalOptions(PlayerControl bot)
    {
        if (!bot || bot.Data is null || bot.Data.IsDead || bot.Data.Disconnected ||
            bot.inVent || bot.walkingToVent || IsHandcuffed(bot) || MeetingHud.Instance || ExileController.Instance ||
            Time.time < NextPortalUseAt.GetValueOrDefault(bot.PlayerId) ||
            !TryGetPortalEndpoints(out var first, out var second, out var teleportDuration) ||
            GetStaticBool("Portal", "isTeleporting"))
        {
            return Array.Empty<TorPortalTraversalOption>();
        }

        var options = new List<TorPortalTraversalOption>(2)
        {
            new(first, second, false, 0, teleportDuration),
            new(second, first, false, 0, teleportDuration)
        };

        if (TryGetRole(bot, out var role) && role.Name == "Portalmaker" &&
            GetStaticBool("Portalmaker", "canPortalFromAnywhere"))
        {
            var origin = bot.GetTruePosition();
            options.Add(new TorPortalTraversalOption(origin, first, true, 1, teleportDuration));
            options.Add(new TorPortalTraversalOption(origin, second, true, 2, teleportDuration));
        }

        return options;
    }

    internal static bool TryBeginPortalTeleport(
        PlayerControl bot,
        TorPortalTraversalOption option,
        out string outcome)
    {
        outcome = string.Empty;
        if (!bot || bot.Data is null || bot.Data.IsDead || bot.Data.Disconnected ||
            !bot.moveable || bot.inVent || bot.walkingToVent || IsHandcuffed(bot) ||
            MeetingHud.Instance || ExileController.Instance)
        {
            outcome = "player cannot legally enter a portal in the current game phase";
            return false;
        }

        if (PendingPortalTeleports.ContainsKey(bot.PlayerId))
        {
            outcome = "portal travel is already in progress";
            return false;
        }

        if (Time.time < NextPortalUseAt.GetValueOrDefault(bot.PlayerId))
        {
            outcome = "room-configured portal cooldown is still active";
            return false;
        }

        if (GetStaticBool("Portal", "isTeleporting") ||
            !TryGetPortalEndpoints(out var first, out var second, out var nativeDuration))
        {
            outcome = "the portal network is unavailable or currently occupied";
            return false;
        }

        var position = bot.GetTruePosition();
        var entryMatchesFirst = Vector2.Distance(option.Entry, first) <= 0.12f;
        var entryMatchesSecond = Vector2.Distance(option.Entry, second) <= 0.12f;
        var exitMatchesFirst = Vector2.Distance(option.Exit, first) <= 0.12f;
        var exitMatchesSecond = Vector2.Distance(option.Exit, second) <= 0.12f;
        if (option.Remote)
        {
            if (!TryGetRole(bot, out var role) || role.Name != "Portalmaker" ||
                !GetStaticBool("Portalmaker", "canPortalFromAnywhere") ||
                option.ExitMode is < 1 or > 2 ||
                option.ExitMode == 1 && !exitMatchesFirst ||
                option.ExitMode == 2 && !exitMatchesSecond)
            {
                outcome = "remote portal use is not legal for this player or endpoint";
                return false;
            }
        }
        else if ((!entryMatchesFirst || !exitMatchesSecond) && (!entryMatchesSecond || !exitMatchesFirst) ||
                 Vector2.Distance(position, option.Entry) > 0.32f)
        {
            outcome = "ordinary portal use requires physical proximity to the matching entry";
            return false;
        }

        var duration = Mathf.Max(0.1f, nativeDuration > 0.05f ? nativeDuration : option.Duration);
        if (!option.Remote)
        {
            bot.NetTransform.RpcSnapTo(option.Entry);
        }

        SendRpc(bot, 146, writer =>
        {
            writer.Write(bot.PlayerId);
            writer.Write(option.ExitMode);
        });
        InvokeProcedure("usePortal", bot.PlayerId, option.ExitMode);

        bot.moveable = false;
        bot.NetTransform.Halt();
        StopRoleChannelMovement(bot);
        PendingPortalTeleports[bot.PlayerId] = new PendingPortalTeleport(
            option.Entry,
            option.Exit,
            Time.time + duration * 0.5f,
            Time.time + duration,
            false);
        outcome = $"entered {(option.Remote ? "remote" : "nearby")} portal; nativeDuration={duration:0.00}s";
        _log?.LogInfo(
            $"DeepBot TOR portal travel started: bot={Describe(bot)}, entry={option.Entry}, exit={option.Exit}, " +
            $"remote={option.Remote}, exitMode={option.ExitMode}, duration={duration:0.00}s.");
        return true;
    }

    internal static bool IsMovementInverted(PlayerControl? player)
    {
        if (!player || GetStaticInt("Invert", "meetings") <= 0 ||
            GetStaticField("Invert", "invert") is not IEnumerable invertedPlayers)
        {
            return false;
        }

        return invertedPlayers.Cast<object>()
            .OfType<PlayerControl>()
            .Any(candidate => candidate && candidate.PlayerId == player!.PlayerId);
    }

    internal static bool IsHandcuffed(PlayerControl? player)
    {
        if (!player)
        {
            return false;
        }

        var id = player!.PlayerId;
        if (GetStaticField("Deputy", "handcuffedKnows") is IDictionary active && active.Contains(id))
        {
            return true;
        }

        return GetStaticField("Deputy", "handcuffedPlayers") is IEnumerable pending &&
               pending.Cast<object>().Any(value => Convert.ToByte(value) == id);
    }

    internal static bool AreLoverPartners(PlayerControl? first, PlayerControl? second)
    {
        if (!first || !second)
        {
            return false;
        }

        var lover1 = GetStaticField("Lovers", "lover1") as PlayerControl;
        var lover2 = GetStaticField("Lovers", "lover2") as PlayerControl;
        if (!lover1 || !lover2)
        {
            return false;
        }
        var firstPlayer = first!;
        var secondPlayer = second!;
        var firstLover = lover1!;
        var secondLover = lover2!;
        return (firstLover.PlayerId == firstPlayer.PlayerId && secondLover.PlayerId == secondPlayer.PlayerId) ||
               (secondLover.PlayerId == firstPlayer.PlayerId && firstLover.PlayerId == secondPlayer.PlayerId);
    }

    internal static bool AreKnownAllies(PlayerControl? first, PlayerControl? second)
    {
        if (!first || !second || first!.PlayerId == second!.PlayerId)
        {
            return first && second && first!.PlayerId == second!.PlayerId;
        }

        if (AreLoverPartners(first, second) || IsImpostorTeam(first) && IsImpostorTeam(second))
        {
            return true;
        }

        return TryGetRole(first, out var firstRole) &&
               TryGetRole(second, out var secondRole) &&
               firstRole.Name is "Jackal" or "Sidekick" &&
               secondRole.Name is "Jackal" or "Sidekick";
    }

    internal static bool ShouldProtectMeetingTarget(PlayerControl voter, PlayerControl? target)
    {
        if (!voter || !target || voter.PlayerId == target!.PlayerId)
        {
            return false;
        }

        if (AreKnownAllies(voter, target))
        {
            return true;
        }

        return TryGetRole(voter, out var voterRole) &&
               voterRole.Name == "Lawyer" &&
               GetStaticField("Lawyer", "target") is PlayerControl client &&
               client &&
               client.PlayerId == target.PlayerId;
    }

    internal static bool TryGetStrategicMeetingVoteTarget(PlayerControl voter, out byte targetId)
    {
        targetId = byte.MaxValue;
        if (!voter ||
            !TryGetRole(voter, out var voterRole) ||
            voterRole.Name != "Prosecutor" ||
            GetStaticField("Lawyer", "target") is not PlayerControl target ||
            !target ||
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            target.PlayerId == voter.PlayerId)
        {
            return false;
        }

        targetId = target.PlayerId;
        return true;
    }

    internal static void RegisterConfiguredCooldown(PlayerControl bot, TorRoleInfo role)
    {
        if (!bot)
        {
            return;
        }

        var field = role.Name switch
        {
            "Deputy" => "handcuffCooldown",
            "Trickster" => GetStaticBool("JackInTheBox", "boxesConvertedToVents") ? "lightsOutCooldown" : "placeBoxCooldown",
            "Yoyo" => "markCooldown",
            "Bomber" => "bombCooldown",
            _ => "cooldown"
        };
        var configured = Mathf.Max(0.05f, GetStaticFloat(role.Name, field));
        var key = (bot.PlayerId, role.Name);
        NextRoleAbilityAt[key] = Mathf.Max(NextRoleAbilityAt.GetValueOrDefault(key), Time.time + configured);
    }

    internal static bool HasNearbyUsableBody(PlayerControl bot)
    {
        if (!bot)
        {
            return false;
        }

        return UnityEngine.Object.FindObjectsOfType<DeadBody>()
            .Any(body =>
                DeadBodyPerception.IsVisibleAndReportable(body) &&
                Vector2.Distance(bot.GetTruePosition(), body.TruePosition) <= DeadBodyPerception.GetReportDistance(bot) &&
                !PhysicsHelpers.AnythingBetween(bot.GetTruePosition(), body.TruePosition, Constants.ShipAndObjectsMask, false));
    }

    internal static bool TryUseStrategicMeetingVoteAbility(PlayerControl bot, byte desiredVoteId, out string outcome)
    {
        outcome = string.Empty;
        if (!bot || !MeetingHud.Instance || desiredVoteId == 253 || !IsRoleOwner(bot, "Swapper"))
        {
            return false;
        }

        if (GetStaticInt("Swapper", "charges") <= 0 ||
            GetStaticInt("Swapper", "playerId1") != byte.MaxValue)
        {
            return false;
        }

        var voteAreas = MeetingHud.Instance.playerStates
            .ToArray()
            .Where(area => area is not null && !area.AmDead)
            .ToArray();
        var desiredArea = voteAreas.FirstOrDefault(area => area.TargetPlayerId == desiredVoteId);
        if (desiredArea is null)
        {
            return false;
        }

        var counts = voteAreas
            .Where(area => area.VotedFor >= 0 && area.VotedFor < 253)
            .GroupBy(area => (byte)area.VotedFor)
            .ToDictionary(group => group.Key, group => group.Count());
        var desiredCount = counts.GetValueOrDefault(desiredVoteId);
        var source = counts
            .Where(pair => pair.Key != desiredVoteId)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .FirstOrDefault();
        if (source.Value <= desiredCount || source.Value <= 0)
        {
            return false;
        }

        var sourceArea = voteAreas.FirstOrDefault(area => area.TargetPlayerId == source.Key);
        if (sourceArea is null ||
            GetStaticBool("Swapper", "canOnlySwapOthers") &&
            (source.Key == bot.PlayerId || desiredVoteId == bot.PlayerId))
        {
            return false;
        }

        SendRpc(bot, 129, writer =>
        {
            writer.Write(source.Key);
            writer.Write(desiredVoteId);
        });
        InvokeProcedure("swapperSwap", source.Key, desiredVoteId);
        SetStaticField("Swapper", "charges", GetStaticInt("Swapper", "charges") - 1);
        outcome = $"swapped current vote leader playerId={source.Key} with evidence-backed target playerId={desiredVoteId}";
        return true;
    }

    internal static bool TryGetPublicRoleAlignment(string roleName, out string alignment)
    {
        var spec = RoleSpecs.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, roleName, StringComparison.OrdinalIgnoreCase));
        alignment = spec?.Alignment ?? string.Empty;
        return spec is not null;
    }

    internal static bool TryUseEvidenceBackedGuesserShot(
        PlayerControl bot,
        PlayerControl target,
        string inferredRoleName,
        float inferenceConfidence,
        out string outcome)
    {
        outcome = string.Empty;
        if (!bot || !target || !MeetingHud.Instance || bot.Data is null || target.Data is null ||
            bot.Data.IsDead || target.Data.IsDead || target.Data.Disconnected ||
            inferenceConfidence < 0.90f || ShouldProtectMeetingTarget(bot, target))
        {
            return false;
        }

        var guesserRole = IsRoleOwner(bot, "NiceGuesser")
            ? "NiceGuesser"
            : IsRoleOwner(bot, "EvilGuesser")
                ? "EvilGuesser"
                : string.Empty;
        if (string.IsNullOrEmpty(guesserRole) ||
            GetStaticInt("Guesser", guesserRole == "NiceGuesser"
                ? "remainingShotsNiceGuesser"
                : "remainingShotsEvilGuesser") <= 0 ||
            !TryResolveTorRoleId(inferredRoleName, out var guessedRoleId))
        {
            return false;
        }

        if (!GetStaticBool("Utilities.HandleGuesser", "killsThroughShield") &&
            GetStaticField("Medic", "shielded") is PlayerControl shielded &&
            shielded && shielded.PlayerId == target.PlayerId)
        {
            outcome = $"held {guesserRole} shot because {Describe(target)} is visibly protected by TOR shield rules";
            return false;
        }

        var guessedCorrectly = TryGetRole(target, out var actualRole) &&
                               string.Equals(actualRole.Name, inferredRoleName, StringComparison.OrdinalIgnoreCase);
        var dyingTarget = guessedCorrectly ? target : bot;
        SendRpc(bot, 152, writer =>
        {
            writer.Write(bot.PlayerId);
            writer.Write(dyingTarget.PlayerId);
            writer.Write(target.PlayerId);
            writer.Write(guessedRoleId);
        });
        InvokeProcedure("guesserShoot", bot.PlayerId, dyingTarget.PlayerId, target.PlayerId, guessedRoleId);
        outcome = guessedCorrectly
            ? $"{guesserRole} correctly inferred {Describe(target)} as {inferredRoleName} from personally visible behavior"
            : $"{guesserRole} misguessed {Describe(target)} as {inferredRoleName} and TOR applied the normal self-elimination";
        return true;
    }

    private static bool TryResolveTorRoleId(string roleName, out byte roleId)
    {
        roleId = byte.MaxValue;
        if (!EnsureLoaded() || _assembly is null || string.IsNullOrWhiteSpace(roleName))
        {
            return false;
        }

        var roleInfoType = _assembly.GetType("TheOtherRoles.RoleInfo", false);
        if (roleInfoType?.GetField("allRoleInfos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) is not IEnumerable roleInfos)
        {
            return false;
        }

        foreach (var item in roleInfos)
        {
            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();
            var id = itemType.GetField("roleId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(item);
            var displayName = itemType.GetField("name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(item) as string;
            if (id is null ||
                !string.Equals(id.ToString(), roleName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(displayName, roleName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            roleId = Convert.ToByte(id);
            return true;
        }

        return false;
    }

    internal static DeadBody? FindVisibleUsableBody(PlayerControl bot)
    {
        if (!bot || bot.Data is null)
        {
            return null;
        }

        var vision = ShipStatus.Instance
            ? ShipStatus.Instance.CalculateLightRadius(bot.Data)
            : 5f;
        return UnityEngine.Object.FindObjectsOfType<DeadBody>()
            .Where(DeadBodyPerception.IsVisibleAndReportable)
            .Select(body => new
            {
                Body = body,
                Visible = DeadBodyPerception.CanObserve(bot, body, vision, out var distance, out _),
                Distance = distance
            })
            .Where(item => item.Visible)
            .OrderBy(item => item.Distance)
            .Select(item => item.Body)
            .FirstOrDefault();
    }

    internal static bool ShouldReserveBodyForAbility(PlayerControl bot)
    {
        return TryGetRole(bot, out var role) && role.Name == "Vulture";
    }

    internal static bool CanUseVents(PlayerControl bot, TorRoleInfo role)
    {
        if (!bot || bot.Data is null || IsHandcuffed(bot) || !EnsureLoaded() || _helpersType is null)
        {
            return false;
        }

        try
        {
            _roleCanUseVentsMethod ??= _helpersType.GetMethod(
                "roleCanUseVents",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(PlayerControl) },
                null);
            var nativeAllowsVenting = _roleCanUseVentsMethod?.Invoke(null, new object[] { bot }) is true;
            if (!nativeAllowsVenting)
            {
                return false;
            }

            return !string.Equals(role.Name, "Engineer", StringComparison.Ordinal) ||
                   IsTorEngineerVentReady(bot, out _);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(
                $"DeepBot TOR native vent permission failed: player={bot.Data?.PlayerName}({bot.PlayerId}), " +
                $"role={role.Name}, error={ex.GetBaseException().Message}");
            return false;
        }
    }

    private static bool IsTorEngineerVentReady(PlayerControl bot, out float remainingSeconds)
    {
        remainingSeconds = 0f;
        if (!EnsureLoaded() || _assembly is null)
        {
            return false;
        }

        try
        {
            var type = _assembly.GetType(TorEngineerVentRulesTypeName, false);
            var method = type?.GetMethod(
                "CanEnter",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(PlayerControl), typeof(float).MakeByRefType() },
                null);
            if (method is null)
            {
                return false;
            }

            object[] arguments = { bot, 0f };
            var ready = method.Invoke(null, arguments) is true;
            remainingSeconds = arguments[1] is float seconds ? Mathf.Max(0f, seconds) : 0f;
            return ready;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(
                $"DeepBot TOR Engineer native vent readiness failed: player={bot.Data?.PlayerName}({bot.PlayerId}), " +
                $"error={ex.GetBaseException().Message}");
            return false;
        }
    }

    internal static bool TryEnterTorEngineerVent(
        PlayerControl bot,
        int ventId,
        out string outcome)
    {
        outcome = "TOR Engineer native vent rules unavailable";
        if (!EnsureLoaded() || _assembly is null)
        {
            return false;
        }

        try
        {
            var assembly = _assembly;
            if (assembly is null)
            {
                outcome = "TOR assembly is unavailable";
                return false;
            }

            var type = assembly.GetType(TorEngineerVentRulesTypeName, false);
            var method = type?.GetMethod(
                "TryEnterVent",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(PlayerControl), typeof(int), typeof(string).MakeByRefType() },
                null);
            if (method is null)
            {
                return false;
            }

            object[] arguments = { bot, ventId, string.Empty };
            var entered = method.Invoke(null, arguments) is true;
            outcome = arguments[2] as string ?? (entered ? "entered through TOR native rules" : "TOR rejected vent entry");
            return entered;
        }
        catch (Exception ex)
        {
            outcome = $"TOR Engineer native vent invocation failed: {ex.GetBaseException().Message}";
            _log?.LogWarning(
                $"DeepBot TOR Engineer native vent invocation failed: player={bot.Data?.PlayerName}({bot.PlayerId}), " +
                $"vent={ventId}, error={ex.GetBaseException()}");
            return false;
        }
    }

    internal static bool IsArsonistReadyToIgnite(PlayerControl bot)
    {
        return TryGetRole(bot, out var role) && role.Name == "Arsonist" && DousedEveryoneAlive(bot);
    }

    internal static PlayerControl? FindPreferredAbilityTarget(PlayerControl bot, TorRoleInfo role)
    {
        return PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player => IsLegalNearbyTarget(bot, player, role))
            .Where(player => role.Name != "Arsonist" || !IsDoused(player.PlayerId))
            .OrderBy(player => Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()))
            .FirstOrDefault();
    }

    internal static PlayerControl? FindArsonistPursuitTarget(PlayerControl bot)
    {
        if (!bot || !TryGetRole(bot, out var role) || role.Name != "Arsonist")
        {
            return null;
        }

        return PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player =>
                IsLivingOpponent(bot, player, role) &&
                !IsDoused(player.PlayerId) &&
                BotPerceptionPolicy.CanBeOrdinarilyObserved(player))
            .Select(player => new
            {
                Player = player,
                Distance = Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()),
                Blocked = PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    player.GetTruePosition(),
                    Constants.ShipOnlyMask,
                    false)
            })
            .Where(item => item.Distance <= 8.5f && !item.Blocked)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Player.PlayerId)
            .Select(item => item.Player)
            .FirstOrDefault();
    }

    internal static string BuildKnownRoleInformation(PlayerControl bot, TorRoleInfo role)
    {
        if (role.Name is "Jackal" or "Sidekick")
        {
            var allies = new List<string>();
            AddRoleOwner(allies, "Jackal", "jackal", bot.PlayerId);
            AddRoleOwner(allies, "Sidekick", "sidekick", bot.PlayerId);
            return WithModifierInformation(bot, allies.Count == 0
                ? "No living Jackal-team ally is currently known."
                : $"Known Jackal-team allies: {string.Join(", ", allies)}. Protect their cover and do not accuse them casually.");
        }

        if (role.Name is "Lawyer" or "Prosecutor")
        {
            var client = GetStaticField("Lawyer", "target") as PlayerControl;
            var objective = role.Name == "Prosecutor"
                ? "Build a truthful case that gets this target voted out."
                : "Defend that client without fabricating observations.";
            return WithModifierInformation(bot, client && client!.Data is not null
                ? $"Your assigned {(role.Name == "Prosecutor" ? "prosecution target" : "client")} is {Describe(client)}. Win condition: {role.WinCondition} {objective}"
                : $"No living {role.Name} target is currently known. Win condition: {role.WinCondition}");
        }

        if (role.IsImpostorTeam)
        {
            var allies = PlayerControl.AllPlayerControls
                .ToArray()
                .Where(player =>
                    player &&
                    player.PlayerId != bot.PlayerId &&
                    player.Data is not null &&
                    !player.Data.Disconnected &&
                    IsImpostorTeam(player))
                .Select(Describe)
                .ToArray();
            return WithModifierInformation(bot, allies.Length == 0
                ? "No living impostor teammate is known."
                : $"Known impostor teammates: {string.Join(", ", allies)}. Preserve their cover unless sacrificing one is unavoidable.");
        }

        return WithModifierInformation(bot, role.IsNeutral
            ? $"Your hidden independent role is {role.Name}. Win condition: {role.WinCondition} Live plan: {BuildStrategicRoleBrief(bot, role)} Other hidden roles are not known unless personally observed."
            : $"Your hidden crew role is {role.Name}. You do not automatically know anyone else's hidden role.");
    }

    internal static string BuildPublicDeductionRulebook()
    {
        var crew = string.Join(", ", RoleSpecs.Where(role => role.Alignment == "crewmate").Select(role => role.Name));
        var impostor = string.Join(", ", RoleSpecs.Where(role => role.Alignment == "impostor").Select(role => role.Name));
        var neutral = string.Join(", ", RoleSpecs.Where(role => role.Alignment == "neutral").Select(role => role.Name));
        var modifiers = string.Join(", ", ModifierSpecs.Select(modifier => modifier.Name).Distinct(StringComparer.Ordinal));
        var outcomeMap = string.Join(", ", RoleSpecs.Select(role =>
            $"{role.Name}={BuildPublicWinConditionBrief(role.Name, role.Alignment)}"));
        return
            $"Public TOR role rulebook (possible roles, never secret assignments): crew=[{crew}]; impostor=[{impostor}]; neutral=[{neutral}]; modifiers=[{modifiers}]. " +
            $"Public strategic outcome map (possible win goals, not assignments): {outcomeMap}. " +
            "Deduction constraints: a witnessed vent proves only a vent-capable role (ordinary impostor, Engineer, or a room-enabled Jackal/Sidekick/Spy/Vulture/Thief); " +
            "a witnessed kill proves a kill-capable role, which can also be Sheriff, Jackal faction, Vampire, Warlock, Ninja, Thief, Bomber, Arsonist, or another hostile custom role, so use target legality and aftermath to narrow it; " +
            "a disappearing body can indicate Janitor, Cleaner, Vulture, or another explicit body-removal skill; fake-task standing and task-bar motion alone do not prove crew; " +
            "Jester wants exile, Arsonist must douse everyone then ignite, Vulture must consume bodies, Lawyer/Prosecutor act around their assigned target, Pursuer prioritizes survival, and Jackal/Sidekick have an independent faction objective. " +
            "Every player must optimize the actual goal of their own current role: crew protects crew victory, impostors protect impostor-team victory, and neutrals prioritize their independent win. " +
            "A public Jester self-claim creates exile risk: faction players should not hand over a Jester win on speech alone; require stronger physical evidence or choose another candidate. " +
            "Lovers must preserve the known partner, and an ungrown Mini must not be killed because that can immediately award the Mini outcome under TOR rules. " +
            "Only infer from personally visible actions and public claims; never read another player's hidden role from engine state.";
    }

    private static string BuildPublicWinConditionBrief(string roleName, string alignment)
    {
        if (string.Equals(alignment, "crewmate", StringComparison.Ordinal))
        {
            return "crew victory by tasks or removing every hostile faction";
        }

        if (string.Equals(alignment, "impostor", StringComparison.Ordinal))
        {
            return "impostor-team parity or fatal sabotage";
        }

        return roleName switch
        {
            "Jester" => "be voted out",
            "Jackal" => "Jackal faction eliminates all outsiders",
            "Sidekick" => "help or inherit the Jackal faction and eliminate outsiders",
            "Arsonist" => "fully douse every other living player then ignite",
            "Vulture" => "consume the configured number of bodies",
            "Prosecutor" => "get the assigned prosecution target voted out",
            "Lawyer" => "keep the assigned client alive and make the client faction win, or convert after client loss",
            "Pursuer" => "survive through a non-impostor victory",
            "Thief" => "legally steal a hostile role then inherit that faction goal",
            "Shifter" => "shift into another role then pursue the inherited role goal",
            _ => $"independent {roleName} objective"
        };
    }

    internal static bool TryGetNearestMediumSoulPosition(PlayerControl bot, out Vector2 position)
    {
        if (TryFindMediumSoul(bot, false, out _, out _, out var soulPosition))
        {
            position = soulPosition;
            return true;
        }

        position = default;
        return false;
    }

    internal static string BuildStrategicRoleBrief(PlayerControl bot, TorRoleInfo role)
    {
        var live = role.Name switch
        {
            "Engineer" => $"Repairs remaining={GetStaticInt("Engineer", "remainingFixes")}; reserve a charge for a dangerous active emergency and otherwise use room-legal vents only for a purposeful rotation.",
            "Mayor" => $"Remote meetings remaining={GetStaticInt("Mayor", "remoteMeetingsLeft")}; call one only when retained evidence is strong enough to justify interrupting the round.",
            "Medic" => GetStaticBool("Medic", "usedShield")
                ? "Shield is already committed; observe whether the protected player is pressured and use that information later without revealing hidden mechanics."
                : "Shield one exposed, useful, or credibly trusted player; do not select a random nearest body merely because the button is ready.",
            "Sheriff" => "The shot is lethal evidence enforcement, not a scouting tool. Require a witnessed hostile act or a strong retained meeting case and account for room-enabled neutral/Spy/Mini rules before firing.",
            "Deputy" => $"Handcuffs remaining={GetStaticFloat("Deputy", "remainingHandcuffs"):0}; restrain an evidence-backed threat before it can use a kill or special ability, then reassess after the configured duration.",
            "Tracker" => GetStaticBool("Tracker", "usedTracker")
                ? "A tracking target is already committed; use the resulting route information as evidence instead of repeatedly searching for another target."
                : "Track a trusted escort or a concrete suspect whose future route answers a real question.",
            "TimeMaster" => GetStaticBool("TimeMaster", "shieldActive")
                ? "Time shield is active; stay close enough to the threatened area for a rewind to matter."
                : "Raise the shield only around credible immediate danger, not in an empty room.",
            "Camouflager" => GetStaticFloat("Camouflager", "camouflageTimer") > 0.05f
                ? "Camouflage is active; execute the planned rotation, rescue, or elimination and leave before identities become reliable again."
                : "Wait for a concrete kill, escape, or identity-confusion opportunity before spending camouflage.",
            "Hacker" => $"Information charges: admin={GetStaticInt("Hacker", "chargesAdminTable")}, vitals={GetStaticInt("Hacker", "chargesVitals")}; spend the source that resolves the current uncertainty, then remember the anonymous nature of admin information.",
            "Medium" => $"Available souls={GetStaticCollectionCount("Medium", "deadBodies")}; approach a legal soul and retain only the clue TOR actually supplies.",
            "Arsonist" => BuildArsonistProgress(bot),
            "Vulture" => $"Progress: eaten={GetStaticInt("Vulture", "eatenBodies")}/{GetStaticInt("Vulture", "vultureNumberToWin")}; seek an accessible body and consume it instead of reporting when safe.",
            "Jackal" => GetStaticBool("Jackal", "canCreateSidekick")
                ? "Phase: recruit one strategically useful nearby player before beginning risky eliminations."
                : "Phase: Sidekick choice is complete; isolate and eliminate non-allies. Fake tasks remain cover only.",
            "Sidekick" => GetStaticBool("Sidekick", "canKill")
                ? "Phase: support the Jackal and take safe isolated kills against non-allies."
                : "Phase: cannot kill under current room settings; protect and assist the Jackal through cover and voting.",
            "Lawyer" => GetStaticField("Lawyer", "target") is PlayerControl client && client
                ? $"Client={Describe(client)}. Keep this client alive and help their team win; if the client dies, adapt to Pursuer survival."
                : "No active client is available; prepare for the Pursuer survival objective.",
            "Prosecutor" => GetStaticField("Lawyer", "target") is PlayerControl prosecutionTarget && prosecutionTarget
                ? $"Prosecution target={Describe(prosecutionTarget)}. Accumulate credible meeting evidence and steer votes toward that target without revealing the hidden objective."
                : "No active prosecution target is available; follow TOR's conversion/fallback behavior.",
            "Pursuer" => $"Blanks used={GetStaticInt("Pursuer", "blanks")}/{GetStaticInt("Pursuer", "blanksNumber")}; survive and prevent an impostor win.",
            "Thief" => "Phase: observe likely hostile roles, then attempt one isolated eligible kill to steal that role; avoid random crew targets because a failed theft is suicide.",
            "Vampire" => PendingVampireBites.TryGetValue(bot.PlayerId, out var bite)
                ? $"A delayed bite on playerId={bite.TargetId} is in progress; preserve cover until it resolves."
                : "Seek an isolated target, then bite only when the delayed death creates no obvious trail back to you.",
            "Warlock" => PendingWarlockCurses.TryGetValue(bot.PlayerId, out var curse)
                ? $"PlayerId={curse.VictimId} is cursed; wait for that player to approach a useful second target."
                : "Choose a mobile curse carrier likely to approach an isolated opponent; the redirected kill should conceal your location.",
            "Ninja" => GetStaticField("Ninja", "ninjaMarked") is PlayerControl marked && marked
                ? $"Marked target={Describe(marked)}; strike only when the remote assassination and invisibility create a credible escape."
                : "Mark an isolated useful target first; do not mark randomly when witnesses make the later strike obvious.",
            "Morphling" => GetStaticField("Morphling", "sampledTarget") is PlayerControl sampled && sampled
                ? $"Phase: sampled {Describe(sampled)}. Move out of other players' sight, then trigger the second stage to morph; never skip the sample stage."
                : "Phase: choose and approach one credible disguise target, sample them first, then disengage before transforming.",
            "Portalmaker" => GetStaticField("Portal", "firstPortal") is not null && GetStaticField("Portal", "secondPortal") is null
                ? "Phase: first portal is placed; travel to a meaningfully separated useful room and place the second endpoint."
                : GetStaticBool("Portal", "bothPlacedAndEnabled")
                    ? "Phase: both endpoints are active. Use the native timed portal only when its entry, animation, and exit make the current route meaningfully shorter."
                    : "Phase: place the first endpoint where later rotations matter, then complete the pair in a distant room.",
            "Trickster" => BuildTricksterProgress(bot),
            "Cleaner" or "Janitor" => "A body-removal skill must be used only on a physically visible nearby corpse when witness risk is acceptable; after cleaning, leave or construct ordinary cover instead of standing on the vanished body.",
            "Eraser" => GetStaticCollectionCount("Eraser", "futureErased") > 0
                ? "An erasure is already scheduled for the next resolution; preserve cover and do not treat the hidden target as public knowledge."
                : "Schedule erasure on a high-value opposing role only when personal evidence makes that target strategically credible.",
            "Witch" => PendingWitchSpells.TryGetValue(bot.PlayerId, out var spell)
                ? $"Spell channel is active on playerId={spell.TargetId}; stay in native range and line of sight until completion or abort cleanly if legality changes."
                : "Select an isolated high-value opponent, complete the configured cast without moving, then leave before the meeting resolves the spell.",
            "Shifter" => GetStaticField("Shifter", "futureShift") is PlayerControl futureShift && futureShift
                ? $"A shift with {Describe(futureShift)} is scheduled; survive until TOR resolves it and then adopt the inherited objective."
                : "Shift only when observed evidence makes the target role worth inheriting; a blind nearest-player shift can destroy the current objective.",
            "Trapper" => $"Trap charges remaining={GetStaticInt("Trapper", "charges")}; place separated information traps on real traffic lines and interpret only TOR-revealed trigger data.",
            "SecurityGuard" => $"Screws remaining={GetStaticInt("SecurityGuard", "remainingScrews")}; on MIRA seal a reachable vent, otherwise choose between a separated camera and vent seal according to map legality and information value.",
            "Bomber" => GetStaticBool("Bomber", "isPlanted")
                ? "A bomb is active; clear its native blast area, avoid exposing ownership, and let TOR handle arming, defuse, and explosion outcomes."
                : "Plant only where predicted traffic and timing create a deliberate split or elimination, then immediately execute an escape/alibi stage.",
            "Yoyo" => GetStaticField("Yoyo", "markedLocation") is not null
                ? "Phase: return point is armed; blink only for a concrete ambush, alibi, escape, or concealed rotation, then let the timed return resolve."
                : "Phase: mark a defensible return point first, then move elsewhere before using the second-stage blink.",
            "Godfather" => "Lead ordinary kills while alive; protect the Mafioso's cover and choose targets by isolation, faction value, and escape feasibility rather than always preferring the human host.",
            "Mafioso" => GetStaticField("Godfather", "godfather") is PlayerControl godfather && godfather.Data is not null && !godfather.Data.IsDead && !godfather.Data.Disconnected
                ? $"Godfather {Describe(godfather)} is alive, so ordinary killing is locked; fake tasks, gather cover, and support the team until succession unlocks."
                : "The Godfather is gone or disconnected; succession has unlocked ordinary kills, so adopt the killer role without remaining idle.",
            "BountyHunter" => GetStaticField("BountyHunter", "bounty") is PlayerControl bounty && bounty
                ? $"Current bounty={Describe(bounty)}; prefer it only when the chase is safe, otherwise abandon pursuit and accept TOR's configured cooldown penalty."
                : "No live bounty is currently assigned; maintain normal impostor cover and target selection.",
            "NiceGuesser" or "EvilGuesser" => "Meeting shot only: guess an exact role solely from a uniquely identifying witnessed action or equivalent strong evidence; a blind guess can kill the guesser.",
            "Swapper" => "Meeting only: swap two vote columns only when the predicted tally and personal objective make the changed outcome better than leaving votes untouched.",
            "Lighter" => "Use improved vision as an observation advantage, not as permission to claim events through walls or outside current light radius.",
            "Detective" => "Build deductions from personal footprints and timelines; distinguish a route clue from proof of a hidden role.",
            "Seer" => "Retain only soul information TOR visibly exposes and combine it with later public evidence without revealing engine-only identities.",
            "Snitch" => "Prioritize completing real assigned tasks while avoiding obvious isolation near the final-task reveal threshold.",
            "Spy" => "Use impostor-facing information carefully while preserving an ordinary crew route; vent use depends on the room option and remains publicly suspicious.",
            "Jester" => "Phase: create believable inconsistencies and attract votes gradually; do not perform an obvious role confession that rational players would ignore.",
            _ => string.Empty
        };
        var modifierPlan = BuildModifierPlan(bot);
        return $"Win condition: {role.WinCondition} Ability/operating plan: {role.AbilityPurpose}" +
               (string.IsNullOrWhiteSpace(live) ? string.Empty : $" {live}") +
               (string.IsNullOrWhiteSpace(modifierPlan) ? string.Empty : $" {modifierPlan}");
    }

    private static string BuildTricksterProgress(PlayerControl bot)
    {
        var boxes = GetStaticCollectionCount("JackInTheBox", "AllJackInTheBoxes");
        var limit = Mathf.Max(1, GetStaticInt("JackInTheBox", "JackInTheBoxLimit"));
        var converted = GetStaticBool("JackInTheBox", "boxesConvertedToVents");
        var darkness = GetStaticFloat("Trickster", "lightsOutTimer") > 0.05f;
        if (boxes < limit)
        {
            return $"Phase: build separated box network; progress={boxes}/{limit}. Route to a different useful room before each placement.";
        }

        if (!converted)
        {
            return $"Phase: all {limit} boxes are placed; wait for TOR's next-meeting conversion instead of repeatedly pressing the skill.";
        }

        return darkness
            ? "Phase: darkness is active; use the converted box vents for a concealed ambush or escape, then leave the scene."
            : "Phase: converted box network is ready; trigger darkness only with a concrete target, kill, escape, or time-pressure plan.";
    }

    internal static void Update(int matchSerial)
    {
        SynchronizeMatchState(matchSerial);
        UpdateVirtualBotHandcuffs();
        UpdateVirtualBotTrapTriggers();
        UpdatePortalAvailabilityAndTravel();

        foreach (var pair in PendingRoleRoots.ToArray())
        {
            var rooted = FindPlayer(pair.Key);
            if (Time.time < pair.Value && rooted && rooted!.Data is not null && !rooted.Data.IsDead)
            {
                rooted.moveable = false;
                StopRoleChannelMovement(rooted);
                continue;
            }

            PendingRoleRoots.Remove(pair.Key);
            if (rooted && rooted!.Data is not null && !rooted.Data.IsDead &&
                !MeetingHud.Instance && !ExileController.Instance && !IsRuleImmobilized(rooted))
            {
                rooted.moveable = true;
            }
            _log?.LogInfo($"DeepBot TOR role root expired: bot={Describe(rooted)}.");
        }

        foreach (var pair in PendingWitchSpells.ToArray())
        {
            var bot = FindPlayer(pair.Key);
            var target = FindPlayer(pair.Value.TargetId);
            var witchRole = default(TorRoleInfo);
            var roleMatches = bot && TryGetRole(bot!, out witchRole) && witchRole.Name == "Witch";
            var targetRemainsLegal = roleMatches &&
                                     target &&
                                     IsLegalNearbyTarget(bot!, target, witchRole) &&
                                     GetStaticField("Witch", "spellCastingTarget") is PlayerControl castingTarget &&
                                     castingTarget &&
                                     castingTarget.PlayerId == target!.PlayerId;
            if (!targetRemainsLegal)
            {
                CancelPendingWitchSpell(pair.Key, bot, target, "target changed, became hidden, moved out of range, or lost line of sight");
                continue;
            }

            StopRoleChannelMovement(bot!);
            if (Time.time < pair.Value.CompleteAt)
            {
                continue;
            }

            CompletePendingWitchSpell(bot!, target!, witchRole);
        }

        foreach (var pair in PendingDouses.ToArray())
        {
            var bot = FindPlayer(pair.Key);
            var target = FindPlayer(pair.Value.TargetId);
            if (!bot || !target || bot!.Data is null || target!.Data is null || bot.Data.IsDead || target.Data.IsDead ||
                BotPerceptionPolicy.IsConcealedByVent(target) ||
                Vector2.Distance(bot.GetTruePosition(), target.GetTruePosition()) > 2.25f)
            {
                PendingDouses.Remove(pair.Key);
                NextRoleAbilityAt[(pair.Key, "Arsonist")] = Time.time + 4f;
                _log?.LogInfo($"DeepBot Arsonist douse interrupted: bot={Describe(bot)}, target={Describe(target)}.");
                continue;
            }

            if (bot.MyPhysics)
            {
                bot.MyPhysics.SetNormalizedVelocity(Vector2.zero);
                if (bot.MyPhysics.body)
                {
                    bot.MyPhysics.body.velocity = Vector2.zero;
                }
            }

            if (Time.time < pair.Value.CompleteAt)
            {
                continue;
            }

            AddDousedPlayer(target);
            SendRpc(bot, 179, writer =>
            {
                writer.Write(bot.PlayerId);
                writer.Write((byte)2);
                writer.Write(target.PlayerId);
            });
            PendingDouses.Remove(pair.Key);
            NextRoleAbilityAt[(pair.Key, "Arsonist")] = Time.time + Mathf.Max(0.05f, GetStaticFloat("Arsonist", "cooldown"));
            _log?.LogInfo($"DeepBot Arsonist douse completed: bot={Describe(bot)}, target={Describe(target)}, {BuildArsonistProgress(bot)}");
        }

        foreach (var pair in PendingVampireBites.ToArray())
        {
            if (Time.time < pair.Value.CompleteAt) continue;
            var bot = FindPlayer(pair.Key);
            var target = FindPlayer(pair.Value.TargetId);

            var killed = false;
            var result = "bite target no longer valid";
            if (bot && target && bot!.Data is not null && target!.Data is not null &&
                !bot.Data.IsDead && !target.Data.IsDead && !target.Data.Disconnected &&
                TryGetRole(bot, out var vampireRole) && vampireRole.Name == "Vampire")
            {
                if (!TryExecuteRuleAwareMurder(bot, target, out killed, out result, showAnimation: false))
                {
                    result = "TOR murder validator unavailable; delayed bite was safely cancelled";
                }
            }

            // Keep the pending marker alive while MurderPlayer runs.  The
            // VampireDelayedDeathPositionPatch uses it to distinguish this
            // delayed, animation-free death from an ordinary kill and prevent
            // the vampire from being snapped to the victim by native kill
            // presentation code.  Clearing it before the murder made that
            // invariant impossible to recognize.
            PendingVampireBites.Remove(pair.Key);

            if (bot)
            {
                SendRpc(bot!, 133, writer =>
                {
                    writer.Write(byte.MaxValue);
                    writer.Write(byte.MaxValue);
                });
                InvokeProcedure("vampireSetBitten", byte.MaxValue, byte.MaxValue);
                NextRoleAbilityAt[(bot!.PlayerId, "Vampire")] =
                    Time.time + Mathf.Max(0.05f, GetStaticFloat("Vampire", "cooldown"));
            }
            _log?.LogInfo($"DeepBot Vampire delayed bite resolved: bot={Describe(bot)}, target={Describe(target)}, killed={killed}, result={result}.");
        }

        foreach (var pair in PendingWarlockCurses.ToArray())
        {
            var bot = FindPlayer(pair.Key);
            var victim = FindPlayer(pair.Value.VictimId);
            if (!bot || !victim || bot!.Data is null || victim!.Data is null || bot.Data.IsDead || victim.Data.IsDead ||
                victim.Data.Disconnected || !TryGetRole(bot, out var warlockRole) || warlockRole.Name != "Warlock" ||
                MeetingHud.Instance || ExileController.Instance)
            {
                ClearWarlockCurse(pair.Key, "meeting started or curse carrier became unavailable");
                continue;
            }

            if (BotPerceptionPolicy.IsConcealedByVent(victim))
            {
                SetStaticField("Warlock", "curseVictimTarget", null!);
                continue;
            }

            SetStaticField("Warlock", "curseVictimTarget", FindWarlockSecondTarget(bot, victim.PlayerId)!);
        }

        foreach (var pair in PendingNinjaReveals.ToArray())
        {
            if (Time.time < pair.Value) continue;
            PendingNinjaReveals.Remove(pair.Key);
            var bot = FindPlayer(pair.Key);
            if (!bot || !TryGetRole(bot, out var ninjaRole) || ninjaRole.Name != "Ninja") continue;
            SendRpc(bot!, 160, writer =>
            {
                writer.Write(bot!.PlayerId);
                writer.Write(byte.MaxValue);
            });
            InvokeProcedure("setInvisible", bot!.PlayerId, byte.MaxValue);
            _log?.LogInfo($"DeepBot Ninja invisibility ended: bot={Describe(bot)}.");
        }

        foreach (var pair in PendingYoyoReturns.ToArray())
        {
            if (Time.time < pair.Value) continue;
            var bot = FindPlayer(pair.Key);
            PendingYoyoReturns.Remove(pair.Key);
            if (!bot || bot!.Data is null || bot.Data.IsDead || GetStaticField("Yoyo", "markedLocation") is null)
            {
                continue;
            }

            var buffer = BuildPositionBuffer(bot.GetTruePosition());
            SendRpc(bot, 169, writer =>
            {
                writer.Write((byte)0);
                writer.WriteBytesAndSize(buffer);
            });
            InvokeProcedure("yoyoBlink", false, buffer);
            NextRoleAbilityAt[(bot.PlayerId, "Yoyo")] =
                Time.time + Mathf.Max(0.05f, GetStaticFloat("Yoyo", "markCooldown"));
            _log?.LogInfo($"DeepBot Yoyo strategic return completed: bot={Describe(bot)}.");
        }
    }

    private static void SynchronizeMatchState(int matchSerial)
    {
        if (matchSerial <= 0 || matchSerial == _observedMatchSerial)
        {
            return;
        }

        _observedMatchSerial = matchSerial;
        LoggedAssignments.Clear();
        LoggedAssignmentConflicts.Clear();
        PendingDouses.Clear();
        PendingVampireBites.Clear();
        PendingWarlockCurses.Clear();
        PendingWitchSpells.Clear();
        PendingRoleRoots.Clear();
        PendingNinjaReveals.Clear();
        PendingYoyoReturns.Clear();
        PendingPortalTeleports.Clear();
        NextPortalUseAt.Clear();
        NextRoleAbilityAt.Clear();
        StrategicPlacements.Clear();
        _portalsWereEnabled = false;
        _nextVirtualTrapScanAt = 0f;
        _log?.LogInfo($"DeepBot TOR staged-role state reset for new match: match={matchSerial}.");
    }

    private static void UpdatePortalAvailabilityAndTravel()
    {
        var portalsEnabled = GetStaticBool("Portal", "bothPlacedAndEnabled");
        if (portalsEnabled && !_portalsWereEnabled)
        {
            var cooldown = Mathf.Max(0f, GetStaticFloat("Portalmaker", "usePortalCooldown"));
            if (PlayerControl.AllPlayerControls is not null)
            {
                foreach (var bot in PlayerControl.AllPlayerControls.ToArray().Where(DeepBotIdentity.IsBot))
                {
                    NextPortalUseAt[bot.PlayerId] = Time.time + cooldown;
                }
            }
            _log?.LogInfo($"DeepBot TOR portal network enabled: initialCooldown={cooldown:0.00}s.");
        }
        else if (!portalsEnabled && _portalsWereEnabled)
        {
            NextPortalUseAt.Clear();
        }
        _portalsWereEnabled = portalsEnabled;

        foreach (var pair in PendingPortalTeleports.ToArray())
        {
            var bot = FindPlayer(pair.Key);
            var travel = pair.Value;
            if (!bot || bot!.Data is null || bot.Data.IsDead || bot.Data.Disconnected ||
                MeetingHud.Instance || ExileController.Instance || !portalsEnabled)
            {
                PendingPortalTeleports.Remove(pair.Key);
                if (bot && bot!.Data is not null && !bot.Data.IsDead && !IsHandcuffed(bot))
                {
                    bot.moveable = !MeetingHud.Instance && !ExileController.Instance;
                }
                _log?.LogInfo($"DeepBot TOR portal travel cancelled safely: bot={Describe(bot)}, portalEnabled={portalsEnabled}.");
                continue;
            }

            bot.moveable = false;
            bot.NetTransform.Halt();
            StopRoleChannelMovement(bot);
            if (!travel.Moved && Time.time >= travel.MidpointAt)
            {
                bot.NetTransform.RpcSnapTo(travel.Exit);
                travel = travel with { Moved = true };
                PendingPortalTeleports[pair.Key] = travel;
                _log?.LogInfo($"DeepBot TOR portal midpoint reached: bot={Describe(bot)}, exit={travel.Exit}.");
            }

            if (Time.time < travel.CompleteAt)
            {
                continue;
            }

            PendingPortalTeleports.Remove(pair.Key);
            NextPortalUseAt[pair.Key] = Time.time + Mathf.Max(0f, GetStaticFloat("Portalmaker", "usePortalCooldown"));
            if (!IsHandcuffed(bot) && !MeetingHud.Instance && !ExileController.Instance)
            {
                bot.moveable = true;
            }
            _log?.LogInfo($"DeepBot TOR portal travel completed: bot={Describe(bot)}, exit={travel.Exit}.");
        }
    }

    private static void UpdateVirtualBotHandcuffs()
    {
        if (!EnsureLoaded() || GetStaticField("Deputy", "handcuffedKnows") is not IDictionary active)
        {
            return;
        }

        var pending = GetStaticField("Deputy", "handcuffedPlayers") as IEnumerable;
        foreach (var bot in PlayerControl.AllPlayerControls.ToArray().Where(DeepBotIdentity.IsBot))
        {
            var id = bot.PlayerId;
            var newlyPending = pending?.Cast<object>().Any(value => Convert.ToByte(value) == id) == true;
            if (newlyPending && !active.Contains(id))
            {
                InvokeRoleMethod("Deputy", "setHandcuffedKnows", true, id);
                _log?.LogInfo($"DeepBot Deputy handcuff activated: bot={Describe(bot)}, duration={GetStaticFloat("Deputy", "handcuffDuration"):0.0}s.");
            }

            if (!active.Contains(id))
            {
                continue;
            }

            var remaining = Convert.ToSingle(active[id]) - Time.deltaTime;
            if (remaining > 0f)
            {
                active[id] = remaining;
                continue;
            }

            active.Remove(id);
            InvokeRoleMethod("Deputy", "setHandcuffedKnows", false, id);
            _log?.LogInfo($"DeepBot Deputy handcuff expired: bot={Describe(bot)}.");
        }
    }

    private static void UpdateVirtualBotTrapTriggers()
    {
        if (Time.time < _nextVirtualTrapScanAt ||
            !EnsureLoaded() ||
            AmongUsClient.Instance is null ||
            !AmongUsClient.Instance.AmHost)
        {
            return;
        }
        _nextVirtualTrapScanAt = Time.time + VirtualTrapScanIntervalSeconds;

        try
        {
            _trapType ??= _assembly?.GetType("TheOtherRoles.Objects.Trap");
            var traps = _trapType?.GetField("traps", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IEnumerable;
            if (traps is null)
            {
                return;
            }

            var trapper = GetStaticField("Trapper", "trapper") as PlayerControl;
            var triggerDistance = UnityEngine.Object.FindObjectsOfType<Vent>()
                .Where(vent => vent)
                .Select(vent => vent.UsableDistance * 0.5f)
                .DefaultIfEmpty(0.7f)
                .First();

            foreach (var bot in PlayerControl.AllPlayerControls.ToArray().Where(DeepBotIdentity.IsBot))
            {
                if (!bot || bot.Data is null || bot.Data.IsDead || bot.Data.Disconnected ||
                    BotPerceptionPolicy.IsConcealedByVent(bot) || !bot.moveable ||
                    trapper && trapper!.PlayerId == bot.PlayerId || IsRuleImmobilized(bot))
                {
                    continue;
                }

                foreach (var trap in traps)
                {
                    if (trap is null)
                    {
                        continue;
                    }

                    var type = trap.GetType();
                    var revealed = (bool?)type.GetField("revealed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(trap) == true;
                    var triggerable = (bool?)type.GetField("triggerable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(trap) == true;
                    var gameObject = type.GetField("trap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(trap) as GameObject;
                    var trappedPlayers = type.GetField("trappedPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(trap) as IEnumerable;
                    var alreadyTriggered = trappedPlayers?.Cast<object>().OfType<PlayerControl>().Any(player => player && player.PlayerId == bot.PlayerId) == true;
                    if (revealed || !triggerable || !gameObject || alreadyTriggered ||
                        Vector2.Distance(bot.GetTruePosition(), gameObject!.transform.position) > triggerDistance)
                    {
                        continue;
                    }

                    var instanceId = Convert.ToByte(type.GetField("instanceId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(trap) ?? 0);
                    SendRpc(bot, 163, writer =>
                    {
                        writer.Write(bot.PlayerId);
                        writer.Write(instanceId);
                    });
                    InvokeProcedure("triggerTrap", bot.PlayerId, instanceId);
                    _log?.LogInfo($"DeepBot TOR trap triggered: bot={Describe(bot)}, trap={instanceId}, duration={GetStaticFloat("Trapper", "trapDuration"):0.0}s.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"DeepBot TOR virtual trap scan failed safely: {ex.GetBaseException().Message}");
        }
    }

    internal static bool TryUseAbility(
        PlayerControl bot,
        TorRoleInfo role,
        byte? requestedTargetId,
        out string outcome)
    {
        outcome = string.Empty;
        var abilityReady = IsAbilityReady(bot, role);
        var hasSequence = TryGetAbilitySequencePlan(bot, role, out var sequence);
        var sequenceRoleCoolingDown = hasSequence &&
                                      string.Equals(sequence.AbilityAction, "role", StringComparison.Ordinal) &&
                                      !IsConfiguredRoleStageReady(bot, role.Name);
        if (!BotBehaviorPolicy.CanContinueAbilityThroughReadinessGate(
                abilityReady,
                hasSequence && sequence.Active,
                hasSequence && sequence.ShouldUse,
                hasSequence ? sequence.AbilityAction : null) ||
            sequenceRoleCoolingDown ||
            !AmongUsClient.Instance)
        {
            outcome = "TOR ability is unavailable or cooling down";
            return false;
        }

        var target = requestedTargetId.HasValue ? FindPlayer(requestedTargetId.Value) : null;
        var ninjaMarked = role.Name == "Ninja"
            ? GetStaticField("Ninja", "ninjaMarked") as PlayerControl
            : null;
        var morphSampled = role.Name == "Morphling"
            ? GetStaticField("Morphling", "sampledTarget") as PlayerControl
            : null;
        if (ninjaMarked)
        {
            target = ninjaMarked;
        }
        else if (morphSampled)
        {
            target = morphSampled;
        }

        var targetIsLegal = role.Name == "Warlock" && PendingWarlockCurses.TryGetValue(bot.PlayerId, out var pendingCurse)
            ? IsLegalWarlockSecondTarget(bot, pendingCurse.VictimId, target)
            : role.Name == "Morphling" && morphSampled
                ? IsSequenceTargetAvailable(morphSampled, allowVentConcealment: true) &&
                  !AreLoverPartners(bot, morphSampled) &&
                  (!role.IsImpostorTeam || !IsImpostorTeam(morphSampled))
            : role.Name == "Ninja" && ninjaMarked
                ? IsLivingOpponent(bot, target, role)
                : IsLegalNearbyTarget(bot, target, role);
        if (NeedsLivingTarget(role.Name) && !targetIsLegal)
        {
            outcome = "no legal nearby target";
            return false;
        }

        try
        {
            switch (role.Name)
            {
                case "Engineer":
                    return TryUseEngineerRepair(bot, out outcome);
                case "Mayor":
                    return TryUseMayorMeeting(bot, out outcome);
                case "Portalmaker":
                    return TryPlacePortal(bot, out outcome);
                case "Medic":
                {
                    var afterMeeting = GetStaticBool("Medic", "setShieldAfterMeeting");
                    var rpc = afterMeeting ? (byte)142 : (byte)124;
                    SendRpc(bot, rpc, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure(afterMeeting ? "setFutureShielded" : "medicSetShielded", target!.PlayerId);
                    SetStaticField("Medic", "meetingAfterShielding", false);
                    outcome = $"shielded {Describe(target)}";
                    return true;
                }
                case "Sheriff":
                {
                    if (TryCheckRuleAwareMurder(bot, target!, out var sheriffCheck) &&
                        !string.Equals(sheriffCheck, "PerformKill", StringComparison.Ordinal))
                    {
                        outcome = $"Sheriff shot was blocked by TOR rules ({sheriffCheck})";
                        return true;
                    }

                    var actualVictim = IsSheriffKillLegal(target!) ? target! : bot;
                    SendRpc(bot, 108, writer =>
                    {
                        writer.Write(bot.PlayerId);
                        writer.Write(actualVictim.PlayerId);
                        writer.Write(byte.MaxValue);
                    });
                    InvokeProcedure("uncheckedMurderPlayer", bot.PlayerId, actualVictim.PlayerId, byte.MaxValue);
                    outcome = actualVictim.PlayerId == bot.PlayerId
                        ? $"misfired while attempting to shoot {Describe(target)}"
                        : $"shot hostile target {Describe(target)}";
                    return true;
                }
                case "Deputy":
                    SendRpc(bot, 135, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure("deputyUsedHandcuffs", target!.PlayerId);
                    NextRoleAbilityAt[(bot.PlayerId, role.Name)] =
                        Time.time + Mathf.Max(0.05f, GetStaticFloat("Deputy", "handcuffCooldown"));
                    outcome = $"handcuffed suspicious nearby player {Describe(target)}";
                    return true;
                case "Tracker":
                    SendRpc(bot, 132, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure("trackerUsedTracker", target!.PlayerId);
                    outcome = $"tracked {Describe(target)}";
                    return true;
                case "TimeMaster":
                    SendRpc(bot, 126);
                    InvokeProcedure("timeMasterShield");
                    outcome = "raised the time shield near danger";
                    return true;
                case "Morphling":
                    return TryUseMorphling(bot, target!, out outcome);
                case "Camouflager":
                    SendRpc(bot, 131);
                    InvokeProcedure("camouflagerCamouflage");
                    outcome = "activated camouflage for a planned concealment";
                    return true;
                case "Hacker":
                    return TryUseHacker(bot, out outcome);
                case "Medium":
                    return TryUseMedium(bot, out outcome);
                case "Vampire":
                    return TryUseVampireBite(bot, target!, out outcome);
                case "Warlock":
                    return TryUseWarlockCurse(bot, target!, out outcome);
                case "Ninja":
                    return TryUseNinja(bot, target!, out outcome);
                case "Jackal":
                    if (GetStaticBool("Jackal", "canCreateSidekick"))
                    {
                        SendRpc(bot, 137, writer => writer.Write(target!.PlayerId));
                        InvokeProcedure("jackalCreatesSidekick", target!.PlayerId);
                        outcome = $"recruited {Describe(target)} as sidekick";
                    }
                    else
                    {
                        if (!TryExecuteRuleAwareMurder(bot, target!, out var killed, out var killOutcome))
                        {
                            outcome = "Jackal attack safely cancelled because TOR's rule validator was unavailable";
                            return false;
                        }
                        NextRoleAbilityAt[(bot.PlayerId, role.Name)] = Time.time + Mathf.Max(0.05f, GetStaticFloat("Jackal", "cooldown"));
                        outcome = killed
                            ? $"Jackal eliminated isolated opponent {Describe(target)} ({killOutcome})"
                            : $"Jackal attack on {Describe(target)} was blocked ({killOutcome})";
                    }
                    return true;
                case "Sidekick":
                    if (!TryExecuteRuleAwareMurder(bot, target!, out var sidekickKilled, out var sidekickOutcome))
                    {
                        outcome = "Sidekick attack safely cancelled because TOR's rule validator was unavailable";
                        return false;
                    }
                    NextRoleAbilityAt[(bot.PlayerId, role.Name)] = Time.time + Mathf.Max(0.05f, GetStaticFloat("Sidekick", "cooldown"));
                    outcome = sidekickKilled
                        ? $"Sidekick eliminated isolated opponent {Describe(target)} ({sidekickOutcome})"
                        : $"Sidekick attack on {Describe(target)} was blocked ({sidekickOutcome})";
                    return true;
                case "Arsonist":
                    if (DousedEveryoneAlive(bot))
                    {
                        SendRpc(bot, 151);
                        InvokeProcedure("arsonistWin");
                        outcome = "ignited after every other living player was doused";
                        return true;
                    }
                    if (target is null || IsDoused(target.PlayerId))
                    {
                        outcome = "target is missing or already doused";
                        return false;
                    }
                    PendingDouses[bot.PlayerId] = new PendingDouse(
                        target.PlayerId,
                        Time.time + Mathf.Max(0.05f, GetStaticFloat("Arsonist", "duration")));
                    outcome = $"began channeling douse on {Describe(target)}";
                    return true;
                case "Pursuer":
                    SendRpc(bot, 155, writer =>
                    {
                        writer.Write(target!.PlayerId);
                        writer.Write(byte.MaxValue);
                    });
                    InvokeProcedure("setBlanked", target!.PlayerId, byte.MaxValue);
                    SetStaticField("Pursuer", "blanks", GetStaticInt("Pursuer", "blanks") + 1);
                    NextRoleAbilityAt[(bot.PlayerId, role.Name)] = Time.time + Mathf.Max(0.05f, GetStaticFloat("Pursuer", "cooldown"));
                    outcome = $"blanked {Describe(target)} to protect survival";
                    return true;
                case "Thief":
                    return TryThiefSteal(bot, target!, out outcome);
                case "Eraser":
                    SendRpc(bot, 140, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure("setFutureErased", target!.PlayerId);
                    outcome = $"scheduled erasure of {Describe(target)}";
                    return true;
                case "Witch":
                    return TryStartWitchSpell(bot, target!, out outcome);
                case "Shifter":
                    SendRpc(bot, 141, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure("setFutureShifted", target!.PlayerId);
                    outcome = $"scheduled a shift with {Describe(target)}";
                    return true;
                case "Cleaner":
                case "Janitor":
                case "Vulture":
                    return TryConsumeNearbyBody(bot, role.Name, out outcome);
                case "Trapper":
                    return TryPlaceTrap(bot, out outcome);
                case "Trickster":
                    return TryUseTrickster(bot, out outcome);
                case "SecurityGuard":
                    return TryUseSecurityGuard(bot, out outcome);
                case "Bomber":
                    return TryPlantBomb(bot, out outcome);
                case "Yoyo":
                    return TryUseYoyo(bot, out outcome);
                default:
                    outcome = "role is recognized but its active ability is not enabled yet";
                    return false;
            }
        }
        catch (Exception ex)
        {
            outcome = $"TOR bridge error: {ex.GetBaseException().Message}";
            _log?.LogWarning($"DeepBot TOR ability bridge failed: bot={Describe(bot)}, role={role.Name}, error={ex}");
            return false;
        }
    }

    private static bool TryStartWitchSpell(PlayerControl bot, PlayerControl target, out string outcome)
    {
        var duration = Mathf.Max(0.05f, GetStaticFloat("Witch", "spellCastingDuration"));
        SetStaticField("Witch", "currentTarget", target);
        SetStaticField("Witch", "spellCastingTarget", target);
        PendingWitchSpells[bot.PlayerId] = new PendingWitchSpell(target.PlayerId, Time.time + duration);
        StopRoleChannelMovement(bot);
        outcome = $"began {duration:0.0}s spell channel on {Describe(target)}";
        _log?.LogInfo(
            $"DeepBot Witch spell channel started: bot={Describe(bot)}, target={Describe(target)}, " +
            $"duration={duration:0.00}s, range={GetAbilityUseRange("Witch"):0.00}.");
        return true;
    }

    private static void CompletePendingWitchSpell(PlayerControl bot, PlayerControl target, TorRoleInfo role)
    {
        PendingWitchSpells.Remove(bot.PlayerId);
        var handled = TryCheckRuleAwareMurder(bot, target, out var result);
        var performsSpell = handled && string.Equals(result, "PerformKill", StringComparison.Ordinal);
        var consumesCooldown = performsSpell || handled && string.Equals(result, "BlankKill", StringComparison.Ordinal);

        if (performsSpell)
        {
            SendRpc(bot, 143, writer => writer.Write(target.PlayerId));
            InvokeProcedure("setFutureSpelled", target.PlayerId);
        }

        if (consumesCooldown)
        {
            var priorAddition = GetStaticFloat("Witch", "currentCooldownAddition");
            var configuredAddition = GetStaticFloat("Witch", "cooldownAddition");
            var nextAddition = Mathf.Max(0f, priorAddition + Mathf.Max(0f, configuredAddition));
            SetStaticField("Witch", "currentCooldownAddition", nextAddition);
            NextRoleAbilityAt[(bot.PlayerId, role.Name)] =
                Time.time + ResolveWitchAbilityCooldown(GetStaticFloat("Witch", "cooldown"), priorAddition, configuredAddition);

            if (GetStaticBool("Witch", "triggerBothCooldowns"))
            {
                var mini = GetStaticField("Mini", "mini") as PlayerControl;
                var isMini = mini && mini!.PlayerId == bot.PlayerId;
                bot.killTimer = Mathf.Max(
                    bot.killTimer,
                    ResolveWitchKillCooldown(
                        GameRuleSettings.GetKillCooldown(bot.killTimer),
                        isMini,
                        isMini && InvokeStaticBool("Mini", "isGrownUp")));
            }
        }
        else
        {
            NextRoleAbilityAt[(bot.PlayerId, role.Name)] = Time.time + 0.35f;
        }

        ClearWitchCastingFields();
        _log?.LogInfo(
            $"DeepBot Witch spell channel resolved: bot={Describe(bot)}, target={Describe(target)}, " +
            $"handled={handled}, result={result}, spelled={performsSpell}, cooldownConsumed={consumesCooldown}.");
    }

    private static void CancelPendingWitchSpell(byte botId, PlayerControl? bot, PlayerControl? target, string reason)
    {
        PendingWitchSpells.Remove(botId);
        NextRoleAbilityAt[(botId, "Witch")] = Time.time + 0.35f;
        ClearWitchCastingFields();
        if (bot)
        {
            StopRoleChannelMovement(bot!);
        }
        _log?.LogInfo(
            $"DeepBot Witch spell channel interrupted: bot={Describe(bot)}, target={Describe(target)}, reason={reason}.");
    }

    private static void ClearWitchCastingFields()
    {
        SetStaticField("Witch", "spellCastingTarget", null!);
        SetStaticField("Witch", "currentTarget", null!);
    }

    private static void StopRoleChannelMovement(PlayerControl bot)
    {
        if (!bot.MyPhysics)
        {
            return;
        }

        bot.MyPhysics.SetNormalizedVelocity(Vector2.zero);
        if (bot.MyPhysics.body)
        {
            bot.MyPhysics.body.velocity = Vector2.zero;
        }
    }

    private static bool TryUseEngineerRepair(PlayerControl bot, out string outcome)
    {
        var repairable = bot.myTasks?.ToArray()
            .FirstOrDefault(task => task && !task.IsComplete && IsRepairableEmergency(task.TaskType));
        if (!repairable || !ShipStatus.Instance)
        {
            outcome = "no active repairable emergency";
            return false;
        }
        var repairTaskType = repairable!.TaskType;

        try
        {
            var assembly = _assembly;
            if (assembly is null)
            {
                outcome = "TOR assembly is unavailable";
                return false;
            }

            var type = assembly.GetType(TorEngineerVentRulesTypeName, false);
            var method = type?.GetMethod(
                "TryRepairEmergency",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(PlayerControl), typeof(int), typeof(string).MakeByRefType() },
                null);
            if (method is null)
            {
                outcome = "TOR native Engineer repair bridge is unavailable";
                return false;
            }

            var args = new object[] { bot, (int)repairTaskType, string.Empty };
            var repaired = method.Invoke(null, args) is true;
            outcome = args[2] as string ?? (repaired
                ? $"TOR native Engineer repaired {repairTaskType}"
                : $"TOR native Engineer declined {repairTaskType}");
            return repaired;
        }
        catch (Exception ex)
        {
            outcome = $"TOR native Engineer repair failed: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryPlacePortal(PlayerControl bot, out string outcome)
    {
        if (GetStaticField("Portal", "secondPortal") is not null)
        {
            outcome = "both room-configured portals are already placed";
            return false;
        }

        var position = bot.GetTruePosition();
        var placements = GetStrategicPlacements(bot, "Portalmaker", reset: GetStaticField("Portal", "firstPortal") is null);
        if (placements.Count > 0 && Vector2.Distance(placements[^1], position) < 6f)
        {
            outcome = "second portal must be placed in a meaningfully separated room";
            return false;
        }

        var buffer = BuildPositionBuffer(position);
        SendRpc(bot, 145, writer => writer.WriteBytesAndSize(buffer));
        InvokeProcedure("placePortal", buffer);
        placements.Add(position);
        NextRoleAbilityAt[(bot.PlayerId, "Portalmaker")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Portalmaker", "cooldown"));
        outcome = $"placed portal {placements.Count}/2 at {SkeldPathGraph.Instance.NearestNode(position).Id}";
        return true;
    }

    private static bool TryUseMorphling(PlayerControl bot, PlayerControl target, out string outcome)
    {
        var sampled = GetStaticField("Morphling", "sampledTarget") as PlayerControl;
        if (!sampled)
        {
            SetStaticField("Morphling", "sampledTarget", target);
            NextRoleAbilityAt[(bot.PlayerId, "Morphling")] = Time.time + 1.05f;
            outcome = $"sampled {Describe(target)}; first stage complete, now disengage and transform from concealment";
            return true;
        }

        SendRpc(bot, 130, writer => writer.Write(sampled!.PlayerId));
        InvokeProcedure("morphlingMorph", sampled!.PlayerId);
        SetStaticField("Morphling", "sampledTarget", null!);
        NextRoleAbilityAt[(bot.PlayerId, "Morphling")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Morphling", "cooldown"));
        outcome = $"completed second stage and morphed into sampled player {Describe(sampled)}";
        return true;
    }

    private static bool TryUseTrickster(PlayerControl bot, out string outcome)
    {
        var converted = GetStaticBool("JackInTheBox", "boxesConvertedToVents");
        var boxes = GetStaticCollectionCount("JackInTheBox", "AllJackInTheBoxes");
        var limit = Mathf.Max(1, GetStaticInt("JackInTheBox", "JackInTheBoxLimit"));
        if (converted && boxes >= limit)
        {
            SendRpc(bot, 148);
            InvokeProcedure("lightsOut");
            NextRoleAbilityAt[(bot.PlayerId, "Trickster")] =
                Time.time + Mathf.Max(0.05f, GetStaticFloat("Trickster", "lightsOutCooldown"));
            outcome = "activated box-network darkness for a deliberate hostile play";
            return true;
        }

        if (boxes >= limit)
        {
            outcome = "box network is complete and will convert after the next meeting";
            return false;
        }

        var position = bot.GetTruePosition();
        var placements = GetStrategicPlacements(bot, "Trickster", reset: boxes == 0);
        if (placements.Any(previous => Vector2.Distance(previous, position) < 4f))
        {
            outcome = "move to a different room before placing the next box";
            return false;
        }

        var buffer = BuildPositionBuffer(position);
        SendRpc(bot, 147, writer => writer.WriteBytesAndSize(buffer));
        InvokeProcedure("placeJackInTheBox", buffer);
        placements.Add(position);
        NextRoleAbilityAt[(bot.PlayerId, "Trickster")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Trickster", "placeBoxCooldown"));
        outcome = $"placed Jack-in-the-box {boxes + 1}/{limit} at {SkeldPathGraph.Instance.NearestNode(position).Id}";
        return true;
    }

    private static bool TryUseSecurityGuard(PlayerControl bot, out string outcome)
    {
        var remaining = GetStaticInt("SecurityGuard", "remainingScrews");
        var ventPrice = Mathf.Max(1, GetStaticInt("SecurityGuard", "ventPrice"));
        var camPrice = Mathf.Max(1, GetStaticInt("SecurityGuard", "camPrice"));
        var nearbyVent = FindNearestUnsealedVent(bot, 1.75f);
        var mustSealVent = GameRuleSettings.IsMiraHqMap();
        var shouldSealVent = nearbyVent &&
                             remaining >= ventPrice &&
                             (mustSealVent || remaining < camPrice || GetStaticInt("SecurityGuard", "placedCameras") > 0);
        if (shouldSealVent)
        {
            SendRpc(bot, 150, writer => writer.WritePacked(nearbyVent!.Id));
            InvokeProcedure("sealVent", nearbyVent!.Id);
            SetStaticField("SecurityGuard", "ventTarget", null!);
            NextRoleAbilityAt[(bot.PlayerId, "SecurityGuard")] =
                Time.time + Mathf.Max(0.05f, GetStaticFloat("SecurityGuard", "cooldown"));
            outcome = $"scheduled vent {nearbyVent.Id} for sealing using the room-configured screw cost {ventPrice}";
            return true;
        }

        if (mustSealVent)
        {
            outcome = nearbyVent
                ? "not enough room-configured screws to seal the nearby MIRA vent"
                : "MIRA Security Guard must approach an unsealed vent; portable cameras are not legal on this map";
            return false;
        }

        return TryPlaceSecurityCamera(bot, out outcome);
    }

    private static bool TryPlaceSecurityCamera(PlayerControl bot, out string outcome)
    {
        var remaining = GetStaticInt("SecurityGuard", "remainingScrews");
        var price = Mathf.Max(1, GetStaticInt("SecurityGuard", "camPrice"));
        if (remaining < price)
        {
            outcome = "not enough room-configured screws for a camera";
            return false;
        }

        var position = bot.GetTruePosition();
        var placements = GetStrategicPlacements(bot, "SecurityGuard", reset: remaining == GetStaticInt("SecurityGuard", "totalScrews"));
        if (placements.Any(previous => Vector2.Distance(previous, position) < 5f))
        {
            outcome = "an information device is already too close to this location";
            return false;
        }

        var buffer = BuildPositionBuffer(position);
        SendRpc(bot, 149, writer => writer.WriteBytesAndSize(buffer));
        InvokeProcedure("placeCamera", buffer);
        placements.Add(position);
        NextRoleAbilityAt[(bot.PlayerId, "SecurityGuard")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("SecurityGuard", "cooldown"));
        outcome = $"placed a camera at informative chokepoint {SkeldPathGraph.Instance.NearestNode(position).Id}; screws={remaining - price}";
        return true;
    }

    private static bool TryUseVampireBite(PlayerControl bot, PlayerControl target, out string outcome)
    {
        if (GetStaticField("Vampire", "bitten") is not null || PendingVampireBites.ContainsKey(bot.PlayerId))
        {
            outcome = "a previous bite is still pending";
            return false;
        }

        if (TryCheckRuleAwareMurder(bot, target, out var check) &&
            !string.Equals(check, "PerformKill", StringComparison.Ordinal))
        {
            outcome = $"bite was blocked by TOR rules ({check})";
            return true;
        }

        if (IsNearGarlic(target))
        {
            if (!GetStaticBool("Vampire", "canKillNearGarlics"))
            {
                outcome = "bite was blocked because the target is protected by garlic under the room rules";
                return true;
            }

            if (!TryExecuteRuleAwareMurder(bot, target, out var killedNearGarlic, out var garlicResult))
            {
                outcome = "TOR murder validator unavailable; garlic-proximity attack was safely cancelled";
                return false;
            }

            outcome = killedNearGarlic
                ? $"directly killed {Describe(target)} near garlic as required by TOR rules ({garlicResult})"
                : $"garlic-proximity attack on {Describe(target)} was blocked ({garlicResult})";
            return true;
        }

        SendRpc(bot, 133, writer =>
        {
            writer.Write(target.PlayerId);
            writer.Write((byte)0);
        });
        InvokeProcedure("vampireSetBitten", target.PlayerId, (byte)0);
        PendingVampireBites[bot.PlayerId] = new PendingVampireBite(
            target.PlayerId,
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Vampire", "delay")),
            bot.GetTruePosition());
        outcome = $"bit isolated target {Describe(target)}; delayed kill will resolve after the room-configured delay";
        return true;
    }

    private static bool IsNearGarlic(PlayerControl target)
    {
        var garlicType = _assembly?.GetType("TheOtherRoles.Objects.Garlic");
        var garlics = garlicType?.GetField("garlics", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IEnumerable;
        if (garlics is null)
        {
            return false;
        }

        foreach (var garlic in garlics)
        {
            var gameObject = garlic?.GetType()
                .GetField("garlic", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(garlic) as GameObject;
            if (gameObject && Vector2.Distance(gameObject!.transform.position, target.GetTruePosition()) <= 1.91f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryUseWarlockCurse(PlayerControl bot, PlayerControl target, out string outcome)
    {
        if (PendingWarlockCurses.TryGetValue(bot.PlayerId, out var pending))
        {
            if (!IsLegalWarlockSecondTarget(bot, pending.VictimId, target))
            {
                outcome = "curse carrier has no legal second target in TOR kill range";
                return false;
            }

            var handled = TryExecuteRuleAwareMurder(bot, target, out var killed, out var result, showAnimation: false);
            if (!handled)
            {
                outcome = "TOR murder validator unavailable; forced-kill stage safely held";
                return false;
            }

            if (result.Contains("result=SuppressKill", StringComparison.Ordinal))
            {
                outcome = $"forced-kill stage was suppressed by TOR and remains armed ({result})";
                return true;
            }

            var rootSeconds = Mathf.Max(0f, GetStaticFloat("Warlock", "rootTime"));
            ClearWarlockCurse(bot.PlayerId, $"second stage target={Describe(target)}, killed={killed}, result={result}");
            bot.killTimer = Mathf.Max(bot.killTimer, Mathf.Max(0.05f, GetStaticFloat("Warlock", "cooldown")));
            if (rootSeconds > 0f)
            {
                PendingRoleRoots[bot.PlayerId] = Time.time + rootSeconds;
                bot.moveable = false;
                StopRoleChannelMovement(bot);
            }
            outcome = $"completed curse against {Describe(target)}; killed={killed}; rooted={rootSeconds:0.0}s ({result})";
            return true;
        }

        SetStaticField("Warlock", "curseVictim", target);
        SetStaticField("Warlock", "curseVictimTarget", null!);
        PendingWarlockCurses[bot.PlayerId] = new PendingWarlockCurse(target.PlayerId);
        outcome = $"cursed mobile carrier {Describe(target)} and will redirect a kill only if that carrier approaches a legal opponent";
        return true;
    }

    private static PlayerControl? FindWarlockSecondTarget(PlayerControl bot, byte victimId)
    {
        var victim = FindPlayer(victimId);
        if (!victim || victim!.Data is null || victim.Data.IsDead || victim.Data.Disconnected ||
            BotPerceptionPolicy.IsConcealedByVent(victim))
        {
            return null;
        }

        return PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player => IsLegalWarlockSecondTarget(bot, victimId, player))
            .OrderBy(player => Vector2.Distance(victim.GetTruePosition(), player.GetTruePosition()))
            .ThenBy(player => player.PlayerId)
            .FirstOrDefault();
    }

    private static bool IsLegalWarlockSecondTarget(PlayerControl bot, byte victimId, PlayerControl? target)
    {
        var victim = FindPlayer(victimId);
        if (!victim || !target ||
            target!.PlayerId == victimId ||
            target.PlayerId == bot.PlayerId ||
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            BotPerceptionPolicy.IsConcealedByVent(target) ||
            IsImpostorTeam(target) ||
            AreLoverPartners(bot, target))
        {
            return false;
        }

        var carrierPosition = victim!.GetTruePosition();
        return Vector2.Distance(carrierPosition, target.GetTruePosition()) <= GameRuleSettings.GetKillDistance(1.8f) &&
               !PhysicsHelpers.AnythingBetween(
                   carrierPosition,
                   target.GetTruePosition(),
                   Constants.ShipOnlyMask,
                   false);
    }

    private static void ClearWarlockCurse(byte botId, string reason)
    {
        PendingWarlockCurses.Remove(botId);
        SetStaticField("Warlock", "curseVictim", null!);
        SetStaticField("Warlock", "curseVictimTarget", null!);
        NextRoleAbilityAt[(botId, "Warlock")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Warlock", "cooldown"));
        _log?.LogInfo($"DeepBot Warlock curse resolved: bot={Describe(FindPlayer(botId))}, {reason}.");
    }

    private static bool TryUseNinja(PlayerControl bot, PlayerControl target, out string outcome)
    {
        var marked = GetStaticField("Ninja", "ninjaMarked") as PlayerControl;
        if (!marked)
        {
            SetStaticField("Ninja", "ninjaMarked", target);
            NextRoleAbilityAt[(bot.PlayerId, "Ninja")] = Time.time + 5f;
            outcome = $"marked {Describe(target)} for a later concealed strike";
            return true;
        }

        if (TryCheckRuleAwareMurder(bot, marked!, out var check) &&
            !string.Equals(check, "PerformKill", StringComparison.Ordinal))
        {
            SetStaticField("Ninja", "ninjaMarked", null!);
            NextRoleAbilityAt[(bot.PlayerId, "Ninja")] = Time.time + 5f;
            outcome = $"marked strike was blocked by TOR rules ({check})";
            return true;
        }

        var originBuffer = BuildPositionBuffer(bot.GetTruePosition());
        SendRpc(bot, 144, writer => writer.WriteBytesAndSize(originBuffer));
        InvokeProcedure("placeNinjaTrace", originBuffer);

        SendRpc(bot, 160, writer =>
        {
            writer.Write(bot.PlayerId);
            writer.Write(byte.MinValue);
        });
        InvokeProcedure("setInvisible", bot.PlayerId, byte.MinValue);

        PerformUncheckedMurder(bot, marked!);

        var destinationBuffer = BuildPositionBuffer(marked!.GetTruePosition());
        SendRpc(bot, 144, writer => writer.WriteBytesAndSize(destinationBuffer));
        InvokeProcedure("placeNinjaTrace", destinationBuffer);

        PendingNinjaReveals[bot.PlayerId] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Ninja", "invisibleDuration"));
        SetStaticField("Ninja", "ninjaMarked", null!);
        NextRoleAbilityAt[(bot.PlayerId, "Ninja")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Ninja", "cooldown"));
        outcome = $"assassinated marked target {Describe(marked)} with TOR traces and room-configured invisibility";
        return true;
    }

    private static bool TryPlantBomb(PlayerControl bot, out string outcome)
    {
        if (GetStaticBool("Bomber", "isPlanted"))
        {
            outcome = "the previous bomb is still active";
            return false;
        }

        if (!TryCheckRuleAwareMurder(bot, bot, out var placementCheck, ignoreMedic: true))
        {
            outcome = "bomb placement safely cancelled because TOR's native placement precheck was unavailable";
            return false;
        }

        if (string.Equals(placementCheck, "BlankKill", StringComparison.Ordinal))
        {
            // TOR consumes the blank instead of creating a bomb. Keep the
            // world free of a fake planted state so the next legal cooldown
            // cycle can use the native button path again.
            SetStaticField("Bomber", "isPlanted", false);
            NextRoleAbilityAt[(bot.PlayerId, "Bomber")] =
                Time.time + Mathf.Max(0.05f, GetStaticFloat("Bomber", "bombCooldown"));
            outcome = "TOR blocked bomb placement with BlankKill; no bomb object was created";
            return true;
        }

        var position = bot.GetTruePosition();
        var blastRadius = Mathf.Max(0.1f, GetStaticFloat("Bomber", "destructionRange"));
        var endangeredLover = PlayerControl.AllPlayerControls
            .ToArray()
            .FirstOrDefault(player =>
                player &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                AreLoverPartners(bot, player) &&
                Vector2.Distance(position, player.GetTruePosition()) <= blastRadius + 0.75f);
        if (endangeredLover)
        {
            outcome = $"bomb placement cancelled because known Lover partner {Describe(endangeredLover)} is inside the predicted blast area";
            return false;
        }

        var buffer = BuildPositionBuffer(position);
        SendRpc(bot, 165, writer => writer.WriteBytesAndSize(buffer));
        InvokeProcedure("placeBomb", buffer);
        SetStaticField("Bomber", "isPlanted", true);
        NextRoleAbilityAt[(bot.PlayerId, "Bomber")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Bomber", "bombCooldown"));
        outcome =
            $"planted a room-configured bomb at {SkeldPathGraph.Instance.NearestNode(position).Id} " +
            $"for a deliberate split or elimination; armsAfter={GetStaticFloat("Bomber", "bombActiveAfter"):0.0}s, " +
            $"explodesAfterArmed={GetStaticFloat("Bomber", "destructionTime"):0.0}s, radius={blastRadius:0.0}";
        return true;
    }

    private static bool TryUseHacker(PlayerControl bot, out string outcome)
    {
        var players = PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player => player && player.Data is not null && !player.Data.Disconnected)
            .ToArray();
        var dead = players.Where(player => player.Data!.IsDead).ToArray();
        var vitalsCharges = GetStaticInt("Hacker", "chargesVitals");
        var adminCharges = GetStaticInt("Hacker", "chargesAdminTable");

        if (dead.Length > 0 && vitalsCharges > 0)
        {
            SetStaticField("Hacker", "chargesVitals", vitalsCharges - 1);
            SetStaticField("Hacker", "hackerTimer", Mathf.Max(0.05f, GetStaticFloat("Hacker", "duration")));
            outcome = "used one TOR vitals charge; status=" + string.Join(", ", players.Select(player =>
                $"{Describe(player)}:{(player.Data!.IsDead ? "dead" : "alive")}"));
            return true;
        }

        if (adminCharges > 0)
        {
            SetStaticField("Hacker", "chargesAdminTable", adminCharges - 1);
            var occupancy = players
                .Where(player => !player.Data!.IsDead)
                .GroupBy(player => SkeldPathGraph.Instance.NearestNode(player.GetTruePosition()).Id)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}");
            outcome = "used one TOR admin charge; anonymous occupancy by nearest room node=" + string.Join(", ", occupancy);
            return true;
        }

        outcome = "no Hacker information charge remains";
        return false;
    }

    private static bool TryUseMayorMeeting(PlayerControl bot, out string outcome)
    {
        if (MeetingHud.Instance || ExileController.Instance || HasRepairableEmergency(bot) || !bot.moveable)
        {
            outcome = "remote meeting blocked by the current TOR/game phase";
            return false;
        }

        var remaining = GetStaticInt("Mayor", "remoteMeetingsLeft");
        if (!GetStaticBool("Mayor", "meetingButton") || remaining <= 0)
        {
            outcome = "no room-configured Mayor remote meeting remains";
            return false;
        }

        InvokeHelper("handleVampireBiteOnBodyReport");
        InvokeProcedure("uncheckedCmdReportDeadBody", bot.PlayerId, byte.MaxValue);
        SendRpc(bot, 109, writer =>
        {
            writer.Write(bot.PlayerId);
            writer.Write(byte.MaxValue);
        });
        SetStaticField("Mayor", "remoteMeetingsLeft", remaining - 1);
        outcome = "called a TOR remote meeting after evidence crossed the Mayor threshold";
        return true;
    }

    private static bool TryUseMedium(PlayerControl bot, out string outcome)
    {
        if (!TryFindMediumSoul(bot, true, out var tuple, out var deadPlayer, out _))
        {
            outcome = "no soul is within TOR's configured interaction range";
            return false;
        }

        var deadType = deadPlayer!.GetType();
        var player = deadType.GetField("player", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(deadPlayer) as PlayerControl;
        var killer = deadType.GetField("killerIfExisting", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(deadPlayer) as PlayerControl;
        if (!player)
        {
            outcome = "the selected soul no longer has a valid player record";
            return false;
        }

        SetStaticField("Medium", "target", deadPlayer);
        SetStaticField("Medium", "soulTarget", deadPlayer);
        var mediumType = GetTorType("Medium");
        var getInfo = mediumType?.GetMethod("getInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var info = getInfo?.Invoke(null, new object?[] { player, killer }) as string;
        if (string.IsNullOrWhiteSpace(info))
        {
            info = $"Soul information for {Describe(player)} was inconclusive.";
        }

        if (GetStaticBool("Medium", "oneTimeUse") && GetStaticField("Medium", "deadBodies") is IList souls)
        {
            souls.Remove(tuple);
        }
        SetStaticField("Medium", "soulTarget", null!);
        outcome = $"questioned {Describe(player)}'s soul; answer={info}";
        return true;
    }

    private static bool TryFindMediumSoul(
        PlayerControl bot,
        bool requireNearby,
        out object? tuple,
        out object? deadPlayer,
        out Vector2 position)
    {
        tuple = null;
        deadPlayer = null;
        position = default;
        if (!bot || GetStaticField("Medium", "deadBodies") is not IEnumerable souls)
        {
            return false;
        }

        var usableDistance = UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(vent => vent)
            .Select(vent => vent.UsableDistance)
            .DefaultIfEmpty(1.5f)
            .First();
        var botPosition = bot.GetTruePosition();
        var bestDistance = float.MaxValue;
        foreach (var candidate in souls)
        {
            if (candidate is null)
            {
                continue;
            }

            var candidateType = candidate.GetType();
            var dp = candidateType.GetProperty("Item1")?.GetValue(candidate);
            var rawPosition = candidateType.GetProperty("Item2")?.GetValue(candidate);
            if (dp is null || rawPosition is not Vector3 soulPosition)
            {
                continue;
            }

            var distance = Vector2.Distance(botPosition, soulPosition);
            if ((requireNearby && distance > usableDistance) || distance >= bestDistance)
            {
                continue;
            }

            tuple = candidate;
            deadPlayer = dp;
            position = soulPosition;
            bestDistance = distance;
        }

        return tuple is not null;
    }

    private static bool TryUseYoyo(PlayerControl bot, out string outcome)
    {
        var position = bot.GetTruePosition();
        var buffer = BuildPositionBuffer(position);
        if (GetStaticField("Yoyo", "markedLocation") is null)
        {
            SendRpc(bot, 168, writer => writer.WriteBytesAndSize(buffer));
            InvokeProcedure("yoyoMarkLocation", buffer);
            NextRoleAbilityAt[(bot.PlayerId, "Yoyo")] = Time.time + 10f;
            outcome = $"marked a return location at {SkeldPathGraph.Instance.NearestNode(position).Id}";
            return true;
        }

        SendRpc(bot, 169, writer =>
        {
            writer.Write(byte.MaxValue);
            writer.WriteBytesAndSize(buffer);
        });
        InvokeProcedure("yoyoBlink", true, buffer);
        PendingYoyoReturns[bot.PlayerId] = Time.time + Mathf.Max(0.05f, GetStaticFloat("Yoyo", "blinkDuration"));
        outcome = "blinked to the marked location and scheduled a room-configured timed return";
        return true;
    }

    private static byte[] BuildPositionBuffer(Vector2 position)
    {
        var buffer = new byte[sizeof(float) * 2];
        Buffer.BlockCopy(BitConverter.GetBytes(position.x), 0, buffer, 0, sizeof(float));
        Buffer.BlockCopy(BitConverter.GetBytes(position.y), 0, buffer, sizeof(float), sizeof(float));
        return buffer;
    }

    private static Vent? FindNearestUnsealedVent(PlayerControl bot, float maximumDistance)
    {
        if (!bot)
        {
            return null;
        }

        var position = bot.GetTruePosition();
        return UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(vent => vent && !IsVentScheduledOrSealed(vent))
            .Select(vent => new
            {
                Vent = vent,
                Distance = Vector2.Distance(position, vent.transform.position)
            })
            .Where(item => item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Vent.Id)
            .Select(item => item.Vent)
            .FirstOrDefault();
    }

    private static bool IsVentScheduledOrSealed(Vent vent)
    {
        if (!vent)
        {
            return true;
        }

        var name = vent!.name ?? string.Empty;
        if (name.StartsWith("JackInTheBoxVent_", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SealedVent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (GetStaticField("TORMapOptions", "ventsToSeal") is IEnumerable scheduled)
        {
            foreach (var item in scheduled)
            {
                if (item is Vent scheduledVent && scheduledVent && scheduledVent.Id == vent.Id)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetBombPosition(out Vector2 position)
    {
        position = default;
        var bomb = GetStaticField("Bomber", "bomb");
        var gameObject = bomb?.GetType()
            .GetField("bomb", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(bomb) as GameObject;
        if (!gameObject)
        {
            return false;
        }

        position = gameObject!.transform.position;
        return true;
    }

    private static List<Vector2> GetStrategicPlacements(PlayerControl bot, string role, bool reset)
    {
        var key = (bot.PlayerId, role);
        if (reset || !StrategicPlacements.TryGetValue(key, out var placements))
        {
            placements = [];
            StrategicPlacements[key] = placements;
        }
        return placements;
    }

    private static bool TryPlaceTrap(PlayerControl bot, out string outcome)
    {
        if (GetStaticInt("Trapper", "charges") <= 0)
        {
            outcome = "no trap charges remaining";
            return false;
        }

        var position = bot.GetTruePosition();
        var buffer = BuildPositionBuffer(position);
        SendRpc(bot, 162, writer => writer.WriteBytesAndSize(buffer));
        InvokeProcedure("setTrap", buffer);
        NextRoleAbilityAt[(bot.PlayerId, "Trapper")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Trapper", "cooldown"));
        outcome = $"placed an information trap at {SkeldPathGraph.Instance.NearestNode(position).Id}";
        return true;
    }

    private static bool HasRepairableEmergency(PlayerControl bot)
    {
        return bot.myTasks?.ToArray()
            .Any(task => task && !task.IsComplete && IsRepairableEmergency(task.TaskType)) == true;
    }

    private static bool IsRepairableEmergency(TaskTypes taskType)
    {
        return taskType is
            TaskTypes.FixLights or
            TaskTypes.RestoreOxy or
            TaskTypes.ResetReactor or
            TaskTypes.ResetSeismic or
            TaskTypes.FixComms or
            TaskTypes.StopCharles;
    }

    private static bool TryConsumeNearbyBody(PlayerControl bot, string roleName, out string outcome)
    {
        var body = UnityEngine.Object.FindObjectsOfType<DeadBody>()
            .Where(DeadBodyPerception.IsVisibleAndReportable)
            .Select(candidate => new
            {
                Body = candidate,
                Distance = Vector2.Distance(bot.GetTruePosition(), candidate.TruePosition)
            })
            .Where(item =>
                item.Distance <= DeadBodyPerception.GetReportDistance(bot) &&
                !PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    item.Body.TruePosition,
                    Constants.ShipAndObjectsMask,
                    false))
            .OrderBy(item => item.Distance)
            .Select(item => item.Body)
            .FirstOrDefault();
        if (!body)
        {
            outcome = "no visible report-range body to consume";
            return false;
        }

        var selectedBody = body!;
        SendRpc(bot, 123, writer =>
        {
            writer.Write(selectedBody.ParentId);
            writer.Write(bot.PlayerId);
        });
        InvokeProcedure("cleanBody", selectedBody.ParentId, bot.PlayerId);
        if (roleName == "Vulture")
        {
            NextRoleAbilityAt[(bot.PlayerId, roleName)] =
                Time.time + Mathf.Max(5f, GetStaticFloat("Vulture", "cooldown"));
        }
        outcome = $"{roleName.ToLowerInvariant()} removed body playerId={selectedBody.ParentId}";
        return true;
    }

    private static bool NeedsLivingTarget(string roleName)
    {
        return roleName is "Medic" or "Sheriff" or "Deputy" or "Tracker" or "Morphling" or "Vampire" or "Warlock" or "Ninja" or "Jackal" or "Sidekick" or "Pursuer" or "Thief" or "Eraser" or "Witch" or "Shifter" ||
               roleName == "Arsonist" && !DousedEveryoneAlive(GetStaticField("Arsonist", "arsonist") as PlayerControl);
    }

    private static bool IsLivingOpponent(PlayerControl bot, PlayerControl? target, TorRoleInfo role)
    {
        if (!target ||
            target!.PlayerId == bot.PlayerId ||
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            BotPerceptionPolicy.IsConcealedByVent(target))
        {
            return false;
        }

        if (AreLoverPartners(bot, target) && (role.IsImpostorTeam || role.IsNeutral))
        {
            return false;
        }

        if (role.Name == "Witch" && role.IsImpostorTeam && GetStaticBool("Witch", "canSpellAnyone"))
        {
            return true;
        }

        return !role.IsImpostorTeam || !IsImpostorTeam(target);
    }

    private static bool IsSheriffKillLegal(PlayerControl target)
    {
        if (IsImpostorTeam(target))
        {
            var mini = GetStaticField("Mini", "mini") as PlayerControl;
            if (mini && mini!.PlayerId == target.PlayerId && !InvokeStaticBool("Mini", "isGrownUp"))
            {
                return false;
            }
            return true;
        }

        if (!TryGetRole(target, out var targetRole))
        {
            return false;
        }

        if (targetRole.Name is "Jackal" or "Sidekick")
        {
            return true;
        }

        if (targetRole.Name == "Spy" && GetStaticBool("Sheriff", "spyCanDieToSheriff"))
        {
            return true;
        }

        return targetRole.IsNeutral && GetStaticBool("Sheriff", "canKillNeutrals");
    }

    private static string BuildWinCondition(string roleName, string alignment)
    {
        if (string.Equals(alignment, "crewmate", StringComparison.Ordinal))
        {
            return $"Win with the crew by completing real tasks or eliminating every hostile faction; use {roleName} information/ability without inventing knowledge.";
        }

        if (string.Equals(alignment, "impostor", StringComparison.Ordinal))
        {
            return $"Win with the impostor team by reaching hostile parity or a fatal sabotage while preserving allied cover; use {roleName} for a deliberate elimination or deception plan.";
        }

        return roleName switch
        {
            "Jester" => "Be voted out during a meeting; survival, tasks, and crew victory are only cover, not the primary objective.",
            "Jackal" => "Keep the Jackal faction alive and eliminate all players outside the Jackal/Sidekick team until that faction controls the game.",
            "Sidekick" => "Help the Jackal faction eliminate all outsiders; inherit leadership if the Jackal dies.",
            "Arsonist" => "Douse every other living player completely, then ignite; do not ignite or claim success before the live douse set is complete.",
            "Vulture" => "Consume the room-configured number of bodies before another faction ends the game; reporting a needed safe body works against this objective.",
            "Prosecutor" => "Get the assigned prosecution target voted out during a meeting; if that objective becomes impossible, follow TOR's configured conversion or fallback rules.",
            "Lawyer" => "Keep the assigned client alive and make the client's faction win, earning an additional Lawyer victory; if the client dies, convert and follow the Pursuer objective.",
            "Pursuer" => "Remain alive through a non-impostor victory without being unacknowledged/exiled; blanks are defensive tools, not random attacks.",
            "Thief" => "Steal an eligible hostile role by killing its owner, then pursue the inherited faction's win condition; an illegal theft attempt causes suicide.",
            "Shifter" => "Shift into a strategically useful role, survive the transfer rules, then pursue the inherited role's actual faction objective.",
            _ => $"Pursue the independent {roleName} win condition while staying alive and using only personally available information."
        };
    }

    private static string BuildArsonistProgress(PlayerControl? arsonist)
    {
        var doused = GetDousedPlayerIds();
        var remaining = PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player =>
                player &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                (!arsonist || player.PlayerId != arsonist!.PlayerId) &&
                !doused.Contains(player.PlayerId))
            .Select(Describe)
            .ToArray();
        return remaining.Length == 0
            ? $"Progress: doused={doused.Count}; every other living player is doused, so the next ability must be IGNITE with no target."
            : $"Progress: doused={doused.Count}; undoused living targets={string.Join(", ", remaining)}. Approach one listed target and complete the full douse channel; do not ignite yet.";
    }

    private static HashSet<byte> GetDousedPlayerIds()
    {
        var ids = new HashSet<byte>();
        if (GetStaticField("Arsonist", "dousedPlayers") is not IEnumerable players)
        {
            return ids;
        }

        foreach (var value in players)
        {
            if (value is PlayerControl player && player)
            {
                ids.Add(player.PlayerId);
            }
        }

        return ids;
    }

    private static bool IsDoused(byte playerId)
    {
        return GetDousedPlayerIds().Contains(playerId);
    }

    private static bool DousedEveryoneAlive(PlayerControl? arsonist)
    {
        if (!arsonist)
        {
            return false;
        }

        var doused = GetDousedPlayerIds();
        return PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player => player && player.Data is not null && !player.Data.IsDead && !player.Data.Disconnected)
            .All(player => player.PlayerId == arsonist!.PlayerId || doused.Contains(player.PlayerId));
    }

    private static void AddDousedPlayer(PlayerControl target)
    {
        var list = GetStaticField("Arsonist", "dousedPlayers");
        var add = list?.GetType().GetMethod("Add", new[] { typeof(PlayerControl) });
        add?.Invoke(list, new object[] { target });
    }

    private static void PerformUncheckedMurder(PlayerControl killer, PlayerControl victim)
    {
        SendRpc(killer, 108, writer =>
        {
            writer.Write(killer.PlayerId);
            writer.Write(victim.PlayerId);
            writer.Write(byte.MaxValue);
        });
        InvokeProcedure("uncheckedMurderPlayer", killer.PlayerId, victim.PlayerId, byte.MaxValue);
    }

    private static bool TryThiefSteal(PlayerControl thief, PlayerControl target, out string outcome)
    {
        if (TryCheckRuleAwareMurder(thief, target, out var thiefCheck) &&
            !string.Equals(thiefCheck, "PerformKill", StringComparison.Ordinal))
        {
            outcome = $"Thief attempt was blocked by TOR rules ({thiefCheck})";
            return true;
        }

        var eligible = IsImpostorTeam(target);
        if (TryGetRole(target, out var targetRole))
        {
            eligible |= targetRole.Name is "Jackal" or "Sidekick" ||
                        targetRole.Name == "Sheriff" && GetStaticBool("Thief", "canKillSheriff");
        }

        if (!eligible)
        {
            PerformUncheckedMurder(thief, thief);
            outcome = $"fatal illegal theft attempt against {Describe(target)}";
            return true;
        }

        SendRpc(thief, 161, writer => writer.Write(target.PlayerId));
        InvokeProcedure("thiefStealsRole", target.PlayerId);
        PerformUncheckedMurder(thief, target);
        outcome = $"stole the eligible role from {Describe(target)} and inherited its objective";
        return true;
    }

    private static bool IsLegalNearbyTarget(PlayerControl bot, PlayerControl? target, TorRoleInfo role)
    {
        if (!IsLivingOpponent(bot, target, role) ||
            !BotPerceptionPolicy.CanBeOrdinarilyObserved(target) ||
            Vector2.Distance(bot.GetTruePosition(), target!.GetTruePosition()) > GetAbilityUseRange(role.Name) ||
            PhysicsHelpers.AnythingBetween(
                bot.GetTruePosition(),
                target.GetTruePosition(),
                Constants.ShipOnlyMask,
                false))
        {
            return false;
        }

        if (role.Name is "Jackal" or "Sidekick")
        {
            var jackal = GetStaticField("Jackal", "jackal") as PlayerControl;
            var sidekick = GetStaticField("Sidekick", "sidekick") as PlayerControl;
            if ((jackal && target.PlayerId == jackal!.PlayerId) ||
                (sidekick && target.PlayerId == sidekick!.PlayerId))
            {
                return false;
            }

            if (role.Name == "Jackal" &&
                GetStaticBool("Jackal", "canCreateSidekick") &&
                IsImpostorTeam(target) &&
                !GetStaticBool("Jackal", "canCreateSidekickFromImpostor"))
            {
                return false;
            }
        }

        return true;
    }

    private static PlayerControl? FindPlayer(byte playerId)
    {
        return PlayerControl.AllPlayerControls
            .ToArray()
            .FirstOrDefault(player => player && player.PlayerId == playerId);
    }

    private static bool TryGetPortalEndpoints(out Vector2 first, out Vector2 second, out float duration)
    {
        first = default;
        second = default;
        duration = 0f;
        if (!GetStaticBool("Portal", "bothPlacedAndEnabled") ||
            GetStaticField("Portal", "firstPortal") is not { } firstPortal ||
            GetStaticField("Portal", "secondPortal") is not { } secondPortal ||
            !TryGetPortalPosition(firstPortal, out first) ||
            !TryGetPortalPosition(secondPortal, out second))
        {
            return false;
        }

        duration = Mathf.Max(0.1f, GetStaticFloat("Portal", "teleportDuration"));
        return true;
    }

    private static bool TryGetPortalPosition(object portal, out Vector2 position)
    {
        position = default;
        var gameObject = portal.GetType()
            .GetField("portalGameObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(portal) as GameObject;
        if (!gameObject)
        {
            return false;
        }

        position = gameObject!.transform.position;
        return true;
    }

    private static void AddRoleOwner(List<string> allies, string typeName, string fieldName, byte selfId)
    {
        var owner = GetStaticField(typeName, fieldName) as PlayerControl;
        if (owner && owner!.PlayerId != selfId && owner.Data is not null && !owner.Data.IsDead && !owner.Data.Disconnected)
        {
            allies.Add(Describe(owner));
        }
    }

    private static void SendRpc(PlayerControl sender, byte rpcId, Action<MessageWriter>? write = null)
    {
        var writer = AmongUsClient.Instance.StartRpcImmediately(sender.NetId, rpcId, SendOption.Reliable, -1);
        write?.Invoke(writer);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    private static void InvokeProcedure(string methodName, params object[] args)
    {
        if (!EnsureLoaded() || _rpcProcedureType is null)
        {
            throw new InvalidOperationException("TOR RPCProcedure is unavailable.");
        }

        var method = _rpcProcedureType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            throw new MissingMethodException(_rpcProcedureType.FullName, methodName);
        }

        method.Invoke(null, args);
    }

    private static void InvokeHelper(string methodName)
    {
        if (!EnsureLoaded() || _helpersType is null)
        {
            throw new InvalidOperationException("TOR Helpers is unavailable.");
        }

        var method = _helpersType.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            throw new MissingMethodException(_helpersType.FullName, methodName);
        }
        method.Invoke(null, null);
    }

    private static bool InvokeStaticBool(string typeName, string methodName)
    {
        if (!EnsureLoaded() || _assembly is null)
        {
            return false;
        }

        var type = GetTorType(typeName);
        var method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return method?.Invoke(null, null) is bool value && value;
    }

    private static object? InvokeRoleMethod(string typeName, string methodName, params object[] args)
    {
        var type = GetTorType(typeName);
        var method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            throw new MissingMethodException(type?.FullName ?? typeName, methodName);
        }
        return method.Invoke(null, args);
    }

    private static Type? GetTorType(string typeName)
    {
        return _rootType?.GetNestedType(typeName, BindingFlags.Public | BindingFlags.NonPublic) ??
               _assembly?.GetType($"TheOtherRoles.{typeName}", false) ??
               _assembly?.GetType($"TheOtherRoles.Objects.{typeName}", false);
    }

    private static bool EnsureLoaded()
    {
        if (_assembly is not null && _rootType is not null && _rpcProcedureType is not null)
        {
            return true;
        }

        _assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, TorAssemblyName, StringComparison.Ordinal));
        _rootType = _assembly?.GetType(TorRootTypeName, false);
        _rpcProcedureType = _assembly?.GetType(TorRpcProcedureTypeName, false);
        _helpersType = _assembly?.GetType(TorHelpersTypeName, false);
        return _rootType is not null && _rpcProcedureType is not null;
    }

    private static object? GetStaticField(string nestedTypeName, string fieldName)
    {
        if (!EnsureLoaded() || _rootType is null)
        {
            return null;
        }

        var key = (nestedTypeName, fieldName);
        if (!FieldCache.TryGetValue(key, out var field))
        {
            // TOR 4.6.0 keeps legacy roles nested in TheOtherRoles.TheOtherRoles,
            // but newer roles (Lawyer, Pursuer, Witch, Ninja, Thief, Trapper,
            // Bomber, Yoyo, Shifter...) are namespace-level types. Supporting
            // both layouts is required for the role shown by TOR to be the role
            // that DeepBot actually reasons and acts as.
            var roleType = _rootType.GetNestedType(
                               nestedTypeName,
                               BindingFlags.Public | BindingFlags.NonPublic) ??
                           _assembly?.GetType($"TheOtherRoles.{nestedTypeName}", false) ??
                           _assembly?.GetType($"TheOtherRoles.Objects.{nestedTypeName}", false);
            field = roleType?.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            FieldCache[key] = field;
        }

        return field?.GetValue(null);
    }

    private static void SetStaticField(string nestedTypeName, string fieldName, object value)
    {
        if (!EnsureLoaded() || _rootType is null)
        {
            return;
        }

        var key = (nestedTypeName, fieldName);
        if (!FieldCache.TryGetValue(key, out var field))
        {
            _ = GetStaticField(nestedTypeName, fieldName);
            FieldCache.TryGetValue(key, out field);
        }

        field?.SetValue(null, value);
    }

    private static bool GetStaticBool(string typeName, string fieldName)
    {
        return GetStaticField(typeName, fieldName) is bool value && value;
    }

    private static float GetStaticFloat(string typeName, string fieldName)
    {
        return GetStaticField(typeName, fieldName) switch
        {
            float value => value,
            double value => (float)value,
            _ => 0f
        };
    }

    private static int GetStaticInt(string typeName, string fieldName)
    {
        return GetStaticField(typeName, fieldName) switch
        {
            int value => value,
            byte value => value,
            _ => 0
        };
    }

    private static int GetStaticCollectionCount(string typeName, string fieldName)
    {
        return GetStaticField(typeName, fieldName) is ICollection collection ? collection.Count : 0;
    }

    private static bool OwnsModifier(PlayerControl player, ModifierSpec spec)
    {
        var value = GetStaticField(spec.TypeName, spec.OwnerField);
        if (value is PlayerControl owner)
        {
            return owner && owner.PlayerId == player.PlayerId;
        }

        if (value is not IEnumerable owners)
        {
            return false;
        }

        foreach (var item in owners)
        {
            if (item is PlayerControl candidate && candidate && candidate.PlayerId == player.PlayerId)
            {
                return true;
            }
        }

        return false;
    }

    private static string WithModifierInformation(PlayerControl bot, string primaryInformation)
    {
        var modifierPlan = BuildModifierPlan(bot);
        return string.IsNullOrWhiteSpace(modifierPlan)
            ? primaryInformation
            : $"{primaryInformation} {modifierPlan}";
    }

    private static string BuildModifierPlan(PlayerControl bot)
    {
        var modifiers = GetModifiers(bot);
        if (modifiers.Count == 0)
        {
            return string.Empty;
        }

        var details = modifiers.Select(modifier => $"{modifier.Name}: {modifier.Description}").ToList();
        if (modifiers.Any(modifier => modifier.Name == "Lover"))
        {
            var lover1 = GetStaticField("Lovers", "lover1") as PlayerControl;
            var lover2 = GetStaticField("Lovers", "lover2") as PlayerControl;
            var partner = lover1 && lover1!.PlayerId == bot.PlayerId ? lover2 : lover1;
            if (partner && partner!.Data is not null)
            {
                details.Add($"Lover partner={Describe(partner)}; account for the shared survival outcome.");
            }
        }

        return $"Active modifiers: {string.Join(" | ", details)}";
    }

    private static string Describe(PlayerControl? player)
    {
        return player?.Data is null
            ? "unknown"
            : $"{player.Data.PlayerName}({player.PlayerId})";
    }

    private sealed record RoleSpec(
        string Name,
        string OwnerField,
        string Alignment,
        bool ActiveAbility,
        string AbilityPurpose)
    {
        public string TypeName => Name is "NiceGuesser" or "EvilGuesser" ? "Guesser" :
                                  Name == "Prosecutor" ? "Lawyer" : Name;
    }

    private sealed record ModifierSpec(
        string Name,
        string TypeName,
        string OwnerField,
        bool IsCollection,
        string Description);

    private readonly record struct PendingDouse(byte TargetId, float CompleteAt);
    private readonly record struct PendingVampireBite(byte TargetId, float CompleteAt, Vector2 Origin);
    private readonly record struct PendingWarlockCurse(byte VictimId);
    private readonly record struct PendingWitchSpell(byte TargetId, float CompleteAt);
    private readonly record struct PendingPortalTeleport(
        Vector2 Entry,
        Vector2 Exit,
        float MidpointAt,
        float CompleteAt,
        bool Moved);
}

internal readonly record struct TorRoleInfo(
    string Name,
    string Alignment,
    bool ActiveAbility,
    string WinCondition,
    string AbilityPurpose)
{
    internal bool IsImpostorTeam => string.Equals(Alignment, "impostor", StringComparison.Ordinal);
    internal bool IsNeutral => string.Equals(Alignment, "neutral", StringComparison.Ordinal);
}

internal readonly record struct TorModifierInfo(string Name, string Description);

internal readonly record struct TorAbilitySequencePlan(
    bool Active,
    bool ShouldUse,
    byte? TargetPlayerId,
    string AbilityAction,
    string Reason,
    float Confidence,
    float RecheckSeconds);

internal readonly record struct TorPortalTraversalOption(
    Vector2 Entry,
    Vector2 Exit,
    bool Remote,
    byte ExitMode,
    float Duration);

internal readonly record struct TorIntroRoleInfo(
    string Name,
    string Description,
    Color Color,
    string Alignment,
    string RoleId);

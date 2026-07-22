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

    private static readonly Dictionary<(string Type, string Field), FieldInfo?> FieldCache = [];
    private static Assembly? _assembly;
    private static Type? _rootType;
    private static Type? _rpcProcedureType;
    private static Type? _helpersType;
    private static MethodInfo? _checkMurderMethod;
    private static MethodInfo? _checkAndKillMethod;
    private static ManualLogSource? _log;
    private static bool _availabilityLogged;
    private static readonly Dictionary<byte, string> LoggedAssignments = [];
    private static readonly Dictionary<byte, PendingDouse> PendingDouses = [];
    private static readonly Dictionary<byte, PendingVampireBite> PendingVampireBites = [];
    private static readonly Dictionary<byte, PendingWarlockCurse> PendingWarlockCurses = [];
    private static readonly Dictionary<byte, float> PendingNinjaReveals = [];
    private static readonly Dictionary<byte, float> PendingYoyoReturns = [];
    private static readonly Dictionary<(byte PlayerId, string Role), float> NextRoleAbilityAt = [];
    private static readonly Dictionary<(byte PlayerId, string Role), List<Vector2>> StrategicPlacements = [];
    private static Type? _trapType;

    internal static bool IsAvailable => EnsureLoaded();

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
        out string resultName)
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
                new object[] { killer, target, false, false, false, false });
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
    }

    internal static bool TryGetRole(PlayerControl? player, out TorRoleInfo role)
    {
        role = default;
        if (!player || !EnsureLoaded())
        {
            return false;
        }

        foreach (var spec in RoleSpecs)
        {
            if (spec.Name == "Prosecutor" && !GetStaticBool("Lawyer", "isProsecutor") ||
                spec.Name == "Lawyer" && GetStaticBool("Lawyer", "isProsecutor"))
            {
                continue;
            }

            var owner = GetStaticField(spec.TypeName, spec.OwnerField) as PlayerControl;
            if (owner && owner!.PlayerId == player!.PlayerId)
            {
                role = new TorRoleInfo(
                    spec.Name,
                    spec.Alignment,
                    spec.ActiveAbility,
                    BuildWinCondition(spec.Name, spec.Alignment),
                    spec.AbilityPurpose);
                return true;
            }
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
        if (TryGetRole(player, out var primary) && primary.ActiveAbility)
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
            if (!player ||
                player.Data is null ||
                !player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal))
            {
                continue;
            }

            var hasCustomRole = TryGetRole(player, out var role);
            var primaryName = hasCustomRole ? role.Name : player.Data.RoleType.ToString();
            var alignment = hasCustomRole
                ? role.Alignment
                : player.Data.Role?.IsImpostor == true ? "impostor" : "crewmate";
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

    internal static bool IsAbilityReady(PlayerControl bot, TorRoleInfo role)
    {
        if (!role.ActiveAbility || !bot || bot.Data is null || bot.Data.IsDead || IsHandcuffed(bot))
        {
            return false;
        }

        if (PendingDouses.ContainsKey(bot.PlayerId) ||
            PendingVampireBites.ContainsKey(bot.PlayerId) ||
            PendingWarlockCurses.ContainsKey(bot.PlayerId) ||
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
                               GetStaticInt("SecurityGuard", "camPrice"),
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

    internal static bool HasExclusiveKillAbilityPending(PlayerControl? bot)
    {
        return bot && PendingVampireBites.ContainsKey(bot!.PlayerId);
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
            if (godfather && godfather!.Data is not null && !godfather.Data.IsDead && !godfather.Data.Disconnected)
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
        return roleName == "Arsonist" ? 2f : 3.5f;
    }

    internal static bool IsRuleImmobilized(PlayerControl? player)
    {
        if (!player || !EnsureLoaded())
        {
            return false;
        }

        var map = GetStaticField("Trap", "trapPlayerIdMap") as IDictionary;
        return map?.Contains(player!.PlayerId) == true;
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
        if (!bot || bot.Data is null || IsHandcuffed(bot)) return false;
        return role.Name switch
        {
            "Engineer" => true,
            "Jackal" => GetStaticBool("Jackal", "canUseVents"),
            "Sidekick" => GetStaticBool("Sidekick", "canUseVents"),
            "Spy" => GetStaticBool("Spy", "canEnterVents"),
            "Vulture" => GetStaticBool("Vulture", "canUseVents"),
            "Thief" => GetStaticBool("Thief", "canUseVents"),
            "Janitor" => false,
            "Mafioso" when GetStaticField("Godfather", "godfather") is PlayerControl godfather &&
                            godfather && godfather.Data is not null && !godfather.Data.IsDead => false,
            _ => role.IsImpostorTeam && bot.Data.Role?.CanVent == true
        };
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
                    player.Data.Role?.IsImpostor == true)
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
        return
            $"Public TOR role rulebook (possible roles, never secret assignments): crew=[{crew}]; impostor=[{impostor}]; neutral=[{neutral}]; modifiers=[{modifiers}]. " +
            "Deduction constraints: a witnessed vent proves only a vent-capable role (ordinary impostor, Engineer, or a room-enabled Jackal/Sidekick/Spy/Vulture/Thief); " +
            "a witnessed kill proves a kill-capable role, which can also be Sheriff, Jackal faction, Vampire, Warlock, Ninja, Thief, Bomber, Arsonist, or another hostile custom role, so use target legality and aftermath to narrow it; " +
            "a disappearing body can indicate Janitor, Cleaner, Vulture, or another explicit body-removal skill; fake-task standing and task-bar motion alone do not prove crew; " +
            "Jester wants exile, Arsonist must douse everyone then ignite, Vulture must consume bodies, Lawyer/Prosecutor act around their assigned target, Pursuer prioritizes survival, and Jackal/Sidekick have an independent faction objective. " +
            "Only infer from personally visible actions and public claims; never read another player's hidden role from engine state.";
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
            "Jester" => "Phase: create believable inconsistencies and attract votes gradually; do not perform an obvious role confession that rational players would ignore.",
            _ => string.Empty
        };
        var modifierPlan = BuildModifierPlan(bot);
        return $"Win condition: {role.WinCondition} Ability/operating plan: {role.AbilityPurpose}" +
               (string.IsNullOrWhiteSpace(live) ? string.Empty : $" {live}") +
               (string.IsNullOrWhiteSpace(modifierPlan) ? string.Empty : $" {modifierPlan}");
    }

    internal static void Update()
    {
        UpdateVirtualBotHandcuffs();
        UpdateVirtualBotTrapTriggers();

        foreach (var pair in PendingDouses.ToArray())
        {
            var bot = FindPlayer(pair.Key);
            var target = FindPlayer(pair.Value.TargetId);
            if (!bot || !target || bot!.Data is null || target!.Data is null || bot.Data.IsDead || target.Data.IsDead ||
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
            PendingDouses.Remove(pair.Key);
            NextRoleAbilityAt[(pair.Key, "Arsonist")] = Time.time + Mathf.Max(0.05f, GetStaticFloat("Arsonist", "cooldown"));
            _log?.LogInfo($"DeepBot Arsonist douse completed: bot={Describe(bot)}, target={Describe(target)}, {BuildArsonistProgress(bot)}");
        }

        foreach (var pair in PendingVampireBites.ToArray())
        {
            if (Time.time < pair.Value.CompleteAt) continue;
            var bot = FindPlayer(pair.Key);
            var target = FindPlayer(pair.Value.TargetId);
            PendingVampireBites.Remove(pair.Key);

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
                !TryGetRole(bot, out var warlockRole) || warlockRole.Name != "Warlock" ||
                Time.time >= pair.Value.ExpiresAt)
            {
                ClearWarlockCurse(pair.Key, "curse expired or carrier became unavailable");
                continue;
            }

            var forcedTarget = PlayerControl.AllPlayerControls
                .ToArray()
                .Where(player =>
                    player &&
                    player.PlayerId != bot.PlayerId &&
                    player.PlayerId != victim.PlayerId &&
                    player.Data is not null &&
                    !player.Data.IsDead &&
                    !player.Data.Disconnected &&
                    player.Data.Role?.IsImpostor != true &&
                    Vector2.Distance(victim.GetTruePosition(), player.GetTruePosition()) <= 2.0f)
                .OrderBy(player => Vector2.Distance(victim.GetTruePosition(), player.GetTruePosition()))
                .FirstOrDefault();
            if (!forcedTarget) continue;

            var handled = TryExecuteRuleAwareMurder(bot, forcedTarget!, out var killed, out var result, showAnimation: false);
            ClearWarlockCurse(pair.Key, $"redirected target={Describe(forcedTarget)}, handled={handled}, killed={killed}, result={result}");
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

    private static void UpdateVirtualBotHandcuffs()
    {
        if (!EnsureLoaded() || GetStaticField("Deputy", "handcuffedKnows") is not IDictionary active)
        {
            return;
        }

        var pending = GetStaticField("Deputy", "handcuffedPlayers") as IEnumerable;
        foreach (var bot in PlayerControl.AllPlayerControls.ToArray().Where(player =>
                     player && player.Data is not null &&
                     player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal)))
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
        if (!EnsureLoaded() || AmongUsClient.Instance is null || !AmongUsClient.Instance.AmHost)
        {
            return;
        }

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

            foreach (var bot in PlayerControl.AllPlayerControls.ToArray().Where(player =>
                         player && player.Data is not null &&
                         player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal)))
            {
                if (!bot || bot.Data is null || bot.Data.IsDead || bot.Data.Disconnected || bot.inVent || !bot.moveable ||
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
        if (!IsAbilityReady(bot, role) || !AmongUsClient.Instance)
        {
            outcome = "TOR ability is unavailable or cooling down";
            return false;
        }

        var target = requestedTargetId.HasValue ? FindPlayer(requestedTargetId.Value) : null;
        var ninjaMarked = role.Name == "Ninja"
            ? GetStaticField("Ninja", "ninjaMarked") as PlayerControl
            : null;
        if (ninjaMarked)
        {
            target = ninjaMarked;
        }

        var targetIsLegal = role.Name == "Ninja" && ninjaMarked
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
                    SendRpc(bot, 130, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure("morphlingMorph", target!.PlayerId);
                    outcome = $"morphed into {Describe(target)}";
                    return true;
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
                    SendRpc(bot, 143, writer => writer.Write(target!.PlayerId));
                    InvokeProcedure("setFutureSpelled", target!.PlayerId);
                    outcome = $"spelled {Describe(target)} for the next meeting";
                    return true;
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
                    return TryPlaceSecurityCamera(bot, out outcome);
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

    private static bool TryUseEngineerRepair(PlayerControl bot, out string outcome)
    {
        var repairable = bot.myTasks?.ToArray()
            .FirstOrDefault(task => task && !task.IsComplete && IsRepairableEmergency(task.TaskType));
        if (!repairable || !ShipStatus.Instance)
        {
            outcome = "no active repairable emergency";
            return false;
        }

        SendRpc(bot, 122);
        InvokeProcedure("engineerUsedRepair");
        switch (repairable!.TaskType)
        {
            case TaskTypes.FixLights:
                SendRpc(bot, 120);
                InvokeProcedure("engineerFixLights");
                break;
            case TaskTypes.RestoreOxy:
                ShipStatus.Instance.UpdateSystem(SystemTypes.LifeSupp, bot, 0 | 64);
                ShipStatus.Instance.UpdateSystem(SystemTypes.LifeSupp, bot, 1 | 64);
                break;
            case TaskTypes.ResetReactor:
                ShipStatus.Instance.UpdateSystem(SystemTypes.Reactor, bot, 16);
                break;
            case TaskTypes.ResetSeismic:
                ShipStatus.Instance.UpdateSystem(SystemTypes.Laboratory, bot, 16);
                break;
            case TaskTypes.FixComms:
                ShipStatus.Instance.UpdateSystem(SystemTypes.Comms, bot, 16 | 0);
                ShipStatus.Instance.UpdateSystem(SystemTypes.Comms, bot, 16 | 1);
                break;
            case TaskTypes.StopCharles:
                ShipStatus.Instance.UpdateSystem(SystemTypes.Reactor, bot, 0 | 16);
                ShipStatus.Instance.UpdateSystem(SystemTypes.Reactor, bot, 1 | 16);
                break;
        }

        outcome = $"spent one room-configured repair charge on {repairable.TaskType}";
        return true;
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
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Vampire", "delay")));
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
        if (PendingWarlockCurses.ContainsKey(bot.PlayerId))
        {
            outcome = "a curse carrier is already active";
            return false;
        }

        SetStaticField("Warlock", "curseVictim", target);
        SetStaticField("Warlock", "curseVictimTarget", null!);
        PendingWarlockCurses[bot.PlayerId] = new PendingWarlockCurse(target.PlayerId, Time.time + 30f);
        outcome = $"cursed mobile carrier {Describe(target)} and will redirect a kill only if that carrier approaches a legal opponent";
        return true;
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

        var position = bot.GetTruePosition();
        var buffer = BuildPositionBuffer(position);
        SendRpc(bot, 165, writer => writer.WriteBytesAndSize(buffer));
        InvokeProcedure("placeBomb", buffer);
        SetStaticField("Bomber", "isPlanted", true);
        NextRoleAbilityAt[(bot.PlayerId, "Bomber")] =
            Time.time + Mathf.Max(0.05f, GetStaticFloat("Bomber", "bombCooldown"));
        outcome = $"planted a room-configured bomb at {SkeldPathGraph.Instance.NearestNode(position).Id} for a deliberate split or elimination";
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
            target.Data.Disconnected)
        {
            return false;
        }

        if (AreLoverPartners(bot, target) && (role.IsImpostorTeam || role.IsNeutral))
        {
            return false;
        }

        return !role.IsImpostorTeam || target.Data.Role?.IsImpostor != true;
    }

    private static bool IsSheriffKillLegal(PlayerControl target)
    {
        if (target.Data?.Role?.IsImpostor == true)
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

        var eligible = target.Data?.Role?.IsImpostor == true;
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
            Vector2.Distance(bot.GetTruePosition(), target!.GetTruePosition()) > (role.Name == "Arsonist" ? 2.0f : 3.5f))
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
                target.Data.Role?.IsImpostor == true &&
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
    private readonly record struct PendingVampireBite(byte TargetId, float CompleteAt);
    private readonly record struct PendingWarlockCurse(byte VictimId, float ExpiresAt);
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

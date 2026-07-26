using BepInEx.Logging;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal static class BotBehaviorPolicy
{
    private static readonly string[][] PlayerColorAliases =
    [
        ["红", "红色", "red"],
        ["蓝", "蓝色", "深蓝", "blue"],
        ["绿", "绿色", "深绿", "green"],
        ["粉", "粉色", "粉红", "粉红色", "pink"],
        ["橙", "橙色", "橘色", "orange"],
        ["黄", "黄色", "yellow"],
        ["黑", "黑色", "black"],
        ["白", "白色", "white"],
        ["紫", "紫色", "purple"],
        ["棕", "棕色", "褐色", "brown"],
        ["青", "青色", "浅蓝", "天蓝", "cyan"],
        ["浅绿", "亮绿", "lime"],
        ["暗红", "栗色", "酒红", "maroon"],
        ["玫红", "玫瑰", "玫瑰色", "rose"],
        ["香蕉黄", "淡黄", "banana"],
        ["灰", "灰色", "gray", "grey"],
        ["棕褐", "棕褐色", "卡其", "tan"],
        ["珊瑚", "珊瑚色", "coral"]
    ];

    public static int SelectDistributedIndex(byte playerId, int epoch, int candidateCount)
    {
        return candidateCount <= 1 ? 0 : (playerId + epoch) % candidateCount;
    }

    public static bool IsDestinationReached(Vector2 current, Vector2 intendedTarget, Vector2 routeEndpoint, float useDistance)
    {
        var interactionDistance = Mathf.Clamp(useDistance, 0.55f, 1.5f);
        var endpointNearTarget = Vector2.Distance(routeEndpoint, intendedTarget) <= interactionDistance + 0.45f;
        var botNearEndpoint = Vector2.Distance(current, routeEndpoint) <= Mathf.Max(0.55f, interactionDistance);
        return endpointNearTarget && botNearEndpoint;
    }

    public static bool ShouldRefreshMovingTarget(Vector2 routedPosition, Vector2 livePosition, float refreshDistance)
    {
        return Vector2.Distance(routedPosition, livePosition) >= Mathf.Max(0.1f, refreshDistance);
    }

    public static float ScoreMurderCandidate(
        float distance,
        int nearbyCrew,
        bool isBotVictim,
        bool repeatedTarget,
        float tieBreaker)
    {
        return 9f -
               Mathf.Max(0f, distance) * 0.55f -
               Math.Max(0, nearbyCrew) * 3.2f +
               (isBotVictim ? 3.5f : 0f) -
               (repeatedTarget ? 4.5f : 0f) +
               Mathf.Clamp(tieBreaker, 0f, 0.5f);
    }

    public static bool ShouldUseRecentSightForMurderPlan(
        bool currentlyVisible,
        float lastSeenAge,
        float memoryWindow)
    {
        return currentlyVisible ||
               lastSeenAge >= 0f && lastSeenAge <= Mathf.Max(0.1f, memoryWindow);
    }

    public static bool IsPersistentApproach(
        float previousDistance,
        float currentDistance,
        float sampleAge,
        int resultingStreak,
        float triggerDistance)
    {
        return sampleAge is >= 0.25f and <= 1.25f &&
               previousDistance - currentDistance >= 0.18f &&
               currentDistance <= triggerDistance &&
               resultingStreak >= 2;
    }

    public static bool ShouldExecuteMurder(string exposure, bool hasEscapeRoute)
    {
        return exposure == "low" ||
               exposure == "crowded" && hasEscapeRoute;
    }

    public static bool ShouldUseDirectGhostTaskRoute(bool isDead, bool isTaskAction)
    {
        return isDead && isTaskAction;
    }

    public static bool ShouldStartActivePlayWindow(bool localPlayerReady, bool allLivingPlayersMovable)
    {
        return localPlayerReady && allLivingPlayersMovable;
    }

    public static bool HasStableActionWindow(
        bool rawReady,
        float readySince,
        float now,
        float requiredStableSeconds)
    {
        return rawReady &&
               readySince > 0f &&
               now - readySince >= Mathf.Max(0f, requiredStableSeconds);
    }

    public static bool ShouldUseOpeningFakeTask(bool isImpostor, bool openingCoverCompleted)
    {
        return isImpostor && !openingCoverCompleted;
    }

    public static bool ShouldKeepRoundResumePending(bool assignedPhysicalAction)
    {
        // A failed route probe is not a decision to stand still.  The bot must
        // retry until it owns a real route/dwell action; otherwise roles that
        // have no real tasks (notably impostors and neutrals) can be exposed at
        // match start or after a meeting while crewmates immediately move.
        return !assignedPhysicalAction;
    }

    public static bool ShouldUseWitnessedKillerInMeeting(
        bool hasPersonalWitness,
        bool killerAlive,
        bool isSelf,
        bool protectedAlly)
    {
        return hasPersonalWitness && killerAlive && !isSelf && !protectedAlly;
    }

    public static bool ShouldOverrideForRoleObjective(string roleName, bool objectiveOpportunity)
    {
        return objectiveOpportunity && roleName is
            "Engineer" or
            "Arsonist" or
            "Vulture" or
            "Cleaner" or
            "Janitor";
    }

    public static bool ShouldWaitForMiraDeconCapacity(
        bool alreadyInside,
        int livingPlayersInside,
        int capacity)
    {
        return !alreadyInside && livingPlayersInside >= Math.Max(1, capacity);
    }

    public static bool ShouldPrioritizeVisibleBody(bool withinReportRange, bool criticalEmergencyActive)
    {
        return withinReportRange || !criticalEmergencyActive;
    }

    public static bool ShouldImpostorReportBody(
        bool ownRecentVictim,
        int visibleWitnesses,
        float socialWillingness,
        float personalRoll)
    {
        if (visibleWitnesses >= 2)
        {
            return true;
        }

        if (ownRecentVictim && visibleWitnesses <= 0)
        {
            return false;
        }

        var willingness = Mathf.Clamp01(socialWillingness);
        var threshold = visibleWitnesses == 1
            ? 0.50f + willingness * 0.38f
            : 0.12f + willingness * 0.28f;
        return Mathf.Clamp01(personalRoll) <= threshold;
    }

    public static float GetBodyObservationDelay(
        float eyewitnessReliance,
        bool impostor,
        bool criticalEmergency)
    {
        var normalDelay = 1.55f + Mathf.Clamp01(eyewitnessReliance) * 1.45f + (impostor ? 0.45f : 0f);
        return criticalEmergency
            ? Mathf.Clamp(normalDelay, 1.2f, 1.7f)
            : Mathf.Clamp(normalDelay, 1.5f, 3.4f);
    }

    public static bool ShouldRespondToEmergency(
        bool critical,
        bool impostor,
        float responsiveness,
        int visibleLikelyResponders,
        float unresolvedSeconds,
        float personalRoll)
    {
        // An unresolved sabotage is public evidence that the responders seen
        // earlier did not finish it.  Every living crewmate therefore retries
        // independently after a bounded delay.  Critical two-panel events need
        // the earlier deadline because MIRA routes can consume much of the
        // remaining timer; lights/comms also need a deadline so a set of high
        // personal rolls cannot leave the outage untouched forever. Impostors
        // retain the strategic choice to help or continue their deception.
        var forcedCrewResponseAfter = critical ? 4f : 8f;
        if (!impostor && unresolvedSeconds >= forcedCrewResponseAfter)
        {
            return true;
        }

        var probability =
            Mathf.Clamp01(responsiveness) +
            (critical ? 0.18f : 0f) +
            Mathf.Clamp01(unresolvedSeconds / 24f) * 0.30f -
            Math.Max(0, visibleLikelyResponders) * 0.16f -
            (impostor ? 0.22f : 0f);
        return Mathf.Clamp01(probability) >= Mathf.Clamp01(personalRoll);
    }

    public static float GetMeetingVoteConfidenceThreshold(float voteBoldness)
    {
        return Mathf.Lerp(0.78f, 0.28f, Mathf.Clamp01(voteBoldness));
    }

    public static bool ShouldCommitMeetingCandidate(
        bool validTarget,
        bool skipVote,
        bool crewAligned,
        bool personallyWitnessed,
        float modelConfidence,
        float confidenceThreshold,
        float groundedEvidence,
        float groundedThreshold)
    {
        if (!validTarget || skipVote)
        {
            return false;
        }

        if (personallyWitnessed)
        {
            return true;
        }

        // For crew, confidence describes how certain the model sounds; it is
        // not an additional kind of evidence. Once the candidate reaches this
        // personality's grounded evidence line, a slightly cautious confidence
        // value must not silently turn the already-validated vote back into a
        // skip at submission time. Hostile/neutral roles instead evaluate the
        // candidate against their private objective and retain the personality
        // confidence gate.
        return crewAligned
            ? groundedEvidence >= groundedThreshold
            : modelConfidence >= confidenceThreshold;
    }

    public static bool ShouldSelectStrategicAbilityRole(
        bool abilityReady,
        bool canUseVents,
        bool sequencePending)
    {
        return abilityReady || canUseVents || sequencePending;
    }

    public static bool CanContinueAbilityThroughReadinessGate(
        bool abilityReady,
        bool sequenceActive,
        bool sequenceShouldUse,
        string? sequenceAction)
    {
        return abilityReady ||
               sequenceActive &&
               sequenceShouldUse &&
               string.Equals(sequenceAction, "role", StringComparison.Ordinal);
    }

    public static bool ShouldAttemptGuesserInference(
        string guesserRole,
        string inferredAlignment,
        float inferenceConfidence,
        float observationAgeSeconds)
    {
        if (inferenceConfidence < 0.90f || observationAgeSeconds < 0f || observationAgeSeconds > 55f)
        {
            return false;
        }

        return guesserRole switch
        {
            "NiceGuesser" => !string.Equals(inferredAlignment, "crewmate", StringComparison.OrdinalIgnoreCase),
            "EvilGuesser" => !string.Equals(inferredAlignment, "impostor", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static bool HasUnanalyzedMeetingTranscript(int analyzedVersion, int currentVersion)
    {
        return analyzedVersion < currentVersion;
    }

    public static bool MentionsPlayerAlias(
        string text,
        byte playerId,
        string? displayName,
        int colorId = -1,
        string? localizedColorName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var compactText = CompactMeetingText(text);
        var normalizedText = text.Trim().ToLowerInvariant();
        var compactName = CompactMeetingText(displayName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(compactName) &&
            compactText.Contains(compactName, StringComparison.Ordinal))
        {
            return true;
        }

        const string botPrefix = "deepbot";
        if (compactName.StartsWith(botPrefix, StringComparison.Ordinal) &&
            int.TryParse(compactName[botPrefix.Length..], out var ordinal))
        {
            if (ContainsIndexedAlias(compactText, "bot", ordinal) ||
                ContainsIndexedAlias(compactText, botPrefix, ordinal) ||
                ContainsChinesePlayerNumber(compactText, ordinal))
            {
                return true;
            }
        }

        if (ContainsIndexedAlias(compactText, "player", playerId) ||
            ContainsIndexedAlias(compactText, "玩家", playerId))
        {
            return true;
        }

        var normalizedLocalizedColor = (localizedColorName ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedLocalizedColor) &&
            ContainsColorAlias(normalizedText, normalizedLocalizedColor))
        {
            return true;
        }

        return colorId >= 0 &&
               colorId < PlayerColorAliases.Length &&
               PlayerColorAliases[colorId].Any(alias =>
                   ContainsColorAlias(normalizedText, alias.ToLowerInvariant()));
    }

    public static int FindPlayerAliasIndex(
        string text,
        byte playerId,
        string? displayName,
        int colorId = -1,
        string? localizedColorName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        var compactText = CompactMeetingText(text);
        var indexes = new List<int>();
        static void AddIndex(List<int> destination, string haystack, string? value)
        {
            var compactValue = CompactMeetingText(value ?? string.Empty);
            if (compactValue.Length == 0)
            {
                return;
            }

            var index = haystack.IndexOf(compactValue, StringComparison.Ordinal);
            if (index >= 0)
            {
                destination.Add(index);
            }
        }

        AddIndex(indexes, compactText, displayName);
        AddIndex(indexes, compactText, localizedColorName);
        AddIndex(indexes, compactText, $"player{playerId}");
        AddIndex(indexes, compactText, $"玩家{playerId}");

        var compactName = CompactMeetingText(displayName ?? string.Empty);
        const string botPrefix = "deepbot";
        if (compactName.StartsWith(botPrefix, StringComparison.Ordinal) &&
            int.TryParse(compactName[botPrefix.Length..], out var ordinal))
        {
            AddIndex(indexes, compactText, $"bot{ordinal}");
            AddIndex(indexes, compactText, $"deepbot{ordinal}");
            AddIndex(indexes, compactText, $"{ordinal}号");
        }

        if (colorId >= 0 && colorId < PlayerColorAliases.Length)
        {
            foreach (var alias in PlayerColorAliases[colorId])
            {
                AddIndex(indexes, compactText, alias);
            }
        }

        return indexes.Count == 0 ? -1 : indexes.Min();
    }

    public static int MeetingResponderOrder(byte playerId, int humanTranscriptVersion)
    {
        return (playerId * 37 + humanTranscriptVersion * 19) % 101;
    }

    public static bool ShouldRepairVisionDistance(float actual, float expected, float tolerance = 0.30f)
    {
        return float.IsFinite(expected) &&
               (!float.IsFinite(actual) ||
                Mathf.Abs(actual - expected) > Mathf.Max(0.01f, tolerance));
    }

    public static bool ShouldRestoreHostView(
        bool hostAvailable,
        bool localPlayerIsHost,
        bool cameraTargetMissingOrBot)
    {
        return hostAvailable && (!localPlayerIsHost || cameraTargetMissingOrBot);
    }

    private static string CompactMeetingText(string value)
    {
        return new string(value
            .Where(ch => !char.IsWhiteSpace(ch))
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool ContainsIndexedAlias(string value, string prefix, int number)
    {
        var alias = $"{prefix}{number}";
        var start = 0;
        while (start < value.Length)
        {
            var index = value.IndexOf(alias, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var after = index + alias.Length;
            if (after >= value.Length || !char.IsDigit(value[after]))
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    private static bool ContainsChinesePlayerNumber(string value, int number)
    {
        var alias = $"{number}号";
        var index = value.IndexOf(alias, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index == 0 || !char.IsDigit(value[index - 1]))
            {
                return true;
            }

            index = value.IndexOf(alias, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool ContainsColorAlias(string value, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        var index = value.IndexOf(alias, StringComparison.Ordinal);
        while (index >= 0)
        {
            var beforeIsLatinOrDigit = index > 0 && char.IsLetterOrDigit(value[index - 1]) && value[index - 1] <= 127;
            var after = index + alias.Length;
            var afterIsLatinOrDigit = after < value.Length && char.IsLetterOrDigit(value[after]) && value[after] <= 127;
            if (!beforeIsLatinOrDigit && !afterIsLatinOrDigit)
            {
                return true;
            }

            index = value.IndexOf(alias, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    public static bool CanReleasePostMurderMovement(
        float now,
        float movementUnlockAt,
        bool meetingOrExileActive)
    {
        return now >= movementUnlockAt && !meetingOrExileActive;
    }

    public static float AdvanceVirtualKillCooldown(
        float currentCooldown,
        float previouslyObservedCooldown,
        float elapsedSeconds)
    {
        if (currentCooldown <= 0f)
        {
            return 0f;
        }

        var nativeTimerAdvanced =
            elapsedSeconds > 0f &&
            currentCooldown < previouslyObservedCooldown - 0.02f;
        return nativeTimerAdvanced
            ? currentCooldown
            : Mathf.Max(0f, currentCooldown - Mathf.Max(0f, elapsedSeconds));
    }

    public static bool ShouldRewriteUnsupportedMurderFact(
        string? line,
        bool hasPersonalWitness,
        bool referencedTargetMatchesWitness)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] unsupportedFactPhrases =
        [
            "你杀人的", "把你杀人", "你的杀人", "你杀了", "你刀了", "你刚杀", "你行凶",
            "你动刀", "你杀完", "亲眼看见你", "亲眼看到你", "我看见你杀", "我看到你杀"
        ];
        var containsUnsupportedFact = unsupportedFactPhrases.Any(phrase =>
            line.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        return IsMurderRouteAssumption(line) ||
               (containsUnsupportedFact && (!hasPersonalWitness || !referencedTargetMatchesWitness));
    }

    public static bool ShouldRewriteSelfIncriminatingSecret(
        string? line,
        bool mustConcealHiddenActions)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] directRoleConfessions =
        [
            "我是内鬼", "我就是内鬼", "我是狼", "我就是狼", "我认狼", "我自爆",
            "i am the impostor", "i'm the impostor", "im the impostor", "i am impostor",
            "i'm impostor", "im impostor"
        ];
        if (directRoleConfessions.Any(phrase =>
                line.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!mustConcealHiddenActions)
        {
            return false;
        }

        string[] firstPersonSecretActions =
        [
            "我杀了", "我刚杀", "我杀掉", "我刀了", "我刚刀", "我动的手", "我行凶",
            "我咬了", "我下毒", "我毒了", "我炸了", "我放了炸弹", "我清了尸体",
            "我清尸", "我吃了尸体", "我腐蚀了", "我破坏了", "我关了灯", "我拉了电",
            "i killed", "i murdered", "i stabbed", "i bit", "i poisoned", "i bombed",
            "i planted the bomb", "i sabotaged", "i cleaned the body", "i ate the body"
        ];
        if (firstPersonSecretActions.Any(phrase =>
                line.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var trimmed = line.TrimStart();
        var implicitFirstPersonCrime =
            (trimmed.StartsWith("刚", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("刚才", StringComparison.OrdinalIgnoreCase)) &&
            (trimmed.Contains("杀了", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("刀了", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("咬了", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("下毒", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("放了炸弹", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("清尸", StringComparison.OrdinalIgnoreCase));
        var asksWhetherTheCrimeWasSeen =
            (line.Contains("看到我", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("看见我", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("see me", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("saw me", StringComparison.OrdinalIgnoreCase)) &&
            (line.Contains("杀", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("刀", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("kill", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("murder", StringComparison.OrdinalIgnoreCase));
        return implicitFirstPersonCrime || asksWhetherTheCrimeWasSeen;
    }

    public static bool IsMurderRouteAssumption(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] phrases =
        [
            "杀人的路线", "杀人路线", "杀完人的路线", "作案路线", "行凶路线", "凶手路线"
        ];
        return phrases.Any(phrase => line.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldReconsiderBotMeetingLine(
        bool directlyAddressesBot,
        bool mentionsCurrentCandidate)
    {
        return directlyAddressesBot || mentionsCurrentCandidate;
    }

    public static void LogSelfTest(ManualLogSource log)
    {
        var distributionOk =
            SelectDistributedIndex(1, 0, 3) != SelectDistributedIndex(2, 0, 3) &&
            SelectDistributedIndex(2, 0, 3) != SelectDistributedIndex(3, 0, 3);
        var dualPanelAssignments = Enumerable.Range(1, 5)
            .Select(playerId => SelectDistributedIndex((byte)playerId, (int)TaskTypes.RestoreOxy, 2))
            .ToArray();
        var dualPanelSplitValid =
            dualPanelAssignments.Count(index => index == 0) is 2 or 3 &&
            dualPanelAssignments.Count(index => index == 1) is 2 or 3;
        var validArrival = IsDestinationReached(
            new Vector2(0.82f, 0f),
            Vector2.zero,
            new Vector2(0.8f, 0f),
            0.9f);
        var wrongRoomRejected = !IsDestinationReached(
            new Vector2(0f, 3f),
            Vector2.zero,
            new Vector2(0f, 3f),
            0.9f);
        var movingTargetRefresh =
            ShouldRefreshMovingTarget(Vector2.zero, new Vector2(0.8f, 0f), 0.75f) &&
            !ShouldRefreshMovingTarget(Vector2.zero, new Vector2(0.2f, 0f), 0.75f);
        var routeDiversityValid =
            Mathf.Approximately(RuntimeSkeldGrid.RouteDiversityPenaltyForUseCount(0), 0f) &&
            RuntimeSkeldGrid.RouteDiversityPenaltyForUseCount(1) > 0f &&
            Mathf.Approximately(
                RuntimeSkeldGrid.RouteDiversityPenaltyForUseCount(9),
                RuntimeSkeldGrid.RouteDiversityPenaltyForUseCount(3));
        var personalitiesValid = BotPersonalityCatalog.Validate();
        var bodyRangeValid =
            DeadBodyPerception.IsWithinRange(2.5f, 2.5f) &&
            !DeadBodyPerception.IsWithinRange(2.51f, 2.5f) &&
            DeadBodyPerception.CanBypassCloseRangeOcclusion(0.25f, 2.5f) &&
            !DeadBodyPerception.CanBypassCloseRangeOcclusion(0.8f, 2.5f);
        var murderFairnessValid =
            ScoreMurderCandidate(4f, 0, true, false, 0f) >
            ScoreMurderCandidate(4f, 0, false, false, 0f) &&
            ScoreMurderCandidate(4f, 0, false, true, 0f) <
            ScoreMurderCandidate(4f, 0, false, false, 0f);
        var recentSightMurderPlanningValid =
            ShouldUseRecentSightForMurderPlan(true, float.MaxValue, 14f) &&
            ShouldUseRecentSightForMurderPlan(false, 6f, 14f) &&
            !ShouldUseRecentSightForMurderPlan(false, 14.1f, 14f);
        var threatApproachValid =
            IsPersistentApproach(3.4f, 2.8f, 0.5f, 2, 3.2f) &&
            !IsPersistentApproach(2.8f, 2.9f, 0.5f, 2, 3.2f);
        var murderExecutionValid =
            ShouldExecuteMurder("low", false) &&
            ShouldExecuteMurder("crowded", true) &&
            !ShouldExecuteMurder("crowded", false) &&
            !ShouldExecuteMurder("witnessed", true);
        var virtualKillCooldownValid =
            Mathf.Approximately(AdvanceVirtualKillCooldown(10f, 10f, 1f), 9f) &&
            Mathf.Approximately(AdvanceVirtualKillCooldown(9f, 10f, 1f), 9f) &&
            Mathf.Approximately(AdvanceVirtualKillCooldown(0.5f, 0.5f, 1f), 0f);
        var ghostDirectRouteValid =
            ShouldUseDirectGhostTaskRoute(true, true) &&
            !ShouldUseDirectGhostTaskRoute(false, true) &&
            !ShouldUseDirectGhostTaskRoute(true, false);
        var activePlayGateValid =
            ShouldStartActivePlayWindow(true, true) &&
            !ShouldStartActivePlayWindow(true, false) &&
            !ShouldStartActivePlayWindow(false, true);
        var stableActionWindowValid =
            !HasStableActionWindow(true, 10f, 11.49f, 1.5f) &&
            HasStableActionWindow(true, 10f, 11.5f, 1.5f) &&
            !HasStableActionWindow(false, 10f, 20f, 1.5f) &&
            !HasStableActionWindow(true, 0f, 20f, 1.5f);
        var impostorOpeningCoverValid =
            ShouldUseOpeningFakeTask(true, false) &&
            !ShouldUseOpeningFakeTask(true, true) &&
            !ShouldUseOpeningFakeTask(false, false);
        var independentRoundResumeValid =
            ShouldKeepRoundResumePending(false) &&
            !ShouldKeepRoundResumePending(true);
        var witnessedMeetingEvidenceValid =
            ShouldUseWitnessedKillerInMeeting(true, true, false, false) &&
            !ShouldUseWitnessedKillerInMeeting(false, true, false, false) &&
            !ShouldUseWitnessedKillerInMeeting(true, false, false, false) &&
            !ShouldUseWitnessedKillerInMeeting(true, true, true, false) &&
            !ShouldUseWitnessedKillerInMeeting(true, true, false, true);
        var roleObjectiveOverrideValid =
            ShouldOverrideForRoleObjective("Arsonist", true) &&
            ShouldOverrideForRoleObjective("Vulture", true) &&
            ShouldOverrideForRoleObjective("Engineer", true) &&
            !ShouldOverrideForRoleObjective("Arsonist", false) &&
            !ShouldOverrideForRoleObjective("Sheriff", true);
        var miraDeconCapacityValid =
            !ShouldWaitForMiraDeconCapacity(true, 4, 2) &&
            !ShouldWaitForMiraDeconCapacity(false, 1, 2) &&
            ShouldWaitForMiraDeconCapacity(false, 2, 2);
        var bodyEmergencyPriorityValid =
            ShouldPrioritizeVisibleBody(true, true) &&
            ShouldPrioritizeVisibleBody(false, false) &&
            !ShouldPrioritizeVisibleBody(false, true);
        var bodyObservationPolicyValid =
            !ShouldImpostorReportBody(true, 0, 1f, 0f) &&
            ShouldImpostorReportBody(true, 2, 0f, 1f) &&
            ShouldImpostorReportBody(false, 1, 1f, 0.7f) &&
            GetBodyObservationDelay(0f, false, false) >= 1.5f &&
            GetBodyObservationDelay(1f, true, false) <= 3.4f &&
            GetBodyObservationDelay(1f, true, true) <= 1.7f;
        var roomRuleTypesValid = GameRuleSettings.OptionTypesResolved;
        var independentEmergencyValid =
            ShouldRespondToEmergency(true, false, 0.8f, 0, 0f, 0.5f) &&
            !ShouldRespondToEmergency(false, true, 0.3f, 2, 0f, 0.5f) &&
            ShouldRespondToEmergency(true, false, 0.05f, 6, 4f, 1f) &&
            ShouldRespondToEmergency(false, false, 0.05f, 6, 8f, 1f) &&
            !ShouldRespondToEmergency(false, true, 0.05f, 6, 20f, 1f);
        var meetingThresholdsValid =
            GetMeetingVoteConfidenceThreshold(0.9f) <
            GetMeetingVoteConfidenceThreshold(0.1f);
        var groundedMeetingCommitValid =
            ShouldCommitMeetingCandidate(true, false, true, false, 0.65f, 0.66f, 1.22f, 1.20f) &&
            !ShouldCommitMeetingCandidate(true, false, true, false, 0.95f, 0.66f, 0.35f, 1.20f) &&
            ShouldCommitMeetingCandidate(true, false, false, false, 0.70f, 0.66f, 0f, 1.20f) &&
            !ShouldCommitMeetingCandidate(true, false, false, false, 0.60f, 0.66f, 2f, 1.20f) &&
            ShouldCommitMeetingCandidate(true, false, true, true, 0.10f, 0.90f, 0f, 1.20f) &&
            !ShouldCommitMeetingCandidate(true, true, true, true, 1f, 0f, 2f, 0f);
        var multiStageReadinessValid =
            ShouldSelectStrategicAbilityRole(false, false, true) &&
            CanContinueAbilityThroughReadinessGate(false, true, true, "role") &&
            !CanContinueAbilityThroughReadinessGate(false, true, false, "hold") &&
            !CanContinueAbilityThroughReadinessGate(false, false, true, "role");
        var guesserEvidenceGateValid =
            ShouldAttemptGuesserInference("NiceGuesser", "impostor", 0.96f, 8f) &&
            !ShouldAttemptGuesserInference("NiceGuesser", "crewmate", 0.98f, 8f) &&
            ShouldAttemptGuesserInference("EvilGuesser", "crewmate", 0.97f, 12f) &&
            !ShouldAttemptGuesserInference("EvilGuesser", "impostor", 0.97f, 12f) &&
            !ShouldAttemptGuesserInference("NiceGuesser", "neutral", 0.89f, 2f) &&
            !ShouldAttemptGuesserInference("EvilGuesser", "neutral", 0.99f, 60f);
        var meetingReconsiderationValid =
            HasUnanalyzedMeetingTranscript(1, 2) &&
            !HasUnanalyzedMeetingTranscript(2, 2);
        var meetingAliasesValid =
            MentionsPlayerAlias("投bot2", 7, "DeepBot 2") &&
            MentionsPlayerAlias("我怀疑 2号", 7, "DeepBot 2") &&
            MentionsPlayerAlias("粉色是内鬼", 7, "DeepBot 2", 3, "粉红色") &&
            MentionsPlayerAlias("pink is sus", 7, "DeepBot 2", 3, "Pink") &&
            !MentionsPlayerAlias("粉色是内鬼", 8, "DeepBot 3", 1, "蓝色") &&
            !MentionsPlayerAlias("投bot3", 7, "DeepBot 2") &&
            !MentionsPlayerAlias("投bot20", 7, "DeepBot 2");
        const string addressedClaim = "Alpha，我看到粉色杀人";
        var addressedIndex = FindPlayerAliasIndex(addressedClaim, 1, "Alpha", 1, "蓝色");
        var claimedTargetIndex = FindPlayerAliasIndex(addressedClaim, 7, "DeepBot 2", 3, "粉红色");
        var claimActionIndex = CompactMeetingText(addressedClaim).IndexOf("杀人", StringComparison.Ordinal);
        var addressedClaimTargetingValid =
            addressedIndex >= 0 && claimedTargetIndex >= 0 && claimActionIndex >= 0 &&
            Math.Abs(claimedTargetIndex - claimActionIndex) < Math.Abs(addressedIndex - claimActionIndex);
        var meetingResponderSchedulingValid =
            MeetingResponderOrder(1, 1) != MeetingResponderOrder(2, 1) &&
            MeetingResponderOrder(1, 1) != MeetingResponderOrder(1, 2);
        var visionDistanceRepairValid =
            !ShouldRepairVisionDistance(2f, 2.02f) &&
            !ShouldRepairVisionDistance(5f, 4.95f) &&
            ShouldRepairVisionDistance(5f, 2f) &&
            ShouldRepairVisionDistance(float.NaN, 2f) &&
            !ShouldRepairVisionDistance(2f, float.NaN);
        var hostViewRestoreValid =
            ShouldRestoreHostView(true, false, false) &&
            ShouldRestoreHostView(true, true, true) &&
            !ShouldRestoreHostView(true, true, false) &&
            !ShouldRestoreHostView(false, false, true);
        var postMurderEscapeGateValid =
            !CanReleasePostMurderMovement(0.5f, 0.9f, false) &&
            !CanReleasePostMurderMovement(1f, 0.9f, true) &&
            CanReleasePostMurderMovement(1f, 0.9f, false);
        var configurableAppearanceValid =
            DeepBotAppearance.ResolveName(0, 0) == "DeepBot 1" &&
            DeepBotAppearance.ResolveName(0, 1) == "Alpha" &&
            DeepBotIdentity.IsReservedClientId(64) &&
            DeepBotIdentity.IsReservedClientId(95) &&
            !DeepBotIdentity.IsReservedClientId(63) &&
            !DeepBotIdentity.IsReservedClientId(96);
        var privateEvidenceBoundaryValid =
            ShouldRewriteUnsupportedMurderFact("把你杀人的路线说一下", false, false) &&
            ShouldRewriteUnsupportedMurderFact("说一下你的作案路线", true, true) &&
            ShouldRewriteUnsupportedMurderFact("我看到你杀人了", true, false) &&
            !ShouldRewriteUnsupportedMurderFact("我怀疑你，请解释路线", false, false) &&
            !ShouldRewriteUnsupportedMurderFact("我亲眼看到你杀人", true, true) &&
            ShouldReconsiderBotMeetingLine(true, false) &&
            ShouldReconsiderBotMeetingLine(false, true) &&
            !ShouldReconsiderBotMeetingLine(false, false);
        var hiddenRoleSecrecyValid =
            ShouldRewriteSelfIncriminatingSecret("刚在电房杀了Nova(8)，尸体就在控制台旁。有人看到我了？", true) &&
            ShouldRewriteSelfIncriminatingSecret("我是内鬼，这把投我", true) &&
            ShouldRewriteSelfIncriminatingSecret("I killed Nova. Did anyone see me?", true) &&
            !ShouldRewriteSelfIncriminatingSecret("我亲眼看到Nova杀人", true) &&
            !ShouldRewriteSelfIncriminatingSecret("我怀疑Nova，请解释路线", true) &&
            !ShouldRewriteSelfIncriminatingSecret("我是警长，我刚才开枪了", false);
        var level = distributionOk &&
                    dualPanelSplitValid &&
                    validArrival &&
                    wrongRoomRejected &&
                    movingTargetRefresh &&
                    routeDiversityValid &&
                    personalitiesValid &&
                    bodyRangeValid &&
                    murderFairnessValid &&
                    recentSightMurderPlanningValid &&
                    threatApproachValid &&
                    murderExecutionValid &&
                    virtualKillCooldownValid &&
                    ghostDirectRouteValid &&
                    activePlayGateValid &&
                    stableActionWindowValid &&
                    impostorOpeningCoverValid &&
                    independentRoundResumeValid &&
                    witnessedMeetingEvidenceValid &&
                    roleObjectiveOverrideValid &&
                    miraDeconCapacityValid &&
                    bodyEmergencyPriorityValid &&
                    bodyObservationPolicyValid &&
                    roomRuleTypesValid &&
                    independentEmergencyValid &&
                    meetingThresholdsValid &&
                    groundedMeetingCommitValid &&
                    multiStageReadinessValid &&
                    guesserEvidenceGateValid &&
                     meetingReconsiderationValid &&
                     meetingAliasesValid &&
                     addressedClaimTargetingValid &&
                     meetingResponderSchedulingValid &&
                    visionDistanceRepairValid &&
                    hostViewRestoreValid &&
                    postMurderEscapeGateValid &&
                    configurableAppearanceValid &&
                    privateEvidenceBoundaryValid &&
                    hiddenRoleSecrecyValid
            ? "ok"
            : "warning";
        log.LogInfo(
            $"DeepBot behavior self-test: level={level}, distributedTasks={distributionOk}, " +
            $"dualPanelSplit={dualPanelSplitValid}, " +
            $"validArrival={validArrival}, wrongRoomRejected={wrongRoomRejected}, " +
            $"movingTargetRefresh={movingTargetRefresh}, routeDiversity={routeDiversityValid}, " +
            $"personalities={personalitiesValid}, " +
            $"bodyReportRange={bodyRangeValid}, murderFairness={murderFairnessValid}, " +
            $"recentSightMurderPlanning={recentSightMurderPlanningValid}, " +
            $"threatApproach={threatApproachValid}, murderExecution={murderExecutionValid}, " +
            $"virtualKillCooldown={virtualKillCooldownValid}, ghostDirectRoute={ghostDirectRouteValid}, " +
            $"activePlayGate={activePlayGateValid}, stableActionWindow={stableActionWindowValid}, " +
            $"impostorOpeningCover={impostorOpeningCoverValid}, independentRoundResume={independentRoundResumeValid}, " +
            $"witnessedMeetingEvidence={witnessedMeetingEvidenceValid}, " +
            $"roleObjectiveOverride={roleObjectiveOverrideValid}, " +
            $"miraDeconCapacity={miraDeconCapacityValid}, " +
            $"bodyEmergencyPriority={bodyEmergencyPriorityValid}, " +
            $"bodyObservationPolicy={bodyObservationPolicyValid}, " +
            $"roomRuleTypes={roomRuleTypesValid}, " +
            $"independentEmergency={independentEmergencyValid}, meetingThresholds={meetingThresholdsValid}, " +
            $"groundedMeetingCommit={groundedMeetingCommitValid}, " +
            $"multiStageReadiness={multiStageReadinessValid}, " +
            $"guesserEvidenceGate={guesserEvidenceGateValid}, " +
            $"meetingReconsideration={meetingReconsiderationValid}, meetingAliases={meetingAliasesValid}, " +
            $"addressedClaimTargeting={addressedClaimTargetingValid}, " +
            $"humanReactionScheduling={meetingResponderSchedulingValid}, visionDistanceRepair={visionDistanceRepairValid}, " +
            $"hostViewRestore={hostViewRestoreValid}, postMurderEscapeGate={postMurderEscapeGateValid}, " +
            $"configurableAppearance={configurableAppearanceValid}, privateEvidenceBoundary={privateEvidenceBoundaryValid}, " +
            $"hiddenRoleSecrecy={hiddenRoleSecrecyValid}");
    }
}

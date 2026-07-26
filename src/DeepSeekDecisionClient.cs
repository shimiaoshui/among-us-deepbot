using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace AmongUsDeepSeekBots;

internal sealed class DeepSeekDecisionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http = new();
    private readonly Func<string> _model;
    private readonly Func<string> _apiBaseUrl;
    private readonly Func<string?> _apiKey;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _rateLimitGate = new();
    private DateTimeOffset _requestBackoffUntil;
    private DateTimeOffset _nextBackoffStatusLogAt;
    private int _consecutiveRateLimits;

    public DeepSeekDecisionClient(Func<string> model, Func<string> apiBaseUrl, Func<string?> apiKey, Action<string> log)
    {
        _model = model;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
        _log = log;
        // Agnes reasoning models may spend a sizeable part of the response budget on
        // reasoning_content before producing the final JSON. Twenty seconds was too
        // short for real meeting prompts and caused every follow-up to fall back.
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<BotActionDecision?> GetActionAsync(BotActionPrompt prompt, CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        if (ShouldSkipForSharedBackoff("action")) return null;

        var request = new
        {
            model = _model(),
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = JsonSerializer.Serialize(prompt, JsonOptions) }
            },
            max_tokens = 2048,
            temperature = 0.55,
            response_format = new { type = "json_object" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NormalizeEndpoint(_apiBaseUrl()));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await SendWithSharedRateGuardAsync(httpRequest, "action", cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log($"DeepSeek action request failed: HTTP {(int)response.StatusCode}, body={Truncate(body, 240)}");
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        var choice = envelope?.Choices is { Length: > 0 } ? envelope.Choices[0] : null;
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            LogMissingFinalContent("action", choice);
            return null;
        }

        if (TryDeserializeActionDecision(content, prompt, out var decision, out var parseError))
        {
            return decision;
        }

        if (TryExtractJsonObject(content, out var json))
        {
            if (TryDeserializeActionDecision(json, prompt, out decision, out var extractedError))
            {
                return decision;
            }

            _log($"DeepSeek action extracted JSON parse failed: {extractedError}, body={Truncate(json, 240)}");
            return null;
        }

        _log($"DeepSeek action JSON parse failed: {parseError}, body={Truncate(content, 240)}");
        return null;
    }

    private bool TryDeserializeActionDecision(
        string json,
        BotActionPrompt prompt,
        out BotActionDecision? decision,
        out string error)
    {
        decision = null;
        error = string.Empty;
        try
        {
            decision = JsonSerializer.Deserialize<BotActionDecision>(json, JsonOptions);
            return decision is not null;
        }
        catch (Exception original)
        {
            if (!TryNormalizeVisiblePlayerId(
                    json,
                    "target_player_id",
                    prompt.Observation.VisiblePlayers,
                    out var normalized,
                    out var resolvedId))
            {
                error = original.Message;
                return false;
            }

            try
            {
                decision = JsonSerializer.Deserialize<BotActionDecision>(normalized, JsonOptions);
                if (decision is null)
                {
                    error = "normalized action decision was empty";
                    return false;
                }

                _log(
                    $"DeepSeek action target name safely resolved from the bot's visible roster: " +
                    $"bot={prompt.BotName}({prompt.BotId}), targetPlayerId={resolvedId}.");
                return true;
            }
            catch (Exception normalizedError)
            {
                error = normalizedError.Message;
                return false;
            }
        }
    }

    private static bool TryNormalizeVisiblePlayerId(
        string json,
        string propertyName,
        string visiblePlayers,
        out string normalizedJson,
        out int resolvedId)
    {
        normalizedJson = json;
        resolvedId = -1;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root ||
                !root.TryGetPropertyValue(propertyName, out var targetNode) ||
                targetNode is not JsonValue targetValue ||
                !targetValue.TryGetValue<string>(out var targetToken) ||
                string.IsNullOrWhiteSpace(targetToken) ||
                !TryResolveVisiblePlayerId(targetToken, visiblePlayers, out resolvedId))
            {
                return false;
            }

            root[propertyName] = resolvedId;
            normalizedJson = root.ToJsonString(JsonOptions);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveVisiblePlayerId(string token, string visiblePlayers, out int playerId)
    {
        playerId = -1;
        if (int.TryParse(token.Trim(), out var numericId) && numericId is >= byte.MinValue and <= byte.MaxValue)
        {
            playerId = numericId;
            return true;
        }

        var matches = Regex.Matches(
                visiblePlayers ?? string.Empty,
                @"(?:^|;\s*)(?<name>.*?)\((?<id>\d{1,3})\)(?:\s|;|$)")
            .Cast<Match>()
            .Where(match => string.Equals(
                match.Groups["name"].Value.Trim(),
                token.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .Select(match => int.TryParse(match.Groups["id"].Value, out var id) ? id : -1)
            .Where(id => id is >= byte.MinValue and <= byte.MaxValue)
            .Distinct()
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        playerId = matches[0];
        return true;
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        const string source = "{\"action\":\"shadow\",\"target_player_id\":\"Alpha\",\"confidence\":0.8}";
        var visibleNameResolves = TryNormalizeVisiblePlayerId(
            source,
            "target_player_id",
            "Alpha(2) dist=1.5; Nova(8) dist=3.0",
            out var normalized,
            out var resolvedId) &&
            resolvedId == 2 &&
            JsonNode.Parse(normalized)?["target_player_id"]?.GetValue<int>() == 2;
        var hiddenNameRejected = !TryNormalizeVisiblePlayerId(
            source,
            "target_player_id",
            "Nova(8) dist=3.0",
            out _,
            out _);
        const string truncatedMeeting =
            "{\"message\":\"我看到红色跳管\",\"vote_player_id\":2,\"skip_vote\":false,\"reason\":\"unfinished";
        var completeLeadingMeetingFieldsRecovered = TryRecoverTruncatedMeetingDecision(
            truncatedMeeting,
            "0,2,8",
            out var recoveredMeeting) &&
            recoveredMeeting?.VotePlayerId == 2 &&
            recoveredMeeting.Message == "我看到红色跳管";
        var illegalRecoveredVoteRejected = !TryRecoverTruncatedMeetingDecision(
            truncatedMeeting.Replace("\"vote_player_id\":2", "\"vote_player_id\":7", StringComparison.Ordinal),
            "0,2,8",
            out _);
        var illegalCompleteVoteRejected = EnforceLegalMeetingVote(
            new BotMeetingDecision("test", 7, false, "test", 0.9f, null, "none"),
            "0,2,8") is { SkipVote: true, VotePlayerId: null };
        log.LogInfo(
            $"DeepBot LLM decision parser self-test: " +
            $"level={(visibleNameResolves && hiddenNameRejected && completeLeadingMeetingFieldsRecovered && illegalRecoveredVoteRejected && illegalCompleteVoteRejected ? "ok" : "error")}, " +
            $"visibleNameResolves={visibleNameResolves}, hiddenNameRejected={hiddenNameRejected}, " +
            $"truncatedMeetingRecovered={completeLeadingMeetingFieldsRecovered}, " +
            $"illegalRecoveredVoteRejected={illegalRecoveredVoteRejected}, " +
            $"illegalCompleteVoteRejected={illegalCompleteVoteRejected}.");
    }

    public async Task<BotMeetingDecision?> GetMeetingDecisionAsync(BotMeetingPrompt prompt, CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        if (ShouldSkipForSharedBackoff("meeting")) return null;

        var request = new
        {
            model = _model(),
            messages = new object[]
            {
                new { role = "system", content = BuildMeetingSystemPrompt() },
                new { role = "user", content = JsonSerializer.Serialize(prompt, JsonOptions) }
            },
            // The response is a short JSON decision. A very large allowance
            // encouraged long hidden reasoning and caused concurrent bots to
            // time out before answering the latest human statement.
            max_tokens = 2048,
            temperature = BotPersonalityCatalog.ForPlayer(prompt.BotId).MeetingTemperature,
            response_format = new { type = "json_object" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NormalizeEndpoint(_apiBaseUrl()));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await SendWithSharedRateGuardAsync(httpRequest, "meeting", cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log($"DeepSeek meeting request failed: HTTP {(int)response.StatusCode}, body={Truncate(body, 240)}");
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        var choice = envelope?.Choices is { Length: > 0 } ? envelope.Choices[0] : null;
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            LogMissingFinalContent("meeting", choice);
            return null;
        }

        try
        {
            var decision = JsonSerializer.Deserialize<BotMeetingDecision>(content, JsonOptions);
            return EnforceLegalMeetingVote(decision, prompt.LegalVotePlayerIds);
        }
        catch (Exception ex)
        {
            if (TryExtractJsonObject(content, out var json))
            {
                try
                {
                    var decision = JsonSerializer.Deserialize<BotMeetingDecision>(json, JsonOptions);
                    return EnforceLegalMeetingVote(decision, prompt.LegalVotePlayerIds);
                }
                catch (Exception inner)
                {
                    if (TryRecoverTruncatedMeetingDecision(json, prompt.LegalVotePlayerIds, out var recovered))
                    {
                        _log(
                            $"DeepSeek meeting response recovered from complete leading JSON fields: " +
                            $"bot={prompt.BotName}({prompt.BotId}), vote={recovered!.VotePlayerId?.ToString() ?? "skip"}.");
                        return recovered;
                    }
                    _log($"DeepSeek meeting extracted JSON parse failed: {inner.Message}, body={Truncate(json, 240)}");
                    return null;
                }
            }

            if (TryRecoverTruncatedMeetingDecision(content, prompt.LegalVotePlayerIds, out var truncatedDecision))
            {
                _log(
                    $"DeepSeek meeting response recovered after token truncation: " +
                    $"bot={prompt.BotName}({prompt.BotId}), vote={truncatedDecision!.VotePlayerId?.ToString() ?? "skip"}.");
                return truncatedDecision;
            }

            _log($"DeepSeek meeting JSON parse failed: {ex.Message}, body={Truncate(content, 240)}");
            return null;
        }
    }

    private static BotMeetingDecision? EnforceLegalMeetingVote(
        BotMeetingDecision? decision,
        string legalVotePlayerIds)
    {
        if (decision is null)
        {
            return null;
        }

        var legalIds = Regex.Matches(legalVotePlayerIds ?? string.Empty, @"\d{1,3}")
            .Cast<Match>()
            .Select(match => int.TryParse(match.Value, out var id) ? id : -1)
            .Where(id => id is >= byte.MinValue and <= byte.MaxValue)
            .ToHashSet();
        if (decision.SkipVote || !decision.VotePlayerId.HasValue)
        {
            return decision with { VotePlayerId = null, SkipVote = true };
        }

        return legalIds.Contains(decision.VotePlayerId.Value)
            ? decision
            : decision with
            {
                VotePlayerId = null,
                SkipVote = true,
                Reason = "Rejected a vote target outside the authoritative legal meeting candidate set.",
                Confidence = Math.Min(decision.Confidence, 0.45f)
            };
    }

    private static bool TryRecoverTruncatedMeetingDecision(
        string content,
        string legalVotePlayerIds,
        out BotMeetingDecision? decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(content) ||
            !content.TrimStart().StartsWith("{", StringComparison.Ordinal) ||
            !TryExtractCompleteJsonStringField(content, "message", out var message) ||
            !TryExtractJsonBooleanField(content, "skip_vote", out var skipVote))
        {
            return false;
        }

        var legalIds = Regex.Matches(legalVotePlayerIds ?? string.Empty, @"\d{1,3}")
            .Cast<Match>()
            .Select(match => int.TryParse(match.Value, out var id) ? id : -1)
            .Where(id => id is >= byte.MinValue and <= byte.MaxValue)
            .ToHashSet();
        int? voteId = null;
        if (TryExtractJsonIntegerField(content, "vote_player_id", out var parsedVoteId) &&
            parsedVoteId.HasValue &&
            legalIds.Contains(parsedVoteId.Value))
        {
            voteId = parsedVoteId.Value;
        }

        if (!skipVote && !voteId.HasValue)
        {
            return false;
        }

        var confidence = TryExtractJsonFloatField(content, "confidence", out var parsedConfidence)
            ? Math.Clamp(parsedConfidence, 0f, 1f)
            : 0.55f;
        decision = new BotMeetingDecision(
            message,
            skipVote ? null : voteId,
            skipVote,
            "recovered only from complete leading fields of a truncated model response",
            confidence);
        return true;
    }

    private static bool TryExtractCompleteJsonStringField(string content, string fieldName, out string value)
    {
        value = string.Empty;
        var match = Regex.Match(
            content,
            $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\"") ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractJsonBooleanField(string content, string fieldName, out bool value)
    {
        value = false;
        var match = Regex.Match(content, $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(?<value>true|false)", RegexOptions.IgnoreCase);
        return match.Success && bool.TryParse(match.Groups["value"].Value, out value);
    }

    private static bool TryExtractJsonIntegerField(string content, string fieldName, out int? value)
    {
        value = null;
        var match = Regex.Match(content, $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(?<value>null|-?\\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        if (string.Equals(match.Groups["value"].Value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!int.TryParse(match.Groups["value"].Value, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryExtractJsonFloatField(string content, string fieldName, out float value)
    {
        value = 0f;
        var match = Regex.Match(content, $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)");
        return match.Success &&
               float.TryParse(
                   match.Groups["value"].Value,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }

    public async Task<BotAbilityDecision?> GetAbilityDecisionAsync(
        BotAbilityPrompt prompt,
        CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        if (ShouldSkipForSharedBackoff("ability")) return null;

        var request = new
        {
            model = _model(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
You are deciding whether one Among Us player should use their current role ability now. Output JSON only.
Respect the exact ability purpose, current phase, room settings, cooldowns, visibility, and private timeline supplied in the prompt. Do not use an ability merely because it is ready.
Before choosing, internally perform a compact state-machine check: (1) actual faction/independent win objective, (2) native legality and current sequence stage, (3) target evidence and witness risk, (4) the immediate follow-up route/cover/escape, and (5) one concrete abort condition. Never expose chain-of-thought; return only the compact plan fields below.
Multi-stage abilities must preserve their stage order. Sampling/marking/placing/cursing/biting/planting is not success by itself: plan the required continuation, wait, escape, meeting resolution, or inherited objective. Never restart stage one while a valid later stage is pending, and abort safely if the target dies, becomes hidden, leaves legal range, a meeting starts, or the native rule changes.
Use private memory and currently visible players only. Never infer hidden roles unavailable to this player.
If KnownRoleInformation identifies a living Lover partner, never choose a hostile role ability against that partner. This is a hard rule, not a preference.
Engineer vents are for meaningful shortcuts, escape, or emergency rotation. Tracker marks a useful trusted or suspicious player. Guardian Angel protects a player likely to be endangered. Phantom invisibility conceals movement or escapes witnesses. Shapeshifter disguises before deception or a planned kill while unobserved. Detective investigates a genuinely useful suspect. Impostor vents are for covert escape or repositioning, not random travel.
target_player_id must be a currently legal visible target or null.
ability_action must be "role", "vent", or "hold". Use "role" for the named active skill, "vent" only when vent access is listed and it serves a concrete shortcut/escape/ambush purpose, and "hold" when no skill should be used now.
For Vulture, a visible consumable body is the primary objective: choose role/eat instead of report. Hold only for a concrete nearby-witness risk, then reassess quickly; ordinary reporting is illegal for this role. Engineer conserves finite repairs and obeys configured vent time/charges. Medic shields an exposed useful or credibly trusted player. Sheriff and Deputy require evidence, not proximity. Tracker selects a person whose later route answers a question. TimeMaster shields only credible nearby danger. Hacker chooses the information source that resolves a real uncertainty. Medium approaches a usable soul and retains only TOR-provided information.
For Vampire, bite only an isolated target and immediately plan an escape while the victim later dies at their own position. Warlock must choose a mobile curse carrier and a later legal second target. Ninja must mark, conceal, strike, and exit as one plan. Morphling and vanilla Shapeshifter must select/sample an identity, reach concealment, transform, then act under that cover. Portalmaker places two separated useful endpoints and uses the native timed portal only when the full entry-animation-exit route is better. Trickster places the configured number of separated boxes, waits for native conversion, then combines darkness and box vents with a concrete ambush or escape. Bomber plants only where traffic and timing support an intentional split or elimination, then clears the blast area. Yoyo marks, relocates, blinks, and accounts for the timed return.
Jackal decides between recruiting a useful Sidekick and later isolated kills; Sidekick obeys room kill/vent settings and protects the Jackal. Godfather kills while Mafioso builds cover until succession unlocks. BountyHunter prefers the bounty only when safe. Cleaner and Janitor clean only a visible nearby body and then leave or form cover. Arsonist channels every undoused living target before igniting. Pursuer blanks a concrete survival threat. Thief attempts only a strongly evidenced eligible hostile role because an illegal target is fatal. Eraser, Witch, and Shifter must account for next-meeting resolution. Trapper and SecurityGuard use separated informative chokepoints and map legality. Mayor, Swapper, Guesser, Lawyer, and Prosecutor reserve meeting actions for evidence and their actual objective. For all vent-capable roles, choose vent only when the route, ambush, or escape advantage outweighs being seen.
Output exactly: {"use":true|false,"ability_action":"role|vent|hold","target_player_id":number|null,"plan_goal":"short objective","next_stage":"one immediate follow-up","abort_if":"one concrete abort condition","recheck_seconds":1.0,"reason":"short strategic purpose","confidence":0.0}
"""
                },
                new { role = "user", content = JsonSerializer.Serialize(prompt, JsonOptions) }
            },
            max_tokens = 1536,
            temperature = 0.42,
            response_format = new { type = "json_object" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NormalizeEndpoint(_apiBaseUrl()));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await SendWithSharedRateGuardAsync(httpRequest, "ability", cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log($"DeepSeek ability request failed: HTTP {(int)response.StatusCode}, body={Truncate(body, 240)}");
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        var choice = envelope?.Choices is { Length: > 0 } ? envelope.Choices[0] : null;
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            LogMissingFinalContent("ability", choice);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BotAbilityDecision>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            if (TryExtractJsonObject(content, out var json))
            {
                try
                {
                    return JsonSerializer.Deserialize<BotAbilityDecision>(json, JsonOptions);
                }
                catch (Exception inner)
                {
                    _log($"DeepSeek ability extracted JSON parse failed: {inner.Message}, body={Truncate(json, 240)}");
                    return null;
                }
            }

            _log($"DeepSeek ability JSON parse failed: {ex.Message}, body={Truncate(content, 240)}");
            return null;
        }
    }

    public async Task<BotReflectionDecision?> GetReflectionAsync(
        BotReflectionPrompt prompt,
        CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (ShouldSkipForSharedBackoff("reflection")) return null;

        var request = new
        {
            model = _model(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
You are the post-match coach for one Among Us bot. Output JSON only.
Derive zero to three durable, reusable lessons from this bot's own timeline, its final win/loss, and roles revealed on the end screen.
Final revealed roles may be used only to evaluate whether an earlier judgment was correct. Never create a future skill that leaks player names, ids, hidden roles, exact map coordinates, or actions the bot could not know during play.
Prefer concrete mistakes such as voting without corroboration, helping vote out a Jester, shooting an illegal Sheriff target, killing into witnesses, failing to leave a body, wasting sabotage, ignoring an emergency, or repeating an already-known lesson.
Success may reinforce a genuinely novel strategy, but do not manufacture a lesson when the evidence is weak.
ExistingCoreSkills lists lessons already stored. If a new lesson has the same meaning, omit it entirely; local semantic deduplication is also applied after your response.
key must be stable lowercase dot notation, for example meeting.require_corrob_before_vote. category must be one of meeting, voting, murder, role_ability, sabotage, navigation, emergency, deception, survival, tasks.
Write principle, trigger, and action in concise Chinese without match-specific names.
Output exactly: {"summary":"one short Chinese outcome diagnosis","lessons":[{"key":"...","category":"...","principle":"...","trigger":"...","action":"..."}]}
"""
                },
                new { role = "user", content = JsonSerializer.Serialize(prompt, JsonOptions) }
            },
            max_tokens = 4096,
            temperature = 0.32,
            response_format = new { type = "json_object" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NormalizeEndpoint(_apiBaseUrl()));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await SendWithSharedRateGuardAsync(httpRequest, "reflection", cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log($"DeepSeek reflection request failed: HTTP {(int)response.StatusCode}, body={Truncate(body, 240)}");
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        var choice = envelope?.Choices is { Length: > 0 } ? envelope.Choices[0] : null;
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            LogMissingFinalContent("reflection", choice);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BotReflectionDecision>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            if (TryExtractJsonObject(content, out var json))
            {
                try
                {
                    return JsonSerializer.Deserialize<BotReflectionDecision>(json, JsonOptions);
                }
                catch (Exception inner)
                {
                    _log($"DeepSeek reflection extracted JSON parse failed: {inner.Message}, body={Truncate(json, 240)}");
                    return null;
                }
            }

            _log($"DeepSeek reflection JSON parse failed: {ex.Message}, body={Truncate(content, 240)}");
            return null;
        }
    }

    public static string? LoadHostApiKey()
    {
        var runtimeKey = Environment.GetEnvironmentVariable("AMONG_US_DEEPBOT_API_KEY")?.Trim();
        if (!string.IsNullOrWhiteSpace(runtimeKey) &&
            runtimeKey.StartsWith("sk-", StringComparison.Ordinal))
        {
            return runtimeKey;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmongUsDeepSeekBots");
        var path = Path.Combine(dir, "api-key.txt");
        if (!File.Exists(path))
        {
            return null;
        }

        var key = File.ReadAllText(path).Trim();
        return key.StartsWith("sk-", StringComparison.Ordinal) ? key : null;
    }

    private async Task<HttpResponseMessage?> SendWithSharedRateGuardAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        if (ShouldSkipForSharedBackoff(operation))
        {
            return null;
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ShouldSkipForSharedBackoff(operation))
            {
                return null;
            }

            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
            {
                RegisterRateLimit(response, operation);
            }
            else if (response.IsSuccessStatusCode)
            {
                lock (_rateLimitGate)
                {
                    _consecutiveRateLimits = 0;
                    _requestBackoffUntil = default;
                }
            }

            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private bool ShouldSkipForSharedBackoff(string operation)
    {
        lock (_rateLimitGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= _requestBackoffUntil)
            {
                return false;
            }

            if (now >= _nextBackoffStatusLogAt)
            {
                _nextBackoffStatusLogAt = now.AddSeconds(20);
                _log(
                    $"DeepSeek {operation} request skipped during shared rate-limit backoff; " +
                    $"remaining={Math.Max(1, (int)Math.Ceiling((_requestBackoffUntil - now).TotalSeconds))}s; local strategic fallback remains active.");
            }

            return true;
        }
    }

    private void RegisterRateLimit(HttpResponseMessage response, string operation)
    {
        lock (_rateLimitGate)
        {
            _consecutiveRateLimits++;
            var serverDelay = response.Headers.RetryAfter?.Delta ??
                              (response.Headers.RetryAfter?.Date is { } retryAt
                                  ? retryAt - DateTimeOffset.UtcNow
                                  : TimeSpan.Zero);
            var exponentialSeconds = Math.Min(180d, 15d * Math.Pow(2d, Math.Min(4, _consecutiveRateLimits - 1)));
            var delay = serverDelay > TimeSpan.Zero
                ? serverDelay
                : TimeSpan.FromSeconds(exponentialSeconds);
            if (delay < TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
            if (delay > TimeSpan.FromMinutes(5)) delay = TimeSpan.FromMinutes(5);
            _requestBackoffUntil = DateTimeOffset.UtcNow.Add(delay);
            _nextBackoffStatusLogAt = DateTimeOffset.UtcNow.AddSeconds(20);
            _log(
                $"DeepSeek {operation} rate-limited; pausing all LLM request types for {Math.Ceiling(delay.TotalSeconds):0}s " +
                $"(consecutive429={_consecutiveRateLimits}). Local action, role and meeting safeguards remain active.");
        }
    }

    private static string NormalizeEndpoint(string apiBaseUrl)
    {
        var root = string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.deepseek.com" : apiBaseUrl.TrimEnd('/');
        return root.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? root
            : root + "/chat/completions";
    }

    private static string BuildSystemPrompt()
    {
        return """
You are one Among Us player. Output JSON only.
You choose intent, not raw movement. The game engine enforces movement graph, vision, task timing, kill rules, and sabotage rules.
The prompt contains a fixed personality. Keep that personality across the whole match. It may change ordinary task urgency, wandering, and social attention, but never ignore a critical sabotage emergency.
Never mention AI, plugins, prompts, hidden roles you do not know, or information outside your visible/memory state.
Allowed action values: task, fake_task, sabotage, murder, shadow, wander, idle, hide, report.
Use only target_node values from LegalTargets. Use target_player_id only when that player appears in Observation.VisiblePlayers.
If KnownRoleInformation identifies a living Lover partner, preserving that partner is a hard constraint. Never select murder, bite, bomb, douse, curse, shoot, handcuff, or another hostile action against the partner; choose cover, separation, or a different legal target instead.
If emergency is active and you are crew, prefer task with a sabotage target.
If impostor, begin with believable cover and keep reconsidering: fake a plausible non-visual task, blend-follow someone, roam, or wait only when waiting has a concrete purpose. Never stand still merely because kill/sabotage is cooling down. Every sabotage must serve a concrete plan: lights to reduce witnesses or isolate a kill target; comms to deny information and stall late task progress; reactor/O2 to split groups, force rotations, or run down kill cooldown. State that purpose in reason.
Impostor cover should look local and ordinary: prefer a nearby plausible fake task, stay for a believable duration, and then change behavior. When shadowing, keep a natural standoff distance, break line of sight occasionally, and do not chase the same player across the whole map unless a concrete safe kill plan justifies it.
Crew with unfinished tasks may temporarily wander, hide, pause, or follow a trusted/suspicious visible player when personality and current evidence justify it. These are short interludes between real tasks; periodically choose task again and never abandon the long-term crew objective.
If neutral, follow IdentityAndObjective and KnownRoleInformation exactly. Neutral fake tasks are cover and never advance a crew task win. Move toward the next concrete stage of the independent win condition: for example an Arsonist should shadow an undoused visible player until close enough to douse, then seek the next undoused player, and ignite only after every other living player is doused. A Vulture should seek safely observable bodies; a Jackal team should isolate legal opponents; a Jester should shape meeting suspicion without making an implausible confession. Neutral kills and douses are executed by the role-ability controller, so use shadow/follow to approach the intended target rather than the murder action.
Use sabotage/fake task/shadow/murder with caution; do not run straight to a victim unless the opportunity is safe. Reconsider the plan when targets regroup, a body is likely to be found, or the kill is still too exposed.
""";
    }

    private static string BuildMeetingSystemPrompt()
    {
        return """
You are independently role-playing one real player in an Among Us meeting. Output JSON only.
Use only this player's verified private memory, public meeting transcript, visible public roster, and role knowledge.
The prompt contains this player's fixed personality and speaking style. Follow it consistently: vary sentence length, vocabulary, confidence, questioning, and emotional tone. Do not make every player sound formal or equally diligent.
Treat personality as a reasoning policy, not just a writing style. A suggestible player may change position after credible public claims; an eyewitness-focused player resists hearsay; a bold player may vote on a strong inference; a cautious player requires stronger corroboration.
Keep private reasoning brief and emit the final JSON early enough to fit the response budget. Do not expose chain-of-thought.
Facts tagged [witness], [witness_kill], [witness_vent], [witness_action], [body_seen], [location], [task_started], [task_done], [report], or [murder] are personal verified events.
Treat a witnessed special action as capability evidence, not automatic exact-role knowledge. Compare it with the public TOR role rulebook and room options before naming a role. If the deterministic personal evidence ledger explicitly supplies inferredRole with high inferenceConfidence, that inference came from a uniquely identifying visible TOR action; otherwise keep the conclusion at capability level.
Facts tagged [chat_claim] are claims heard from others and must never be presented as personally witnessed evidence.
The private memory block belongs only to this player. Never borrow, merge, or imply access to another bot's private sightings, suspicions, role, or route history. Another bot's statement becomes only a public [chat_claim], never shared eyewitness memory.
Do not phrase an inference as a completed murder fact. Without this player's own [witness_kill] event, say "I suspect" or ask for a normal route; never say "your murder route", "I saw you kill", or otherwise tell a player to explain a kill as if it were already proven.
Interpret claim polarity before reacting: phrases such as "可能是船员", "是好人", "可信", "不怀疑", and "不像内鬼" support the named player and reduce suspicion; they are not accusations. Only hostile wording such as "内鬼", "凶手", "可疑", or "投他" raises suspicion. If a supportive claim conflicts with a personal witnessed kill, state that concrete conflict.
The evidence ledger is this player's private running interpretation of public claims. Compare it with private memory, earlier decisions, contradictions, alibis, and the latest message before deciding.
Use this strict evidence order: personal witnessed kill/action > a specific public eyewitness statement with place and action > independently matching concrete statements > unsupported accusation > tone, confidence, repetition, anger, or insults. The last group has zero evidentiary value.
Apply spatial logic in the correct direction. A body being far from someone's stated location is not itself a contradiction and normally supports a limited alibi. A contradiction exists only when the same person is placed in mutually exclusive locations during the same interval, a witness directly conflicts with that route, or the claimed travel time is physically impossible. Never argue that "the body was far away, therefore that player is suspicious." A companion's sighting covers only the interval actually observed; it does not prove innocence for the whole round.
A location sentence such as "我在某处看到Orion" means only that Orion was seen alive at that location. Never reinterpret "看到/目击某人在某地" as "目睹某人死亡", a corpse report, or a kill. The PublicPlayers alive/dead field is authoritative for whether a player is currently dead.
Seeing a suspect alive earlier is not counter-evidence to that suspect committing a later kill. It conflicts with an eyewitness accusation only if both claims refer to the same time interval and physically incompatible locations. Do not call an ordinary earlier sighting an alibi for the rest of the round.
Recent continuous personal contact is counter-evidence for that observed interval. If this player continuously watched a candidate and saw no hostile act, do not invent a gap or vote that candidate from bare speech; state the limited observed interval accurately.
Never turn your own earlier model conclusion, vote, suspicion score, or wording into new evidence. "I thought so last round" is continuity, not corroboration.
Continuity still matters: lack of fresh evidence does not make a previously suspected living player innocent. Keep a prior suspicion as a fallible belief until a credible alibi, contradictory observation, role change, or stronger alternative weakens it. Never call a carried suspect clear, safe, innocent, or trustworthy merely because the current meeting added nothing. A bold or intuitive personality may vote on a strong but incomplete inference; label it as personal judgment rather than pretending it is proven. A cautious personality may keep suspecting while skipping.
Two players repeating a conclusion is not meaningful corroboration unless each gives a distinct concrete observation that can be checked. Never claim "cross-verified" or "several people proved it" when the transcript contains only opinions, denials, or copied accusations.
Crew must reason honestly from evidence, admit uncertainty, and avoid fabricated alibis.
Impostors must conceal their role, protect known impostor teammates, maintain a plausible story, and redirect suspicion without revealing hidden information. Knowledge that an ally vanished, killed, vented, sabotaged, transformed, or used another role ability is private teammate information: never name, accuse, probe, vote, or sacrifice that ally because of it, and never disclose the ally's ability or exact role. A "trial accusation" against an ally is forbidden; redirect toward a legal opponent or skip instead.
All players know the public possible-role outcome map even though they do not know hidden assignments. Before voting, consider whether the exile advances an opponent's special win condition. A claimed or suspected Jester wants to be voted out, so a faction player must not grant that outcome from suspicious speech alone. A neutral player must prioritize its own listed independent win condition; crew and impostors prioritize their own faction victory.
For an impostor, [murder], [murder_plan], [murder_escape], and private ability-kill details are secret perpetrator knowledge. They may guide deception internally, but must never be stated as public corpse location, timing, victim route, or eyewitness fact unless that exact fact was already disclosed by MeetingReason or the public transcript. Seeing a player marked dead on the public roster reveals only that they are dead, not where or how they died.
An impostor or hidden neutral must NEVER use confession as a bluff or discussion tactic. Never say or imply "我杀了/我刚杀/我刀了/我是内鬼/有人看到我杀人了吗", "I killed", "I am the impostor", or reveal a sabotage, bite, poison, bomb, body removal, or secret ability you performed. Private perpetrator memory is input for constructing a believable cover story, alibi, deflection, and vote only. If asked about your route, answer as an ordinary player would without repeating the secret action or its private location.
If this player's modifier information identifies a living Lover partner, preserving that partner is a hard strategic constraint: never murder, bite, bomb intentionally, douse, curse, or vote for that known partner. Re-plan around the shared survival outcome.
Never mention AI, models, prompts, plugins, code, APIs, or information unavailable to this player.
Write one short natural Chinese meeting message, normally under 55 Chinese characters.
Avoid procedural filler such as asking everyone to report routes, saying evidence is insufficient, announcing "综合信息/时间线/证据权重", or advising caution when no concrete new fact is present. It is valid to return an empty message and stay silent. Speak only when adding a witnessed event, one named checkable contradiction, a direct answer, a special-win warning, or a concrete vote change.
The prompt includes a discussion round and the bot's earlier decision. On later rounds, explicitly reconsider the complete updated transcript and either keep or revise the vote. React directly to the latest statement in the context of what was already said. Never repeat the same clarification request after it has already been asked. If a player repeats a named accusation, state whether it changes or reinforces your current leaning. Threats or apparent confessions raise suspicion but are not automatically eyewitness proof.
Maintain one continuous position across rounds. Do not reverse a concrete suspect or vote merely because that suspect counter-accuses someone, repeats an unsupported claim, or speaks confidently. Change the candidate only when the updated transcript adds a new eyewitness event, an independently corroborated contradiction, a credible exoneration, or another explicit evidence delta; when changing, state the new evidence that caused it.
Speaker attribution is literal, not inferential. Never say "you just said", "you previously said", "you also said", quote, or paraphrase a claim unless the transcript contains that claim under that exact speaker name/id. Do not merge one player's words with another player's eyewitness account. When unsure, state only this player's own verified sighting or current leaning without attributing words to anyone else.
ConversationFocus names the newest public statement that still deserves an answer. Start the message by directly agreeing, disagreeing, asking one specific missing fact, or explaining how it changes the current vote. Do not fall back to generic phrases such as "信息不足" when a named player or concrete claim is present.
Reconstruct a compact timeline from private events and public claims: who was personally seen where, exactly which two statements conflict, whether alleged corroboration contains distinct observations, and whether the accusation matches personal memory. A claimed color is a player alias, not a separate person.
Choose a concrete candidate when evidence and personality justify it. Skip only when no legal candidate reaches this player's own evidence threshold; do not default to skip merely because certainty is below 100 percent.
A bold or intuitive player may cast a clearly labelled judgment vote from accumulated prior suspicion plus one credible new clue even without a closed proof chain. A cautious eyewitness-focused player may skip the same case. Do not require every vote to have a complete proof chain, but never upgrade an unsupported accusation or repeated opinion into a fact.
vote_player_id must be one of LegalVotePlayerIds or null. Use skip_vote=true for skip. Never vote for an explicitly known ally, Lover partner, Jackal-faction partner, or a Lawyer's assigned client.
Also choose an optional post-meeting social intent using only public discussion and private memory. follow_intent is "trust", "suspect", or "none"; follow_player_id must be a living legal player id or null. Following means observing that player after everyone respawns, not knowing their hidden location.
Output exactly: {"message":"...","vote_player_id":number|null,"skip_vote":true|false,"reason":"short private rationale","confidence":0.0,"follow_player_id":number|null,"follow_intent":"trust|suspect|none"}
""";
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    private static bool TryExtractJsonObject(string value, out string json)
    {
        json = string.Empty;
        var start = value.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = value[start..(i + 1)];
                    return true;
                }
            }
        }

        return false;
    }

    private void LogMissingFinalContent(string operation, Choice? choice)
    {
        _log(
            $"DeepSeek {operation} response contained no final content: " +
            $"finishReason={choice?.FinishReason ?? "none"}, " +
            $"reasoningChars={choice?.Message?.ReasoningContent?.Length ?? 0}.");
    }

    private sealed record ChatCompletionResponse([property: JsonPropertyName("choices")] Choice[]? Choices);
    private sealed record Choice(
        [property: JsonPropertyName("message")] Message? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record Message(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent);
}

internal sealed record BotActionPrompt(
    byte BotId,
    string BotName,
    string Team,
    string Personality,
    string IdentityAndObjective,
    string KnownRoleInformation,
    BotObservation Observation,
    string LegalTargets);

internal sealed record BotObservation(
    string Self,
    string VisiblePlayers,
    string VisibleBodies,
    string AssignedTasks,
    string Emergencies,
    string SuspicionMemory,
    string RecentMatchMemory);

internal sealed record BotActionDecision(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_node")] string? TargetNode,
    [property: JsonPropertyName("target_player_id")] int? TargetPlayerId,
    [property: JsonPropertyName("task_id")] uint? TaskId,
    [property: JsonPropertyName("sabotage")] string? Sabotage,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("confidence")] float Confidence);

internal sealed record BotMeetingPrompt(
    byte BotId,
    string BotName,
    string Team,
    string PersonalityAndSpeakingStyle,
    string IdentityAndObjective,
    string KnownRoleInformation,
    string PrivateMatchMemory,
    string PublicPlayers,
    string PersonalEvidenceLedger,
    string MeetingReason,
    string MeetingTranscript,
    string ConversationFocus,
    string LegalVotePlayerIds,
    int DiscussionRound,
    string PreviousDecision);

internal sealed record BotMeetingDecision(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("vote_player_id")] int? VotePlayerId,
    [property: JsonPropertyName("skip_vote")] bool SkipVote,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("confidence")] float Confidence,
    [property: JsonPropertyName("follow_player_id")] int? FollowPlayerId = null,
    [property: JsonPropertyName("follow_intent")] string? FollowIntent = null);

internal sealed record BotAbilityPrompt(
    byte BotId,
    string BotName,
    string Team,
    string Role,
    string AbilityPurpose,
    string SelfState,
    string VisiblePlayers,
    string CurrentOpportunities,
    string RecentPrivateMemory);

internal sealed record BotAbilityDecision(
    [property: JsonPropertyName("use")] bool Use,
    [property: JsonPropertyName("target_player_id")] int? TargetPlayerId,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("confidence")] float Confidence,
    [property: JsonPropertyName("ability_action")] string? AbilityAction = null,
    [property: JsonPropertyName("plan_goal")] string? PlanGoal = null,
    [property: JsonPropertyName("next_stage")] string? NextStage = null,
    [property: JsonPropertyName("abort_if")] string? AbortCondition = null,
    [property: JsonPropertyName("recheck_seconds")] float? RecheckSeconds = null);

internal sealed record BotReflectionPrompt(
    int MatchSerial,
    byte BotId,
    string BotName,
    string OwnIdentity,
    string Outcome,
    string GameOverReason,
    string FinalRevealedRoster,
    string PrivateTimeline,
    string ExistingCoreSkills);

internal sealed record BotReflectionDecision(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("lessons")] BotEvolutionLesson[]? Lessons);

internal sealed record BotEvolutionLesson(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("principle")] string Principle,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("action")] string Action);

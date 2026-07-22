using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

        var request = new
        {
            model = _model(),
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = JsonSerializer.Serialize(prompt, JsonOptions) }
            },
            max_tokens = 1100,
            temperature = 0.55,
            response_format = new { type = "json_object" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NormalizeEndpoint(_apiBaseUrl()));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
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

        try
        {
            return JsonSerializer.Deserialize<BotActionDecision>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            if (TryExtractJsonObject(content, out var json))
            {
                try
                {
                    return JsonSerializer.Deserialize<BotActionDecision>(json, JsonOptions);
                }
                catch (Exception inner)
                {
                    _log($"DeepSeek action extracted JSON parse failed: {inner.Message}, body={Truncate(json, 240)}");
                    return null;
                }
            }

            _log($"DeepSeek action JSON parse failed: {ex.Message}, body={Truncate(content, 240)}");
            return null;
        }
    }

    public async Task<BotMeetingDecision?> GetMeetingDecisionAsync(BotMeetingPrompt prompt, CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

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

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
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
            return JsonSerializer.Deserialize<BotMeetingDecision>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            if (TryExtractJsonObject(content, out var json))
            {
                try
                {
                    return JsonSerializer.Deserialize<BotMeetingDecision>(json, JsonOptions);
                }
                catch (Exception inner)
                {
                    _log($"DeepSeek meeting extracted JSON parse failed: {inner.Message}, body={Truncate(json, 240)}");
                    return null;
                }
            }

            _log($"DeepSeek meeting JSON parse failed: {ex.Message}, body={Truncate(content, 240)}");
            return null;
        }
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
Respect the exact ability purpose supplied in the prompt. Do not use an ability merely because it is ready.
Use private memory and currently visible players only. Never infer hidden roles unavailable to this player.
Engineer vents are for meaningful shortcuts, escape, or emergency rotation. Tracker marks a useful trusted or suspicious player. Guardian Angel protects a player likely to be endangered. Phantom invisibility conceals movement or escapes witnesses. Shapeshifter disguises before deception or a planned kill while unobserved. Detective investigates a genuinely useful suspect. Impostor vents are for covert escape or repositioning, not random travel.
target_player_id must be a currently legal visible target or null.
ability_action must be "role", "vent", or "hold". Use "role" for the named active skill, "vent" only when vent access is listed and it serves a concrete shortcut/escape/ambush purpose, and "hold" when no skill should be used now.
For Vulture, a visible consumable body is the primary objective: choose role/eat instead of report. Hold only for a concrete nearby-witness risk, then reassess quickly; ordinary reporting is illegal for this role. For Engineer, choose role only during a dangerous active sabotage and conserve finite repair charges otherwise. For Vampire, bite only an isolated target when the delayed death will not expose the route. For Warlock, curse a carrier likely to approach a legal isolated second target; do not curse randomly. For Ninja, mark and later strike only when invisibility and the remote route produce a credible escape. For Portalmaker, place two portals in meaningfully separated useful rooms. For Deputy, handcuff only a behaviorally suspicious nearby player. For Trapper, SecurityGuard, and Trickster placement skills, choose informative separated chokepoints rather than arbitrary empty rooms. Trickster should use darkness only when it supports a concrete kill, escape, or time-pressure plan. Bomber must plant only where traffic and timing support an intentional split or elimination. Yoyo should mark and blink only for an alibi, escape, ambush, or concealed rotation, never as random movement. For Engineer and vent-capable custom roles, choose vent only when the route advantage outweighs the risk of being seen.
Output exactly: {"use":true|false,"ability_action":"role|vent|hold","target_player_id":number|null,"reason":"short strategic purpose","confidence":0.0}
"""
                },
                new { role = "user", content = JsonSerializer.Serialize(prompt, JsonOptions) }
            },
            max_tokens = 700,
            temperature = 0.42,
            response_format = new { type = "json_object" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NormalizeEndpoint(_apiBaseUrl()));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
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

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
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
Facts tagged [witness], [body_seen], [location], [task_started], [task_done], [report], or [murder] are personal verified events.
Facts tagged [chat_claim] are claims heard from others and must never be presented as personally witnessed evidence.
The evidence ledger is this player's private running interpretation of public claims. Compare it with private memory, earlier decisions, contradictions, alibis, and the latest message before deciding.
Crew must reason honestly from evidence, admit uncertainty, and avoid fabricated alibis.
Impostors must conceal their role, protect known impostor teammates, maintain a plausible story, and redirect suspicion without revealing hidden information.
Never mention AI, models, prompts, plugins, code, APIs, or information unavailable to this player.
Write one short natural Chinese meeting message, normally under 55 Chinese characters.
The prompt includes a discussion round and the bot's earlier decision. On later rounds, explicitly reconsider the complete updated transcript and either keep or revise the vote. React directly to the latest statement in the context of what was already said. Never repeat the same clarification request after it has already been asked. If a player repeats a named accusation, state whether it changes or reinforces your current leaning. Threats or apparent confessions raise suspicion but are not automatically eyewitness proof.
ConversationFocus names the newest public statement that still deserves an answer. Start the message by directly agreeing, disagreeing, asking one specific missing fact, or explaining how it changes the current vote. Do not fall back to generic phrases such as "信息不足" when a named player or concrete claim is present.
Reconstruct a compact timeline from private events and public claims: who was seen where, which claims conflict, who supplied corroboration, and whether the current accusation matches personal memory. A claimed color is a player alias, not a separate person.
Choose a concrete candidate when evidence and personality justify it. Skip only when no legal candidate reaches this player's own evidence threshold; do not default to skip merely because certainty is below 100 percent.
vote_player_id must be one of LegalVotePlayerIds or null. Use skip_vote=true for skip. Never vote for a known impostor teammate.
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
    [property: JsonPropertyName("ability_action")] string? AbilityAction = null);

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

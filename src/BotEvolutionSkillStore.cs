using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;

namespace AmongUsDeepSeekBots;

internal sealed class BotEvolutionSkillStore
{
    private const int MaxSkills = 120;
    private readonly object _gate = new();
    private readonly ManualLogSource _log;
    private readonly string _jsonPath;
    private readonly string _markdownPath;
    private EvolutionSkillDocument _document = new();

    internal BotEvolutionSkillStore(ManualLogSource log)
    {
        _log = log;
        var directory = Path.Combine(Paths.ConfigPath, "AmongUsDeepSeekBots");
        Directory.CreateDirectory(directory);
        _jsonPath = Path.Combine(directory, "core-evolution-skills.json");
        _markdownPath = Path.Combine(directory, "core-evolution-skills.md");
        Load();
    }

    internal string BuildPrompt(int maxSkills = 14)
    {
        lock (_gate)
        {
            if (_document.Skills.Count == 0)
            {
                return "no cross-match evolution skills yet";
            }

            return string.Join(
                "\n",
                _document.Skills
                    .OrderByDescending(skill => skill.HitCount)
                    .ThenByDescending(skill => skill.LastSeenUtc)
                    .Take(Math.Clamp(maxSkills, 4, 24))
                    .Select(skill =>
                        $"[{skill.Category}/{skill.Key}] when={skill.Trigger}; do={skill.Action}; principle={skill.Principle}; verified={skill.HitCount}x"));
        }
    }

    internal string BuildReflectionReference(int maxSkills = 40)
    {
        lock (_gate)
        {
            if (_document.Skills.Count == 0) return "none";
            return string.Join(
                "\n",
                _document.Skills
                    .OrderByDescending(skill => skill.HitCount)
                    .ThenByDescending(skill => skill.LastSeenUtc)
                    .Take(Math.Clamp(maxSkills, 8, 60))
                    .Select(skill => $"{skill.Key}|{skill.Category}|{skill.Principle}|{skill.Trigger}|{skill.Action}"));
        }
    }

    internal EvolutionMergeResult Merge(
        IEnumerable<BotEvolutionLesson> lessons,
        int matchSerial,
        byte botId,
        string role,
        bool won)
    {
        var added = 0;
        var reinforced = 0;
        lock (_gate)
        {
            foreach (var lesson in lessons.Take(4))
            {
                var normalized = NormalizeLesson(lesson);
                if (normalized is null) continue;

                var existing = FindEquivalent(normalized);
                if (existing is not null)
                {
                    existing.HitCount++;
                    existing.LastSeenUtc = DateTime.UtcNow;
                    existing.LastMatchSerial = matchSerial;
                    existing.LastSourceBotId = botId;
                    existing.LastOutcome = won ? "win" : "loss";
                    if (!existing.SourceRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.SourceRoles.Add(role);
                    }
                    reinforced++;
                    continue;
                }

                _document.Skills.Add(new EvolutionSkill
                {
                    Key = normalized.Key,
                    Category = normalized.Category,
                    Principle = normalized.Principle,
                    Trigger = normalized.Trigger,
                    Action = normalized.Action,
                    HitCount = 1,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    LastMatchSerial = matchSerial,
                    LastSourceBotId = botId,
                    LastOutcome = won ? "win" : "loss",
                    SourceRoles = [role]
                });
                added++;
            }

            if (_document.Skills.Count > MaxSkills)
            {
                _document.Skills = _document.Skills
                    .OrderByDescending(skill => skill.HitCount)
                    .ThenByDescending(skill => skill.LastSeenUtc)
                    .Take(MaxSkills)
                    .ToList();
            }

            if (added > 0 || reinforced > 0)
            {
                _document.Version = 1;
                _document.UpdatedUtc = DateTime.UtcNow;
                SaveLocked();
            }
        }

        return new EvolutionMergeResult(added, reinforced, _jsonPath, _markdownPath);
    }

    private EvolutionSkill? FindEquivalent(BotEvolutionLesson lesson)
    {
        var key = NormalizeKey(lesson.Key);
        var exact = _document.Skills.FirstOrDefault(skill => NormalizeKey(skill.Key) == key);
        if (exact is not null) return exact;

        return _document.Skills
            .Where(skill => string.Equals(skill.Category, lesson.Category, StringComparison.OrdinalIgnoreCase))
            .Select(skill => new { Skill = skill, Similarity = BigramSimilarity(skill.Principle, lesson.Principle) })
            .Where(item => item.Similarity >= 0.72)
            .OrderByDescending(item => item.Similarity)
            .Select(item => item.Skill)
            .FirstOrDefault();
    }

    private static BotEvolutionLesson? NormalizeLesson(BotEvolutionLesson lesson)
    {
        var key = NormalizeKey(lesson.Key);
        var category = Clean(lesson.Category, 24).ToLowerInvariant();
        var principle = Clean(lesson.Principle, 180);
        var trigger = Clean(lesson.Trigger, 140);
        var action = Clean(lesson.Action, 180);
        if (key.Length < 5 || category.Length < 3 || principle.Length < 8 || action.Length < 6)
        {
            return null;
        }

        return new BotEvolutionLesson(key, category, principle, trigger, action);
    }

    private static string NormalizeKey(string value)
    {
        var clean = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .ToArray());
        while (clean.Contains("__", StringComparison.Ordinal)) clean = clean.Replace("__", "_", StringComparison.Ordinal);
        return clean.Trim('_');
    }

    private static string Clean(string value, int maxLength)
    {
        var clean = string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static double BigramSimilarity(string left, string right)
    {
        var a = Bigrams(left);
        var b = Bigrams(right);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(item => b.Contains(item));
        return (2.0 * intersection) / (a.Count + b.Count);
    }

    private static HashSet<string> Bigrams(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < normalized.Length; index++)
        {
            result.Add(normalized.Substring(index, 2));
        }
        return result;
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_jsonPath))
                {
                    _document = JsonSerializer.Deserialize<EvolutionSkillDocument>(File.ReadAllText(_jsonPath)) ?? new();
                }
                _log.LogInfo($"DeepBot evolution skill store ready: skills={_document.Skills.Count}, path={_jsonPath}.");
            }
            catch (Exception ex)
            {
                _document = new EvolutionSkillDocument();
                _log.LogWarning($"DeepBot evolution skill store load failed; starting empty: {ex.Message}");
            }
        }
    }

    private void SaveLocked()
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(_document, jsonOptions);
        var tempPath = _jsonPath + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, _jsonPath, true);

        var markdown = new StringBuilder();
        markdown.AppendLine("# DeepBot 核心进化技能库");
        markdown.AppendLine();
        markdown.AppendLine($"更新时间：{_document.UpdatedUtc:O}");
        markdown.AppendLine();
        foreach (var skill in _document.Skills.OrderByDescending(item => item.HitCount).ThenBy(item => item.Key))
        {
            markdown.AppendLine($"## {skill.Key}");
            markdown.AppendLine();
            markdown.AppendLine($"- 类别：{skill.Category}");
            markdown.AppendLine($"- 原则：{skill.Principle}");
            markdown.AppendLine($"- 触发：{skill.Trigger}");
            markdown.AppendLine($"- 行动：{skill.Action}");
            markdown.AppendLine($"- 验证次数：{skill.HitCount}");
            markdown.AppendLine($"- 来源职业：{string.Join("、", skill.SourceRoles)}");
            markdown.AppendLine();
        }
        File.WriteAllText(_markdownPath, markdown.ToString(), new UTF8Encoding(false));
    }

    private sealed class EvolutionSkillDocument
    {
        public int Version { get; set; } = 1;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<EvolutionSkill> Skills { get; set; } = [];
    }

    private sealed class EvolutionSkill
    {
        public string Key { get; set; } = "";
        public string Category { get; set; } = "";
        public string Principle { get; set; } = "";
        public string Trigger { get; set; } = "";
        public string Action { get; set; } = "";
        public int HitCount { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int LastMatchSerial { get; set; }
        public byte LastSourceBotId { get; set; }
        public string LastOutcome { get; set; } = "";
        public List<string> SourceRoles { get; set; } = [];
    }
}

internal readonly record struct EvolutionMergeResult(int Added, int Reinforced, string JsonPath, string MarkdownPath);


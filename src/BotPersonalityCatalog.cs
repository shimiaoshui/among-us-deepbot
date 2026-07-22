namespace AmongUsDeepSeekBots;

internal static class BotPersonalityCatalog
{
    private static readonly BotPersonalityProfile[] Profiles =
    [
        new(
            "急性子",
            "目标感很强，任务之间几乎不停留，看到任务就尽快推进。",
            "说话短、直接、结论先行；会追问关键时间点，不绕弯。",
            0.4f,
            1.2f,
            0.05f,
            0.63f,
            0.90f,
            0.42f,
            0.55f,
            0.88f),
        new(
            "认真派",
            "稳定做任务，完成后会短暂停一下确认周围情况再继续。",
            "按时间和地点陈述证据，语气冷静，少用绝对判断。",
            1.8f,
            3.6f,
            0.20f,
            0.67f,
            0.86f,
            0.34f,
            0.82f,
            0.58f),
        new(
            "社交派",
            "任务和观察他人同样重要，做完任务后常去公共区域转一圈。",
            "喜欢向别人提问、接话和比较口供，语气自然、有互动感。",
            3.5f,
            6.5f,
            0.65f,
            0.74f,
            0.72f,
            0.88f,
            0.38f,
            0.66f),
        new(
            "谨慎派",
            "不连续冲任务，完成后会观察路线和附近玩家，再决定下一步。",
            "先承认不确定性，再列证据和矛盾；措辞克制、偏分析型。",
            5.5f,
            9.0f,
            0.72f,
            0.60f,
            0.60f,
            0.22f,
            0.94f,
            0.24f),
        new(
            "懒散派",
            "不热衷连续做任务，完成一个后经常闲逛或发呆一阵再继续。",
            "口语化、随意、偶尔吐槽；消息较短，但被点名时会认真回应。",
            8.0f,
            14.0f,
            0.90f,
            0.79f,
            0.38f,
            0.70f,
            0.42f,
            0.48f)
    ];

    public static BotPersonalityProfile ForPlayer(byte playerId)
    {
        var index = playerId == 0 ? 0 : (playerId - 1) % Profiles.Length;
        return Profiles[index];
    }

    public static bool Validate()
    {
        return Profiles.Length == 5 &&
            Profiles.All(profile =>
                profile.PostTaskPauseMin >= 0f &&
                profile.PostTaskPauseMax >= profile.PostTaskPauseMin &&
                profile.WanderChance is >= 0f and <= 1f &&
                profile.MeetingTemperature is >= 0f and <= 1f &&
                profile.EmergencyResponsiveness is >= 0f and <= 1f &&
                profile.SocialSuggestibility is >= 0f and <= 1f &&
                profile.EyewitnessReliance is >= 0f and <= 1f &&
                profile.VoteBoldness is >= 0f and <= 1f);
    }
}

internal sealed record BotPersonalityProfile(
    string Name,
    string TaskStyle,
    string MeetingStyle,
    float PostTaskPauseMin,
    float PostTaskPauseMax,
    float WanderChance,
    float MeetingTemperature,
    float EmergencyResponsiveness,
    float SocialSuggestibility,
    float EyewitnessReliance,
    float VoteBoldness)
{
    public string ActionPrompt =>
        $"固定性格={Name}；任务习惯={TaskStyle}；紧急响应倾向={EmergencyResponsiveness:0.00}。" +
        "紧急事件是否前往必须依据自己看到的人、距离、剩余风险和阵营独立判断，不能假设知道其他人的目标。";

    public string MeetingPrompt =>
        $"固定性格={Name}；说话风格={MeetingStyle}；听信他人倾向={SocialSuggestibility:0.00}；" +
        $"亲眼证据依赖={EyewitnessReliance:0.00}；冒险投票倾向={VoteBoldness:0.00}。" +
        "结合这些倾向独立判断：容易信任者可被可信说法改变，重视亲眼者除非证据很强否则不随声附和，" +
        "果断者可在不完全确定时下注，谨慎者提高投票门槛。";
}

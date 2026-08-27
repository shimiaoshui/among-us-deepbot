namespace AmongUsDeepSeekBots;

internal static class BotPersonalityCatalog
{
    private static readonly BotPersonalityProfile[] Profiles =
    [
        new(
            "急性子",
            "目标感很强，任务之间几乎不停留，看到任务就尽快推进。",
            "说话短、直接、结论先行；会用一点损友式吐槽催人回答，但必须带上关键地点或动作。",
            0.4f,
            1.2f,
            0.05f,
            0.63f,
            0.90f,
            0.42f,
            0.55f,
            0.96f),
        new(
            "果断派",
            "做事速度快，看到合法机会会直接执行，不会为了等完美局面长期停住。",
            "先给结论再补理由；像敢押注的牌友，偶尔抖机灵，但会明确区分判断和亲眼事实。",
            0.8f,
            2.2f,
            0.28f,
            0.78f,
            0.88f,
            0.45f,
            0.70f,
            0.84f),
        new(
            "社交派",
            "任务和观察他人同样重要，做完任务后常去公共区域转一圈。",
            "喜欢接话、玩梗和比较口供，像熟人局聊天；笑归笑，仍要说清具体人、地点和动作。",
            3.5f,
            6.5f,
            0.65f,
            0.74f,
            0.72f,
            0.88f,
            0.38f,
            0.80f),
        new(
            "谨慎派",
            "不连续冲任务，完成后会观察路线和附近玩家，再决定下一步。",
            "偏谨慎但不是法官念判词；用冷幽默或一句反问点出矛盾，避免长篇分析腔。",
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
            "口语化、懒洋洋、爱吐槽和接梗；消息短，被点名时会把有效信息讲明白。",
            8.0f,
            14.0f,
            0.90f,
            0.79f,
            0.38f,
            0.70f,
            0.42f,
            0.68f),
        new(
            "直觉派",
            "容易临场改变计划；看到机会会先行动再复盘，但仍遵守技能、距离和阵营规则。",
            "会把直觉说成下注、闻到味了之类的口语梗；不等证据链完全闭环，敢根据连续疑点站队。",
            1.0f,
            3.0f,
            0.55f,
            0.88f,
            0.70f,
            0.64f,
            0.48f,
            0.92f)
    ];

    public static BotPersonalityProfile ForPlayer(byte playerId)
    {
        var index = playerId == 0 ? 0 : (playerId - 1) % Profiles.Length;
        return Profiles[index];
    }

    public static bool Validate()
    {
        return Profiles.Length == 6 &&
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
        (VoteBoldness >= 0.80f
            ? "你偏冲动：发现合法且符合阵营目标的机会时可以快速行动，不必等待完美局面；失败后再调整。"
            : VoteBoldness >= 0.60f
                ? "你愿意承担适度风险，不要总选择等待或最保守路线。"
                : "你偏谨慎，可以等待更好的窗口，但不能因此长期停滞。") +
        "紧急事件是否前往必须依据自己看到的人、距离、剩余风险和阵营独立判断，不能假设知道其他人的目标。";

    public string MeetingPrompt =>
        $"固定性格={Name}；说话风格={MeetingStyle}；听信他人倾向={SocialSuggestibility:0.00}；" +
        $"亲眼证据依赖={EyewitnessReliance:0.00}；冒险投票倾向={VoteBoldness:0.00}。" +
        "结合这些倾向独立判断：玩家的证词是有效的社交信息，不要默认不信。容易信任者会被明确对象、地点、动作或路线的说法明显改变判断；冲动者可以把可信玩家的具体目击当作下注依据；重视亲眼者会保留意见，但也不能把别人的具体证词当空气。语气和跟票本身仍不算证据。" +
        "果断者和直觉派可在不完全确定时明确标注为个人判断并下注，谨慎者提高投票门槛；表达可以俏皮、损友式、带轻微玩梗或吐槽，但不能辱骂、刷烂梗、自爆身份、泄露队友或拿玩笑替代事实；不要把所有人格都写成冷静分析员。";
}

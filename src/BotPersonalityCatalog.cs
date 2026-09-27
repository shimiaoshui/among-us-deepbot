namespace AmongUsDeepSeekBots;

internal static class BotPersonalityCatalog
{
    private static readonly BotPersonalityProfile[] Profiles =
    [
        new(
            "急性子",
            "目的意識が強く、タスクの間でほとんど立ち止まらず、見つけたタスクをすぐ進める。",
            "短く直接的に結論から話す。軽いツッコミで返答を促すが、場所や行動は必ず添える。",
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
            "行動が速く、合法的な好機を見ればすぐ実行し、完璧な局面待ちで止まり続けない。",
            "結論を先に出して理由を添える。賭けに出る友人のように軽口も言うが、判断と自分の目撃は分ける。",
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
            "タスクと他人の観察を同じくらい大切にし、タスク後は公共エリアを見回す。",
            "会話に入り、冗談を交え、証言を比べる。仲間内の会話らしく話すが、人、場所、行動は明確にする。",
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
            "タスクを連続で急がず、終えたらルートと近くのプレイヤーを観察して次を決める。",
            "慎重だが裁判官の読み上げにはしない。短い皮肉や質問で矛盾を指摘し、長い分析は避ける。",
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
            "タスクを連続でこなすことに熱心ではなく、一つ終えると少し歩き回ったり休んだりする。",
            "口語的でのんびりし、軽いツッコミや冗談を言う。発言は短く、名指しされたら有効な情報を明確に言う。",
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
            "その場で計画を変えやすい。好機を見れば先に動いて振り返るが、能力、距離、陣営のルールは守る。",
            "直感を賭けや違和感として口語的に話す。証拠が完全にそろう前でも、連続した疑点で立場を決められる。",
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
    private string MeetingPersonalityName => Name switch
    {
        "急性子" => "せっかち",
        "果断派" => "即決派",
        "社交派" => "社交派",
        "谨慎派" => "慎重派",
        "懒散派" => "のんびり派",
        "直觉派" => "直感派",
        _ => Name
    };

    public string ActionPrompt =>
        $"固定性格={Name}；任务习惯={TaskStyle}；紧急响应倾向={EmergencyResponsiveness:0.00}。" +
        (VoteBoldness >= 0.80f
            ? "你偏冲动：发现合法且符合阵营目标的机会时可以快速行动，不必等待完美局面；失败后再调整。"
            : VoteBoldness >= 0.60f
                ? "你愿意承担适度风险，不要总选择等待或最保守路线。"
                : "你偏谨慎，可以等待更好的窗口，但不能因此长期停滞。") +
        "紧急事件是否前往必须依据自己看到的人、距离、剩余风险和阵营独立判断，不能假设知道其他人的目标。";

    public string MeetingPrompt =>
        $"固定した性格={MeetingPersonalityName}。話し方={MeetingStyle}。他人の証言を受け入れる傾向={SocialSuggestibility:0.00}。" +
        $"自分の目撃を重視する度合い={EyewitnessReliance:0.00}。思い切った投票の傾向={VoteBoldness:0.00}。" +
        "この傾向を使って独立に判断する。プレイヤーの証言は有効な社会的情報であり、初めから無視しない。信じやすい性格は対象、場所、行動、ルートが明確な話で判断を大きく変えられる。衝動的な性格は信頼できるプレイヤーの具体的な目撃を賭けの根拠にできる。目撃重視の性格も保留はできるが、他人の具体的な証言を無視してはいけない。口調と追従投票だけは証拠にならない。" +
        "大胆型と直感型は完全に確信できなくても個人的な判断だと明示して投票できる。慎重型は投票の基準を上げる。表現は軽快で、仲のよい相手への軽いツッコミや冗談を含めてもよいが、暴言、同じネタの連投、正体の自白、味方の暴露、冗談で事実の代用はしない。全員を冷静な分析役のように書かない。";
}

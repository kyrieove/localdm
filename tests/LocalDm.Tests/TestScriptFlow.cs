using LocalDm.Core.Domain;

namespace LocalDm.Tests;

/// <summary>
/// 测试用的流程声明。故意做成两份形状不同的流程（单轮有投票 / 双轮无投票），
/// 用来证明阶段数量、顺序与动作来自剧本包，而不是某个固定模板。
/// </summary>
internal static class TestScriptFlow
{
    /// <summary>单轮搜证 + 投票 + 真相：5 个环节。</summary>
    public static ScriptFlow SingleRoundWithVote(params string[] searchMaterials) => new()
    {
        Steps =
        [
            Step("read", "阅本", ScriptStepKind.Read, "第 1 页 阅本", publicGroups: ["组织"]),
            Step(
                "search", "搜证", ScriptStepKind.Search, "第 3 页 搜证",
                conditions: ["按编号逐个发放线索"],
                materials: searchMaterials,
                requiresAll: true),
            Step("talk", "讨论", ScriptStepKind.Talk, "第 5 页 讨论"),
            Step("vote", "投票", ScriptStepKind.Vote, "第 6 页 投票", conditions: ["纸笔投票完成"]),
            new ScriptFlowStep
            {
                StepId = "reveal",
                Title = "真相",
                Kind = ScriptStepKind.Reveal,
                SourceRef = "第 9 页 真相",
                Conditions = ["结局可以公布"],
                RevealLabel = "公开真相",
            },
        ],
    };

    /// <summary>双轮搜证、没有投票：6 个环节。</summary>
    public static ScriptFlow DoubleRoundWithoutVote(string firstRoundGroup, string secondRoundGroup) => new()
    {
        Steps =
        [
            Step("prologue", "序章共读", ScriptStepKind.Intro, "第 1 页 序章", advanceLabel: "进入第一轮搜证"),
            Step(
                "search-one", "第一轮搜证", ScriptStepKind.Search, "第 3 页 第一轮搜证",
                conditions: ["本轮两份材料全部发出"],
                materials: [firstRoundGroup],
                requiresAll: true),
            Step("talk-one", "第一轮讨论", ScriptStepKind.Talk, "第 5 页 第一轮讨论"),
            Step(
                "search-two", "第二轮搜证", ScriptStepKind.Search, "第 6 页 第二轮搜证",
                conditions: ["本轮材料发出"],
                materials: [secondRoundGroup],
                requiresAll: true),
            Step("talk-two", "最终讨论", ScriptStepKind.Talk, "第 8 页 最终讨论"),
            new ScriptFlowStep
            {
                StepId = "ending",
                Title = "结局与复盘",
                Kind = ScriptStepKind.Reveal,
                SourceRef = "第 9 页 结局",
                RevealLabel = "公开结局",
            },
        ],
    };

    /// <summary>缺来源依据的流程：整包必须停在 NeedsReview。</summary>
    public static ScriptFlow WithUnreviewedStep() => new()
    {
        Steps =
        [
            Step("read", "阅本", ScriptStepKind.Read, "第 1 页 阅本"),
            new ScriptFlowStep
            {
                StepId = "unknown",
                Title = "第二轮",
                Kind = ScriptStepKind.Search,
                // 没有 SourceRef：来源里没说清这一轮要做什么。
            },
            new ScriptFlowStep
            {
                StepId = "ending",
                Title = "结局",
                Kind = ScriptStepKind.Reveal,
                SourceRef = "第 9 页 结局",
            },
        ],
    };

    private static ScriptFlowStep Step(
        string id,
        string title,
        ScriptStepKind kind,
        string sourceRef,
        IReadOnlyList<string>? conditions = null,
        IReadOnlyList<string>? materials = null,
        bool requiresAll = false,
        string? advanceLabel = null,
        IReadOnlyList<string>? publicGroups = null) => new()
    {
        StepId = id,
        Title = title,
        Kind = kind,
        SourceRef = sourceRef,
        Conditions = conditions ?? [$"原本条件：{title}按剧本完成"],
        Hint = $"{title}环节当前提示",
        MaterialIds = materials ?? [],
        RequiresAllMaterials = requiresAll,
        AdvanceLabel = advanceLabel,
        PublicGroups = publicGroups ?? [],
    };
}

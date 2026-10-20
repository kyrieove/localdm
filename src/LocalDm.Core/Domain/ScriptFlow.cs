namespace LocalDm.Core.Domain;

/// <summary>
/// 剧本流程的结构化声明：环节数量、顺序、标题、提示、动作与材料都从这里来。
///
/// 这是一份<b>来源依据</b>，不是默认模板：每个环节都必须带 <see cref="ScriptFlowStep.SourceRef"/>，
/// 指到剧本原文里写清楚该环节的那一页/那句话。缺依据的环节保持 NeedsReview，
/// 准备器不会替它补一个「看起来合理」的环节，也不会把某个固定流程套给所有剧本。
/// </summary>
public sealed record ScriptFlow
{
    public IReadOnlyList<ScriptFlowStep> Steps { get; init; } = [];

    /// <summary>
    /// 流程声明自身的问题：空环节、重复/空步骤 ID、自相矛盾的声明。
    ///
    /// 这些问题必须让整包待复核：重复 ID 会让「推进」永远停在同一个环节（引擎按 ID 找下一环），
    /// 而「要求材料发完却一份都没声明」是自相矛盾 —— 删掉要求来让它合法是错的。
    /// </summary>
    public IReadOnlyList<string> ValidationIssues
    {
        get
        {
            var issues = new List<string>();
            if (Steps.Count == 0)
            {
                issues.Add("FLOW_EMPTY");
                return issues;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Steps.Count; i++)
            {
                var step = Steps[i];
                if (string.IsNullOrWhiteSpace(step.StepId))
                {
                    issues.Add($"FLOW_EMPTY_STEP_ID:{i + 1}");
                }
                else if (!seen.Add(step.StepId))
                {
                    issues.Add($"FLOW_DUPLICATE_STEP_ID:{step.StepId}");
                }

                if (step.RequiresAllMaterials && step.MaterialIds.Count == 0)
                {
                    issues.Add($"FLOW_MATERIAL_REQUIREMENT_WITHOUT_MATERIALS:{step.StepId}");
                }
            }

            return issues;
        }
    }

    /// <summary>
    /// 每个环节都有标题与来源依据、且声明本身没有矛盾时，才算「已确定」；
    /// 否则整包只能进 NeedsReview。
    /// </summary>
    public bool IsFullyDeclared =>
        Steps.Count > 0
        && ValidationIssues.Count == 0
        && Steps.All(s => !string.IsNullOrWhiteSpace(s.Title) && !s.NeedsReview);

    /// <summary>缺依据的环节标题，用于准备报告里点名复核对象（不含剧情内容）。</summary>
    public IReadOnlyList<string> StepsNeedingReview =>
        Steps.Where(s => string.IsNullOrWhiteSpace(s.Title) || s.NeedsReview)
            .Select((s, i) => string.IsNullOrWhiteSpace(s.Title) ? $"第 {i + 1} 步" : s.Title)
            .ToArray();
}

/// <summary>环节在玩法上的类别，决定规则核心允许哪些动作，而不是靠中文标题判断。</summary>
public enum ScriptStepKind
{
    /// <summary>阅本：开局，读自己的角色内容。</summary>
    Read = 0,

    /// <summary>开场说明：讲解规则与时长。</summary>
    Intro = 1,

    /// <summary>搜证：按编号逐个发放材料。</summary>
    Search = 2,

    /// <summary>讨论：玩家自由交流。</summary>
    Talk = 3,

    /// <summary>投票：录入纸笔汇总。</summary>
    Vote = 4,

    /// <summary>真相：公开结局。</summary>
    Reveal = 5,

    /// <summary>复盘：回顾。</summary>
    Review = 6,
}

/// <summary>一个环节的声明。字段全部来自剧本来源，不由软件推断。</summary>
public sealed record ScriptFlowStep
{
    public required string StepId { get; init; }

    public required string Title { get; init; }

    public ScriptStepKind Kind { get; init; } = ScriptStepKind.Talk;

    /// <summary>左侧「当前流程提示」的一句话，同时是可传模型的公开依据。留空则退回按条件拼接。</summary>
    public string? Hint { get; init; }

    /// <summary>本环节原本条件（无剧透）。</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];

    /// <summary>推进动作名，例如「进入第二轮搜证」。留空则按下一环节标题生成。</summary>
    public string? AdvanceLabel { get; init; }

    /// <summary>揭秘动作名，例如「公开结局」。留空则用「公开真相」。</summary>
    public string? RevealLabel { get; init; }

    /// <summary>本环节发放的材料编号（对应 <see cref="Material.MaterialId"/>）。</summary>
    public IReadOnlyList<string> MaterialIds { get; init; } = [];

    /// <summary>本环节材料必须逐个发完才能推进（两轮搜证这类流程的真实条件）。</summary>
    public bool RequiresAllMaterials { get; init; }

    /// <summary>本环节允许公开的段落 ID；为空时按 <see cref="PublicGroups"/> 解析。</summary>
    public IReadOnlyList<string> PublicSegmentIds { get; init; } = [];

    /// <summary>
    /// 本环节允许公开的来源分组名（导入时的目录名，例如「组织」「序章」）。
    /// 由分组名解析到实际段 ID，准备者不必知道软件生成的段 ID 规则。
    /// </summary>
    public IReadOnlyList<string> PublicGroups { get; init; } = [];

    /// <summary>
    /// 本环节提供「请主持人开场」这个动作。由流程声明决定，
    /// 不按「是不是第一个环节」这种固定序号推断 —— 序章本身可能就是第一步。
    /// </summary>
    public bool OffersOpeningSpeech { get; init; }

    /// <summary>来源依据：剧本原文里的页/句位置。为空表示这一步待复核。</summary>
    public string? SourceRef { get; init; }

    public bool NeedsReview => string.IsNullOrWhiteSpace(SourceRef);
}

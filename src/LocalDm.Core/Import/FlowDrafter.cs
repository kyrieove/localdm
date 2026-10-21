using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Core.Import;

/// <summary>一页手册：页号与识别文字。只有组织者手册的页会进这里。</summary>
public sealed record FlowDraftPage(int PageNumber, string Text);

/// <summary>
/// 起草流程要什么：手册全部页、剧本名与人数、两类分组的名字。
///
/// 只给线索卡与角色本的**分组名**，不给它们的任何文字：
/// 起草只需要知道「有哪几叠卡」，不需要读到卡面与角色本内容。
/// </summary>
public sealed record FlowDraftInput
{
    public required string Title { get; init; }

    public required int PlayerCount { get; init; }

    /// <summary>② 页确认的组织者手册分组名；写进 layering.organizerGroup，不让模型填。</summary>
    public required string OrganizerGroupName { get; init; }

    public required IReadOnlyList<FlowDraftPage> OrganizerPages { get; init; }

    public IReadOnlyList<string> MaterialGroupNames { get; init; } = [];

    public IReadOnlyList<string> RoleGroupNames { get; init; } = [];
}

/// <summary>起草结果。失败时 <see cref="Flow"/> 为 null，绝不返回一份看起来合理的默认流程。</summary>
public sealed record FlowDraftResult
{
    public required ScriptFlow? Flow { get; init; }

    /// <summary>失败原因（给导入的人看的一句话），成功时为 null。</summary>
    public string? FailureReason { get; init; }

    public int Attempts { get; init; }

    public bool Succeeded => Flow is not null;
}

/// <summary>
/// 依据组织者手册起草游戏流程。
///
/// 导入的人可能就是这局的玩家，所以提示词只允许输出流程与操作，
/// 产出还要过一遍剧透检查（<see cref="FlowSpoilerCheck"/>）才显示。
/// </summary>
public sealed class FlowDrafter
{
    private readonly IMagpieGateway _gateway;

    public FlowDrafter(IMagpieGateway gateway) => _gateway = gateway;

    /// <summary>
    /// 固定提示词。措辞是方案里定下的，不要改写：
    /// 它规定了只写流程不写剧情、kind 的取值、唯一 Reveal、页码依据与禁止输出 publicPages。
    /// 第 8 条给出 JSON 的确切结构：不给结构时，模型会自拟字段名，解析出来没有 steps。
    /// 分层只要 truthPages：publicPages 与 ignoredPages 都不让模型决定，解析时一律置空。
    /// </summary>
    public const string Prompt =
        """
        你在帮一个剧本杀软件整理流程。下面是组织者手册的全部页面（含真相），读完后输出这本剧本的游戏流程。
        看到这份流程的人可能就是这局的玩家，所以：
        1. title、hint、conditions 只写流程和操作（阅读、调查、讨论、投票、揭秘、复盘该做什么），不写任何剧情、人物关系、线索内容、凶手、手法或结局。
        2. 不写任何角色的秘密，也不写只有读过真相才知道的名词。
        3. kind 只能是 Read、Intro、Search、Talk、Vote、Reveal、Review 之一；必须有且只有一个 Reveal 环节，Reveal 之后才能出现 Review。
        4. sourceRef 写成"组织者手册第 N 页"，N 是这一环在手册里的依据页。
        5. layering.truthPages 列出所有写着真相、凶手、作案经过、复盘答案的页；拿不准的页也算进去。
        6. 不要输出 publicPages。
        7. 只输出 JSON，不要任何解释。
        8. JSON 必须是下面这个结构，字段名一字不改，steps 按游戏顺序排列：
        {
          "layering": { "truthPages": [8, 9] },
          "steps": [
            {
              "stepId": "read",
              "title": "阅本",
              "kind": "Read",
              "hint": "一句话说明这一环大家要做什么",
              "conditions": ["进入下一环之前要满足的条件"],
              "sourceRef": "组织者手册第 3 页",
              "offersOpeningSpeech": true
            }
          ]
        }
        stepId 用英文小写短词，各环节互不相同。offersOpeningSpeech 只在需要主持人开场讲背景的那一环写 true，其余写 false。上面的页号和文字只是格式示例，按手册实际内容填写。
        """;

    /// <summary>
    /// 起草流程：模型只输出 JSON，解析失败或校验不通过就带着失败原因重试一次。
    /// </summary>
    public async Task<FlowDraftResult> DraftAsync(
        FlowDraftInput input,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var request = BuildMessages(input);
        string? failure = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var messages = attempt == 1
                ? request
                : [.. request, new MagpieMessage { Role = "user", Content = $"上一次的输出不能用：{failure}。请严格按第 8 条的结构重新只输出 JSON。" }];

            var reply = await _gateway.CompleteAsync(
                new MagpieCompletionRequest { ModelId = modelId, Messages = messages },
                cancellationToken);

            var parsed = TryParse(reply, input, out failure);
            if (parsed is not null)
            {
                return new FlowDraftResult { Flow = parsed, Attempts = attempt };
            }
        }

        return new FlowDraftResult
        {
            Flow = null,
            FailureReason = failure ?? "AI 没能起草流程。",
            Attempts = 2,
        };
    }

    /// <summary>
    /// 组装请求：system 是固定提示词，user 是这本剧本的事实（页、人数、分组名）。
    /// 公开出来是为了让测试能直接核对请求里有什么、没有什么。
    /// </summary>
    public static IReadOnlyList<MagpieMessage> BuildMessages(FlowDraftInput input)
    {
        var user = new StringBuilder();
        user.AppendLine($"剧本名：{input.Title}");
        user.AppendLine($"玩家人数：{input.PlayerCount}");
        user.AppendLine(input.MaterialGroupNames.Count == 0
            ? "线索卡分组：无"
            : $"线索卡分组：{string.Join("、", input.MaterialGroupNames)}");
        user.AppendLine(input.RoleGroupNames.Count == 0
            ? "角色本分组：无"
            : $"角色本分组：{string.Join("、", input.RoleGroupNames)}");
        user.AppendLine();
        user.AppendLine("【组织者手册】");
        foreach (var page in input.OrganizerPages.OrderBy(page => page.PageNumber))
        {
            user.AppendLine($"第 {page.PageNumber} 页");
            user.AppendLine(page.Text);
        }

        return
        [
            new MagpieMessage { Role = "system", Content = Prompt },
            new MagpieMessage { Role = "user", Content = user.ToString() },
        ];
    }

    /// <summary>
    /// 解析并校验一次输出。任何一条不合格都返回 null 并把原因写进 <paramref name="failure"/>，
    /// 由调用方决定是否重试。
    /// </summary>
    private static ScriptFlow? TryParse(string reply, FlowDraftInput input, out string? failure)
    {
        DraftDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DraftDto>(StripCodeFence(reply), JsonOptions);
        }
        catch (JsonException)
        {
            // 不带异常原文：JsonException 的消息会把输入片段抄进来，而输入里是手册内容。
            failure = "JSON 解析失败";
            return null;
        }

        if (dto?.Steps is null || dto.Steps.Count == 0)
        {
            failure = "没有给出任何环节";
            return null;
        }

        var kinds = new List<ScriptStepKind>();
        foreach (var step in dto.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Kind))
            {
                // 模型没给类别时按讨论处理，后面还有「必须有揭示环节」这条兜底。
                kinds.Add(ScriptStepKind.Talk);
                continue;
            }

            var parsed = ParseKind(step.Kind);
            if (parsed is null)
            {
                failure = $"环节类别不认识：{step.Kind}";
                return null;
            }

            kinds.Add(parsed.Value);
        }

        var steps = dto.Steps.Select((step, index) => new ScriptFlowStep
        {
            // 模型漏写 stepId 时按顺序补一个：它只是内部标识，导入的人看不到，不值得为此整份重来。
            StepId = string.IsNullOrWhiteSpace(step.StepId) ? $"step-{index + 1}" : step.StepId.Trim(),
            Title = step.Title ?? string.Empty,
            Kind = kinds[index],
            Hint = string.IsNullOrWhiteSpace(step.Hint) ? null : step.Hint,
            Conditions = step.Conditions ?? [],
            SourceRef = string.IsNullOrWhiteSpace(step.SourceRef) ? null : step.SourceRef,
            OffersOpeningSpeech = step.OffersOpeningSpeech,
        }).ToArray();

        var maxPage = input.OrganizerPages.Count == 0 ? 0 : input.OrganizerPages.Max(page => page.PageNumber);
        var layering = new ScriptLayering
        {
            // 组织者手册的分组名由 ② 页确认，不让模型填。
            OrganizerGroup = input.OrganizerGroupName,
            // 一律置空：导入的人看不到正文，没法确认哪页能上公共屏幕；开场由 AI 主持自己讲。
            PublicPages = [],
            TruthPages = dto.Layering?.TruthPages ?? [],
            // 同样一律置空：导入的人看不到正文，也没法确认哪页能丢。手册第 2 页常同时有版权声明与
            // 开场信，整页丢掉后 AI 主持就读不到开场信息；多留几页只是多几百字。
            // ④ 页仍可手动把某页切成「不入包」。
            IgnoredPages = [],
        };

        var flow = new ScriptFlow { Steps = steps, Layering = layering };

        var issue = Validate(flow, maxPage);
        if (issue is not null)
        {
            failure = issue;
            return null;
        }

        failure = null;
        return flow;
    }

    /// <summary>
    /// 类别解析：英文枚举名与中文说法都认（提示词全是中文，模型常常直接答「阅本」「揭秘」）。
    /// 认不出返回 null，由调用方判为不合格。
    /// </summary>
    private static ScriptStepKind? ParseKind(string raw)
    {
        var text = raw.Trim();
        if (Enum.TryParse<ScriptStepKind>(text, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return text switch
        {
            "阅本" or "读本" or "阅读" => ScriptStepKind.Read,
            "开场" or "开场说明" or "介绍" => ScriptStepKind.Intro,
            "搜证" or "调查" or "搜查" => ScriptStepKind.Search,
            "讨论" or "交流" or "自由交流" => ScriptStepKind.Talk,
            "投票" or "表决" => ScriptStepKind.Vote,
            "揭秘" or "真相" or "公开真相" or "揭晓" => ScriptStepKind.Reveal,
            "复盘" or "回顾" => ScriptStepKind.Review,
            _ => null,
        };
    }

    /// <summary>结构校验：声明自身的问题、唯一 Reveal、Reveal 之后才有 Review、页号在手册范围内。</summary>
    private static string? Validate(ScriptFlow flow, int maxPage)
    {
        if (flow.ValidationIssues.Count > 0)
        {
            return "环节声明有问题：" + string.Join("、", flow.ValidationIssues);
        }

        var reveals = flow.Steps.Count(step => step.Kind == ScriptStepKind.Reveal);
        if (reveals != 1)
        {
            return $"揭晓环节应当有且只有一个，实际 {reveals} 个";
        }

        var revealIndex = flow.Steps.ToList().FindIndex(step => step.Kind == ScriptStepKind.Reveal);
        if (flow.Steps.Take(revealIndex).Any(step => step.Kind == ScriptStepKind.Review))
        {
            return "复盘不能排在揭晓之前";
        }

        var pages = (flow.Layering?.TruthPages ?? []).ToArray();
        var outOfRange = pages.Where(page => page < 1 || page > maxPage).ToArray();
        if (outOfRange.Length > 0)
        {
            return $"页号超出组织者手册的范围（共 {maxPage} 页）：{string.Join("、", outOfRange)}";
        }

        return null;
    }

    /// <summary>模型偶尔会把 JSON 包在 ``` 里：去掉围栏再解析，别为格式小事重试一次。</summary>
    private static string StripCodeFence(string reply)
    {
        var text = reply.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }

        var body = text[(firstLineEnd + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return fence >= 0 ? body[..fence].TrimEnd() : body;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class DraftDto
    {
        public DraftLayeringDto? Layering { get; set; }

        public List<DraftStepDto>? Steps { get; set; }
    }

    private sealed class DraftLayeringDto
    {
        /// <summary>模型可能输出它；解析出来也不采用（一律置空）。</summary>
        public List<int>? PublicPages { get; set; }

        public List<int>? TruthPages { get; set; }

        /// <summary>模型可能输出它；解析出来也不采用（一律置空，④ 页由人手动选）。</summary>
        public List<int>? IgnoredPages { get; set; }
    }

    private sealed class DraftStepDto
    {
        public string? StepId { get; set; }

        public string? Title { get; set; }

        /// <summary>模型可能给英文枚举名，也可能给中文（阅本/搜证/揭秘…）：两种都认。</summary>
        public string? Kind { get; set; }

        public string? Hint { get; set; }

        public List<string>? Conditions { get; set; }

        public string? SourceRef { get; set; }

        public bool OffersOpeningSpeech { get; set; }
    }
}

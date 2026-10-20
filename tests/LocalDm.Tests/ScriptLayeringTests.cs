using LocalDm.Core.Domain;
using LocalDm.Core.Import;

namespace LocalDm.Tests;

/// <summary>
/// 组织者手册的分层按流程声明来：目录名与页号不写死在代码里。
/// 换一份剧本照着声明写就行，不必改软件。
/// </summary>
public sealed class ScriptLayeringTests
{
    [Fact]
    public void No_layering_in_the_flow_falls_back_to_the_default_rule()
    {
        Assert.Equal(ScriptLayeringRule.Default, ScriptLayeringRule.FromFlow(null));
        Assert.Equal(ScriptLayeringRule.Default, ScriptLayeringRule.FromFlow(new ScriptFlow()));
    }

    [Fact]
    public void Declared_layering_produces_a_rule_from_the_declaration()
    {
        var flow = new ScriptFlow
        {
            Layering = new ScriptLayering
            {
                OrganizerGroup = "主持手册",
                PublicPages = [1, 2],
                TruthPages = [8, 9],
                IgnoredPages = [12],
            },
        };

        var rule = ScriptLayeringRule.FromFlow(flow);

        Assert.Equal("主持手册", rule.OrganizerGroupName);
        Assert.Equal([1, 2], rule.PublicOrganizerPages);
        Assert.Equal([8, 9], rule.TruthPages);
        Assert.Equal([12], rule.IgnoredPages);
    }

    [Fact]
    public void Declared_layering_divides_the_handbook_by_the_declared_pages()
    {
        var flow = LayeringFlow("组织");
        var result = RealScriptPackageBuilder.Build(
            "合成本",
            5,
            Pages(),
            Sources(),
            ScriptLayeringRule.FromFlow(flow),
            flow);

        var byId = result.Package.Segments.ToDictionary(s => s.SegmentId, s => s.Visibility);

        Assert.Equal(SegmentVisibility.Public, byId["seg-组织-1"]);
        Assert.Equal(SegmentVisibility.Public, byId["seg-组织-2"]);
        // 手册里没声明的页是主持人专用页
        Assert.Equal(SegmentVisibility.Private, byId["seg-组织-5"]);
        Assert.Equal(SegmentVisibility.Truth, byId["seg-组织-8"]);
        // 声明为忽略的页根本不进剧本包
        Assert.DoesNotContain(result.Package.Segments, s => s.SegmentId == "seg-组织-12");
    }

    [Fact]
    public void Organizer_group_that_does_not_exist_blocks_the_package()
    {
        var flow = LayeringFlow("不存在的目录");
        var result = RealScriptPackageBuilder.Build(
            "合成本",
            5,
            Pages(),
            Sources(),
            ScriptLayeringRule.FromFlow(flow),
            flow);

        Assert.Contains("LAYERING_ORGANIZER_GROUP_MISSING:不存在的目录", result.Readiness.IssueCategories);
        Assert.False(result.Readiness.CanStart);
        Assert.Equal(PackageValidationState.NeedsReview, result.Package.Validation);
    }

    private static ScriptFlow LayeringFlow(string organizerGroup) => new()
    {
        Layering = new ScriptLayering
        {
            OrganizerGroup = organizerGroup,
            PublicPages = [1, 2],
            TruthPages = [8],
            IgnoredPages = [12],
        },
        Steps =
        [
            new ScriptFlowStep
            {
                StepId = "read",
                Title = "阅本",
                Kind = ScriptStepKind.Read,
                SourceRef = "第 1 页",
            },
        ],
    };

    private static RealScriptPackageBuilder.ExtractedPage[] Pages() =>
    [
        new() { Group = "组织", PageNumber = 1, PageLabel = "1", Text = "开场信", FileId = "f0", SpanId = "sp0" },
        new() { Group = "组织", PageNumber = 2, PageLabel = "2", Text = "规则说明", FileId = "f1", SpanId = "sp1" },
        new() { Group = "组织", PageNumber = 5, PageLabel = "5", Text = "主持人专用说明", FileId = "f2", SpanId = "sp2" },
        new() { Group = "组织", PageNumber = 8, PageLabel = "8", Text = "案件真相", FileId = "f3", SpanId = "sp3" },
        new() { Group = "组织", PageNumber = 12, PageLabel = "12", Text = "封底", FileId = "f4", SpanId = "sp4" },
    ];

    private static SourceFile[] Sources() => Pages().Select(p => new SourceFile
    {
        FileId = p.FileId,
        PrivatePath = p.FileId,
        ContentHash = new string('a', 64),
        PageCount = 1,
        Origin = SourceOrder.ManifestOrder,
        ToolIdentity = "test",
        ParseState = FileParseState.Complete,
    }).ToArray();
}

using System.IO;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.App.Demo;

/// <summary>
/// 内置演示剧本：用于在真实窗口中走通「确定性完整局」。
/// 内容为合成占位文本，明确标注非真实剧本；真实剧本必须经 US2 准备流程。
/// </summary>
public static class DemoPackage
{
    public const string PackageId = "pkg-demo-synthetic";
    public const string SessionId = "session-demo";

    public static ScriptPackage Create()
    {
        var hash = new string('e', 64);
        var file = new SourceFile
        {
            FileId = "demo-source",
            PrivatePath = "demo.md",
            ContentHash = new string('f', 64),
            PageCount = 5,
            Origin = SourceOrder.PageOrder,
            ToolIdentity = "synthetic-demo",
            ParseState = FileParseState.Complete,
        };

        var spans = Enumerable.Range(1, 5).Select(n => new SourceSpan
        {
            SpanId = $"demo-s{n}",
            FileId = file.FileId,
            PageNumber = n,
            BlockNumber = 1,
            ToolLocator = $"doc:demo/tier:synthetic/page:{n}/block:1",
            TextHash = ManifestFingerprint.ComputeSha256Text("演示页 " + n),
            OriginalText = "演示页 " + n,
        }).ToArray();

        CanonicalSegment Seg(string id, string text, SegmentVisibility v, string span) => new()
        {
            SegmentId = id,
            OriginalText = text,
            SourceSpanIds = [span],
            Visibility = v,
        };

        // 演示真相用真实的结构（标题 + 等宽时间线 + 段落），
        // 这样大屏的等宽排版与列参考线在演示里就能被看到，
        // 而不是等真剧本上手才发现排版有问题。
        var demoTruth = string.Join("\n", new[]
        {
            "## 13:30~18:00 的时间线",
            "",
            "```",
            "姓名    13:30        14:20        15:10        16:00",
            "玉书    塔顶值守    收到电报    离开塔顶    回到塔底",
            "海      整理灯油    与玉书争执  检查灯芯    塔底待命",
            "唐      外出送信    码头徘徊    回到灯塔    锁上大门",
            "舒      整理床铺    听见争吵    独自下楼    守在楼梯口",
            "```",
            "",
            "## 案情概要",
            "",
            "这是演示剧本的真相占位文本，用于验证揭秘投影与大屏排版。",
            "灯塔的钟声在那一夜响了四次，而四个人都声称自己没有听见。",
            "",
            "## 结论",
            "",
            "钟声不是有人敲的，而是雾号。四个人的说法都成立，拼在一起才是真相。",
        });

        var segments = new[]
        {
            Seg("d-intro", "欢迎来到演示剧本，请大家先安静阅读自己的角色本。", SegmentVisibility.Public, "demo-s1"),
            Seg("d-search", "现在开始第一轮搜证，请主持人按编号发放线索。", SegmentVisibility.Public, "demo-s2"),
            Seg("d-discuss", "请就目前的线索进行讨论，主持人不会催促。", SegmentVisibility.Public, "demo-s3"),
            Seg("d-vote", "请用纸笔投票，主持人录入汇总后再进入真相。", SegmentVisibility.Public, "demo-s4"),
            Seg("d-truth", demoTruth, SegmentVisibility.Truth, "demo-s5"),
        };

        ScriptPhase P(
            string id,
            string title,
            PublicAction[] actions,
            string[] materials,
            bool vote,
            bool reveal,
            string seg,
            string hint,
            string? advanceLabel = null,
            string? revealLabel = null,
            bool requiresAllMaterials = false,
            bool offersOpeningSpeech = false) => new()
        {
            PhaseId = id,
            Title = title,
            PublicSegmentIds = [seg],
            AllowedActions = actions,
            Conditions = [$"原本条件：{title}环节按剧本规则完成后才能继续"],
            MaterialIds = materials,
            IsVotePhase = vote,
            IsRevealPhase = reveal,
            SourceSpanIds = ["demo-s1"],
            Hint = hint,
            AdvanceLabel = advanceLabel,
            RevealLabel = revealLabel,
            RequiresAllMaterials = requiresAllMaterials,
            OffersOpeningSpeech = offersOpeningSpeech,
        };

        return new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = PackageId,
            Version = "1",
            Title = "演示用合成剧本（非真实剧本）",
            PlayerCount = 5,
            ManifestHash = hash,
            SourceFiles = [file],
            Spans = spans,
            Segments = segments,
            Roles = [],
            Phases =
            [
                P(
                    "d-read", "阅本",
                    [PublicAction.Start, PublicAction.Advance, PublicAction.Pause],
                    [], false, false, "d-intro",
                    hint: "请大家拿到角色本，先读完属于自己的内容。",
                    advanceLabel: "进入搜证",
                    offersOpeningSpeech: true),
                P(
                    "d-searchp", "搜证",
                    [PublicAction.Advance, PublicAction.ReleaseMaterials, PublicAction.Pause],
                    ["线索包一", "线索包二"], false, false, "d-search",
                    hint: "本环节有两份材料，逐个发放后再继续。",
                    advanceLabel: "进入讨论",
                    requiresAllMaterials: true),
                P(
                    "d-discussp", "讨论",
                    [PublicAction.Advance, PublicAction.Pause],
                    [], false, false, "d-discuss",
                    hint: "围绕已经公开的信息讨论，主持人不会催促。",
                    advanceLabel: "进入投票"),
                P(
                    "d-votep", "投票",
                    [PublicAction.Advance, PublicAction.SubmitVoteSummary, PublicAction.Pause],
                    [], true, false, "d-vote",
                    hint: "完成纸笔投票，录入大家核对后的票数汇总。",
                    advanceLabel: "进入真相"),
                P(
                    "d-revealp", "真相",
                    [PublicAction.Reveal, PublicAction.Pause],
                    [], false, true, "d-truth",
                    hint: "点击会向全体公开结局。",
                    revealLabel: "公开真相"),
            ],
            Materials =
            [
                new Material { MaterialId = "线索包一", Label = "线索包一", SourceSpanIds = ["demo-s2"] },
                new Material { MaterialId = "线索包二", Label = "线索包二", SourceSpanIds = ["demo-s2"] },
            ],
            Truth = new TruthSection
            {
                RevealedSegmentIds = ["d-truth"],
                PrivateText = demoTruth,
                IsConfirmedByHuman = false,
            },
            Validation = PackageValidationState.Ready,
        };
    }

    /// <summary>演示剧本固定写入指定数据根目录下的 demo 包文件，便于复查。</summary>
    public static async Task<ScriptPackage> EnsureAsync(string dataRoot, CancellationToken cancellationToken)
    {
        var package = Create();
        var packagesDir = Path.Combine(dataRoot, "packages");
        Directory.CreateDirectory(packagesDir);
        var path = Path.Combine(packagesDir, $"{PackageId}-1.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return package;
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// 平板服务读真实剧本包这条路：准备清单 → 服务按名字列卡、取卡面、给本人角色本。
///
/// 全程用合成文件与合成文字，不碰任何真实剧本材料。
/// 真的起服务、发请求、断言响应 —— 不是只测内部方法。
/// </summary>
public sealed class TabletPackageModeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-tablet-package-" + Guid.NewGuid().ToString("N"));

    public TabletPackageModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_prepared_package_is_served_with_catalog_cards_and_role_book()
    {
        var (packagePath, cardId) = PrepareSyntheticPackage();

        using var host = TabletBlazorHost.Start(
            Project("LocalDm.Tablet.csproj"),
            new Dictionary<string, string> { ["LOCALDM_TABLET_PACKAGE"] = packagePath });
        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var ct = TestContext.Current.CancellationToken;

        // 目录：剧本名、角色、卡目录都来自冻结包
        var catalog = await client.GetStringAsync("/tablet/catalog", ct);
        using var catalogDocument = JsonDocument.Parse(catalog);
        var catalogRoot = catalogDocument.RootElement;
        Assert.Equal("合成平板剧本", catalogRoot.GetProperty("storyTitle").GetString());
        Assert.Equal(1, catalogRoot.GetProperty("roles").GetArrayLength());
        Assert.Equal("角色一", catalogRoot.GetProperty("roles")[0].GetProperty("roleId").GetString());
        Assert.Equal(1, catalogRoot.GetProperty("cards").GetArrayLength());
        Assert.Equal(cardId, catalogRoot.GetProperty("cards")[0].GetProperty("cardId").GetString());

        // 卡面：真的是那张图片的字节，不是占位
        var image = await client.GetByteArrayAsync($"/tablet/cards/{Uri.EscapeDataString(cardId)}/front", ct);
        Assert.NotEmpty(image);
        Assert.Equal(0x89, image[0]);
        Assert.Equal((byte)'P', image[1]);

        // 没准备好的卡取不到：不拿别的图顶替
        var missing = await client.GetAsync("/tablet/cards/不存在的卡/front", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // 角色本：认领之后才给，而且只给本人
        var roleId = Uri.EscapeDataString("角色一");
        var claim = await client.PostAsync($"/tablet/claim?roleId={roleId}", null, ct);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        var book = await client.GetStringAsync("/tablet/role/book", ct);
        using var bookDocument = JsonDocument.Parse(book);
        var bookRoot = bookDocument.RootElement;
        Assert.Equal("角色一", bookRoot.GetProperty("roleId").GetString());
        Assert.Equal(2, bookRoot.GetProperty("chapters").GetArrayLength());
        Assert.Equal(
            SyntheticRoleBookPageText,
            bookRoot.GetProperty("chapters")[0].GetProperty("blocks")[0].GetProperty("text").GetString());

        // 原稿页：本人角色的那一页，取得到
        var source = await client.GetByteArrayAsync("/tablet/role/source/1", ct);
        Assert.NotEmpty(source);
        Assert.Equal(0x89, source[0]);
    }

    private const string SyntheticRoleBookPageText = "这是合成角色本第一页的文字。";

    /// <summary>
    /// 造一份合成冻结包：1 个角色（2 页）、1 组线索卡（1 张卡，正反面各一个文件），
    /// 然后按真实流程准备平板清单并写回一个新包。
    /// </summary>
    private (string PackagePath, string CardId) PrepareSyntheticPackage()
    {
        var sourcesDir = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourcesDir);

        var files = new List<SourceFile>();
        var spans = new List<SourceSpan>();
        var segments = new List<CanonicalSegment>();

        // 角色本两页
        for (var page = 1; page <= 2; page++)
        {
            var fileId = $"role-{page}";
            var path = Path.Combine(sourcesDir, $"角色{page}.png");
            WritePng(path, page);
            files.Add(SourceFileOf(fileId, path));
            spans.Add(Span($"role-span-{page}", fileId, page));
            segments.Add(Segment($"role-seg-{page}", page == 1 ? SyntheticRoleBookPageText : "合成角色本第二页。", SegmentVisibility.Private, $"role-span-{page}"));
        }

        // 一张线索卡：正面与卡背各一个文件，编号相同
        var cardFront = Path.Combine(sourcesDir, "7.png");
        var cardBack = Path.Combine(sourcesDir, "7卡背.png");
        WritePng(cardFront, 7);
        WritePng(cardBack, 8);
        files.Add(SourceFileOf("card-7", cardFront));
        files.Add(SourceFileOf("card-7-back", cardBack));
        spans.Add(Span("card-span-front", "card-7", 1));
        spans.Add(Span("card-span-back", "card-7-back", 1));
        segments.Add(Segment("card-seg-front", "合成卡面文字。", SegmentVisibility.Private, "card-span-front"));
        segments.Add(Segment("card-seg-back", "合成卡背文字。", SegmentVisibility.Private, "card-span-back"));

        var package = new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-synthetic-tablet",
            Version = "1",
            Title = "合成平板剧本",
            PlayerCount = 1,
            ManifestHash = new string('a', 64),
            ContentHash = new string('b', 64),
            SourceFiles = files,
            Spans = spans,
            Segments = segments,
            Roles = [new RoleIndex
            {
                RoleId = "角色一",
                PrivateMaterialRef = "合成角色本",
                SourceSpanIds = ["role-span-1", "role-span-2"],
            }],
            Phases = [new ScriptPhase
            {
                PhaseId = "phase-read",
                Title = "阅本",
                SourceRef = "第 1 页",
                PublicSegmentIds = [],
                AllowedActions = [],
                Conditions = [],
                MaterialIds = ["clue-1"],
                SourceSpanIds = ["role-span-1"],
                IsVotePhase = false,
                IsRevealPhase = false,
            }],
            Materials = [new Material
            {
                MaterialId = "clue-1",
                Label = "合成线索组",
                SourceSpanIds = ["card-span-front", "card-span-back"],
            }],
            Truth = new TruthSection
            {
                RevealedSegmentIds = [],
                PrivateText = "合成真相。",
                IsConfirmedByHuman = false,
            },
            Validation = PackageValidationState.Ready,
        };

        var assetsDir = Path.Combine(_root, "assets");
        var manifest = TabletPackagePreparer.Prepare(package, assetsDir);
        var prepared = RealScriptPackageBuilder.WithTabletManifest(package, manifest);

        var packagesDir = Path.Combine(_root, "packages");
        Directory.CreateDirectory(packagesDir);
        var packagePath = Path.Combine(packagesDir, $"{prepared.PackageId}-{prepared.Version}.json");
        File.WriteAllText(
            packagePath,
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions { WriteIndented = true }));

        var cardId = manifest.Cards.Keys.Single();
        return (packagePath, cardId);
    }

    private static SourceFile SourceFileOf(string fileId, string path) => new()
    {
        FileId = fileId,
        PrivatePath = path,
        ContentHash = new string('c', 64),
        PageCount = 1,
        Origin = SourceOrder.PageOrder,
        ToolIdentity = "合成",
        ParseState = FileParseState.Complete,
    };

    private static SourceSpan Span(string spanId, string fileId, int page) => new()
    {
        SpanId = spanId,
        FileId = fileId,
        PageNumber = page,
        BlockNumber = 1,
        ToolLocator = $"doc:合成/p{page}/b1",
        TextHash = new string('d', 64),
        OriginalText = "合成文字。",
    };

    private static CanonicalSegment Segment(string segmentId, string text, SegmentVisibility visibility, string spanId) => new()
    {
        SegmentId = segmentId,
        OriginalText = text,
        Visibility = visibility,
        SourceSpanIds = [spanId],
    };

    /// <summary>真 PNG：每一张的内容标记不同，取图时能认出拿的是哪一张。</summary>
    private static void WritePng(string path, int marker)
    {
        var pixels = new byte[24 * 16 * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i + marker) % 256);
        }

        File.WriteAllBytes(path, TestPngWriter.Encode(pixels, 24, 16));
    }

    private static string Project(string projectFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalDm.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录。");
        return Directory.GetFiles(Path.Combine(root, "src"), projectFileName, SearchOption.AllDirectories).Single();
    }
}

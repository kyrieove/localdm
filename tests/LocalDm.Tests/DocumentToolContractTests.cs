using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;

namespace LocalDm.Tests;

/// <summary>T027 / T025：隐私预检与文档工具契约边界（不调用真实服务）。</summary>
public sealed class DocumentToolContractTests
{
    private const string AppHome = @"D:\2\dev\dm\.appdata\mineru-home";

    private static ToolIdentity Identity(string version = "4.0.1") => new()
    {
        Tool = "mineru",
        Version = version,
        AppOwnedHome = AppHome,
    };

    [Fact]
    public void Correct_version_app_owned_home_and_disabled_telemetry_allows_parsing()
    {
        var preflight = new MineruPrivacyPreflight(AppHome, @"C:\Users\x\.mineru");

        var result = preflight.Evaluate(Identity(), AppHome, "disabled");

        Assert.True(result.Allowed);
        Assert.Equal(PrivacyDecision.LocalOnly, result.Decision);
    }

    [Fact]
    public void Version_other_than_4_0_1_is_blocked()
    {
        var preflight = new MineruPrivacyPreflight(AppHome);

        var result = preflight.Evaluate(Identity("4.1.0"), AppHome, "disabled");

        Assert.False(result.Allowed);
        Assert.Equal("TOOL_VERSION_UNSUPPORTED", result.ErrorCode);
    }

    [Fact]
    public void Home_pointing_to_global_profile_is_blocked()
    {
        var preflight = new MineruPrivacyPreflight(AppHome);

        var result = preflight.Evaluate(Identity(), @"C:\Users\x\.mineru", "disabled");

        Assert.False(result.Allowed);
        Assert.Equal("TOOL_HOME_NOT_APP_OWNED", result.ErrorCode);
        Assert.Equal(PrivacyDecision.Blocked, result.Decision);
    }

    [Fact]
    public void Telemetry_unset_is_not_treated_as_disabled()
    {
        var preflight = new MineruPrivacyPreflight(AppHome);

        var result = preflight.Evaluate(Identity(), AppHome, "unset");

        Assert.False(result.Allowed);
        Assert.Equal("TOOL_TELEMETRY_UNKNOWN", result.ErrorCode);
    }

    [Fact]
    public void Unknown_home_or_missing_json_is_blocked_not_assumed()
    {
        var preflight = new MineruPrivacyPreflight(AppHome);
        var (home, telemetry) = MineruPrivacyPreflight.ReadStatusJson("not json", string.Empty);

        Assert.Null(home);
        Assert.Null(telemetry);
        Assert.False(preflight.Evaluate(Identity(), home, telemetry).Allowed);
    }

    [Fact]
    public void Status_json_parsing_reads_only_known_fields()
    {
        var (home, telemetry) = MineruPrivacyPreflight.ReadStatusJson(
            """{"running":true,"mineru_home":"D:\\2\\dev\\dm\\.appdata\\mineru-home","version":"4.0.1"}""",
            """{"state":"disabled","pending_metrics":0}""");

        Assert.Equal(AppHome, home);
        Assert.Equal("disabled", telemetry);
    }

    [Fact]
    public async Task Blocked_privacy_decision_prevents_tool_invocation()
    {
        // 用一个不存在的可执行文件：若代码真的去调用进程，会得到 TOOL_NOT_READY 而不是 CONSENT_REQUIRED
        using var runner = new MineruRunner(Path.Combine(Path.GetTempPath(), "no-such-mineru.exe"), AppHome);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => runner.ParseAsync(
            new Core.Ports.DocumentParseRequest
            {
                FileId = "f",
                AbsolutePath = "x.pdf",
                Kind = Core.Ports.DocumentKind.TextPdf,
                PrivacyDecision = PrivacyDecision.Blocked,
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(DomainErrorCode.CONSENT_REQUIRED, ex.Code);
    }

    [Fact]
    public async Task Unknown_privacy_decision_also_prevents_invocation()
    {
        using var runner = new MineruRunner(Path.Combine(Path.GetTempPath(), "no-such-mineru.exe"), AppHome);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => runner.ParseAsync(
            new Core.Ports.DocumentParseRequest
            {
                FileId = "f",
                AbsolutePath = "x.pdf",
                Kind = Core.Ports.DocumentKind.TextPdf,
                PrivacyDecision = PrivacyDecision.Unknown,
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(DomainErrorCode.CONSENT_REQUIRED, ex.Code);
    }

    [Fact]
    public void Locator_whitelist_accepts_documented_shapes()
    {
        Assert.True(IsOk("doc:abc/tier:standard/page:12"));
        Assert.True(IsOk("doc:abc/tier:standard/page:12/block:3"));
        Assert.True(IsOk("doc:abc-1_2/tier:flash/page:1/block:0/char:42"));
        Assert.False(IsOk("doc:abc/page:12"));
        Assert.False(IsOk("doc:abc/tier:standard/page:x"));
        Assert.False(IsOk("page:12"));

        static bool IsOk(string locator)
        {
            try
            {
                _ = LocalDm.Infrastructure.Import.SourceIndex.ParseLocator(locator);
                return true;
            }
            catch (DomainRuleException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task Real_material_without_consent_is_blocked_before_parse()
    {
        var preflight = new MineruPrivacyPreflight(AppHome);
        using var runner = new MineruRunner(Path.Combine(Path.GetTempPath(), "no-such-mineru.exe"), AppHome);
        var pipeline = new LocalDm.Infrastructure.Import.ImportPipeline(runner, preflight);

        // 反正在预检阶段就会失败（进程不存在），确认不会因为「未知外发边界」继续到解析
        var result = await pipeline.ImportFileAsync(
            "f",
            @"C:\real\script.pdf",
            Core.Ports.DocumentKind.TextPdf,
            3,
            consent: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalDm.Infrastructure.Import.ImportFileState.Blocked, result.State);
        Assert.NotNull(result.ErrorCode);
        Assert.Empty(result.Spans);
    }
}

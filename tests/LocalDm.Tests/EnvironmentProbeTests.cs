using System.Globalization;
using System.Runtime.InteropServices;
using LocalDm.Infrastructure.Diagnostics;

namespace LocalDm.Tests;

/// <summary>
/// 建立「测试能发现并通过」与目标平台前提的实证基线；不是产品行为测试。
/// </summary>
public sealed class EnvironmentProbeTests
{
    [Fact]
    public void RunsOnTargetWindows64Bit()
    {
        Assert.True(OperatingSystem.IsWindows(), "目标平台为 Windows。");
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
    }

    [Fact]
    public void RedactionRemovesKeyLikeValues()
    {
        var redacted = RedactedLog.Redact("请求头 Authorization: Bearer sk-abcdef123456 失败");

        Assert.DoesNotContain("sk-abcdef123456", redacted, StringComparison.Ordinal);
        Assert.Contains("失败", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ChineseCultureIsAvailableForUi()
    {
        Assert.False(string.IsNullOrWhiteSpace(CultureInfo.GetCultureInfo("zh-CN").DisplayName));
    }
}

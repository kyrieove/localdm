using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Documents;

/// <summary>
/// 隐私预检：核对工具版本、app-owned MINERU_HOME、规则/引擎/遥测状态。
/// 不复制用户全局规则与 key；本地边界不明确时拒绝解析（返回 TOOL_NOT_READY 或 Blocked）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MineruPrivacyPreflight
{
    public const string RequiredVersion = "4.0.1";

    private readonly string _appOwnedHome;
    private readonly string? _globalHome;

    public MineruPrivacyPreflight(string appOwnedHome, string? globalHome = null)
    {
        _appOwnedHome = appOwnedHome;
        _globalHome = globalHome;
    }

    public MineruPreflightResult Evaluate(ToolIdentity identity, string? homeReportedByTool, string? telemetryState)
    {
        if (!string.Equals(identity.Version, RequiredVersion, StringComparison.Ordinal))
        {
            return MineruPreflightResult.Blocked(
                "TOOL_VERSION_UNSUPPORTED",
                $"只支持已核验的 MinerU {RequiredVersion}，当前为 {identity.Version}。",
                PrivacyDecision.Blocked);
        }

        if (!SamePath(homeReportedByTool, _appOwnedHome))
        {
            return MineruPreflightResult.Blocked(
                "TOOL_HOME_NOT_APP_OWNED",
                "文档工具未使用应用专属目录，可能继承未知外发规则，已停止。",
                PrivacyDecision.Blocked);
        }

        if (_globalHome is not null && SamePath(_globalHome, _appOwnedHome))
        {
            return MineruPreflightResult.Blocked(
                "TOOL_HOME_IS_GLOBAL",
                "应用专属目录不能指向用户全局配置，已停止。",
                PrivacyDecision.Blocked);
        }

        if (!string.Equals(telemetryState, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            // 4.0.1 的 unset 不能当作 disabled
            return MineruPreflightResult.Blocked(
                "TOOL_TELEMETRY_UNKNOWN",
                "未能确认遥测已关闭，未提交材料。",
                PrivacyDecision.Blocked);
        }

        return MineruPreflightResult.Ok();
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
                .Replace('\\', '/')
                .ToLowerInvariant();

        return Normalize(left) == Normalize(right);
    }

    /// <summary>
    /// 解析 server status / telemetry status 的 JSON 输出，不执行其中任何命令字符串。
    /// </summary>
    public static (string? HomeReported, string? TelemetryState) ReadStatusJson(string serverStatusJson, string telemetryStatusJson)
    {
        string? home = null;
        string? telemetry = null;

        try
        {
            using var server = JsonDocument.Parse(serverStatusJson);
            if (server.RootElement.TryGetProperty("mineru_home", out var h))
            {
                home = h.GetString();
            }
        }
        catch (JsonException)
        {
            // 保持 null：未知即拒绝
        }

        try
        {
            using var tel = JsonDocument.Parse(telemetryStatusJson);
            if (tel.RootElement.TryGetProperty("state", out var s))
            {
                telemetry = s.GetString();
            }
        }
        catch (JsonException)
        {
            // 保持 null
        }

        return (home, telemetry);
    }
}

public sealed record MineruPreflightResult(bool Allowed, string? ErrorCode, string? PublicReason, PrivacyDecision Decision)
{
    public static MineruPreflightResult Ok() => new(true, null, null, PrivacyDecision.LocalOnly);

    public static MineruPreflightResult Blocked(string code, string reason, PrivacyDecision decision) =>
        new(false, code, reason, decision);
}

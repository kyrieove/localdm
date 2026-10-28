namespace LocalDm.Infrastructure.Diagnostics;

/// <summary>
/// 界面、错误和日志只呈现脱敏信息；原始 key、工具 stderr 全文和私有路径不直接进入 UI。
/// </summary>
public static partial class RedactedLog
{
    /// <summary>
    /// 移除常见凭据形态（Bearer 头、sk-/sk- 前缀 token、key=value）后再返回可显示的文本。
    /// 已知上限：不做语义判断，仅按固定形态擦除；不能替代不记录敏感字段的设计。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = BearerPattern().Replace(text, "$1" + Redacted);
        result = SkTokenPattern().Replace(result, Redacted);
        return KeyValuePattern().Replace(result, "$1" + Redacted);
    }

    public const string Redacted = "[redacted]";

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)\b(authorization\s*:\s*bearer)\s+\S+")]
    private static partial System.Text.RegularExpressions.Regex BearerPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{8,}")]
    private static partial System.Text.RegularExpressions.Regex SkTokenPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)\b(api[_-]?key|token|caller[_-]?key|secret)\s*[=:]\s*\S+")]
    private static partial System.Text.RegularExpressions.Regex KeyValuePattern();
}

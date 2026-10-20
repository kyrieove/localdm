namespace LocalDm.Core.Domain;

/// <summary>
/// 外部工具与模型的设置。caller key 只保存指向 DPAPI 密文的引用，绝不保存明文。
/// 应用数据模型 C12：未知外发边界视为禁止。
/// </summary>
public sealed record ToolSettings
{
    /// <summary>Windows Magpie 基础地址；首版仅允许 loopback。</summary>
    public required Uri MagpieBaseUri { get; init; }

    /// <summary>来自实际模型列表或用户明确填写的 model ID，不按显示名称猜测。</summary>
    public string? ModelId { get; init; }

    /// <summary>指向 DPAPI 密文的引用标识；原始 key 不序列化进普通设置与日志。</summary>
    public string? CallerKeyRef { get; init; }

    /// <summary>MinerU CLI 绝对路径。</summary>
    public string? MineruExecutable { get; init; }

    /// <summary>System.Speech 声音名称；为空表示跟随系统默认中文音色。</summary>
    public string? VoiceId { get; init; }

    /// <summary>主持台输入框提示（OpenLess 焦点提示）。</summary>
    public string InputHint { get; init; } = DefaultInputHint;

    /// <summary>
    /// 回复生成后是否自动朗读。关闭后只显示文字，仍可逐条重播。
    /// 默认开启：这是产品的主要使用方式；关掉是主持人的选择。
    /// </summary>
    public bool AutoRead { get; init; } = true;

    /// <summary>
    /// 所用模型的上下文窗口长度（tokens）。
    ///
    /// 上下文占用按这个窗口计量：快满时把较早的对话整理成备忘，而不是按固定字数截断。
    /// </summary>
    public int ContextWindowTokens { get; init; } = DefaultContextWindowTokens;

    /// <summary>
    /// 界面缩放倍数（LayoutTransform）：整个窗口内容按它放大缩小，Ctrl+加号、Ctrl+减号、Ctrl+滚轮都能改。
    /// </summary>
    public double UiScale { get; init; } = DefaultUiScale;

    /// <summary>对话与输入区的宽度档位。</summary>
    public ChatWidthMode ChatWidthMode { get; init; } = ChatWidthMode.Standard;

    public const int DefaultContextWindowTokens = 65_536;

    public const int MinContextWindowTokens = 8_192;

    public const int MaxContextWindowTokens = 2_000_000;

    public const double DefaultUiScale = 1.0;

    public const double MinUiScale = 0.8;

    public const double MaxUiScale = 2.0;

    /// <summary>加减一次的步长。</summary>
    public const double UiScaleStep = 0.1;

    public const string DefaultInputHint = "聚焦本框后使用 OpenLess 转写，回车发送，Shift+Enter 换行。";

    public static readonly Uri DefaultMagpieUri = new("http://127.0.0.1:3425/");

    public static ToolSettings Default { get; } = new()
    {
        MagpieBaseUri = DefaultMagpieUri,
    };

    /// <summary>
    /// 唯一允许的模型地址形态：loopback HTTP/HTTPS。其他地址一律拒绝，不自动切到别的供应商。
    /// </summary>
    public void Validate()
    {
        if (MagpieBaseUri is null || !MagpieBaseUri.IsAbsoluteUri)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "模型地址必须是绝对地址。");
        }

        if (!IsLoopback(MagpieBaseUri))
        {
            throw new DomainRuleException(
                DomainErrorCode.CONSENT_REQUIRED,
                "首版只允许连接本机 loopback 模型地址。");
        }

        if (MagpieBaseUri.Scheme is not ("http" or "https"))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "模型地址只支持 http 或 https。");
        }

        if (ContextWindowTokens is < MinContextWindowTokens or > MaxContextWindowTokens)
        {
            throw new DomainRuleException(
                DomainErrorCode.INVALID_INPUT,
                $"模型上下文长度必须在 {MinContextWindowTokens} 到 {MaxContextWindowTokens} tokens 之间。");
        }

        if (double.IsNaN(UiScale) || UiScale is < MinUiScale or > MaxUiScale)
        {
            throw new DomainRuleException(
                DomainErrorCode.INVALID_INPUT,
                $"界面缩放必须在 {MinUiScale} 到 {MaxUiScale} 之间。");
        }

        if (!Enum.IsDefined(ChatWidthMode))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "对话宽度只有标准、宽、满宽三档。");
        }
    }

    /// <summary>这一档对话与输入区的最大宽度；满宽返回 null，表示不限宽。</summary>
    public static double? ChatMaxWidth(ChatWidthMode mode) => mode switch
    {
        ChatWidthMode.Wide => 1320,
        ChatWidthMode.Full => null,
        _ => 980,
    };

    /// <summary>
    /// 玩家消息气泡的最大宽度：按同一比例放宽（标准档 620 / 980）。
    /// 气泡比整行窄，是为了让玩家的原话在一屏里一眼能和主持人的长文分开。
    /// </summary>
    public static double? ChatBubbleMaxWidth(ChatWidthMode mode) =>
        ChatMaxWidth(mode) is { } width ? width * 620 / 980 : null;

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 对话与输入区的宽度档位。三档都是 DIP，两端各留 34 的边距。
/// </summary>
public enum ChatWidthMode
{
    /// <summary>标准：980。</summary>
    Standard = 0,

    /// <summary>宽：1320。</summary>
    Wide = 1,

    /// <summary>满宽：不限宽，只剩两端边距。</summary>
    Full = 2,
}

/// <summary>
/// 数据外发授权：绑定目标工具、实际处理目的地、数据种类、源清单 hash 与允许范围。
/// 一次普通文本测试不构成真实剧本的外发授权。
/// </summary>
public sealed record DataConsent
{
    public required string ToolIdentity { get; init; }

    /// <summary>实际处理目的地（工具侧声明），不是「本机入口」。</summary>
    public required string Destination { get; init; }

    public required DataKind DataKind { get; init; }

    public required string SourceManifestHash { get; init; }

    public required ConsentScope Scope { get; init; }

    /// <summary>
    /// 未获授权时只能处理合成数据；未知外发边界视为禁止。
    /// </summary>
    public static bool AllowsRealMaterial(DataConsent? consent, string toolIdentity, string manifestHash) =>
        consent is not null
        && consent.Scope is ConsentScope.LocalProcessingOnly or ConsentScope.AllowRemoteProcessing
        && string.Equals(consent.ToolIdentity, toolIdentity, StringComparison.Ordinal)
        && string.Equals(consent.SourceManifestHash, manifestHash, StringComparison.Ordinal);
}

public enum DataKind
{
    /// <summary>合成测试材料。</summary>
    Synthetic = 0,

    /// <summary>真实剧本 / 真实文档。</summary>
    RealScript = 1,
}

public enum ConsentScope
{
    None = 0,

    /// <summary>只允许本机离线处理，不涉及任何外发。</summary>
    LocalProcessingOnly = 1,

    /// <summary>明确允许该目的地进行远程处理。</summary>
    AllowRemoteProcessing = 2,
}

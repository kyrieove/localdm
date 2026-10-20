using LocalDm.Core.Domain;

namespace LocalDm.Core.Ports;

/// <summary>
/// Windows Magpie 普通文字模型契约。仅为本项目验证的最小子集：/v1/models 与非流式 /v1/chat/completions。
/// </summary>
public interface IMagpieGateway
{
    /// <summary>
    /// 列出实际可用的 model ID。/health 返回 404 是正常现象，不能据此判定离线。
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 非流式生成：返回 choices[0].message.content 的非空文字；空内容或无效 JSON 一律视为失败。
    /// </summary>
    Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 非流式生成并带回 token 用量。
    ///
    /// 默认实现退回到 <see cref="CompleteAsync"/>：拿不到用量不是错误，
    /// 只是这一轮只能按估算计量上下文占用。
    /// </summary>
    async Task<MagpieCompletion> CompleteWithUsageAsync(
        MagpieCompletionRequest request,
        CancellationToken cancellationToken) =>
        new(await CompleteAsync(request, cancellationToken), null);
}

/// <summary>
/// 一次模型调用的 token 用量。
/// CacheHitTokens 是 DeepSeek 的可选字段：命中前缀缓存的那部分 prompt token。
/// </summary>
public sealed record MagpieUsage(int PromptTokens, int CompletionTokens, int? CacheHitTokens);

/// <summary>模型返回的文字与用量。用量为 null 表示这次响应没有带来可用的 usage。</summary>
public sealed record MagpieCompletion(string Content, MagpieUsage? Usage);

public sealed record MagpieCompletionRequest
{
    public required string ModelId { get; init; }

    /// <summary>普通输入只包含当前公开内容，绝不含私有剧本或真相。</summary>
    public required IReadOnlyList<MagpieMessage> Messages { get; init; }

    public bool Stream { get; init; }
}

public sealed record MagpieMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

/// <summary>
/// Windows TTS 契约：单队列、单段播放，Speak/Stop/Replay。
/// </summary>
public interface ITtsService
{
    /// <summary>已安装的中文声音。</summary>
    IReadOnlyList<TtsVoice> GetChineseVoices();

    /// <summary>把 safeText 排入唯一播放队列；返回播完或失败。</summary>
    Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken);

    /// <summary>取消当前及队列中的播音，清空待播项；不生成游戏事件。</summary>
    void Stop();

    /// <summary>复用已有 safeText 重播：不请求模型、不写状态、不重新提交操作。</summary>
    Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken);

    /// <summary>当前是否在播音。</summary>
    bool IsSpeaking { get; }
}

/// <summary>
/// 一个可用的中文音色。
/// 只描述「是什么」，不描述「怎么展示」：面向用户的中文名属于界面层，
/// 放在 Core 会让领域模型承担展示职责。
/// </summary>
public sealed record TtsVoice
{
    public required string Name { get; init; }

    public required string Culture { get; init; }
}

/// <summary>
/// 文档工具（MinerU 4.0.1）契约。调用前必须通过隐私预检，未知边界返回 TOOL_NOT_READY。
/// </summary>
public interface IDocumentTool
{
    Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 提取一个文件；PDF 必须显式 --pages all，图片不带 --pages。
    /// 返回的批次可能带下一请求，调用方必须续读到 next_request=null。
    /// </summary>
    Task<DocumentExtraction> ParseAsync(DocumentParseRequest request, CancellationToken cancellationToken);
}

public sealed record ToolIdentity
{
    public required string Tool { get; init; }

    public required string Version { get; init; }

    /// <summary>应用专属 MINERU_HOME，不指向用户全局配置。</summary>
    public required string AppOwnedHome { get; init; }
}

public sealed record DocumentParseRequest
{
    public required string FileId { get; init; }

    public required string AbsolutePath { get; init; }

    public required DocumentKind Kind { get; init; }

    /// <summary>应用层已确认的外发边界；Blocked 时实现必须拒绝调用。</summary>
    public required PrivacyDecision PrivacyDecision { get; init; }

    /// <summary>
    /// 解析档位。必须由调用方显式指定，不自动降级：
    /// flash 是本机轻量档，basic/standard/advanced 需要 parse-server（不自动启用）。
    /// </summary>
    public ParseTier Tier { get; init; } = ParseTier.Flash;
}

/// <summary>与 MinerU 4.0.1 公开档位一一对应。</summary>
public enum ParseTier
{
    /// <summary>本机轻量档：无需额外服务，能读文本层与图片页。</summary>
    Flash = 0,

    Basic = 1,
    Standard = 2,
    Advanced = 3,
}

public enum DocumentKind
{
    TextPdf = 0,
    ScanPdf = 1,
    Image = 2,
}

public sealed record DocumentExtraction
{
    public required string FileId { get; init; }

    /// <summary>工具层是否完成：parse.status=done、content 非 null、无 error、无待继续请求。</summary>
    public required bool ToolComplete { get; init; }

    public required IReadOnlyList<ExtractionBatch> Batches { get; init; }

    public ContinuationRequest? NextRequest { get; init; }

    /// <summary>脱敏警告，例如 stderr 已擦除后的要点。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 会话存储契约：event、receipt 与 session projection 在同一事务提交。
/// </summary>
public interface ISessionStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>读取当前会话；不存在返回 null。</summary>
    Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<OperationReceipt?> FindReceiptAsync(string sessionId, string operationId, CancellationToken cancellationToken);

    /// <summary>
    /// 原子提交：校验 revision → 去重 receipt → 插入 event → 更新投影。
    /// 事务失败时三者均不改变。
    /// </summary>
    Task CommitAsync(
        Session current,
        Session next,
        GameCommand command,
        GameEvent gameEvent,
        OperationReceipt receipt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GameEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken);

    Task SaveSessionAsync(Session session, CancellationToken cancellationToken);

    /// <summary>
    /// 列出所有存档的绑定信息（只有 id、包标识、状态与 revision，不含剧情内容）。
    /// 用来发现「存档还在、绑定的剧本包已经不在」的局面：那种局面必须明说不能恢复。
    /// </summary>
    Task<IReadOnlyList<StoredSessionRef>> ListSessionsAsync(CancellationToken cancellationToken);
}

/// <summary>一个存档绑定了哪份冻结包。只有标识与进度，没有任何剧情文本。</summary>
public sealed record StoredSessionRef(
    string SessionId,
    string PackageId,
    string PackageVersion,
    string ManifestHash,
    SessionState State,
    string? CurrentPhaseId,
    int Revision);

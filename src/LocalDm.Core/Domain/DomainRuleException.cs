namespace LocalDm.Core.Domain;

/// <summary>
/// 领域错误码。界面、日志与证据只呈现这些脱敏代码与公共原因，不呈现原始异常全文。
/// </summary>
public enum DomainErrorCode
{
    INVALID_INPUT = 0,
    MISSING_SOURCE = 1,
    SOURCE_MISMATCH = 2,
    NOT_READY = 3,
    STALE_REVISION = 4,
    OPERATION_CONFLICT = 5,
    PHASE_CONDITION_UNMET = 6,
    VOTE_INVALID = 9,
    PACKAGE_MISMATCH = 10,
    TOOL_NOT_READY = 11,
    CONSENT_REQUIRED = 12,
    TTS_UNAVAILABLE = 13,
    TTS_FAILED = 14,
    REQUEST_CANCELLED = 15,
    MODEL_FAILED = 16,
    STORAGE_FAILED = 17,
    UNSUPPORTED = 18,
}

/// <summary>
/// 可以在界面显示的领域错误：只带错误码和脱敏后的公共原因。
/// </summary>
public sealed class DomainRuleException : Exception
{
    public DomainRuleException(DomainErrorCode code, string publicReason)
        : base(publicReason)
    {
        Code = code;
        PublicReason = publicReason;
    }

    public DomainErrorCode Code { get; }

    /// <summary>可直接展示给玩家的原因，不含私有文本、路径或原始 stderr。</summary>
    public string PublicReason { get; }
}

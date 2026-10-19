using System.Runtime.Versioning;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 恢复：检查包指纹与数据库完整性，恢复最后完整事务。
/// 失配返回 PACKAGE_MISMATCH，不能换另一个包继续；未提交操作不补执行。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionRecovery
{
    private readonly ISessionStore _store;
    private readonly SessionStore? _concrete;

    public SessionRecovery(ISessionStore store)
    {
        _store = store;
        _concrete = store as SessionStore;
    }

    public async Task<RecoveryResult> RecoverAsync(
        ScriptPackage package,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (_concrete is not null && !await _concrete.CheckIntegrityAsync(cancellationToken))
        {
            return RecoveryResult.Failed(
                DomainErrorCode.STORAGE_FAILED,
                "存档文件完整性检查未通过，未继续读取。",
                null);
        }

        var session = await _store.LoadSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return RecoveryResult.Failed(
                DomainErrorCode.INVALID_INPUT,
                "找不到该局的存档。",
                null);
        }

        try
        {
            session.ValidateBindings(package.PackageId, package.Version, package.ManifestHash);
        }
        catch (DomainRuleException ex)
        {
            return RecoveryResult.Failed(DomainErrorCode.PACKAGE_MISMATCH, ex.PublicReason, session);
        }

        // 冻结包文件指纹校验：任何一个源文件被改动都不能继续该局
        foreach (var file in package.SourceFiles)
        {
            if (!File.Exists(file.PrivatePath))
            {
                // 源文件不在本机（如外部材料包路径）时只用包内 hash 绑定，不做静默替换
                continue;
            }

            var actual = ManifestFingerprint.ComputeSha256(file.PrivatePath);
            if (!string.Equals(actual, file.ContentHash, StringComparison.Ordinal))
            {
                return RecoveryResult.Failed(
                    DomainErrorCode.PACKAGE_MISMATCH,
                    "剧本文件已被改动，不能继续该局。",
                    session);
            }
        }

        return RecoveryResult.Ok(session);
    }

    /// <summary>恢复后播音状态一律为 Idle；绝不自动重播或补做未确认操作。</summary>
    public static SpeechState RestoredSpeechState => SpeechState.Idle;
}

public sealed record RecoveryResult(
    bool Recovered,
    DomainErrorCode? ErrorCode,
    string? PublicReason,
    Session? Session)
{
    public static RecoveryResult Ok(Session session) => new(true, null, null, session);

    public static RecoveryResult Failed(DomainErrorCode code, string reason, Session? session) =>
        new(false, code, reason, session);
}

using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Core.Game;

/// <summary>
/// 命令分发：先按 operationId 去重 → 交给规则核心 → 事务内写 event/receipt/projection。
/// 返回 Committed / Duplicate / Rejected，不返回原始异常。
/// </summary>
public sealed class CommandDispatcher
{
    private readonly ISessionStore _store;

    public CommandDispatcher(ISessionStore store)
    {
        _store = store;
    }

    public async Task<DispatchResult> DispatchAsync(
        GameCommand command,
        ScriptPackage package,
        CancellationToken cancellationToken)
    {
        try
        {
            command.Validate();

            var existing = await _store.FindReceiptAsync(command.SessionId, command.OperationId, cancellationToken);
            if (existing is not null)
            {
                return SamePayload(existing, command)
                    ? new DispatchResult(DispatchOutcome.Duplicate, existing, null, null)
                    : Rejected(DomainErrorCode.OPERATION_CONFLICT, "同一 operationId 提交了不同内容。");
            }

            var current = await _store.LoadSessionAsync(command.SessionId, cancellationToken)
                          ?? throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "会话不存在。");

            if (current.Revision != command.ExpectedRevision)
            {
                return Rejected(DomainErrorCode.STALE_REVISION, "当前 revision 已变化，请重新读取阶段。");
            }

            var next = GameEngine.Apply(current, package, command);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var receipt = new OperationReceipt
            {
                SessionId = command.SessionId,
                OperationId = command.OperationId,
                PayloadFingerprint = command.PayloadFingerprint(),
                Revision = next.Revision,
                Type = command.Type,
                CommittedAtUnixMs = now,
            };
            var gameEvent = new GameEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                SessionId = command.SessionId,
                OperationId = command.OperationId,
                Revision = next.Revision,
                Type = command.Type,
                SafePayload = SafePayload(current, next, command),
                CommittedAtUnixMs = now,
            };

            await _store.CommitAsync(current, next, command, gameEvent, receipt, cancellationToken);

            var projection = PublicSessionProjector.Project(package, next);
            return new DispatchResult(DispatchOutcome.Committed, receipt, projection, null);
        }
        catch (DuplicateReceiptSignal duplicate)
        {
            // 存储层在事务内发现同 ID 同内容：返回原结果，不再产生第二次状态变化
            return new DispatchResult(DispatchOutcome.Duplicate, duplicate.Original, null, null);
        }
        catch (DomainRuleException ex)
        {
            return Rejected(ex.Code, ex.PublicReason);
        }
        catch (InvalidOperationException)
        {
            return Rejected(DomainErrorCode.STORAGE_FAILED, "存档写入失败，操作未生效。");
        }
    }

    private static bool SamePayload(OperationReceipt existing, GameCommand command) =>
        string.Equals(existing.PayloadFingerprint, command.PayloadFingerprint(), StringComparison.Ordinal);

    /// <summary>读取当前会话状态（恢复与界面刷新用）。</summary>
    public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _store.LoadSessionAsync(sessionId, cancellationToken);

    private static DispatchResult Rejected(DomainErrorCode code, string reason) =>
        new(DispatchOutcome.Rejected, null, null, new Rejection(code, reason));

    /// <summary>安全载荷：只记录阶段、编号与计数，不拷贝完整真相。</summary>
    private static string SafePayload(Session before, Session after, GameCommand command) =>
        JsonSerializer.Serialize(new
        {
            type = command.Type.ToString(),
            fromState = before.State.ToString(),
            toState = after.State.ToString(),
            fromPhase = before.CurrentPhaseId,
            toPhase = after.CurrentPhaseId,
            materials = command.MaterialIds.Count > 0 ? command.MaterialIds : null,
            votes = command.Votes?.Total,
        });
}

/// <summary>
/// 存储层发现「同 operationId 同内容」时抛出的信号：调用方应返回原 receipt，而不是当作失败。
/// 定义在 Core，让存储实现可以抛出而无需依赖具体数据库类型。
/// </summary>
public class DuplicateReceiptSignal : Exception
{
    public DuplicateReceiptSignal(OperationReceipt original)
        : base("同一 operationId 已提交过，返回原结果。")
    {
        Original = original;
    }

    public OperationReceipt Original { get; }
}

public enum DispatchOutcome
{
    Committed = 0,
    Duplicate = 1,
    Rejected = 2,
}

public sealed record DispatchResult(
    DispatchOutcome Outcome,
    OperationReceipt? Receipt,
    PublicSessionProjection? Projection,
    Rejection? Rejection);

public sealed record Rejection(DomainErrorCode Code, string PublicReason);

using LocalDm.Core.Domain;

namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 同一 operationId 已提交过，且内容完全一致：返回原结果，不再执行。
/// 不是错误，不产生第二次状态变化。
/// </summary>
public sealed class DuplicateOperationException : LocalDm.Core.Game.DuplicateReceiptSignal
{
    public DuplicateOperationException(OperationReceipt original)
        : base(original)
    {
    }
}

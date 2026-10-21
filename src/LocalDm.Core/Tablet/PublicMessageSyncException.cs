namespace LocalDm.Core.Tablet;

/// <summary>
/// 公开消息没存进去或没读出来时的异常。
///
/// 界面据此显示「同步异常」：不能吞掉，也不能报告「已同步」。
/// 出了这个错要停住继续累加 —— 序号一旦跳号，平板补发就会出现缺口。
/// </summary>
public sealed class PublicMessageSyncException : Exception
{
    public PublicMessageSyncException(string publicReason, Exception? innerException = null)
        : base(publicReason, innerException)
    {
        PublicReason = publicReason;
    }

    /// <summary>可直接展示给主持人的原因，不含私有文本与路径。</summary>
    public string PublicReason { get; }
}

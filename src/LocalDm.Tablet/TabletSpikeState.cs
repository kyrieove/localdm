using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 合成材料探针用的会话状态。业务状态放在服务端单例里，
/// 不依赖浏览器 circuit：刷新后重新建立页面时仍能读回同一份。
/// </summary>
public sealed class TabletSpikeState
{
    private readonly object _gate = new();
    private string? _roleId;

    public TabletSpikeState(TabletSpikeData data) => Data = data;

    public TabletSpikeData Data { get; }

    public TabletSpikeRole? Role
    {
        get
        {
            lock (_gate)
            {
                return _roleId is null ? null : Data.Roles.FirstOrDefault(r => r.RoleId == _roleId);
            }
        }
    }

    public IReadOnlyList<TabletSpikeRole> Roles => Data.Roles;

    public IReadOnlyList<TabletSpikeCard> Cards => Data.Cards;

    public string? SelectedCardId { get; private set; }

    public int? RolePage { get; private set; }

    public void Claim(string roleId)
    {
        lock (_gate)
        {
            _roleId = roleId;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _roleId = null;
            SelectedCardId = null;
            RolePage = null;
        }
    }

    public void Select(string cardId) => SelectedCardId = cardId;

    public void OpenPage(int pageNumber) => RolePage = pageNumber;
}

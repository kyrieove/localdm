using System.Collections.Concurrent;
using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;
/// <summary>
/// 合成材料探针用的会话状态。
///
/// 每个浏览器会话一份（按 Cookie 取键），6 台设备同时加入时各认各的角色；
/// 同时实现「已认领的角色不能再被别人认领」，与产品契约一致。
/// 业务状态存在服务端字典里，不依赖浏览器连接：刷新后重新建立 circuit 仍读回同一份。
/// </summary>
public sealed class TabletSpikeState
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly object _claimGate = new();

    public TabletSpikeState(ITabletContent content)
    {
        Content = content;
        if (content is SwitchableTabletContent switchable)
        {
            // 换剧本：所有设备的认领与阅读状态一并清掉，页面自动回到选角色页或等待页。
            switchable.Changed += () =>
            {
                Interlocked.Increment(ref _contentGeneration);
                ResetAll();
                TabletSessions.RaiseChanged();
            };
        }
    }

    private int _contentGeneration;

    /// <summary>
    /// 换剧本或重新开始一局时加一。重新开始沿用同一个存档 id，公开消息序号又从头数，
    /// 网页只比 id 会留着上一局的对话、漏掉新消息，所以还要比这个数。
    /// </summary>
    public int ContentGeneration => Volatile.Read(ref _contentGeneration);

    /// <summary>当前剧本状态；合成探针与真实包始终是 Ready。</summary>
    public TabletStoryState StoryState => Content.StoryState;

    private void ResetAll()
    {
        lock (_claimGate)
        {
            foreach (var session in _sessions.Values)
            {
                session.RoleId = null;
                session.SelectedCardId = null;
                session.ReadingOffset = 0;
                session.OpenedPages = 0;
                session.Settings = session.Settings with { SourcePageNumber = 1 };
            }
        }
    }

    /// <summary>内容来源：合成探针或真实剧本包。</summary>
    public ITabletContent Content { get; }

    public IReadOnlyList<TabletRole> Roles => Content.Roles;

    public IReadOnlyList<TabletCatalogItem> Cards => Content.Cards;

    /// <summary>取一个会话；不存在时建一份空的。</summary>
    public SessionEntry Session(string key) =>
        _sessions.GetOrAdd(key, _ => new SessionEntry());

    /// <summary>已被别人认领的角色 ID 集合。</summary>
    public IReadOnlyList<string> ClaimedRoles
    {
        get
        {
            lock (_claimGate)
            {
                return _sessions.Values
                    .Select(s => s.RoleId)
                    .Where(id => id is not null)
                    .Select(id => id!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>认领角色：被别人抢先认领时返回 false，界面提示重选。</summary>
    public bool TryClaim(string key, string roleId)
    {
        lock (_claimGate)
        {
            var current = Session(key).RoleId;
            if (string.Equals(current, roleId, StringComparison.Ordinal))
            {
                return true;
            }

            var taken = _sessions.Values.Any(s => string.Equals(s.RoleId, roleId, StringComparison.Ordinal));
            if (taken)
            {
                return false;
            }

            var session = Session(key);
            session.RoleId = roleId;
            session.SelectedCardId = null;
            session.ReadingOffset = 0;
            session.OpenedPages = 0;
            session.Settings = session.Settings with { SourcePageNumber = 1 };
            return true;
        }
    }

    public void Release(string key)
    {
        lock (_claimGate)
        {
            var session = Session(key);
            session.RoleId = null;
            session.SelectedCardId = null;
            session.ReadingOffset = 0;
            session.OpenedPages = 0;
            session.Settings = session.Settings with { SourcePageNumber = 1 };
        }
    }

    /// <summary>
    /// 电脑上释放某个角色：平板关了浏览器或换了 App 扫码后 Cookie 就丢了，
    /// 旧会话还占着角色；释放后新设备能重新认领。
    /// </summary>
    public void ReleaseRole(string roleId)
    {
        lock (_claimGate)
        {
            foreach (var (key, session) in _sessions)
            {
                if (string.Equals(session.RoleId, roleId, StringComparison.Ordinal))
                {
                    Release(key);
                }
            }
        }
    }

    /// <summary>翻开第 page 页：只有紧接着的下一页能翻，返回是否翻开。</summary>
    public bool OpenPage(string key, int page)
    {
        lock (_claimGate)
        {
            var session = Session(key);
            if (session.RoleId is null || page != session.OpenedPages + 1)
            {
                return false;
            }

            session.OpenedPages = page;
            return true;
        }
    }

    public sealed class SessionEntry
    {
        public string? RoleId { get; set; }

        public string? SelectedCardId { get; set; }

        /// <summary>阅读位置：按文本字符偏移量保存，字号与窗口变化后仍能回到读到的位置。</summary>
        public long ReadingOffset { get; set; }

        /// <summary>角色本原稿已按顺序翻开的页数；没翻开的页平板上显示卡背。</summary>
        public int OpenedPages { get; set; }

        public ReadingSettings Settings { get; set; } = new();
    }
}

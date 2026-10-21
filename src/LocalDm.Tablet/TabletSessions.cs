namespace LocalDm.Tablet;

/// <summary>
/// 浏览器会话键与变更通知。探针用 Cookie 识别设备，
/// 这样刷新、断线重连后仍能拿回同一份角色与页码。
/// </summary>
public static class TabletSessions
{
    public const string CookieName = "localdm-tablet-spike";

    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// 没有 Cookie 时现场发一个，保证每台设备拿到不同键。
    /// 返回 null 表示这次请求没有会话（Cookie 缺失或被清掉）：
    /// 图片接口据此拒绝，客户端不能靠「不带 Cookie」绕过认领。
    /// </summary>
    public static string? KeyFor(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var existing) && existing.Length > 0)
        {
            return existing;
        }

        return null;
    }

    /// <summary>发一个新会话 Cookie 并返回它的键。</summary>
    public static string Issue(HttpContext context)
    {
        var fresh = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(CookieName, fresh, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // 探针只在局域网跑，不设 Secure；一期产品用 HTTP 时同样不能设。
            IsEssential = true,
        });
        return fresh;
    }
}

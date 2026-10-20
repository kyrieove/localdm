using LocalDm.App.Demo;
using LocalDm.Core.Domain;

namespace LocalDm.App;

/// <summary>
/// 一局存档的 id 规则。
///
/// 之前「装载剧本」用 <c>session-{包id}</c>，而内置演示剧本用的是固定 id，
/// 两条路径各算各的：同一局演示从首页进和从主持台进会落到两个不同的存档里，
/// 进度互相看不见。规则收在这里，只有一处。
/// </summary>
public static class SessionIds
{
    public static string For(ScriptPackage package) =>
        string.Equals(package.PackageId, DemoPackage.PackageId, StringComparison.Ordinal)
            ? DemoPackage.SessionId
            : "session-" + package.PackageId;
}

using LocalDm.Core.Hosting;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 估算每个已导入剧本的主持 system 消息有多少 token，其中角色本占多少。只打印数字，不打印剧本内容。
/// </summary>
internal static class ContextSizeProbe
{
    public static int Run(string[] args)
    {
        var dataRoot = args.Length >= 2 && args[0] == "--data" ? args[1] : SessionContext.ResolveDataRoot();
        var dir = Path.Combine(dataRoot, "packages");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"没有剧本包目录：{dir}");
            return 2;
        }

        foreach (var path in Directory.GetFiles(dir, "pkg-*.json").Where(p => !p.EndsWith(".flow.json", StringComparison.Ordinal)))
        {
            var package = DmAcceptanceProbe.LoadPackage(path);
            var system = ContextBudget.EstimateTokens(HostNarration.BuildSystemPrompt(package));
            var roleBooks = package.Segments
                .Where(segment => HostNarration.IsRoleBookSegment(package, segment))
                .Sum(segment => ContextBudget.EstimateTokens(segment.OriginalText));
            Console.WriteLine($"{package.PackageId} 人数={package.PlayerCount} system≈{system} token，其中角色本≈{roleBooks} token");
        }

        return 0;
    }
}

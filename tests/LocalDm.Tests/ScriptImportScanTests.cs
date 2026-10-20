using System.IO;
using LocalDm.Infrastructure.Import;

namespace LocalDm.Tests;

/// <summary>
/// 扫描与猜类别：只在临时目录里造文件夹结构，放几张 1×1 的图片。
/// 不读 stories/、.appdata/、.local/ 下的任何文件，也不跑识别。
/// </summary>
public sealed class ScriptImportScanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-scan-" + Guid.NewGuid().ToString("N"));

    public ScriptImportScanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
    }

    private ScriptImportService NewService() => new();

    [Fact]
    public void Folders_with_images_become_groups_and_get_guessed_by_name_and_path()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "角色本装订/组织/1.jpg");
        WritePage(story, "角色本装订/组织/2.jpg");
        WritePage(story, "角色本装订/张三/1.jpg");
        WritePage(story, "线索卡牌/书房/1.jpg");

        var scan = NewService().Scan(story);

        Assert.Equal(3, scan.Groups.Count);
        var organizer = Assert.Single(scan.Groups, g => g.Kind == ScriptGroupKind.Organizer);
        Assert.Equal("组织", organizer.Name);
        Assert.Contains("组织", organizer.GuessReason, StringComparison.Ordinal);
        Assert.Equal("角色本装订/组织", organizer.RelativePath);

        var role = Assert.Single(scan.Groups, g => g.Kind == ScriptGroupKind.Role);
        Assert.Equal("张三", role.Name);

        // 「书房」自己没线索关键词，但父路径有：依据必须写出来，导入的人才知道软件为什么这么猜。
        var material = Assert.Single(scan.Groups, g => g.Kind == ScriptGroupKind.Material);
        Assert.Equal("书房", material.Name);
        Assert.Contains("路径含“线索”", material.GuessReason, StringComparison.Ordinal);

        Assert.False(scan.NeedsOrganizerChoice);
        Assert.Empty(scan.Issues);
    }

    [Fact]
    public void Each_pdf_is_its_own_group()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePdf(story, "主持人手册.pdf");
        WritePdf(story, "角色/A.pdf");
        WritePdf(story, "角色/B.pdf");
        WritePage(story, "搜证卡/1.jpg");

        var scan = NewService().Scan(story);

        Assert.Equal(4, scan.Groups.Count);
        Assert.Equal("主持人手册", Assert.Single(scan.Groups, g => g.Kind == ScriptGroupKind.Organizer).Name);
        Assert.Equal(
            new[] { "A", "B" },
            scan.Groups.Where(g => g.Kind == ScriptGroupKind.Role).Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));
        var material = Assert.Single(scan.Groups, g => g.Kind == ScriptGroupKind.Material);
        Assert.Equal("搜证卡", material.Name);
        Assert.Equal("pdf", Assert.Single(scan.Groups, g => g.Name == "A").FileTypeLabel);
    }

    [Fact]
    public void Duplicate_leaf_names_use_relative_paths_so_they_stay_unique()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "线索卡牌/一楼/1.jpg");
        WritePage(story, "房间卡/一楼/1.jpg");

        var scan = NewService().Scan(story);

        Assert.Equal(2, scan.Groups.Count);
        Assert.Equal(2, scan.Groups.Select(g => g.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("线索卡牌/一楼", scan.Groups.Select(g => g.Name));
        Assert.Contains("房间卡/一楼", scan.Groups.Select(g => g.Name));
    }

    [Fact]
    public void Scan_says_when_no_group_looks_like_the_organizer_manual()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "张三/1.jpg");
        WritePage(story, "李四/1.jpg");

        var scan = NewService().Scan(story);

        Assert.True(scan.NeedsOrganizerChoice);
        Assert.Contains(ScriptScanResult.OrganizerGroupMissing, scan.Issues);
        Assert.Null(scan.OrganizerGroupName);
    }

    [Fact]
    public void Two_organizer_candidates_are_reported()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "组织/1.jpg");
        WritePage(story, "主持人手册/1.jpg");

        var scan = NewService().Scan(story);

        Assert.True(scan.HasMultipleOrganizerCandidates);
        Assert.Contains("MULTIPLE_ORGANIZER_GROUPS", scan.Issues);
    }

    [Fact]
    public void Intermediate_folders_with_only_subfolders_are_not_groups()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "角色本装订/组织/1.jpg");

        var scan = NewService().Scan(story);

        var group = Assert.Single(scan.Groups);
        Assert.Equal("组织", group.Name);
        Assert.DoesNotContain(scan.Groups, g => g.Name == "角色本装订");
    }

    [Fact]
    public void Junk_and_hidden_files_are_skipped()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "组织/1.jpg");
        var junk = Path.Combine(story, "组织", "Thumbs.db");
        File.WriteAllBytes(junk, [0]);
        var desktop = Path.Combine(story, "组织", ".DS_Store");
        File.WriteAllBytes(desktop, [0]);
        var hidden = WritePage(story, "组织/2.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var scan = NewService().Scan(story);

        var group = Assert.Single(scan.Groups);
        Assert.Single(group.Files);
        Assert.EndsWith("1.jpg", group.Files[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Page_order_puts_card_backs_right_after_their_front()
    {
        var story = Directory.CreateDirectory(Path.Combine(_root, "剧本")).FullName;
        WritePage(story, "线索/1.jpg");
        WritePage(story, "线索/10.jpg");
        WritePage(story, "线索/2卡背.jpg");
        WritePage(story, "线索/2.jpg");

        var scan = NewService().Scan(story);

        var group = Assert.Single(scan.Groups);
        Assert.Equal(
            new[] { "1", "2", "2卡背", "10" },
            group.Files.Select(f => Path.GetFileNameWithoutExtension(f)));
    }

    /// <summary>造一页 1×1 的 PNG（扩展名随意），内容对扫描没有意义。</summary>
    private string WritePage(string storyRoot, string relative)
    {
        var path = Path.Combine(storyRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, OnePixelPng);
        return path;
    }

    private string WritePdf(string storyRoot, string relative)
    {
        var path = Path.Combine(storyRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "%PDF-1.4\n% 测试用空文件，不含任何剧本内容\n"u8.ToArray());
        return path;
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}

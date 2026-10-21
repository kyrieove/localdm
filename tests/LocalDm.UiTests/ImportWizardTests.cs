using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.App.Views;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Import;

namespace LocalDm.UiTests;

/// <summary>
/// 导入向导的界面验收：② 页不出现图片与识别文字；④ 页把可能剧透的条目藏起来并挡住下一步；
/// 导入完成后剧本库里多出这一本。
///
/// 识别与模型都是假的：这些用例验的是界面，不是 OCR。
/// 剧本文件夹全部在临时目录里现造，不读 stories/、.appdata/、.local/。
/// </summary>
public sealed class ImportWizardTests
{
    /// <summary>假识别在每个页里放的标记：界面上一旦出现它，就是泄露了识别文字。</summary>
    private const string ExtractionMarker = "识别得到的正文标记";

    [Fact]
    public async Task Choosing_a_folder_enables_next_and_an_invalid_player_count_disables_it()
    {
        await WpfRunner.RunAsync(() =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var raised = 0;
            wizard.NextCommand.CanExecuteChanged += (_, _) => raised++;

            Assert.False(wizard.NextCommand.CanExecute(null));

            // 按钮只在收到 CanExecuteChanged 时才重新问能不能点：只看 CanExecute 的返回值测不出「一直是灰的」。
            wizard.SetFolder(NewStory(root.Path));
            Assert.True(raised > 0, "选完文件夹后必须通知「下一步」重新判断。");
            Assert.True(wizard.NextCommand.CanExecute(null));

            raised = 0;
            wizard.PlayerCount = 7;
            Assert.True(raised > 0, "改玩家人数后必须通知「下一步」重新判断。");
            Assert.False(wizard.NextCommand.CanExecute(null));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Folder_page_text_boxes_are_tall_enough_for_their_text()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var view = WizardView(wizard);
            var window = Show(view);
            try
            {
                wizard.SetFolder(NewStory(root.Path));
                await WaitForRenderAsync();

                // 写死高度时，内边距一扣，正文只剩中间一条，字被上下裁掉。
                foreach (var name in new[] { "FolderBox", "TitleBox", "PlayerCountBox" })
                {
                    var box = Assert.IsType<TextBox>(view.FindName(name));
                    var needed = box.FontSize * box.FontFamily.LineSpacing
                                 + box.Padding.Top + box.Padding.Bottom
                                 + box.BorderThickness.Top + box.BorderThickness.Bottom;
                    Assert.True(box.ActualHeight + 0.5 >= needed,
                        $"{name} 高 {box.ActualHeight:F1}，放下一行字至少要 {needed:F1}。");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Group_page_has_no_images_and_shows_no_recognised_text()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var window = Show(WizardView(wizard));
            try
            {
                wizard.SetFolder(NewStory(root.Path));
                await wizard.ScanAsync();
                Assert.True(wizard.IsGroupsStep);

                // 先跑一次识别，确认识别文字不会漏到 ② 页上。
                await wizard.ExtractAndDraftAsync();
                await wizard.BackAsync();
                Assert.True(wizard.IsGroupsStep);
                await WaitForRenderAsync();

                var texts = CollectTexts(window);
                Assert.Contains(texts, text => text.Contains("组织", StringComparison.Ordinal));
                Assert.DoesNotContain(texts, text => text.Contains(ExtractionMarker, StringComparison.Ordinal));

                // ② 页不能有任何图片控件：缩略图也会剧透。
                Assert.Empty(CollectVisuals<Image>(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Flow_page_hides_flagged_hints_and_blocks_next_until_rewritten()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var window = Show(WizardView(wizard));
            try
            {
                wizard.SetFolder(NewStory(root.Path));
                await wizard.ScanAsync();
                await wizard.ExtractAndDraftAsync();
                Assert.True(wizard.IsFlowStep);

                // 起草结果里有一条照抄了真相页：必须被藏起来，且挡住下一步。
                var flagged = wizard.FlowSteps.First(row => row.IsHidden);
                Assert.Equal("这条提示可能含剧情，已隐藏，请重写", flagged.DisplayHint);
                Assert.False(wizard.CanBuild);
                Assert.False(wizard.NextCommand.CanExecute(null));

                // 界面上的文字里不能出现那条被藏起来的原文。
                await WaitForRenderAsync();
                var texts = CollectTexts(window);
                Assert.DoesNotContain(texts, text => text.Contains(TruthSentence, StringComparison.Ordinal));
                Assert.Contains(texts, text => text.Contains("已隐藏", StringComparison.Ordinal));

                // 重写之后才能继续。
                flagged.Hint = "翻开线索卡，逐张读完。";
                Assert.False(flagged.IsHidden);
                Assert.True(wizard.CanBuild);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Flow_page_rewriting_hidden_hint_notifies_build_command_and_makes_it_executable()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var window = Show(WizardView(wizard));
            try
            {
                wizard.SetFolder(NewStory(root.Path));
                await wizard.ScanAsync();
                await wizard.ExtractAndDraftAsync();
                Assert.True(wizard.IsFlowStep);

                var flagged = wizard.FlowSteps.First(row => row.IsHidden);
                Assert.False(wizard.CanBuild);
                Assert.False(wizard.BuildCommand.CanExecute(null));

                var buildRaised = 0;
                wizard.BuildCommand.CanExecuteChanged += (_, _) => buildRaised++;

                // 把提示改写成不含真相原文的句子
                flagged.Hint = "翻开线索卡，逐张读完。";

                Assert.True(buildRaised > 0, "把提示改写后，BuildCommand 必须收到 CanExecuteChanged 通知。");
                Assert.False(flagged.IsHidden);
                Assert.True(wizard.CanBuild);
                Assert.True(wizard.BuildCommand.CanExecute(null));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Groups_page_changing_organizer_to_role_and_back_notifies_next_command()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var window = Show(WizardView(wizard));
            try
            {
                wizard.SetFolder(NewStory(root.Path));
                await wizard.ScanAsync();
                Assert.True(wizard.IsGroupsStep);

                var organizer = wizard.Groups.Single(g => g.Kind == ScriptGroupKind.Organizer);
                Assert.True(wizard.NextCommand.CanExecute(null));

                var nextRaised = 0;
                wizard.NextCommand.CanExecuteChanged += (_, _) => nextRaised++;

                // 把唯一的组织者手册改成角色本
                organizer.Kind = ScriptGroupKind.Role;

                Assert.True(nextRaised > 0, "改动组织者手册类别后，NextCommand 必须收到 CanExecuteChanged 通知。");
                Assert.False(wizard.NextCommand.CanExecute(null), "缺少组织者手册时 NextCommand 必须不可用。");

                nextRaised = 0;
                // 再改回来
                organizer.Kind = ScriptGroupKind.Organizer;

                Assert.True(nextRaised > 0, "改回组织者手册后，NextCommand 必须收到 CanExecuteChanged 通知。");
                Assert.True(wizard.NextCommand.CanExecute(null), "拥有唯一组织者手册时 NextCommand 必须变为可用。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Library_lists_the_new_package_after_a_finished_import()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var wizard = NewWizard(root.Path);
            var window = Show(WizardView(wizard));
            try
            {
                wizard.SetFolder(NewStory(root.Path));
                wizard.Title = "合成导入剧本";
                wizard.PlayerCount = 4;
                await wizard.ScanAsync();
                await wizard.ExtractAndDraftAsync();

                // 起草出来的环节里若有一条被藏起来，先重写成干净的提示再建包。
                foreach (var row in wizard.FlowSteps)
                {
                    row.Hint = "按流程做下一步。";
                }

                await wizard.BuildAsync();
                Assert.True(wizard.IsDoneStep, wizard.Status);
                Assert.Contains("可以开局", wizard.ReadinessText, StringComparison.Ordinal);

                var library = new LibraryViewModel(Path.Combine(root.Path, "data", "packages"));
                await library.LoadAsync(TestContext.Current.CancellationToken);

                var row2 = Assert.Single(library.Packages, p => p.Title == "合成导入剧本");
                Assert.True(row2.CanStart);
                Assert.True(library.HasPackages);

                // 导入中的那行字：列表里看得见「正在导入：第 N/M 页」。
                library.SetImporting("正在导入：第 1/4 页");
                Assert.True(library.IsImporting);
                library.SetImporting(null);
                Assert.False(library.IsImporting);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ===== ④ 页确认后的流程字段 =====

    [Fact]
    public async Task Rebuilding_the_same_flow_twice_keeps_the_package_id()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var story = NewStory(root.Path);
            var (package, packagePath, _) = await BuildPackageAsync(root.Path, story, FlowWithEveryField());

            // 「重新整理流程」重建两次：不改任何内容。
            var first = await RebuildAsync(root.Path, package, packagePath);
            var second = await RebuildAsync(root.Path, package, packagePath);

            Assert.Equal(package.PackageId, first);
            Assert.Equal(first, second);
        });
    }

    [Fact]
    public async Task Confirming_an_existing_flow_keeps_the_fields_the_page_does_not_edit()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var story = NewStory(root.Path);
            var flow = FlowWithEveryField();
            var (package, packagePath, _) = await BuildPackageAsync(root.Path, story, flow);

            var wizard = NewWizard(root.Path);
            await wizard.OpenExistingFlowAsync(package, ImportWizardViewModel.LoadSavedFlow(packagePath));
            Assert.True(wizard.IsFlowStep, wizard.Status);

            // 不做修改直接确认：界面上没有编辑入口的字段必须原样带回去。
            var confirmed = wizard.BuildFlow().Steps;
            Assert.Equal(flow.Steps.Select(step => step.StepId), confirmed.Select(step => step.StepId));
            Assert.Equal(flow.Steps[0].OffersOpeningSpeech, confirmed[0].OffersOpeningSpeech);
            Assert.Equal(flow.Steps[0].PublicGroups, confirmed[0].PublicGroups);
            Assert.Equal(flow.Steps[1].MaterialIds, confirmed[1].MaterialIds);
            Assert.Equal(flow.Steps[1].RequiresAllMaterials, confirmed[1].RequiresAllMaterials);
            Assert.Equal(flow.Steps[1].AdvanceLabel, confirmed[1].AdvanceLabel);
            Assert.Equal(flow.Steps[2].RevealLabel, confirmed[2].RevealLabel);

            // 真的建一次包：写出去的流程文件里这些字段也在。
            await wizard.BuildAsync();
            Assert.True(wizard.IsDoneStep, wizard.Status);
            var saved = ImportWizardViewModel.LoadSavedFlow(NewestPackagePath(Path.Combine(root.Path, "data", "packages")))!;
            Assert.True(saved.Steps[0].OffersOpeningSpeech);
            Assert.Equal(["组织"], saved.Steps[0].PublicGroups);
            Assert.Equal(["书房"], saved.Steps[1].MaterialIds);
            Assert.True(saved.Steps[1].RequiresAllMaterials);
            Assert.Equal("进入第二轮搜证", saved.Steps[1].AdvanceLabel);
            Assert.Equal("公开结局", saved.Steps[2].RevealLabel);
            Assert.Equal(flow.Steps.Select(step => step.StepId), saved.Steps.Select(step => step.StepId));
        });
    }

    [Fact]
    public async Task Changing_only_a_title_keeps_the_step_id_and_the_other_fields()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var story = NewStory(root.Path);
            var flow = FlowWithEveryField();
            var (package, packagePath, _) = await BuildPackageAsync(root.Path, story, flow);

            var wizard = NewWizard(root.Path);
            await wizard.OpenExistingFlowAsync(package, ImportWizardViewModel.LoadSavedFlow(packagePath));
            wizard.FlowSteps[1].Title = "第二轮阅本";

            var confirmed = wizard.BuildFlow().Steps;
            Assert.Equal(flow.Steps[1].StepId, confirmed[1].StepId);
            Assert.Equal("第二轮阅本", confirmed[1].Title);
            Assert.Equal(flow.Steps[1].MaterialIds, confirmed[1].MaterialIds);
            Assert.Equal(flow.Steps[1].RequiresAllMaterials, confirmed[1].RequiresAllMaterials);
            Assert.Equal(flow.Steps[1].AdvanceLabel, confirmed[1].AdvanceLabel);
            Assert.Equal(flow.Steps[1].Kind, confirmed[1].Kind);
        });
    }

    [Fact]
    public async Task Added_rows_get_unique_step_ids_without_any_hash()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var story = NewStory(root.Path);
            // 已有的 ID 里有一个 step-4：第一行新环节在索引 3 上按序号取名正好撞上它。
            var flow = FlowWithEveryField(stepIds: ["read", "step-3", "step-4"]);
            var (package, packagePath, _) = await BuildPackageAsync(root.Path, story, flow);

            var wizard = NewWizard(root.Path);
            await wizard.OpenExistingFlowAsync(package, ImportWizardViewModel.LoadSavedFlow(packagePath));
            wizard.AddStepCommand.Execute(null);
            wizard.AddStepCommand.Execute(null);

            var ids = wizard.BuildFlow().Steps.Select(step => step.StepId).ToArray();
            Assert.Equal(5, ids.Length);
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(["read", "step-3", "step-4"], ids[..3]);
            Assert.Equal("step-4-2", ids[3]);
            Assert.Equal("step-5", ids[4]);
            Assert.Empty(wizard.BuildFlow().ValidationIssues);
        });
    }

    // ===== 辅助 =====

    /// <summary>合成真相页里的一句话：起草结果照抄它就是剧透。</summary>
    private const string TruthSentence =
        "凶手在子夜换掉了走廊尽头那盏灯然后从侧门离开海砂馆";

    /// <summary>
    /// 一份每个字段都用上的流程：界面上只能改标题/类别/提示/条件/依据页，
    /// 其余字段要在确认后原样保住。
    /// </summary>
    private static ScriptFlow FlowWithEveryField(IReadOnlyList<string>? stepIds = null)
    {
        stepIds ??= ["read", "search", "reveal"];
        return new ScriptFlow
        {
            Layering = new ScriptLayering
            {
                OrganizerGroup = "组织",
                PublicPages = [],
                TruthPages = [2],
                IgnoredPages = [],
            },
            Steps =
            [
                new ScriptFlowStep
                {
                    StepId = stepIds[0],
                    Title = "阅本",
                    Kind = ScriptStepKind.Read,
                    Hint = "读自己的角色本。",
                    Conditions = ["全员读完"],
                    SourceRef = "组织者手册第 1 页",
                    OffersOpeningSpeech = true,
                    PublicGroups = ["组织"],
                },
                new ScriptFlowStep
                {
                    StepId = stepIds[1],
                    Title = "搜证",
                    Kind = ScriptStepKind.Search,
                    Hint = "按编号发线索卡。",
                    Conditions = ["材料发完"],
                    SourceRef = "组织者手册第 1 页",
                    MaterialIds = ["书房"],
                    RequiresAllMaterials = true,
                    AdvanceLabel = "进入第二轮搜证",
                },
                new ScriptFlowStep
                {
                    StepId = stepIds[2],
                    Title = "揭秘",
                    Kind = ScriptStepKind.Reveal,
                    Hint = "公开真相。",
                    Conditions = [],
                    SourceRef = "组织者手册第 2 页",
                    RevealLabel = "公开结局",
                },
            ],
        };
    }

    /// <summary>直接用服务建一次包：拿来当「已有的包 + 已有的流程文件」。</summary>
    private static async Task<(ScriptPackage Package, string PackagePath, ScriptFlow Flow)> BuildPackageAsync(
        string root,
        string story,
        ScriptFlow flow)
    {
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        var service = new ScriptImportService(new ScriptImportOptions
        {
            DataRoot = dataRoot,
            Extractor = new FakeExtractor(),
        });
        var extraction = await service.ExtractAsync(
            story,
            service.Scan(story).Groups,
            progress: null,
            TestContext.Current.CancellationToken);
        var result = await service.BuildAsync(extraction, "合成字段剧本", 4, flow, TestContext.Current.CancellationToken);
        return (result.Build.Package, result.PackagePath, flow);
    }

    /// <summary>走一次「重新整理流程」并把包重建出来，返回新包的标识。</summary>
    private static async Task<string> RebuildAsync(string root, ScriptPackage package, string packagePath)
    {
        var wizard = NewWizard(root);
        await wizard.OpenExistingFlowAsync(package, ImportWizardViewModel.LoadSavedFlow(packagePath));
        Assert.True(wizard.IsFlowStep, wizard.Status);
        await wizard.BuildAsync();
        Assert.True(wizard.IsDoneStep, wizard.Status);
        var rebuilt = System.Text.Json.JsonSerializer.Deserialize<ScriptPackage>(
            File.ReadAllText(NewestPackagePath(Path.Combine(root, "data", "packages"))))!;
        return rebuilt.PackageId;
    }

    private static string NewestPackagePath(string packagesDir) =>
        Directory.GetFiles(packagesDir, "*.json")
            .Where(path => !path.EndsWith(".flow.json", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();

    private static ImportWizardViewModel NewWizard(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        var packagesDir = Path.Combine(dataRoot, "packages");
        Directory.CreateDirectory(packagesDir);

        return new ImportWizardViewModel(
            dataRoot,
            packagesDir,
            () => new ScriptImportService(new ScriptImportOptions
            {
                DataRoot = dataRoot,
                Extractor = new FakeExtractor(),
            }),
            new FlowDrafter(new FakeGateway()),
            () => "ui-test-model");
    }

    /// <summary>造一个剧本文件夹：组织者手册 2 页、一个角色本、一组线索卡。</summary>
    private static string NewStory(string root)
    {
        var story = Path.Combine(root, "剧本-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var (relative, index) in new[]
                 {
                     ("组织/1.jpg", 1), ("组织/2.jpg", 2), ("角色本/张三/1.jpg", 3), ("线索卡牌/书房/1.jpg", 4),
                 })
        {
            var path = Path.Combine(story, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [.. OnePixelPng, .. BitConverter.GetBytes(index)]);
        }

        return story;
    }

    private static ImportWizardView WizardView(ImportWizardViewModel wizard) => new() { DataContext = wizard };

    private static Window Show(FrameworkElement content)
    {
        var window = new Window
        {
            Content = content,
            ShowActivated = false,
            Width = 1200,
            Height = 900,
        };
        WpfRunner.ShowOffscreen(window);
        return window;
    }

    private static async Task WaitForRenderAsync()
    {
        await Task.Delay(120);
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(60);
    }

    private static List<string> CollectTexts(DependencyObject root)
    {
        var texts = new List<string>();
        if (root is TextBlock block)
        {
            texts.Add(block.Text ?? string.Empty);
        }
        else if (root is TextBox box)
        {
            texts.Add(box.Text ?? string.Empty);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            texts.AddRange(CollectTexts(VisualTreeHelper.GetChild(root, i)));
        }

        return texts;
    }

    private static List<T> CollectVisuals<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        if (root is T match)
        {
            found.Add(match);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            found.AddRange(CollectVisuals<T>(VisualTreeHelper.GetChild(root, i)));
        }

        return found;
    }

    /// <summary>假识别：每页回一段带标记的文字；组织者手册第 2 页里放一句「真相」。</summary>
    private sealed class FakeExtractor : IPageExtractor
    {
        public Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ToolIdentity { Tool = "fake-mineru", Version = "1.0", AppOwnedHome = "fake-home" });

        public Task<ImportFileResult> ExtractAsync(
            string fileId,
            string absolutePath,
            DataConsent consent,
            CancellationToken cancellationToken)
        {
            var source = SourceIndex.BuildSourceFile(
                fileId,
                absolutePath,
                DocumentKind.Image,
                pdfPageCount: null,
                SourceOrder.ManifestOrder,
                "fake-mineru 1.0");
            var isTruthPage = fileId.Contains("组织-2", StringComparison.Ordinal);
            var text = isTruthPage
                ? $"{ExtractionMarker}。{TruthSentence}。"
                : $"{ExtractionMarker}：{fileId}。";
            var span = new SourceSpan
            {
                SpanId = fileId + "-b0",
                FileId = fileId,
                PageNumber = 1,
                BlockNumber = 0,
                ToolLocator = $"doc:{fileId}/tier:flash/page:1/block:0",
                TextHash = SourceIndex.ComputeTextHash(text),
                OriginalText = text,
            };
            return Task.FromResult(new ImportFileResult(
                fileId,
                ImportFileState.Complete,
                ToolComplete: true,
                source,
                [span],
                [],
                ErrorCode: null,
                PublicReason: null));
        }
    }

    /// <summary>假模型：给一份结构合法、但有一条提示照抄了真相的流程。</summary>
    private sealed class FakeGateway : IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["ui-test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(
                $$"""
                {
                  "layering": { "truthPages": [2], "ignoredPages": [1] },
                  "steps": [
                    { "stepId": "read", "title": "阅本", "kind": "Read",
                      "hint": "拿到角色本，读到写着先不要翻页的地方为止。",
                      "conditions": ["全员读完"], "sourceRef": "组织者手册第 1 页", "offersOpeningSpeech": true },
                    { "stepId": "search", "title": "搜证", "kind": "Search",
                      "hint": "{{TruthSentence}}", "conditions": [], "sourceRef": "组织者手册第 2 页" },
                    { "stepId": "reveal", "title": "揭秘", "kind": "Reveal",
                      "hint": "公开真相。", "conditions": [], "sourceRef": "组织者手册第 2 页" }
                  ]
                }
                """);
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-wizard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

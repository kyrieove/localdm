STATUS: done

Files changed:
- `src/LocalDm.Tablet/ITabletContent.cs` — `StoryState` 接口默认实现（Ready）
- `src/LocalDm.Tablet/SwitchableTabletContent.cs` — 新建，`TabletStoryState` + 可切换内容
- `src/LocalDm.Tablet/TabletSpikeState.cs` — 订阅 `Changed` 清空认领、`StoryState`、`ResetAll`
- `src/LocalDm.Tablet/Pages/Home.razor` — 等待页 / 准备中页分支，`@using LocalDm.Tablet`
- `src/LocalDm.Tablet/wwwroot/reader.css` — 末尾追加等待页样式
- `tests/LocalDm.Tests/TabletSwitchableTests.cs` — 新建，两个 `[Fact]`

Syntax check: `dotnet build tests/LocalDm.Tests/LocalDm.Tests.csproj` 成功，0 警告 0 错误（含 Razor 编译）。未运行 Check，未提交。CSS 无独立语法检查器可用。

无法完成：无（`docs/orch/.../card.md` 在我开始前就是已修改状态，我未改动它）。
VERDICT: FAIL

- [P1] [src/LocalDm.Tablet/Pages/RoleReader.razor:213](/D:/2/dev/dm/src/LocalDm.Tablet/Pages/RoleReader.razor:213) — **未翻开章节的标题仍通过正文以外的入口显示。** 新增过滤只作用于 `Flatten`；玩家在尚未翻开任何页时切到文字重排，仍能看到完整章节目录；正文为空时，`SyncStateAsync` 还会把第一章标题显示到页脚。这使任务卡要求跳过的未翻开章节标题仍然可见。确认代码：第 101 行直接输出 `@Book.Chapters[i].Title`，第 273 行直接赋值 `_chapterTitle = Book.Chapters[_chapter].Title;`。经 grep，此类未过滤标题输出共两处：第 101 行目录，以及第 273 行赋值后在第 113 行输出的页脚。修复：目录和页脚使用与 `Flatten` 相同的章节可见性判断，无可见章节时清空标题，并保持导航索引与正文一致。

- [P2] [tests/LocalDm.Tests/TabletPageFlipTests.cs:8](/D:/2/dev/dm/tests/LocalDm.Tests/TabletPageFlipTests.cs:8) — **新增膨胀：重复实现测试内容夹具。** `private sealed class MinimalTabletContent : ITabletContent` 连同角色列表和两个返回失败的方法，重复了现有测试夹具。经 grep，全部重复位置为本文件第 8、22、30 行，对应 `TabletSpeechTests.cs` 第 83、95、103 行。修复：复用已有可访问的测试内容实现，或将该夹具共享，避免维护两份相同实现。此项本身不导致 FAIL。

既有问题：无另行确认；按要求未运行测试或脚本，未修改文件。
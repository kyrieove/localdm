# 0013 PC 界面换成平板「灯下」的视觉语言

日期 2026-10-26。合并提交 `f75f4a9`，前后对比见 `docs/preview/pc-ui-dengxia-20261025/index.html`。

## 背景
平板 Flutter 原型「灯下」做出来后，用户觉得 PC 端（纸色 + 印章红 + 宋体标题）落后了，要求 PC 照平板重做；具体设计交给 Claude 定。

## 决定
- 配色：默认深色，近黑暖灰底加琥珀单强调色，色值与 `apps/tablet/lib/theme.dart` 同一套；浅色可选，设置项 `LightTheme`，重启生效。全部无衬线；只有复盘时间线保留等宽字（`MonospaceGrid` 靠它对齐）。
- 颜色在 `Themes/Palette.Dark.xaml` / `Palette.Light.xaml`，由 `ThemePalette` 按 `ThemePalette.UseLight` 挑一份，`Shared.xaml` 合并它。开关放在这一层，是因为每个 View 自己合并 `Shared.xaml`，界面测试不走 `App.xaml`；`App` 构造函数在加载 XAML 前读设置。键名沿用旧的 Paper/Ink/Seal，对应关系写在 `Palette.Dark.xaml` 头部。
- 外壳：84 宽图标导航栏（顶上环节表盘）+ 288 宽环节面板（内容宽 < 1000 自动收起，点表盘开合）。
- 只有「重新开始本局」用底部确认面板；揭秘保持点击即确认（`RevealWarning` 写着「无需再次确认」，是既定规则）。
- 对比度由 `ThemeContrastTests` 逐对检查 ≥4.5:1，两套配色都测。

## 否掉的方案
- WPF 自带 Fluent 主题（`ThemeMode`）：.NET 10 下不报实验警告，但各 View 合并的 `Shared.xaml` 隐式样式离控件更近，把 Fluent 整个盖掉；要用就得给每个隐式样式补 `BasedOn`，且它的配色体系和「灯下」是两套。改为自绘 ComboBox、CheckBox、RadioButton、Expander、Slider、ToolTip 的深色模板。
- Flutter 与 WPF 共用一份色值文件：平板设计还会变，先照抄，变了再同步。

## 后果
- 平板 `theme.dart` 改色时，PC 的两个 Palette 文件要手动跟着改。
- 原来左栏常驻的「音色 · 自动朗读」一行删了；「模型已就绪」改成没选模型时在「设置」下常亮「未选模型」。

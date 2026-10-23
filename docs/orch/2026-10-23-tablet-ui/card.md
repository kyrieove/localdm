# tablet-ui — 剧本库常驻「平板加入」卡片、主持台侧栏二维码入口、偏好设置里平板只留高级项

## Goal
- 剧本库页顶部（「导入新剧本」那一行下面）常驻一张「平板加入」卡片：二维码、网址（可选网卡）、复制按钮、一句话状态标题、状态说明、已加入角色、出错原因、「连不上怎么办」折叠说明。
- 主持台/任意页面的左侧栏（偏好设置按钮上方）有一个「平板入口」按钮：小二维码 + 网址，点开弹出大二维码窗口。
- 偏好设置里的平板区改成「平板服务（高级）」：只留开关、状态、网址选择、电脑打开、复制网址，不再放二维码。

## Risk
medium

## Parameters
- 数据来源：`LibraryViewModel.Tablet`（`TabletJoinViewModel`，属性 `QrCode`、`SelectedAddress`、`Addresses`、`Headline`、`Status`、`ErrorText`、`HasError`、`JoinedSummary`、`CopyCommand`、`OpenCommand`、`ToggleCommand`、`ToggleLabel`、`IsRunning` 已存在）。
- 已有资源与转换器：`BoolToVisibility`、`NotEmptyToVisibility`、样式 `ChatMeta`、`QuietButton`、`RailButton`、`RailLabel`，画刷 `PaperDim`、`PaperDeep`、`Rule`、`Seal`、`SealDeep`、`SealWash`、`Muted`、`Paper`，字体 `DisplayFace`。
- 二维码图片显示用 `RenderOptions.BitmapScalingMode="NearestNeighbor"`，卡片内 132×132，侧栏 32×32，弹窗 280×280。
- 「连不上怎么办」四条：1. 平板和电脑连同一个 Wi-Fi。2. Windows 第一次弹出网络提示时，选「专用网络」并允许。3. 网络类型若是「公用」，在 Windows 网络设置里改成「专用」。4. 仍不行：先在电脑浏览器里打开上面的网址，能打开就是平板那边的网络问题。

## Files
may change: `src/LocalDm.App/Views/LibraryView.xaml`, `src/LocalDm.App/MainWindow.xaml`, `src/LocalDm.App/MainWindow.xaml.cs`, `src/LocalDm.App/Views/SetupView.xaml`, `tests/LocalDm.UiTests/TabletJoinTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 4)
1. `src/LocalDm.App/Views/LibraryView.xaml`：在注释行 `<!-- 正在导入：点一下回到向导 -->` 的**上一行**插入下面这段（整张卡片）：
```xml
            <!-- 平板加入：软件一开就有，不管有没有打开剧本 -->
            <Border x:Name="TabletJoinCard" DataContext="{Binding Tablet}" Margin="0,18,0,0" Padding="16" CornerRadius="8"
                    Background="{StaticResource PaperDim}" BorderBrush="{StaticResource Rule}" BorderThickness="1">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Border Grid.Column="0" Width="140" Height="140" Background="White" BorderBrush="{StaticResource Rule}"
                            BorderThickness="1" CornerRadius="6" VerticalAlignment="Top">
                        <Image x:Name="TabletJoinQr" Source="{Binding QrCode}" Width="128" Height="128"
                               RenderOptions.BitmapScalingMode="NearestNeighbor" />
                    </Border>
                    <StackPanel Grid.Column="1" Margin="18,0,0,0">
                        <TextBlock Text="平板加入" Style="{StaticResource RailLabel}" />
                        <TextBlock x:Name="TabletJoinHeadline" Text="{Binding Headline}" FontSize="17" FontWeight="SemiBold" Margin="0,4,0,0" />
                        <TextBlock Text="{Binding Status}" Style="{StaticResource ChatMeta}" TextWrapping="Wrap" Margin="0,4,0,0" />
                        <Grid Margin="0,10,0,0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <ComboBox x:Name="TabletJoinAddress" ItemsSource="{Binding Addresses}"
                                      SelectedItem="{Binding SelectedAddress}" Height="32" VerticalContentAlignment="Center" />
                            <Button Grid.Column="1" Content="复制网址" Command="{Binding CopyCommand}"
                                    Style="{StaticResource QuietButton}" Padding="14,6" Margin="8,0,0,0" />
                        </Grid>
                        <TextBlock Text="{Binding JoinedSummary}" Style="{StaticResource ChatMeta}" Margin="0,8,0,0"
                                   Visibility="{Binding JoinedSummary, Converter={StaticResource NotEmptyToVisibility}}" />
                        <Border Margin="0,8,0,0" Padding="10,8" CornerRadius="6" Background="{StaticResource SealWash}"
                                BorderBrush="{StaticResource Seal}" BorderThickness="1"
                                Visibility="{Binding HasError, Converter={StaticResource BoolToVisibility}}">
                            <TextBlock x:Name="TabletJoinError" Text="{Binding ErrorText}" TextWrapping="Wrap" FontSize="12"
                                       Foreground="{StaticResource SealDeep}" />
                        </Border>
                        <Expander Header="连不上怎么办" Margin="0,8,0,0" FontSize="12" Foreground="{StaticResource Seal}">
                            <TextBlock TextWrapping="Wrap" FontSize="12" LineHeight="20" Margin="0,6,0,0"
                                       Foreground="{StaticResource InkSoft}"
                                       Text="1. 平板和电脑连同一个 Wi-Fi。&#10;2. Windows 第一次弹出网络提示时，选「专用网络」并允许。&#10;3. 网络类型若是「公用」，在 Windows 网络设置里改成「专用」。&#10;4. 仍不行：先在电脑浏览器里打开上面的网址，能打开就是平板那边的网络问题。" />
                        </Expander>
                    </StackPanel>
                </Grid>
            </Border>

```
2. `src/LocalDm.App/MainWindow.xaml`：在 `<Button x:Name="NavVoice" ...` 这个按钮的**上一行**插入：
```xml
                    <Button x:Name="NavTablet" Style="{StaticResource RailButton}" FontSize="12" Click="OnTabletEntryClick"
                            ToolTip="点开看大二维码，平板扫码加入">
                        <StackPanel Orientation="Horizontal">
                            <Border Width="36" Height="36" Background="White" CornerRadius="4" VerticalAlignment="Center">
                                <Image Source="{Binding DataContext.Library.Tablet.QrCode, RelativeSource={RelativeSource AncestorType=Window}}"
                                       Width="32" Height="32" RenderOptions.BitmapScalingMode="NearestNeighbor" />
                            </Border>
                            <StackPanel Margin="10,0,0,0" VerticalAlignment="Center">
                                <TextBlock Text="平板入口" FontWeight="SemiBold" />
                                <TextBlock Text="{Binding DataContext.Library.Tablet.SelectedAddress, RelativeSource={RelativeSource AncestorType=Window}}"
                                           FontSize="10" Foreground="{StaticResource Muted}" TextTrimming="CharacterEllipsis" MaxWidth="130" />
                            </StackPanel>
                        </StackPanel>
                    </Button>
```
   `src/LocalDm.App/MainWindow.xaml.cs`：在 `MainWindow` 类里加方法（`using System.Windows.Controls; using System.Windows.Media;` 缺哪个补哪个）：
```csharp
    private void OnTabletEntryClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Library.Tablet: { } tablet })
        {
            return;
        }

        var panel = new StackPanel { Margin = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "平板扫码加入",
            FontSize = 20,
            FontFamily = (FontFamily)FindResource("DisplayFace"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var qr = new Image { Source = tablet.QrCode, Width = 280, Height = 280, Margin = new Thickness(0, 14, 0, 10) };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(qr);
        panel.Children.Add(new TextBlock
        {
            Text = tablet.SelectedAddress ?? tablet.Status,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        new Window
        {
            Title = "平板入口",
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Paper"),
        }.ShowDialog();
    }
```
3. `src/LocalDm.App/Views/SetupView.xaml`：在 `<Border x:Name="TabletConnectionSection" ...>` 里 (a) 把文字 `Text="平板连接"` 改成 `Text="平板服务（高级）"`；(b) 把 `<TextBlock Text="{Binding StoryTitle}" ... />`（标题下面那行副标题）改成 `<TextBlock Text="平板入口与二维码在剧本库页顶部；这里只用来关闭或重新开启服务。" Style="{StaticResource ChatMeta}" Margin="0,6,0,0" TextWrapping="Wrap" />`；(c) 删掉 `<Image x:Name="TabletQrCode" .../>` 这一个元素（两行），并把包着它的 `Grid` 的两列定义改成只有一列：删掉 `<ColumnDefinition Width="Auto" />` 那一列，使 `StackPanel Grid.Column="1" Margin="20,0,0,0"` 变成 `StackPanel Grid.Column="0" Margin="0"`。其余不动。
4. `tests/LocalDm.UiTests/TabletJoinTests.cs`：新增一个测试 `Library_page_shows_join_card_before_any_script_is_opened`（`[Fact]`，async，用 `WpfRunner.RunAsync`）：用与 `Settings_connection_button_shows_current_status_without_a_popup` 相同的方式建 `composition`/`shell`，`await shell.Library.Tablet!.StartAsync()`；创建 `new LibraryView { DataContext = shell.Library }`，放进 `new Window { Content = view, Width = 1200, Height = 800 }`，`WpfRunner.ShowOffscreen(window)`；断言 `view.FindName("TabletJoinCard")` 非 null、`TabletJoinHeadline` 是 `TextBlock` 且 `Text == "平板可以加入"`、`TabletJoinQr` 是 `Image` 且 `Source` 非 null；`finally` 里 `window.Close()`，并 `await shell.Library.Tablet!.DisposeAsync()`；临时目录清理照抄同文件里的写法。

## Check
`dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletJoinTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

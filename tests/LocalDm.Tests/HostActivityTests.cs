using LocalDm.App.ViewModels;

namespace LocalDm.Tests;

/// <summary>
/// 「进行中」状态条。
///
/// 主持人点完按钮到听见声音之间可能有好几秒，这段时间屏幕上必须有话说。
/// 这些断言就是那条体验的回归防线。
/// </summary>
public sealed class HostActivityTests
{
    [Fact]
    public void Begin_marks_the_activity_active_and_shows_the_first_step()
    {
        var activity = new HostActivity();

        activity.Begin(ActivityKind.Thinking, "正在请本机模型回答…", ["请模型回答", "朗读"], "当前环节：搜证");

        Assert.True(activity.IsActive);
        Assert.True(activity.IsVisible);
        Assert.Equal(ActivityKind.Thinking, activity.Kind);
        Assert.Equal("正在请本机模型回答…", activity.Title);
        Assert.Equal("当前环节：搜证", activity.Detail);
        Assert.Equal(2, activity.Steps.Count);
        Assert.True(activity.Steps[0].IsCurrent);
        Assert.False(activity.Steps[1].IsCurrent);
    }

    [Fact]
    public void SetStep_marks_the_previous_step_as_past()
    {
        var activity = new HostActivity();
        activity.Begin(ActivityKind.Thinking, "正在写主持词…", ["请模型写词", "朗读"]);

        activity.SetStep(1, "正在朗读");

        Assert.True(activity.Steps[0].IsPast);
        Assert.True(activity.Steps[1].IsCurrent);
        Assert.Equal("正在朗读", activity.Title);
    }

    [Fact]
    public void Complete_stops_the_animation_but_keeps_the_result_visible()
    {
        var activity = new HostActivity();
        activity.Begin(ActivityKind.Speaking, "正在朗读…", ["朗读"]);

        activity.Complete("已朗读");

        Assert.False(activity.IsActive);
        Assert.True(activity.IsVisible);
        Assert.Equal("已朗读", activity.Title);
    }

    [Fact]
    public void Fail_marks_the_error_kind()
    {
        var activity = new HostActivity();
        activity.Begin(ActivityKind.Thinking, "正在写主持词…", ["写词", "朗读"]);

        activity.Fail("模型没有给出可用内容", "请检查本机模型服务。");

        Assert.Equal(ActivityKind.Error, activity.Kind);
        Assert.True(activity.IsError);
        Assert.False(activity.IsActive);
        Assert.Contains("请检查本机模型服务", activity.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_state_still_shows_content_so_the_band_never_jumps()
    {
        var activity = new HostActivity();

        activity.SetIdleState("就绪", "当前环节：阅本");

        Assert.True(activity.IsVisible);
        Assert.False(activity.IsActive);
        Assert.Equal("就绪", activity.Title);
        Assert.Equal("当前环节：阅本", activity.Detail);
    }

    [Fact]
    public void Clear_goes_back_to_the_idle_state()
    {
        var activity = new HostActivity();
        activity.SetIdleState("就绪", "当前环节：阅本");
        activity.Begin(ActivityKind.Thinking, "正在写主持词…", ["写词"]);

        activity.Clear();

        // 状态条是常驻的：清空后回到空闲态，而不是什么都不显示。
        Assert.True(activity.IsVisible);
        Assert.False(activity.IsActive);
        Assert.Equal("就绪", activity.Title);
    }
}

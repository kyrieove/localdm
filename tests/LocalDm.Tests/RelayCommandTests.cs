using LocalDm.App.ViewModels;

namespace LocalDm.Tests;

public sealed class RelayCommandTests
{
    [Fact]
    public async Task CanExecute_returns_false_while_executing_and_recovers_on_completion()
    {
        var tcs = new TaskCompletionSource();
        var canExecuteChangedCount = 0;
        var command = new RelayCommand(async () => await tcs.Task);
        command.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        Assert.True(command.CanExecute(null));

        command.Execute(null);

        // 执行期间：CanExecute 为 false，且已触发一次 CanExecuteChanged
        Assert.False(command.CanExecute(null));
        Assert.True(canExecuteChangedCount >= 1);

        var countBeforeCompletion = canExecuteChangedCount;
        tcs.SetResult();
        await Task.Yield();

        // 结束后：CanExecute 恢复为 true，且再次触发 CanExecuteChanged
        Assert.True(command.CanExecute(null));
        Assert.True(canExecuteChangedCount > countBeforeCompletion);
    }

    [Fact]
    public async Task CanExecute_recovers_when_execution_throws_exception()
    {
        var tcs = new TaskCompletionSource();
        Exception? caughtException = null;
        var canExecuteChangedCount = 0;

        var command = new RelayCommand(async () =>
        {
            await tcs.Task;
            throw new InvalidOperationException("boom");
        });

        command.ExecutionFailed += (_, ex) => caughtException = ex;
        command.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        Assert.True(command.CanExecute(null));

        command.Execute(null);

        Assert.False(command.CanExecute(null));

        var countBeforeException = canExecuteChangedCount;
        tcs.SetResult();
        await Task.Yield();

        // 抛异常后：通过 ExecutionFailed 抛出，CanExecute 恢复为 true
        Assert.NotNull(caughtException);
        Assert.Equal("boom", caughtException.Message);
        Assert.True(command.CanExecute(null));
        Assert.True(canExecuteChangedCount > countBeforeException);
    }

    [Fact]
    public void Allowed_reentry_command_can_execute_while_executing()
    {
        var tcs = new TaskCompletionSource();
        var executionCount = 0;

        var command = new RelayCommand(
            async () =>
            {
                executionCount++;
                await tcs.Task;
            },
            canExecute: null,
            allowReentry: true);

        Assert.True(command.CanExecute(null));

        command.Execute(null);
        Assert.Equal(1, executionCount);

        // 放行的命令执行中仍可执行
        Assert.True(command.CanExecute(null));

        command.Execute(null);
        Assert.Equal(2, executionCount);

        tcs.SetResult();
    }

    [Fact]
    public void Non_reentrant_command_ignores_second_execute_while_executing()
    {
        var tcs = new TaskCompletionSource();
        var executionCount = 0;

        var command = new RelayCommand(async () =>
        {
            executionCount++;
            await tcs.Task;
        });

        command.Execute(null);
        Assert.Equal(1, executionCount);
        Assert.False(command.CanExecute(null));

        // 连点执行：第二次被拒绝
        command.Execute(null);
        Assert.Equal(1, executionCount);

        tcs.SetResult();
    }

    [Fact]
    public void Respects_custom_canExecute_delegate_when_not_executing()
    {
        var allow = false;
        var command = new RelayCommand(() => Task.CompletedTask, () => allow);

        Assert.False(command.CanExecute(null));

        allow = true;
        Assert.True(command.CanExecute(null));
    }
}

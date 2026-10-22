# 任务：RelayCommand 执行中不禁用自己

现象：`src/LocalDm.App/ViewModels/ShellViewModel.cs` 里的 `RelayCommand.Execute` 是 `async void`，执行期间 `CanExecute` 仍返回 true，连点按钮会重复执行同一个异步操作。

要求：
- 执行期间 `CanExecute` 返回 false，开始和结束时都调用 `RaiseCanExecuteChanged()`。
- 执行结束后恢复，包括 `_execute` 抛异常的情况（异常仍走现有的 `ExecutionFailed`）。
- 不改变现有 `canExecute` 委托的判断，也不改变 `CommandManager.RequerySuggested` 的挂接方式。
- 检查是否有命令依赖"执行中还能再次触发"（例如停止、取消类按钮在生成过程中要能点）。如果有，这类命令不能被这次改动禁用：给 RelayCommand 加一个构造参数（例如 `allowReentry`）显式放行，并在这些命令上传 true。找不准时按规矩写 OVERNIGHT-QUESTION.md 停下。
- 补测试：执行中 CanExecute 为 false；结束后恢复；抛异常后也恢复；放行的命令执行中仍可执行。

不要改 `HostViewModel.cs` 里与本任务无关的代码。

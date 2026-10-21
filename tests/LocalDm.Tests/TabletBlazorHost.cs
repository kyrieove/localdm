using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace LocalDm.Tests;

/// <summary>
/// 在测试进程里以子进程方式启动 LocalDm.Tablet 探针服务，并等待端口就绪。
/// 停止时结束进程树，避免残留监听。
/// </summary>
internal sealed class TabletBlazorHost : IDisposable
{
    private readonly Process _process;

    private TabletBlazorHost(Process process) => _process = process;

    public static TabletBlazorHost Start(string projectPath, int port)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // 直接跑 publish 产物：Debug 输出里没有 _framework/blazor.web.js，
        // dotnet run 又会在测试进程里挂住输出管道。
        var published = Path.Combine(Path.GetTempPath(), "localdm-tablet-pub");
        if (!Directory.Exists(Path.Combine(published, "LocalDm.Tablet.dll")))
        {
            var publish = Process.Start(new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            publish.StartInfo.ArgumentList.Add("publish");
            publish.StartInfo.ArgumentList.Add(projectPath);
            publish.StartInfo.ArgumentList.Add("-c");
            publish.StartInfo.ArgumentList.Add("Release");
            publish.StartInfo.ArgumentList.Add("-o");
            publish.StartInfo.ArgumentList.Add(published);
            publish.Start();
            _ = publish.StandardOutput.ReadToEndAsync();
            var error = publish.StandardError.ReadToEndAsync();
            if (!publish.WaitForExit(180_000))
            {
                publish.Kill(entireProcessTree: true);
                throw new TimeoutException("publish 探针工程超时。");
            }

            if (publish.ExitCode != 0)
            {
                throw new InvalidOperationException("publish 探针工程失败：" + error);
            }
        }

        var assembly = Path.Combine(published, "LocalDm.Tablet.dll");
        startInfo.WorkingDirectory = published;
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");

        var process = new Process { StartInfo = startInfo };
        process.Start();
        // 立即开始异步排空输出：管道满了会让子进程卡住，测试就永远等不到端口。
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        // 等端口能连上：最多 120 秒（首次 dotnet run 可能触发一次构建）。
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException("探针服务提前退出：" + error);
            }

            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                return new TabletBlazorHost(process);
            }
            catch (SocketException)
            {
                Thread.Sleep(300);
            }
        }

        throw new TimeoutException("探针服务在 120 秒内没有监听端口。");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}

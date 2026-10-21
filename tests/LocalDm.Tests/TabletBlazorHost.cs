using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace LocalDm.Tests;

/// <summary>
/// 探针应用的宿主：按测试隔离发布目录，只构建一次、只启动一次。
///
/// 早期版本用 <c>Directory.Exists</c> 检查一个 DLL 文件（永假），
/// 又固定共享临时目录，导致并发测试互相覆盖、重复 publish。
/// 现在每次调用都生成新的 GUID 目录，进程完整回收。
/// </summary>
internal sealed class TabletBlazorHost : IDisposable
{
    private readonly Process _process;
    private readonly string _directory;
    private readonly int _port;

    private TabletBlazorHost(Process process, string directory, int port)
    {
        _process = process;
        _directory = directory;
        _port = port;
    }

    /// <summary>应用监听的端口。</summary>
    public int Port => _port;

    /// <summary>发布并启动探针应用，返回就绪的宿主。</summary>
    public static TabletBlazorHost Start(string projectPath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "localdm-tablet-host-" + Guid.NewGuid().ToString("N"));
        Publish(projectPath, directory);

        var port = FreePort();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(directory, "LocalDm.Tablet.dll"));
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        var process = new Process { StartInfo = startInfo };
        process.Start();
        // 立即异步排空输出管道：管道满会让子进程挂住，测试永远等不到端口就绪。
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("探针应用提前退出（启动失败）。");
            }

            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                return new TabletBlazorHost(process, directory, port);
            }
            catch (SocketException)
            {
                Thread.Sleep(250);
            }
        }

        process.Kill(entireProcessTree: true);
        process.Dispose();
        throw new TimeoutException("探针应用在 180 秒内没有监听端口。");
    }

    private static void Publish(string projectPath, string directory)
    {
        var publish = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        publish.ArgumentList.Add("publish");
        publish.ArgumentList.Add(projectPath);
        publish.ArgumentList.Add("-c");
        publish.ArgumentList.Add("Release");
        publish.ArgumentList.Add("-o");
        publish.ArgumentList.Add(directory);

        var process = new Process { StartInfo = publish };
        process.Start();
        _ = process.StandardOutput.ReadToEndAsync();
        // 必须等 stderr 真正读完：直接拿 Task 进异常消息只会打出一串状态机类型名。
        var error = process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("publish 探针应用超时。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("publish 探针应用失败：" + error);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // 发布目录删不掉不影响断言结论。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

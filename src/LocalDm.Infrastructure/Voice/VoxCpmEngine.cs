using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// 本机 VoxCPM 神经音色的引擎。
///
/// 与 Edge 引擎的根本差别：VoxCPM 是常驻模型，不是无状态服务。
/// 加载 2B 权重要几十秒，每句重载一次等于每句话都卡半分钟，
/// 所以这里维持一个长驻 python 进程：加载一次，之后靠 stdin/stdout 收发 JSON 行。
///
/// 三条硬约束（与 Edge 引擎同源）：
/// 1) 不写死任何开发机绝对路径 —— python 与模型路径都来自配置；
/// 2) 子进程的两个输出流必须被读走，管道写满会让子进程永久阻塞；
/// 3) 播音必须有硬超时，否则模型卡住会把整条播音队列堵死。
/// </summary>
public sealed class VoxCpmEngine : ITtsEngine, IDisposable
{
    /// <summary>探测结果的缓存时长：装好依赖或改完配置后不必重启应用即可生效。</summary>
    private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>worker 首次启动（含加载权重）的上限。加载慢是常驻模型的固有成本。</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<VoxCpmOptions> _optionsProvider;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _speakLock = new(1, 1);

    private Process? _worker;
    private bool? _cachedIsAvailable;
    private DateTime _probeExpiresAt = DateTime.MinValue;
    private bool _disposed;

    public VoxCpmEngine(Func<VoxCpmOptions> optionsProvider)
    {
        _optionsProvider = optionsProvider;
    }

    public TtsEngineKind Kind => TtsEngineKind.VoxCpm;

    public bool IsAvailable()
    {
        var options = _optionsProvider();
        if (!options.Enabled)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_cachedIsAvailable.HasValue && DateTime.UtcNow < _probeExpiresAt)
            {
                return _cachedIsAvailable.Value;
            }
        }

        // 探测要起 python 子进程并等它退出：绝不能持着全局锁做这件事，
        // Stop() 用的就是这把锁，探测期间按「停止播音」会拿不到锁。
        var available = Probe(options);

        lock (_stateLock)
        {
            _cachedIsAvailable = available;
            _probeExpiresAt = DateTime.UtcNow + ProbeCacheTtl;
            return available;
        }
    }

    /// <summary>探测结果作废：下一次问就会重新探测。</summary>
    public void InvalidateProbeCache()
    {
        lock (_stateLock)
        {
            _cachedIsAvailable = null;
            _probeExpiresAt = DateTime.MinValue;
        }
    }

    public IReadOnlyList<TtsVoice> GetVoices()
    {
        if (!IsAvailable())
        {
            return [];
        }

        return _optionsProvider()
            .GetVoiceSpecs()
            .Select(VoxCpmVoices.Build)
            .ToArray();
    }

    public async Task<bool> TrySpeakAsync(string text, TtsVoice voice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var spec = TtsVoiceId.ParseVoxCpm(voice.Name);
        if (spec is null)
        {
            return false;
        }

        var options = _optionsProvider();
        if (!options.Enabled)
        {
            return false;
        }

        // 播音串行化：常驻 worker 只有一条 stdin，写入必须排队。
        await _speakLock.WaitAsync(cancellationToken);
        try
        {
            var worker = await EnsureWorkerAsync(options, cancellationToken);
            if (worker is null)
            {
                return false;
            }

            var request = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["design"] = spec.Mode == VoxCpmVoiceSpecMode.Design ? spec.Description : null,
                ["reference_wav"] = spec.Mode == VoxCpmVoiceSpecMode.Clone ? spec.ReferenceAudioPath : null,
            };

            var payload = JsonSerializer.Serialize(request);

            using var timeoutCts = new CancellationTokenSource(SpeakTimeoutFor(text.Length));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await worker.StandardInput.WriteLineAsync(payload.AsMemory(), linked.Token);
                await worker.StandardInput.FlushAsync(linked.Token);

                var response = await ReadResponseAsync(worker, linked.Token);
                return response is not null && response.Value.GetProperty("ok").GetBoolean();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时（不是主持人点了停止）：按失败处理，让上层降级。
                // 若把它当成取消，主持人会看到「已停止播音」而实际是他自己没点停止。
                StopWorker();
                InvalidateProbeCache();
                return false;
            }
            catch (OperationCanceledException)
            {
                StopWorker();
                throw;
            }
            catch
            {
                // worker 崩了、管道断了：下一次播音会重新拉起 worker。
                StopWorker();
                System.Diagnostics.Debug.WriteLine("[VoxCpm] 播音失败，已降级。");
                return false;
            }
        }
        finally
        {
            _speakLock.Release();
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            var worker = _worker;
            if (worker is null)
            {
                return;
            }

            // 先请它停下来：worker 会中断正在播的音频。
            // 只发指令不杀进程，是为了保住已经加载好的模型。
            try
            {
                if (!worker.HasExited)
                {
                    worker.StandardInput.WriteLine("{\"cmd\":\"stop\"}");
                    worker.StandardInput.Flush();
                }
            }
            catch
            {
                // 进程可能刚退出：落到下面的强杀。
            }

            _worker = null;
        }
    }

    /// <summary>
    /// 拿到可用的 worker；没有就起一个。
    /// 起进程与等「模型已加载」的握手都要有界，否则界面会一直卡着。
    /// </summary>
    private async Task<Process?> EnsureWorkerAsync(VoxCpmOptions options, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_worker is { HasExited: false })
            {
                return _worker;
            }

            _worker = null;
        }

        var python = ResolvePythonPath(options.PythonPath);
        var script = ResolveScriptPath();
        if (string.IsNullOrEmpty(python) || !File.Exists(script))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(options.ModelPath!);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch
        {
            return null;
        }

        if (proc is null)
        {
            return null;
        }

        // stderr 必须被读走：模型加载日志很多，写满管道会让 worker 阻塞在写操作上。
        _ = proc.StandardError.ReadToEndAsync();

        lock (_stateLock)
        {
            _worker = proc;
        }

        using var startupCts = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCts.Token);

        try
        {
            var ready = await ReadResponseAsync(proc, linked.Token);
            if (ready is null || !ready.Value.GetProperty("ok").GetBoolean())
            {
                StopWorker();
                return null;
            }

            return proc;
        }
        catch
        {
            StopWorker();
            return null;
        }
    }

    /// <summary>
    /// 读一行 JSON 响应。worker 的协议是「一行一个 JSON 对象」。
    /// </summary>
    private static async Task<JsonElement?> ReadResponseAsync(Process worker, CancellationToken cancellationToken)
    {
        var line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 不是 JSON：worker 出了意料之外的情况。
            return null;
        }
    }

    /// <summary>按文本长度估算播音超时：中文大约每秒 4–5 个字，留两倍余量。</summary>
    private static TimeSpan SpeakTimeoutFor(int textLength) =>
        TimeSpan.FromSeconds(Math.Clamp(30 + (textLength / 3.0), 30, 600));

    private void StopWorker()
    {
        lock (_stateLock)
        {
            var proc = _worker;
            _worker = null;
            if (proc is null)
            {
                return;
            }

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程可能刚退出或已被释放：不影响降级路径。
            }

            proc.Dispose();
        }
    }

    /// <summary>
    /// 探测依赖是否齐备。
    ///
    /// 只验 python 存在是不够的：voxcpm 包装不上时探测会通过，
    /// 之后每次播音都起进程再失败。这里直接 import 一次真正的模块。
    /// </summary>
    private static bool Probe(VoxCpmOptions options)
    {
        var python = ResolvePythonPath(options.PythonPath);
        var script = ResolveScriptPath();
        if (string.IsNullOrEmpty(python) || !File.Exists(script))
        {
            return false;
        }

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = "-c \"import voxcpm, torch\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (proc is null)
            {
                return false;
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                KillQuietly(proc);
                try
                {
                    proc.WaitForExit(1000);
                }
                catch (InvalidOperationException)
                {
                    // 进程已释放。
                }

                return false;
            }

            try
            {
                Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // 读取失败不影响探测结论。
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                proc?.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static void KillQuietly(Process? proc)
    {
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能刚退出或已被释放：不影响降级路径。
        }
    }

    /// <summary>
    /// 定位脚本：运行目录（含 Voice 子目录）两处足够覆盖打包版与便携版。
    /// 找不到就是找不到 —— 不再回落到开发机绝对路径。
    /// </summary>
    private static string ResolveScriptPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var direct = Path.Combine(baseDir, "voxcpm_worker.py");
        if (File.Exists(direct))
        {
            return direct;
        }

        var directVoice = Path.Combine(baseDir, "Voice", "voxcpm_worker.py");
        if (File.Exists(directVoice))
        {
            return directVoice;
        }

        return direct;
    }

    /// <summary>
    /// 定位 python 解释器：优先设置里配的路径，其次环境变量 <c>LOCALDM_VOXCPM_PYTHON</c>，
    /// 否则用 PATH 上的 python。不写死任何本机绝对路径。
    /// </summary>
    private static string ResolvePythonPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var custom = Environment.GetEnvironmentVariable("LOCALDM_VOXCPM_PYTHON");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return custom;
        }

        return "python";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWorker();
        _speakLock.Dispose();
    }
}

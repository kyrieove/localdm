using System.Diagnostics;
using System.IO;
using System.Text;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// 微软 Edge-TTS 客户端与原生音频播放器。
///
/// 当环境里有 python 与 edge-tts 时，调用 edge_speak.py（经 Windows MCI 播放
/// 微软神经音色，默认 zh-CN-YunxiNeural 沉稳男声）；若环境不可用、网络波动或超时，
/// 自动优雅降级，允许上层退回 Windows 本地 SAPI。
///
/// 三条硬约束：
/// 1) 不写死任何开发机绝对路径 —— 打包版与便携版要能在别的机器上跑；
/// 2) 子进程的 stdout/stderr 必须被读走，管道写满会让子进程永久阻塞；
/// 3) 播音必须有硬超时，否则网络卡住会把整条播音队列堵死。
/// </summary>
public static class EdgeTtsEngine
{
    private static readonly object Lock = new();

    /// <summary>探测结果的缓存时长：装好依赖后不必重启应用即可生效。</summary>
    private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static string? _cachedPythonPath;
    private static bool? _cachedIsAvailable;
    private static DateTime _probeExpiresAt = DateTime.MinValue;
    private static Process? _currentProc;

    /// <summary>
    /// 引擎是否可用。首次调用会同步探测（最多 <see cref="ProbeTimeout"/>），
    /// 因此界面线程应先调用 <see cref="WarmUpAsync"/> 预热，避免启动卡顿。
    /// </summary>
    public static bool IsAvailable()
    {
        lock (Lock)
        {
            if (_cachedIsAvailable.HasValue && DateTime.UtcNow < _probeExpiresAt)
            {
                return _cachedIsAvailable.Value;
            }
        }

        // 探测要起 python 子进程并等它退出（最长 5 秒）。
        // 绝不能持着全局锁做这件事：Stop() 用的就是这把锁，
        // 探测期间按「停止播音」会拿不到锁。
        var available = Probe();

        lock (Lock)
        {
            _cachedIsAvailable = available;
            _probeExpiresAt = DateTime.UtcNow + ProbeCacheTtl;
            return available;
        }
    }

    /// <summary>探测结果作废：下一次问就会重新探测（装好依赖后不必重启应用）。</summary>
    public static void InvalidateProbeCache()
    {
        lock (Lock)
        {
            _cachedIsAvailable = null;
            _probeExpiresAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 后台预热探测结果。启动时先跑它，之后界面线程再问 <see cref="IsAvailable"/> 就是瞬时的。
    /// </summary>
    public static Task WarmUpAsync() => Task.Run(() => IsAvailable());

    /// <summary>
    /// 实际播一次。成功返回 true；环境不可用或失败返回 false，由上层降级到本地 SAPI。
    /// </summary>
    public static async Task<bool> TrySpeakAsync(string text, string voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsAvailable())
        {
            return false;
        }

        var python = ResolvePythonPath();
        var script = ResolveScriptPath();
        if (string.IsNullOrEmpty(python) || !File.Exists(script))
        {
            return false;
        }

        Stop();

        // 播音必须有硬超时：edge-tts 走公网，连接卡住时没有 timeout 就会永远占着队列。
        using var timeoutCts = new CancellationTokenSource(SpeakTimeoutFor(text.Length));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var psi = CreateSpeechStartInfo(python, script, voice);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            lock (Lock)
            {
                _currentProc = proc;
            }

            // 管道必须被读走：子进程写满 stdout/stderr 缓冲区后会阻塞在写操作上永不退出。
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);

            await proc.StandardInput.WriteAsync(text.AsMemory(), linked.Token);
            proc.StandardInput.Close();

            using var reg = linked.Token.Register(() => KillQuietly(proc));

            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // 读干两个流，避免进程退出后残留未消费的读取任务。
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时（不是主持人点了停止）：按失败处理，让上层降级到本地语音。
            // 若把它当成取消，主持人会看到「已停止播音」而实际是他自己没点停止。
            Stop();
            InvalidateProbeCache();
            return false;
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch
        {
            // 脚本 ImportError、网络失败、Win32Exception 等：无法区分「环境没有」与「Edge 坏了」，
            // 这里统一降级，诊断信息只写 Debug 输出，不进公共界面。
            System.Diagnostics.Debug.WriteLine("[EdgeTts] 播音失败，已降级到本地语音。");
            return false;
        }
        finally
        {
            lock (Lock)
            {
                _currentProc = null;
            }
        }
    }

    private static ProcessStartInfo CreateSpeechStartInfo(string python, string script, string voice)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // 文本走 stdin，不走命令行：命令行有长度上限，而且任务管理器能直接看到参数。
            RedirectStandardInput = true,
            // Python 按 UTF-8 解码原始 stdin；不能沿用 Windows 的 GB2312 控制台编码。
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(voice);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    /// <summary>立即停止当前播音（杀子进程）。</summary>
    public static void Stop()
    {
        lock (Lock)
        {
            var proc = _currentProc;
            _currentProc = null;
            KillQuietly(proc);
        }
    }

    /// <summary>按文本长度估算播音超时：中文大约每秒 4–5 个字，留两倍余量。</summary>
    private static TimeSpan SpeakTimeoutFor(int textLength) =>
        TimeSpan.FromSeconds(Math.Clamp(30 + (textLength / 3.0), 30, 600));

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
    /// 探测 python 与脚本真正需要的依赖。
    /// 播放使用 Windows 自带的 MCI，不要求安装 sounddevice / PortAudio。
    /// </summary>
    private static bool Probe()
    {
        var python = ResolvePythonPath();
        var script = ResolveScriptPath();
        if (string.IsNullOrEmpty(python) || !File.Exists(script))
        {
            return false;
        }

        Process? proc = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import edge_tts, soundfile");
            proc = Process.Start(startInfo);

            if (proc is null)
            {
                return false;
            }

            // 必须把探测输出读走，否则子进程写满管道后同样会卡住。
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                // 超时不能只释放 Process 对象：子进程会变成孤儿留在后台。
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

            // 把管道读干，读取任务才不会带着未观察的异常结束。
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

    /// <summary>
    /// 定位脚本：运行目录（含 Voice 子目录）两处足够覆盖打包版与便携版。
    /// 找不到就是找不到 —— 不再回落到开发机绝对路径。
    /// </summary>
    private static string ResolveScriptPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var direct = Path.Combine(baseDir, "edge_speak.py");
        if (File.Exists(direct))
        {
            return direct;
        }

        var directVoice = Path.Combine(baseDir, "Voice", "edge_speak.py");
        if (File.Exists(directVoice))
        {
            return directVoice;
        }

        return direct;
    }

    /// <summary>
    /// 定位 python 解释器：优先环境变量 <c>LOCALDM_PYTHON_PATH</c>，否则用 PATH 上的 python。
    /// 不写死任何本机绝对路径。
    /// </summary>
    private static string? ResolvePythonPath()
    {
        if (_cachedPythonPath is not null)
        {
            return _cachedPythonPath;
        }

        var custom = Environment.GetEnvironmentVariable("LOCALDM_PYTHON_PATH");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return _cachedPythonPath = custom;
        }

        return _cachedPythonPath = "python";
    }
}

using LocalDm.Core.Ports;
using NAudio.Wave;

namespace LocalDm.Infrastructure.Voice.Dictation;

public sealed class DictationService : IDictationService
{
    private readonly DictationModelStore _store;
    private readonly object _gate = new();

#pragma warning disable CS0618
    private WaveInEvent? _mic;
#pragma warning restore CS0618
    private MemoryStream? _pcm;
    private DictationRecognizer? _recognizer;
    private Timer? _idleTimer;
    private DictationState _state = DictationState.Idle;
    private bool _disposed;

    public DictationState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event Action<DictationState>? StateChanged;
    public event Action<string>? Progress;

    public DictationService(DictationModelStore store)
    {
        _store = store;
        _idleTimer = new Timer(OnIdleTimeout, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void SetState(DictationState state)
    {
        Action<DictationState>? handler = null;
        lock (_gate)
        {
            if (_state != state)
            {
                _state = state;
                handler = StateChanged;
            }
        }
        handler?.Invoke(state);
    }

    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_state != DictationState.Idle)
            {
                return "正在处理上一段语音，请稍等。";
            }
        }

        SetState(DictationState.Preparing);

        string? prepareError;
        try
        {
            prepareError = await _store.EnsureAsync(
                new Progress<string>(p => Progress?.Invoke(p)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SetState(DictationState.Idle);
            return null;
        }
        catch
        {
            // 枚举来源/目标目录被拒绝等：不能让状态卡在 Preparing
            SetState(DictationState.Idle);
            return DictationModelStore.PrepareFailedMessage;
        }

        if (prepareError != null)
        {
            SetState(DictationState.Idle);
            return prepareError;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                SetState(DictationState.Idle);
                return null;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

#pragma warning disable CS0618
            if (WaveInEvent.DeviceCount == 0)
#pragma warning restore CS0618
            {
                SetState(DictationState.Idle);
                return "没有找到麦克风，请检查 Windows 设置 → 隐私 → 麦克风。";
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    SetState(DictationState.Idle);
                    return null;
                }

                _pcm = new MemoryStream();
#pragma warning disable CS0618
                _mic = new WaveInEvent
#pragma warning restore CS0618
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };

                _mic.DataAvailable += (s, e) =>
                {
                    lock (_gate)
                    {
                        _pcm?.Write(e.Buffer, 0, e.BytesRecorded);
                    }
                };

                _mic.StartRecording();
            }

            SetState(DictationState.Listening);
            return null;
        }
        catch (OperationCanceledException)
        {
            CleanupMic();
            SetState(DictationState.Idle);
            return null;
        }
        catch
        {
            CleanupMic();
            SetState(DictationState.Idle);
            return "无法使用麦克风，请检查 Windows 设置 → 隐私 → 麦克风是否允许桌面应用使用。";
        }
    }

    public async Task<DictationResult> StopAsync(CancellationToken cancellationToken)
    {
        byte[]? bytes = null;

        lock (_gate)
        {
            if (_disposed || _state != DictationState.Listening)
            {
                return new DictationResult("", null);
            }

            StopAndDisposeMic();

            if (_pcm != null)
            {
                bytes = _pcm.ToArray();
                _pcm.Dispose();
                _pcm = null;
            }
        }

        SetState(DictationState.Recognizing);

        if (bytes == null || bytes.Length < 9600)
        {
            SetState(DictationState.Idle);
            return new DictationResult("", "没听清，请再说一次。");
        }

        try
        {
            var text = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samples = DictationRecognizer.Pcm16ToFloat(bytes);
                return Recognize(samples);
            }, cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
            {
                return new DictationResult("", "没听清，请再说一次。");
            }

            return new DictationResult(text, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DictationResult("", "识别出错，请再试一次，或直接打字。");
        }
        finally
        {
            SetState(DictationState.Idle);
        }
    }

    private string Recognize(float[] samples)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return "";
            }

            _recognizer ??= new DictationRecognizer(_store.Destination);
            _idleTimer?.Change(TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
        }

        var text = _recognizer.Transcribe(samples);

        lock (_gate)
        {
            _idleTimer?.Change(TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
        }

        return text;
    }

    private void OnIdleTimeout(object? state)
    {
        ReleaseIdle();
    }

    private void ReleaseIdle()
    {
        lock (_gate)
        {
            if (_state == DictationState.Idle)
            {
                _recognizer?.Dispose();
                _recognizer = null;
            }
            else
            {
                // 到点时正在录音或识别：稍后再看，否则模型会一直留在内存里
                _idleTimer?.Change(TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Cancel()
    {
        var wasListening = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_state == DictationState.Listening)
            {
                wasListening = true;
                StopAndDisposeMic();
                if (_pcm != null)
                {
                    _pcm.Dispose();
                    _pcm = null;
                }
            }
        }

        // 识别进行中不能改状态，否则会放行新的录音并被旧识别的 finally 覆盖
        if (wasListening)
        {
            SetState(DictationState.Idle);
        }
    }

    private void CleanupMic()
    {
        lock (_gate)
        {
            StopAndDisposeMic();
            if (_pcm != null)
            {
                _pcm.Dispose();
                _pcm = null;
            }
        }
    }

    private void StopAndDisposeMic()
    {
        if (_mic != null)
        {
            try
            {
                _mic.StopRecording();
            }
            catch
            {
            }
            _mic.Dispose();
            _mic = null;
        }
    }

    public void Dispose()
    {
        // 先收麦克风：Cancel 在 _disposed 之后会直接返回
        Cancel();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleTimer?.Dispose();
            _idleTimer = null;

            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}

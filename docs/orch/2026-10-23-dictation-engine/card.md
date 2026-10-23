# dictation-engine — 软件内置「按下说话转文字」引擎：本地 Qwen3-ASR（sherpa-onnx）+ 麦克风录音

## Goal
- `IDictationService`（Core 端口）与 `DictationService`（Infrastructure）可用：`StartAsync` 准备模型（首次把 OpenLess 已下载的 qwen3-asr-0.6b-int8 模型复制到软件数据目录并校验）并开始录音；`StopAsync` 停止录音、本地识别、返回文字；音频只在内存里，识别完即丢，不落盘。
- 识别用 sherpa-onnx 官方 .NET 包（Apache-2.0）加载 Qwen3-ASR；模型只在第一次识别时加载进内存，识别后闲置 5 分钟自动释放。
- 一个 Probe 命令 `--dictation-check` 能用系统中文语音合成一句话 → 本地识别 → 打印识别结果与耗时（用来实测，不进单元测试）。
- 不复制 OpenLess 的任何源码（它是 AGPL-3.0）；只用它已下载的模型文件。

## Risk
medium

## Parameters
- 包版本：`org.k2fsa.sherpa.onnx` 1.13.8（它依赖运行时包 `org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.8，自动带入），`NAudio` 3.3.0。
- 音频：16000 Hz、单声道、16 位 PCM；录音缓冲 100 毫秒；识别线程数 4，Provider `cpu`。
- 最短有效录音 0.3 秒（4800 个采样点），更短一律当「没听清」。
- 模型名 `qwen3-asr-0.6b-int8`；来源目录 `%APPDATA%\OpenLess\models\sherpa-onnx\qwen3-asr-0.6b-int8`（source: 本机 `ls` 该目录得到 `conv_frontend.onnx`、`decoder.int8.onnx`、`encoder.int8.onnx`、`tokenizer/`，共约 942 MB）；目标目录 `<数据根>\models\qwen3-asr-0.6b-int8`。
- 校验：每个文件复制后算 SHA-256，与来源一致才算成功；先复制到 `<目标>.part`，全部通过后写 `ready.txt` 再改名为目标目录，中途失败只留 `.part`（下次开始前先删掉）。
- 用户可见文案（原样）：没找到模型 →「没有找到语音模型。请先安装 OpenLess（它自带识别模型），或把模型文件放到：{目标目录}」；没有麦克风 →「没有找到麦克风，请检查 Windows 设置 → 隐私 → 麦克风。」；麦克风打不开 →「无法使用麦克风，请检查 Windows 设置 → 隐私 → 麦克风是否允许桌面应用使用。」；空结果或太短 →「没听清，请再说一次。」；识别抛异常 →「识别出错，请再试一次，或直接打字。」；复制进度 →「正在准备语音模型（一次性，约 0.9 GB）：{百分比}%」；复制失败 →「语音模型准备失败，请确认磁盘剩余空间大于 1 GB 后重试。」；识别进行中又点开始 →「正在处理上一段语音，请稍等。」。
- 官方示例参考（只读）：sherpa-onnx 仓库 v1.13.8 的 `dotnet-examples/non-streaming-qwen3-asr-decode-files`；API：`new OfflineRecognizerConfig()`，`config.ModelConfig.Qwen3Asr.{ConvFrontend,Encoder,Decoder,Tokenizer}`，`config.ModelConfig.NumThreads`，`config.ModelConfig.Provider`，`new OfflineRecognizer(config)`，`recognizer.CreateStream()`，`stream.AcceptWaveform(16000, float[])`，`recognizer.Decode(stream)`，`stream.Result.Text`。

## Files
may change: `Directory.Packages.props`, `src/LocalDm.Infrastructure/LocalDm.Infrastructure.csproj`, `src/LocalDm.Core/packages.lock.json`, `src/LocalDm.Infrastructure/packages.lock.json`, `src/LocalDm.Tablet/packages.lock.json`, `tests/LocalDm.Tests/packages.lock.json`, `tests/LocalDm.UiTests/packages.lock.json`, `tools/LocalDm.Probe/packages.lock.json`, `src/LocalDm.Core/Ports/IDictationService.cs`, `src/LocalDm.Infrastructure/Voice/Dictation/`, `tools/LocalDm.Probe/Program.cs`, `tools/LocalDm.Probe/DictationProbe.cs`, `tests/LocalDm.Tests/DictationTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 7)
1. `Directory.Packages.props`：在 `<PackageVersion Include="QRCoder" Version="1.7.0" />` 这一行之后加两行：`<PackageVersion Include="org.k2fsa.sherpa.onnx" Version="1.13.8" />` 和 `<PackageVersion Include="NAudio" Version="3.3.0" />`。`src/LocalDm.Infrastructure/LocalDm.Infrastructure.csproj`：在 `<PackageReference Include="System.Speech" />` 之后加 `<PackageReference Include="org.k2fsa.sherpa.onnx" />` 和 `<PackageReference Include="NAudio" />`。然后在仓库根执行 `dotnet restore LocalDm.slnx --force-evaluate` 让各个 `packages.lock.json` 更新（它们在 `may change` 里）。
2. 新建 `src/LocalDm.Core/Ports/IDictationService.cs`（命名空间 `LocalDm.Core.Ports`），原样：
```csharp
namespace LocalDm.Core.Ports;

public enum DictationState { Idle, Preparing, Listening, Recognizing }

/// <summary>识别结果：Text 为空时 Problem 说明原因，直接给人看。</summary>
public sealed record DictationResult(string Text, string? Problem);

/// <summary>
/// 按下说话转文字：本机录音、本机识别，音频只在内存里，识别完即丢，不落盘。
/// </summary>
public interface IDictationService : IDisposable
{
    DictationState State { get; }

    event Action<DictationState>? StateChanged;

    /// <summary>准备模型时的进度文字（只在首次复制模型时出现）。</summary>
    event Action<string>? Progress;

    /// <summary>准备模型并开始录音。返回 null 表示已经在听；返回文字表示没能开始，原因直接给人看。</summary>
    Task<string?> StartAsync(CancellationToken cancellationToken);

    /// <summary>停止录音并识别。</summary>
    Task<DictationResult> StopAsync(CancellationToken cancellationToken);

    /// <summary>放弃本次录音，什么都不识别。</summary>
    void Cancel();
}
```
3. 新建 `src/LocalDm.Infrastructure/Voice/Dictation/DictationModelStore.cs`（命名空间 `LocalDm.Infrastructure.Voice.Dictation`）：类 `public sealed class DictationModelStore`，构造函数 `(string dataRoot, string? sourceDirectory = null)`；常量 `public const string ModelName = "qwen3-asr-0.6b-int8";` 必需文件 `conv_frontend.onnx`、`encoder.int8.onnx`、`decoder.int8.onnx` 和子目录 `tokenizer`（含其下所有文件）；`public string Destination`（`<dataRoot>/models/qwen3-asr-0.6b-int8`）；`public bool IsReady`（目标目录里有 `ready.txt`、三个文件、`tokenizer` 目录）；`public async Task<string?> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)`：已就绪返回 null；来源目录默认 `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenLess", "models", "sherpa-onnx", ModelName)`，来源缺文件返回 Parameters 里的「没有找到语音模型…」文案；否则按 Parameters 的 `.part` 流程逐文件复制（80 KB 缓冲，`await` 流式复制，按已复制字节占总字节的百分比向 `progress` 报文案，百分比变化才报），每个文件复制完比较来源与目标的 SHA-256（`System.Security.Cryptography.SHA256.HashDataAsync`），不一致抛 `IOException`；全部通过写 `ready.txt`（内容 `ok`），删旧目标目录（如存在）后 `Directory.Move(part, Destination)`，返回 null；`IOException` 与 `UnauthorizedAccessException` 返回 Parameters 里的「语音模型准备失败…」文案；`OperationCanceledException` 不捕获。
4. 新建 `src/LocalDm.Infrastructure/Voice/Dictation/DictationRecognizer.cs`：`public sealed class DictationRecognizer : IDisposable`，构造函数 `(string modelDirectory)` 按 Parameters 里的 API 建 `OfflineRecognizer`（`ConvFrontend = Path.Combine(dir, "conv_frontend.onnx")`、`Encoder = ...encoder.int8.onnx`、`Decoder = ...decoder.int8.onnx`、`Tokenizer = Path.Combine(dir, "tokenizer")`，`NumThreads = 4`，`Provider = "cpu"`）；`public string Transcribe(float[] samples)`：`using var stream = recognizer.CreateStream(); stream.AcceptWaveform(16000, samples); recognizer.Decode(stream); return stream.Result.Text.Trim();`；`Dispose` 释放 recognizer。再加 `public static float[] Pcm16ToFloat(byte[] pcm)`：每 2 字节小端有符号整数除以 32768f，奇数长度丢掉最后 1 字节。
5. 新建 `src/LocalDm.Infrastructure/Voice/Dictation/DictationService.cs`：`public sealed class DictationService : IDictationService`，构造函数 `(DictationModelStore store)`；字段：`WaveInEvent? _mic`、`MemoryStream? _pcm`（读写都在 `lock (_gate)` 里）、`DictationRecognizer? _recognizer`、`System.Threading.Timer? _idleTimer`、`DictationState _state`；`State` 属性，`SetState` 变化时触发 `StateChanged`。`StartAsync`：状态不是 Idle 返回「正在处理上一段语音，请稍等。」；`SetState(Preparing)`；调 `store.EnsureAsync(new Progress<string>(p => Progress?.Invoke(p)), ct)`，返回非 null 则 `SetState(Idle)` 并返回该文案；`WaveInEvent.DeviceCount == 0` 返回「没有找到麦克风…」；建 `_pcm = new MemoryStream()`，`_mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 }`，`DataAvailable` 里 `lock (_gate) { _pcm?.Write(e.Buffer, 0, e.BytesRecorded); }`，`StartRecording()`，`SetState(Listening)`，返回 null；`OperationCanceledException` → 清理、`SetState(Idle)`、返回 null；其它异常 → 清理、`SetState(Idle)`、返回「无法使用麦克风…」文案。`StopAsync`：状态不是 Listening 返回 `new DictationResult("", null)`；停掉并释放 `_mic`，在锁里取出 `_pcm.ToArray()` 并置空；`SetState(Recognizing)`；字节数 < 9600（4800 个采样点 × 2 字节）→ `SetState(Idle)`，返回 `new DictationResult("", "没听清，请再说一次。")`；否则 `await Task.Run(() => Recognize(DictationRecognizer.Pcm16ToFloat(bytes)), ct)`，`Recognize` 里懒创建 `_recognizer = new DictationRecognizer(store.Destination)`（加锁，只建一次）、调用 `Transcribe`、把 `_idleTimer` 重置为 5 分钟后调用 `ReleaseIdle`（`ReleaseIdle` 在状态是 Idle 时释放并置空 `_recognizer`）；结果为空串返回「没听清，请再说一次。」；识别抛异常（`OperationCanceledException` 除外）返回「识别出错，请再试一次，或直接打字。」；`finally` 里 `SetState(Idle)`。`Cancel()`：若在 Listening，停掉 `_mic`、丢掉 `_pcm`、`SetState(Idle)`。`Dispose`：`Cancel()`，释放 `_idleTimer` 与 `_recognizer`。全部公共方法对 `_disposed` 做保护（已释放则直接返回）。
6. `tools/LocalDm.Probe/Program.cs`：在 `--prepare-tablet` 那个 `if` 块之后（`Console.WriteLine("LocalDm.Probe:");` 之前）加：
```csharp
if (args.Length > 0 && args[0] == "--dictation-check")
{
    return await DictationProbe.RunAsync(args.Skip(1).ToArray());
}
```
   并在用法列表末尾（`--prepare-tablet ...` 那行之后）加一行 `Console.WriteLine("  --dictation-check [--model-dir <目录>]");`。新建 `tools/LocalDm.Probe/DictationProbe.cs`（命名空间 `LocalDm.Infrastructure.Probe`，`internal static class DictationProbe`，`public static Task<int> RunAsync(string[] args)`）：模型目录取 `--model-dir`，没给就用 `new DictationModelStore(Path.GetTempPath()).Destination` 不对 —— 没给时用 `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenLess", "models", "sherpa-onnx", DictationModelStore.ModelName)`；用 `System.Speech.Synthesis.SpeechSynthesizer` 选第一个 `zh` 开头的已安装音色，`SetOutputToWaveFile(tmp, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono))` 朗读固定句子「你好，我想去书房看一看。」，读回 wav 去掉 44 字节头得到 PCM；先打印「加载模型…」并计时，`new DictationRecognizer(dir)`，再计时 `Transcribe(DictationRecognizer.Pcm16ToFloat(pcm))`，打印 `识别结果：{text}`、`加载 {ms} ms，识别 {ms} ms`；没有 zh 音色或目录不存在时打印原因并返回 2；正常返回 0。
7. 新建 `tests/LocalDm.Tests/DictationTests.cs`（命名空间 `LocalDm.Tests`，`using LocalDm.Infrastructure.Voice.Dictation;`），实现 `IDisposable` 用临时目录：
   - `Pcm16_converts_to_float_and_drops_odd_trailing_byte`：`new byte[] { 0x00, 0x80, 0xFF, 0x7F, 0x01 }` 转出长度 2，第一个等于 `-1.0f`，第二个接近 `0.99997f`（`Assert.InRange(x, 0.9999f, 1f)`）。
   - `Model_store_copies_verifies_and_marks_ready`：造合成来源目录（三个小文件各写几个字节 + `tokenizer/vocab.txt`），`new DictationModelStore(dataRoot, sourceDir)`，`IsReady` 为 false；`await EnsureAsync(null, ct)` 返回 null；`IsReady` 为 true；目标里三个文件内容与来源逐字节相同；再调一次仍返回 null。
   - `Model_store_reports_missing_source_with_target_path`：来源目录不存在，`EnsureAsync` 返回的文案含 `Destination` 且含「没有找到语音模型」，`IsReady` 仍为 false。
   - `Model_store_leaves_no_ready_marker_when_source_changes_mid_copy` 不做（YAGNI）。

## Check
`dotnet test tests/LocalDm.Tests --filter-class "LocalDm.Tests.DictationTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- 不读取、不复制 OpenLess 的源码；不修改 OpenLess 的目录与设置，只读它的模型目录。
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

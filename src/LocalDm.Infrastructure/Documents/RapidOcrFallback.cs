using System.Diagnostics;
using System.Text.RegularExpressions;
using LocalDm.Core.Domain;

namespace LocalDm.Infrastructure.Documents;

/// <summary>
/// 缺页回退 OCR：主解析器（MinerU flash）一页都没读出来时，用本机另一套 OCR 再试一次。
/// 只在本机执行；输出会标注来源实现，便于人工优先抽查。
/// 已知上限：轻量识别在竖排/旋转版式上会打乱行序，因此仅作回退，不作主力。
/// </summary>
public interface IFallbackOcr
{
    string Name { get; }

    Task<IReadOnlyList<ExtractionBatch>> TryReadAsync(string imagePath, CancellationToken cancellationToken);
}

/// <summary>
/// 通过外部 Python + RapidOCR（ONNX，本机运行）读取单张图片。
/// 不下载模型以外的任何东西，也不把图片发到任何服务。
/// </summary>
public sealed partial class RapidOcrFallback : IFallbackOcr
{
    private readonly string _pythonPath;
    private readonly string _scriptPath;

    public RapidOcrFallback(string pythonPath, string scriptPath)
    {
        _pythonPath = pythonPath;
        _scriptPath = scriptPath;
    }

    public string Name => "rapidocr";

    public async Task<IReadOnlyList<ExtractionBatch>> TryReadAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        // 裸命令（默认 "python"）交给进程启动按 PATH 找；File.Exists 不查 PATH。
        if ((Path.IsPathRooted(_pythonPath) && !File.Exists(_pythonPath)) || !File.Exists(_scriptPath))
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(_scriptPath);
        startInfo.ArgumentList.Add("--image");
        startInfo.ArgumentList.Add(Path.GetFullPath(imagePath));
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return [];
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return [];
        }

        var stdout = await stdoutTask;
        _ = await stderrTask;
        if (process.ExitCode != 0)
        {
            return [];
        }

        var text = stdout.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        return
        [
            new ExtractionBatch
            {
                Locator = "doc:fallback/tier:ocr/page:1/block:0",
                Text = text,
                StartOffset = 0,
                EndOffset = text.Length,
                PageRange = "1",
                StartLocator = "doc:fallback/tier:ocr/page:1",
                EndLocator = null,
            },
        ];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 已退出
        }
    }

    /// <summary>默认回退脚本位置（仓库内 scripts/fallback-ocr.py）。</summary>
    public static string DefaultScriptPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LocalDm.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir is null ? string.Empty : Path.Combine(dir, "scripts", "fallback-ocr.py");
    }
}

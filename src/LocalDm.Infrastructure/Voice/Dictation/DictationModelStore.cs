using System.Security.Cryptography;

namespace LocalDm.Infrastructure.Voice.Dictation;

public sealed class DictationModelStore
{
    public const string ModelName = "qwen3-asr-0.6b-int8";

    public const string PrepareFailedMessage = "语音模型准备失败，请确认磁盘剩余空间大于 1 GB 后重试。";

    private static readonly string[] RequiredFiles =
    [
        "conv_frontend.onnx",
        "encoder.int8.onnx",
        "decoder.int8.onnx"
    ];

    private readonly string _sourceDirectory;

    public string Destination { get; }

    public DictationModelStore(string dataRoot, string? sourceDirectory = null)
    {
        Destination = Path.Combine(dataRoot, "models", ModelName);
        _sourceDirectory = sourceDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenLess",
            "models",
            "sherpa-onnx",
            ModelName);
    }

    public bool IsReady
    {
        get
        {
            if (!Directory.Exists(Destination))
            {
                return false;
            }

            if (!File.Exists(Path.Combine(Destination, "ready.txt")))
            {
                return false;
            }

            foreach (var file in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(Destination, file)))
                {
                    return false;
                }
            }

            var tokenizerDir = Path.Combine(Destination, "tokenizer");
            return Directory.Exists(tokenizerDir) && Directory.EnumerateFileSystemEntries(tokenizerDir).Any();
        }
    }

    public async Task<string?> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return null;
        }

        if (!Directory.Exists(_sourceDirectory))
        {
            return $"没有找到语音模型。请先安装 OpenLess（它自带识别模型），或把模型文件放到：{Destination}";
        }

        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(_sourceDirectory, file)))
            {
                return $"没有找到语音模型。请先安装 OpenLess（它自带识别模型），或把模型文件放到：{Destination}";
            }
        }

        var sourceTokenizerDir = Path.Combine(_sourceDirectory, "tokenizer");
        if (!Directory.Exists(sourceTokenizerDir) || !Directory.EnumerateFileSystemEntries(sourceTokenizerDir).Any())
        {
            return $"没有找到语音模型。请先安装 OpenLess（它自带识别模型），或把模型文件放到：{Destination}";
        }

        var partDir = Destination + ".part";

        try
        {
            if (Directory.Exists(partDir))
            {
                Directory.Delete(partDir, recursive: true);
            }

            Directory.CreateDirectory(partDir);
            var partTokenizerDir = Path.Combine(partDir, "tokenizer");
            Directory.CreateDirectory(partTokenizerDir);

            // 收集所有需要复制的文件（相对路径与绝对路径）
            var filesToCopy = new List<(string relativePath, string sourcePath, string destPath, long size)>();

            foreach (var file in RequiredFiles)
            {
                var src = Path.Combine(_sourceDirectory, file);
                var dst = Path.Combine(partDir, file);
                var fi = new FileInfo(src);
                filesToCopy.Add((file, src, dst, fi.Length));
            }

            foreach (var srcFile in Directory.EnumerateFiles(sourceTokenizerDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_sourceDirectory, srcFile);
                var dst = Path.Combine(partDir, rel);
                var fi = new FileInfo(srcFile);
                filesToCopy.Add((rel, srcFile, dst, fi.Length));
            }

            long totalBytes = filesToCopy.Sum(f => f.size);
            long copiedBytes = 0;
            int lastPercentage = -1;

            byte[] buffer = new byte[80 * 1024];

            foreach (var item in filesToCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetDir = Path.GetDirectoryName(item.destPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await using (var inStream = new FileStream(item.sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var outStream = new FileStream(item.destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = await inStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        copiedBytes += read;

                        int percentage = totalBytes > 0 ? (int)(copiedBytes * 100 / totalBytes) : 100;
                        if (percentage != lastPercentage)
                        {
                            lastPercentage = percentage;
                            progress?.Report($"正在准备语音模型（一次性，约 0.9 GB）：{percentage}%");
                        }
                    }
                }

                // 校验 SHA-256
                await using (var srcStream = File.OpenRead(item.sourcePath))
                await using (var dstStream = File.OpenRead(item.destPath))
                {
                    var srcHash = await SHA256.HashDataAsync(srcStream, cancellationToken);
                    var dstHash = await SHA256.HashDataAsync(dstStream, cancellationToken);

                    if (!srcHash.SequenceEqual(dstHash))
                    {
                        throw new IOException($"SHA-256 mismatch for {item.relativePath}");
                    }
                }
            }

            await File.WriteAllTextAsync(Path.Combine(partDir, "ready.txt"), "ok", cancellationToken);

            if (Directory.Exists(Destination))
            {
                Directory.Delete(Destination, recursive: true);
            }

            var destParent = Path.GetDirectoryName(Destination);
            if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            Directory.Move(partDir, Destination);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PrepareFailedMessage;
        }
    }
}

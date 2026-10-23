using System.IO;
using LocalDm.Infrastructure.Voice.Dictation;
using Xunit;

namespace LocalDm.Tests;

public sealed class DictationTests : IDisposable
{
    private readonly string _tempDir;

    public DictationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dictation_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Pcm16_converts_to_float_and_drops_odd_trailing_byte()
    {
        var pcm = new byte[] { 0x00, 0x80, 0xFF, 0x7F, 0x01 };
        var floats = DictationRecognizer.Pcm16ToFloat(pcm);

        Assert.Equal(2, floats.Length);
        Assert.Equal(-1.0f, floats[0]);
        Assert.InRange(floats[1], 0.9999f, 1f);
    }

    [Fact]
    public async Task Model_store_copies_verifies_and_marks_ready()
    {
        var dataRoot = Path.Combine(_tempDir, "data");
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        var convBytes = new byte[] { 1, 2, 3 };
        var encBytes = new byte[] { 4, 5, 6, 7 };
        var decBytes = new byte[] { 8, 9 };

        var ct = TestContext.Current.CancellationToken;

        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "conv_frontend.onnx"), convBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "encoder.int8.onnx"), encBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "decoder.int8.onnx"), decBytes, ct);

        var tokenizerDir = Path.Combine(sourceDir, "tokenizer");
        Directory.CreateDirectory(tokenizerDir);
        var vocabBytes = new byte[] { 10, 11, 12 };
        await File.WriteAllBytesAsync(Path.Combine(tokenizerDir, "vocab.txt"), vocabBytes, ct);

        var store = new DictationModelStore(dataRoot, sourceDir);
        Assert.False(store.IsReady);

        var error = await store.EnsureAsync(null, ct);
        Assert.Null(error);
        Assert.True(store.IsReady);

        Assert.Equal(convBytes, await File.ReadAllBytesAsync(Path.Combine(store.Destination, "conv_frontend.onnx"), ct));
        Assert.Equal(encBytes, await File.ReadAllBytesAsync(Path.Combine(store.Destination, "encoder.int8.onnx"), ct));
        Assert.Equal(decBytes, await File.ReadAllBytesAsync(Path.Combine(store.Destination, "decoder.int8.onnx"), ct));
        Assert.Equal(vocabBytes, await File.ReadAllBytesAsync(Path.Combine(store.Destination, "tokenizer", "vocab.txt"), ct));

        var secondError = await store.EnsureAsync(null, ct);
        Assert.Null(secondError);
    }

    [Fact]
    public async Task Model_store_reports_missing_source_with_target_path()
    {
        var dataRoot = Path.Combine(_tempDir, "data");
        var missingSourceDir = Path.Combine(_tempDir, "non_existent_source");

        var store = new DictationModelStore(dataRoot, missingSourceDir);
        Assert.False(store.IsReady);

        var error = await store.EnsureAsync(null, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Contains(store.Destination, error);
        Assert.Contains("没有找到语音模型", error);
        Assert.False(store.IsReady);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}

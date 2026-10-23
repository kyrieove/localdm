using SherpaOnnx;

namespace LocalDm.Infrastructure.Voice.Dictation;

public sealed class DictationRecognizer : IDisposable
{
    private readonly OfflineRecognizer _recognizer;
    private bool _disposed;

    public DictationRecognizer(string modelDirectory)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Qwen3Asr.ConvFrontend = Path.Combine(modelDirectory, "conv_frontend.onnx");
        config.ModelConfig.Qwen3Asr.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
        config.ModelConfig.Qwen3Asr.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
        config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(modelDirectory, "tokenizer");
        config.ModelConfig.NumThreads = 4;
        config.ModelConfig.Provider = "cpu";

        _recognizer = new OfflineRecognizer(config);
    }

    public string Transcribe(float[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    public static float[] Pcm16ToFloat(byte[] pcm)
    {
        int sampleCount = pcm.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(pcm, i * 2);
            samples[i] = sample / 32768f;
        }
        return samples;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer.Dispose();
    }
}

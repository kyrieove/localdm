using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using LocalDm.Infrastructure.Voice.Dictation;

namespace LocalDm.Infrastructure.Probe;

internal static class DictationProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? modelDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--model-dir" && i + 1 < args.Length)
            {
                modelDir = args[i + 1];
                i++;
            }
        }

        modelDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenLess",
            "models",
            "sherpa-onnx",
            DictationModelStore.ModelName);

        if (!Directory.Exists(modelDir))
        {
            Console.WriteLine($"模型目录不存在：{modelDir}");
            return 2;
        }

        using var synth = new SpeechSynthesizer();
        var zhVoice = synth.GetInstalledVoices()
            .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

        if (zhVoice == null)
        {
            Console.WriteLine("没有找到中文系统音色（zh 开头）。");
            return 2;
        }

        synth.SelectVoice(zhVoice.VoiceInfo.Name);

        var tmpWav = Path.Combine(Path.GetTempPath(), $"dictation_probe_{Guid.NewGuid():N}.wav");
        try
        {
            synth.SetOutputToWaveFile(
                tmpWav,
                new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

            synth.Speak("你好，我想去书房看一看。");
            synth.SetOutputToNull();

            var wavBytes = await File.ReadAllBytesAsync(tmpWav);
            if (wavBytes.Length <= 44)
            {
                Console.WriteLine("合成音频为空。");
                return 2;
            }

            var pcm = new byte[wavBytes.Length - 44];
            Array.Copy(wavBytes, 44, pcm, 0, pcm.Length);

            Console.WriteLine("加载模型…");
            var sw = Stopwatch.StartNew();
            using var recognizer = new DictationRecognizer(modelDir);
            sw.Stop();
            long loadMs = sw.ElapsedMilliseconds;

            var samples = DictationRecognizer.Pcm16ToFloat(pcm);
            sw.Restart();
            var text = recognizer.Transcribe(samples);
            sw.Stop();
            long transcribeMs = sw.ElapsedMilliseconds;

            Console.WriteLine($"识别结果：{text}");
            Console.WriteLine($"加载 {loadMs} ms，识别 {transcribeMs} ms");
            return 0;
        }
        finally
        {
            if (File.Exists(tmpWav))
            {
                try
                {
                    File.Delete(tmpWav);
                }
                catch
                {
                }
            }
        }
    }
}

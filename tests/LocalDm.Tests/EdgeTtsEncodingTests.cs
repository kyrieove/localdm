using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Tests;

[CollectionDefinition("Speech console encoding", DisableParallelization = true)]
public sealed class SpeechConsoleEncodingCollection;

[Collection("Speech console encoding")]
public sealed class EdgeTtsEncodingTests
{
    [Fact]
    public async Task Chinese_speech_reaches_the_child_as_utf8_without_a_bom()
    {
        var original = Console.InputEncoding;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.InputEncoding = Encoding.GetEncoding(936);
            await AssertChineseBytesAsync();
        }
        finally
        {
            Console.InputEncoding = original;
        }
    }

    private static async Task AssertChineseBytesAsync()
    {
        const string text = "各位，《孽岛疑云》的故事背景发生在民国三年八月十五日。";
        var factory = typeof(EdgeTtsEngine).GetMethod(
            "CreateSpeechStartInfo", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(factory);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = Assert.IsType<ProcessStartInfo>(factory.Invoke(null,
            [powershell, "unused-script", "zh-CN-YunxiNeural"]));

        // 子进程只回传实际收到的原始字节，不合成语音，也不依赖 Python 或网络。
        const string readBytes = "$ProgressPreference = 'SilentlyContinue'; $m = [IO.MemoryStream]::new(); "
            + "[Console]::OpenStandardInput().CopyTo($m); "
            + "[Console]::Write([Convert]::ToBase64String($m.ToArray()))";
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(readBytes)));

        using var child = Process.Start(startInfo);
        Assert.NotNull(child);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var killOnTimeout = timeout.Token.Register(() =>
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        });
        var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = child.StandardError.ReadToEndAsync(timeout.Token);
        await child.StandardInput.WriteAsync(text.AsMemory(), timeout.Token);
        child.StandardInput.Close();
        await child.WaitForExitAsync(timeout.Token);

        Assert.Equal(string.Empty, await error);
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), (await output).Trim());
    }
}

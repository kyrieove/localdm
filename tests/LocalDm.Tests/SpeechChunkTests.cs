using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Tests;

public sealed class SpeechChunkTests
{
    private sealed class RecordingEdge : IEdgeTtsEngine
    {
        public List<string> Texts { get; } = new();

        public bool IsAvailable() => true;

        public Task<bool> TrySpeakAsync(string text, string voice, CancellationToken cancellationToken)
        {
            Texts.Add(text);
            return Task.FromResult(true);
        }

        public void Stop() { }
    }

    [Fact]
    public void Short_text_is_one_chunk()
    {
        var chunks = SpeechChunks.Split("  你好。\n大家好。 ");
        Assert.Equal(new[] { "你好。\n大家好。" }, chunks);
    }

    [Fact]
    public void Long_text_is_split_without_losing_text_and_every_chunk_fits()
    {
        var paragraph = string.Concat(Enumerable.Repeat("这是一句用来测试的长句子。", 8));
        var original = string.Join("\n", Enumerable.Repeat(paragraph, 30)) + "\n" + new string('字', 1200);

        var chunks = SpeechChunks.Split(original);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.True(chunk.Length > 0 && chunk.Length <= SpeechChunks.MaxChars);
        });
        Assert.Equal(original.Replace("\n", ""), string.Concat(chunks).Replace("\n", ""));
    }

    [Fact]
    public async Task Each_chunk_goes_to_edge_in_order()
    {
        var paragraph = string.Concat(Enumerable.Repeat("这是一句用来测试的长句子。", 8));
        var longText = string.Join("\n", Enumerable.Repeat(paragraph, 30)) + "\n" + new string('字', 1200);

        var edge = new RecordingEdge();
        var item = new SpeechItem
        {
            SpeechId = "sp-chunk",
            RequestId = "req-1",
            GenerationId = "gen-1",
            SafeText = longText
        };

        using var tts = new TtsService(edge);
        await tts.SpeakAsync(item, "zh-CN-YunxiNeural", CancellationToken.None);

        Assert.Equal(SpeechChunks.Split(item.SafeText), edge.Texts);
    }
}

# chunked-speech — 长文分段朗读：某段失败只从这段接着读，不从头

## Goal
- `TtsService.SpeakAsync` / `ReplayAsync` 把 `SafeText` 切成每段 ≤ 500 字的若干段，按顺序逐段朗读；每段单独调用 Edge，所以超时按段计算。
- Edge 在第 k 段失败时：第 k 段起剩下的段都用本地 SAPI 读（不再切回 Edge），前面已读完的段不重读。
- 500 字以内的文本只有一段，行为和现在一样。
- 停止（`Stop`）后不再读后面的段。

## Risk
medium

## Parameters
- 每段最大字数 `MaxChars = 500`。
- 段落分隔：`'\n'`；每段 `Trim()`，空段丢弃。
- 句末切分点：`。！？!?；;` 之后（标点留在前一句末尾），正则 `(?<=[。！？!?；;])`。
- 合并时段与段之间用 `"\n"` 连接。

## Files
may change: `src/LocalDm.Infrastructure/Voice/TtsService.cs`, `src/LocalDm.Infrastructure/Voice/SpeechChunks.cs`, `tests/LocalDm.Tests/SpeechChunkTests.cs` / read-only: `src/LocalDm.Infrastructure/Voice/EdgeTtsEngine.cs`, `src/LocalDm.Infrastructure/Voice/IEdgeTtsEngine.cs`, `tests/LocalDm.Tests/EdgeTtsTests.cs`

## Steps (N = 3)
1. 新建 `src/LocalDm.Infrastructure/Voice/SpeechChunks.cs`（命名空间 `LocalDm.Infrastructure.Voice`）：
   ```csharp
   using System.Text.RegularExpressions;

   namespace LocalDm.Infrastructure.Voice;

   /// <summary>
   /// 把长文切成朗读段：Edge 每段单独合成，超时按段算；某段失败时从这段接着读，不从头。
   /// </summary>
   public static class SpeechChunks
   {
       public const int MaxChars = 500;

       private static readonly Regex SentenceEnd = new("(?<=[。！？!?；;])", RegexOptions.Compiled);

       public static IReadOnlyList<string> Split(string text, int maxChars = MaxChars)
       {
           // 先拆成不超过 maxChars 的单元：段落；太长的段落按句末；还太长的句子硬切。
           var units = new List<string>();
           foreach (var paragraph in text.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0))
           {
               if (paragraph.Length <= maxChars)
               {
                   units.Add(paragraph);
                   continue;
               }

               foreach (var sentence in SentenceEnd.Split(paragraph).Where(s => s.Length > 0))
               {
                   for (var start = 0; start < sentence.Length; start += maxChars)
                   {
                       units.Add(sentence.Substring(start, Math.Min(maxChars, sentence.Length - start)));
                   }
               }
           }

           // 再把相邻单元合并到不超过 maxChars，减少 Edge 进程启动次数。
           var chunks = new List<string>();
           var current = "";
           foreach (var unit in units)
           {
               if (current.Length == 0)
               {
                   current = unit;
               }
               else if (current.Length + 1 + unit.Length <= maxChars)
               {
                   current += "\n" + unit;
               }
               else
               {
                   chunks.Add(current);
                   current = unit;
               }
           }

           if (current.Length > 0)
           {
               chunks.Add(current);
           }

           return chunks;
       }
   }
   ```
2. `src/LocalDm.Infrastructure/Voice/TtsService.cs` → `PlayAsync`（第 91–201 行）：
   - 把第 150–183 行本地 SAPI 播放（`ApplyVoice(voiceId)` 到 `_synth.SpeakCompleted -= OnCompleted;` 的 finally 结束）原样挪进一个新的私有方法 `private async Task SpeakLocalAsync(string text, string? voiceId, CancellationToken token)`，其中 `item.SafeText` 换成 `text`，`linked.Token` 换成 `token`。逻辑、错误信息、注释都不改。
   - 第 110–185 行的 `try { … }` 块内容改成逐段循环（`catch (OperationCanceledException)` 和外层 `finally` 不动）：
     ```csharp
     var targetEdgeVoice = …;   // 第 113–115 行原样保留
     var edgeAllowed = (voiceId is null || voiceId.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase))
         && _edge.IsAvailable();

     foreach (var chunk in SpeechChunks.Split(item.SafeText))
     {
         linked.Token.ThrowIfCancellationRequested();

         var usedEdge = false;
         if (edgeAllowed)
         {
             // 第 121–140 行原来的 try/catch/finally 原样保留，只把 item.SafeText 换成 chunk
         }

         if (!usedEdge)
         {
             // 一段失败后剩下的都用本地语音：来回换声更难听，也省得每段再等一次超时。
             edgeAllowed = false;
             // 第 145–148 行原来的注释和 linked.Token.ThrowIfCancellationRequested(); 原样保留
             await SpeakLocalAsync(chunk, voiceId, linked.Token);
         }
     }
     ```
   - 原来第 112 行那句「如果未特别锁定本地声音，优先尝试 Edge-TTS……」注释保留在 `targetEdgeVoice` 上方。
3. 新建 `tests/LocalDm.Tests/SpeechChunkTests.cs`（命名空间 `LocalDm.Tests`，`using LocalDm.Core.Domain; using LocalDm.Infrastructure.Voice;`；看 `EdgeTtsTests.cs` 顶部的 using 照抄需要的）：
   - `Short_text_is_one_chunk`：`SpeechChunks.Split("  你好。\n大家好。 ")` 等于 `["你好。\n大家好。"]`。
   - `Long_text_is_split_without_losing_text_and_every_chunk_fits`：构造文本 = 30 个段落，每段是 `string.Concat(Enumerable.Repeat("这是一句用来测试的长句子。", 8))`（每段 104 字），段间 `"\n"`；再追加一个 1200 字无标点的段落 `new string('字', 1200)`。断言：`chunks.Count > 1`；每段 `0 < Length <= SpeechChunks.MaxChars`；`string.Concat(chunks).Replace("\n", "")` 等于原文 `.Replace("\n", "")`。
   - `Each_chunk_goes_to_edge_in_order`：在测试类里写一个私有假引擎 `RecordingEdge : IEdgeTtsEngine`（`IsAvailable() => true`，`TrySpeakAsync` 把 `text` 加进 `List<string> Texts` 并返回 `Task.FromResult(true)`，`Stop()` 空实现）；用上一条的长文本建 `SpeechItem { SpeechId = "sp-chunk", RequestId = "req-1", GenerationId = "gen-1", SafeText = <长文本> }`，`using var tts = new TtsService(edge); await tts.SpeakAsync(item, "zh-CN-YunxiNeural", CancellationToken.None);`，断言 `edge.Texts` 与 `SpeechChunks.Split(item.SafeText)` 逐项相等。这条假引擎每段都成功，不会走到本地语音，不会出声，不要调用 `SkipBecauseItActuallySpeaks`。

## Check
`dotnet test tests/LocalDm.Tests --filter "FullyQualifiedName~SpeechChunkTests|FullyQualifiedName~EdgeTtsTests|FullyQualifiedName~TtsServiceTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- Reuse existing functions; don't copy existing logic.
- No rm, no git push, no git checkout . / restore . / reset --hard.
- 不改 `EdgeTtsEngine.cs` 的超时公式，不改 `ApplyVoice`、`Stop`、`Dispose`。
- 不删、不跳过已有测试。

## Acceptance
- verdict: accepted
- files: src/LocalDm.Infrastructure/Voice/SpeechChunks.cs, src/LocalDm.Infrastructure/Voice/TtsService.cs, tests/LocalDm.Tests/SpeechChunkTests.cs
- check: `dotnet test tests/LocalDm.Tests --filter "FullyQualifiedName~SpeechChunkTests|FullyQualifiedName~EdgeTtsTests|FullyQualifiedName~TtsServiceTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec antigravity/gemini-3.8-flash: fresh 143k, cached 269k, out 15k  (agy-chunked-speech.jsonl) · review gpt-6.1-sol: fresh 20k, cached 25k, out 1k  (codex-review-chunked-speech.jsonl)
- review findings: 0 real / 0 false positive / 0 missed (medium, gpt-6.1-sol)

using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Tablet;
using LocalDm.Tablet;

namespace LocalDm.Infrastructure.Probe;

public static class PlaytestProbe
{
    public sealed record PlayerMove(string To, string Text, bool Ready, string? Vote, bool Confused);

    public static PlayerMove ParseMove(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PlayerMove("pass", string.Empty, false, null, false);
        }

        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var json = trimmed.Substring(start, end - start + 1);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? rawTo = null;
                if (root.TryGetProperty("to", out var toProp) && toProp.ValueKind == JsonValueKind.String)
                {
                    rawTo = toProp.GetString();
                }

                var to = rawTo switch
                {
                    "dm" => "dm",
                    "pass" => "pass",
                    _ => "table",
                };

                string text = trimmed;
                if (root.TryGetProperty("text", out var textProp))
                {
                    if (textProp.ValueKind == JsonValueKind.String)
                    {
                        text = textProp.GetString() ?? string.Empty;
                    }
                    else
                    {
                        text = raw;
                    }
                }

                var ready = root.TryGetProperty("ready", out var readyProp) && readyProp.ValueKind == JsonValueKind.True;

                string? vote = null;
                if (root.TryGetProperty("vote", out var voteProp) && voteProp.ValueKind == JsonValueKind.String)
                {
                    vote = voteProp.GetString();
                }

                var confused = root.TryGetProperty("confused", out var confusedProp) && confusedProp.ValueKind == JsonValueKind.True;

                return new PlayerMove(to, text, ready, vote, confused);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // 解析失败时降级返回 table
            }
        }

        return new PlayerMove("table", trimmed, false, null, false);
    }

    public static string TallyVotes(IEnumerable<string?> votes, IReadOnlyList<string> roleIds)
    {
        if (roleIds.Count == 0)
        {
            return string.Empty;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in roleIds)
        {
            counts[r] = 0;
        }

        foreach (var v in votes)
        {
            if (v is not null && counts.ContainsKey(v))
            {
                counts[v]++;
            }
        }

        var parts = roleIds
            .Where(r => counts[r] > 0)
            .Select(r => $"{r}={counts[r]}")
            .ToList();

        if (parts.Count == 0)
        {
            return $"{roleIds[0]}=1";
        }

        return string.Join(", ", parts);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var repoRoot = DmAcceptanceProbe.FindRepoRoot();
        var defaultData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData
            ? Path.Combine(localAppData, "LocalDm")
            : Path.Combine(repoRoot, ".appdata", "localdm-data");
        var dataRoot = DmAcceptanceProbe.ArgumentValue(args, "--data") ?? defaultData;
        var settingsPath = DmAcceptanceProbe.ArgumentValue(args, "--settings") ?? Path.Combine(dataRoot, "settings.json");
        var modelOverride = DmAcceptanceProbe.ArgumentValue(args, "--model");
        var playerModel = DmAcceptanceProbe.ArgumentValue(args, "--player-model") ?? "deepseek-v4.1-flash";

        var roundsArg = DmAcceptanceProbe.ArgumentValue(args, "--rounds");
        var rounds = int.TryParse(roundsArg, out var rVal) ? rVal : 2;

        var maxStepsArg = DmAcceptanceProbe.ArgumentValue(args, "--max-steps");
        var maxSteps = int.TryParse(maxStepsArg, out var mVal) ? mVal : 40;

        var outRoot = DmAcceptanceProbe.ArgumentValue(args, "--out") ?? Path.Combine(repoRoot, ".local", "playtest");

        var runDir = DmAcceptanceProbe.NewRunDirectory(outRoot);
        var transcriptPath = Path.Combine(runDir, "transcript.md");
        var metricsPath = Path.Combine(runDir, "metrics.json");
        var playersDir = Path.Combine(runDir, "players");
        Directory.CreateDirectory(playersDir);

        Console.WriteLine($"[Playtest] 运行目录：{runDir}");

        var packagePath = DmAcceptanceProbe.LatestPackage(dataRoot);
        if (packagePath is null)
        {
            Console.WriteLine("没有找到任何 pkg-*.json 剧本包，停止。");
            return 2;
        }

        // 和软件开平板时一样：包里没带平板清单就按旁边的 .tablet.json 补齐（已备好则直接读）。
        var package = LocalDm.Infrastructure.Import.TabletMaterials.Ensure(DmAcceptanceProbe.LoadPackage(packagePath), packagePath);
        if (package.Tablet is null)
        {
            Console.WriteLine("! 剧本包没有平板清单，停止。");
            return 3;
        }

        var assetRoot = TabletAssetRoot.Resolve(packagePath, package.Tablet);
        var tabletContent = new PackageTabletContent(package, assetRoot);
        var roleBooks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in package.Roles)
        {
            if (!tabletContent.RoleBooks.TryGetValue(role.RoleId, out var bookText))
            {
                Console.WriteLine($"! 角色本文字不足：{role.RoleId} 0");
                return 3;
            }

            var text = RoleBookPlainText.Build(bookText);
            if (text.Length < 200)
            {
                Console.WriteLine($"! 角色本文字不足：{role.RoleId} {text.Length}");
                return 3;
            }

            roleBooks[role.RoleId] = text;
        }

        var workDb = DmAcceptanceProbe.CopyDatabase(dataRoot, runDir);
        using var host = DmAcceptanceProbe.NewHost(settingsPath, dataRoot, workDb, modelOverride);
        await host.LoadPackageAsync(package);

        var transcript = new StringBuilder();
        transcript.AppendLine("# Playtest 对局记录");
        transcript.AppendLine($"- 剧本：{package.Title}");
        transcript.AppendLine($"- DM 模型：{modelOverride ?? "默认设置"}");
        transcript.AppendLine($"- 玩家模型：{playerModel}");
        transcript.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        transcript.AppendLine();

        var publicLogs = new List<string>();
        var seen = host.Messages.Count;
        await DmAcceptanceProbe.StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Start, null, null));
        seen = AppendNewHostMessages(host, seen, publicLogs, transcript);
        await File.WriteAllTextAsync(transcriptPath, transcript.ToString(), Encoding.UTF8);

        var swTotal = Stopwatch.StartNew();

        var dmLatencyMs = new List<double>();
        var tableTurns = 0;
        var passTurns = 0;
        var dmTurns = 0;
        var playerErrors = 0;
        var confusedCount = 0;
        var stuck = new List<string>();
        var phasesVisited = new List<string>();
        var revealed = false;

        var lastMoves = new Dictionary<string, PlayerMove>(StringComparer.Ordinal);
        var roleIds = package.Roles.Select(r => r.RoleId).ToList();

        int step = 0;
        while (step < maxSteps && host.Session.SessionStateText != "Completed")
        {
            var phaseTitle = host.Session.PhaseTitle;
            phasesVisited.Add(phaseTitle);

            for (var r = 1; r <= rounds; r++)
            {
                foreach (var role in package.Roles)
                {
                    var roleId = role.RoleId;
                    var roleDir = Path.Combine(playersDir, roleId);
                    Directory.CreateDirectory(roleDir);

                    var releasedMaterialsText = GetReleasedMaterialsText(package, VisibleMaterialIds(package, host));
                    var recentPublicLog = string.Join("\n", publicLogs.TakeLast(60));

                    var prompt = BuildPlayerPrompt(
                        roleId,
                        roleIds,
                        phaseTitle,
                        host.Session.Hint,
                        roleBooks[roleId],
                        releasedMaterialsText,
                        recentPublicLog);

                    var (rawReply, ok) = await InvokePlayerProcessAsync(prompt, playerModel, roleDir);
                    if (!ok)
                    {
                        playerErrors++;
                    }

                    var move = ParseMove(rawReply);
                    lastMoves[roleId] = move;
                    if (move.Confused)
                    {
                        confusedCount++;
                    }

                    var playerLogPath = Path.Combine(playersDir, $"{roleId}.log");
                    var logEntry = $"=== PROMPT ===\n{prompt}\n=== RAW REPLY ===\n{rawReply}\n=== PARSED ===\nTo={move.To}, Ready={move.Ready}, Vote={move.Vote}, Confused={move.Confused}\nText={move.Text}\n\n";
                    await File.AppendAllTextAsync(playerLogPath, logEntry, Encoding.UTF8);

                    var preview = move.Text.Length > 40 ? move.Text[..40] : move.Text;
                    Console.WriteLine($"[{phaseTitle} r{r} {roleId}] {move.To} {preview}");

                    transcript.AppendLine($"### {phaseTitle} · 第 {r} 轮 · {roleId}");
                    transcript.AppendLine($"- **To**: {move.To} | **Ready**: {move.Ready} | **Vote**: {move.Vote ?? "null"} | **Confused**: {move.Confused}");
                    transcript.AppendLine($"- **Text**: {move.Text}");

                    if (move.To == "dm")
                    {
                        dmTurns++;
                        var beforeCount = host.Messages.Count;
                        var dmSw = Stopwatch.StartNew();
                        host.InputText = $"{roleId}：{move.Text}";
                        var busyAtSend = host.IsBusy;
                        await host.SendAsync();
                        await DmAcceptanceProbe.WaitIdleAsync(host);
                        dmSw.Stop();
                        dmLatencyMs.Add(dmSw.ElapsedMilliseconds);

                        var reply = ReplyFor(host, beforeCount, busyAtSend);
                        publicLogs.Add($"{roleId}→DM：{move.Text}");
                        publicLogs.Add($"DM：{reply}");

                        transcript.AppendLine($"- **DM 回复**: {reply}");
                    }
                    else if (move.To == "table")
                    {
                        tableTurns++;
                        publicLogs.Add($"{roleId}（对大家）：{move.Text}");
                    }
                    else
                    {
                        passTurns++;
                    }

                    transcript.AppendLine();
                    await File.WriteAllTextAsync(transcriptPath, transcript.ToString(), Encoding.UTF8);
                }

                if (package.Roles.All(r => lastMoves.TryGetValue(r.RoleId, out var m) && m.Ready))
                {
                    break;
                }
            }

            // 操作员动作
            foreach (var material in host.Session.PendingMaterials.ToArray())
            {
                seen = host.Messages.Count;
                await DmAcceptanceProbe.StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.ReleaseMaterials, [material], null));
                seen = AppendNewHostMessages(host, seen, publicLogs, transcript);
            }

            if (host.Session.IsVotePhase && !host.Session.HasVoteSummary)
            {
                var tally = TallyVotes(package.Roles.Select(r => lastMoves.TryGetValue(r.RoleId, out var m) ? m.Vote : null), roleIds);
                if (SessionViewModel.TryParseVotes(tally, out var voteSummary, out _))
                {
                    seen = host.Messages.Count;
                    await DmAcceptanceProbe.StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.SubmitVoteSummary, null, voteSummary));
                    seen = AppendNewHostMessages(host, seen, publicLogs, transcript);
                }
            }

            var allowed = host.Session.Projection?.CurrentPhase.AllowedActions ?? [];
            var revealedTexts = host.Session.Projection?.RevealedTexts ?? [];
            if (allowed.Contains(PublicAction.Reveal) && revealedTexts.Count == 0)
            {
                seen = host.Messages.Count;
                await DmAcceptanceProbe.StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Reveal, null, null));
                seen = AppendNewHostMessages(host, seen, publicLogs, transcript);
                revealed = true;

                // 让四个玩家各说一次揭秘后的感想
                foreach (var role in package.Roles)
                {
                    var roleId = role.RoleId;
                    var roleDir = Path.Combine(playersDir, roleId);
                    var releasedMaterialsText = GetReleasedMaterialsText(package, VisibleMaterialIds(package, host));
                    var recentPublicLog = string.Join("\n", publicLogs.TakeLast(60));

                    var prompt = BuildPlayerPrompt(
                        roleId,
                        roleIds,
                        host.Session.PhaseTitle,
                        host.Session.Hint,
                        roleBooks[roleId],
                        releasedMaterialsText,
                        recentPublicLog);

                    var (rawReply, ok) = await InvokePlayerProcessAsync(prompt, playerModel, roleDir);
                    if (!ok)
                    {
                        playerErrors++;
                    }

                    var move = ParseMove(rawReply);
                    lastMoves[roleId] = move;
                    if (move.Confused)
                    {
                        confusedCount++;
                    }

                    var playerLogPath = Path.Combine(playersDir, $"{roleId}.log");
                    var logEntry = $"=== PROMPT (REVEAL) ===\n{prompt}\n=== RAW REPLY ===\n{rawReply}\n=== PARSED ===\nTo={move.To}, Ready={move.Ready}, Vote={move.Vote}, Confused={move.Confused}\nText={move.Text}\n\n";
                    await File.AppendAllTextAsync(playerLogPath, logEntry, Encoding.UTF8);

                    var preview = move.Text.Length > 40 ? move.Text[..40] : move.Text;
                    Console.WriteLine($"[{host.Session.PhaseTitle} 揭秘感言 {roleId}] {move.To} {preview}");

                    transcript.AppendLine($"### {host.Session.PhaseTitle} · 揭秘感言 · {roleId}");
                    transcript.AppendLine($"- **To**: {move.To} | **Ready**: {move.Ready} | **Vote**: {move.Vote ?? "null"} | **Confused**: {move.Confused}");
                    transcript.AppendLine($"- **Text**: {move.Text}");

                    if (move.To == "dm")
                    {
                        dmTurns++;
                        var beforeCount = host.Messages.Count;
                        var dmSw = Stopwatch.StartNew();
                        host.InputText = $"{roleId}：{move.Text}";
                        var busyAtSend = host.IsBusy;
                        await host.SendAsync();
                        await DmAcceptanceProbe.WaitIdleAsync(host);
                        dmSw.Stop();
                        dmLatencyMs.Add(dmSw.ElapsedMilliseconds);

                        var reply = ReplyFor(host, beforeCount, busyAtSend);
                        publicLogs.Add($"{roleId}→DM：{move.Text}");
                        publicLogs.Add($"DM：{reply}");

                        transcript.AppendLine($"- **DM 回复**: {reply}");
                    }
                    else if (move.To == "table")
                    {
                        tableTurns++;
                        publicLogs.Add($"{roleId}（对大家）：{move.Text}");
                    }
                    else
                    {
                        passTurns++;
                    }

                    transcript.AppendLine();
                    await File.WriteAllTextAsync(transcriptPath, transcript.ToString(), Encoding.UTF8);
                }

                step++;
                continue;
            }

            if (host.Session.CanAdvance)
            {
                seen = host.Messages.Count;
                await DmAcceptanceProbe.StepAsync(host, () => host.Session.ExecuteAsync(GameCommandType.Advance, null, null));
                seen = AppendNewHostMessages(host, seen, publicLogs, transcript);
            }
            else if (host.Session.SessionStateText == "Revealed")
            {
                break; // 揭秘后剧本已结束：不能再推进不算卡住
            }
            else
            {
                stuck.Add($"{phaseTitle}: {host.Session.Status}");
                break;
            }

            step++;
        }

        swTotal.Stop();

        var cardLeaks = DmAcceptanceProbe.CardLeaks(host.Messages, package);
        var finalState = host.Session.SessionStateText;

        dmLatencyMs.Sort();
        double p50 = 0;
        double maxLatency = 0;
        if (dmLatencyMs.Count > 0)
        {
            p50 = dmLatencyMs[dmLatencyMs.Count / 2];
            maxLatency = dmLatencyMs[^1];
        }

        var metricsObj = new
        {
            dmTurns,
            dmLatencyMs,
            dmLatencyP50 = p50,
            dmLatencyMax = maxLatency,
            tableTurns,
            passTurns,
            playerErrors,
            confusedCount,
            stuck,
            cardLeaks,
            phasesVisited,
            revealed,
            finalState,
            minutes = swTotal.Elapsed.TotalMinutes,
        };

        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var metricsJson = JsonSerializer.Serialize(metricsObj, serializerOptions);
        await File.WriteAllTextAsync(metricsPath, metricsJson, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine("========== Playtest 运行完成 ==========");
        Console.WriteLine($"运行目录：{runDir}");
        Console.WriteLine($"最终状态：{finalState} | 揭秘：{revealed} | 总耗时：{swTotal.Elapsed.TotalMinutes:F2} 分钟");
        Console.WriteLine($"DM 轮次：{dmTurns} (P50 {p50:F0}ms, Max {maxLatency:F0}ms) | 桌边轮次：{tableTurns} | 跳过：{passTurns}");
        Console.WriteLine($"卡面泄露：{cardLeaks.Count} | 困惑数：{confusedCount} | 玩家报错：{playerErrors} | 阻塞数：{stuck.Count}");

        return finalState == "Completed" || revealed ? 0 : 1;
    }

    /// <summary>
    /// 玩家能看到的线索组：软件里已发放的，加上 DM 说话里点到名字的那组。
    /// 平板上全部线索卡都在列表里，玩家按 DM 的指引去翻；探针用「DM 点名」模拟这一步。
    /// shortcut: 按组编号做子串匹配，DM 提前顺口提到某组也会放出，要更准就让玩家显式翻卡。
    /// </summary>
    private static IReadOnlyList<string> VisibleMaterialIds(ScriptPackage package, HostViewModel host)
    {
        var released = host.Session.Projection?.ReleasedMaterialIds ?? [];
        var said = string.Join('\n', host.Messages.Where(m => m.IsModelReply).Select(m => m.Text));
        return package.Materials
            .Where(m => released.Contains(m.MaterialId) || said.Contains(m.MaterialId, StringComparison.Ordinal))
            .Select(m => m.MaterialId)
            .ToArray();
    }

    /// <summary>
    /// 这一问的 DM 回复。没有模型回复时写清是哪种：发送时主持台在忙（没发出去），
    /// 还是软件上屏了一条非模型提示（多半是模型报错或返回空内容）。
    /// </summary>
    private static string ReplyFor(HostViewModel host, int beforeCount, bool busyAtSend)
    {
        var added = host.Messages.Skip(beforeCount).ToArray();
        if (added.FirstOrDefault(m => m.IsModelReply) is { } model)
        {
            return model.Text;
        }

        if (busyAtSend)
        {
            return "（无回复：发送时主持台在忙，没发出去）";
        }

        var local = added.LastOrDefault(m => m.Author == ChatAuthor.Host);
        return local is null ? "（无回复：没有任何上屏）" : $"（无回复：软件提示「{local.Text}」）";
    }

    private static string GetReleasedMaterialsText(ScriptPackage package, IReadOnlyList<string>? releasedIds)
    {
        if (releasedIds is null || releasedIds.Count == 0)
        {
            return "（暂无发放材料）";
        }

        var releasedSet = releasedIds.ToHashSet(StringComparer.Ordinal);
        var spans = package.Materials
            .Where(m => releasedSet.Contains(m.MaterialId))
            .SelectMany(m => m.SourceSpanIds)
            .ToHashSet(StringComparer.Ordinal);

        var texts = package.Spans
            .Where(s => spans.Contains(s.SpanId) && !s.SpanId.Contains("卡背", StringComparison.Ordinal))
            .Select(s => s.OriginalText.Replace(DmAcceptanceProbe.CopyrightLine, string.Empty, StringComparison.Ordinal).Trim())
            .Where(t => t.Length > 0);

        return string.Join("\n\n", texts);
    }

    private static int AppendNewHostMessages(HostViewModel host, int seen, List<string> publicLog, StringBuilder transcript)
    {
        var newReplies = host.Messages
            .Skip(seen)
            .Where(m => m.IsModelReply)
            .ToList();

        if (newReplies.Count > 0)
        {
            transcript.AppendLine($"### 操作员 · {host.Session.PhaseTitle}");
            foreach (var m in newReplies)
            {
                publicLog.Add($"DM：{m.Text}");
                transcript.AppendLine($"- **DM 播报**: {m.Text}");
            }
            transcript.AppendLine();
        }

        return host.Messages.Count;
    }

    private static string BuildPlayerPrompt(
        string roleId,
        IReadOnlyList<string> roleIds,
        string phaseTitle,
        string hint,
        string roleBook,
        string releasedMaterials,
        string recentPublicLog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是剧本杀真人玩家，扮演【{roleId}】。");
        sb.AppendLine("规则：只能根据自己的角色本和公开信息行动；想问主持人就 to=dm，想和其他玩家讨论就 to=table，跳过就 to=pass；本环节该做的都做完了就设 ready=true；投票环节在 vote 里填一个角色编号；如果上一次 DM 的回答没帮到自己或答非所问，设 confused=true。");
        sb.AppendLine("注意：只输出一个 JSON，不要任何多余标记或解释，格式如下：");
        sb.AppendLine("{\"to\":\"dm\"|\"table\"|\"pass\",\"text\":\"…\",\"ready\":true|false,\"vote\":\"<roleId>\"|null,\"confused\":true|false}");
        sb.AppendLine();
        sb.AppendLine($"角色列表：{string.Join(", ", roleIds)}");
        sb.AppendLine($"当前环节：{phaseTitle}");
        sb.AppendLine($"流程提示：{hint}");
        sb.AppendLine();
        sb.AppendLine("=== 你的角色本全文 ===");
        sb.AppendLine(roleBook);
        sb.AppendLine();
        sb.AppendLine("=== 已发放材料正文 ===");
        sb.AppendLine(releasedMaterials);
        sb.AppendLine();
        sb.AppendLine("=== 公共记录（最近 60 行） ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(recentPublicLog) ? "（暂无）" : recentPublicLog);
        sb.AppendLine();
        sb.AppendLine("请输出你的行动 JSON：");
        return sb.ToString();
    }

    private static async Task<(string Reply, bool Ok)> InvokePlayerProcessAsync(string prompt, string model, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                // Windows 上 codebuddy 是 npm 的 .cmd 外壳；提示含整本角色本，会超出命令行长度，走标准输入
                FileName = OperatingSystem.IsWindows() ? "codebuddy.cmd" : "codebuddy",
                Arguments = $"-p --model {model} --effort high -y --output-format stream-json",
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                await process.WaitForExitAsync();

                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch
                {
                }

                return ("{\"to\":\"pass\"}", false);
            }

            var stdout = await stdoutTask;

            if (process.ExitCode != 0)
            {
                return ("{\"to\":\"pass\"}", false);
            }

            var outputLines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = outputLines.Length - 1; i >= 0; i--)
            {
                var line = outputLines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "result")
                    {
                        if (doc.RootElement.TryGetProperty("result", out var resProp))
                        {
                            return (resProp.GetString() ?? "{\"to\":\"pass\"}", true);
                        }
                    }
                }
                catch
                {
                    // Ignore non-json lines
                }
            }

            return ("{\"to\":\"pass\"}", false);
        }
        catch
        {
            return ("{\"to\":\"pass\"}", false);
        }
    }
}

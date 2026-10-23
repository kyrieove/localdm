using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Tablet;

namespace LocalDm.Tests;

public sealed class TabletUiProjectionTests
{
    private static readonly ScriptPackage Package = SyntheticPackage.Create();

    private static PublicFeedSnapshot Feed(string sessionId = "session-1") => new()
    {
        SessionId = sessionId,
        Messages =
        [
            new PublicMessage { SessionId = sessionId, MessageId = "m1", Author = PublicMessageAuthor.Player, Text = "【甲】大家好", CreatedAt = DateTimeOffset.UnixEpoch, Kind = PublicMessageKind.PlayerSpeech, Sequence = 1 },
            new PublicMessage { SessionId = sessionId, MessageId = "m2", Author = PublicMessageAuthor.Host, Text = "欢迎各位", CreatedAt = DateTimeOffset.UnixEpoch, Kind = PublicMessageKind.HostReply, Sequence = 2 },
        ],
        NextSequence = 2,
    };

    private static TabletUiProjection Proj(Session s, bool busy = false, bool speaking = false) => TabletUiProjector.Project(Package, s, Feed(s.SessionId), busy, speaking);
    private static TabletUiControl Ctl(TabletUiProjection p, TabletUiControlKind k) => p.Controls.Single(c => c.Kind == k);

    [Fact]
    public void Leak_guard_json_has_no_secret_markers_in_any_state()
    {
        var r = PhaseFlowTests.RevealPhase();
        var pausedAfterReveal = GameEngine.Apply(r, Package, SyntheticPackage.Command(GameCommandType.Pause, r.Revision, "op-pause"));

        var sessions = new (Session Session, bool IsUnlocked)[]
        {
            (SyntheticPackage.NewSession(), false),
            (PhaseFlowTests.Started(), false),
            (PhaseFlowTests.AdvancedTo(2), false),
            (PhaseFlowTests.AdvancedTo(3), false),
            (PhaseFlowTests.AdvancedTo(4), false),
            (PhaseFlowTests.AdvancedTo(5), false),
            (r, true),
            (pausedAfterReveal, true),
        };

        var jsonOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        foreach (var (session, isUnlocked) in sessions)
        {
            var projection = Proj(session);
            var json = JsonSerializer.Serialize(projection, jsonOptions);

            Assert.DoesNotContain(SyntheticPackage.RoleBookText, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SyntheticPackage.ClueCardText, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SyntheticPackage.HostOnlyText, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("原本条件", json, StringComparison.OrdinalIgnoreCase);

            foreach (var seg in Package.Segments)
            {
                Assert.DoesNotContain(seg.SegmentId, json, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("备忘", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("上下文占用", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("privateStory", json, StringComparison.OrdinalIgnoreCase);
            // 手册只经已认领会话的原稿页接口给（ADR 0009），投影里不出现它的资产或地址。
            Assert.DoesNotContain("handbook", json, StringComparison.OrdinalIgnoreCase);

            if (!isUnlocked)
            {
                Assert.DoesNotContain(SyntheticPackage.PublicTruth, json, StringComparison.OrdinalIgnoreCase);
                Assert.Null(projection.RevealedTexts);
            }
            else
            {
                var truthCount = json.Split(SyntheticPackage.PublicTruth).Length - 1;
                Assert.Equal(1, truthCount);
                Assert.NotNull(projection.RevealedTexts);
                Assert.Contains(SyntheticPackage.PublicTruth, projection.RevealedTexts);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var revealedProp = root.GetProperty("RevealedTexts");
                Assert.Equal(JsonValueKind.Array, revealedProp.ValueKind);
                Assert.Equal(1, revealedProp.GetArrayLength());
                Assert.Equal(SyntheticPackage.PublicTruth, revealedProp[0].GetString());

                var messagesJson = root.GetProperty("Messages").GetRawText();
                var phasesJson = root.GetProperty("Phases").GetRawText();
                var controlsJson = root.GetProperty("Controls").GetRawText();
                var hint = root.TryGetProperty("Hint", out var hp) && hp.ValueKind == JsonValueKind.String ? hp.GetString() : null;
                var phaseTitle = root.GetProperty("PhaseTitle").GetString();

                Assert.DoesNotContain(SyntheticPackage.PublicTruth, messagesJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(SyntheticPackage.PublicTruth, phasesJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(SyntheticPackage.PublicTruth, controlsJson, StringComparison.OrdinalIgnoreCase);
                if (hint is not null)
                {
                    Assert.DoesNotContain(SyntheticPackage.PublicTruth, hint, StringComparison.OrdinalIgnoreCase);
                }
                Assert.DoesNotContain(SyntheticPackage.PublicTruth, phaseTitle, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Projection_type_graph_has_no_secret_shaped_members()
    {
        var visited = new HashSet<Type>();
        var forbiddenTypes = new HashSet<Type>
        {
            typeof(ScriptPackage),
            typeof(Session),
            typeof(CanonicalSegment),
            typeof(TruthSection),
            typeof(RoleIndex),
            typeof(ScriptPhase),
        };

        var forbiddenWords = new[]
        {
            "truth",
            "memo",
            "context",
            "budget",
            "rolebook",
            "handbook",
            "manual",
            "segment",
            "secret",
            "condition",
        };

        void Traverse(Type type)
        {
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    Traverse(arg);
                }
            }

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null)
            {
                Traverse(underlying);
            }

            if (!visited.Add(type))
            {
                return;
            }

            Assert.DoesNotContain(type, forbiddenTypes);

            if (type.Namespace is null || !type.Namespace.StartsWith("LocalDm", StringComparison.Ordinal))
            {
                return;
            }

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var prop in properties)
            {
                foreach (var word in forbiddenWords)
                {
                    Assert.DoesNotContain(word, prop.Name, StringComparison.OrdinalIgnoreCase);
                }

                Traverse(prop.PropertyType);
            }
        }

        Traverse(typeof(TabletUiProjection));
    }

    [Fact]
    public void Controls_follow_session_state()
    {
        // Prepared
        var prep = Proj(SyntheticPackage.NewSession());
        var startCtl = Ctl(prep, TabletUiControlKind.Start);
        Assert.True(startCtl.Visible);
        Assert.Equal("开始这一局", startCtl.Label);
        Assert.All(prep.Controls.Where(c => c.Kind != TabletUiControlKind.Start), c => Assert.False(c.Visible));

        // Started()
        var started = Proj(PhaseFlowTests.Started());
        var advStarted = Ctl(started, TabletUiControlKind.Advance);
        Assert.True(advStarted.Visible);
        Assert.True(advStarted.Enabled);
        var pauseStarted = Ctl(started, TabletUiControlKind.PauseResume);
        Assert.True(pauseStarted.Visible);
        Assert.Equal("暂停本局", pauseStarted.Label);
        Assert.False(Ctl(started, TabletUiControlKind.Start).Visible);
        Assert.False(Ctl(started, TabletUiControlKind.ReleaseMaterials).Visible);
        Assert.False(Ctl(started, TabletUiControlKind.SubmitVote).Visible);
        Assert.False(Ctl(started, TabletUiControlKind.Reveal).Visible);
        Assert.False(Ctl(started, TabletUiControlKind.Complete).Visible);

        // AdvancedTo(2)
        var s2 = Proj(PhaseFlowTests.AdvancedTo(2));
        var rel2 = Ctl(s2, TabletUiControlKind.ReleaseMaterials);
        Assert.True(rel2.Visible);
        Assert.Equal("发放材料 M1", rel2.Label);
        Assert.Equal(2, s2.PendingMaterialCount);

        // AdvancedTo(4)
        var s4 = Proj(PhaseFlowTests.AdvancedTo(4));
        var vote4 = Ctl(s4, TabletUiControlKind.SubmitVote);
        Assert.True(vote4.Visible);
        Assert.False(vote4.Enabled);
        Assert.False(Ctl(s4, TabletUiControlKind.Advance).Visible);

        // AdvancedTo(5)
        var s5 = Proj(PhaseFlowTests.AdvancedTo(5));
        var rev5 = Ctl(s5, TabletUiControlKind.Reveal);
        Assert.True(rev5.Visible);
        Assert.Equal("公开真相", rev5.Label);
        Assert.False(Ctl(s5, TabletUiControlKind.Advance).Visible);

        // RevealPhase()
        var rev = PhaseFlowTests.RevealPhase();
        var sRev = Proj(rev);
        var compRev = Ctl(sRev, TabletUiControlKind.Complete);
        Assert.True(compRev.Visible);
        Assert.True(compRev.Enabled);
        Assert.True(Ctl(sRev, TabletUiControlKind.PauseResume).Visible);

        // 揭秘后暂停
        var paused = GameEngine.Apply(rev, Package, SyntheticPackage.Command(GameCommandType.Pause, rev.Revision, "op-pause"));
        var sPaused = Proj(paused);
        var pauseCtl = Ctl(sPaused, TabletUiControlKind.PauseResume);
        Assert.True(pauseCtl.Visible);
        Assert.Equal("继续本局", pauseCtl.Label);
        Assert.True(Ctl(sPaused, TabletUiControlKind.Complete).Visible);
    }

    [Fact]
    public void Busy_host_disables_but_does_not_hide_controls()
    {
        var projBusy = Proj(PhaseFlowTests.AdvancedTo(2), busy: true);
        var adv = Ctl(projBusy, TabletUiControlKind.Advance);
        var rel = Ctl(projBusy, TabletUiControlKind.ReleaseMaterials);
        var pause = Ctl(projBusy, TabletUiControlKind.PauseResume);

        Assert.True(adv.Visible);
        Assert.False(adv.Enabled);
        Assert.True(rel.Visible);
        Assert.False(rel.Enabled);
        Assert.True(pause.Visible);
        Assert.False(pause.Enabled);
        Assert.True(projBusy.HostBusy);

        var projSpeaking = Proj(PhaseFlowTests.AdvancedTo(2), speaking: true);
        Assert.True(projSpeaking.HostSpeaking);
    }

    [Fact]
    public void Phase_list_marks_done_current_upcoming()
    {
        var proj = Proj(PhaseFlowTests.AdvancedTo(3));
        Assert.Equal(5, proj.Phases.Count);
        Assert.Equal(TabletUiPhaseStatus.Done, proj.Phases[0].Status);
        Assert.Equal(TabletUiPhaseStatus.Done, proj.Phases[1].Status);
        Assert.Equal(TabletUiPhaseStatus.Current, proj.Phases[2].Status);
        Assert.Equal(TabletUiPhaseStatus.Upcoming, proj.Phases[3].Status);
        Assert.Equal(TabletUiPhaseStatus.Upcoming, proj.Phases[4].Status);
        Assert.Equal(3, proj.PhaseIndex);
        Assert.Equal(5, proj.PhaseCount);
        Assert.Equal("讨论", proj.PhaseTitle);
        Assert.Equal("讨论环节当前提示", proj.Hint);
    }

    [Fact]
    public void Public_messages_pass_through_unchanged()
    {
        var feed = Feed();
        var p = TabletUiProjector.Project(Package, PhaseFlowTests.Started(), feed, false, false);

        Assert.Same(feed.Messages, p.Messages);
        Assert.Equal(feed.NextSequence, p.NextSequence);
        Assert.Equal("请在电脑上操作", p.ControlsNote);
    }

    [Fact]
    public void Feed_from_another_session_is_rejected()
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            TabletUiProjector.Project(Package, PhaseFlowTests.Started(), Feed("other-session"), false, false));
        Assert.Equal(DomainErrorCode.INVALID_INPUT, ex.Code);
    }
}

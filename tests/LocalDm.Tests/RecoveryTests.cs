using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>T039 / T048：强退恢复、包指纹失配、恢复后 SpeechState=Idle。</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-rec-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptPackage _package = SyntheticPackage.Create();

    public RecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Committed_operations_survive_restart_with_same_revision()
    {
        var dbPath = Path.Combine(_root, "sessions.db");
        using (var store = new SessionStore(dbPath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.SaveSessionAsync(SyntheticPackage.NewSession(), TestContext.Current.CancellationToken);
            var dispatcher = new CommandDispatcher(store);
            await dispatcher.DispatchAsync(
                SyntheticPackage.Command(GameCommandType.Start, 0, "op-1", groupReady: true),
                _package,
                TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();

        // 重新打开 = 强退后重启
        using var reopened = new SessionStore(dbPath);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var recovery = new SessionRecovery(reopened);

        var result = await recovery.RecoverAsync(_package, "session-1", TestContext.Current.CancellationToken);

        Assert.True(result.Recovered);
        Assert.Equal(1, result.Session!.Revision);
        Assert.Equal(SessionState.Playing, result.Session.State);
        Assert.Equal(SpeechState.Idle, SessionRecovery.RestoredSpeechState);
    }

    [Fact]
    public async Task Package_fingerprint_mismatch_blocks_recovery()
    {
        var dbPath = Path.Combine(_root, "sessions.db");
        using (var store = new SessionStore(dbPath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            var foreign = SyntheticPackage.NewSession() with { ManifestHash = new string('d', 64) };
            await store.SaveSessionAsync(foreign, TestContext.Current.CancellationToken);
        }

        using var reopened = new SessionStore(dbPath);
        var result = await new SessionRecovery(reopened).RecoverAsync(
            _package,
            "session-1",
            TestContext.Current.CancellationToken);

        Assert.False(result.Recovered);
        Assert.Equal(DomainErrorCode.PACKAGE_MISMATCH, result.ErrorCode);
    }

    [Fact]
    public async Task Changed_source_file_blocks_recovery()
    {
        var sourcePath = Path.Combine(_root, "source.pdf");
        await File.WriteAllTextAsync(sourcePath, "原始内容", TestContext.Current.CancellationToken);
        var hash = ManifestFingerprint.ComputeSha256(sourcePath);

        var file = new SourceFile
        {
            FileId = "src",
            PrivatePath = sourcePath,
            ContentHash = hash,
            PageCount = 1,
            Origin = SourceOrder.PageOrder,
            ToolIdentity = "mineru 4.0.1",
            ParseState = FileParseState.Complete,
        };
        var manifestHash = ManifestFingerprint.Compute([file]);
        var package = _package with { SourceFiles = [file], ManifestHash = manifestHash };

        var dbPath = Path.Combine(_root, "sessions.db");
        using (var store = new SessionStore(dbPath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            var session = SyntheticPackage.NewSession() with { ManifestHash = manifestHash };
            await store.SaveSessionAsync(session, TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(sourcePath, "被改动的内容", TestContext.Current.CancellationToken);

        using var reopened = new SessionStore(dbPath);
        var result = await new SessionRecovery(reopened).RecoverAsync(package, "session-1", TestContext.Current.CancellationToken);

        Assert.False(result.Recovered);
        Assert.Equal(DomainErrorCode.PACKAGE_MISMATCH, result.ErrorCode);
        Assert.Contains("改动", result.PublicReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_session_is_reported_without_creating_state()
    {
        using var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var result = await new SessionRecovery(store).RecoverAsync(
            _package,
            "missing-session",
            TestContext.Current.CancellationToken);

        Assert.False(result.Recovered);
        Assert.Equal(DomainErrorCode.INVALID_INPUT, result.ErrorCode);
        Assert.Null(result.Session);
    }
}

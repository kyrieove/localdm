using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using Microsoft.Data.Sqlite;

namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 单文件 SQLite 存档。event、receipt 与 session projection 在同一事务提交；
/// 事务失败时三者均不改变。单进程串行处理写操作（同一 SemaphoreSlim）。
/// </summary>
public sealed class SessionStore : ISessionStore, IDisposable
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SessionStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new DomainRuleException(DomainErrorCode.STORAGE_FAILED, "存档路径不能为空。");
        }

        _databasePath = databasePath;
    }

    public static string DefaultDatabasePath(string dataRoot) =>
        Path.Combine(dataRoot, "sessions.db");

    /// <summary>可注入故障点，仅用于自动化验证「事务前后强退」的行为。</summary>
    public Action<TransactionFaultPoint>? FaultInjector { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = LoadSchema();
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        await wal.ExecuteNonQueryAsync(cancellationToken);

        _initialized = true;
    }

    public async Task<Session?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql + " WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task<OperationReceipt?> FindReceiptAsync(
        string sessionId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        return await FindReceiptAsync(connection, transaction: null, sessionId, operationId, cancellationToken);
    }

    private static async Task<OperationReceipt?> FindReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT session_id, operation_id, payload_fingerprint, revision, command_type, committed_at " +
            "FROM operation_receipts WHERE session_id = $s AND operation_id = $o";
        command.Parameters.AddWithValue("$s", sessionId);
        command.Parameters.AddWithValue("$o", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OperationReceipt
        {
            SessionId = reader.GetString(0),
            OperationId = reader.GetString(1),
            PayloadFingerprint = reader.GetString(2),
            Revision = reader.GetInt32(3),
            Type = (GameCommandType)reader.GetInt32(4),
            CommittedAtUnixMs = reader.GetInt64(5),
        };
    }

    public async Task CommitAsync(
        Session current,
        Session next,
        GameCommand command,
        GameEvent gameEvent,
        OperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                FaultInjector?.Invoke(TransactionFaultPoint.BeforeEventWrite);

                // 1. 先按 operationId 去重：同 ID 同内容返回原结果，同 ID 不同内容返回冲突。
                //    必须先于 revision 校验，否则重放会先撞上 STALE_REVISION 而无法返回原结果。
                var existing = await FindReceiptAsync(connection, transaction, command.SessionId, command.OperationId, cancellationToken);
                if (existing is not null)
                {
                    if (!string.Equals(existing.PayloadFingerprint, command.PayloadFingerprint(), StringComparison.Ordinal))
                    {
                        throw new DomainRuleException(DomainErrorCode.OPERATION_CONFLICT, "同一 operationId 提交了不同内容。");
                    }

                    throw new DuplicateOperationException(existing);
                }

                // 2. 事务内重新确认当前 revision，避免并发/过期命令被写入
                var actualRevision = await ReadRevisionAsync(connection, transaction, current.SessionId, cancellationToken);
                if (actualRevision != command.ExpectedRevision || actualRevision != current.Revision)
                {
                    throw new DomainRuleException(DomainErrorCode.STALE_REVISION, "当前 revision 已变化，请重新读取阶段。");
                }

                if (next.Revision != current.Revision + 1)
                {
                    throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "提交后 revision 必须恰好加 1。");
                }

                await UpsertSessionAsync(connection, transaction, next, cancellationToken);

                FaultInjector?.Invoke(TransactionFaultPoint.AfterEventWrite);

                // 2. receipt（唯一约束负责最终去重）
                await using (var insertReceipt = connection.CreateCommand())
                {
                    insertReceipt.Transaction = transaction;
                    insertReceipt.CommandText =
                        "INSERT INTO operation_receipts " +
                        "(session_id, operation_id, payload_fingerprint, revision, command_type, committed_at) " +
                        "VALUES ($s, $o, $f, $r, $t, $at)";
                    insertReceipt.Parameters.AddWithValue("$s", receipt.SessionId);
                    insertReceipt.Parameters.AddWithValue("$o", receipt.OperationId);
                    insertReceipt.Parameters.AddWithValue("$f", receipt.PayloadFingerprint);
                    insertReceipt.Parameters.AddWithValue("$r", receipt.Revision);
                    insertReceipt.Parameters.AddWithValue("$t", (int)receipt.Type);
                    insertReceipt.Parameters.AddWithValue("$at", receipt.CommittedAtUnixMs);
                    await insertReceipt.ExecuteNonQueryAsync(cancellationToken);
                }

                // 3. event（安全载荷）
                await using (var insertEvent = connection.CreateCommand())
                {
                    insertEvent.Transaction = transaction;
                    insertEvent.CommandText =
                        "INSERT INTO events " +
                        "(event_id, session_id, operation_id, revision, type, safe_payload, committed_at) " +
                        "VALUES ($e, $s, $o, $r, $t, $p, $at)";
                    insertEvent.Parameters.AddWithValue("$e", gameEvent.EventId);
                    insertEvent.Parameters.AddWithValue("$s", gameEvent.SessionId);
                    insertEvent.Parameters.AddWithValue("$o", gameEvent.OperationId);
                    insertEvent.Parameters.AddWithValue("$r", gameEvent.Revision);
                    insertEvent.Parameters.AddWithValue("$t", (int)gameEvent.Type);
                    insertEvent.Parameters.AddWithValue("$p", gameEvent.SafePayload);
                    insertEvent.Parameters.AddWithValue("$at", gameEvent.CommittedAtUnixMs);
                    await insertEvent.ExecuteNonQueryAsync(cancellationToken);
                }

                FaultInjector?.Invoke(TransactionFaultPoint.BeforeCommit);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<GameEvent>> ReadEventsAsync(string sessionId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, session_id, operation_id, revision, type, safe_payload, committed_at " +
            "FROM events WHERE session_id = $s ORDER BY revision";
        command.Parameters.AddWithValue("$s", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<GameEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new GameEvent
            {
                EventId = reader.GetString(0),
                SessionId = reader.GetString(1),
                OperationId = reader.GetString(2),
                Revision = reader.GetInt32(3),
                Type = (GameCommandType)reader.GetInt32(4),
                SafePayload = reader.GetString(5),
                CommittedAtUnixMs = reader.GetInt64(6),
            });
        }

        return events;
    }

    /// <summary>初始化或恢复时直接写入会话投影（不经过命令事务）。</summary>
    public async Task SaveSessionAsync(Session session, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await UpsertSessionAsync(connection, transaction, session, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>登记已冻结剧本包（只读引用）。</summary>
    public async Task RegisterPackageAsync(
        ScriptPackage package,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO packages " +
                "(package_id, version, manifest_hash, title, player_count, validation, registered_at) " +
                "VALUES ($p, $v, $h, $t, $c, $val, $at)";
            command.Parameters.AddWithValue("$p", package.PackageId);
            command.Parameters.AddWithValue("$v", package.Version);
            command.Parameters.AddWithValue("$h", package.ManifestHash);
            command.Parameters.AddWithValue("$t", package.Title);
            command.Parameters.AddWithValue("$c", package.PlayerCount);
            command.Parameters.AddWithValue("$val", (int)package.Validation);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>数据库完整性检查；恢复流程在读取会话前调用。</summary>
    public async Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        var result = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _writeLock.Dispose();

    private static async Task<int> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM sessions WHERE session_id = $s";
        command.Parameters.AddWithValue("$s", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            throw new DomainRuleException(DomainErrorCode.STORAGE_FAILED, "会话不存在，无法提交。");
        }

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task UpsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Session session,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO sessions " +
            "(session_id, package_id, package_version, manifest_hash, state, current_phase_id, revision, " +
            " released_materials, vote_summary, resume_state, updated_at) " +
            "VALUES ($id, $p, $v, $h, $st, $ph, $r, $rm, $vs, $rs, $at) " +
            "ON CONFLICT (session_id) DO UPDATE SET " +
            " package_id = excluded.package_id, package_version = excluded.package_version, " +
            " manifest_hash = excluded.manifest_hash, state = excluded.state, " +
            " current_phase_id = excluded.current_phase_id, revision = excluded.revision, " +
            " released_materials = excluded.released_materials, vote_summary = excluded.vote_summary, " +
            " resume_state = excluded.resume_state, updated_at = excluded.updated_at";
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$p", session.PackageId);
        command.Parameters.AddWithValue("$v", session.PackageVersion);
        command.Parameters.AddWithValue("$h", session.ManifestHash);
        command.Parameters.AddWithValue("$st", (int)session.State);
        command.Parameters.AddWithValue("$ph", (object?)session.CurrentPhaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$r", session.Revision);
        command.Parameters.AddWithValue("$rm", JsonSerializer.Serialize(session.ReleasedMaterialIds));
        command.Parameters.AddWithValue("$vs", SerializeVotes(session));
        command.Parameters.AddWithValue(
            "$rs",
            session.ResumeState is null ? DBNull.Value : (int)session.ResumeState.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectSessionSql =
        "SELECT session_id, package_id, package_version, manifest_hash, state, current_phase_id, revision, " +
        "released_materials, vote_summary, resume_state FROM sessions";

    /// <summary>
    /// 投票汇总落盘。
    ///
    /// 多轮投票时每一轮都要留档，所以新格式是「按环节的记录列表」：
    /// <c>{"records":[{"phaseId":"…","counts":[{"Option":"A","Count":3}]}]}</c>。
    /// 旧存档是裸数组（单个汇总、没有环节归属），原样写回旧格式，
    /// 免得读到旧格式再保存时把它变成另一个形状、丢掉归属不明的历史。
    /// </summary>
    private static object SerializeVotes(Session session)
    {
        if (session.VoteSummary is null)
        {
            return DBNull.Value;
        }

        if (session.VoteHistory.Count == 0)
        {
            return JsonSerializer.Serialize(
                session.VoteSummary.Counts.Select(c => new VoteCountDto(c.Option, c.Count)));
        }

        var records = session.VoteHistory
            .Select(r => new PhaseVoteDto(
                r.PhaseId,
                r.Summary.Counts.Select(c => new VoteCountDto(c.Option, c.Count)).ToArray()))
            .ToArray();
        return JsonSerializer.Serialize(new VotePayloadDto(records));
    }

    private static Session ReadSession(SqliteDataReader reader)
    {
        var released = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [];
        VoteSummary? votes = null;
        string? votePhaseId = null;
        IReadOnlyList<PhaseVoteRecord> history = [];

        if (!reader.IsDBNull(8))
        {
            var raw = reader.GetString(8);
            if (raw.TrimStart().StartsWith('{'))
            {
                var payload = JsonSerializer.Deserialize<VotePayloadDto>(raw);
                var records = payload?.Records?
                    .Select(r => new PhaseVoteRecord
                    {
                        PhaseId = r.PhaseId ?? string.Empty,
                        Summary = new VoteSummary
                        {
                            Counts = (r.Counts ?? [])
                                .Select(c => new VoteCount { Option = c.Option, Count = c.Count })
                                .ToArray(),
                        },
                    })
                    .ToArray() ?? [];

                history = records;
                if (records.Length > 0)
                {
                    votes = records[^1].Summary;
                    votePhaseId = records[^1].PhaseId;
                }
            }
            else
            {
                // 旧格式：单个汇总，没有环节归属。
                var dtos = JsonSerializer.Deserialize<VoteCountDto[]>(raw) ?? [];
                votes = new VoteSummary
                {
                    Counts = dtos.Select(d => new VoteCount { Option = d.Option, Count = d.Count }).ToArray(),
                };
            }
        }

        return new Session
        {
            SessionId = reader.GetString(0),
            PackageId = reader.GetString(1),
            PackageVersion = reader.GetString(2),
            ManifestHash = reader.GetString(3),
            State = (SessionState)reader.GetInt32(4),
            CurrentPhaseId = reader.IsDBNull(5) ? null : reader.GetString(5),
            Revision = reader.GetInt32(6),
            ReleasedMaterialIds = released,
            VoteSummary = votes,
            VoteSummaryPhaseId = votePhaseId,
            VoteHistory = history,
            ResumeState = reader.IsDBNull(9) ? null : (SessionState)reader.GetInt32(9),
        };
    }

    /// <summary>
    /// 列出所有存档的绑定信息（不含任何剧情内容）。
    /// 「继续这一局」用它发现「存档还在、但绑定的剧本包已经不在」的情况：
    /// 那种局面必须明说不能恢复，而不是静静忽略或者拿新包顶上去。
    /// </summary>
    public async Task<IReadOnlyList<StoredSessionRef>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id, package_id, package_version, manifest_hash, state, current_phase_id, revision " +
            "FROM sessions ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<StoredSessionRef>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new StoredSessionRef(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                (SessionState)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6)));
        }

        return list;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string LoadSchema() => SqliteSchemaSql.Text;

    private sealed record VoteCountDto(string Option, int Count);

    private sealed record PhaseVoteDto(string? PhaseId, VoteCountDto[]? Counts);

    private sealed record VotePayloadDto(PhaseVoteDto[]? Records);
}

/// <summary>可注入的事务故障点，覆盖 SC-004 要求的事件写入与提交两个位置。</summary>
public enum TransactionFaultPoint
{
    BeforeEventWrite = 0,
    AfterEventWrite = 1,
    BeforeCommit = 2,
}

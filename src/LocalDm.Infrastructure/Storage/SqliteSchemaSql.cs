namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 本地存档 schema。单文件 SQLite、单写入者；receipts 对 (session_id, operation_id) 唯一。
/// 对应 specs/001-local-dm-mvp/data-model.md「本地存储关系」。
/// </summary>
internal static class SqliteSchemaSql
{
    public const string Text = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS packages (
            package_id     TEXT NOT NULL,
            version        TEXT NOT NULL,
            manifest_hash  TEXT NOT NULL,
            title          TEXT NOT NULL,
            player_count   INTEGER NOT NULL,
            validation     INTEGER NOT NULL,
            registered_at  INTEGER NOT NULL,
            PRIMARY KEY (package_id, version)
        );

        CREATE TABLE IF NOT EXISTS sessions (
            session_id         TEXT NOT NULL PRIMARY KEY,
            package_id         TEXT NOT NULL,
            package_version    TEXT NOT NULL,
            manifest_hash      TEXT NOT NULL,
            state              INTEGER NOT NULL,
            current_phase_id   TEXT,
            revision           INTEGER NOT NULL,
            released_materials TEXT NOT NULL DEFAULT '[]',
            vote_summary       TEXT,
            resume_state       INTEGER,
            updated_at         INTEGER NOT NULL
        );

        CREATE TRIGGER IF NOT EXISTS sessions_revision_non_negative
        BEFORE UPDATE ON sessions
        WHEN NEW.revision < 0
        BEGIN
            SELECT RAISE(ABORT, 'REVISION_INVALID');
        END;

        CREATE TABLE IF NOT EXISTS operation_receipts (
            session_id          TEXT NOT NULL,
            operation_id        TEXT NOT NULL,
            payload_fingerprint TEXT NOT NULL,
            revision            INTEGER NOT NULL,
            command_type        INTEGER NOT NULL,
            committed_at        INTEGER NOT NULL,
            PRIMARY KEY (session_id, operation_id),
            FOREIGN KEY (session_id) REFERENCES sessions (session_id)
        );

        CREATE TABLE IF NOT EXISTS events (
            event_id     TEXT NOT NULL PRIMARY KEY,
            session_id   TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            revision     INTEGER NOT NULL,
            type         INTEGER NOT NULL,
            safe_payload TEXT NOT NULL,
            committed_at INTEGER NOT NULL,
            UNIQUE (session_id, revision),
            FOREIGN KEY (session_id) REFERENCES sessions (session_id)
        );

        CREATE INDEX IF NOT EXISTS ix_events_session ON events (session_id, revision);

        CREATE TABLE IF NOT EXISTS package_files (
            package_id  TEXT NOT NULL,
            version     TEXT NOT NULL,
            file_key    TEXT NOT NULL,
            sha256      TEXT NOT NULL,
            byte_length INTEGER NOT NULL,
            PRIMARY KEY (package_id, version, file_key)
        );

        CREATE TABLE IF NOT EXISTS dm_conversations (
            session_id    TEXT NOT NULL PRIMARY KEY,
            snapshot_json TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );

        -- 公开消息记录：给平板看的桌上公开对话。
        -- 和 dm_conversations 分开存：这里没有备忘、没有角色本、没有真相，
        -- 也不会从 AI 备忘里重建。序号在本局内单调递增，平板按它去重与补发。
        -- 同一 message_id 在本局内只存一次，重试不会多出一条、不会跳号。
        CREATE TABLE IF NOT EXISTS public_messages (
            session_id TEXT NOT NULL,
            sequence   INTEGER NOT NULL,
            message_id TEXT NOT NULL,
            author     INTEGER NOT NULL,
            kind       INTEGER NOT NULL,
            body       TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (session_id, sequence),
            UNIQUE (session_id, message_id)
        );
        """;
}

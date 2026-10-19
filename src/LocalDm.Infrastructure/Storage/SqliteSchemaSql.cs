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
        """;
}

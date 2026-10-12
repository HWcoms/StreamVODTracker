using Microsoft.Data.Sqlite;

namespace VODTracker.Infrastructure;

/// <summary>Owns SQLite connection creation and applies ordered, versioned schema migrations.</summary>
public sealed class SqliteDatabase(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        ForeignKeys = true,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var migration = connection.CreateCommand())
        {
            migration.CommandText = "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAtUtc TEXT NOT NULL);";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

        await ApplyMigrationAsync(connection, cancellationToken);
        await ApplyDescriptionMigrationAsync(connection, cancellationToken);
    }

    public SqliteConnection CreateConnection() => new(_connectionString);

    private static async Task ApplyMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var check = connection.CreateCommand();
        check.Transaction = (SqliteTransaction)transaction;
        check.CommandText = "SELECT EXISTS(SELECT 1 FROM SchemaMigrations WHERE Version = 1);";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            await using var schema = connection.CreateCommand();
            schema.Transaction = (SqliteTransaction)transaction;
            schema.CommandText = """
                CREATE TABLE Channels (
                    Id TEXT PRIMARY KEY, Platform TEXT NOT NULL CHECK (Platform IN ('chzzk','soop')),
                    ExternalChannelId TEXT NULL, DisplayName TEXT NOT NULL, ChannelUrl TEXT NULL,
                    IsEnabled INTEGER NOT NULL DEFAULT 1, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                CREATE UNIQUE INDEX UX_Channels_Platform_ExternalChannelId ON Channels(Platform, ExternalChannelId) WHERE ExternalChannelId IS NOT NULL;
                CREATE TABLE MediaItems (
                    Id TEXT PRIMARY KEY, Kind TEXT NOT NULL CHECK (Kind IN ('OnlineVod','LocalVideo','DirectUrl')),
                    Platform TEXT NULL, ExternalVodId TEXT NULL, ChannelId TEXT NULL REFERENCES Channels(Id) ON DELETE SET NULL,
                    Title TEXT NOT NULL, SourceUrl TEXT NULL, LocalPath TEXT NULL, PublishedAtUtc TEXT NULL,
                    DurationSeconds INTEGER NULL, Availability TEXT NOT NULL DEFAULT 'Unknown',
                    FirstSeenAtUtc TEXT NULL, LastSeenAtUtc TEXT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                    CHECK (Kind != 'LocalVideo' OR LocalPath IS NOT NULL),
                    CHECK (Kind != 'OnlineVod' OR SourceUrl IS NOT NULL OR ExternalVodId IS NOT NULL));
                CREATE UNIQUE INDEX UX_MediaItems_Platform_ExternalVodId ON MediaItems(Platform, ExternalVodId) WHERE ExternalVodId IS NOT NULL;
                CREATE INDEX IX_MediaItems_ChannelId ON MediaItems(ChannelId);
                CREATE TABLE WatchRecords (
                    MediaItemId TEXT PRIMARY KEY REFERENCES MediaItems(Id) ON DELETE CASCADE,
                    WatchStatus TEXT NOT NULL DEFAULT 'Unwatched', PositionSeconds REAL NOT NULL DEFAULT 0,
                    CompletedAtUtc TEXT NULL, UpdatedAtUtc TEXT NOT NULL, RowVersion INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE Notes (
                    Id TEXT PRIMARY KEY, MediaItemId TEXT NOT NULL REFERENCES MediaItems(Id) ON DELETE CASCADE,
                    Body TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                CREATE INDEX IX_Notes_MediaItemId ON Notes(MediaItemId);
                CREATE TABLE RefreshRuns (
                    Id TEXT PRIMARY KEY, ChannelId TEXT NOT NULL REFERENCES Channels(Id), StartedAtUtc TEXT NOT NULL,
                    FinishedAtUtc TEXT NULL, Status TEXT NOT NULL, NewItemsCount INTEGER NOT NULL DEFAULT 0, ErrorSummary TEXT NULL);
                CREATE TABLE AppSettings (Key TEXT PRIMARY KEY, Value TEXT NULL);
                INSERT INTO SchemaMigrations(Version, AppliedAtUtc) VALUES (1, $now);
                """;
            schema.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyDescriptionMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var check = connection.CreateCommand();
        check.Transaction = (SqliteTransaction)transaction;
        check.CommandText = "SELECT EXISTS(SELECT 1 FROM SchemaMigrations WHERE Version = 2);";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            migration.CommandText = "ALTER TABLE Channels ADD COLUMN Description TEXT NOT NULL DEFAULT ''; INSERT INTO SchemaMigrations(Version, AppliedAtUtc) VALUES (2, $now);";
            migration.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

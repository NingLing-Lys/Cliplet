using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cliplet;

sealed class ClipDatabase : IDisposable
{
    readonly SqliteConnection connection;
    public string DirectoryPath { get; }
    public string ImageDirectory { get; }

    public ClipDatabase()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cliplet");
        ImageDirectory = Path.Combine(DirectoryPath, "images");
        Directory.CreateDirectory(ImageDirectory);
        var path = Path.Combine(DirectoryPath, "cliplet.sqlite");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS clips (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                text TEXT NOT NULL DEFAULT '',
                image_path TEXT,
                thumb_path TEXT,
                file_paths TEXT NOT NULL DEFAULT '[]',
                preview TEXT NOT NULL,
                source_app TEXT NOT NULL DEFAULT '',
                pinned INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                content_hash TEXT NOT NULL
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_clips_hash ON clips(content_hash);");
        Execute("CREATE INDEX IF NOT EXISTS idx_clips_created ON clips(created_at DESC);");
    }

    public List<ClipItem> LoadAll()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, text, image_path, thumb_path, file_paths, preview, source_app, pinned, created_at, content_hash
            FROM clips
            ORDER BY pinned DESC, created_at DESC
            """;
        using var reader = command.ExecuteReader();
        var items = new List<ClipItem>();
        while (reader.Read())
            items.Add(Read(reader));
        return items;
    }

    public void Insert(ClipItem item)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clips (id, kind, text, image_path, thumb_path, file_paths, preview, source_app, pinned, created_at, content_hash)
            VALUES ($id, $kind, $text, $image, $thumb, $files, $preview, $source, $pinned, $created, $hash)
            """;
        Bind(command, item);
        command.ExecuteNonQuery();
    }

    public void Touch(string id, DateTime createdAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET created_at = $created WHERE id = $id";
        command.Parameters.AddWithValue("$created", ToUnix(createdAt));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetPinned(string id, bool pinned)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET pinned = $pinned WHERE id = $id";
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public List<ClipItem> UnpinnedBeyond(int keep)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, text, image_path, thumb_path, file_paths, preview, source_app, pinned, created_at, content_hash
            FROM clips
            WHERE pinned = 0
            ORDER BY created_at DESC
            LIMIT -1 OFFSET $keep
            """;
        command.Parameters.AddWithValue("$keep", keep);
        using var reader = command.ExecuteReader();
        var items = new List<ClipItem>();
        while (reader.Read())
            items.Add(Read(reader));
        return items;
    }

    public List<ClipItem> DeleteUnpinned()
    {
        var doomed = LoadAll().Where(item => !item.Pinned).ToList();
        Execute("DELETE FROM clips WHERE pinned = 0");
        return doomed;
    }

    public void Dispose() => connection.Dispose();

    static void Bind(SqliteCommand command, ClipItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$kind", item.Kind.ToString());
        command.Parameters.AddWithValue("$text", item.Text);
        command.Parameters.AddWithValue("$image", (object?)item.ImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$thumb", (object?)item.ThumbPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$files", JsonSerializer.Serialize(item.FilePaths));
        command.Parameters.AddWithValue("$preview", item.Preview);
        command.Parameters.AddWithValue("$source", item.SourceApp);
        command.Parameters.AddWithValue("$pinned", item.Pinned ? 1 : 0);
        command.Parameters.AddWithValue("$created", ToUnix(item.CreatedAt));
        command.Parameters.AddWithValue("$hash", item.ContentHash);
    }

    static ClipItem Read(SqliteDataReader reader)
    {
        var files = Array.Empty<string>();
        var json = reader.GetString(5);
        try
        {
            files = JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            files = [];
        }

        return new ClipItem
        {
            Id = reader.GetString(0),
            Kind = Enum.TryParse<ClipKind>(reader.GetString(1), out var kind) ? kind : ClipKind.Text,
            Text = reader.GetString(2),
            ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
            ThumbPath = reader.IsDBNull(4) ? null : reader.GetString(4),
            FilePaths = files,
            Preview = reader.GetString(6),
            SourceApp = reader.GetString(7),
            Pinned = reader.GetInt32(8) == 1,
            CreatedAt = FromUnix(reader.GetInt64(9)),
            ContentHash = reader.GetString(10)
        };
    }

    void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long ToUnix(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    static DateTime FromUnix(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
}

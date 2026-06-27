using Microsoft.Data.Sqlite;
using System.IO;

namespace GPTBackup;

public sealed class BackupDatabase
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public BackupDatabase()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GPTBackup");

        Directory.CreateDirectory(appData);
        DatabasePath = Path.Combine(appData, "gpt-backup.sqlite");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
        Initialize();
    }

    public void SaveChat(ChatSummary chat, IReadOnlyList<ChatMessage> messages, string rawHtml)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chats (id, title, url, raw_html, backed_up_at)
                VALUES ($id, $title, $url, $rawHtml, datetime('now'))
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    url = excluded.url,
                    raw_html = excluded.raw_html,
                    backed_up_at = excluded.backed_up_at;
                """;
            command.Parameters.AddWithValue("$id", chat.Id);
            command.Parameters.AddWithValue("$title", chat.Title);
            command.Parameters.AddWithValue("$url", chat.Url);
            command.Parameters.AddWithValue("$rawHtml", rawHtml);
            command.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM messages WHERE chat_id = $chatId;";
            delete.Parameters.AddWithValue("$chatId", chat.Id);
            delete.ExecuteNonQuery();
        }

        foreach (var message in messages)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages (chat_id, role, text, position)
                VALUES ($chatId, $role, $text, $position);
                """;
            insert.Parameters.AddWithValue("$chatId", chat.Id);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$text", message.Text);
            insert.Parameters.AddWithValue("$position", message.Position);
            insert.ExecuteNonQuery();
        }

        using (var markSaved = connection.CreateCommand())
        {
            markSaved.Transaction = transaction;
            markSaved.CommandText = """
                INSERT INTO backup_queue (id, title, url, status, attempt_count, last_error, discovered_at, updated_at)
                VALUES ($id, $title, $url, 'saved', 0, NULL, datetime('now'), datetime('now'))
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    url = excluded.url,
                    status = 'saved',
                    last_error = NULL,
                    updated_at = datetime('now');
                """;
            markSaved.Parameters.AddWithValue("$id", chat.Id);
            markSaved.Parameters.AddWithValue("$title", chat.Title);
            markSaved.Parameters.AddWithValue("$url", chat.Url);
            markSaved.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int UpsertDiscoveredChats(IEnumerable<ChatSummary> chats)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var changed = 0;

        foreach (var chat in chats)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO backup_queue (id, title, url, status, attempt_count, last_error, discovered_at, updated_at)
                VALUES ($id, $title, $url, 'pending', 0, NULL, datetime('now'), datetime('now'))
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    url = excluded.url,
                    updated_at = datetime('now');
                """;
            command.Parameters.AddWithValue("$id", chat.Id);
            command.Parameters.AddWithValue("$title", chat.Title);
            command.Parameters.AddWithValue("$url", chat.Url);
            changed += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return changed;
    }

    public void MarkBackupStarted(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_queue
            SET status = 'running',
                attempt_count = attempt_count + 1,
                last_error = NULL,
                updated_at = datetime('now')
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void MarkBackupFailed(ChatSummary chat, string error)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backup_queue (id, title, url, status, attempt_count, last_error, discovered_at, updated_at)
            VALUES ($id, $title, $url, 'failed', 1, $error, datetime('now'), datetime('now'))
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                url = excluded.url,
                status = 'failed',
                last_error = excluded.last_error,
                updated_at = datetime('now');
            """;
        command.Parameters.AddWithValue("$id", chat.Id);
        command.Parameters.AddWithValue("$title", chat.Title);
        command.Parameters.AddWithValue("$url", chat.Url);
        command.Parameters.AddWithValue("$error", error);
        command.ExecuteNonQuery();
    }

    public void MarkBackupDeferred(ChatSummary chat, string reason)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backup_queue (id, title, url, status, attempt_count, last_error, discovered_at, updated_at)
            VALUES ($id, $title, $url, 'pending', 0, $reason, datetime('now'), datetime('now'))
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                url = excluded.url,
                status = 'pending',
                last_error = excluded.last_error,
                updated_at = datetime('now');
            """;
        command.Parameters.AddWithValue("$id", chat.Id);
        command.Parameters.AddWithValue("$title", chat.Title);
        command.Parameters.AddWithValue("$url", chat.Url);
        command.Parameters.AddWithValue("$reason", reason);
        command.ExecuteNonQuery();
    }

    public void ResetFailedBackups()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_queue
            SET status = 'pending',
                last_error = NULL,
                updated_at = datetime('now')
            WHERE status = 'failed';
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BackupCandidate> GetBackupQueue(bool includeSaved = false)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = includeSaved
            ? """
              SELECT id, title, url, status, attempt_count, last_error
              FROM backup_queue
              ORDER BY discovered_at ASC;
              """
            : """
              SELECT id, title, url, status, attempt_count, last_error
              FROM backup_queue
              WHERE status IN ('pending', 'failed', 'running')
              ORDER BY
                  CASE status WHEN 'failed' THEN 0 WHEN 'running' THEN 1 ELSE 2 END,
                  discovered_at ASC;
              """;

        var candidates = new List<BackupCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(new BackupCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return candidates;
    }

    public BackupStats GetStats()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN status = 'saved' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status IN ('pending', 'running') THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)
            FROM backup_queue;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new BackupStats(0, 0, 0, 0);
        }

        return new BackupStats(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }

    public IReadOnlyList<SearchResult> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetRecentChats();
        }

        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (terms.Count == 0)
        {
            return GetRecentChats();
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var predicates = terms
            .Select((_, index) => $"c.title LIKE $query{index} OR matches.text LIKE $query{index}")
            .ToList();

        command.CommandText = """
            SELECT c.id, c.title, 'chat', '', c.url, counts.message_count, c.backed_up_at
            FROM chats c
            JOIN (
                SELECT chat_id, COUNT(*) AS message_count
                FROM messages
                GROUP BY chat_id
            ) counts ON counts.chat_id = c.id
            LEFT JOIN messages matches ON matches.chat_id = c.id
            WHERE (
            """ + string.Join($"{Environment.NewLine}                OR ", predicates) + """
            )
            GROUP BY c.id, c.title, c.url, counts.message_count, c.backed_up_at
            ORDER BY c.backed_up_at DESC
            LIMIT 100;
            """;

        for (var i = 0; i < terms.Count; i++)
        {
            command.Parameters.AddWithValue($"$query{i}", $"%{terms[i]}%");
        }

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }

        return results;
    }

    public IReadOnlyList<SearchResult> GetRecentChats()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, 'chat', '', c.url, COUNT(m.id), c.backed_up_at
            FROM chats c
            LEFT JOIN messages m ON m.chat_id = c.id
            GROUP BY c.id, c.title, c.url, c.backed_up_at
            ORDER BY c.backed_up_at DESC
            LIMIT 100;
            """;

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }

        return results;
    }

    public string ExportMarkdown(string chatId)
    {
        using var connection = OpenConnection();

        using var chatCommand = connection.CreateCommand();
        chatCommand.CommandText = "SELECT title, url FROM chats WHERE id = $id;";
        chatCommand.Parameters.AddWithValue("$id", chatId);

        using var chatReader = chatCommand.ExecuteReader();
        if (!chatReader.Read())
        {
            return "";
        }

        var title = chatReader.GetString(0);
        var url = chatReader.GetString(1);
        chatReader.Close();

        using var messageCommand = connection.CreateCommand();
        messageCommand.CommandText = """
            SELECT role, text
            FROM messages
            WHERE chat_id = $id
            ORDER BY position ASC;
            """;
        messageCommand.Parameters.AddWithValue("$id", chatId);

        using var messageReader = messageCommand.ExecuteReader();
        var lines = new List<string>
        {
            $"# {title}",
            "",
            $"Source: {url}",
            ""
        };

        while (messageReader.Read())
        {
            lines.Add($"## {messageReader.GetString(0)}");
            lines.Add("");
            lines.Add(messageReader.GetString(1));
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string ExportContinuationMarkdown(string chatId)
    {
        var markdown = ExportMarkdown(chatId);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var lines = new List<string>
        {
            "# Chat Handoff",
            "",
            "I am continuing a previous ChatGPT conversation. Use the full transcript below as context.",
            "",
            "Please preserve the goals, decisions, constraints, terminology, and project state from the previous chat. If anything is unclear, ask a focused clarifying question before making assumptions.",
            "",
            "Read the transcript chronologically from top to bottom so the timeline, corrections, and later decisions are not mixed up.",
            "",
            "The transcript may be long. Prioritize the most recent decisions and concrete implementation details when continuing, but do not ignore earlier context when it explains why later decisions were made.",
            "",
            "---",
            "",
            markdown
        };

        return string.Join(Environment.NewLine, lines);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chats (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                raw_html TEXT NOT NULL,
                backed_up_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                position INTEGER NOT NULL,
                FOREIGN KEY(chat_id) REFERENCES chats(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS backup_queue (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                discovered_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_messages_chat_position
                ON messages(chat_id, position);

            CREATE INDEX IF NOT EXISTS idx_chats_backed_up_at
                ON chats(backed_up_at);

            CREATE INDEX IF NOT EXISTS idx_backup_queue_status
                ON backup_queue(status, discovered_at);
            """;
        command.ExecuteNonQuery();
    }
}

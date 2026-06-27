namespace GPTBackup;

public sealed record ChatSummary(string Id, string Title, string Url);

public sealed record ChatMessage(string Role, string Text, int Position);

public sealed record SearchResult(
    string ChatId,
    string Title,
    string Role,
    string Text,
    string Url,
    int MessageCount,
    string SavedAt);

public sealed record BackupCandidate(
    string Id,
    string Title,
    string Url,
    string Status,
    int AttemptCount,
    string? LastError);

public sealed record BackupStats(int Discovered, int Saved, int Pending, int Failed);

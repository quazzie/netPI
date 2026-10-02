namespace NetPI.Host.Storage.Sqlite;

/// <summary>One result row, read by column name. Internal to the sqlite provider: no plugin sees a row.</summary>
internal interface ISqlRow
{
    int FieldCount { get; }
    string GetName(int ordinal);
    int Ordinal(string name);
    bool IsNull(string name);
    long GetInt64(string name);
    long? GetInt64OrNull(string name);
    double GetDouble(string name);
    string GetString(string name);
    string? GetStringOrNull(string name);
    byte[]? GetBlob(string name);
    object? GetValue(int ordinal);
}

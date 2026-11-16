using System.IO;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using CsvHelper;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        public static readonly string CONNECTION_STRING = $"Data Source={AppPaths.HistoryDb};";

        // Whether the timestamps of very old versions (date texts) were converted in this run.
        private static bool timestampsMigrated = false;

        static SQLiteHistoryLogger()
        {
            using var connection = Open();
            using var command = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT
                );", connection);
            command.ExecuteNonQuery();
        }

        // A connection for each operation (they are pooled, so this costs little). Sentences are logged, read back
        // as context and listed on the history page from different threads, and a connection is not made to be
        // used by two threads at once.
        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(CONNECTION_STRING);
            connection.Open();
            return connection;
        }

        private static DateTime ToLocalTime(string unixTime) =>
            DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime, CultureInfo.InvariantCulture)).LocalDateTime;

        private static TranslationHistoryEntry ReadEntry(System.Data.Common.DbDataReader reader, DateTime localTime, string timestampFormat)
        {
            return new TranslationHistoryEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                Timestamp = localTime.ToString(timestampFormat),
                TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
            };
        }

        public static async Task LogTranslation(string sourceText, string translatedText,
            string targetLanguage, string apiUsed, CancellationToken token = default)
        {
            string insertQuery = @"
                INSERT INTO TranslationHistory (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)";

            await using var connection = Open();
            await using (var command = new SqliteCommand(insertQuery, connection))
            {
                command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@SourceText", sourceText);
                command.Parameters.AddWithValue("@TranslatedText", translatedText);
                command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
                command.Parameters.AddWithValue("@ApiUsed", apiUsed);
                await command.ExecuteNonQueryAsync(token);
            }
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page, int maxRow, string searchText, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            int totalCount = 0;
            bool oldTimestamps = false;

            await using (var connection = Open())
            {
                await using (var command = new SqliteCommand(@"
                    SELECT COUNT(*)
                    FROM TranslationHistory
                    WHERE SourceText LIKE @search OR TranslatedText LIKE @search", connection))
                {
                    command.Parameters.AddWithValue("@search", $"%{searchText}%");
                    totalCount = Convert.ToInt32(await command.ExecuteScalarAsync(token));
                }

                int offset = Math.Max(0, (page - 1) * maxRow);
                await using (var command = new SqliteCommand(@"
                    SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                    FROM TranslationHistory
                    WHERE SourceText LIKE @search OR TranslatedText LIKE @search
                    ORDER BY Timestamp DESC
                    LIMIT @maxRow OFFSET @offset", connection))
                {
                    command.Parameters.AddWithValue("@search", $"%{searchText}%");
                    command.Parameters.AddWithValue("@maxRow", maxRow);
                    command.Parameters.AddWithValue("@offset", offset);

                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        DateTime localTime;
                        try
                        {
                            localTime = ToLocalTime(reader.GetString(reader.GetOrdinal("Timestamp")));
                        }
                        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
                        {
                            // A row whose time cannot be read is left out (after the conversion below was tried).
                            oldTimestamps = true;
                            continue;
                        }
                        history.Add(ReadEntry(reader, localTime, "yyyy-MM-dd HH:mm"));
                    }
                }
            }

            // DEPRECATED: very old versions stored the time as a date text. Converted once per run, so that a row
            // that stays unreadable cannot make the page load again and again.
            if (oldTimestamps && !timestampsMigrated)
            {
                timestampsMigrated = true;
                await MigrateOldTimestampFormat();
                return await LoadHistoryAsync(page, maxRow, searchText, token);
            }

            // At least one page.
            int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            string deleteQuery = "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory'";
            await using var connection = Open();
            await using var command = new SqliteCommand(deleteQuery, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        public static async Task DeleteRows(IEnumerable<long> ids)
        {
            using var connection = new SqliteConnection(CONNECTION_STRING);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            foreach (long id in ids.Distinct())
            {
                using var command = new SqliteCommand("DELETE FROM TranslationHistory WHERE Id = @id", connection, transaction);
                command.Parameters.AddWithValue("@id", id);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
        {
            string selectQuery = @"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Id DESC
                LIMIT 1";

            await using var connection = Open();
            await using var command = new SqliteCommand(selectQuery, connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                DateTime localTime = ToLocalTime(reader.GetString(reader.GetOrdinal("Timestamp")));
                return ReadEntry(reader, localTime, "yyyy-MM-dd HH:mm");
            }
            return null;
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();

            string selectQuery = @"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Timestamp DESC";

            await using (var connection = Open())
            await using (var command = new SqliteCommand(selectQuery, connection))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    string stored = reader.GetString(reader.GetOrdinal("Timestamp"));
                    DateTime localTime;
                    try
                    {
                        localTime = ToLocalTime(stored);
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
                    {
                        // DEPRECATED: a date text of a very old version. A time that cannot be read at all must
                        // not keep the other rows from being exported.
                        if (!DateTime.TryParse(stored, out localTime))
                            continue;
                    }
                    history.Add(ReadEntry(reader, localTime, "yyyy-MM-dd HH:mm:ss"));
                }
            }

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        // DEPRECATED
        private static async Task MigrateOldTimestampFormat()
        {
            var records = new List<(long id, string timestamp)>();
            await using var connection = Open();
            await using (var command = new SqliteCommand("SELECT Id, Timestamp FROM TranslationHistory", connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(reader.GetOrdinal("Id"));
                    string timestamp = reader.GetString(reader.GetOrdinal("Timestamp"));
                    records.Add((id, timestamp));
                }
            }

            foreach (var (id, timestamp) in records)
            {
                // Only the date texts: the rows that already hold seconds are left alone.
                if (!double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                    DateTime.TryParse(timestamp, out DateTime dt))
                {
                    long unixTime = ((DateTimeOffset)dt).ToUnixTimeSeconds();
                    await using var updateCommand = new SqliteCommand(
                        "UPDATE TranslationHistory SET Timestamp = @Timestamp WHERE Id = @Id",
                        connection);
                    updateCommand.Parameters.AddWithValue("@Id", id);
                    updateCommand.Parameters.AddWithValue("@Timestamp", unixTime.ToString());
                    await updateCommand.ExecuteNonQueryAsync();
                }
            }
        }
    }
}

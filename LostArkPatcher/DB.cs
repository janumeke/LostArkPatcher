using Microsoft.Data.Sqlite;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LostArkPatcher
{
    internal partial class DB : IDisposable, IAsyncDisposable
    {
        private partial class Table(SqliteConnection dbConnection, string name, string? referentialSchema = null) : IDisposable, IAsyncDisposable
        {
            private readonly SqliteCommand dbCommand = dbConnection.CreateCommand();
            public readonly string name = name;
            private readonly string? referentialSchema = referentialSchema;

            public void Dispose()
            {
                dbCommand.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                await dbCommand.DisposeAsync().ConfigureAwait(false);
            }

            public async Task<bool> ExistsAsync()
            {
                dbCommand.CommandText = SQL.Row.CountQuery(SQL.Table.Schema(name));
                if (Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false)) == 0)
                    return false;
                return true;
            }

            public async Task CreateAsync()
            {
                if (referentialSchema is null)
                    throw new InvalidOperationException("Referential schema is not in the table.");

                dbCommand.CommandText = SQL.Table.Create(name, referentialSchema);
                dbCommand.ExecuteNonQuery();
            }

            public async Task<bool> AllColumnTypesMatchAsync()
            {
                if (referentialSchema is null)
                    throw new InvalidOperationException("Referential schema is not in the table.");

                Dictionary<string, string> columnTypes = GetColumnTypes(referentialSchema);
                foreach(var columnType in columnTypes)
                {
                    dbCommand.CommandText = SQL.Column.Type(name, columnType.Key);
                    string? type = Convert.ToString(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                    if (type is null || !type.Equals(columnType.Value, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }

            public async Task MatchAllColumnTypesAsync()
            {
                if (referentialSchema is null)
                    throw new InvalidOperationException("Referential schema is not in the table.");

                Dictionary<string, string> columnTypes = GetColumnTypes(referentialSchema);
                foreach (var columnType in columnTypes)
                {
                    dbCommand.CommandText = SQL.Column.Type(name, columnType.Key);
                    string? type = Convert.ToString(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                    if (type is not null && !type.Equals(columnType.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        dbCommand.CommandText = SQL.Column.Drop(name, columnType.Key);
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    if (type is null || !type.Equals(columnType.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        dbCommand.CommandText = SQL.Column.Add(name, columnType.Key, columnType.Value);
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
            }

            public async Task<int> RowCountAsync()
            {
                dbCommand.CommandText = SQL.Row.Count(name);
                object? count = await dbCommand.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt32(count);
            }

            public async Task<object?> SelectScalarAsync(string? condition = null, string? column = null)
            {
                dbCommand.CommandText = SQL.Row.Select(name, condition, column);
                return await dbCommand.ExecuteScalarAsync().ConfigureAwait(false);
            }

            public async Task<int> ReplaceAsync(string columnList, string valueList)
            {
                dbCommand.CommandText = SQL.Row.Replace(name, columnList, valueList);
                return await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            public async Task<int> DeleteAsync(string? condition = null)
            {
                dbCommand.CommandText = SQL.Row.Delete(name, condition);
                return await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            //Helpers

            [GeneratedRegexAttribute(@"(\S+)\s+([^\s,]+)[^,]*(,|$)")]
            private static partial Regex SchemaColumnNameAndType();

            private static Dictionary<string, string> GetColumnTypes(string schema)
            {
                Dictionary<string, string> coloumnTypes = new();
                foreach (Match match in SchemaColumnNameAndType().Matches(schema))
                    coloumnTypes.Add(match.Groups[1].Value, match.Groups[2].Value);
                return coloumnTypes;
            }
        }

        private readonly SqliteConnection dbConnection;

        //sever
        private readonly Table? file_size = null;
        private readonly Table? fileInfo = null;
        private readonly Table? version_info = null;

        //local
        private readonly Table local_info;
        private readonly Table file_version;
        private readonly Table pending_changes;

        public DB(string path_localDB, string? path_serverDB = null)
        {
            dbConnection = new($"Data Source=file:{path_localDB};Pooling=False");
            dbConnection.Open();
            using SqliteCommand dbCommand = dbConnection.CreateCommand();
            dbCommand.CommandText = "PRAGMA journal_mode = WAL";
            dbCommand.ExecuteNonQuery();
            dbCommand.CommandText = $"PRAGMA temp_store = MEMORY";
            dbCommand.ExecuteNonQuery();

            if (path_serverDB is not null)
            {
                const string SERVERDB = "serverdb";
                const string INFOVERSION = "file_infoversion";

                dbCommand.CommandText = $"ATTACH DATABASE @path AS {SERVERDB}";
                dbCommand.Parameters.AddWithValue("@path", $"file:{path_serverDB}?mode=ro");
                dbCommand.ExecuteNonQuery();
                dbCommand.Parameters.Clear();

                file_size = new(dbConnection, $"{SERVERDB}.file_size");

                dbCommand.CommandText = SQL.Table.CreateTempAsQuery(
                    SQL.Row.Join(
                        $"{SERVERDB}.file_info",
                        $"{SERVERDB}.file_version",
                        "left.id = right.id",
                        null,
                        "left.id, unique_path, path, version, size, hash, property"
                    ),
                    INFOVERSION
                );
                dbCommand.ExecuteNonQuery();
                fileInfo = new(dbConnection, INFOVERSION);

                version_info = new(dbConnection, $"{SERVERDB}.version_info");
            }

            local_info = new(dbConnection, "local_info", "key text primary key, value integer");
            file_version = new(dbConnection, "file_version", "unique_path text primary key, version integer, size integer, hash text, property integer");

            const string PENDINGCHANGES = "pending_changes";
            dbCommand.CommandText = SQL.Table.CreateTemp(PENDINGCHANGES, @"
                unique_path text primary key,
                version integer,
                size integer,
                hash text,
                property integer,
                last_modified integer
            ");
            dbCommand.ExecuteNonQuery();
            pending_changes = new(dbConnection, PENDINGCHANGES);
        }

        public void Dispose()
        {
            file_size?.Dispose();
            fileInfo?.Dispose();
            version_info?.Dispose();
            local_info.Dispose();
            file_version.Dispose();
            pending_changes.Dispose();
            dbConnection.Close();
            dbConnection.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (file_size is not null)
                await file_size.DisposeAsync().ConfigureAwait(false);
            if (fileInfo is not null)
                await fileInfo.DisposeAsync().ConfigureAwait(false);
            if (version_info is not null)
                await version_info.DisposeAsync().ConfigureAwait(false);
            await local_info.DisposeAsync().ConfigureAwait(false);
            await file_version.DisposeAsync().ConfigureAwait(false);
            await pending_changes.DisposeAsync().ConfigureAwait(false);
            await dbConnection.CloseAsync().ConfigureAwait(false);
            await dbConnection.DisposeAsync().ConfigureAwait(false);
        }

        public async Task<int?> GetVersionAsync()
        {
            if (!await local_info.ExistsAsync())
                return null;
            object? version = await local_info.SelectScalarAsync("key='version'", "value").ConfigureAwait(false);
            if (version is null || version == DBNull.Value)
                return null;
            else
                return Convert.ToInt32(version);
        }
        
        /// <exception cref="InvalidOperationException"/>
        public async Task FixVersionAsync()
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            object? _version;
            SqliteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                dbCommand.CommandText = SQL.Row.LeftJoin(
                    fileInfo.name,
                    file_version.name,
                    "left.unique_path = right.unique_path",
                    "right.version is null or left.version > right.version",
                    "min(left.version)"
                );
                _version = await dbCommand.ExecuteScalarAsync().ConfigureAwait(false);
            }
            if (_version != DBNull.Value)
                await local_info.ReplaceAsync("key, value", $"'version', {Convert.ToInt32(_version) - 1}").ConfigureAwait(false);
            else
            {
                _version = await fileInfo.SelectScalarAsync(column: "max(version)").ConfigureAwait(false);
                await local_info.ReplaceAsync("key, value", $"'version', {(_version != DBNull.Value ? Convert.ToInt32(_version) : 0)}").ConfigureAwait(false);
            }
        }

        public async Task<bool> CheckSchemaAsync()
        {
            if (!await local_info.ExistsAsync().ConfigureAwait(false))
                return false;
            if (!await local_info.AllColumnTypesMatchAsync().ConfigureAwait(false))
                return false;

            if (!await file_version.ExistsAsync().ConfigureAwait(false))
                return false;
            if (!await file_version.AllColumnTypesMatchAsync().ConfigureAwait(false))
                return false;

            return true;
        }

        public async Task FixSchemaAsync()
        {
            if (await local_info.ExistsAsync().ConfigureAwait(false))
                await local_info.MatchAllColumnTypesAsync().ConfigureAwait(false);
            else
                await local_info.CreateAsync().ConfigureAwait(false);

            if (await file_version.ExistsAsync().ConfigureAwait(false))
                await file_version.MatchAllColumnTypesAsync().ConfigureAwait(false);
            else
                await file_version.CreateAsync().ConfigureAwait(false);
        }

        public async Task<int?> GetFileMaxVersionAsync()
        {
            object? _version = await file_version.SelectScalarAsync(column: "max(version)");
            if (_version is null || _version == DBNull.Value)
                return null;
            try { return Convert.ToInt32(_version); }
            catch { return null; }
        }

        /// <exception cref="InvalidOperationException"/>
        public async Task<DateTime?> GetVersionDateAsync(int version)
        {
            if (version_info is null)
                throw new InvalidOperationException("Server DB is not attched.");

            object? _date = await version_info.SelectScalarAsync(
                $"version={version}",
                "reg_date"
            );
            if (_date is null || _date == DBNull.Value)
                return null;
            //the datetime string looks more like UTC than KST.
            try { return DateTime.ParseExact(Convert.ToString(_date), "yyyy-MM-dd HH:mm:ss", DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal); }
            catch { return null; }
        }

        /// <exception cref="InvalidOperationException"/>
        public async Task<int> GetOutdatedCountAsync()
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            SqliteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                dbCommand.CommandText = SQL.Row.CountQuery(
                    SQL.Row.LeftJoin(
                        fileInfo.name,
                        file_version.name,
                        "left.unique_path = right.unique_path",
                        "right.version is null or left.version > right.version",
                        "distinct left.unique_path"
                    )
                );
                return Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
            }
        }

        private class PeriodicProgressReporter : IDisposable
        {
            private float previous = 0;
            public float Previous {
                get { return previous; }
                set { Interlocked.Exchange(ref previous, value); }
            }

            private float stage = 0;
            public float Stage
            {
                get { return stage; }
                set { Interlocked.Exchange(ref stage, value); }
            }

            private float stageRatio = 0;
            public float StageRatio
            {
                get { return stageRatio; }
                set { Interlocked.Exchange(ref stageRatio, value); }
            }

            readonly Timer timer;

            public PeriodicProgressReporter(double intervalSeconds, IProgress<float> progress)
            {
                timer = new((_) => {
                    progress.Report(Previous + StageRatio * Stage);
                }, null, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
            }

            public void Dispose()
            {
                timer.Dispose();
            }

            public void StartNextStage(float stage)
            {
                StageRatio = 0;
                Previous = Previous + Stage;
                Stage = stage;
            }
        }

        public record DeltaInfo
        {
            public required int? fromVersion { get; init; }
            public required int toVersion { get; init; }
        }

        public record FileUpdateInfo
        {
            public required int id { get; init; }
            /// <value>Case-sensitive.</value>
            public required string relativePath { get; init; }
            /// <value>Ordered from current version to latest version.</value>
            public required List<DeltaInfo> sequence { get; init; }
        }

        /// <param name="UpdateFileAsync">
        /// <para>out: the version in the end, or <c>null</c> if unchanged</para>
        /// <remarks>Block the calling thread to stop the next file updating.</remarks>
        /// </param>
        /// <exception cref="InvalidOperationException"/>
        //Missing or corrupted files for entries will fail, but missing entries will be updated
        public async Task<(int updated, int failed)> UpdateFilesAsync(Func<FileUpdateInfo, CancellationToken, Task<int?>> UpdateFileAsync, CancellationToken cancelToken, IProgress<float>? progress = null)
        {
            if (fileInfo is null || file_size is null)
                throw new InvalidOperationException("Server DB is not attched.");

            int updated = 0, failed = 0;
            PeriodicProgressReporter? progressReporter = null;
            if (progress is not null)
                progressReporter = new(0.5, progress);
            SqliteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                progressReporter?.StartNextStage(1);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
                uint updatingCount = 0;
                ConcurrentExclusiveSchedulerPair schedulerPair = new(TaskScheduler.Default);

                string sqlUpdatableFiles = SQL.Row.QueryLeftJoin(
                        SQL.Row.GroupQuery(
                            SQL.Row.Order(
                                fileInfo.name,
                                "version",
                                SQL.OrderDirection.Descending
                            ),
                            "id"
                        ),
                        file_version.name,
                        "left.unique_path = right.unique_path",
                        "right.version is null or left.version > right.version",
                        "left.id, left.unique_path, left.path, right.version, left.property"
                );
                dbCommand.CommandText = SQL.Row.CountQuery(sqlUpdatableFiles);
                int totalCount = Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                if (totalCount == 0)
                    return (0, 0);
                int doneCount = 0;
                await using (SqliteTransaction transaction = dbConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    dbCommand.Transaction = transaction;

                    dbCommand.CommandText = sqlUpdatableFiles;
                    DbDataReader reader_updatable = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                    SqliteCommand dbCommand_delta = new(
                        SQL.Row.Select(
                            file_size.name,
                            "id = @id and org_ver = @org_ver",
                            "max(new_ver)"
                        ),
                        dbConnection
                    );
                    SqliteCommand dbCommand_after = new(
                        SQL.Row.Select(
                            fileInfo.name,
                            "id = @id and version = @version",
                            "size, hash"
                        ),
                        dbConnection
                    );
                    SqliteCommand dbCommand_update = new(
                        SQL.Row.Replace(
                            pending_changes.name,
                            "unique_path, version, size, hash, property",
                            "@unique_path, @version, @size, @hash, @property"
                        ),
                        dbConnection
                    );
                    await using (reader_updatable.ConfigureAwait(false))
                    await using (dbCommand_delta.ConfigureAwait(false))
                    await using (dbCommand_after.ConfigureAwait(false))
                    await using (dbCommand_update.ConfigureAwait(false))
                    {
                        dbCommand_delta.Transaction = transaction;
                        dbCommand_after.Transaction = transaction;
                        dbCommand_update.Transaction = transaction;
                        foreach (IDataRecord row in reader_updatable)
                        {
                            if (cancelToken.IsCancellationRequested)
                                break;

                            int id = Convert.ToInt32(row["id"]);
                            string unique_path = Convert.ToString(row["unique_path"]);
                            string path = Convert.ToString(row["path"]);
                            object _version = row["version"];
                            int version = _version == DBNull.Value ? -1 : Convert.ToInt32(_version);
                            int property = Convert.ToInt32(row["property"]);

                            List<DeltaInfo> sequence = new();

                            //Find update steps
                            int fromVersion = version;
                            while (true)
                            {
                                if (cancelToken.IsCancellationRequested)
                                    break;

                                //Any version should be able to update to the latest version, so just choose the newest possible version as the next version
                                dbCommand_delta.Parameters.AddWithValue("@id", id);
                                dbCommand_delta.Parameters.AddWithValue("@org_ver", fromVersion);
                                object? _toVersion = await dbCommand_delta.ExecuteScalarAsync().ConfigureAwait(false);
                                dbCommand_delta.Parameters.Clear();
                                if (_toVersion is null || _toVersion == DBNull.Value)
                                    break;
                                int toVersion = Convert.ToInt32(_toVersion);
                                sequence.Add(new()
                                {
                                    fromVersion = fromVersion == -1 ? null : fromVersion,
                                    toVersion = toVersion
                                });

                                fromVersion = toVersion;
                            }

                            if (cancelToken.IsCancellationRequested)
                                break;

                            //Update
                            if (sequence.Count == 0)
                                Interlocked.Increment(ref failed);
                            else
                            {
                                Interlocked.Increment(ref updatingCount);
#pragma warning disable CS4014
                                UpdateFileAsync(
                                    new()
                                    {
                                        id = id,
                                        relativePath = path,
                                        sequence = new(sequence) //sequence will be used later and shouldn't be modified
                                    },
                                    cancelToken
                                )
                                .ContinueWith(async (preTask) => {
                                    try
                                    {
                                        int? afterVersion = preTask.Result;
                                        if (afterVersion is null)
                                            Interlocked.Increment(ref failed);
                                        else
                                        {
                                            dbCommand_after.Parameters.AddWithValue("@id", id);
                                            dbCommand_after.Parameters.AddWithValue("@version", afterVersion);
                                            await using (DbDataReader reader_after = await dbCommand_after.ExecuteReaderAsync().ConfigureAwait(false))
                                            {
                                                if (!await reader_after.ReadAsync().ConfigureAwait(false))
                                                    Interlocked.Increment(ref failed);
                                                else
                                                {
                                                    dbCommand_update.Parameters.AddWithValue("@unique_path", unique_path);
                                                    dbCommand_update.Parameters.AddWithValue("@version", afterVersion);
                                                    dbCommand_update.Parameters.AddWithValue("@size", Convert.ToInt32(reader_after["size"]));
                                                    dbCommand_update.Parameters.AddWithValue("@hash", Convert.ToString(reader_after["hash"]));
                                                    dbCommand_update.Parameters.AddWithValue("@property", property);
                                                    await dbCommand_update.ExecuteNonQueryAsync().ConfigureAwait(false);
                                                    dbCommand_update.Parameters.Clear();

                                                    if (afterVersion == sequence.Last().toVersion)
                                                        Interlocked.Increment(ref updated);
                                                    else
                                                        Interlocked.Increment(ref failed);
                                                }
                                            }
                                            dbCommand_after.Parameters.Clear();
                                        }
                                    }
                                    finally
                                    {
                                        if (progressReporter is not null)
                                            progressReporter.StageRatio = (float)Interlocked.Increment(ref doneCount) / totalCount;
                                        Interlocked.Decrement(ref updatingCount);
                                    }
                                }, schedulerPair.ExclusiveScheduler);
#pragma warning restore CS4014
                            }
                        }

                        while (updatingCount > 0)
                            await Task.Delay(500).ConfigureAwait(false);
                    }

                    transaction.Commit();
                    dbCommand.Transaction = null;
                }
                progressReporter?.StartNextStage(0);

                dbCommand.CommandText = SQL.Row.ReplaceQuery(
                    file_version.name,
                    "unique_path, version, size, hash, property",
                    SQL.Row.Select(
                        pending_changes.name,
                        null,
                        "unique_path, version, size, hash, property"
                    )
                );
                await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
            }
            progressReporter?.Dispose();
            return (updated, failed);
        }

        /// <param name="FileExists">in: relative path, case-insensitive (ok for windows system)</param>
        /// <exception cref="InvalidOperationException"/>
        //Entries in local DB but in server DB will be deleted and those in server DB but in local DB will be inserted; entries with missing or corrupted files will be unchanged
        public async Task<(int deleted, int inserted)> FixFileKeysAsync()
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            int deleted = 0, inserted = 0;
            SqliteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                dbCommand.CommandText = SQL.Row.DeleteInQuery(
                    file_version.name,
                    "unique_path",
                    SQL.Row.SelectInButIn(
                        "unique_path",
                        file_version.name,
                        fileInfo.name
                    )
                );
                deleted += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                dbCommand.CommandText = SQL.Row.InsertQuery(
                    SQL.Row.SelectInButIn(
                        "unique_path",
                        fileInfo.name,
                        file_version.name,
                        "distinct unique_path"
                    ),
                    "unique_path",
                    file_version.name
                );
                inserted += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            return (deleted, inserted);
        }

        /// <param name="GetFileInfo">
        /// <para>in: relative path, case-insensitive (ok for windows system)</para>
        /// <para>out: a <c>FileInfo</c> object for the file or <c>null</c> if there is an error</para>
        /// </param>
        /// <param name="HashFileAsync">
        /// <para>in: relative path, case-insensitive (ok for windows system)</para>
        /// <para>out: md5 in lower-case or <c>null</c> if non-existent or denied access</para>
        /// <remarks>Block the calling thread to stop the next file updating.</remarks>
        /// </param>
        /// <param name="canGuess">
        /// <para><c>true</c>: Hash only files which cannot be guessed to be an entry in the server database.</para>
        /// <para><c>false</c>: Hash every file whose size is at least in one entry in the server database.</para>
        /// </param>
        /// <param name="hashAfter">
        /// When <paramref name="canGuess"/> is <c>true</c>/<c>false</c>, also/only hash files last modified later than the time (inclusive, in seconds).
        /// </param>
        /// <exception cref="InvalidOperationException"/>
        //Use actual size, version and hash of entry if possible, and actual hash if necessary to best guess which version in the server db the file is
        //Entries with missing or unknown files will be deleted, but entries with files that can't be accessed are unchanged
        public async Task<(int deleted, int modified, bool someAccessesDenied)> FixFileInfosAsync(Func<string, FileInfo?> GetFileInfo, Func<string, CancellationToken, Task<string?>> HashFileAsync, CancellationToken cancelToken, IProgress<float>? progress = null, bool canGuess = false, DateTime? hashAfter = null)
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            int deleted = 0, modified = 0;
            bool someAccessesDenied = false;
            PeriodicProgressReporter? progressReporter = null;
            if (progress is not null)
                progressReporter = new(0.5, progress);
            int totalCount, doneCount;
            SqliteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                progressReporter?.StartNextStage(0.05f); //0~5%
                //Get size and last modified time for all entries
                await pending_changes.DeleteAsync().ConfigureAwait(false);
                string sqlAll = SQL.Row.Select(
                    file_version.name,
                    null,
                    "unique_path"
                );
                dbCommand.CommandText = SQL.Row.CountQuery(sqlAll);
                totalCount = Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                doneCount = 0;
                await using (SqliteTransaction transaction = dbConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    dbCommand.Transaction = transaction;

                    dbCommand.CommandText = sqlAll;
                    DbDataReader reader = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                    SqliteCommand dbCommand_size_lastModified = new(
                        SQL.Row.Replace(
                            pending_changes.name,
                            "unique_path, size, last_modified",
                            "@unique_path, @size, @last_modified"
                        ),
                        dbConnection,
                        transaction
                    );
                    await using (reader.ConfigureAwait(false))
                    await using (dbCommand_size_lastModified.ConfigureAwait(false))
                    {
                        foreach (IDataRecord row in reader)
                        {
                            if (cancelToken.IsCancellationRequested)
                                break;

                            string unique_path = Convert.ToString(row["unique_path"]);
                            FileInfo? fileInfo = GetFileInfo(unique_path);
                            if (fileInfo is null) //skip when file cannot be accessed
                            {
                                someAccessesDenied = true;
                                continue;
                            }

                            dbCommand_size_lastModified.Parameters.AddWithValue("@unique_path", unique_path);
                            dbCommand_size_lastModified.Parameters.AddWithValue("@size", !fileInfo.Exists ? DBNull.Value : fileInfo.Length);
                            if (hashAfter is null)
                                dbCommand_size_lastModified.Parameters.AddWithValue("@last_modified", DBNull.Value);
                            else
                                dbCommand_size_lastModified.Parameters.AddWithValue("@last_modified", !fileInfo.Exists ? DBNull.Value : (int)(fileInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
                            await dbCommand_size_lastModified.ExecuteNonQueryAsync().ConfigureAwait(false);
                            dbCommand_size_lastModified.Parameters.Clear();
                            if (progressReporter is not null)
                                progressReporter.StageRatio = (float)++doneCount / totalCount;
                        }
                    }

                    transaction.Commit();
                    dbCommand.Transaction = null;
                }

                if (cancelToken.IsCancellationRequested)
                {
                    await pending_changes.DeleteAsync().ConfigureAwait(false);
                    return (0, 0, someAccessesDenied);
                }

                progressReporter?.StartNextStage(0.01f); //5~6%
                //Write sizes back
                dbCommand.CommandText = SQL.Row.ReplaceQuery(
                    file_version.name,
                    "unique_path, version, size, hash, property",
                    SQL.Row.Join(
                        pending_changes.name,
                        file_version.name,
                        "left.unique_path = right.unique_path",
                        null,
                        "right.unique_path, right.version, left.size, right.hash, right.property"
                    )
                );
                await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                //Delete entries with missing files
                deleted += await file_version.DeleteAsync("size is null").ConfigureAwait(false);
                await pending_changes.DeleteAsync($"size is null").ConfigureAwait(false);
                //Keep only entries to be hashed in 'pending_changes'
                if (hashAfter is not null)
                    await pending_changes.DeleteAsync($"last_modified < {(int)((DateTime)hashAfter - DateTime.UnixEpoch).TotalSeconds}").ConfigureAwait(false);
                else if(canGuess)
                    await pending_changes.DeleteAsync().ConfigureAwait(false);

                progressReporter?.StartNextStage(0.01f); //6~7%
                //Delete entries that don't match any size in server db
                string sqlServerHasNoSize = SQL.Row.LeftJoin(
                    file_version.name,
                    fileInfo.name,
                    "left.unique_path = right.unique_path and left.size = right.size",
                    "right.unique_path is null",
                    "distinct left.unique_path"
                );
                dbCommand.CommandText = SQL.Row.DeleteInQuery(
                    file_version.name,
                    "unique_path",
                    sqlServerHasNoSize
                );
                deleted += await dbCommand.ExecuteNonQueryAsync();
                dbCommand.CommandText = SQL.Row.DeleteInQuery(
                    pending_changes.name,
                    "unique_path",
                    sqlServerHasNoSize
                );
                await dbCommand.ExecuteNonQueryAsync();

                if (canGuess)
                {
                    /*
                    size matched, version/hash in the same entry matched in server db:
                    T/T: both correct, unchanged
                    T/F and F/T: ambiguous, calculate the file hash
                    T/F and no _/T: version correct and hash wrong, replace the wrong hash (only one hash for one version)
                    F/T and no T/_: hash correct and version wrong, fill in the max version (there may be multiple versions with the same hash)
                    only size matched, number of entries:
                    1: fill in the version and hash
                    2+: ambiguous, calculate the file hash
                    no size matched: delete entry

                    Calculate the file hash:
                    hash matched in server db: fill in the version
                    no such hash in server db: delete entry
                    */

                    string sqlServerHasVersion = SQL.Row.Join(
                        file_version.name,
                        fileInfo.name,
                        @"left.unique_path = right.unique_path and left.size = right.size
                            and left.version = right.version",
                        null,
                        "distinct left.unique_path"
                    );
                    string sqlServerHasHash = SQL.Row.Join(
                        file_version.name,
                        fileInfo.name,
                        @"left.unique_path = right.unique_path and left.size = right.size
                            and left.hash = right.hash",
                        null,
                        "distinct left.unique_path"
                    );
                    string sqlServerHasOnlyVersion = SQL.Row.Join(
                        file_version.name,
                        fileInfo.name,
                        @"left.unique_path = right.unique_path and left.size = right.size
                            and left.version = right.version and (left.hash is null or left.hash <> right.hash)",
                        null,
                        "distinct left.unique_path"
                    );
                    string sqlServerHasOnlyHash = SQL.Row.Join(
                        file_version.name,
                        fileInfo.name,
                        @"left.unique_path = right.unique_path and left.size = right.size
                            and (left.version is null or left.version <> right.version) and left.hash = right.hash",
                        null,
                        "distinct left.unique_path"
                    );

                    //size matched, version/hash in the same entry matched in server db:

                    //T/F and F/T: ambiguous, calculate the file hash
                    string sqlServerHasOnlyVersionAndHasOnlyHash = SQL.Row.QueryJoinQuery(
                        sqlServerHasOnlyVersion,
                        sqlServerHasOnlyHash,
                        "left.unique_path = right.unique_path",
                        null,
                        "left.unique_path"
                    );
                    if (!cancelToken.IsCancellationRequested)
                    {
                        dbCommand.CommandText = SQL.Row.ReplaceQuery(
                            pending_changes.name,
                            "unique_path",
                            sqlServerHasOnlyVersionAndHasOnlyHash
                        );
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    progressReporter?.StartNextStage(0.01f); //7~8%
                    //T/F and no _/T: version correct and hash wrong, replace the wrong hash (only one hash for one version)
                    string sqlServerHasOnlyVersionAndHasNoHash = SQL.Row.SelectInQueryButInQuery(
                        "unique_path",
                        sqlServerHasOnlyVersion,
                        sqlServerHasHash
                    );
                    if (!cancelToken.IsCancellationRequested)
                    {
                        dbCommand.CommandText = SQL.Row.ReplaceQuery(
                            pending_changes.name,
                            "unique_path, version, size, hash, property",
                            SQL.Row.JoinQuery(
                                fileInfo.name,
                                SQL.Row.JoinQuery(
                                    file_version.name,
                                    sqlServerHasOnlyVersionAndHasNoHash,
                                    "left.unique_path = right.unique_path",
                                    null,
                                    "left.unique_path, left.version"
                                ),
                                "left.unique_path = right.unique_path and left.version = right.version",
                                null,
                                "right.unique_path, right.version, left.size, left.hash, left.property"
                            )
                        );
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    progressReporter?.StartNextStage(0.01f); //8~9%
                    //F/T and no T/_: hash correct and version wrong, fill in the max version (there may be multiple versions with the same hash)
                    string sqlServerHasOnlyHashAndHasNoVersion = SQL.Row.SelectInQueryButInQuery(
                        "unique_path",
                        sqlServerHasOnlyHash,
                        sqlServerHasVersion
                    );
                    if (!cancelToken.IsCancellationRequested)
                    {
                        dbCommand.CommandText = SQL.Row.ReplaceQuery(
                            pending_changes.name,
                            "unique_path, version, size, hash, property",
                            SQL.Row.GroupQuery(
                                SQL.Row.OrderQuery(
                                    SQL.Row.JoinQuery(
                                        fileInfo.name,
                                        SQL.Row.JoinQuery(
                                            file_version.name,
                                            sqlServerHasOnlyHashAndHasNoVersion,
                                            "left.unique_path = right.unique_path",
                                            null,
                                            "left.unique_path, left.hash"
                                        ),
                                        "left.unique_path = right.unique_path and left.hash = right.hash",
                                        null,
                                        "right.unique_path, left.version, left.size, right.hash, left.property"
                                    ),
                                    "version",
                                    SQL.OrderDirection.Descending
                                ),
                                "unique_path"
                            )
                        );
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    //only size matched, number of entries:
                    string sqlServerHasNoVersionAndHasNoHash = SQL.Row.SelectInButInQuery(
                        "unique_path",
                        file_version.name,
                        SQL.Row.Union(
                            sqlServerHasVersion,
                            sqlServerHasHash
                        )
                    );

                    progressReporter?.StartNextStage(0.01f); //9~10%
                    //1: fill in the version and hash
                    string sqlServerHasNoVersionAndHasNoHashAndHasOneSize = SQL.Row.SelectQuery(
                        SQL.Row.GroupQuery(
                            SQL.Row.JoinQuery(
                                fileInfo.name,
                                SQL.Row.JoinQuery(
                                    file_version.name,
                                    sqlServerHasNoVersionAndHasNoHash,
                                    "left.unique_path = right.unique_path",
                                    null,
                                    "left.unique_path, left.size"
                                ),
                                "left.unique_path = right.unique_path and left.size = right.size"
                            ),
                            "unique_path",
                            "count(*) = 1"
                        ),
                        "unique_path"
                    );
                    if (!cancelToken.IsCancellationRequested)
                    {
                        dbCommand.CommandText = SQL.Row.ReplaceQuery(
                            pending_changes.name,
                            "unique_path, version, size, hash, property",
                            SQL.Row.JoinQuery(
                                fileInfo.name,
                                SQL.Row.JoinQuery(
                                    file_version.name,
                                    sqlServerHasNoVersionAndHasNoHashAndHasOneSize,
                                    "left.unique_path = right.unique_path",
                                    null,
                                    "left.unique_path, left.size"
                                ),
                                "left.unique_path = right.unique_path and left.size = right.size",
                                null,
                                "right.unique_path, left.version, right.size, left.hash, left.property"
                            )
                        );
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    //2+: ambiguous, calculate the file hash
                    string sqlServerHasNoVersionAndHasNoHashAndHasManySize = SQL.Row.SelectQuery(
                        SQL.Row.GroupQuery(
                            SQL.Row.JoinQuery(
                                fileInfo.name,
                                SQL.Row.JoinQuery(
                                    file_version.name,
                                    sqlServerHasNoVersionAndHasNoHash,
                                    "left.unique_path = right.unique_path",
                                    null,
                                    "left.unique_path, left.size"
                                ),
                                "left.unique_path = right.unique_path and left.size = right.size"
                            ),
                            "unique_path",
                            "count(*) > 1"
                        ),
                        "unique_path"
                    );
                    if (!cancelToken.IsCancellationRequested)
                    {
                        dbCommand.CommandText = SQL.Row.ReplaceQuery(
                            pending_changes.name,
                            "unique_path",
                            sqlServerHasNoVersionAndHasNoHashAndHasManySize
                        );
                        await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                else
                    progressReporter?.StartNextStage(0.03f); //7~10%

                //here, every row in 'pending_changes' has
                //1. all columns (except 'last_modified') not null (for simple update)
                //2. 'hash' null (for hashing)

                progressReporter?.StartNextStage(0.85f); //10~95%
                //Calculate the file hash:
                uint hashingCount = 0;
                ConcurrentExclusiveSchedulerPair schedulerPair = new(TaskScheduler.Default);
                string sqlAllPending = SQL.Row.Select(
                    pending_changes.name,
                    "hash is null"
                );
                dbCommand.CommandText = SQL.Row.CountQuery(sqlAllPending);
                totalCount = Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                doneCount = 0;
                await using (SqliteTransaction transaction = dbConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    dbCommand.Transaction = transaction;

                    dbCommand.CommandText = sqlAllPending;
                    DbDataReader reader_hash = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                    SqliteCommand dbCommand_hash = new(
                        SQL.Row.Replace(
                            pending_changes.name,
                            "unique_path, hash",
                            "@unique_path, @hash"
                        ),
                        dbConnection,
                        transaction
                    );
                    await using (reader_hash.ConfigureAwait(false))
                    await using (dbCommand_hash.ConfigureAwait(false))
                    {
                        foreach (IDataRecord row in reader_hash)
                        {
                            if (cancelToken.IsCancellationRequested)
                                break;

                            string unique_path = Convert.ToString(row["unique_path"]);
                            Interlocked.Increment(ref hashingCount);
#pragma warning disable CS4014
                            HashFileAsync(unique_path, cancelToken)
                            .ContinueWith(async (preTask) => {
                                try
                                {
                                    string? hash = preTask.Result;
                                    if (hash is not null)
                                    {
                                        dbCommand_hash.Parameters.AddWithValue("@unique_path", unique_path);
                                        dbCommand_hash.Parameters.AddWithValue("@hash", hash);
                                        await dbCommand_hash.ExecuteNonQueryAsync().ConfigureAwait(false);
                                        dbCommand_hash.Parameters.Clear();
                                    }
                                    else
                                        someAccessesDenied = true;
                                }
                                finally
                                {
                                    if (progressReporter is not null)
                                        progressReporter.StageRatio = (float)Interlocked.Increment(ref doneCount) / totalCount;
                                    Interlocked.Decrement(ref hashingCount);
                                }
                            }, schedulerPair.ExclusiveScheduler);
#pragma warning restore CS4014
                        }
                    }

                    while (hashingCount > 0)
                        await Task.Delay(500).ConfigureAwait(false);

                    transaction.Commit();
                    dbCommand.Transaction = null;
                }

                //here, every row in 'pending_changes' has
                //1. all columns (except 'last_modified') not null (for simple update)
                //2. 'hash' null (from canceled or failed hashing)
                //3. only 'unique_path' and 'hash' not null (from finished hashing)

                progressReporter?.StartNextStage(0.02f); //95~97%
                //hash matched in server db: fill in the rest
                dbCommand.CommandText = SQL.Row.ReplaceQuery(
                    pending_changes.name,
                    "unique_path, version, size, hash, property", 
                    SQL.Row.JoinQuery(
                        fileInfo.name,
                        SQL.Row.Select(
                            pending_changes.name,
                            "hash is not null and (version is null or size is null or property is null)"
                        ),
                        "left.unique_path = right.unique_path and left.hash = right.hash",
                        null,
                        "right.unique_path, left.version, left.size, right.hash, left.property"
                    )
                );
                await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                //here, every row in 'pending_changes' has
                //1. all columns (except 'last_modified') not null (for simple update and matched hash)
                //2. 'hash' null (from canceled or failed hashing)
                //3. only 'unique_path' and 'hash' not null (for unmatched hash)

                progressReporter?.StartNextStage(0.01f); //97~98%
                //no such hash in server db: delete entry
                dbCommand.CommandText = SQL.Row.DeleteInQuery(
                    file_version.name,
                    "unique_path",
                    SQL.Row.Select(
                        pending_changes.name,
                        "hash is not null and (version is null or size is null or property is null)"
                    )
                );
                deleted += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                progressReporter?.StartNextStage(0.02f); //98~100%
                //Write back fill-ins
                dbCommand.CommandText = SQL.Row.ReplaceQuery(
                    file_version.name,
                    "unique_path, version, size, hash, property",
                    //only rows with any column changed,
                    //because in full mode, files with a correct row will still be hashed
                    SQL.Row.QueryLeftJoin(
                        SQL.Row.Select(
                            pending_changes.name,
                            "unique_path is not null and version is not null and size is not null and hash is not null and property is not null",
                            "unique_path, version, size, hash, property"
                        ),
                        file_version.name,
                        "left.unique_path = right.unique_path and left.version = right.version and left.size = right.size and left.hash = right.hash and left.property = right.property",
                        "right.unique_path is null",
                        "left.unique_path, left.version, left.size, left.hash, left.property"
                    )
                );
                modified += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                progressReporter?.StartNextStage(0); //100%
                await pending_changes.DeleteAsync().ConfigureAwait(false);
            }
            progressReporter?.Dispose();
            return (deleted, modified, someAccessesDenied);
        }
    }
}

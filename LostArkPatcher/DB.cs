using Microsoft.VisualBasic;
using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace LostArkPatcher
{
    internal partial class DB : IDisposable, IAsyncDisposable
    {
        private partial class Table(SQLiteConnection dbConnection, string name, string? referentialSchema = null) : IDisposable, IAsyncDisposable
        {
            private readonly SQLiteCommand dbCommand = dbConnection.CreateCommand();
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

        private readonly SQLiteConnection dbConnection;

        //sever
        private readonly Table? file_size = null;
        private readonly Table? fileInfo = null;

        //local
        private readonly Table local_info;
        private readonly Table file_version;

        //in-memory
        private readonly Table pending_changes;

        public DB(string path_localDB, string? path_serverDB = null)
        {
            dbConnection = new($"Data Source={path_localDB}");
            dbConnection.Open();
            using SQLiteCommand dbCommand = dbConnection.CreateCommand();

            if (path_serverDB is not null)
            {
                const string TEMP_SERVERDB = "serverdb";
                const string TEMP_INFOVERSION = "file_infoversion";

                dbCommand.CommandText = $"ATTACH DATABASE @path AS {TEMP_SERVERDB}";
                dbCommand.Parameters.AddWithValue("@path", path_serverDB);
                dbCommand.ExecuteNonQuery();

                file_size = new(dbConnection, $"{TEMP_SERVERDB}.file_size");

                dbCommand.CommandText = SQL.Table.CreateTempAsQuery(
                    SQL.Row.Join(
                        $"{TEMP_SERVERDB}.file_info",
                        $"{TEMP_SERVERDB}.file_version",
                        "left.id = right.id",
                        null,
                        "left.id, unique_path, path, version, size, hash, property"
                    ),
                    TEMP_INFOVERSION
                );
                dbCommand.ExecuteNonQuery();
                fileInfo = new(dbConnection, TEMP_INFOVERSION);
            }

            local_info = new(dbConnection, "local_info", "key text primary key, value integer");
            file_version = new(dbConnection, "file_version", "unique_path text primary key, version integer, size integer, hash text, property integer");

            const string TEMP_INMENORYDB = "inmemorydb";
            const string TEMP_PENDINGCHANGES = "pending_changes";
            dbCommand.CommandText = $"ATTACH DATABASE ':memory:' AS {TEMP_INMENORYDB}";
            dbCommand.ExecuteNonQuery();
            pending_changes = new(dbConnection, $"{TEMP_INMENORYDB}.{TEMP_PENDINGCHANGES}", @"
                unique_path text primary key,
                version integer,
                size integer,
                hash text,
                property integer
            ");
            pending_changes.CreateAsync().Wait();
        }

        public void Dispose()
        {
            file_size?.Dispose();
            fileInfo?.Dispose();
            local_info.Dispose();
            file_version.Dispose();
            pending_changes.Dispose();
            dbConnection.Close();
            dbConnection.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if(file_size is not null)
                await file_size.DisposeAsync().ConfigureAwait(false);
            if (fileInfo is not null)
                await fileInfo.DisposeAsync().ConfigureAwait(false);
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
            if (version is null || DBNull.Value.Equals(version))
                return null;
            else
                return Convert.ToInt32(version);
        }

        public async Task FixVersionAsync()
        {
            object? _version = await file_version.SelectScalarAsync(column: "max(version)").ConfigureAwait(false);
            if(_version != DBNull.Value)
                await local_info.ReplaceAsync("key, value", $"'version', {Convert.ToInt32(_version)}").ConfigureAwait(false);
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

        /// <exception cref="InvalidOperationException"/>
        public async Task<int> GetOutdatedCountAsync()
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            SQLiteCommand dbCommand = dbConnection.CreateCommand();
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
            private float stage = 0;
            private float stageRatio = 0;
            readonly Timer timer;

            public PeriodicProgressReporter(double intervalSeconds, IProgress<float> progress)
            {
                timer = new((_) => {
                    progress.Report(previous + stageRatio * stage);
                }, null, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
            }

            public void Dispose()
            {
                timer.Dispose();
            }

            public void SetStageTarget(float previous, float stage)
            {
                Interlocked.Exchange(ref this.stageRatio, 0);
                Interlocked.Exchange(ref this.stage, stage);
                Interlocked.Exchange(ref this.previous, previous);
            }

            public void SetStageRatio(float stageRatio)
            {
                Interlocked.Exchange(ref this.stageRatio, stageRatio);
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
            SQLiteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                progressReporter?.SetStageTarget(0f, 1f);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
                uint updatingCount = 0;

                SQLiteCommand dbCommand_delta = new(
                    SQL.Row.Select(
                        file_size.name,
                        "id = @id and org_ver = @org_ver",
                        "max(new_ver)"
                    ),
                    dbConnection
                );
                SQLiteCommand dbCommand_after = new(
                    SQL.Row.Select(
                        fileInfo.name,
                        "id = @id and version = @version",
                        "size, hash"
                    ),
                    dbConnection
                );
                SQLiteCommand dbCommand_update = new(
                    SQL.Row.Replace(
                        pending_changes.name,
                        "unique_path, version, size, hash, property",
                        "@unique_path, @version, @size, @hash, @property"
                    ),
                    dbConnection
                );

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
                dbCommand.CommandText = sqlUpdatableFiles;
                DbDataReader reader_updatable = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader_updatable.ConfigureAwait(false))
                await using (dbCommand_delta.ConfigureAwait(false))
                await using (dbCommand_after.ConfigureAwait(false))
                await using (dbCommand_update.ConfigureAwait(false))
                {
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
                            ++failed;
                        else
                        {
                            ++updatingCount;
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
                                        ++failed;
                                    else
                                    {
                                        dbCommand_after.Parameters.AddWithValue("@id", id);
                                        dbCommand_after.Parameters.AddWithValue("@version", afterVersion);
                                        DbDataReader reader_after = await dbCommand_after.ExecuteReaderAsync().ConfigureAwait(false);
                                        using (reader_after)
                                            if (!await reader_after.ReadAsync().ConfigureAwait(false))
                                                ++failed;
                                            else
                                            {
                                                dbCommand_update.Parameters.AddWithValue("@unique_path", unique_path);
                                                dbCommand_update.Parameters.AddWithValue("@version", afterVersion);
                                                dbCommand_update.Parameters.AddWithValue("@size", Convert.ToInt32(reader_after["size"]));
                                                dbCommand_update.Parameters.AddWithValue("@hash", Convert.ToString(reader_after["hash"]));
                                                dbCommand_update.Parameters.AddWithValue("@property", property);
                                                await dbCommand_update.ExecuteNonQueryAsync().ConfigureAwait(false);

                                                if (afterVersion == sequence.Last().toVersion)
                                                    ++updated;
                                                else
                                                    ++failed;
                                            }
                                    }
                                }
                                finally
                                {
                                    progressReporter?.SetStageRatio((float)++doneCount / totalCount);
                                    --updatingCount;
                                }
                            }, TaskContinuationOptions.ExecuteSynchronously);
#pragma warning restore CS4014
                        }
                    }

                    while (updatingCount > 0)
                        await Task.Delay(500).ConfigureAwait(false);
                }
                progressReporter?.SetStageTarget(1f, 0f);

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
        //Entries not in  server DB or with missing files will be deleted, but entries with corrupted files will be unchanged
        public async Task<(int deleted, int inserted)> FixFileKeysAsync(Func<string, bool> FileExists, CancellationToken cancelToken, IProgress<float>? progress = null)
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            int deleted = 0, inserted = 0;
            PeriodicProgressReporter? progressReporter = null;
            if (progress is not null)
                progressReporter = new(0.5, progress);
            SQLiteCommand dbCommand = dbConnection.CreateCommand();
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

                progressReporter?.SetStageTarget(0.1f, 0.9f);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
                string sqlAllPaths = SQL.Row.Select(
                    file_version.name,
                    null,
                    "unique_path"
                );
                dbCommand.CommandText = SQL.Row.CountQuery(sqlAllPaths);
                int totalCount = Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                int doneCount = 0;
                dbCommand.CommandText = sqlAllPaths;
                DbDataReader reader = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    SQLiteCommand dbCommand_update = new(
                        SQL.Row.Replace(
                            pending_changes.name,
                            "unique_path",
                            "@unique_path"
                        ),
                        dbConnection
                    );
                    await using (dbCommand_update.ConfigureAwait(false))
                        foreach (IDataRecord row in reader)
                        {
                            if (cancelToken.IsCancellationRequested)
                                break;

                            string unique_path = Convert.ToString(row["unique_path"]);
                            if (!FileExists(unique_path))
                            {
                                dbCommand_update.Parameters.AddWithValue("@unique_path", unique_path);
                                await dbCommand_update.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                            progressReporter?.SetStageRatio((float)++doneCount / totalCount);
                        }
                }
                dbCommand.CommandText = SQL.Row.DeleteInQuery(
                    file_version.name,
                    "unique_path",
                    SQL.Row.Select(
                        pending_changes.name,
                        null,
                        "unique_path"
                    )
                );
                deleted = await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
            }
            progressReporter?.Dispose();
            return (deleted, inserted);
        }

        /// <param name="GetFileSize">
        /// <para>in: relative path, case-insensitive (ok for windows system)</para>
        /// <para>out: size in bytes, or negative number if non-existent</para>
        /// </param>
        /// <param name="HashFileAsync">
        /// <para>in: relative path, case-insensitive (ok for windows system)</para>
        /// <para>out: md5 in lower-case or <c>null</c> if non-existent</para>
        /// </param>
        /// <param name="noGuessing">
        /// <para>true: Hash every file whose size is at least in one entry in the server database.</para>
        /// <para>false: Hash only files which cannot be guessed to be an entry in the server database.</para>
        /// </param>
        /// <exception cref="InvalidOperationException"/>
        //Use actual size, version and hash of entry if possible, and actual hash if necessary to best guess which entry in the server db the file is
        public async Task<(int deleted, int modified)> FixFileInfosAsync(Func<string, long> GetFileSize, Func<string, CancellationToken, Task<string?>> HashFileAsync, CancellationToken cancelToken, IProgress<float>? progress = null, bool noGuessing = false)
        {
            if (fileInfo is null)
                throw new InvalidOperationException("Server DB is not attched.");

            int deleted = 0, modified = 0;
            PeriodicProgressReporter? progressReporter = null;
            if (progress is not null)
                progressReporter = new(0.5, progress);
            int totalCount, doneCount;
            SQLiteCommand dbCommand = dbConnection.CreateCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                //Update size in all entries
                progressReporter?.SetStageTarget(0, 0.05f);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
                SQLiteCommand dbCommand_size = new(
                    SQL.Row.Replace(
                        pending_changes.name,
                        "unique_path, size",
                        "@unique_path, @size"
                    ),
                    dbConnection
                );
                string sqlAllSizes = SQL.Row.Select(
                    file_version.name,
                    null,
                    "unique_path, size"
                );
                dbCommand.CommandText = SQL.Row.CountQuery(sqlAllSizes);
                totalCount = Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                doneCount = 0;
                dbCommand.CommandText = sqlAllSizes;
                DbDataReader reader_size = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader_size.ConfigureAwait(false))
                await using (dbCommand_size.ConfigureAwait(false))
                {
                    foreach (IDataRecord row in reader_size)
                    {
                        if (cancelToken.IsCancellationRequested)
                            break;

                        string unique_path = Convert.ToString(row["unique_path"]);
                        object _size = row["size"];
                        int? size = _size == DBNull.Value ? null : Convert.ToInt32(_size);

                        long diskSize = GetFileSize(unique_path);
                        if (diskSize < 0)
                            continue;
                        if (size != diskSize)
                        {
                            dbCommand_size.Parameters.AddWithValue("@unique_path", unique_path);
                            dbCommand_size.Parameters.AddWithValue("@size", diskSize < 0 ? null : diskSize);
                            await dbCommand_size.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        progressReporter?.SetStageRatio((float)++doneCount / totalCount);
                    }
                }
                if (cancelToken.IsCancellationRequested)
                {
                    await pending_changes.DeleteAsync().ConfigureAwait(false);
                    return (0, 0);
                }
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
                progressReporter?.SetStageTarget(0.05f, 0.05f);

                if (noGuessing)
                {
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
                    await dbCommand.ExecuteNonQueryAsync();
                    //Calculate file hash for the rest
                    dbCommand.CommandText = SQL.Row.ReplaceQuery(
                        pending_changes.name,
                        "unique_path",
                        SQL.Row.Select(
                            file_version.name,
                            null,
                            "unique_path"
                        )
                    );
                    await dbCommand.ExecuteNonQueryAsync();
                    progressReporter?.SetStageTarget(0.1f, 0.85f);
                }
                else
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
                    await pending_changes.DeleteAsync().ConfigureAwait(false);

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
                        progressReporter?.SetStageTarget(0.06f, 0.01f);
                    }

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
                        progressReporter?.SetStageTarget(0.07f, 0.01f);
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
                        progressReporter?.SetStageTarget(0.08f, 0.02f);
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

                    //no size matched: delete entry
                    string sqlServerHasNoVersionAndHasNoHashAndHasNoSize = SQL.Row.SelectInQueryButInQuery(
                        "unique_path",
                        sqlServerHasNoVersionAndHasNoHash,
                        SQL.Row.Union(
                            sqlServerHasNoVersionAndHasNoHashAndHasOneSize,
                            sqlServerHasNoVersionAndHasNoHashAndHasManySize
                        )
                    );
                    if (!cancelToken.IsCancellationRequested)
                    {
                        dbCommand.CommandText = SQL.Row.DeleteInQuery(
                            file_version.name,
                            "unique_path",
                            sqlServerHasNoVersionAndHasNoHashAndHasNoSize
                        );
                        deleted += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                        progressReporter?.SetStageTarget(0.1f, 0.85f);
                    }
                }

                //Calculate the file hash:
                ManualResetEvent next = new(true);
                uint hashingCount = 0;
                SQLiteCommand dbCommand_hash = new(
                    SQL.Row.Replace(
                        pending_changes.name,
                        "unique_path, hash",
                        "@unique_path, @hash"
                    ),
                    dbConnection
                );
                string sqlAllPending = SQL.Row.Select(
                    pending_changes.name,
                    "hash is null"
                );
                dbCommand.CommandText = SQL.Row.CountQuery(sqlAllPending);
                totalCount = Convert.ToInt32(await dbCommand.ExecuteScalarAsync().ConfigureAwait(false));
                doneCount = 0;
                dbCommand.CommandText = sqlAllPending;
                DbDataReader reader_hash = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader_hash.ConfigureAwait(false))
                await using (dbCommand_hash.ConfigureAwait(false))
                {
                    foreach (IDataRecord row in reader_hash)
                    {
                        if (cancelToken.IsCancellationRequested)
                            break;

                        string unique_path = Convert.ToString(row["unique_path"]);
                        ++hashingCount;
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
                                }
                            }
                            finally
                            {
                                progressReporter?.SetStageRatio((float)++doneCount / totalCount);
                                --hashingCount;
                            }
                        }, TaskContinuationOptions.ExecuteSynchronously);
#pragma warning restore CS4014
                    }

                    while (hashingCount > 0)
                        await Task.Delay(500).ConfigureAwait(false);
                }
                progressReporter?.SetStageTarget(0.95f, 0.01f);

                //hash matched in server db: fill in the version
                dbCommand.CommandText = SQL.Row.ReplaceQuery(
                    pending_changes.name,
                    "unique_path, version, size, hash, property", 
                    SQL.Row.JoinQuery(
                        fileInfo.name,
                        SQL.Row.Select(
                            pending_changes.name,
                            "version is null"
                        ),
                        "left.unique_path = right.unique_path and left.hash = right.hash",
                        null,
                        "right.unique_path, left.version, left.size, right.hash, left.property"
                    )
                );
                await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                progressReporter?.SetStageTarget(0.96f, 0.01f);

                //no such hash in server db: delete entry
                dbCommand.CommandText = SQL.Row.DeleteInQuery(
                    file_version.name,
                    "unique_path",
                    SQL.Row.Select(
                        pending_changes.name,
                        "version is null and hash is not null"
                    )
                );
                deleted += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                await pending_changes.DeleteAsync("version is null").ConfigureAwait(false);
                progressReporter?.SetStageTarget(0.98f, 0.02f);

                //Write back fill-ins
                dbCommand.CommandText = SQL.Row.ReplaceQuery(
                    file_version.name,
                    "unique_path, version, size, hash, property",
                    SQL.Row.Select(
                        pending_changes.name,
                        "unique_path is not null and version is not null and size is not null and hash is not null and property is not null",
                        "unique_path, version, size, hash, property"
                    )
                );
                modified += await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                await pending_changes.DeleteAsync().ConfigureAwait(false);
                progressReporter?.SetStageTarget(1f, 0f);
            }
            progressReporter?.Dispose();
            return (deleted, modified);
        }
    }
}

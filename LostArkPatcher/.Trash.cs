using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Data.SqlTypes;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LostArkPatcher
{
    public partial class MainWindow : Window
    {
        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (model.operationInProgress)
            {
                model.cancellationTokenSource?.Cancel();
                UpdateButton.IsEnabled = false;
                UpdateButton.Content = "正在中斷...";
                return;
            }
            if (!CheckRunningApps()) return;

            model.operationInProgress = true;
            model.cancellationTokenSource = new();
            SpecifyLocationButton.IsEnabled = false;
            UpdateButton.Content = "中斷";
            RepairButton.IsEnabled = false;
            Log_Clear();

            try
            {
                //Launcher
                if (!File.Exists(model.GameDirectory + LAUNCHER_FILENAME))
                    Log_AddLine("找不到啟動器，請先修復");
                else if (model.LauncherCurrentVersion is not null && model.LauncherCurrentVersion == model.LauncherLatestVersion)
                    Log_AddLine("啟動器已是最新");
                else
                {
                    Log_AddLine("更新啟動器...");
                    Log_ReportFinished(await Task.Run(() => FS.DownloadLauncherAsync(model.GameDirectory, model.cancellationTokenSource.Token)));
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                }
                //Game files
                if (!File.Exists(model.GameDirectory + LOCALDB_FILENAME))
                    Log_AddLine("找不到本地資料庫，請先修復");
                else
                {
                    Log_AddLine("下載伺服器資料庫...");
                    string? serverDBPath = await Task.Run(() => FS.DownloadServerDBAsync(model.cancellationTokenSource.Token));
                    if (serverDBPath is null)
                    {
                        Log_ReportFinished(false);
                        model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log_ReportFinished(true);
                        model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                        using DB db = new(model.GameDirectory + LOCALDB_FILENAME, serverDBPath);
                        if (!await db.CheckSchemaAsync() || !await db.CheckFileVersionsAsync())
                            Log_AddLine("本地資料庫損毀，請先修復");
                        else
                        {
                            model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                            Progress<float> progress = new();
                            progress.ProgressChanged += (sender, e) =>
                            {
                                Log_UpdateProgress(e);
                            };
                            Log_AddLine("更新所有檔案...");
                            (int updated, int failed) = await Task.Run(() => db.UpdateGameFilesAsync(model.GameDirectory, model.cancellationTokenSource.Token, progress));
                            Log_ReportFinished(!model.cancellationTokenSource.Token.IsCancellationRequested);
                            //
                            if (updated > 0)
                                Log_AddLine($"已更新: {updated} 檔案");
                            if (failed > 0)
                                Log_AddLine($"無法更新: {failed} 檔案");
                            await db.FixVersionAsync();
                            model.GetCurrentVersions();
                            RefreshCurrentVersionLabels();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log_AddLine("已中斷");
            }

            FS.CleanUp();
            SpecifyLocationButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "更新";
            RepairButton.IsEnabled = true;
            model.operationInProgress = false;
        }

        private async void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            if (model.operationInProgress)
            {
                model.cancellationTokenSource?.Cancel();
                RepairButton.IsEnabled = false;
                RepairButton.Content = "正在中斷...";
                return;
            }
            if (!CheckGameDirectory()) return;
            if (!CheckRunningApps()) return;

            model.operationInProgress = true;
            model.cancellationTokenSource = new();
            SpecifyLocationButton.IsEnabled = false;
            UpdateButton.IsEnabled = false;
            RepairButton.Content = "中斷";
            Log_Clear();

            try
            {
                if (!File.Exists(model.GameDirectory + LAUNCHER_FILENAME))
                {
                    Log_AddLine("下載登入器...");
                    Log_ReportFinished(await Task.Run(() => FS.DownloadLauncherAsync(model.GameDirectory, model.cancellationTokenSource.Token)));
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                }
                Log_AddLine("下載伺服器資料庫...");
                string? serverDBPath = await Task.Run(() => FS.DownloadServerDBAsync(model.cancellationTokenSource.Token));
                if (serverDBPath is null)
                {
                    Log_ReportFinished(false);
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                }
                else
                {
                    Log_ReportFinished(true);
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    using DB db = new(model.GameDirectory + LOCALDB_FILENAME, serverDBPath);
                    Log_AddLine("檢查本地資料庫...");
                    await Task.Run(async () =>
                    {
                        await db.FixSchemaAsync();
                        await db.FixFileKeysAsync();
                        await db.FixFileVersionHashesAsync();
                    });
                    Log_ReportFinished(true);
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    Progress<float> progress = new();
                    progress.ProgressChanged += (sender, e) =>
                    {
                        Log_UpdateProgress(e);
                    };
                    Log_AddLine("檢查所有檔案大小...");
                    await Task.Run(() => db.CheckFileSizesAsync(model.GameDirectory, model.cancellationTokenSource.Token, progress));
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    Log_ReportFinished(true);
                    Log_AddLine("計算未知檔案雜湊...");
                    await Task.Run(() => db.FillInMissingValidFileHashesAsync(model.GameDirectory, model.cancellationTokenSource.Token, progress));
                    model.cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    Log_ReportFinished(true);
                    Log_AddLine("下載所有遺失檔案...");
                    (int fixes, int failed) = await Task.Run(() => db.DownloadMissingGameFilesAsync(model.GameDirectory, model.cancellationTokenSource.Token, progress));
                    Log_ReportFinished(!model.cancellationTokenSource.Token.IsCancellationRequested);
                    //
                    if (fixes > 0)
                        Log_AddLine($"已修復: {fixes} 檔案");
                    if (failed > 0)
                        Log_AddLine($"無法修復: {failed} 檔案");
                    await db.FixVersionAsync();
                    model.GetCurrentVersions();
                    RefreshCurrentVersionLabels();
                    //Remind for updating
                    int outdatedFilesCount = await db.GetOutdatedCountAsync();
                    if (outdatedFilesCount > 0)
                        Log_AddLine($"需要更新: {outdatedFilesCount} 檔案");
                }
            }
            catch (OperationCanceledException)
            {
                Log_AddLine("已中斷");
            }

            FS.CleanUp();
            SpecifyLocationButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
            RepairButton.IsEnabled = true;
            RepairButton.Content = "修復";
            model.operationInProgress = false;
        }
    }

    internal partial class DB : IDisposable, IAsyncDisposable
    {
        public async Task<(int deleted, int modified)> FixFileInfosAsync(Func<string, long> GetFileSize, AsyncFileHasher HashFileAsync, CancellationToken cancelToken, IProgress<float>? progress = null)
        {
            //By path and size, equal version/ equal hash / number of matched entries in server db:
            progressReporter?.SetStageTarget(0.1f, 0.8f);
            await pending_changes.DeleteAsync();
            string from_join = @$"
                from {file_version.name} as local
                join {fileInfo.name} as server
                on local.unique_path = server.unique_path and local.size = server.size
            ";
            string from_and = @$"
                {from_join}
                and local.version = server.version and local.hash = server.hash
            ";
            string from_xor = @$"
                {from_join}
                and (local.version = server.version or local.hash = server.hash)
                and (local.version is null or local.version <> server.version
                or local.hash is null or local.hash <> server.hash)
                group by local.unique_path
            ";
            string from_xor_1 = @$"
                {from_xor}
                having count() = 1
            ";
            string from_xor_2 = @$"
                {from_xor}
                having count() > 1
            ";
            string from_nor = @$"
                {from_join}
                and (local.version is null or local.version <> server.version)
                and (local.hash is null or local.hash <> server.hash)
                group by local.unique_path
            ";
            string from_nor_1 = @$"
                {from_nor}
                having count() = 1
            ";
            string from_nor_2 = @$"
                {from_nor}
                having count() > 1
            ";
            string select_path = "select local.unique_path";
            string select_all = "select local.unique_path, server.version, server.size, server.hash, server.property";
            //T/F/2 or F/T/2: conflicting matches, calculate the file hash
            dbCommand.CommandText = SQL.Row.ReplaceQuery(
                pending_changes.name,
                "unique_path",
                @$"
                    {select_path}
                    {from_xor_2}
                "
            );
            await dbCommand.ExecuteNonQueryAsync();
            //F/F/2+ (and none of the above): conflicting matches, calculate the file hash
            dbCommand.CommandText = SQL.Row.ReplaceQuery(
                pending_changes.name,
                "unique_path", 
                @$"
                    select unique_path
                    from (
                        {select_path}
                        {from_nor_2}
                    )
                    where unique_path not in (
                        {select_path}
                        {from_and}
                        union
                        {select_path}
                        {from_xor}
                    )
                "
            );
            await dbCommand.ExecuteNonQueryAsync();
            //Calculate the file hash:

            //By path and hash, matched entry in server db:
            //T: hash matched, fill in version
            dbCommand.CommandText = SQL.Row.ReplaceQuery(
                pending_changes.name,
                "unique_path, version, size, hash, property",
                SQL.Row.Join(
                    pending_changes.name,
                    fileInfo.name,
                    "left.unique_path = right.unique_path and left.hash = right.hash",
                    null,
                    "left.unique_path, right.version, right.size, right.hash, right.property"
                )
            );
            await dbCommand.ExecuteNonQueryAsync();
            //F: no such hash, delete entry
            deleted += await file_version.DeleteAsync(@$"
                unique_path in (
                    {SQL.Row.Select(
                        pending_changes.name,
                        "version is null",
                        "unique_path"
                    )}
                )
            ");
            await pending_changes.DeleteAsync("version is null");
            //T/F/1 or F/T/1: only one of version or hash matched, fill the wrong column
            dbCommand.CommandText = SQL.Row.ReplaceQuery(
                pending_changes.name,
                "unique_path, version, size, hash, property",
                @$"
                    {select_all}
                    {from_xor_1}
                "
            );
            await dbCommand.ExecuteNonQueryAsync();
            //F/F/1: only one size matched, fill in version and hash
            dbCommand.CommandText = SQL.Row.ReplaceQuery(
                pending_changes.name,
                "unique_path, version, size, hash, property",
                @$"
                    {select_all}
                    {from_nor_1}
                "
            );
            await dbCommand.ExecuteNonQueryAsync();
            //else: no such size, delete entry
            deleted += await file_version.DeleteAsync(@$"
                unique_path not in (
                    {select_path}
                    {from_and}
                    union
                    select unique_path
                    from {pending_changes.name}
                )
            ");
        }

        private const string SQL_FileVersion_BothVersionHashExist = @"
            select *
            from file_version
            where version is not null and hash is not null
        ";
        private const string SQL_FileVersion_OnlyVersionExists = @"
            select *
            from file_version
            where version is not null and hash is null
        ";
        private const string SQL_FileVersion_OnlyHashExists = @"
            select *
            from file_version
            where version is null and hash is not null
        ";
        private const string SQL_FileVersion_NeitherVersionHashExists = @"
            select *
            from file_version
            where version is null and hash is null
        ";

        private const string tempSubDir = @".patcher\";

        //For waiting all to finish
        public static readonly CountdownEvent lock_pendingFiles = new(0); //either waits to be or is being decompressed, applied to a game file, updated to the DB or saved to memory when cancelled

        //For limiting count
        public static SemaphoreSlim? lock_decompressingFiles;

        static readonly AutoResetEvent lock_applyNotInProgress = new(true); //new downloads and decompressions should wait for the update to finish

        /// <remarks>
        /// It's CPU-intensive.
        /// </remarks>
        public async Task<(int updated, int failed)> UpdateGameFilesAsync(string gameDir, CancellationToken cancellationToken, IProgress<float>? progress = null)
        {
            if (!Path.EndsInDirectorySeparator(gameDir))
                gameDir += Path.DirectorySeparatorChar;
            lock_decompressingFiles ??= new(4);

            SQLiteCommand dbCommand = new(dbConnection);
            await using var d1 = dbCommand.ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.LeftJoins(TEMP_INFOVERSIONLATEST, "file_version",
                "left.unique_path = right.unique_path",
                "right.unique_path is null or left.version > right.version",
                "left.id, left.unique_path, left.path, left.version, right.version, left.size, left.hash, left.property"
            );
            SQLiteCommand dbCommand_count = new(dbConnection);
            await using var d2 = dbCommand_count.ConfigureAwait(false);
            dbCommand_count.CommandText = SQLStrings.GetCountQuery(dbCommand.CommandText);
            int total = Convert.ToInt32(await dbCommand_count.ExecuteScalarAsync().ConfigureAwait(false));
            int updated = 0, failed = 0;
            Thread progressReporter = new(() =>
            {
                try
                {
                    while (true)
                    {
                        progress?.Report((float)(updated + failed) / total);
                        Thread.Sleep(1000);
                    }
                }
                catch (ThreadInterruptedException) { }
            });
            if (total > 0)
                progressReporter.Start();
            //Downloading files is time-consuming and should be the bottleneck, so transaction is not used in case it's interrupted and records of updated files are lost
            var reader = dbCommand.ExecuteReader();
            await using var d3 = reader.ConfigureAwait(false);
            SQLiteCommand dbCommand_update = new(dbConnection);
            await using var d4 = dbCommand_update.ConfigureAwait(false);
            GameFileNames storage = new(gameDir + tempSubDir);
            storage.LoadFromDisk();
            foreach (IDataRecord row in reader)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                int id = Convert.ToInt32(row[0]);
                string uniquePath = row.GetString(1);
                string path = row.GetString(2);
                int latestVersion = Convert.ToInt32(row[3]);
                int currentVersion = row.IsDBNull(4) ? -1 : Convert.ToInt32(row[4]);
                int size = Convert.ToInt32(row[5]);
                string hash = row.GetString(6);
                int property = Convert.ToInt32(row[7]);

                if (currentVersion == -1)
                {
                    if (!File.Exists(gameDir + path))
                    {
                        string? fileNameInStorage = storage.Get(id, null, latestVersion);
                        string? savePath;
                        if (fileNameInStorage is null)
                        {
                            //Most should be delta updates, which uses a lot of disk
                            //Download waits for the updating to finish
                            lock_applyNotInProgress.WaitOne();
                            lock_applyNotInProgress.Set();
                            if (cancellationToken.IsCancellationRequested)
                                break;
                            //Downlaod synchronously
                            savePath = FS.DownloadGameFileAsync(gameDir + tempSubDir, id, latestVersion, null, cancellationToken).Result;
                            if (cancellationToken.IsCancellationRequested)
                            {
                                if (savePath != null)
                                    storage.Add(id, null, latestVersion, Path.GetFileName(savePath));
                                break;
                            }
                        }
                        else
                        {
                            storage.Remove(id, null, latestVersion);
                            savePath = gameDir + tempSubDir + fileNameInStorage;
                        }
                        if (savePath is null)
                            Interlocked.Increment(ref failed);
                        else
                        {
                            lock_pendingFiles.Reset(lock_pendingFiles.CurrentCount + 1);
                            //Decompress and update asynchronously
                            lock_decompressingFiles.WaitAsync(cancellationToken)
                            .ContinueWith((_) =>
                            {
                                //Most should be delta updates, which uses a lot of disk
                                //Decompression waits for the updating to finish
                                lock_applyNotInProgress.WaitOne();
                                lock_applyNotInProgress.Set();
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    lock_decompressingFiles.Release();
                                    storage.Add(id, null, latestVersion, Path.GetFileName(savePath));
                                    lock_pendingFiles.Signal();
                                    return;
                                }
                                //Decompress
                                string? decompressPath = FS.DecompressIfNecessary(savePath);
                                lock_decompressingFiles.Release();
                                if (decompressPath is null)
                                {
                                    try { File.Delete(savePath); } catch { }
                                    Interlocked.Increment(ref failed);
                                    lock_pendingFiles.Signal();
                                    return;
                                }
                                else
                                {
                                    lock_applyNotInProgress.WaitOne();
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        lock_applyNotInProgress.Set();
                                        storage.Add(id, null, latestVersion, Path.GetFileName(decompressPath));
                                        lock_pendingFiles.Signal();
                                        return;
                                    }
                                    bool updateSuccess = FS.UpdateAsync(decompressPath, gameDir + path, false).Result;
                                    lock_applyNotInProgress.Set();
                                    if (updateSuccess)
                                    {
                                        lock (dbCommand_update)
                                        {
                                            dbCommand_update.CommandText = SQLStrings.InsertRows("file_version",
                                                $"unique_path, version, size, hash, property",
                                                $"'{uniquePath.Replace("'", "''")}', {latestVersion}, {size}, '{hash}', {property}"
                                            );
                                            dbCommand_update.ExecuteNonQuery();
                                        }
                                        Interlocked.Increment(ref updated);
                                    }
                                    else
                                        Interlocked.Increment(ref failed);
                                }
                                lock_pendingFiles.Signal();
                            }, cancellationToken);
                            /*FS.QueueDecompressAndUpdate(savePath, gameDir + path, false, (success) => {
                                if (success)
                                {
                                    lock (dbCommand_update)
                                    {
                                        dbCommand_update.CommandText = SQLStrings.InsertRows("file_version",
                                            $"unique_path, version, size, hash, property",
                                            $"'{uniquePath.Replace("'", "''")}', {latestVersion}, {size}, '{hash}', {property}"
                                        );
                                        dbCommand_update.ExecuteNonQuery();
                                    }
                                    Interlocked.Increment(ref updated);
                                }
                                else
                                    Interlocked.Increment(ref failed);
                            });*/
                        }
                    }
                    else
                        Interlocked.Increment(ref failed);
                }
                else
                {
                    if (File.Exists(gameDir + path))
                    {
                        var updateSequence = FindUpdatePathAsync(id, currentVersion, latestVersion).Result;
                        Task? previousUpdate = null;
                        /*List<(string fromPath, string toPath, bool isDelta, Action<bool>? callback)> operations = new();
                        int? lastSuccessfulDownloadVersion = null, lastSuccessfulUpdateVersion = null;*/
                        while (updateSequence.Count > 0)
                        {
                            (int fromVersion, int toVersion) = updateSequence.Pop();
                            string? fileNameInStorage = storage.Get(id, fromVersion, toVersion);
                            string? savePath;
                            if (fileNameInStorage is null)
                            {
                                //Most should be delta updates, which uses a lot of disk
                                //Download waits for the updating to finish
                                lock_applyNotInProgress.WaitOne();
                                lock_applyNotInProgress.Set();
                                if (cancellationToken.IsCancellationRequested)
                                    break; //No further tasks
                                //Download synchronously
                                savePath = FS.DownloadGameFileAsync(gameDir + tempSubDir, id, toVersion, fromVersion, cancellationToken).Result;
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    if (savePath != null)
                                        storage.Add(id, fromVersion, toVersion, Path.GetFileName(savePath));
                                    break; //No further tasks
                                }
                            }
                            else
                            {
                                storage.Remove(id, fromVersion, toVersion);
                                savePath = gameDir + tempSubDir + fileNameInStorage;
                            }
                            if (savePath is null)
                            {
                                //If there is no previous or the previous is not faulted or cancelled, this is the first failure in the sequence
                                if (previousUpdate is null)
                                    Interlocked.Increment(ref failed);
                                else
                                    previousUpdate.ContinueWith((_) =>
                                    {
                                        if (previousUpdate.IsCompletedSuccessfully)
                                            Interlocked.Increment(ref failed);
                                    });
                                break; //No further tasks
                            }
                            else
                            {
                                //Debug.WriteLine($"Downloaded {path[(path.LastIndexOfAny(['\\', '/']) + 1)..]} from {fromVersion} to {toVersion}");
                                lock_pendingFiles.Reset(lock_pendingFiles.CurrentCount + 1);
                                //Decompress and update asynchronously
                                Task? previousUpdateForThis = previousUpdate;
                                previousUpdate = lock_decompressingFiles.WaitAsync().ContinueWith((_) =>
                                {
                                    //Most should be delta updates, which uses a lot of disk
                                    //Decompression waits for the updating to finish
                                    lock_applyNotInProgress.WaitOne();
                                    lock_applyNotInProgress.Set();
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        lock_decompressingFiles.Release();
                                        storage.Add(id, fromVersion, toVersion, Path.GetFileName(savePath));
                                        lock_pendingFiles.Signal();
                                        cancellationToken.ThrowIfCancellationRequested(); //There may be further tasks. Let the next know this was cancelled.
                                    }
                                    //Decompress
                                    string? decompressPath = FS.DecompressIfNecessary(savePath);
                                    lock_decompressingFiles.Release();
                                    if (decompressPath is null)
                                    {
                                        try { File.Delete(savePath); } catch { }
                                        //If there is no previous or the previous is not faulted or cancelled, this is the first failure in the sequence
                                        if (previousUpdateForThis is null)
                                            Interlocked.Increment(ref failed);
                                        else
                                            previousUpdateForThis.ContinueWith((_) =>
                                            {
                                                if (previousUpdateForThis.IsCompletedSuccessfully)
                                                    Interlocked.Increment(ref failed);
                                            });
                                        lock_pendingFiles.Signal();
                                        throw new UpdateException(); //There may be further tasks. Let the next know this failed.
                                    }
                                    else
                                    {
                                        //Debug.WriteLine($"Decompressed {path[(path.LastIndexOfAny(['\\', '/']) + 1)..]} from {fromVersion} to {toVersion}");
                                        //This update can only happen after the previous one is successful
                                        try
                                        {
                                            previousUpdateForThis?.Wait();
                                        }
                                        catch
                                        {
                                            try { File.Delete(decompressPath); } catch { }
                                            lock_pendingFiles.Signal();
                                            throw; //There may be further tasks. Pass on the exception.
                                        }
                                        lock_applyNotInProgress.WaitOne();
                                        if (cancellationToken.IsCancellationRequested)
                                        {
                                            lock_applyNotInProgress.Set();
                                            storage.Add(id, fromVersion, toVersion, Path.GetFileName(decompressPath));
                                            lock_pendingFiles.Signal();
                                            cancellationToken.ThrowIfCancellationRequested(); //There may be further tasks. Let the next know this was cancelled.
                                        }
                                        bool updateSuccess = FS.UpdateAsync(decompressPath, gameDir + path, true).Result;
                                        lock_applyNotInProgress.Set();
                                        if (updateSuccess)
                                        {
                                            if (toVersion == latestVersion) //The last one the sequence
                                            {
                                                lock (dbCommand_update)
                                                {
                                                    dbCommand_update.CommandText = SQLStrings.UpdateRows("file_version",
                                                        $"unique_path = '{uniquePath.Replace("'", "''")}'",
                                                        $"version = {latestVersion}, size = {size}, hash = '{hash}', property = {property}"
                                                    );
                                                    dbCommand_update.ExecuteNonQuery();
                                                }
                                                Interlocked.Increment(ref updated);
                                            }
                                            else
                                            {
                                                lock (dbCommand_update)
                                                {
                                                    dbCommand_update.CommandText = SQLStrings.GetRows(TEMP_INFOVERSION,
                                                        $"unique_path = '{uniquePath.Replace("'", "''")}' and version = {toVersion}",
                                                        $"size, hash, property"
                                                    );
                                                    using var reader_update = dbCommand_update.ExecuteReader();
                                                    if (reader_update.Read())
                                                    {
                                                        int size = Convert.ToInt32(reader_update[0]);
                                                        string hash = reader_update.GetString(1);
                                                        int property = Convert.ToInt32(reader_update[2]);
                                                        reader_update.Close();
                                                        dbCommand_update.CommandText = SQLStrings.UpdateRows("file_version",
                                                            $"unique_path = '{uniquePath.Replace("'", "''")}'",
                                                            $"version = {toVersion}, size = {size}, hash = '{hash}', property = {property}"
                                                        );
                                                        dbCommand_update.ExecuteNonQuery();
                                                    }
                                                }
                                            }
                                            //Debug.WriteLine($"Updated {path[(path.LastIndexOfAny(['\\', '/']) + 1)..]} from {fromVersion} to {toVersion}");
                                        }
                                        else
                                        {
                                            try { File.Delete(decompressPath); } catch { }
                                            //If there is no previous or the previous is not faulted or cancelled, this is the first failure in the sequence
                                            if (previousUpdateForThis is null)
                                                Interlocked.Increment(ref failed);
                                            else
                                                previousUpdateForThis.ContinueWith((_) =>
                                                {
                                                    if (previousUpdateForThis.IsCompletedSuccessfully)
                                                        Interlocked.Increment(ref failed);
                                                });
                                            lock_pendingFiles.Signal();
                                            throw new UpdateException(); //There may be further tasks. Let the next know this failed.
                                        }
                                    }
                                    lock_pendingFiles.Signal();
                                }, cancellationToken);
                            }
                        }
                        /*
                        lastSuccessfulDownloadVersion = toVersion;
                        operations.Add((savePath, gameDir + path, true, (success) => {
                            if(success)
                                lastSuccessfulUpdateVersion = toVersion;
                            if(lastSuccessfulUpdateVersion == lastSuccessfulDownloadVersion || !success) //Last in the sequence
                            {
                                if(lastSuccessfulUpdateVersion != null)
                                    lock (dbCommand_update)
                                    {
                                        dbCommand_update.CommandText = SQLStrings.GetRows($"{FILE_INFOVERSION}",
                                            $"id = {id} and version = {lastSuccessfulUpdateVersion}",
                                            "size, hash, property");
                                        var reader_update = dbCommand_update.ExecuteReader();
                                        if (reader_update.Read())
                                        {
                                            int size = Convert.ToInt32(reader_update[0]);
                                            string hash = reader_update.GetString(1);
                                            int property = Convert.ToInt32(reader_update[2]);
                                            reader_update.Close();

                                            dbCommand_update.CommandText = SQLStrings.UpdateRows("file_version",
                                                $"unique_path = '{uniquePath.Replace("'", "''")}'",
                                                $"version = {lastSuccessfulUpdateVersion}, size = {size}, hash = '{hash}', property = {property}"
                                            );
                                            dbCommand_update.ExecuteNonQuery();
                                        }
                                    }

                                if(success)
                                    Interlocked.Increment(ref updated);
                                else
                                    Interlocked.Increment(ref failed);
                            }
                        }));
                    }
                }
                FS.QueueDecompressAndUpdateSequence(operations);*/
                    }
                    else
                        Interlocked.Increment(ref failed);
                }
            }
            lock_pendingFiles.Wait();
            if (total > 0)
                progressReporter.Interrupt();
            if (cancellationToken.IsCancellationRequested)
                if (!storage.IsEmpty())
                    storage.SaveToDisk();
                else
                    storage.DeleteFileOnDisk();
            else
                storage.DeleteFileOnDisk();
            try { Directory.Delete(gameDir + tempSubDir); } catch { }
            if (total > 0)
                progressReporter.Join();
            return (updated, failed);
        }


        /*
            <version and/or hash in record is correct, and size in record match file size> --(yes)--> assume the record match the file
            |--(no)--> [calculate file hash] ----> <file hash is correct> --(yes)--> [correct the record]
                                                   |--(no)--> [download file and keep the record]
        */

        //Rows that had valid version and no hash, no version and valid hash, or both version and hash valid become valid in all columns.
        //Rows that had invalid version and no hash, no version and invalid hash, or invalid combination of version and hash become having empty version and hash.
        public async Task FixFileVersionHashesAsync()
        {
            SQLiteCommand dbCommand = new(dbConnection);
            await using var d = dbCommand.ConfigureAwait(false);

            //Both version and hash exist
            ////If version and hash not in server database, clear version and hash
            const string VERSIONHASH_JOINED = "versionhash_joined";
            dbCommand.CommandText = SQLStrings.LeftJoinsQuery(SQL_FileVersion_BothVersionHashExist, TEMP_INFOVERSION,
                "left.unique_path = right.unique_path and left.version = right.version and left.hash = right.hash",
                "left.unique_path as unique_path_local, left.version as version_local, left.size as size_local, left.hash as hash_local, left.property as property_local, right.unique_path as unique_path_server, right.version as version_server, right.size as size_server, right.hash as hash_server, right.property as property_server",
                VERSIONHASH_JOINED
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.UpdateRowsQuery("file_version", "unique_path",
                SQLStrings.GetRows(VERSIONHASH_JOINED,
                    "unique_path_server is null",
                    "unique_path_local"
                ),
                "version = null, hash = null"
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            ////If version and hash in server database, fill in missing or wrong size, and property
            dbCommand.CommandText = SQLStrings.ReplaceRowsQuery("file_version", "unique_path, version, size, hash, property",
                SQLStrings.GetRows(VERSIONHASH_JOINED,
                    "unique_path_server is not null and (size_local is null or size_local <> size_server or property_local is null or property_local <> property_server)",
                    "unique_path_local, version_local, size_server, hash_local, property_server"
                )
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.DropTable(VERSIONHASH_JOINED);
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            //Only version exists
            ////If version not in server database, clear version and hash
            const string VERSION_JOINED = "version_joined";
            dbCommand.CommandText = SQLStrings.LeftJoinsQuery(SQL_FileVersion_OnlyVersionExists, TEMP_INFOVERSION,
                "left.unique_path = right.unique_path and left.version = right.version",
                "left.unique_path as unique_path_local, right.unique_path as unique_path_server, left.version as version, right.size as size, right.hash as hash, right.property as property",
                VERSION_JOINED
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.UpdateRowsQuery("file_version", "unique_path",
                SQLStrings.GetRows(VERSION_JOINED,
                    "unique_path_server is null",
                    "unique_path_local"
                ),
                "version = null, hash = null"
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            ////If version in server database, fill in missing or wrong hash, size, and property
            dbCommand.CommandText = SQLStrings.ReplaceRowsQuery("file_version", "unique_path, version, size, hash, property",
                SQLStrings.GetRows(VERSION_JOINED,
                    "unique_path_server is not null",
                    "unique_path_local, version, size, hash, property"
                )
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.DropTable(VERSION_JOINED);
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            //Only hash exists
            ////If hash not in server database, clear version and hash
            const string HASH_JOINED = "hash_joined";
            dbCommand.CommandText = SQLStrings.LeftJoinsQuery(SQL_FileVersion_OnlyHashExists, TEMP_INFOVERSION,
                "left.unique_path = right.unique_path and left.hash = right.hash",
                "left.unique_path as unique_path_local, right.unique_path as unique_path_server, left.hash as hash, right.version as version, right.size as size, right.property as property",
                HASH_JOINED
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.UpdateRowsQuery("file_version", "unique_path",
                SQLStrings.GetRows(HASH_JOINED,
                    "unique_path_server is null",
                    "unique_path_local"
                ),
                "version = null, hash = null"
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            ////If hash in server database, fill in missing or wrong version, size, and property
            dbCommand.CommandText = SQLStrings.ReplaceRowsQuery("file_version", "unique_path, version, size, hash, property",
                SQLStrings.GetRows(HASH_JOINED,
                    "unique_path_server is not null",
                    "unique_path_local, version, size, hash, property"
                )
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.DropTable(HASH_JOINED);
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task<bool> CheckFileVersionsAsync()
        {
            object? count = await file_version.SelectScalarAsync("version is null", "count()");
            if (Convert.ToInt32(count) != 0)
                return false;

            if (fileInfo is null)
                throw new ServerDBNotAttachedException();

            SQLiteCommand dbCommand = new(dbConnection);
            await using (dbCommand.ConfigureAwait(false))
            {
                dbCommand.CommandText = SQL.Row.LeftJoin(
                    file_version.name,
                    fileInfo.name,
                    "left.unique_path = right.unique_path and left.version = right.version",
                    "right.version is null",
                    "count()"
                );
            }
            if (Convert.ToInt32(await dbCommand.ExecuteScalarAsync()) != 0)
                return false;

            return true;
        }

        //Rows that had both version and hash and did't match their respective file size become having empty version and hash
        /// <remarks>
        /// It's CPU-intensive.
        /// </remarks>
        public async Task CheckFileSizesAsync(string gameDir, CancellationToken cancellationToken, IProgress<float>? progress = null)
        {
            if (!Path.EndsInDirectorySeparator(gameDir))
                gameDir += Path.DirectorySeparatorChar;

            SQLiteCommand dbCommand = new(dbConnection);
            await using var d1 = dbCommand.ConfigureAwait(false);
            SQLiteCommand dbCommand_count = new(dbConnection);
            await using var d2 = dbCommand_count.ConfigureAwait(false);

            //Check file sizes for valid rows
            ////If a file size doesn't match its record, clear version and hash
            dbCommand.CommandText = SQLStrings.GetColumnsQuery(SQL_FileVersion_BothVersionHashExist, "unique_path, size");
            dbCommand_count.CommandText = SQLStrings.GetCountQuery(dbCommand.CommandText);
            int total = Convert.ToInt32(await dbCommand_count.ExecuteScalarAsync().ConfigureAwait(false));
            int done = 0;
            Thread progressReporter = new(() =>
            {
                try
                {
                    while (true)
                    {
                        progress?.Report((float)done / total);
                        Thread.Sleep(1000);
                    }
                }
                catch (ThreadInterruptedException) { }
            });
            if (total > 0)
                progressReporter.Start();
            SQLiteTransaction transaction = dbConnection.BeginTransaction();
            await using var d3 = transaction.ConfigureAwait(false);
            var reader = dbCommand.ExecuteReader();
            await using var d4 = reader.ConfigureAwait(false);
            foreach (IDataRecord row in reader)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                string relativePath = row.GetString(0);
                int size = Convert.ToInt32(row[1]);
                if (!File.Exists(gameDir + relativePath) || FS.GetFileSize(gameDir + relativePath) != size)
                {
                    SQLiteCommand dbCommand_update = new(
                        SQLStrings.UpdateRows("file_version", $"unique_path = '{relativePath.Replace("'", "''")}'", "version = null, hash = null"),
                        dbConnection, transaction
                    );
                    await using var d5 = dbCommand_update.ConfigureAwait(false);
                    dbCommand_update.ExecuteNonQuery();
                }
                Interlocked.Increment(ref done);
            }
            if (total > 0)
                progressReporter.Interrupt();
            if (cancellationToken.IsCancellationRequested)
                transaction.Rollback();
            else
                transaction.Commit();
            if (total > 0)
                progressReporter.Join();
        }

        //Rows that had empty version become either valid in all columns and match their respective file hash, or having empty hash
        /// <remarks>
        /// It's CPU-intensive.
        /// </remarks>
        public async Task FillInMissingValidFileHashesAsync(string gameDir, CancellationToken cancellationToken, IProgress<float>? progress = null)
        {
            if (!Path.EndsInDirectorySeparator(gameDir))
                gameDir += Path.DirectorySeparatorChar;

            SQLiteCommand dbCommand = new(dbConnection);
            await using var d1 = dbCommand.ConfigureAwait(false);
            SQLiteCommand dbCommand_count = new(dbConnection);
            await using var d2 = dbCommand_count.ConfigureAwait(false);

            //Calculate and fill in hash for invalid rows
            //Calculating hashes is time-consuming and should be the bottleneck, so transaction is not used in case it's interrupted and hashes are lost
            dbCommand.CommandText = SQLStrings.GetColumnsQuery(SQL_FileVersion_NeitherVersionHashExists, "unique_path");
            dbCommand_count.CommandText = SQLStrings.GetCountQuery(dbCommand.CommandText);
            int total = Convert.ToInt32(await dbCommand_count.ExecuteScalarAsync().ConfigureAwait(false));
            int done = 0;
            Thread progressReporter = new(() =>
            {
                try
                {
                    while (true)
                    {
                        progress?.Report((float)done / total);
                        Thread.Sleep(1000);
                    }
                }
                catch (ThreadInterruptedException) { }
            });
            if (total > 0)
                progressReporter.Start();
            var reader = dbCommand.ExecuteReader();
            await using var d3 = reader.ConfigureAwait(false);
            SQLiteCommand dbCommand_update = new(dbConnection);
            await using var d4 = dbCommand_update.ConfigureAwait(false);
            foreach (IDataRecord row in reader)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                string relativePath = row.GetString(0);
                if (File.Exists(gameDir + relativePath))
                {
                    string? hash = FS.CalcHash(gameDir + relativePath);
                    if (hash is not null)
                    {
                        dbCommand_update.CommandText = SQLStrings.UpdateRows("file_version", $"unique_path = '{relativePath.Replace("'", "''")}'", $"hash = '{hash}'");
                        dbCommand_update.ExecuteNonQuery();
                    }
                }
                Interlocked.Increment(ref done);
            }
            await reader.CloseAsync().ConfigureAwait(false);
            if (total > 0)
                progressReporter.Interrupt();

            //Validate the hashes
            const string HASH_JOINED = "hash_joined";
            dbCommand.CommandText = SQLStrings.LeftJoinsQuery(SQL_FileVersion_OnlyHashExists, TEMP_INFOVERSION,
                "left.unique_path = right.unique_path and left.hash = right.hash",
                "left.unique_path as unique_path_local, right.unique_path as unique_path_server, right.version as version, right.size as size, left.hash as hash, right.property as property",
                HASH_JOINED
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            ////If hash not in server database, clear hash
            dbCommand.CommandText = SQLStrings.UpdateRowsQuery("file_version", "unique_path",
                SQLStrings.GetRows(HASH_JOINED,
                    "unique_path_server is null",
                    "unique_path_local"
                ),
                "hash = null"
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            ////If hash in server database, fill in version, size, and property
            dbCommand.CommandText = SQLStrings.ReplaceRowsQuery("file_version", "unique_path, version, size, hash, property",
                SQLStrings.GetRows(HASH_JOINED,
                    "unique_path_server is not null",
                    "unique_path_local, version, size, hash, property"
                )
            );
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

            dbCommand.CommandText = SQLStrings.DropTable(HASH_JOINED);
            await dbCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (total > 0)
                progressReporter.Join();
        }

        //Rows that had neither version nor hash get file downloaded and become valid in all columns
        /// <remarks>
        /// It's CPU-intensive.
        /// </remarks>
        public async Task<(int fixes, int fails)> DownloadMissingGameFilesAsync(string gameDir, CancellationToken cancellationToken, IProgress<float>? progress = null)
        {
            if (!Path.EndsInDirectorySeparator(gameDir))
                gameDir += Path.DirectorySeparatorChar;
            lock_decompressingFiles ??= new(4);

            SQLiteCommand dbCommand = new(dbConnection);
            await using var d1 = dbCommand.ConfigureAwait(false);
            dbCommand.CommandText = SQLStrings.JoinsQuery(SQL_FileVersion_NeitherVersionHashExists, TEMP_INFOVERSIONLATEST,
                "left.unique_path = right.unique_path",
                "right.id, left.unique_path, right.path, right.version, right.size, right.hash, right.property"
            );
            SQLiteCommand dbCommand_count = new(dbConnection);
            await using var d2 = dbCommand_count.ConfigureAwait(false);
            dbCommand_count.CommandText = SQLStrings.GetCountQuery(dbCommand.CommandText);
            int total = Convert.ToInt32(await dbCommand_count.ExecuteScalarAsync().ConfigureAwait(false));
            int fixes = 0, fails = 0;
            Thread progressReporter = new(() =>
            {
                try
                {
                    while (true)
                    {
                        progress?.Report((float)(fixes + fails) / total);
                        Thread.Sleep(1000);
                    }
                }
                catch (ThreadInterruptedException) { }
            });
            if (total > 0)
                progressReporter.Start();
            //Downloading files is time-consuming and should be the bottleneck, so transaction is not used in case it's interrupted and records of downloaded files are lost
            var reader = dbCommand.ExecuteReader();
            await using var d3 = reader.ConfigureAwait(false);
            SQLiteCommand dbCommand_update = new(dbConnection);
            await using var d4 = dbCommand_update.ConfigureAwait(false);
            GameFileNames storage = new(gameDir + tempSubDir);
            storage.LoadFromDisk();
            foreach (IDataRecord row in reader)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                int id = Convert.ToInt32(row[0]);
                string uniquePath = row.GetString(1);
                string path = row.GetString(2);
                int version = Convert.ToInt32(row[3]);
                int size = Convert.ToInt32(row[4]);
                string hash = row.GetString(5);
                int property = Convert.ToInt32(row[6]);

                string? fileNameInStorage = storage.Get(id, null, version);
                string? savePath;
                if (fileNameInStorage is null)
                {
                    //Most should be full updates(only moving files), which uses little disk
                    //Download doesn't wait for the updating to finish
                    /*lock_updateNotInProgress.WaitOne();
                    lock_updateNotInProgress.Set();*/
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    //Downlaod synchronously
                    savePath = FS.DownloadGameFileAsync(gameDir + tempSubDir, id, version, null, cancellationToken).Result;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (savePath != null)
                            storage.Add(id, null, version, Path.GetFileName(savePath));
                        break;
                    }
                }
                else
                {
                    storage.Remove(id, null, version);
                    savePath = gameDir + tempSubDir + fileNameInStorage;
                }
                if (savePath is null)
                    Interlocked.Increment(ref fails);
                else
                {
                    lock_pendingFiles.Reset(lock_pendingFiles.CurrentCount + 1);
                    //Decompress and update asynchronously
                    lock_decompressingFiles.WaitAsync(cancellationToken)
                    .ContinueWith((_) =>
                    {
                        //Most should be full updates(only moving files), which uses little disk
                        //Decompression doesn't wait for the updating to finish
                        /*lock_updateNotInProgress.WaitOne();
                        lock_updateNotInProgress.Set();*/
                        if (cancellationToken.IsCancellationRequested)
                        {
                            lock_decompressingFiles.Release();
                            storage.Add(id, null, version, Path.GetFileName(savePath));
                            lock_pendingFiles.Signal();
                            return;
                        }
                        //Decompress
                        string? decompressPath = FS.DecompressIfNecessary(savePath);
                        lock_decompressingFiles.Release();
                        if (decompressPath is null)
                        {
                            try { File.Delete(savePath); } catch { }
                            Interlocked.Increment(ref fails);
                            lock_pendingFiles.Signal();
                            return;
                        }
                        else
                        {
                            lock_applyNotInProgress.WaitOne();
                            if (cancellationToken.IsCancellationRequested)
                            {
                                lock_applyNotInProgress.Set();
                                storage.Add(id, null, version, Path.GetFileName(decompressPath));
                                lock_pendingFiles.Signal();
                                return;
                            }
                            bool updateSuccess = FS.UpdateAsync(decompressPath, gameDir + path, false).Result;
                            lock_applyNotInProgress.Set();
                            if (updateSuccess)
                            {
                                lock (dbCommand_update)
                                {
                                    dbCommand_update.CommandText = SQLStrings.UpdateRows("file_version",
                                        $"unique_path = '{uniquePath.Replace("'", "''")}'",
                                        $"version = {version}, size = {size}, hash = '{hash}', property = {property}"
                                    );
                                    dbCommand_update.ExecuteNonQuery();
                                }
                                Interlocked.Increment(ref fixes);
                            }
                            else
                                Interlocked.Increment(ref fails);
                        }
                        lock_pendingFiles.Signal();
                    }, cancellationToken);
                }
            }
            lock_pendingFiles.Wait();
            if (total > 0)
                progressReporter.Interrupt();
            if (cancellationToken.IsCancellationRequested)
                if (!storage.IsEmpty())
                    storage.SaveToDisk();
                else
                    storage.DeleteFileOnDisk();
            else
                storage.DeleteFileOnDisk();
            try { Directory.Delete(gameDir + tempSubDir); } catch { }
            if (total > 0)
                progressReporter.Join();
            return (fixes, fails);
        }

        class UpdateException : Exception { }
    }

    internal static partial class FS
    {
        //If toDir is null, it will be saved to tempDir
        //Returns: file location
        static async Task<string?> DownloadAndDecompressAsync(string url, string? toDir, CancellationToken cancellationToken)
        {
            toDir ??= tempDir;
            if (!Path.EndsInDirectorySeparator(toDir))
                toDir += Path.DirectorySeparatorChar;

            try
            {
                //Download as the url filename under toDir or tempDir
                string? savePath = await DownloadAsync(url, toDir, cancellationToken);
                if (savePath is null)
                    return null;
                string fileName = Path.GetFileName(savePath);

                if (cancellationToken.IsCancellationRequested)
                    return null;

                //Decompress if necessary to the same directory
                if (Path.GetExtension(fileName) == ".cab")
                {
                    string cabName = fileName;
                    fileName = Path.GetFileNameWithoutExtension(fileName);
                    await Task.Run(() =>
                    {
                        DecompressLZMA(toDir + cabName, toDir + fileName);
                    }, CancellationToken.None);
                    File.Delete(toDir + cabName);
                }

                return toDir + fileName;
            }
            catch
            {
                return null;
            }
        }


        static readonly ManualResetEventSlim lock_updateNotInProgress = new(true); //new downloads and decompressions should wait for the update to finish

        //For waiting all to finish
        public static readonly CountdownEvent lock_pendingFiles = new(0); //either waits to be or is being decompressed, applied to a game file, or saved to memory when cancelled

        //For limiting count
        public static SemaphoreSlim? lock_decompressingFiles;

        static readonly Queue<FileToDecompressAndUpdate> decompressionQueue = new();
        static Task? decompressionQueueProcessor;

        static readonly Action<FileToDecompressAndUpdate> Decompress = (FileToDecompressAndUpdate file) => {
            if (Path.GetExtension(file.fromPath) == ".cab")
            {
                string dir = Path.GetDirectoryName(file.fromPath) + Path.DirectorySeparatorChar;
                string cabName = Path.GetFileName(file.fromPath);
                string fileName = Path.GetFileNameWithoutExtension(cabName);
                DecompressLZMA(dir + cabName, dir + fileName);
                File.Delete(dir + cabName);
                file.fromPath = dir + fileName;
            }
        };

        //The order of completions of decompressions or updates is not garanteed.
        //Decompressing is multi-threaded, while updating is single-threaded. Futher decompressions wait for the updating to finish.
        public static void DecompressAndUpdateAsync(FileToDecompressAndUpdate file, CancellationToken cancellationToken)
        {
            lock_pendingFiles.AddCount();
            decompressionQueue.Enqueue(file);

            lock_decompressingFiles ??= new(Environment.ProcessorCount <= 1 ? 1 : Environment.ProcessorCount - 1);
            if(decompressionQueueProcessor is null || decompressionQueueProcessor.IsCompleted)
                decompressionQueueProcessor = Task.Run(() => {
                    while (decompressionQueue.Count > 0)
                    {
                        lock_decompressingFiles.Wait(CancellationToken.None);
                        FileToDecompressAndUpdate file = decompressionQueue.Dequeue();
                        Task.Run(() => {
                            try
                            {
                                lock_updateNotInProgress.Wait(cancellationToken);
                                Decompress(file);
                                UpdateAsync(file, cancellationToken);
                            }
                            catch
                            {
                                file.callback?.Invoke(false);
                            }
                            finally
                            {
                                lock_decompressingFiles.Release();
                            }
                        }, CancellationToken.None);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            //Save the queue to memory and aggregate to disk. Remember to decrease lock_pendingFiles.
                            return;
                        }
                    }
                }, CancellationToken.None);
        }

        static readonly Queue<FileToDecompressAndUpdate> updateQueue = new();
        static Task? updateQueueProcessor;

        static readonly Func<FileToDecompressAndUpdate, bool> Update = (FileToDecompressAndUpdate file) => {
            if (file.isDelta)
            {
                bool result = ApplyDeltaAsync(file.toPath, file.fromPath).Result;
                try
                {
                    File.Delete(file.fromPath);
                }
                catch { }
                return result;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.toPath)); //toPath is not null and should point to a file (not a root directory)
                try
                {
                    File.Move(file.fromPath, file.toPath, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        };

        static void UpdateAsync(FileToDecompressAndUpdate file, CancellationToken cancellationToken)
        {
            updateQueue.Enqueue(file);

            if (updateQueueProcessor is null || updateQueueProcessor.IsCompleted)
                updateQueueProcessor = Task.Run(() => {
                    while (updateQueue.Count > 0)
                    {
                        lock_updateNotInProgress.Reset();
                        FileToDecompressAndUpdate file = updateQueue.Dequeue();
                        file.callback?.Invoke(Update(file));
                        lock_updateNotInProgress.Set();

                        if (cancellationToken.IsCancellationRequested)
                        {
                            //Save the queue to memory and aggregate to disk. Remember to decrease lock_pendingFiles.
                            return;
                        }
                    }
                }, CancellationToken.None);
        }

        //If a decomprssion or update fails, all further operations in the sequence are cancelled. Only the callback of the last successful file or, if none successful, the first failed file will be invoked.
        //Decompressing is multi-threaded, while updating is single-threaded. Futher decompressions wait for the updating to finish.
        public static void DecompressAndUpdateSequenceAsync(IEnumerable<FileToDecompressAndUpdate> sequence, CancellationToken cancellationToken)
        {

        }

        static UpdateQueue[]? decompressAndUpdateQueues;

        static int threadCount;

        static UpdateQueue GetLoadBalancedQueue()
        {
            if(decompressAndUpdateQueues is null)
            {
                threadCount = Environment.ProcessorCount - 1;
                if (threadCount < 1)
                    threadCount = 1;
                decompressAndUpdateQueues = new UpdateQueue[threadCount];
                for (int i = 0; i < threadCount; ++i)
                    decompressAndUpdateQueues[i] = new();
            }

            UpdateQueue? leastQueue = null;
            foreach (UpdateQueue queue in decompressAndUpdateQueues)
                if (leastQueue is null)
                    leastQueue = queue;
                else
                    lock (leastQueue)
                    {
                        lock (queue)
                        {
                            if(queue.Count < leastQueue.Count)
                                leastQueue = queue; //The lock on the original leastQueue will still get released
                        }
                    }
            return leastQueue!;
        }

        public static CountdownEvent AllPendingUpdatesCompleted = new(0);

        //To avoid filename conflictions in the process, fromPath should be an empty directory at the beginning and all fromPaths in the queue should have different filenames
        //The fromPath will be deleted after the operation is finished
        //The callback needs to be thread-safe
        public static void QueueDecompressAndUpdate(string fromPath, string toPath, bool isDelta, Action<bool>? callback)
        {
            var queue = GetLoadBalancedQueue();
            lock (queue)
            {
                //The queue, AllUpdatesCompleted and RunDecompressAndUpdater(queue) are synchronized with the queue lock (1/3)
                queue.Enqueue((fromPath, toPath, isDelta, callback, null));
                if (queue.Count == 1)
                {
                    if (AllPendingUpdatesCompleted.IsSet)
                        AllPendingUpdatesCompleted.Reset(1);
                    else
                        AllPendingUpdatesCompleted.AddCount();
                    RunDecompressAndUpdater(queue);
                    //Invariant: The queue is never empty (1/4)
                }
            }
        }

        static int lastSeqId = 0;

        //To avoid filename conflictions in the process, fromPath should be an empty directory at the beginning and all fromPaths in the queue should have different filenames
        //If an operation in a sequence failed, all futher operations in the same sequence would be skipped.
        //All fromPaths in the seqence will be deleted after the sequence is finished
        //All callbacks in the sequence needs to be thread-safe
        public static void QueueDecompressAndUpdateSequence(IList<(string fromPath, string toPath, bool isDelta, Action<bool>? callback)> sequence)
        {
            var queue = GetLoadBalancedQueue();
            lock (queue)
            {
                //The queue, AllUpdatesCompleted and RunDecompressAndUpdater(queue) are synchronized with the queue lock (2/3)
                ++lastSeqId;
                foreach ((string fromPath, string toPath, bool isDelta, Action<bool>? callback) in sequence)
                    queue.Enqueue((fromPath, toPath, isDelta, callback, lastSeqId));
                if (queue.Count == sequence.Count)
                {
                    if (AllPendingUpdatesCompleted.IsSet)
                        AllPendingUpdatesCompleted.Reset(1);
                    else
                        AllPendingUpdatesCompleted.AddCount();
                    RunDecompressAndUpdater(queue);
                    //Invariant: The queue is never empty (2/4)
                }
            }
        }

        static void RunDecompressAndUpdater(UpdateQueue queue)
        {
            Task.Run(() =>
            {
                while (true)
                {
                    //Invariant: The queue is never empty (3/4)
                    string fromPath, toPath;
                    bool isDelta;
                    Action<bool>? callback;
                    int? seqId;
                    lock (queue)
                    {
                        (fromPath, toPath, isDelta, callback, seqId) = queue.Peek();
                    }

                    bool success;
                    try
                    {
                        string? dir = Path.GetDirectoryName(fromPath) + Path.DirectorySeparatorChar;
                        string filename = Path.GetFileName(fromPath);
                        if (Path.GetExtension(filename) == ".cab")
                        {
                            string cabName = filename;
                            filename = Path.GetFileNameWithoutExtension(filename);
                            DecompressLZMA(dir + cabName, dir + filename);
                            File.Delete(dir + cabName);
                        }

                        if (isDelta)
                        {
                            success = ApplyDeltaAsync(toPath, dir + filename).Result;
                            try
                            {
                                File.Delete(dir + filename);
                            }
                            catch { }
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(toPath)); //toPath is not null and should point to a file (not a root directory)
                            File.Move(dir + filename, toPath, true);
                            success = true;
                        }
                    }
                    catch
                    {
                        success = false;
                    }

                    lock (queue)
                    {
                        //The queue, AllUpdatesCompleted and RunDecompressAndUpdater(queue) are synchronized with the queue lock (3/3)
                        queue.Dequeue();
                        if (queue.Count == 0)
                        {
                            callback?.Invoke(success);
                            AllPendingUpdatesCompleted.Signal();
                            break;
                            //Invariant: The queue is never empty (4/4)
                        }
                        else
                        {
                            if (!success)
                            {
                                //Skip further operations in the same sequence
                                if (seqId != null)
                                {
                                    while (queue.Count > 0)
                                    {
                                        int? nextSeqId = queue.Peek().seqId;
                                        if (nextSeqId != null && nextSeqId == seqId)
                                        {
                                            string nextFromPath = queue.Dequeue().fromPath;
                                            try
                                            {
                                                File.Delete(nextFromPath);
                                            }
                                            catch { }
                                        }
                                        else
                                            break;
                                    }
                                }
                            }
                            callback?.Invoke(success);
                        }
                    }
                }
            });
        }

        //If fromVersion is not null, delta file will be downloaded and applied to path
        //Download from calling thread, and asynchronously decompress and update by a separate thread
        public static Task<bool> UpdateGameFile(string path, int id, int toVersion, int? fromVersion = null)
        {
            string url;
            if (fromVersion is null)
            {
                url = $"{downloadBaseUrl}patch/{id}-{toVersion}.cab";
                return await DownloadAndDecompressAsync(url, path) == path;
            }
            else
            {
                url = $"{downloadBaseUrl}patch/{id}-{fromVersion}-{toVersion}.cab";
                string? deltaPath = await DownloadAndDecompressAsync(url);
                if (deltaPath is null)
                    return false;
                bool applicationSuccessful = await ApplyDeltaAsync(path, deltaPath);
                File.Delete(deltaPath);
                return applicationSuccessful;
            }
        }

        private static readonly SevenZip.Compression.LZMA.Decoder lzmaDecoder = new();

        private static void DecompressLZMA(string inPath, string outPath, SevenZip.Compression.LZMA.Decoder? decoder = null)
        {
            using FileStream iFS = new(inPath, FileMode.Open);
            using FileStream oFS = new(outPath, FileMode.Create);

            byte[] properties = new byte[5];
            iFS.Read(properties, 0, 5);

            byte[] fileLengthBytes = new byte[8];
            iFS.Read(fileLengthBytes, 0, 8);
            long fileLength = BitConverter.ToInt64(fileLengthBytes, 0);

            decoder ??= lzmaDecoder;
            lock (lzmaDecoder)
            {
                lzmaDecoder.SetDecoderProperties(properties);
                lzmaDecoder.Code(iFS, oFS, iFS.Length, fileLength, null);
            }
        }

        public static void CleanUp()
        {
            try { File.Delete(tempDir + launcherIni); } catch { }
            try { File.Delete(tempDir + versionIni); } catch { }
            if (serverDBCab is not null)
            {
                string fileName = serverDBCab;
                if (Path.GetExtension(fileName) == ".cab")
                    fileName = Path.GetFileNameWithoutExtension(fileName);
                try { File.Delete(tempDir + fileName); } catch { }
            }
            foreach (string path in Directory.EnumerateFiles(tempDir, "*.cab", SearchOption.TopDirectoryOnly))
                if (Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(tempDir) && Path.GetExtension(path) == ".cab")
                    try { File.Delete(path); } catch { }
        }
    }
}

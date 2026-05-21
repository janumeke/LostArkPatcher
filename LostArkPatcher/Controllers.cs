using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace LostArkPatcher
{
    internal static class Controllers
    {
        //On MainWindow

        public static void InitializeOnMainWindowLoad(MainWindow mainWindow)
        {
            mainWindow.Loaded += (sender, e) =>
            {
                SetTextOnModelGameDirectoryChange(mainWindow.LocationLabel);
                SetTooltipOnModelGameDirectoryChange(mainWindow.LocationLabel);
                SetTextOnModelLauncherCurrentVersionChange(mainWindow.LauncherCurrentVersionLabel);
                SetTextOnModelLauncherLatestVersionChange(mainWindow.LauncherLatestVersionLabel);
                SetTextOnModelGameCurrentVersionChange(mainWindow.GameCurrentVersionLabel);
                SetTextOnModelGameLatestVersionChange(mainWindow.GameLatestVersionLabel);
                SetTextOnModelLogChange(mainWindow.LogTextBox);
                SetTextOnModelLogContentChange(mainWindow.LogTextBox);
                GetCurrentVersionsOnModelGameDirectoryChange();
                NewModelLogOnModelGameDirectoryChange();
                Model.gameDirectory = Properties.Settings.Default.gameDirectory;
                ChooseGameDirectoryOnClick(mainWindow.SpecifyLocationButton);
                SetIsEnabledOnModelOperationInProgressChange(mainWindow.SpecifyLocationButton);
                SetIsEnabledOnModelOperationInProgressChange(mainWindow.UpdateButton);
                SetIsEnabledOnModelOperationInProgressChange(mainWindow.RepairButton);
                UpdateGameFilesOrCancelOnClick(mainWindow.UpdateButton);
                RepairGameFilesOrCancelOnClick(mainWindow.RepairButton);
                GetLatestVersionsAsync();
            };
        }

        public static void SaveSettingsOnMainWindowClose(MainWindow mainWindow)
        {
            mainWindow.Closed += (sender, e) =>
            {
                Properties.Settings.Default.gameDirectory = Model.gameDirectory;
                Properties.Settings.Default.Save();
            };
        }

        //On Model

        public static void SetTextOnModelGameDirectoryChange(TextBlock textBlock)
        {
            Model.GameDirectoryChanged += (_, s) => {
                textBlock.Text = s;
            };
        }

        public static void SetTooltipOnModelGameDirectoryChange(TextBlock textBlock)
        {
            Model.GameDirectoryChanged += (_, s) => {
                textBlock.ToolTip = s;
            };
        }

        public static void SetTextOnModelLauncherCurrentVersionChange(TextBlock textBlock)
        {
            Model.LauncherCurrentVersionChanged += (_, s) => {
                if(s is null)
                    textBlock.Text = "錯誤";
                else
                    textBlock.Text = s;
            };
        }

        public static void SetTextOnModelLauncherLatestVersionChange(TextBlock textBlock)
        {
            Model.LauncherLatestVersionChanged += (_, s) => {
                if (s is null)
                    textBlock.Text = "?";
                else
                    textBlock.Text = s;
            };
        }

        public static void SetTextOnModelGameCurrentVersionChange(TextBlock textBlock)
        {
            Model.GameCurrentVersionChanged += (_, i) => {
                if (i is null)
                    textBlock.Text = "錯誤";
                else
                    textBlock.Text = i.ToString();
            };
        }

        public static void SetTextOnModelGameLatestVersionChange(TextBlock textBlock)
        {
            Model.GameLatestVersionChanged += (_, s) => {
                if (s is null)
                    textBlock.Text = "?";
                else
                    textBlock.Text = s;
            };
        }

        public static void SetTextOnModelLogChange(TextBox textBox)
        {
            Model.LogChanged += (_, log) =>
            {
                textBox.Text = log?.ToString();
                textBox.ScrollToEnd();
            };
        }

        public static void SetTextOnModelLogContentChange(TextBox textBox)
        {
            Model.LogContentChanged += (_, s) =>
            {
                textBox.Text = s;
                textBox.ScrollToEnd();
            };
        }

        public static void GetCurrentVersionsOnModelGameDirectoryChange()
        {
            Model.GameDirectoryChanged += async (_, s) =>
            {
                await GetCurrentVersionsAsync();
            };
        }

        public static void NewModelLogOnModelGameDirectoryChange()
        {
            Model.GameDirectoryChanged += (_, s) =>
            {
                Model.log = new();
            };
        }

        public static void SetIsEnabledOnModelOperationInProgressChange(Button button)
        {
            Model.OperationInProgressChanged += (_, b) =>
            {
                button.IsEnabled = !b;
            };
        }

        //On click

        public static void ChooseGameDirectoryOnClick(Button button)
        {
            button.Click += async (sender, e) =>
            {
                OpenFolderDialog dialog = new()
                {
                    Title = "選取遊戲資料夾",
                    Multiselect = false
                };
                if (dialog.ShowDialog() == false)
                    return;
                Model.gameDirectory = dialog.FolderName;
            };
        }

        /* Updating
         *  downloaded storage: <= 1 GB
         *  decompressed storage: <= (decompress_threads + 1) files
         * HDD:
         *  update steps: <= 2
         *  download threads: 8 when not decoding
         *  decompress threads: (cores - 1) when not decoding
         *  decode threads: 1
         * SSD:
         *  update steps: unlimited
         *  download threads: 8
         *  decompress threads: (cores - 1)
         *  decode threads: 1
         */

        private static long maxDownloadedSize = 0;

        private static int  maxDecompressedCount = 0;

        private static readonly ManualResetEvent downloadedNotFull = new(true), decompressedNotFull = new(true);

        private static FS.DiskType diskType;

        private static Semaphore? maxDownloadingThreads = null, maxDecompressingThreads = null;

        private static readonly AutoResetEvent notDecoding = new(true);

        private static void SetUpdatingLimitations()
        {
            maxDownloadedSize = 1024 * 1024 * 1024; //1GB
            int coresToUse = Environment.ProcessorCount - 1;
            if (coresToUse < 1)
                coresToUse = 1;
            maxDecompressedCount = coresToUse + 1;
            diskType = FS.GetDiskType(Model.gameDirectory) ?? FS.DiskType.HDD;
            maxDownloadingThreads = new(8, 8);
            maxDecompressingThreads = new(coresToUse, coresToUse);
        }

        private const string tempDirectory = ".patcher", registryFile = "dict";

        private static ConcurrentDictionary<(int id, int? fromVersion, int toVersion), string>? registry;

        private static void LoadUpdatingCache()
        {
            List<GameFileEntry>? _registry = FS.LoadGameFileRegistry(Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + registryFile);
            registry = new();
            if (_registry is not null)
                foreach(GameFileEntry entry in _registry)
                    registry.TryAdd((entry.Id, entry.FromVersion, entry.ToVersion), entry.Path);
        }

        private static void SaveUpdatingCache()
        {
            if (registry is not null && !registry.IsEmpty)
                FS.SaveGameFileRegistry(registry, Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + registryFile);
            else
                try
                {
                    File.Delete(Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + registryFile);
                }
                catch { }
        }

        /// out: the version in the end, or null if unchanged
        private static async Task<int?> UpdateFileAsync(DB.FileUpdateInfo info, CancellationToken cancelToken)
        {
            if (maxDownloadingThreads is null || maxDecompressingThreads is null)
                throw new InvalidOperationException("Limitations of updating is not set.");

            //Locks are acquired in this order: downloadedNotFull, decompressedNotFull, maxDownloadingTasks, notDownloading, maxDecompressingTasks, notDecompressing, notDecoding
            //Special case where there is one full file
            if ((info.sequence.Count == 1 && info.sequence.First().fromVersion == null) //New file
                || (diskType == FS.DiskType.HDD && info.sequence.Count > 2)) //Always use full instead of deltas
            {
                //Download
                if (diskType == FS.DiskType.HDD)
                {
                    downloadedNotFull.WaitOne();
                    maxDownloadingThreads.WaitOne();
                    notDecoding.WaitOne();
                    notDecoding.Set();
                }
                else
                {
                    downloadedNotFull.WaitOne();
                    maxDownloadingThreads.WaitOne();
                }
                return await Task.Run<int?>(async () => {
                    string? path;
                    if (registry is not null && registry.TryRemove((info.id, null, info.sequence.Last().toVersion), out path)
                        && File.Exists(Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + path))
                        path = Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + path;
                    else
                        path = await FS.DownloadGameFileAsync(info.id, info.sequence.Last().toVersion, null, Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar, cancelToken);
                    maxDownloadingThreads.Release();
                    if (path is null)
                        return null;
                    long size = FS.GetFileSize(path);
                    if (Interlocked.Add(ref maxDownloadedSize, -size) <= 0)
                        downloadedNotFull.Reset();
                    //Decompress
                    if (diskType == FS.DiskType.HDD)
                    {
                        decompressedNotFull.WaitOne();
                        maxDecompressingThreads.WaitOne();
                        notDecoding.WaitOne();
                        notDecoding.Set();
                    }
                    else
                    {
                        decompressedNotFull.WaitOne();
                        maxDecompressingThreads.WaitOne();
                    }
                    if (cancelToken.IsCancellationRequested)
                    {
                        registry?.TryAdd((info.id, null, info.sequence.Last().toVersion), Path.GetFileName(path));
                        path = null;
                    }
                    else
                        path = FS.DecompressIfNecessary(path);
                    if (Interlocked.Add(ref maxDownloadedSize, size) > 0)
                        downloadedNotFull.Set();
                    maxDecompressingThreads.Release();
                    if (path is null)
                        return null;
                    if (Interlocked.Decrement(ref maxDecompressedCount) == 0)
                        decompressedNotFull.Reset();
                    //Decode
                    //only moving file to the same partition and not locking
                    bool decode = await FS.DecodeAsync(path, Model.gameDirectory + info.relativePath, false);
                    if (Interlocked.Increment(ref maxDecompressedCount) > 0)
                        decompressedNotFull.Set();
                    if (decode)
                        return info.sequence.Last().toVersion;
                    else
                        return null;
                });
            }
            else //Deltas
            {
                Task<int?>? lastVersionTask = null;
                foreach (DB.DeltaInfo delta in info.sequence)
                {
                    //Download
                    if (diskType == FS.DiskType.HDD)
                    {
                        downloadedNotFull.WaitOne();
                        maxDownloadingThreads.WaitOne();
                        notDecoding.WaitOne();
                        notDecoding.Set();
                    }
                    else
                    {
                        downloadedNotFull.WaitOne();
                        maxDownloadingThreads.WaitOne();
                    }
                    Task<int?>? preVersionTask = lastVersionTask;
                    lastVersionTask = Task.Run<int?>(async () => {
                        string? path;
                        if (registry is not null && registry.TryRemove((info.id, delta.fromVersion, delta.toVersion), out path)
                            && File.Exists(Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + path))
                            path = Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar + path;
                        else
                            path = await FS.DownloadGameFileAsync(info.id, delta.toVersion, delta.fromVersion, Model.gameDirectory + tempDirectory + Path.DirectorySeparatorChar, cancelToken);
                        maxDownloadingThreads.Release();
                        if (path is null)
                            if (preVersionTask is not null)
                                return await preVersionTask;
                            else
                                return null;
                        long size = FS.GetFileSize(path);
                        if (Interlocked.Add(ref maxDownloadedSize, -size) <= 0)
                            downloadedNotFull.Reset();
                        //Decompress
                        if (diskType == FS.DiskType.HDD)
                        {
                            decompressedNotFull.WaitOne();
                            maxDecompressingThreads.WaitOne();
                            notDecoding.WaitOne();
                            notDecoding.Set();
                        }
                        else
                        {
                            decompressedNotFull.WaitOne();
                            maxDecompressingThreads.WaitOne();
                        }
                        if (cancelToken.IsCancellationRequested)
                        {
                            registry?.TryAdd((info.id, delta.fromVersion, delta.toVersion), Path.GetFileName(path));
                            path = null;
                        }
                        else
                            path = FS.DecompressIfNecessary(path);
                        if (Interlocked.Add(ref maxDownloadedSize, size) > 0)
                            downloadedNotFull.Set();
                        maxDecompressingThreads.Release();
                        if (path is null)
                            if (preVersionTask is not null)
                                return await preVersionTask;
                            else
                                return null;
                        if (Interlocked.Decrement(ref maxDecompressedCount) == 0)
                            decompressedNotFull.Reset();
                        //Decode after previous version has finished decoding
                        if (preVersionTask is not null)
                        {
                            int? preVersion = await preVersionTask;
                            if (preVersion is null || preVersion != delta.fromVersion) //one of the previous tasks failed and this delta cannot decode
                                return preVersion;
                        }
                        notDecoding.WaitOne();
                        bool decoding;
                        if (cancelToken.IsCancellationRequested)
                        {
                            registry?.TryAdd((info.id, delta.fromVersion, delta.toVersion), Path.GetFileName(path));
                            decoding = false;
                        }
                        else
                            decoding = await FS.DecodeAsync(path, Model.gameDirectory + info.relativePath, delta.fromVersion != null);
                        if (Interlocked.Increment(ref maxDecompressedCount) > 0)
                            decompressedNotFull.Set();
                        notDecoding.Set();
                        if (decoding)
                            return delta.toVersion;
                        else if (preVersionTask is not null)
                            return await preVersionTask;
                        else
                            return null;
                    });
                }
                return await lastVersionTask;
            }
        }

        public static void UpdateGameFilesOrCancelOnClick(Button button)
        {
            button.Click += async (sender, e) =>
            {
                if (Model.operationInProgress)
                {
                    if (Model.cancellationTokenSource is not null && !Model.cancellationTokenSource.IsCancellationRequested)
                    {
                        Model.cancellationTokenSource.Cancel();
                        Model.log?.AddLine(new Model.Log.LogLine("已要求中斷"));
                    }
                    return;
                }
                if (!CheckGameDirectory() || !CheckRunningApps())
                    return;

                Model.operationInProgress = true;
                string originalContent = button.Content.ToString();
                button.Content = "中斷";
                button.IsEnabled = true;

                Model.log = new();
                Model.Log.LogLinePending lastLine;
                Model.cancellationTokenSource = new();

                string? serverDBPath = null;
                try
                {
                    if (Model.launcherLatestVersion is null || Model.gameLatestVersion is null)
                    {
                        lastLine = new Model.Log.LogLinePending("取得最新資訊");
                        Model.log.AddLine(lastLine);
                        await GetLatestVersionsAsync();
                        if (Model.cancellationTokenSource.IsCancellationRequested)
                        {
                            lastLine.Status = Model.Log.LogLineState.Interrupted;
                            return;
                        }
                        else if (Model.launcherLatestVersion is null || Model.gameLatestVersion is null)
                        {
                            lastLine.Status = Model.Log.LogLineState.Failed;
                            return;
                        }
                        else
                            lastLine.Status = Model.Log.LogLineState.Finished;
                    }

                    if(Model.launcherCurrentVersion is null || Model.launcherCurrentVersion != Model.launcherLatestVersion)
                    {
                        lastLine = new Model.Log.LogLinePending("更新啟動器");
                        Model.log.AddLine(lastLine);
                        if (Model.launcherInstallerUrl is null)
                            lastLine.Status = Model.Log.LogLineState.Failed;
                        else
                        {
                            bool updatingLauncher = await FS.DownloadLauncherAsync(Model.launcherInstallerUrl, Model.gameDirectory, Model.cancellationTokenSource.Token);
                            if (Model.cancellationTokenSource.IsCancellationRequested)
                                lastLine.Status = Model.Log.LogLineState.Interrupted;
                            else if (updatingLauncher)
                                lastLine.Status = Model.Log.LogLineState.Finished;
                            else
                                lastLine.Status = Model.Log.LogLineState.Failed;
                        }
                    }

                    if (Model.cancellationTokenSource.IsCancellationRequested)
                        return;

                    lastLine = new Model.Log.LogLinePending("下載伺服器資料庫");
                    Model.log.AddLine(lastLine);
                    if(Model.serverDBArchiveUrl is null)
                    {
                        lastLine.Status = Model.Log.LogLineState.Failed;
                        return;
                    }
                    else
                    {
                        serverDBPath = await FS.DownloadServerDBAsync(Model.serverDBArchiveUrl, null, Model.cancellationTokenSource.Token);
                        if (Model.cancellationTokenSource.IsCancellationRequested)
                        {
                            lastLine.Status = Model.Log.LogLineState.Interrupted;
                            return;
                        }
                        else if (serverDBPath is null)
                        {
                            lastLine.Status = Model.Log.LogLineState.Failed;
                            return;
                        }
                        else
                            lastLine.Status = Model.Log.LogLineState.Finished;
                    }

                    //SQLite doesn't support asynchronous operations
                    await Task.Run(async () => {
                        await using DB db = new(Model.gameDirectory + Model.LOCALDB_FILENAME, serverDBPath);

                        lastLine = new Model.Log.LogLinePending("檢查本地資料庫");
                        Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(lastLine); });
                        bool scehma = await db.CheckSchemaAsync();
                        Application.Current.Dispatcher.Invoke(() => { lastLine.Status = Model.Log.LogLineState.Finished; });
                        if (!scehma)
                        {
                            Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(new Model.Log.LogLine("發現錯誤，請先修復")); });
                            return;
                        }

                        lastLine = new Model.Log.LogLineProgress("更新所有檔案");
                        Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(lastLine); });
                        SetUpdatingLimitations();
                        LoadUpdatingCache();
                        (int updated, int failed) = await db.UpdateFilesAsync(UpdateFileAsync, Model.cancellationTokenSource.Token, new Progress<float>((ratio) => {
                            Application.Current.Dispatcher.Invoke(() => { ((Model.Log.LogLineProgress)lastLine).Percent = ratio * 100; });
                        }));
                        SaveUpdatingCache();
                        Application.Current.Dispatcher.Invoke(() => {
                            if (Model.cancellationTokenSource.IsCancellationRequested)
                                lastLine.Status = Model.Log.LogLineState.Interrupted;
                            else
                                lastLine.Status = Model.Log.LogLineState.Finished;
                            if (updated > 0)
                                Model.log.AddLine(new Model.Log.LogLine($"  已更新：{updated}"));
                            if (failed > 0)
                                Model.log.AddLine(new Model.Log.LogLine($"  更新失敗：{failed}"));
                        });

                        await db.FixVersionAsync();
                    });
                    await GetCurrentVersionsAsync();
                }
                finally
                {
                    try
                    {
                        File.Delete(serverDBPath);
                        Directory.Delete(Model.gameDirectory + tempDirectory);
                    }
                    catch { }
                    Model.operationInProgress = false;
                    button.Content = originalContent;
                }
            };
        }

        /* Hashing threads:
         * HDD: 1
         * SSD: cores-1
         */

        private static Semaphore? maxHashingThreads = null;

        private readonly static ConcurrentDictionary<MD5, bool> md5s = new();

        private static void SetHashingLimitations()
        {
            if (FS.GetDiskType(Model.gameDirectory) == FS.DiskType.HDD)
            {
                //hashing is bottlenecked by reading files and concurrent reads create delay
                maxHashingThreads = new(1, 1);
                while (md5s.IsEmpty)
                    md5s.TryAdd(MD5.Create(), true);
            }
            else
            {
                int coresToUse = Environment.ProcessorCount - 1;
                if (coresToUse < 1)
                    coresToUse = 1;
                maxHashingThreads = new(coresToUse, coresToUse);
                while (md5s.Count < coresToUse)
                    md5s.TryAdd(MD5.Create(), true);
            }
        }

        /// in: relative path, case-insensitive (ok for windows system)
        /// out: md5 in lower-case or null if non-existent
        private static async Task<string?> HashFileAsync(string relativePath, CancellationToken cancelToken)
        {
            if(maxHashingThreads is null)
                throw new InvalidOperationException("Limitations of hashing is not set.");

            maxHashingThreads.WaitOne();
            try
            {
                return await Task.Run(() => {
                    foreach(var kvp in md5s)
                        if(kvp.Value)
                        {
                            md5s[kvp.Key] = false;
                            string? hash = FS.GetFileMD5(Model.gameDirectory + relativePath, kvp.Key);
                            md5s[kvp.Key] = true;
                            return hash;
                        }
                    throw new InvalidOperationException("No available MD5.");
                });
            }
            finally
            {
                maxHashingThreads.Release();
            }
        }

        public static void RepairGameFilesOrCancelOnClick(Button button)
        {
            button.Click += async (sender, e) =>
            {
                if (Model.operationInProgress)
                {
                    if (Model.cancellationTokenSource is not null && !Model.cancellationTokenSource.IsCancellationRequested)
                    {
                        Model.cancellationTokenSource.Cancel();
                        Model.log?.AddLine(new Model.Log.LogLine("已要求中斷"));
                    }
                    return;
                }
                if (!CheckGameDirectory() || !CheckRunningApps())
                    return;

                Model.operationInProgress = true;
                string originalContent = button.Content.ToString();
                button.Content = "中斷";
                button.IsEnabled = true;

                Model.log = new();
                Model.Log.LogLinePending lastLine, stageLine;
                Model.cancellationTokenSource = new();

                string? serverDBPath = null;
                try
                {
                    if (Model.serverDBArchiveUrl is null)
                    {
                        lastLine = new Model.Log.LogLinePending("取得最新資訊");
                        Model.log.AddLine(lastLine);
                        await GetLatestVersionsAsync();
                        if (Model.cancellationTokenSource.IsCancellationRequested)
                        {
                            lastLine.Status = Model.Log.LogLineState.Interrupted;
                            return;
                        }
                        else if (Model.serverDBArchiveUrl is null)
                        {
                            lastLine.Status = Model.Log.LogLineState.Failed;
                            return;
                        }
                        else
                            lastLine.Status = Model.Log.LogLineState.Finished;
                    }

                    lastLine = new Model.Log.LogLinePending("下載伺服器資料庫");
                    Model.log.AddLine(lastLine);
                    serverDBPath = await FS.DownloadServerDBAsync(Model.serverDBArchiveUrl, null, Model.cancellationTokenSource.Token);
                    if (Model.cancellationTokenSource.IsCancellationRequested)
                    {
                        lastLine.Status = Model.Log.LogLineState.Interrupted;
                        return;
                    }
                    else if (serverDBPath is null)
                    {
                        lastLine.Status = Model.Log.LogLineState.Failed;
                        return;
                    }
                    else
                        lastLine.Status = Model.Log.LogLineState.Finished;

                    //SQLite doesn't support asynchronous operations
                    await Task.Run(async () =>
                    {
                        await using DB db = new(Model.gameDirectory + Model.LOCALDB_FILENAME, serverDBPath);

                        stageLine = new Model.Log.LogLinePending("檢查本地資料庫");
                        Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(stageLine); });
                        try
                        {
                            lastLine = new Model.Log.LogLinePending("├檢查結構");
                            Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(lastLine); });
                            bool schema = await db.CheckSchemaAsync();
                            Application.Current.Dispatcher.Invoke(() => { lastLine.Status = Model.Log.LogLineState.Finished; });
                            if (!schema)
                            {
                                lastLine = new Model.Log.LogLinePending("├修復結構");
                                Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(lastLine); });
                                await db.FixSchemaAsync();
                                Application.Current.Dispatcher.Invoke(() => { lastLine.Status = Model.Log.LogLineState.Finished; });
                            }

                            if (Model.cancellationTokenSource.IsCancellationRequested)
                                return;

                            int deleted, inserted, modified;

                            lastLine = new Model.Log.LogLinePending("├檢查項目");
                            Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(lastLine); });
                            (deleted, inserted) = await db.FixFileKeysAsync();
                            Application.Current.Dispatcher.Invoke(() => {
                                if (Model.cancellationTokenSource.IsCancellationRequested)
                                    lastLine.Status = Model.Log.LogLineState.Interrupted;
                                else
                                    lastLine.Status = Model.Log.LogLineState.Finished;
                            });

                            if (Model.cancellationTokenSource.IsCancellationRequested)
                                return;

                            lastLine = new Model.Log.LogLineProgress("└檢查資料");
                            Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(lastLine); });
                            SetHashingLimitations();
                            (deleted, modified) = await db.FixFileInfosAsync((relativePath) => {
                                return FS.GetFileSize(Model.gameDirectory + relativePath);
                            }, HashFileAsync, Model.cancellationTokenSource.Token, new Progress<float>((ratio) => {
                                Application.Current.Dispatcher.Invoke(() => { ((Model.Log.LogLineProgress)lastLine).Percent = ratio * 100; });
                            }));
                            Application.Current.Dispatcher.Invoke(() => {
                                if (Model.cancellationTokenSource.IsCancellationRequested)
                                    lastLine.Status = Model.Log.LogLineState.Interrupted;
                                else
                                    lastLine.Status = Model.Log.LogLineState.Finished;
                                if (deleted + modified > 0)
                                    Model.log.AddLine(new Model.Log.LogLine($"    已修復：{deleted + modified}"));
                            });
                        }
                        finally
                        {
                            await db.FixVersionAsync();
                            Application.Current.Dispatcher.Invoke(() => {
                                if (Model.cancellationTokenSource.IsCancellationRequested)
                                    stageLine.Status = Model.Log.LogLineState.Interrupted;
                                else
                                    stageLine.Status = Model.Log.LogLineState.Finished;
                            });
                        }

                        if (Model.cancellationTokenSource.IsCancellationRequested)
                            return;

                        int outdated = await db.GetOutdatedCountAsync();
                        if (outdated > 0)
                            Application.Current.Dispatcher.Invoke(() => { Model.log.AddLine(new Model.Log.LogLine($"需要更新：{outdated}")); });
                    });
                    await GetCurrentVersionsAsync();
                }
                finally
                {
                    try
                    {
                        File.Delete(serverDBPath);
                    }
                    catch { }
                    Model.operationInProgress = false;
                    button.Content = originalContent;
                }
            };
        }

        //Manual

        public static async Task GetCurrentVersionsAsync()
        {
            string launcherPath = Model.gameDirectory + Model.LAUNCHER_FILENAME;
            if (!File.Exists(launcherPath))
                Model.launcherCurrentVersion = null;
            else
                Model.launcherCurrentVersion = FileVersionInfo.GetVersionInfo(launcherPath).FileVersion;

            string localDBPath = Model.gameDirectory + Model.LOCALDB_FILENAME;
            if (!File.Exists(localDBPath))
                Model.gameCurrentVersion = null;
            else
            {
                using DB db = new(localDBPath);
                Model.gameCurrentVersion = await db.GetVersionAsync();
            }
        }

        public static async Task GetLatestVersionsAsync()
        {
            var launcherInfo = await FS.RetrieveLauncherInfoAsync(CancellationToken.None);
            Model.launcherLatestVersion = launcherInfo.latestVersion;
            Model.launcherInstallerUrl = launcherInfo.installerUrl;
            var gameInfo = await FS.RetrieveGameFilesInfoAsync(CancellationToken.None);
            Model.gameLatestVersion = gameInfo.latestVersion;
            Model.serverDBArchiveUrl = gameInfo.serverDBArchiveUrl;
        }

        public static bool CheckGameDirectory()
        {
            if (Model.gameDirectory == String.Empty)
            {
                MessageBox.Show("未指定遊戲位置。", "錯誤");
                return false;
            }
            if (!Directory.Exists(Model.gameDirectory))
            {
                MessageBox.Show("遊戲位置不存在，請先建立資料夾。", "錯誤");
                return false;
            }
            if ((!File.Exists(Model.gameDirectory + Model.LOCALDB_FILENAME) || !File.Exists(Model.gameDirectory + Model.LAUNCHER_FILENAME))
                && MessageBox.Show("這似乎不是遊戲資料夾。\n其中的檔案可能會被覆蓋。\n你確定要繼續?", "警告", MessageBoxButton.OKCancel) == MessageBoxResult.Cancel)
                    return false;
            return true;
        }

        public static bool CheckRunningApps()
        {
            if ((Process.GetProcessesByName("Launcher").Length > 0 || Process.GetProcessesByName("LOSTARK").Length > 0)
                && MessageBox.Show("啟動器或遊戲似乎正在運行。\n此時操作可能會產生問題。\n你確定要繼續?", "警告", MessageBoxButton.OKCancel) == MessageBoxResult.Cancel)
                    return false;
            return true;
        }
    }
}

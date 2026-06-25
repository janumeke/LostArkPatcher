using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace LostArkPatcher
{
    internal static class Model
    {
        //Should be set before preparations for updating or repairing
        private static string gamePath;
        private static FS.DiskType diskType;

        # region Updating Game Files
        /*  downloaded storage: <= 1 GB
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

        private static Semaphore? maxDownloadingThreads = null, maxDecompressingThreads = null;

        private static readonly AutoResetEvent notDecoding = new(true);

        //Should be called before updating
        private static void SetUpdatingLimitations()
        {
            maxDownloadedSize = 1024 * 1024 * 1024; //1GB
            int coresToUse = Environment.ProcessorCount - 1;
            if (coresToUse < 1)
                coresToUse = 1;
            maxDecompressedCount = coresToUse + 1;
            maxDownloadingThreads = new(8, 8);
            maxDecompressingThreads = new(coresToUse, coresToUse);
        }

        private const string TEMPDIRECTORY = ".patcher", REGISTRYFILENAME = "dict";

        private static ConcurrentDictionary<(int id, int? fromVersion, int toVersion), string>? registry;

        //Should be called before updating
        private static void LoadUpdatingCache()
        {
            List<GameFileEntry>? _registry = FS.LoadGameFileRegistry(gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar + REGISTRYFILENAME);
            registry = new();
            if (_registry is not null)
                foreach(GameFileEntry entry in _registry)
                    registry.TryAdd((entry.Id, entry.FromVersion, entry.ToVersion), entry.Path);
        }

        //Should be called after updating
        private static void SaveUpdatingCache()
        {
            string path = gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar + REGISTRYFILENAME;
            if (registry is not null && !registry.IsEmpty)
                FS.SaveGameFileRegistry(registry, path);
            else
                try { File.Delete(path); } catch { }
        }

        // out: the version in the end, or null if unchanged
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
                        && File.Exists(gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar + path))
                        path = gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar + path;
                    else
                        path = await FS.DownloadGameFileAsync(info.id, info.sequence.Last().toVersion, null, gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar, cancelToken).ConfigureAwait(false);
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
                        path = await FS.DecompressIfNecessaryAsync(path);
                    if (Interlocked.Add(ref maxDownloadedSize, size) > 0)
                        downloadedNotFull.Set();
                    maxDecompressingThreads.Release();
                    if (path is null)
                        return null;
                    if (Interlocked.Decrement(ref maxDecompressedCount) == 0)
                        decompressedNotFull.Reset();
                    //Decode
                    //only moving file to the same partition and not locking
                    bool decode = await FS.DecodeAsync(path, gamePath + info.relativePath, false).ConfigureAwait(false);
                    if (Interlocked.Increment(ref maxDecompressedCount) > 0)
                        decompressedNotFull.Set();
                    if (decode)
                        return info.sequence.Last().toVersion;
                    else
                        return null;
                }).ConfigureAwait(false);
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
                            && File.Exists(gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar + path))
                            path = gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar + path;
                        else
                            path = await FS.DownloadGameFileAsync(info.id, delta.toVersion, delta.fromVersion, gamePath + TEMPDIRECTORY + Path.DirectorySeparatorChar, cancelToken).ConfigureAwait(false);
                        maxDownloadingThreads.Release();
                        if (path is null)
                            if (preVersionTask is not null)
                                return await preVersionTask.ConfigureAwait(false);
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
                            path = await FS.DecompressIfNecessaryAsync(path);
                        if (Interlocked.Add(ref maxDownloadedSize, size) > 0)
                            downloadedNotFull.Set();
                        maxDecompressingThreads.Release();
                        if (path is null)
                            if (preVersionTask is not null)
                                return await preVersionTask.ConfigureAwait(false);
                            else
                                return null;
                        if (Interlocked.Decrement(ref maxDecompressedCount) == 0)
                            decompressedNotFull.Reset();
                        //Decode after previous version has finished decoding
                        if (preVersionTask is not null)
                        {
                            int? preVersion = await preVersionTask.ConfigureAwait(false);
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
                            decoding = await FS.DecodeAsync(path, gamePath + info.relativePath, delta.fromVersion != null).ConfigureAwait(false);
                        if (Interlocked.Increment(ref maxDecompressedCount) > 0)
                            decompressedNotFull.Set();
                        notDecoding.Set();
                        if (decoding)
                            return delta.toVersion;
                        else if (preVersionTask is not null)
                            return await preVersionTask.ConfigureAwait(false);
                        else
                            return null;
                    });
                }
                return await lastVersionTask.ConfigureAwait(false);
            }
        }

        public static async Task<(int updated, int failed)> UpdateGameFilesAsync(string gamePath, string serverDBPath, CancellationToken cancelToken, IProgress<float>? progress = null)
        {
            try
            {
                await using DB db = new(gamePath + LOCALDB_FILENAME, serverDBPath);
                Model.gamePath = gamePath;
                Model.diskType = FS.GetDiskType(gamePath) ?? FS.DiskType.HDD;
                SetUpdatingLimitations();
                LoadUpdatingCache();
                //SQLite doesn't support asynchronous operations
                (int updated, int failed) = await Task.Run(() => db.UpdateFilesAsync(UpdateFileAsync, cancelToken, progress));
                SaveUpdatingCache();
                //SQLite doesn't support asynchronous operations
                await Task.Run(() => db.FixVersionAsync());
                return (updated, failed);
            }
            finally
            {
                try { Directory.Delete(gamePath + TEMPDIRECTORY); } catch { }
            }
        }
        #endregion

        #region Repairing Game Files
        /* Hashing threads:
         *  HDD: 1
         *  SSD: cores-1
         */

        private static Semaphore? maxHashingThreads = null;

        private readonly static ConcurrentBag<MD5> md5s = new();

        //Should be called before repairing
        private static void SetHashingLimitations()
        {
            if (diskType == FS.DiskType.HDD)
            {
                //hashing is bottlenecked by reading files and concurrent reads create delay
                maxHashingThreads = new(1, 1);
                while (md5s.IsEmpty)
                    md5s.Add(MD5.Create());
            }
            else
            {
                int coresToUse = Environment.ProcessorCount - 1;
                if (coresToUse < 1)
                    coresToUse = 1;
                maxHashingThreads = new(coresToUse, coresToUse);
                while (md5s.Count < coresToUse)
                    md5s.Add(MD5.Create());
            }
        }

        // in: relative path, case-insensitive (ok for windows system)
        // out: md5 in lower-case or null if non-existent
        private static async Task<string?> HashFileAsync(string relativePath, CancellationToken cancelToken)
        {
            if(maxHashingThreads is null)
                throw new InvalidOperationException("Limitations of hashing is not set.");

            maxHashingThreads.WaitOne();
            try
            {
                if (md5s.TryTake(out MD5 md5))
                {
                    string? hash = await Task.Run(() => FS.GetFileMD5(gamePath + relativePath, md5)).ConfigureAwait(false);
                    md5s.Add(md5);
                    return hash;
                }
                else
                    throw new InvalidOperationException("No available MD5.");
            }
            finally
            {
                maxHashingThreads.Release();
            }
        }

        public static async Task<(int deleted, int modified)> RepairGameFileEntriesAsync(string gamePath, string serverDBPath, bool fully, CancellationToken cancelToken, IProgress<float>? progress = null)
        {
            await using DB db = new(gamePath + LOCALDB_FILENAME, serverDBPath);
            Model.gamePath = gamePath;
            Model.diskType = FS.GetDiskType(gamePath) ?? FS.DiskType.HDD;
            SetHashingLimitations();
            //SQLite doesn't support asynchronous operations
            (int deleted, int modified) = await Task.Run(() => db.FixFileInfosAsync((relativePath) => {
                return FS.GetFileSize(gamePath + relativePath);
            }, HashFileAsync, cancelToken, progress, fully));
            //SQLite doesn't support asynchronous operations
            await Task.Run(() => db.FixVersionAsync());
            return (deleted, modified);
        }
        #endregion

        public const string LAUNCHER_FILENAME = "Launcher.exe";
        public const string LOCALDB_FILENAME = "local.db";

        public static async Task<string?> GetLauncherCurrentVersionAsync(string gamePath)
        {
            string launcherPath = gamePath + LAUNCHER_FILENAME;
            if (!File.Exists(launcherPath))
                return null;
            else
                return FileVersionInfo.GetVersionInfo(launcherPath).FileVersion;
        }

        public static async Task<int?> GetGameCurrentVersionAsync(string gamePath)
        {
            string localDBPath = gamePath + LOCALDB_FILENAME;
            if (!File.Exists(localDBPath))
                return null;
            else
            {
                await using DB db = new(localDBPath);
                //SQLite doesn't support asynchronous operations
                return await Task.Run(() => db.GetVersionAsync());
            }
        }

        public static string? LauncherInstallerUrl { get; private set; } = null;
        public static string? ServerDBArchiveUrl { get; private set; } = null;

        public static async Task<string?> GetLauncherLatestVersionAsync()
        {
            var launcherInfo = await FS.RetrieveLauncherInfoAsync(CancellationToken.None);
            LauncherInstallerUrl = launcherInfo.installerUrl;
            return launcherInfo.latestVersion;
        }

        public static async Task<string?> GetGameLatestVersionAsync()
        {
            var gameInfo = await FS.RetrieveGameFilesInfoAsync(CancellationToken.None);
            ServerDBArchiveUrl = gameInfo.serverDBArchiveUrl;
            return gameInfo.latestVersion;
        }
    }
}

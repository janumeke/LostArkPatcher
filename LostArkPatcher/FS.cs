using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Lzma;
using VCDiff.Decoders;
using VCDiff.Includes;

namespace LostArkPatcher
{
    //XmlSerializer requires the type to be public 
    public class GameFileEntry
    {
        public int Id { get; set; }
        public int? FromVersion { get; set; }
        public int ToVersion { get; set; }
        public string Path { get; set; }
    }

    internal static partial class FS
    {
        public static string TempDirectoryPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + nameof(LostArkPatcher) + Path.DirectorySeparatorChar;

        private static readonly HttpClient client = new(new SocketsHttpHandler(){
            SslOptions = new(){
                RemoteCertificateValidationCallback = (_, _, _, sslPolicyErrors) => {
                    return (sslPolicyErrors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == System.Net.Security.SslPolicyErrors.None;
                }
            }
        });
        private const string downloadBaseUrl = "https://patch-loa.mangot5.com.tw/live/"; //The site's ssl certificate doesn't match the domain
        private const string launcherIni = "launcher_info.ini";
        private const string versionIni = "version.ini";

        [LibraryImport("kernel32.dll")]
        private static partial int GetPrivateProfileStringA(
            [MarshalAs(UnmanagedType.LPStr)]
            string lpAppName,
            [MarshalAs(UnmanagedType.LPStr)]
            string lpKeyName,
            [MarshalAs(UnmanagedType.LPStr)]
            string? lpDefault,
            [Out]
            byte[] lpszReturnBuffer,
            int nSize,
            [MarshalAs(UnmanagedType.LPStr)]
            string lpFileName
        );

        /// <remarks>
        /// Thread-safe.
        /// </remarks>
        /// <returns>Installer info or <c>null</c> if it failed or was cancelled.</returns>
        public static async Task<(string? latestVersion, string? installerUrl)> RetrieveLauncherInfoAsync(CancellationToken cancellationToken)
        {
            string? iniPath = null;
            try
            {
                iniPath = await DownloadAsync(downloadBaseUrl + launcherIni, null, cancellationToken).ConfigureAwait(false);
                if (iniPath == null)
                    return (null, null);

                byte[] bytes;

                bytes = new byte[128];
                GetPrivateProfileStringA("LAUNCHER", "installer_url", null, bytes, bytes.Length, iniPath);
                string installerUrl = Encoding.ASCII.GetString(bytes).TrimEnd('\0');

                bytes = new byte[32];
                GetPrivateProfileStringA("LAUNCHER", "version", null, bytes, bytes.Length, iniPath);
                string version = Encoding.ASCII.GetString(bytes).TrimEnd('\0');

                return (version, installerUrl);
            }
            catch
            {
                return (null, null);
            }
            finally
            {
                try
                {
                    if(iniPath is not null)
                        File.Delete(iniPath);
                }
                catch { }
            }
        }

        /// <remarks>
        /// Thread-safe.
        /// </remarks>
        /// <returns>Game info or <c>null</c> if it failed or was cancelled.</returns>
        public static async Task<(string? latestVersion, string? serverDBArchiveUrl)> RetrieveGameFilesInfoAsync(CancellationToken cancellationToken)
        {
            string? iniPath = null;
            try
            {
                iniPath = await DownloadAsync(downloadBaseUrl + versionIni, null, cancellationToken).ConfigureAwait(false);
                if (iniPath == null)
                    return (null, null);

                byte[] bytes;

                bytes = new byte[32];
                GetPrivateProfileStringA("Download", "DB file", null, bytes, bytes.Length, iniPath);
                string archive = Encoding.ASCII.GetString(bytes).TrimEnd('\0');

                bytes = new byte[32];
                GetPrivateProfileStringA("Download", "Version", null, bytes, bytes.Length, iniPath);
                string version = Encoding.ASCII.GetString(bytes).TrimEnd('\0');

                return (version, downloadBaseUrl + archive);
            }
            catch
            {
                return (null, null);
            }
            finally
            {
                try
                {
                    if (iniPath is not null)
                        File.Delete(iniPath);
                }
                catch { }
            }
        }

        /// <remarks>
        /// Thread-unsafe.
        /// CPU-intensive.
        /// </remarks>
        public static async Task<bool> DownloadLauncherAsync(string installerUrl, string toDir, CancellationToken cancellationToken)
        {
            string? installerPath;

            installerPath = await DownloadAsync(installerUrl, toDir, cancellationToken).ConfigureAwait(false);
            if (installerPath == null)
                return false;
            installerPath = await DecompressIfNecessaryAsync(installerPath);
            if (installerPath is null)
                return false;

            try
            {
                Process installer = new();
                installer.StartInfo.FileName = installerPath;
                installer.StartInfo.Arguments = "/VERYSILENT /TASKS=\"\"";
                if (!installer.Start())
                    return false;
                await installer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (installer.ExitCode == 0)
                    return true;
                else
                    return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(installerPath);
                }
                catch { }
            }
        }

        /// <remarks>
        /// If <paramref name="toDir"/> is <c>null</c>, it will be saved to TempDirectoryPath.
        /// Thread-safe.
        /// CPU-intensive.
        /// </remarks>
        /// <returns>
        /// File location or <c>null</c> if it failed or was cancelled.
        /// <para>Caller is responsible for cleaning up the file.</para>
        /// </returns>
        public static async Task<string?> DownloadServerDBAsync(string archiveUrl, string? toDir, CancellationToken cancellationToken)
        {
            string? path = await DownloadAsync(archiveUrl, toDir, cancellationToken).ConfigureAwait(false);
            if(path is null)
                return null;
            return await DecompressIfNecessaryAsync(path);
        }

        /// <remarks>
        /// This doesn't decompress.
        /// If <paramref name="toDir"/> is <c>null</c>, it will be saved to TempDirectoryPath.
        /// Thread-safe.
        /// </remarks>
        /// <returns>File location or <c>null</c> if it failed or was cancelled.</returns>
        public static Task<string?> DownloadGameFileAsync(int id, int toVersion, int? fromVersion, string? toDir, CancellationToken cancellationToken)
        {
            string url;
            if (fromVersion is null)
                url = $"{downloadBaseUrl}patch/{id}-{toVersion}.cab";
            else
                url = $"{downloadBaseUrl}patch/{id}-{fromVersion}-{toVersion}.cab";
            return DownloadAsync(url, toDir, cancellationToken);
        }

        /// <remarks>
        /// It will be saved to the same directory. The original compressed file will be deleted.
        /// Thread-safe.
        /// CPU-intensive.
        /// </remarks>
        /// <param name="lzmaDecoder">If it's <c>null</c>, the internal decoder which can be used by one thread only is used.</param>
        /// <returns>File location or <c>null</c> if it failed.</returns>
        public static async Task<string?> DecompressIfNecessaryAsync(string path)
        {
            if (Path.GetExtension(path) == ".cab")
            {
                string dir = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
                string cabName = Path.GetFileName(path);
                string fileName = Path.GetFileNameWithoutExtension(cabName);
                try
                {
                    await DecompressLZMAAsync(dir + cabName, dir + fileName);
                    return dir + fileName;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    try
                    {
                        File.Delete(dir + cabName);
                    }
                    catch { }
                }
            }
            else
                return path;
        }

        /// <remarks>
        /// <paramref name="toPath"/> will be overwritten and <paramref name="fromPath"/> will be deleted.
        /// Thread-safe.
        /// </remarks>
        public static async Task<bool> DecodeAsync(string fromPath, string toPath, bool isDelta)
        {
            if (isDelta)
            {
                bool result = await ApplyDeltaAsync(toPath, fromPath).ConfigureAwait(false);
                try
                {
                    File.Delete(fromPath);
                }
                catch { }
                return result;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(toPath)); //toPath is not null and should point to a file (not a root directory)
                try
                {
                    File.Move(fromPath, toPath, true);
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    try
                    {
                        File.Delete(fromPath);
                    }
                    catch { }
                }
            }
        }

        public enum DiskType
        {
            Unspicified,
            HDD,
            SSD,
            SCM
        }

        public static DiskType? GetDiskType(string path)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var version = Environment.OSVersion.Version;
            if (version.Major < 6
               || (version.Major == 6 && version.Minor < 2)) //before windows 8
                return null; //not supported

            if (path.Length < 2 || path[1] != ':' || !Char.IsAsciiLetter(path[0]))
                return null;
            char driveLetter = Char.ToUpper(path[0]);

            using ManagementObjectCollection collection_partition = new ManagementObjectSearcher(
                @"\\.\Root\Microsoft\Windows\Storage",
                "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"
            ).Get();
            foreach (ManagementBaseObject object_partition in collection_partition)
                if ((Char)object_partition["DriveLetter"] == driveLetter)
                {
                    UInt32 diskNumber = (UInt32)object_partition["DiskNumber"];
                    using ManagementObjectCollection collection_disk = new ManagementObjectSearcher(
                        @"\\.\Root\Microsoft\Windows\Storage",
                        "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"
                    ).Get();
                    foreach (ManagementBaseObject object_disk in collection_disk)
                        if ((String)object_disk["DeviceId"] == diskNumber.ToString())
                            switch((UInt16)object_disk["MediaType"])
                            {
                                case 0:
                                    return DiskType.Unspicified;
                                case 3:
                                    return DiskType.HDD;
                                case 4:
                                    return DiskType.SSD;
                                case 5:
                                    return DiskType.SCM;
                            }
                }
            return null;
        }

        /// <remarks>
        /// Thread-safe.
        /// </remarks>
        /// <returns>In bytes or <c>-1</c> if it doesn't exist.</returns>
        public static long GetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return -1;
            }
        }

        private static readonly MD5 md5 = MD5.Create();

        /// <remarks>
        /// Thread-safe.
        /// CPU-intensive.
        /// </remarks>
        /// <param name="md5">If it's <c>null</c>, the internal md5 which can be used by one thread only is used.</param>
        /// <returns>In lower-case or <c>null</c> if it failed.</returns>
        public static string? GetFileMD5(string path, MD5? md5 = null)
        {
            try
            {
                using FileStream fs = File.OpenRead(path);
                md5 ??= FS.md5;
                lock (md5)
                {
                    return Convert.ToHexString(md5.ComputeHash(fs)).ToLower();
                }
            }
            catch
            {
                return null;
            }
        }

        private static XmlSerializer? xmlSerializer = new(typeof(List<GameFileEntry>));

        /// <remarks>
        /// Thread-unsafe.
        /// </remarks>
        public static bool SaveGameFileRegistry(IDictionary<(int id, int? fromVersion, int toVersion), string> registry, string path)
        {
            try
            {
                List<GameFileEntry> list = new();
                foreach (var pair in registry)
                    list.Add(new()
                    {
                        Id = pair.Key.id,
                        FromVersion = pair.Key.fromVersion,
                        ToVersion = pair.Key.toVersion,
                        Path = pair.Value
                    });
                using FileStream fs = File.Open(path, FileMode.Create);
                xmlSerializer ??= new(typeof(List<GameFileEntry>));
                xmlSerializer.Serialize(fs, list);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <remarks>
        /// Thread-unsafe.
        /// </remarks>
        public static List<GameFileEntry>? LoadGameFileRegistry(string path)
        {
            try
            {
                using FileStream fs = File.Open(path, FileMode.Open);
                xmlSerializer ??= new(typeof(List<GameFileEntry>));
                return (List<GameFileEntry>?)xmlSerializer.Deserialize(fs);
            }
            catch
            {
                return null;
            }
        }
        
        //Helpers

        /// <remarks>
        /// If <paramref name="toDir"/> is <c>null</c>, it will be saved to TempDirectoryPath.
        /// Thread-safe.
        /// </remarks>
        /// <returns>File location or <c>null</c> if it failed or was cancelled.</returns>
        private static async Task<string?> DownloadAsync(string url, string? toDir, CancellationToken cancellationToken)
        {
            toDir ??= TempDirectoryPath;
            if (!Path.EndsInDirectorySeparator(toDir))
                toDir += Path.DirectorySeparatorChar;

            string fileName = url.Substring(url.LastIndexOfAny(['\\', '/']) + 1);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                Stream ws = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(toDir);
                FileStream fs = File.Open(toDir + fileName, FileMode.Create);
                await using (ws.ConfigureAwait(false))
                await using (fs.ConfigureAwait(false))
                {
                    await ws.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                    return toDir + fileName;
                }
            }
            catch
            {
                try
                {
                    File.Delete(toDir + fileName);
                }
                catch { }
                return null;
            }
        }

        /// <remarks>
        /// Thread-safe.
        /// CPU-intensive.
        /// </remarks>
        /// <param name="decoder">If it's <c>null</c>, the internal decoder which can be used by one thread only is used.</param>
        private static async Task DecompressLZMAAsync(string inPath, string outPath)
        {
            await using FileStream iFS = File.Open(inPath, FileMode.Open);
            await using LzmaStream lzmaStream = new(iFS, CompressionMode.Decompress, leaveOpen: false);
            await using FileStream oFS = File.Open(outPath, FileMode.Create);
            await lzmaStream.CopyToAsync(oFS);
        }

        /// <remarks>
        /// <paramref name="basePath"/> + ".new" is used as the temperary output.
        /// Thread-safe.
        /// </remarks>
        private static async Task<bool> ApplyDeltaAsync(string basePath, string deltaPath)
        {
            string outPath = basePath + ".new";
            try
            {
                FileStream baseFs = File.OpenRead(basePath);
                FileStream deltaFs = File.OpenRead(deltaPath);
                FileStream outFs = File.OpenWrite(outPath);

                VCDiffResult result;
                await using (baseFs.ConfigureAwait(false))
                await using (deltaFs.ConfigureAwait(false))
                await using (outFs.ConfigureAwait(false))
                {
                    VcDecoder decoder = new(baseFs, deltaFs, outFs);
                    var tuple = await decoder.DecodeAsync().ConfigureAwait(false);
                    result = tuple.result;
                }
                if (result == VCDiffResult.SUCCESS)
                {
                    File.Move(outPath, basePath, true);
                    return true;
                }
                else
                    return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(outPath);
                }
                catch { }
            }
        }
    }
}

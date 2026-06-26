using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace LostArkPatcher
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private string gameDirectory = String.Empty;
        /// <remarks>Always ends with a directory seprartor.</remarks>
        public string GameDirectory
        {
            get
            {
                return gameDirectory;
            }
            set
            {
                if (value != String.Empty && !Path.EndsInDirectorySeparator(value))
                    gameDirectory = value + Path.DirectorySeparatorChar;
                else
                    gameDirectory = value;
                OnPropertyChanged();
                Log = new();
                Model.GetLauncherCurrentVersionAsync(GameDirectory)
                .ContinueWith((preTask) => {
                    LauncherCurrentVersion = preTask.Result;
                });
                Model.GetGameCurrentVersionAsync(GameDirectory)
                .ContinueWith((preTask) => {
                    GameCurrentVersion = preTask.Result;
                });
            }
        }

        private string? launcherCurrentVersion;
        public string? LauncherCurrentVersion
        {
            get
            {
                return launcherCurrentVersion;
            }
            set
            {
                launcherCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? launcherLatestVersion;
        public string? LauncherLatestVersion
        {
            get
            {
                return launcherLatestVersion;
            }
            set
            {
                launcherLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private int? gameCurrentVersion;
        public int? GameCurrentVersion
        {
            get
            {
                return gameCurrentVersion;
            }
            set
            {
                gameCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? gameLatestVersion;
        public string? GameLatestVersion
        {
            get
            {
                return gameLatestVersion;
            }
            set
            {
                gameLatestVersion = value;
                OnPropertyChanged();
            }
        }

        public class LogLine
        {
            private string text = String.Empty;
            public string Text
            {
                get
                {
                    return text;
                }
                set
                {
                    text = value;
                    OnContentChanged();
                }
            }

            public event EventHandler<string>? ContentChanged;

            protected void OnContentChanged()
            {
                ContentChanged?.Invoke(this, ToString());
            }

            public LogLine() { }

            public LogLine(string text)
            {
                this.text = text;
            }

            public override string ToString()
            {
                return Text;
            }
        }

        public enum LogLineState
        {
            Pending,
            Interrupted,
            Finished,
            Failed
        }

        public class LogLinePending : LogLine
        {
            private string waitingIndicator = "...";
            public string WaitingIndicator
            {
                get
                {
                    return waitingIndicator;
                }
                set
                {
                    waitingIndicator = value;
                    OnContentChanged();
                }
            }

            private string interruptedIndicator = "中斷";
            public string InterruptedIndicator
            {
                get
                {
                    return interruptedIndicator;
                }
                set
                {
                    interruptedIndicator = value;
                    if (Status == LogLineState.Interrupted)
                        OnContentChanged();
                }
            }

            private string finishedIndicator = "完成";
            public string FinishedIndicator
            {
                get
                {
                    return finishedIndicator;
                }
                set
                {
                    finishedIndicator = value;
                    if (Status == LogLineState.Finished)
                        OnContentChanged();
                }
            }

            private string failedIndicator = "失敗";
            public string FailedIndicator
            {
                get
                {
                    return failedIndicator;
                }
                set
                {
                    failedIndicator = value;
                    if (Status == LogLineState.Failed)
                        OnContentChanged();
                }
            }

            private LogLineState status = LogLineState.Pending;
            public LogLineState Status
            {
                get
                {
                    return status;
                }
                set
                {
                    status = value;
                    OnContentChanged();
                }
            }

            public LogLinePending() { }

            public LogLinePending(string caption) : base(caption) { }

            public override string ToString()
            {
                return Status switch
                {
                    LogLineState.Pending => Text + WaitingIndicator,
                    LogLineState.Interrupted => Text + WaitingIndicator + InterruptedIndicator,
                    LogLineState.Finished => Text + WaitingIndicator + FinishedIndicator,
                    LogLineState.Failed => Text + WaitingIndicator + FailedIndicator,
                    _ => base.ToString(),
                };
            }
        }

        public class LogLineProgress : LogLinePending
        {
            private float percent = 0;
            public float Percent
            {
                get
                {
                    return percent;
                }
                set
                {
                    percent = value;
                    if (Status == LogLineState.Pending)
                        OnContentChanged();
                }
            }

            public LogLineProgress() { }

            public LogLineProgress(string caption) : base(caption) { }

            public override string ToString()
            {
                return Status switch
                {
                    LogLineState.Pending => Text + WaitingIndicator + $"{Percent:F2}%",
                    _ => base.ToString(),
                };
            }
        }

        public class LogLines
        {
            public event EventHandler<string>? ContentChanged;

            protected void OnContentChanged()
            {
                ContentChanged?.Invoke(this, ToString());
            }

            private readonly List<LogLine> lines = new();

            private void handlerOnLineContentChanged(object? sender, string arg)
            {
                OnContentChanged();
            }

            public void AddLine(LogLine line)
            {
                lines.Add(line);
                line.ContentChanged += handlerOnLineContentChanged;
                OnContentChanged();
            }

            public bool RemoveLine(LogLine line)
            {
                bool result = lines.Remove(line);
                if (result)
                {
                    line.ContentChanged -= handlerOnLineContentChanged;
                    OnContentChanged();
                }
                return result;
            }

            public override string ToString()
            {
                return String.Join('\n', lines);
            }
        }

        private void handlerOnLogContentChanged(object? sender, string arg)
        {
            OnPropertyChanged("LogView");
        }
        private LogLines? log = null;
        public LogLines? Log
        {
            get
            {
                return log;
            }
            set
            {
                if(log is not null)
                    log.ContentChanged -= handlerOnLogContentChanged;
                log = value;
                if (log is not null)
                    log.ContentChanged += handlerOnLogContentChanged;
                OnPropertyChanged("LogView");
            }
        }
        public string? LogView => Log?.ToString();

        public enum Operation
        { 
            Update,
            Repair
        }

        private Operation? operationInProgress = null;

        public Operation? OperationInProgress
        {
            get
            {
                return operationInProgress;
            }
            set
            {
                operationInProgress = value;
                ChooseGameDirectory.NotifyCanExecuteChanged();
                UpdateOrCancelAsync.NotifyCanExecuteChanged();
                RepairOrCancelAsync.NotifyCanExecuteChanged();
                switch (operationInProgress)
                {
                    case Operation.Update:
                        RepairArrowToggleVisible = true;
                        RepairArrowToggleEnabled = false;
                        break;
                    case Operation.Repair:
                        RepairArrowToggleVisible = false;
                        break;
                    default:
                        RepairArrowToggleVisible = true;
                        RepairArrowToggleEnabled = true;
                        break;
                }
                RepairPopupOpen = false;
            }
        }

        private CancellationTokenSource? cancellationTokenSource;

        public enum OperationMode
        {
            Quick,
            Last,
            Full
        }

        public RelayCommand ChooseGameDirectory { get; }
        public AsyncRelayCommand UpdateOrCancelAsync { get; }
        public AsyncRelayCommand<OperationMode> RepairOrCancelAsync { get; }

        public MainViewModel()
        {
            GameDirectory = Properties.Settings.Default.gameDirectory;
            Model.GetLauncherLatestVersionAsync()
            .ContinueWith((preTask) => {
                LauncherLatestVersion = preTask.Result;
            });
            Model.GetGameLatestVersionAsync()
            .ContinueWith((preTask) => {
                GameLatestVersion = preTask.Result;
            });
            ChooseGameDirectory = new(ExecuteChooseGameDirectory, CanChooseGameDirectory);
            UpdateOrCancelAsync = new(ExecuteUpdateOrCancelAsync, CanUpdateOrCancel, AsyncRelayCommandOptions.AllowConcurrentExecutions);
            RepairOrCancelAsync = new(ExecuteRepairOrCancelAsync, CanRepairOrCancel, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        }

        public void OnWindowClosed(object? sender, EventArgs e)
        {
            Properties.Settings.Default.gameDirectory = GameDirectory;
            Properties.Settings.Default.Save();
            Model.CleanUp();
        }

        private void ExecuteChooseGameDirectory()
        {
            OpenFolderDialog dialog = new()
            {
                Title = "選取遊戲資料夾",
                Multiselect = false
            };
            if (dialog.ShowDialog() == false)
                return;
            GameDirectory = dialog.FolderName;
        }

        private bool CanChooseGameDirectory()
        {
            return OperationInProgress == null;
        }

        private string updateButtonText = "更新";
        public string UpdateButtonText
        {
            get
            {
                return updateButtonText;
            }
            set
            {
                updateButtonText = value;
                OnPropertyChanged();
            }
        }

        public async Task ExecuteUpdateOrCancelAsync()
        {
            if (OperationInProgress == Operation.Update)
            {
                if (cancellationTokenSource is not null && !cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                    UpdateButtonText = "正在中斷...";
                }
                return;
            }
            else if (OperationInProgress != null)
                return;

            if (!CheckGameDirectory() || !CheckRunningApps())
                return;

            cancellationTokenSource = new();
            OperationInProgress = Operation.Update;
            string originalButtonText = UpdateButtonText;
            UpdateButtonText = "中斷";
            try
            {
                Log = new();
                LogLinePending lastLine;

                if (LauncherLatestVersion is null || Model.LauncherInstallerUrl is null)
                    LauncherLatestVersion = await Model.GetLauncherLatestVersionAsync();
                if (LauncherLatestVersion is null)
                    Log.AddLine(new LogLine("啟動器最新版本未知，略過更新"));
                else if (LauncherCurrentVersion is null || LauncherCurrentVersion != LauncherLatestVersion)
                {
                    lastLine = new LogLinePending("更新啟動器");
                    Log.AddLine(lastLine);
                    if (Model.LauncherInstallerUrl is null)
                        lastLine.Status = LogLineState.Failed;
                    else if (await FS.DownloadLauncherAsync(Model.LauncherInstallerUrl, GameDirectory, cancellationTokenSource.Token))
                    {
                        lastLine.Status = LogLineState.Finished;
                        LauncherCurrentVersion = await Model.GetLauncherCurrentVersionAsync(GameDirectory);
                    }
                    else if (cancellationTokenSource.IsCancellationRequested)
                        lastLine.Status = LogLineState.Interrupted;
                    else
                        lastLine.Status = LogLineState.Failed;
                }

                if (cancellationTokenSource.IsCancellationRequested)
                {
                    Log.AddLine(new LogLine("已中斷"));
                    return;
                }

                if (!File.Exists(GameDirectory + Model.LOCALDB_FILENAME))
                {
                    Log.AddLine(new LogLine("找不到本地資料庫，請先修復"));
                    return;
                }

                lastLine = new LogLinePending("檢查本地資料庫");
                Log.AddLine(lastLine);
                bool checkingSchema;
                await using (DB db = new(GameDirectory + Model.LOCALDB_FILENAME))
                    //SQLite doesn't support asynchronous operations
                    checkingSchema = await Task.Run(() => db.CheckSchemaAsync());
                lastLine.Status = LogLineState.Finished;
                if (!checkingSchema)
                {
                    Log.AddLine(new LogLine("發現錯誤，請先修復"));
                    return;
                }

                if (Model.CachedServerDBPath is null || !File.Exists(Model.CachedServerDBPath))
                {
                    lastLine = new LogLinePending("下載伺服器資料庫");
                    Log.AddLine(lastLine);
                    await Model.CacheServerDB();
                    if (Model.CachedServerDBPath is null || !File.Exists(Model.CachedServerDBPath))
                    {
                        lastLine.Status = LogLineState.Failed;
                        return;
                    }
                    else
                        lastLine.Status = LogLineState.Finished;
                }

                lastLine = new LogLineProgress("更新所有檔案");
                Log.AddLine(lastLine);
                (int updated, int failed) = await Model.UpdateGameFilesAsync(GameDirectory, Model.CachedServerDBPath, cancellationTokenSource.Token, new Progress<float>((ratio) => {
                    ((LogLineProgress)lastLine).Percent = ratio * 100;
                }));
                if (cancellationTokenSource.IsCancellationRequested)
                    lastLine.Status = LogLineState.Interrupted;
                else
                    lastLine.Status = LogLineState.Finished;
                if (updated > 0)
                    Log.AddLine(new LogLine($"  已更新：{updated}"));
                if (failed > 0)
                    Log.AddLine(new LogLine($"  更新失敗：{failed}"));
                GameCurrentVersion = await Model.GetGameCurrentVersionAsync(GameDirectory);
            }
            finally
            {
                OperationInProgress = null;
                UpdateButtonText = originalButtonText;
            }
        }

        public bool CanUpdateOrCancel()
        {
            return OperationInProgress is null || OperationInProgress == Operation.Update;
        }

        private string repairButtonText = "修復";
        public string RepairButtonText
        {
            get
            {
                return repairButtonText;
            }
            set
            {
                repairButtonText = value;
                OnPropertyChanged();
            }
        }

        private bool repairArrowToggleVisible = true;
        public bool RepairArrowToggleVisible
        {
            get
            {
                return repairArrowToggleVisible;
            }
            set
            {
                repairArrowToggleVisible = value;
                OnPropertyChanged();
            }
        }

        private bool repairArrowToggleEnabled = true;
        public bool RepairArrowToggleEnabled
        {
            get
            {
                return repairArrowToggleEnabled;
            }
            set
            {
                repairArrowToggleEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool repairPopupOpen = false;
        public bool RepairPopupOpen
        {
            get
            {
                return repairPopupOpen;
            }
            set
            {
                repairPopupOpen = value;
                OnPropertyChanged();
            }
        }

        public async Task ExecuteRepairOrCancelAsync(OperationMode mode)
        {
            if (OperationInProgress == Operation.Repair)
            {
                if (cancellationTokenSource is not null && !cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                    RepairButtonText = "正在中斷...";
                }
                return;
            }
            else if (OperationInProgress != null)
                return;

            if (!CheckGameDirectory() || !CheckRunningApps())
                return;

            cancellationTokenSource = new();
            OperationInProgress = Operation.Repair;
            string originalButtonText = RepairButtonText;
            RepairButtonText = "中斷";
            try
            {
                Log = new();
                LogLinePending lastLine, stageLine;

                if(Model.CachedServerDBPath is null || !File.Exists(Model.CachedServerDBPath))
                {
                    lastLine = new LogLinePending("下載伺服器資料庫");
                    Log.AddLine(lastLine);
                    await Model.CacheServerDB();
                    if (Model.CachedServerDBPath is null || !File.Exists(Model.CachedServerDBPath))
                    {
                        lastLine.Status = LogLineState.Failed;
                        return;
                    }
                    else
                        lastLine.Status = LogLineState.Finished;
                }

                stageLine = new LogLinePending("檢查本地資料庫");
                Log.AddLine(stageLine);
                try
                {
                    int deleted, inserted, modified;
                    bool someAccessesDenied;

                    await using (DB db = new(GameDirectory + Model.LOCALDB_FILENAME, Model.CachedServerDBPath))
                    {
                        lastLine = new LogLinePending("├檢查結構");
                        Log.AddLine(lastLine);
                        //SQLite doesn't support asynchronous operations
                        bool checkingSchema = await Task.Run(() => db.CheckSchemaAsync());
                        lastLine.Status = LogLineState.Finished;
                        if (!checkingSchema)
                        {
                            lastLine = new LogLinePending("├修復結構");
                            Log.AddLine(lastLine);
                            //SQLite doesn't support asynchronous operations
                            await Task.Run(() => db.FixSchemaAsync());
                            lastLine.Status = LogLineState.Finished;
                        }

                        if (cancellationTokenSource.IsCancellationRequested)
                        {
                            Log.AddLine(new LogLine("已中斷"));
                            return;
                        }

                        lastLine = new LogLinePending("├檢查項目");
                        Log.AddLine(lastLine);
                        //SQLite doesn't support asynchronous operations
                        (deleted, inserted) = await Task.Run(() => db.FixFileKeysAsync());
                        lastLine.Status = LogLineState.Finished;
                    }

                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.AddLine(new LogLine("已中斷"));
                        return;
                    }

                    lastLine = new LogLineProgress("└檢查資料");
                    Log.AddLine(lastLine);
                    (deleted, modified, someAccessesDenied) = await Model.RepairGameFileEntriesAsync(GameDirectory, Model.CachedServerDBPath, CovertToModelRepairMode(mode), cancellationTokenSource.Token, new Progress<float>((ratio) => {
                        ((LogLineProgress)lastLine).Percent = ratio * 100;
                    }));
                    if (cancellationTokenSource.IsCancellationRequested)
                        lastLine.Status = LogLineState.Interrupted;
                    else
                        lastLine.Status = LogLineState.Finished;
                    if (deleted + modified > 0)
                        Log.AddLine(new LogLine($"    已修復：{deleted + modified}"));
                    if (someAccessesDenied)
                        Log.AddLine(new LogLine($"    警告：已略過無法存取的檔案"));
                    GameCurrentVersion = await Model.GetGameCurrentVersionAsync(GameDirectory);
                }
                finally
                {
                    if (cancellationTokenSource.IsCancellationRequested)
                        stageLine.Status = LogLineState.Interrupted;
                    else
                        stageLine.Status = LogLineState.Finished;
                }

                if (cancellationTokenSource.IsCancellationRequested)
                {
                    Log.AddLine(new LogLine("已中斷"));
                    return;
                }

                await using (DB db = new(GameDirectory + Model.LOCALDB_FILENAME, Model.CachedServerDBPath))
                {
                    //SQLite doesn't support asynchronous operations
                    int outdated = await Task.Run(() => db.GetOutdatedCountAsync());
                    if (outdated > 0)
                        Log.AddLine(new LogLine($"需要更新：{outdated}"));
                }
            }
            finally
            {
                OperationInProgress = null;
                RepairButtonText = originalButtonText;
            }

        }

        public bool CanRepairOrCancel(OperationMode mode)
        {
            return OperationInProgress is null || OperationInProgress == Operation.Repair;
        }

        private bool CheckGameDirectory()
        {
            if (GameDirectory == String.Empty)
            {
                MessageBox.Show("未指定遊戲位置。", "錯誤");
                return false;
            }
            if (!Directory.Exists(GameDirectory))
            {
                MessageBox.Show("遊戲位置不存在，請先建立資料夾。", "錯誤");
                return false;
            }
            if ((!File.Exists(GameDirectory + Model.LOCALDB_FILENAME) || !File.Exists(GameDirectory + Model.LAUNCHER_FILENAME))
                && MessageBox.Show("這似乎不是遊戲資料夾。\n其中的檔案可能會被覆蓋。\n你確定要繼續?", "警告", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
                return false;
            return true;
        }

        private static bool CheckRunningApps()
        {
            if ((Process.GetProcessesByName("Launcher").Length > 0 || Process.GetProcessesByName("LOSTARK").Length > 0)
                && MessageBox.Show("啟動器或遊戲似乎正在運行。\n此時操作可能會產生問題。\n你確定要繼續?", "警告", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
                return false;
            return true;
        }

        private static Model.RepairMode CovertToModelRepairMode(OperationMode mode)
        {
            switch (mode)
            {
                case OperationMode.Quick:
                    return Model.RepairMode.Quick;
                case OperationMode.Last:
                    return Model.RepairMode.LastVersion;
                case OperationMode.Full:
                    return Model.RepairMode.Full;
                default:
                    throw new ArgumentException(null, nameof(mode));
            }
        }
    }
}

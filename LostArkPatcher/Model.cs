using System.IO;

namespace LostArkPatcher
{
    internal static class Model
    {
        private static string _gameDirectory = String.Empty;
        public static event EventHandler<string>? GameDirectoryChanged;
        /// <value>Always ends with a directory seprartor.</value>
        public static string gameDirectory
        {
            get
            {
                return _gameDirectory;
            }
            set
            {
                if (value != String.Empty && !Path.EndsInDirectorySeparator(value))
                    _gameDirectory = value + Path.DirectorySeparatorChar;
                else
                    _gameDirectory = value;
                GameDirectoryChanged?.Invoke(null, _gameDirectory);
            }
        }

        private static string? _launcherCurrentVersion;
        public static event EventHandler<string?>? LauncherCurrentVersionChanged;
        public static string? launcherCurrentVersion
        {
            get
            {
                return _launcherCurrentVersion;
            }
            set
            {
                _launcherCurrentVersion = value;
                LauncherCurrentVersionChanged?.Invoke(null, _launcherCurrentVersion);
            }
        }

        private static string? _launcherLatestVersion;
        public static event EventHandler<string?>? LauncherLatestVersionChanged;
        public static string? launcherLatestVersion
        {
            get
            {
                return _launcherLatestVersion;
            }
            set
            {
                _launcherLatestVersion = value;
                LauncherLatestVersionChanged?.Invoke(null, _launcherLatestVersion);
            }
        }

        private static int? _gameCurrentVersion;
        public static event EventHandler<int?>? GameCurrentVersionChanged;
        public static int? gameCurrentVersion
        {
            get
            {
                return _gameCurrentVersion;
            }
            set
            {
                _gameCurrentVersion = value;
                GameCurrentVersionChanged?.Invoke(null, _gameCurrentVersion);
            }
        }

        private static string? _gameLatestVersion;
        public static event EventHandler<string?>? GameLatestVersionChanged;
        public static string? gameLatestVersion
        {
            get
            {
                return _gameLatestVersion;
            }
            set
            {
                _gameLatestVersion = value;
                GameLatestVersionChanged?.Invoke(null, _gameLatestVersion);
            }
        }

        public class Log
        {
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
                        OnLogLineChanged(ToString());
                    }
                }

                public event EventHandler<string>? LogLineChanged;

                protected void OnLogLineChanged(string arg)
                {
                    LogLineChanged?.Invoke(this, arg);
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
                        OnLogLineChanged(ToString());
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
                        OnLogLineChanged(ToString());
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
                        OnLogLineChanged(ToString());
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
                        OnLogLineChanged(ToString());
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
                        OnLogLineChanged(ToString());
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
                        OnLogLineChanged(ToString());
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

            public event EventHandler<string>? LogChanged;

            protected void OnLogChanged(string arg)
            {
                LogChanged?.Invoke(this, arg);
            }

            private void handlerOnLineChange(object? sender, string arg)
            {
                OnLogChanged(ToString());
            }

            private readonly List<LogLine> lines = new();

            public void AddLine(LogLine line)
            {
                lines.Add(line);
                line.LogLineChanged += handlerOnLineChange;
                OnLogChanged(ToString());
            }

            public bool RemoveLine(LogLine line)
            {
                bool result = lines.Remove(line);
                if (result)
                {
                    line.LogLineChanged -= handlerOnLineChange;
                    OnLogChanged(ToString());
                }
                return result;
            }

            public override string ToString()
            {
                return String.Join('\n', lines);
            }
        }

        private static Log? _log = null;
        public static event EventHandler<Log?>? LogChanged;
        public static event EventHandler<string>? LogContentChanged;
        private static void handlerOnLogContentChange(object? sender, string arg)
        {
            LogContentChanged?.Invoke(null, arg);
        }
        public static Log? log
        {
            get
            {
                return _log;
            }
            set
            {
                if(_log is not null)
                    _log.LogChanged -= handlerOnLogContentChange;
                _log = value;
                if (_log is not null)
                    _log.LogChanged += handlerOnLogContentChange;
                LogChanged?.Invoke(null, _log);
            }
        }

        private static bool _operationInProgress = false;
        public static event EventHandler<bool>? OperationInProgressChanged;
        public static bool operationInProgress
        {
            get
            {
                return _operationInProgress;
            }
            set
            {
                _operationInProgress = value;
                OperationInProgressChanged?.Invoke(null, _operationInProgress);
            }
        }

        public const string LAUNCHER_FILENAME = "Launcher.exe";
        public const string LOCALDB_FILENAME = "local.db";

        public static string? launcherInstallerUrl = null;
        public static string? serverDBArchiveUrl = null;

        public static CancellationTokenSource? cancellationTokenSource;
    }
}

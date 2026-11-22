using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AntigravityChineseHelper
{
    internal sealed class HelperTrayContext : ApplicationContext
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int ShutdownGraceSeconds = 10;
        private static readonly int[] RetryDelaysSeconds = new int[] { 2, 5, 15 };

        private readonly SynchronizationContext _uiContext;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _titleModeItem;
        private readonly ToolStripMenuItem _launchNoticeItem;
        private readonly ToolStripMenuItem _launchItem;
        private readonly ToolStripMenuItem _restartItem;
        private readonly ToolStripMenuItem _reloadItem;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private readonly object _logLock = new object();

        private Process _runner;
        private int _externalRunnerPid;
        private FileStream _runnerLogStream;
        private StreamWriter _runnerLogWriter;
        private bool _wantsTranslation = true;
        private bool _isQuitting;
        private bool _stopping;
        private bool _launching;
        private int _restartingPid;
        private DateTime _restartDeadlineUtc;
        private AntigravityRun _desktopRun;
        private string _launchNoticeMessage;
        private int _launchNoticePid;
        private string _lastError;

        public HelperTrayContext(bool startInBackground)
        {
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_uiContext);

            _menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("正在启动汉化…");
            _statusItem.Enabled = false;
            _menu.Items.Add(_statusItem);

            _titleModeItem = new ToolStripMenuItem(string.Empty);
            _titleModeItem.Enabled = false;
            _titleModeItem.Visible = false;
            _menu.Items.Add(_titleModeItem);

            _launchNoticeItem = new ToolStripMenuItem(string.Empty);
            _launchNoticeItem.Enabled = false;
            _launchNoticeItem.Visible = false;
            _menu.Items.Add(_launchNoticeItem);

            _menu.Items.Add(new ToolStripSeparator());

            _launchItem = new ToolStripMenuItem("通过助手启动 Antigravity", null, OnLaunchClicked);
            _menu.Items.Add(_launchItem);

            _restartItem = new ToolStripMenuItem("重新启动并汉化", null, OnRestartClicked);
            _restartItem.Visible = false;
            _menu.Items.Add(_restartItem);

            _reloadItem = new ToolStripMenuItem("重新加载词库", null, OnReloadClicked);
            _menu.Items.Add(_reloadItem);

            _toggleItem = new ToolStripMenuItem("暂停汉化并恢复英文", null, OnToggleClicked);
            _menu.Items.Add(_toggleItem);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("打开中文词库", null, OnOpenTranslationsClicked));
            _menu.Items.Add(new ToolStripMenuItem("打开使用说明", null, OnOpenReadmeClicked));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("退出并恢复英文", null, OnQuitClicked));

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = LoadTrayIcon();
            _notifyIcon.Text = "Antigravity 汉化助手";
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Visible = true;
            _notifyIcon.MouseUp += OnNotifyIconMouseUp;

            StartReopenPipeListener();

            if (startInBackground)
            {
                StartRunner(0);
            }
            else
            {
                LaunchAntigravity();
            }

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 2000;
            _statusTimer.Tick += delegate { RefreshStatus(); };
            _statusTimer.Start();
        }

        private bool CanLaunchAntigravity
        {
            get { return _wantsTranslation && !_stopping && !_isQuitting && !_launching && _restartingPid == 0; }
        }

        private string SupportFolder
        {
            get { return NodeRuntime.ResolveSupportFolder(); }
        }

        private string HomeFolder
        {
            get
            {
                string installedHome = Path.Combine(SupportFolder, "application");
                if (File.Exists(Path.Combine(installedHome, "runtime", "runner.mjs")))
                {
                    return installedHome;
                }
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, "runtime", "runner.mjs")))
                {
                    return exeDir;
                }
                return installedHome;
            }
        }

        private string RuntimeFolder
        {
            get { return Path.Combine(HomeFolder, "runtime"); }
        }

        private static Icon LoadTrayIcon()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                Icon associated = Icon.ExtractAssociatedIcon(exePath);
                if (associated != null) return associated;
            }
            catch { }
            return IconGenerator.CreateRuntimeIcon();
        }

        private static string ClampTooltip(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Antigravity 汉化助手";
            // NotifyIcon.Text requires fewer than 64 UTF-16 code units on Windows.
            if (value.Length <= 60) return value;
            return value.Substring(0, 59) + "…";
        }

        private int ActiveRunnerPid()
        {
            if (_runner != null)
            {
                try
                {
                    if (!_runner.HasExited) return _runner.Id;
                }
                catch { }
            }
            if (_externalRunnerPid > 0)
            {
                try
                {
                    using (Process ext = Process.GetProcessById(_externalRunnerPid))
                    {
                        if (!ext.HasExited) return _externalRunnerPid;
                    }
                }
                catch { }
                _externalRunnerPid = 0;
            }
            return 0;
        }

        private bool IsRunnerAlive()
        {
            return ActiveRunnerPid() > 0;
        }

        private int TryDetectExistingRunnerPid()
        {
            try
            {
                string pidFile = Path.Combine(SupportFolder, "runner.pid");
                if (File.Exists(pidFile))
                {
                    int pid;
                    if (int.TryParse(File.ReadAllText(pidFile, Encoding.UTF8).Trim(), out pid) && pid > 0)
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            if (!p.HasExited) return pid;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private void OnNotifyIconMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                MethodInfo showContextMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (showContextMenu != null)
                {
                    showContextMenu.Invoke(_notifyIcon, null);
                }
                else
                {
                    SetForegroundWindow(_menu.Handle);
                    _menu.Show(Cursor.Position);
                }
            }
        }

        private void HandleReopenRequest()
        {
            if (_isQuitting) return;
            if (_wantsTranslation)
            {
                LaunchAntigravity();
            }
            else
            {
                SetForegroundWindow(_menu.Handle);
                _menu.Show(Cursor.Position);
            }
        }

        private void StartReopenPipeListener()
        {
            Thread listener = new Thread(delegate()
            {
                string pipeName = HelperApp.GetReopenPipeName();
                while (!_isQuitting)
                {
                    try
                    {
                        using (NamedPipeServerStream server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte))
                        {
                            server.WaitForConnection();
                            if (_isQuitting) break;
                            using (StreamReader reader = new StreamReader(server, Encoding.UTF8))
                            {
                                string command = (reader.ReadLine() ?? string.Empty).Trim();
                                if (string.Equals(command, "reopen", StringComparison.OrdinalIgnoreCase))
                                {
                                    _uiContext.Post(delegate { HandleReopenRequest(); }, null);
                                }
                                else if (string.Equals(command, "quit", StringComparison.OrdinalIgnoreCase))
                                {
                                    _uiContext.Post(delegate { OnQuitClicked(this, EventArgs.Empty); }, null);
                                }
                            }
                        }
                    }
                    catch
                    {
                        if (_isQuitting) break;
                        Thread.Sleep(250);
                    }
                }
            });
            listener.IsBackground = true;
            listener.Start();
        }

        private void AppendLog(string line)
        {
            if (line == null) return;
            lock (_logLock)
            {
                if (_runnerLogWriter != null)
                {
                    try
                    {
                        _runnerLogWriter.WriteLine(line);
                    }
                    catch { }
                }
            }
        }

        private void LogRecovery(string message)
        {
            AppendLog("[Desktop] " + message);
        }

        private void CloseLog()
        {
            lock (_logLock)
            {
                if (_runnerLogWriter != null)
                {
                    try { _runnerLogWriter.Flush(); _runnerLogWriter.Dispose(); } catch { }
                    _runnerLogWriter = null;
                }
                if (_runnerLogStream != null)
                {
                    try { _runnerLogStream.Dispose(); } catch { }
                    _runnerLogStream = null;
                }
            }
        }

        private void StartRunner(int retryAttempt)
        {
            if (!_wantsTranslation || IsRunnerAlive() || _stopping || _isQuitting) return;
            _runner = null;
            CloseLog();

            string scriptPath = Path.Combine(RuntimeFolder, "runner.mjs");
            if (!File.Exists(scriptPath))
            {
                _lastError = "请先运行“安装汉化助手.cmd”安装运行文件";
                RefreshStatus();
                return;
            }

            string nodePath = NodeRuntime.FindExecutable(RuntimeFolder);
            if (string.IsNullOrEmpty(nodePath))
            {
                _lastError = "未找到 Node.js 22 或更新版本，打开说明查看运行要求";
                RefreshStatus();
                return;
            }

            try
            {
                Directory.CreateDirectory(SupportFolder);
                string logPath = Path.Combine(SupportFolder, "helper.log");
                lock (_logLock)
                {
                    _runnerLogStream = HelperLog.Open(logPath);
                    _runnerLogWriter = new StreamWriter(_runnerLogStream, new UTF8Encoding(false));
                    _runnerLogWriter.AutoFlush = true;
                }

                ProcessStartInfo info = BuildNodeProcessInfo(nodePath, "\"" + scriptPath + "\"");
                info.EnvironmentVariables["ANTIGRAVITY_HELPER_SHUTDOWN_TIMEOUT_MS"] = ((ShutdownGraceSeconds - 1) * 1000).ToString();
                info.EnvironmentVariables["ANTIGRAVITY_HELPER_STATE_DIR"] = SupportFolder;

                DateTime startedAtUtc = DateTime.UtcNow;
                Process process = new Process();
                process.StartInfo = info;
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) AppendLog(e.Data);
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) AppendLog(e.Data);
                };
                process.Exited += delegate(object s, EventArgs e)
                {
                    int exitCode = 1;
                    try { exitCode = process.ExitCode; } catch { }
                    _uiContext.Post(delegate
                    {
                        RunnerExited(process, exitCode, startedAtUtc, retryAttempt);
                    }, null);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _runner = process;
                _lastError = null;
                _statusItem.Text = "正在连接 Antigravity…";
            }
            catch (Exception ex)
            {
                _lastError = "启动失败：" + ex.Message;
                CloseLog();
            }
            RefreshStatus();
        }

        private void RunnerExited(Process finished, int exitCode, DateTime startedAtUtc, int retryAttempt)
        {
            if (!ReferenceEquals(_runner, finished))
            {
                try { finished.Dispose(); } catch { }
                return;
            }
            CloseLog();

            if (_wantsTranslation && !_stopping && !_isQuitting && exitCode == 0)
            {
                int adoptedPid = TryDetectExistingRunnerPid();
                if (adoptedPid > 0)
                {
                    _runner = null;
                    try { finished.Dispose(); } catch { }
                    _externalRunnerPid = adoptedPid;
                    _lastError = null;
                    RefreshStatus();
                    return;
                }
            }

            int nextAttempt = (DateTime.UtcNow - startedAtUtc).TotalSeconds >= 60.0 ? 0 : retryAttempt;
            bool crashed = exitCode != 0;
            if (_wantsTranslation && !_stopping && !_isQuitting && crashed && nextAttempt < RetryDelaysSeconds.Length)
            {
                int delaySeconds = RetryDelaysSeconds[nextAttempt];
                _lastError = "汉化后台意外退出（" + exitCode + "），" + delaySeconds + " 秒后自动重试";
                System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();
                retryTimer.Interval = delaySeconds * 1000;
                retryTimer.Tick += delegate
                {
                    retryTimer.Stop();
                    retryTimer.Dispose();
                    if (!ReferenceEquals(_runner, finished))
                    {
                        try { finished.Dispose(); } catch { }
                        return;
                    }
                    _runner = null;
                    try { finished.Dispose(); } catch { }
                    StartRunner(nextAttempt + 1);
                };
                retryTimer.Start();
            }
            else
            {
                _runner = null;
                try { finished.Dispose(); } catch { }
                if (_wantsTranslation && !_stopping && !_isQuitting)
                {
                    _lastError = crashed
                        ? "汉化后台连续退出，请检查 helper.log 后点“重新加载词库”重试"
                        : "汉化后台已停止，点“重新加载词库”恢复";
                }
            }
            RefreshStatus();
        }

        private ProcessStartInfo BuildNodeProcessInfo(string nodePath, string scriptArguments)
        {
            ProcessStartInfo info = NodeRuntime.BuildNodeCommandInfo(nodePath, RuntimeFolder, SupportFolder, scriptArguments);
            info.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            info.EnvironmentVariables.Remove("ELECTRON_NO_ATTACH_CONSOLE");
            return info;
        }

        private void StopRunner(Action completion)
        {
            Process process = _runner;
            int externalPid = _externalRunnerPid;
            if (!IsRunnerAlive())
            {
                _runner = null;
                _externalRunnerPid = 0;
                CloseLog();
                if (completion != null) completion();
                return;
            }

            _stopping = true;
            _statusItem.Text = "正在恢复英文…";
            RefreshStatus();

            string nodePath = NodeRuntime.FindExecutable(RuntimeFolder);
            string scriptPath = Path.Combine(RuntimeFolder, "runner.mjs");
            int runnerPid = externalPid;
            if (process != null)
            {
                try { runnerPid = process.Id; } catch { }
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (!string.IsNullOrEmpty(nodePath) && File.Exists(scriptPath))
                {
                    try
                    {
                        ProcessStartInfo stopInfo = BuildNodeProcessInfo(nodePath, "\"" + scriptPath + "\" --stop");
                        NodeRuntime.RunCommand(stopInfo, (ShutdownGraceSeconds - 1) * 1000);
                    }
                    catch { }
                }

                try
                {
                    if (process != null)
                    {
                        if (!process.WaitForExit(ShutdownGraceSeconds * 1000))
                        {
                            AppendLog("恢复英文的等待时间已结束，结束汉化助手进程。");
                            NodeRuntime.KillProcessTree(runnerPid);
                            process.WaitForExit(2000);
                        }
                    }
                    else if (runnerPid > 0)
                    {
                        using (Process ext = Process.GetProcessById(runnerPid))
                        {
                            if (!ext.WaitForExit(ShutdownGraceSeconds * 1000))
                            {
                                NodeRuntime.KillProcessTree(runnerPid);
                            }
                        }
                    }
                }
                catch { }

                _uiContext.Post(delegate
                {
                    if (ReferenceEquals(_runner, process))
                    {
                        _runner = null;
                    }
                    _externalRunnerPid = 0;
                    if (process != null) { try { process.Dispose(); } catch { } }
                    _stopping = false;
                    CloseLog();
                    if (completion != null) completion();
                }, null);
            });
        }

        private RunnerStatus ReadRunnerStatus()
        {
            int activePid = ActiveRunnerPid();
            if (activePid <= 0)
            {
                return null;
            }
            string statusPath = Path.Combine(SupportFolder, "status.json");
            if (!File.Exists(statusPath))
            {
                return null;
            }
            try
            {
                string json = File.ReadAllText(statusPath, Encoding.UTF8);
                RunnerStatus status = RunnerStatus.Parse(json);
                if (status.Pid.HasValue && status.Pid.Value != activePid)
                {
                    return null;
                }
                return status;
            }
            catch
            {
                return null;
            }
        }

        private void ObserveDesktopRun(int pid)
        {
            DateTime? launchDateUtc = AntigravityRun.LaunchDateUtcOfPid(pid);
            if (!launchDateUtc.HasValue)
            {
                _desktopRun = null;
                return;
            }
            if (_desktopRun != null
                && _desktopRun.Pid == pid
                && _desktopRun.LaunchDateUtc == launchDateUtc.Value
                && (_desktopRun.DebugPortRequested.HasValue || _desktopRun.RestartFailure != null))
            {
                return;
            }
            _desktopRun = new AntigravityRun(pid, launchDateUtc.Value, AntigravityRun.DebugPortRequestedByPid(pid));
        }

        private bool CanRestart(int desktopPid, RunnerStatus status, DateTime nowUtc)
        {
            if (!CanLaunchAntigravity || desktopPid <= 0 || status == null || !status.IsWaitingForApp)
            {
                return false;
            }
            ObserveDesktopRun(desktopPid);
            if (_desktopRun == null)
            {
                return false;
            }
            if (status.DiscoveredAt == null)
            {
                return true;
            }
            return status.DiscoveryDate(_desktopRun.LaunchDateUtc, nowUtc).HasValue;
        }

        private void RestartAntigravity(int desktopPid)
        {
            if (!CanLaunchAntigravity || desktopPid <= 0)
            {
                return;
            }
            LogRecovery("请求 Desktop 正常退出（PID " + desktopPid + "）");
            if (_desktopRun != null)
            {
                _desktopRun.RestartFailure = null;
            }
            if (!AntigravityRun.RequestGracefulExit(desktopPid))
            {
                LogRecovery("Desktop 拒绝退出");
                if (_desktopRun != null)
                {
                    _desktopRun.RestartFailure = "Antigravity 未接受退出请求 · 可在菜单中重试";
                }
                RefreshStatus();
                return;
            }
            _restartingPid = desktopPid;
            _restartDeadlineUtc = DateTime.UtcNow.AddSeconds(20);
            RefreshStatus();
            PollAntigravityExit();
        }

        private void PollAntigravityExit()
        {
            if (_isQuitting || !_wantsTranslation || _restartingPid == 0)
            {
                return;
            }
            DateTime? launchedAt = _desktopRun != null && _desktopRun.Pid == _restartingPid
                ? (DateTime?)_desktopRun.LaunchDateUtc
                : null;
            if (AntigravityRun.HasExited(_restartingPid, launchedAt))
            {
                LogRecovery("Desktop 已退出，准备重新启动");
                _restartingPid = 0;
                LaunchAntigravity();
                return;
            }
            if (DateTime.UtcNow >= _restartDeadlineUtc)
            {
                LogRecovery("等待 Desktop 退出超时");
                _restartingPid = 0;
                if (_desktopRun != null)
                {
                    _desktopRun.RestartFailure = "Antigravity 仍在运行 · 可在菜单中重试";
                }
                RefreshStatus();
                return;
            }
            System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 200;
            pollTimer.Tick += delegate
            {
                pollTimer.Stop();
                pollTimer.Dispose();
                PollAntigravityExit();
            };
            pollTimer.Start();
        }

        private void RefreshStatus()
        {
            _toggleItem.Text = _wantsTranslation ? "暂停汉化并恢复英文" : "恢复汉化";
            _toggleItem.Enabled = !_stopping && !_launching && _restartingPid == 0;
            _launchItem.Text = _launching ? "正在启动 Antigravity…" : "通过助手启动 Antigravity";
            _launchItem.Enabled = CanLaunchAntigravity;
            _reloadItem.Enabled = _wantsTranslation && !_stopping && !_isQuitting;
            _restartItem.Visible = false;
            _restartItem.Enabled = false;
            _titleModeItem.Visible = false;
            _launchNoticeItem.Visible = false;

            if (!_stopping)
            {
                if (!_wantsTranslation)
                {
                    _statusItem.Text = "已暂停 · 英文界面";
                }
                else if (!string.IsNullOrEmpty(_lastError))
                {
                    _statusItem.Text = _lastError;
                }
                else if (_restartingPid != 0)
                {
                    _statusItem.Text = "正在正常退出 Antigravity，以便重新启动汉化…";
                }
                else if (!IsRunnerAlive())
                {
                    _statusItem.Text = "汉化助手尚未运行";
                }
                else if (_launching)
                {
                    _statusItem.Text = "正在通过助手启动 Antigravity…";
                }
                else
                {
                    RunnerStatus status = ReadRunnerStatus();
                    if (status == null)
                    {
                        _statusItem.Text = "正在连接 Antigravity…";
                    }
                    else
                    {
                        _statusItem.Text = status.MenuTitle;
                        int desktopPid = AntigravityRun.FindRunningDesktopPid();
                        if (_launchNoticePid > 0 && desktopPid != _launchNoticePid)
                        {
                            _launchNoticeMessage = null;
                            _launchNoticePid = 0;
                        }
                        else if (!string.IsNullOrEmpty(_launchNoticeMessage) && desktopPid > 0)
                        {
                            _launchNoticePid = desktopPid;
                        }
                        if (!string.IsNullOrEmpty(_launchNoticeMessage))
                        {
                            _launchNoticeItem.Text = _launchNoticeMessage;
                            _launchNoticeItem.Visible = true;
                        }
                        if (desktopPid > 0)
                        {
                            string titleMode = status.TitleModeDescription;
                            if (!string.IsNullOrEmpty(titleMode))
                            {
                                _titleModeItem.Text = titleMode;
                                _titleModeItem.Visible = true;
                            }
                            DateTime nowUtc = DateTime.UtcNow;
                            if (CanRestart(desktopPid, status, nowUtc))
                            {
                                _restartItem.Visible = true;
                                _restartItem.Enabled = CanLaunchAntigravity;
                                if (_desktopRun != null && !string.IsNullOrEmpty(_desktopRun.RestartFailure))
                                {
                                    _statusItem.Text = _desktopRun.RestartFailure;
                                }
                                else if (_desktopRun != null && _desktopRun.NeedsAutomaticRestart(status, nowUtc))
                                {
                                    RestartAntigravity(desktopPid);
                                }
                                else
                                {
                                    _statusItem.Text = "Antigravity 已打开 · 等待连接本机调试接口";
                                }
                            }
                        }
                    }
                }
            }

            _notifyIcon.Text = ClampTooltip("Antigravity 汉化助手：" + _statusItem.Text);
        }

        private void LaunchAntigravity()
        {
            if (!CanLaunchAntigravity) return;
            _launching = true;
            _launchNoticeMessage = null;
            _launchNoticePid = 0;
            if (!IsRunnerAlive()) StartRunner(0);
            RefreshStatus();

            string nodePath = NodeRuntime.FindExecutable(RuntimeFolder);
            string scriptPath = Path.Combine(RuntimeFolder, "runner.mjs");
            if (string.IsNullOrEmpty(nodePath))
            {
                FinishLaunch("未找到 Node.js 22 或更新版本，打开说明查看运行要求。", true);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                bool ready = false;
                while (DateTime.UtcNow < deadline && !_isQuitting && _wantsTranslation && !_stopping)
                {
                    if (!IsRunnerAlive())
                    {
                        _uiContext.Post(delegate
                        {
                            FinishLaunch(_lastError ?? "汉化助手未能启动，请先恢复汉化后重试。", true);
                        }, null);
                        return;
                    }
                    if (ProbeRunnerStatus(nodePath, scriptPath))
                    {
                        ready = true;
                        break;
                    }
                    Thread.Sleep(120);
                }

                if (_isQuitting || !_wantsTranslation || _stopping)
                {
                    _uiContext.Post(delegate
                    {
                        _launching = false;
                        RefreshStatus();
                    }, null);
                    return;
                }

                if (!ready)
                {
                    _uiContext.Post(delegate
                    {
                        FinishLaunch("等待汉化助手启动超时，请重新加载助手后重试。", true);
                    }, null);
                    return;
                }

                RunLaunchCommand(nodePath, scriptPath);
            });
        }

        private bool ProbeRunnerStatus(string nodePath, string scriptPath)
        {
            try
            {
                ProcessStartInfo info = BuildNodeProcessInfo(nodePath, "\"" + scriptPath + "\" --status");
                NodeCommandResult result = NodeRuntime.RunCommand(info, 3000);
                return result.Exited && result.ExitCode == 0 && result.Stdout.Contains("\"running\": true");
            }
            catch
            {
                return false;
            }
        }

        private void RunLaunchCommand(string nodePath, string scriptPath)
        {
            try
            {
                ProcessStartInfo info = BuildNodeProcessInfo(nodePath, "\"" + scriptPath + "\" --launch");
                NodeCommandResult result = NodeRuntime.RunCommand(info, 95000);
                string stdout = result.Stdout;
                string stderr = result.Stderr.Trim();
                bool exited = result.Exited;
                int exitCode = result.ExitCode;

                _uiContext.Post(delegate
                {
                    if (!exited)
                    {
                        FinishLaunch("启动 Antigravity 等待超时，请稍后重试。", true);
                        return;
                    }
                    if (exitCode != 0)
                    {
                        FinishLaunch(string.IsNullOrEmpty(stderr) ? "启动 Antigravity 失败。" : stderr, true);
                        return;
                    }
                    LaunchReply reply = LaunchReply.TryParse(stdout);
                    if (reply != null)
                    {
                        LogRecovery(reply.Launched ? "已启动汉化 Desktop" : ("本次没有启动新 Desktop：" + reply.Message));
                        FinishLaunch(reply.Message, false);
                    }
                    else
                    {
                        FinishLaunch("无法读取 Antigravity 启动结果。", true);
                    }
                }, null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(delegate { FinishLaunch("启动失败：" + ex.Message, true); }, null);
            }
        }

        private void FinishLaunch(string message, bool failed)
        {
            _launching = false;
            if (failed && !string.IsNullOrEmpty(message))
            {
                LogRecovery("启动失败：" + message);
            }
            if (!failed && !string.IsNullOrEmpty(message))
            {
                _launchNoticeMessage = message;
                _launchNoticePid = AntigravityRun.FindRunningDesktopPid();
            }
            RefreshStatus();
            if (!failed || _isQuitting || string.IsNullOrEmpty(message)) return;
            MessageBox.Show(
                message,
                "无法启动 Antigravity",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void OnLaunchClicked(object sender, EventArgs e)
        {
            LaunchAntigravity();
        }

        private void OnRestartClicked(object sender, EventArgs e)
        {
            int desktopPid = AntigravityRun.FindRunningDesktopPid();
            RunnerStatus status = ReadRunnerStatus();
            if (!CanRestart(desktopPid, status, DateTime.UtcNow))
            {
                RefreshStatus();
                return;
            }
            RestartAntigravity(desktopPid);
        }

        private void OnReloadClicked(object sender, EventArgs e)
        {
            if (_stopping || !_wantsTranslation) return;
            if (!IsRunnerAlive())
            {
                StartRunner(0);
                return;
            }
            string nodePath = NodeRuntime.FindExecutable(RuntimeFolder);
            string scriptPath = Path.Combine(RuntimeFolder, "runner.mjs");
            if (string.IsNullOrEmpty(nodePath)) return;

            _statusItem.Text = "正在重新加载词库…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ProcessStartInfo info = BuildNodeProcessInfo(nodePath, "\"" + scriptPath + "\" --reload");
                    NodeCommandResult result = NodeRuntime.RunCommand(info, 15000);
                    string stderr = result.Stderr.Trim();
                    if ((!result.Exited || result.ExitCode != 0) && !string.IsNullOrEmpty(stderr))
                    {
                        AppendLog(stderr);
                    }
                }
                catch (Exception ex)
                {
                    _uiContext.Post(delegate
                    {
                        _lastError = "重新加载失败：" + ex.Message;
                        RefreshStatus();
                    }, null);
                }
            });
        }

        private void OnToggleClicked(object sender, EventArgs e)
        {
            if (_stopping || _launching || _restartingPid != 0) return;
            _wantsTranslation = !_wantsTranslation;
            if (_wantsTranslation)
            {
                StartRunner(0);
            }
            else
            {
                StopRunner(delegate { RefreshStatus(); });
            }
        }

        private void OnOpenTranslationsClicked(object sender, EventArgs e)
        {
            OpenWithShell(Path.Combine(RuntimeFolder, "translations.json"));
        }

        private void OnOpenReadmeClicked(object sender, EventArgs e)
        {
            OpenWithShell(Path.Combine(HomeFolder, "README.md"));
        }

        private static void OpenWithShell(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show("未找到文件：" + filePath, "Antigravity 汉化助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = filePath;
                info.UseShellExecute = true;
                using (Process proc = Process.Start(info)) { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开文件：" + ex.Message, "Antigravity 汉化助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnQuitClicked(object sender, EventArgs e)
        {
            if (_isQuitting) return;
            _isQuitting = true;
            _wantsTranslation = false;
            _restartingPid = 0;
            _statusTimer.Stop();

            StopRunner(delegate
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _menu.Dispose();
                ExitThread();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _statusTimer.Dispose();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _menu.Dispose();
                CloseLog();
            }
            base.Dispose(disposing);
        }
    }

    internal static class HelperApp
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static string GetReopenPipeName()
        {
            string user = Environment.UserName ?? "default";
            StringBuilder sanitized = new StringBuilder();
            for (int i = 0; i < user.Length; i++)
            {
                char ch = user[i];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                {
                    sanitized.Append(ch);
                }
            }
            return "AntigravityChineseHelper.Reopen." + sanitized.ToString();
        }

        private static bool HasArg(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void SignalRunningTrayInstance()
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", GetReopenPipeName(), PipeDirection.Out))
                {
                    client.Connect(1000);
                    using (StreamWriter writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("reopen");
                        writer.Flush();
                    }
                }
            }
            catch { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
            Environment.SetEnvironmentVariable("ELECTRON_NO_ATTACH_CONSOLE", null);

            if (args.Length >= 2 && string.Equals(args[0], "--generate-icon", StringComparison.OrdinalIgnoreCase))
            {
                IconGenerator.WriteIcoFile(args[1]);
                return 0;
            }

            if (HasArg(args, "--watch-antigravity"))
            {
                return AppWatcher.Run();
            }

            try { SetProcessDPIAware(); } catch { }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("运行异常：" + e.Exception.Message, "Antigravity 汉化助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool background = HasArg(args, "--background");
            bool createdNew;
            using (Mutex trayMutex = new Mutex(true, AppWatcher.TrayMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!background)
                    {
                        SignalRunningTrayInstance();
                    }
                    return 0;
                }

                using (HelperTrayContext context = new HelperTrayContext(background))
                {
                    Application.Run(context);
                }
            }
            return 0;
        }
    }
}

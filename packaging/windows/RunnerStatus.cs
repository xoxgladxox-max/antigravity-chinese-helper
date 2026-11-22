using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace AntigravityChineseHelper
{
    public sealed class RunnerStatus
    {
        private static readonly string[] IsoFormats = new string[]
        {
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "o"
        };

        public string Status { get; private set; }
        public int TranslatedWindows { get; private set; }
        public string LastError { get; private set; }
        public int? Pid { get; private set; }
        public string State { get; private set; }
        public string DiscoveredAt { get; private set; }
        public bool? DebugPortPresent { get; private set; }
        public bool? NativeChineseTitles { get; private set; }

        public RunnerStatus(
            string status,
            int translatedWindows,
            string lastError,
            int? pid = null,
            string state = null,
            string discoveredAt = null,
            bool? debugPortPresent = null,
            bool? nativeChineseTitles = null)
        {
            if (string.IsNullOrEmpty(status))
            {
                throw new FormatException("Missing required field: status");
            }
            Status = status;
            TranslatedWindows = translatedWindows;
            LastError = lastError;
            Pid = pid;
            State = state;
            DiscoveredAt = discoveredAt;
            DebugPortPresent = debugPortPresent;
            NativeChineseTitles = nativeChineseTitles;
        }

        public string TitleModeDescription
        {
            get
            {
                if (Status != "running" && Status != "waiting")
                {
                    return null;
                }
                if (NativeChineseTitles.HasValue)
                {
                    if (NativeChineseTitles.Value)
                    {
                        return "会话标题 · 直接生成中文";
                    }
                    return (State == "connected" && TranslatedWindows > 0)
                        ? "会话标题 · 由主窗口自动改为中文"
                        : "会话标题 · 等待主窗口连接";
                }
                return "会话标题 · 等待确认后端状态";
            }
        }

        public bool IsWaitingForApp
        {
            get { return Status == "waiting" && State == "waiting-for-app"; }
        }

        public DateTime? DiscoveryDate(DateTime launchDateUtc, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(DiscoveredAt))
            {
                return null;
            }
            DateTime date;
            if (!DateTime.TryParseExact(
                DiscoveredAt,
                IsoFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out date))
            {
                return null;
            }
            if (date < launchDateUtc)
            {
                return null;
            }
            double ageSeconds = (nowUtc - date).TotalSeconds;
            if (ageSeconds < 0.0 || ageSeconds > 10.0)
            {
                return null;
            }
            return date;
        }

        public string MenuTitle
        {
            get
            {
                if (Status == "error")
                {
                    return "汉化遇到问题：" + (string.IsNullOrEmpty(LastError) ? "打开说明查看日志" : LastError);
                }
                if (Status == "stopped")
                {
                    return "汉化助手已停止";
                }
                if (TranslatedWindows > 0)
                {
                    return "已汉化 " + TranslatedWindows + " 个窗口";
                }
                if (Status == "waiting" || Status == "running")
                {
                    return "运行中 · 等待 Antigravity 窗口";
                }
                return "汉化运行中";
            }
        }

        private static int? ReadOptionalInt(Dictionary<string, object> root, string key)
        {
            object raw;
            if (!root.TryGetValue(key, out raw) || raw == null)
            {
                return null;
            }
            if (raw is int) return (int)raw;
            if (raw is long) return Convert.ToInt32((long)raw);
            return null;
        }

        private static bool? ReadOptionalBool(Dictionary<string, object> root, string key)
        {
            object raw;
            if (!root.TryGetValue(key, out raw) || !(raw is bool))
            {
                return null;
            }
            return (bool)raw;
        }

        private static string ReadOptionalString(Dictionary<string, object> root, string key)
        {
            object raw;
            if (!root.TryGetValue(key, out raw) || !(raw is string))
            {
                return null;
            }
            return (string)raw;
        }

        public static RunnerStatus Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("Empty status JSON");
            }
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null)
            {
                throw new FormatException("Status payload must be a JSON object");
            }
            object rawStatus;
            object rawWindows;
            if (!root.TryGetValue("status", out rawStatus) || !(rawStatus is string))
            {
                throw new FormatException("Missing required string field: status");
            }
            if (!root.TryGetValue("translatedWindows", out rawWindows) || rawWindows == null)
            {
                throw new FormatException("Missing required integer field: translatedWindows");
            }
            int translatedWindows;
            if (rawWindows is int)
            {
                translatedWindows = (int)rawWindows;
            }
            else if (rawWindows is long)
            {
                translatedWindows = Convert.ToInt32((long)rawWindows);
            }
            else
            {
                throw new FormatException("translatedWindows must be an integer");
            }

            return new RunnerStatus(
                (string)rawStatus,
                translatedWindows,
                ReadOptionalString(root, "lastError"),
                ReadOptionalInt(root, "pid"),
                ReadOptionalString(root, "state"),
                ReadOptionalString(root, "discoveredAt"),
                ReadOptionalBool(root, "debugPortPresent"),
                ReadOptionalBool(root, "nativeChineseTitles"));
        }
    }

    public sealed class AntigravityRun
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        public int Pid { get; private set; }
        public DateTime LaunchDateUtc { get; private set; }
        public bool? DebugPortRequested { get; private set; }
        public string RestartFailure { get; set; }

        public AntigravityRun(int pid, DateTime launchDateUtc, bool? debugPortRequested)
        {
            Pid = pid;
            LaunchDateUtc = launchDateUtc;
            DebugPortRequested = debugPortRequested;
        }

        public bool NeedsAutomaticRestart(RunnerStatus status, DateTime nowUtc)
        {
            if (RestartFailure != null || DebugPortRequested != false || status == null || !status.IsWaitingForApp)
            {
                return false;
            }
            DateTime? discovery = status.DiscoveryDate(LaunchDateUtc, nowUtc);
            if (!discovery.HasValue)
            {
                return false;
            }
            return (discovery.Value - LaunchDateUtc).TotalSeconds >= 5.0;
        }

        public static string[] SplitCommandLine(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return null;
            }
            int argc;
            IntPtr argvPtr = CommandLineToArgvW(commandLine, out argc);
            if (argvPtr == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                if (argc <= 0)
                {
                    return new string[0];
                }
                string[] args = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    IntPtr itemPtr = Marshal.ReadIntPtr(argvPtr, i * IntPtr.Size);
                    args[i] = Marshal.PtrToStringUni(itemPtr) ?? string.Empty;
                }
                return args;
            }
            finally
            {
                LocalFree(argvPtr);
            }
        }

        public static bool? ParseCommandLineHasDebugPort(string commandLine)
        {
            string[] args = SplitCommandLine(commandLine);
            if (args == null || args.Length == 0)
            {
                return null;
            }
            for (int i = 1; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument == "--")
                {
                    break;
                }
                if (argument == "--remote-debugging-port"
                    || argument.StartsWith("--remote-debugging-port=", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsMainDesktopCommandLine(string commandLine)
        {
            string[] args = SplitCommandLine(commandLine);
            if (args == null || args.Length == 0)
            {
                return false;
            }
            for (int i = 1; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument == "--")
                {
                    break;
                }
                if (argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase)
                    || argument.IndexOf("sidecar_sdk", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        public static string QueryProcessCommandLine(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            return item["CommandLine"] as string;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool? DebugPortRequestedByPid(int pid)
        {
            string commandLine = QueryProcessCommandLine(pid);
            return ParseCommandLineHasDebugPort(commandLine);
        }

        public static DateTime? LaunchDateUtcOfPid(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return null;
                    return process.StartTime.ToUniversalTime();
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool HasExited(int pid, DateTime? launchedAtUtc)
        {
            DateTime? current = LaunchDateUtcOfPid(pid);
            if (!current.HasValue)
            {
                return true;
            }
            if (launchedAtUtc.HasValue)
            {
                return current.Value != launchedAtUtc.Value;
            }
            return false;
        }

        public static int FindRunningDesktopPid()
        {
            Process[] processes = Process.GetProcessesByName(AntigravityLaunchState.TargetProcessName);
            if (processes.Length == 0)
            {
                return 0;
            }
            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        if (!processes[i].HasExited && processes[i].MainWindowHandle != IntPtr.Zero)
                        {
                            return processes[i].Id;
                        }
                    }
                    catch { }
                }

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'Antigravity.exe'"))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                object rawPid = item["ProcessId"];
                                string cmdLine = item["CommandLine"] as string;
                                if (rawPid != null && IsMainDesktopCommandLine(cmdLine))
                                {
                                    int pid = Convert.ToInt32(rawPid);
                                    if (!HasExited(pid, null))
                                    {
                                        return pid;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try { processes[i].Dispose(); } catch { }
                }
            }
            return 0;
        }

        public static bool RequestGracefulExit(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process proc = Process.GetProcessById(pid))
                {
                    if (proc.HasExited) return true;
                    if (proc.CloseMainWindow())
                    {
                        return true;
                    }
                }
            }
            catch { }

            bool posted = false;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint windowPid;
                    GetWindowThreadProcessId(hWnd, out windowPid);
                    if (windowPid == (uint)pid && IsWindowVisible(hWnd))
                    {
                        if (PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                        {
                            posted = true;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return posted;
        }
    }

    public sealed class LaunchReply
    {
        public bool Launched { get; private set; }
        public string Message { get; private set; }

        public LaunchReply(bool launched, string message)
        {
            Launched = launched;
            Message = message ?? string.Empty;
        }

        public static LaunchReply TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null)
                {
                    return null;
                }
                object rawLaunched;
                if (!root.TryGetValue("launched", out rawLaunched) || !(rawLaunched is bool))
                {
                    return null;
                }
                string message = string.Empty;
                object rawMessage;
                if (root.TryGetValue("message", out rawMessage) && rawMessage is string)
                {
                    message = (string)rawMessage;
                }
                return new LaunchReply((bool)rawLaunched, message);
            }
            catch
            {
                return null;
            }
        }
    }
}

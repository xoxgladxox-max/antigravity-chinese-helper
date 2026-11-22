using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace AntigravityChineseHelper
{
    public sealed class AntigravityLaunchState
    {
        public const string TargetProcessName = "Antigravity";
        private readonly Dictionary<int, long> _handledProcesses = new Dictionary<int, long>();

        public static bool IsTargetProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
            {
                return false;
            }
            return string.Equals(processName, TargetProcessName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, TargetProcessName + ".exe", StringComparison.OrdinalIgnoreCase);
        }

        public bool ShouldOpenHelper(string processName, int processId, long startTimeTicks)
        {
            if (!IsTargetProcess(processName) || processId <= 0)
            {
                return false;
            }
            long recordedTicks;
            if (_handledProcesses.TryGetValue(processId, out recordedTicks) && recordedTicks == startTimeTicks)
            {
                return false;
            }
            _handledProcesses[processId] = startTimeTicks;
            return true;
        }

        public void ApplicationTerminated(string processName, int processId)
        {
            if (!IsTargetProcess(processName))
            {
                return;
            }
            _handledProcesses.Remove(processId);
        }

        public void SynchronizeActiveProcesses(IDictionary<int, long> activeTargetProcesses)
        {
            List<int> exited = new List<int>();
            foreach (KeyValuePair<int, long> entry in _handledProcesses)
            {
                long currentTicks;
                if (!activeTargetProcesses.TryGetValue(entry.Key, out currentTicks) || currentTicks != entry.Value)
                {
                    exited.Add(entry.Key);
                }
            }
            for (int i = 0; i < exited.Count; i++)
            {
                _handledProcesses.Remove(exited[i]);
            }
        }
    }

    public static class AppWatcher
    {
        public const string WatcherMutexName = @"Local\AntigravityChineseHelper.Watcher";
        public const string TrayMutexName = @"Local\AntigravityChineseHelper.Tray";

        private sealed class ClassifiedPid
        {
            public readonly long StartTicks;
            public readonly bool IsMainDesktop;

            public ClassifiedPid(long startTicks, bool isMainDesktop)
            {
                StartTicks = startTicks;
                IsMainDesktop = isMainDesktop;
            }
        }

        public static int Run()
        {
            bool createdNew;
            using (Mutex watcherMutex = new Mutex(true, WatcherMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return 0;
                }

                AntigravityLaunchState state = new AntigravityLaunchState();
                Dictionary<int, ClassifiedPid> pidCache = new Dictionary<int, ClassifiedPid>();
                string executablePath = Assembly.GetExecutingAssembly().Location;

                while (true)
                {
                    try
                    {
                        Dictionary<int, long> activeDesktop = new Dictionary<int, long>();
                        HashSet<int> aliveAny = new HashSet<int>();
                        Process[] processes = Process.GetProcessesByName(AntigravityLaunchState.TargetProcessName);
                        bool shouldLaunchHelper = false;

                        for (int i = 0; i < processes.Length; i++)
                        {
                            using (Process proc = processes[i])
                            {
                                bool exited = false;
                                try { exited = proc.HasExited; }
                                catch { /* Access denied to HasExited means the process is still alive. */ }
                                if (exited)
                                {
                                    continue;
                                }

                                long startTicks = 0;
                                try { startTicks = proc.StartTime.ToUniversalTime().Ticks; }
                                catch { startTicks = 0; }

                                aliveAny.Add(proc.Id);
                                ClassifiedPid cached;
                                if (!pidCache.TryGetValue(proc.Id, out cached) || cached.StartTicks != startTicks)
                                {
                                    bool isMain = false;
                                    try
                                    {
                                        if (proc.MainWindowHandle != IntPtr.Zero)
                                        {
                                            isMain = true;
                                        }
                                        else
                                        {
                                            string cmdLine = AntigravityRun.QueryProcessCommandLine(proc.Id);
                                            isMain = cmdLine != null && AntigravityRun.IsMainDesktopCommandLine(cmdLine);
                                        }
                                    }
                                    catch { }
                                    cached = new ClassifiedPid(startTicks, isMain);
                                    pidCache[proc.Id] = cached;
                                }

                                if (cached.IsMainDesktop)
                                {
                                    activeDesktop[proc.Id] = startTicks;
                                    if (state.ShouldOpenHelper(AntigravityLaunchState.TargetProcessName, proc.Id, startTicks))
                                    {
                                        shouldLaunchHelper = true;
                                    }
                                }
                            }
                        }

                        List<int> stalePids = new List<int>();
                        foreach (int trackedPid in pidCache.Keys)
                        {
                            if (!aliveAny.Contains(trackedPid))
                            {
                                stalePids.Add(trackedPid);
                            }
                        }
                        for (int i = 0; i < stalePids.Count; i++)
                        {
                            pidCache.Remove(stalePids[i]);
                        }

                        state.SynchronizeActiveProcesses(activeDesktop);

                        if (shouldLaunchHelper && !IsTrayHelperRunning())
                        {
                            StartHelperInBackground(executablePath);
                        }
                    }
                    catch
                    {
                        // Watcher stays resilient across transient process enumeration errors.
                    }

                    Thread.Sleep(2000);
                }
            }
        }

        public static bool IsTrayHelperRunning()
        {
            Mutex existing;
            if (Mutex.TryOpenExisting(TrayMutexName, out existing))
            {
                existing.Dispose();
                return true;
            }
            return false;
        }

        private static void StartHelperInBackground(string executablePath)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = executablePath;
                info.Arguments = "--background";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                Process started = Process.Start(info);
                if (started != null)
                {
                    started.Dispose();
                }
            }
            catch
            {
                // Will retry on next Antigravity launch.
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using AntigravityChineseHelper;

internal static class HelperWindowsTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void TestRunnerStatus()
    {
        string[,] cases = new string[,]
        {
            {
                "汉化错误保留原始提示",
                "{\"status\":\"error\",\"translatedWindows\":1,\"lastError\":\"词库重新加载失败\",\"subagentContext\":{\"targetTokens\":64000,\"active\":true,\"error\":null,\"message\":\"\"}}",
                "汉化遇到问题：词库重新加载失败"
            },
            {
                "显示汉化窗口数",
                "{\"status\":\"running\",\"translatedWindows\":2,\"lastError\":null,\"subagentContext\":{\"targetTokens\":null,\"active\":false,\"error\":null,\"message\":\"使用子代理默认上下文\"}}",
                "已汉化 2 个窗口"
            },
            {
                "等待窗口时忽略旧预算提醒",
                "{\"status\":\"waiting\",\"translatedWindows\":0,\"lastError\":null,\"subagentContext\":{\"targetTokens\":64000,\"active\":false,\"error\":null,\"message\":\"已保存。\"}}",
                "运行中 · 等待 Antigravity 窗口"
            },
            {
                "已连接但没有主窗口",
                "{\"status\":\"running\",\"translatedWindows\":0,\"lastError\":null,\"subagentContext\":{\"targetTokens\":64000,\"active\":true,\"error\":null,\"message\":\"\"}}",
                "运行中 · 等待 Antigravity 窗口"
            },
            {
                "旧预算错误不影响汉化状态",
                "{\"status\":\"running\",\"translatedWindows\":1,\"lastError\":null,\"subagentContext\":{\"targetTokens\":64000,\"active\":false,\"error\":\"助手后端已断开\",\"message\":\"已保存\"}}",
                "已汉化 1 个窗口"
            },
            {
                "无需预算状态即可读取汉化状态",
                "{\"status\":\"waiting\",\"translatedWindows\":0,\"lastError\":null}",
                "运行中 · 等待 Antigravity 窗口"
            },
            {
                "停止后显示汉化已停止",
                "{\"status\":\"stopped\",\"translatedWindows\":0,\"lastError\":null}",
                "汉化助手已停止"
            },
            {
                "兼容忽略旧预算字段的残缺负载",
                "{\"status\":\"running\",\"translatedWindows\":1,\"lastError\":null,\"subagentContext\":{\"targetTokens\":64000}}",
                "已汉化 1 个窗口"
            }
        };

        int count = cases.GetLength(0);
        for (int i = 0; i < count; i++)
        {
            RunnerStatus parsed = RunnerStatus.Parse(cases[i, 1]);
            Expect(parsed.MenuTitle == cases[i, 2], cases[i, 0] + "：得到 " + parsed.MenuTitle);
        }

        string[,] titleModes = new string[,]
        {
            {
                "{\"status\":\"running\",\"state\":\"connected\",\"translatedWindows\":1,\"nativeChineseTitles\":true}",
                "会话标题 · 直接生成中文"
            },
            {
                "{\"status\":\"running\",\"state\":\"connected\",\"translatedWindows\":1,\"nativeChineseTitles\":false}",
                "会话标题 · 由主窗口自动改为中文"
            },
            {
                "{\"status\":\"waiting\",\"state\":\"waiting-for-window\",\"translatedWindows\":0,\"nativeChineseTitles\":false}",
                "会话标题 · 等待主窗口连接"
            },
            {
                "{\"status\":\"waiting\",\"state\":\"waiting-for-app\",\"translatedWindows\":0,\"nativeChineseTitles\":null}",
                "会话标题 · 等待确认后端状态"
            }
        };
        for (int i = 0; i < titleModes.GetLength(0); i++)
        {
            RunnerStatus parsed = RunnerStatus.Parse(titleModes[i, 0]);
            Expect(parsed.TitleModeDescription == titleModes[i, 1], "标题模式不匹配：" + parsed.TitleModeDescription);
        }

        string[] invalid = new string[]
        {
            "{\"state\":\"running\",\"windows\":2,\"message\":\"旧格式错误\"}",
            "{\"status\":\"running\",\"lastError\":null}",
            "{\"translatedWindows\":1,\"lastError\":null}"
        };
        for (int i = 0; i < invalid.Length; i++)
        {
            bool threw = false;
            try
            {
                RunnerStatus.Parse(invalid[i]);
            }
            catch (FormatException)
            {
                threw = true;
            }
            Expect(threw, "缺少 runner 必填字段时不能补默认状态");
        }

        CheckDesktopRecovery();
        CheckLaunchArguments();

        LaunchReply reply = LaunchReply.TryParse("{\"launched\":true,\"message\":\"\"}");
        Expect(reply != null && reply.Launched && reply.Message == "", "LaunchReply 解析有效 JSON");
        Expect(LaunchReply.TryParse("{\"message\":\"missing launched\"}") == null, "LaunchReply 拒绝缺少 launched 字段");

        Console.WriteLine("RunnerStatus：8 个汉化状态、4 个标题模式、桌面自动恢复与命令行参数检查通过");
    }

    private static void CheckDesktopRecovery()
    {
        DateTime start = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);
        Func<double, string, bool, RunnerStatus> makeStatus = delegate(double offsetSeconds, string state, bool portExists)
        {
            DateTime dt = start.AddSeconds(offsetSeconds);
            string iso = dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string statusVal = state == "connected" ? "running" : "waiting";
            int wins = state == "connected" ? 1 : 0;
            string json = "{\"status\":\"" + statusVal + "\",\"state\":\"" + state + "\",\"translatedWindows\":" + wins
                + ",\"discoveredAt\":\"" + iso + "\",\"debugPortPresent\":" + (portExists ? "true" : "false") + "}";
            return RunnerStatus.Parse(json);
        };

        AntigravityRun original = new AntigravityRun(10, start, false);
        RunnerStatus waiting = makeStatus(8.0, "waiting-for-app", false);
        Expect(original.NeedsAutomaticRestart(waiting, start.AddSeconds(8.0)), "冷启动缺少端口应触发自动重启");
        Expect(original.NeedsAutomaticRestart(makeStatus(3600.0, "waiting-for-app", true), start.AddSeconds(3600.0)),
            "残留端口文件不能阻止无端口进程自动恢复");
        Expect(!original.NeedsAutomaticRestart(makeStatus(8.0, "connected", true), start.AddSeconds(8.0)),
            "已连接的桌面不能触发重启");
        Expect(!original.NeedsAutomaticRestart(makeStatus(8.0, "waiting-for-window", true), start.AddSeconds(8.0)),
            "正在等待窗口时不能触发重启");

        Expect(!new AntigravityRun(11, start, true).NeedsAutomaticRestart(waiting, start.AddSeconds(8.0)),
            "已带调试端口参数的进程不能自动退出");
        Expect(!new AntigravityRun(11, start, null).NeedsAutomaticRestart(waiting, start.AddSeconds(8.0)),
            "无法确认参数时不能自动退出");

        original.RestartFailure = "用户取消退出";
        AntigravityRun reopened = new AntigravityRun(12, start.AddSeconds(20.0), false);
        Expect(!original.NeedsAutomaticRestart(makeStatus(28.0, "waiting-for-app", false), start.AddSeconds(28.0)),
            "取消退出后同一进程停止自动重试");
        Expect(reopened.NeedsAutomaticRestart(makeStatus(28.0, "waiting-for-app", false), start.AddSeconds(28.0)),
            "新启动的同版 Desktop 独立恢复");
        Expect(!reopened.NeedsAutomaticRestart(makeStatus(19.0, "waiting-for-app", false), start.AddSeconds(20.0)),
            "上个进程的旧探测不能触发退出");
        Expect(!reopened.NeedsAutomaticRestart(makeStatus(21.0, "waiting-for-app", false), start.AddSeconds(21.0)),
            "5 秒启动宽限期内不能触发退出");
        Expect(!reopened.NeedsAutomaticRestart(makeStatus(28.0, "waiting-for-app", false), start.AddSeconds(50.0)),
            "超过 10 秒的过期探测不能触发退出");
    }

    private static void CheckLaunchArguments()
    {
        Expect(AntigravityRun.ParseCommandLineHasDebugPort("\"C:\\Program Files\\Antigravity\\Antigravity.exe\" --remote-debugging-port=0") == true,
            "识别 --remote-debugging-port=0");
        Expect(AntigravityRun.ParseCommandLineHasDebugPort("\"C:\\Program Files\\Antigravity\\Antigravity.exe\" -- --remote-debugging-port=0") == false,
            "-- 之后的参数不应视为 Electron 调试端口开关");
        Expect(AntigravityRun.IsMainDesktopCommandLine("\"C:\\Users\\Demo\\Antigravity.exe\" --remote-debugging-port=0"),
            "主进程命令行识别为 Desktop");
        Expect(!AntigravityRun.IsMainDesktopCommandLine("\"C:\\Users\\Demo\\Antigravity.exe\" --type=renderer"),
            "Chromium 子进程不能误判为主进程");
        Expect(!AntigravityRun.IsMainDesktopCommandLine("\"C:\\Users\\Demo\\Antigravity.exe\" --import=file:///sidecar_sdk/register_loader.js main.mjs"),
            "Sidecar Node 子进程不能误判为主进程");

        // Verify real WMI process command-line and lifetime inspection on local child processes.
        string[] cases = new string[] { "--remote-debugging-port=0", "-- --remote-debugging-port=0" };
        bool[] expected = new bool[] { true, false };
        for (int i = 0; i < cases.Length; i++)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -Command \"[Console]::In.ReadLine() | Out-Null; #\" " + cases[i];
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardError = true;
            DateTime beforeUtc = DateTime.UtcNow.AddSeconds(-1);
            using (Process child = Process.Start(psi))
            {
                int pid = child.Id;
                DateTime? launchedUtc = AntigravityRun.LaunchDateUtcOfPid(pid);
                Expect(launchedUtc.HasValue && launchedUtc.Value >= beforeUtc && !AntigravityRun.HasExited(pid, launchedUtc),
                    "应能读取本地进程的启动时间与存活状态");
                Expect(AntigravityRun.DebugPortRequestedByPid(pid) == expected[i],
                    "应能通过 Win32_Process + CommandLineToArgvW 识别真实子进程的调试端口参数：" + cases[i]);
                child.StandardInput.WriteLine();
                child.WaitForExit(5000);
                Expect(AntigravityRun.HasExited(pid, launchedUtc), "进程退出后 HasExited 应返回 true");
            }
        }
    }

    private static void TestAppWatcherState()
    {
        AntigravityLaunchState state = new AntigravityLaunchState();
        Expect(!state.ShouldOpenHelper("notepad", 100, 1000L), "Other apps must not start the helper");
        Expect(state.ShouldOpenHelper("Antigravity", 100, 1000L), "Running Antigravity must start helper on observer startup");
        Expect(!state.ShouldOpenHelper("Antigravity.exe", 100, 1000L), "Duplicate poll must not reopen manually quit helper");

        state.ApplicationTerminated("AntigravityChineseHelper", 100);
        Expect(!state.ShouldOpenHelper("Antigravity", 100, 1000L), "Quitting helper must not reset current Antigravity session");

        Expect(state.ShouldOpenHelper("Antigravity", 101, 2000L), "New Antigravity PID must start helper");
        Expect(state.ShouldOpenHelper("Antigravity", 100, 3000L), "Reused PID with new start timestamp must start helper");

        Dictionary<int, long> active = new Dictionary<int, long>();
        active[101] = 2000L;
        state.SynchronizeActiveProcesses(active);
        Expect(state.ShouldOpenHelper("Antigravity", 100, 3000L), "Exited PID removed during sync can start helper again");

        Console.WriteLine("AppWatcher: startup, unrelated apps, manual exit, PID reuse, and sync passed");
    }

    private static string WriteCmdNode(string root, string relativePath, string body)
    {
        string fullPath = Path.Combine(root, relativePath);
        string dir = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, "@echo off\r\n" + body + "\r\n", Encoding.ASCII);
        return fullPath;
    }

    private static void TestRuntimeSupport()
    {
        string home = Path.Combine(Path.GetTempPath(), "agh-win-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            env["USERPROFILE"] = home;
            env["APPDATA"] = Path.Combine(home, "AppData", "Roaming");
            env["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
            env["ProgramFiles"] = Path.Combine(home, "ProgramFiles");
            env["ProgramFiles(x86)"] = Path.Combine(home, "ProgramFilesX86");
            env["PATH"] = Path.Combine(home, "empty-bin");

            string explicitNode = WriteCmdNode(home, @"chosen\node.cmd", "echo v22.11.0");
            string oldNode = WriteCmdNode(home, @"old\node.cmd", "echo v20.10.0");
            string miseShim = WriteCmdNode(home, @"AppData\Local\mise\shims\node.cmd",
                "if exist working-directory-marker if \"%MISE_NOT_FOUND_AUTO_INSTALL%\"==\"false\" echo v22.12.0");

            File.WriteAllText(Path.Combine(home, "working-directory-marker"), "");

            Dictionary<string, string> configured = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
            configured["ANTIGRAVITY_HELPER_NODE"] = explicitNode;
            Expect(string.Equals(NodeRuntime.FindExecutable(home, configured, home), explicitNode, StringComparison.OrdinalIgnoreCase),
                "Explicit ANTIGRAVITY_HELPER_NODE must take priority");

            Expect(string.Equals(NodeRuntime.FindExecutable(home, env, home), miseShim, StringComparison.OrdinalIgnoreCase),
                "mise shim runs in working directory with MISE_NOT_FOUND_AUTO_INSTALL=false");

            string v24 = WriteCmdNode(home, @"AppData\Roaming\nvm\v24.1.0\node.exe", "rem placeholder");
            string v23 = WriteCmdNode(home, @"AppData\Roaming\fnm\node-versions\v23.2.0\installation\node.exe", "rem placeholder");
            string v2210 = WriteCmdNode(home, @"AppData\Roaming\nvm\v22.10.0\node.exe", "rem placeholder");
            string v229 = WriteCmdNode(home, @"AppData\Local\mise\installs\node\22.9.0\node.exe", "rem placeholder");

            List<string> candidates = NodeRuntime.GetCandidates(home, env);
            Expect(candidates.IndexOf(v24) >= 0 && candidates.IndexOf(v23) >= 0, "Versioned installs discovered");
            Expect(candidates.IndexOf(v24) < candidates.IndexOf(v23), "Descending major version order");
            Expect(candidates.IndexOf(v2210) < candidates.IndexOf(v229), "Descending minor version order");

            string hung = WriteCmdNode(home, @"hung\node.cmd", "ping -n 10 127.0.0.1 >nul");
            Stopwatch sw = Stopwatch.StartNew();
            string validated = NodeRuntime.ValidatedExecutable(new string[] { hung, oldNode, explicitNode }, env, home, 600);
            sw.Stop();
            Expect(string.Equals(validated, explicitNode, StringComparison.OrdinalIgnoreCase), "Skip hung and Node 20 candidates");
            Expect(sw.ElapsedMilliseconds < 8000, "Hung shim must not block discovery");

            string logPath = Path.Combine(home, "helper.log");
            File.WriteAllBytes(logPath, Encoding.UTF8.GetBytes("first line\n"));
            using (FileStream small = HelperLog.Open(logPath))
            {
                byte[] next = Encoding.UTF8.GetBytes("next line\n");
                small.Write(next, 0, next.Length);
            }
            Expect(File.ReadAllText(logPath, Encoding.UTF8) == "first line\nnext line\n", "Small logs preserved and appended");

            StringBuilder bigBuilder = new StringBuilder();
            string padding = new string('x', 48);
            for (int i = 0; i < 12000; i++)
            {
                bigBuilder.Append("line ").Append(i).Append(": 汉化日志 ").Append(padding).Append('\n');
            }
            File.WriteAllBytes(logPath, Encoding.UTF8.GetBytes(bigBuilder.ToString()));
            using (FileStream large = HelperLog.Open(logPath))
            {
                byte[] appended = Encoding.UTF8.GetBytes("appended\n");
                large.Write(appended, 0, appended.Length);
            }
            byte[] retainedBytes = File.ReadAllBytes(logPath);
            string retainedText = Encoding.UTF8.GetString(retainedBytes);
            Expect(retainedBytes.Length <= (256 * 1024) + 9, "Large log keeps only bounded tail");
            Expect(retainedText.StartsWith("line ") && retainedText.EndsWith("line 11999: 汉化日志 " + padding + "\nappended\n"),
                "Log tail starts at complete UTF-8 line and retains latest output");

            string icoPath = Path.Combine(home, "test.ico");
            IconGenerator.WriteIcoFile(icoPath);
            byte[] icoBytes = File.ReadAllBytes(icoPath);
            Expect(icoBytes.Length > 1000 && icoBytes[0] == 0 && icoBytes[1] == 0 && icoBytes[2] == 1 && icoBytes[4] == 7,
                "Generated .ico contains valid 7-frame ICONDIR header");
            using (Icon runtimeIcon = IconGenerator.CreateRuntimeIcon())
            {
                Expect(runtimeIcon.Width > 0 && runtimeIcon.Height > 0, "Runtime icon loads cleanly");
            }

            ProcessStartInfo fastCmd = new ProcessStartInfo();
            fastCmd.FileName = "cmd.exe";
            fastCmd.Arguments = "/d /s /c \"echo {\"launched\":true,\"message\":\"\"}& exit /b 0\"";
            fastCmd.UseShellExecute = false;
            fastCmd.CreateNoWindow = true;
            fastCmd.RedirectStandardOutput = true;
            fastCmd.RedirectStandardError = true;
            fastCmd.StandardOutputEncoding = Encoding.UTF8;
            fastCmd.StandardErrorEncoding = Encoding.UTF8;
            NodeCommandResult cmdResult = NodeRuntime.RunCommand(fastCmd, 5000);
            Expect(cmdResult.Exited && cmdResult.ExitCode == 0, "RunCommand captures ExitCode before disposing Process");
            LaunchReply fastReply = LaunchReply.TryParse(cmdResult.Stdout);
            Expect(fastReply != null && fastReply.Launched, "RunCommand captures complete stdout from fast-exiting process");

            Console.WriteLine("RuntimeSupport & IconGenerator: Node selection, timeout, log retention, RunCommand, and ICO generation passed");
        }
        finally
        {
            try { GC.Collect(); GC.WaitForPendingFinalizers(); Directory.Delete(home, true); } catch { }
        }
    }

    public static int Main()
    {
        TestRunnerStatus();
        TestAppWatcherState();
        TestRuntimeSupport();
        return 0;
    }
}

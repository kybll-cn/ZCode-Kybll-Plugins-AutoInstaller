/* ZCode 三插件一键安装器（多机通用版）
 *
 * 双击一个 exe，就把三个插件装好：
 *   1. Zcode-Status-TypeBar      —— token 用量状态条（改 app.asar）
 *   2. Zcode-Status-Daily-Use    —— 设置页「今日用量」区块（改 app.asar）
 *   3. ZCode-Search-Aggregation  —— 多引擎并行搜索（原生插件，走 zcode CLI）
 *
 * 与本地源码版的关键差别：**插件包从 CNB release 下载**，不依赖本机存在
 * 源码目录。所以这个 exe 拷到任何一台装了 ZCode 的 Windows 上都能直接跑。
 *
 * 下载清单写死在 PLUGINS 里（含 SHA256）：下载后必须校验哈希，对不上就中止，
 * 不拿未知内容去改用户的 app.asar。升级插件时改这张表 + 重新编译即可。
 *
 * 为什么等 ZCode 退出：前两个插件靠往 app.asar 主进程入口追加一行 import 生效，
 * 而 app.asar 被 ZCode 进程占着，替换那一步会报 WinError 5。流程是
 *   提示退出 → 轮询等待进程消失 → 依次安装 → 装搜索插件 → 重新拉起 ZCode。
 *
 * 为什么用 C# 而不是 Python：exe 要能在任何机器上双击就跑，不该要求对方先装
 * Python 3.8+。csc.exe（.NET Framework 自带）编出来的 exe 只要有 Windows 就有。
 * 但两个 asar 安装器本身是 Python 脚本，所以本机仍需 Python 3.8+ —— 缺失时
 * 给出明确的下载提示，而不是让用户看一堆报错。
 *
 * 这里只是"调度器"：真正的安装逻辑在下载下来的各插件 install.py 里
 * （那个才是被测试覆盖的部分）。本程序负责：等进程、下载校验、解压、
 * 串起三个安装器、汇总结果、拉起客户端。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class Program
{
    /* 插件清单。版本、URL、SHA256 三者要一起改；换版本时重新算哈希。
     * 仓库：https://cnb.cool/kybll */
    private class PluginSpec
    {
        public string Name;        // 目录名，也是 release 里的顶层目录名
        public string Version;
        public string Url;
        public string Sha256;
        public bool IsAsar;        // true = 改 app.asar；false = 原生插件
    }

    private static readonly PluginSpec[] PLUGINS = new[]
    {
        new PluginSpec {
            Name = "Zcode-Status-TypeBar",
            Version = "1.1.0",
            Url = "https://cnb.cool/kybll/Zcode-Status-TypeBar/-/releases/download/v1.1.0/Zcode-Status-TypeBar-v1.1.0.zip",
            Sha256 = "d69b8bacdeeb3a9a20cd65772dec0de39abeb0d531656dad44b71c4a2339e526",
            IsAsar = true,
        },
        new PluginSpec {
            Name = "Zcode-Status-Daily-Use",
            Version = "1.1.0",
            Url = "https://cnb.cool/kybll/Zcode-Status-Daily-Use/-/releases/download/v1.1.0/Zcode-Status-Daily-Use-v1.1.0.zip",
            Sha256 = "c4bb6288a41065cb000e841d732384eb2fd0f6b0ccf3ba6efe8a6e844f20dee2",
            IsAsar = true,
        },
        new PluginSpec {
            Name = "ZCode-Search-Aggregation",
            Version = "1.0.0",
            Url = "https://cnb.cool/kybll/ZCode-Search-Aggregation/-/releases/download/v1.0.0/ZCode-Search-Aggregation-v1.0.0.zip",
            Sha256 = "7b6e951a2821b193db6dfce94e7fe694a08b21fd08a2958e4ae85b3ce360b5c5",
            IsAsar = false,
        },
    };

    private const int WaitTimeoutSeconds = 1800;  // 等 ZCode 退出的上限（30 分钟）
    private const int PollIntervalMs = 1000;

    private static readonly List<string> Log = new List<string>();

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.Title = "ZCode 插件一键安装"; } catch { }

        bool noRestart = Array.IndexOf(args, "--no-restart") >= 0;
        bool skipDownload = Array.IndexOf(args, "--offline") >= 0;
        /* --dry-run：只走完"找 Python / 找 ZCode / 下载校验解压"，
         * 不等进程、不改 app.asar、不装任何东西。用来验证下载链路。 */
        bool dryRun = Array.IndexOf(args, "--dry-run") >= 0;

        Say("==================================================");
        Say("  ZCode 插件一键安装器");
        Say("==================================================");
        Say("");

        /* 1. 找 Python —— 两个 asar 安装器都是 Python 脚本 */
        string python = FindPython();
        if (python == null)
        {
            Say("[x] 没有找到 Python 3.8+。");
            Say("");
            Say("    请先安装 Python 3.8 或更新的 3.x：");
            Say("      https://www.python.org/downloads/windows/");
            Say("");
            Say("    安装时务必勾选 \"Add python.exe to PATH\"，然后重新运行本程序。");
            Finish(2);
            return 2;
        }
        Say("[1/5] Python: " + python);

        /* 2. 找 ZCode */
        string zcodeExe = FindZCode();
        if (zcodeExe == null)
        {
            Say("[x] 没有找到 ZCode.exe。");
            Say("    请确认已安装 ZCode 桌面客户端。");
            Finish(2);
            return 2;
        }
        Say("[2/5] ZCode: " + zcodeExe);

        /* 3. 下载并校验三个插件包 */
        string workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZCode-Plugin-Installer", "packages");
        try
        {
            Directory.CreateDirectory(workDir);
        }
        catch (Exception e)
        {
            Say("[x] 无法创建工作目录 " + workDir + ": " + e.Message);
            Finish(2);
            return 2;
        }

        Say("[3/5] 获取插件包...");
        var extracted = new Dictionary<string, string>();
        foreach (var p in PLUGINS)
        {
            string dir;
            if (!FetchPlugin(p, workDir, skipDownload, out dir))
            {
                Say("");
                Say("[x] 「" + p.Name + "」获取失败，已中止（没有改动任何文件）。");
                Finish(1);
                return 1;
            }
            extracted[p.Name] = dir;
        }

        /* 4. 等 ZCode 退出 */
        if (dryRun)
        {
            Say("");
            Say("[4/5] --dry-run：跳过等待进程与安装。");
            Say("[5/5] --dry-run：跳过安装。");
            Say("");
            Say("  下载与校验全部通过，三个插件包就绪。");
            Finish(0);
            return 0;
        }

        Say("");
        Say("[4/5] 等待 ZCode 退出...");
        if (IsZCodeRunning())
        {
            Say("      检测到 ZCode 正在运行。");
            Say("");
            Say("      请手动完全退出 ZCode：");
            Say("        1. 关掉所有 ZCode 窗口");
            Say("        2. 在系统托盘（右下角隐藏图标里）右键 ZCode 图标，选择\"退出\"");
            Say("");
            Say("      只关窗口是不够的 —— 托盘进程还在时会占用 app.asar，安装必定失败。");
            Say("");
            Say("      正在等待（最多 " + (WaitTimeoutSeconds / 60) + " 分钟）...");
            if (!WaitForExit(WaitTimeoutSeconds))
            {
                Say("");
                Say("[x] 等待超时，ZCode 仍未退出，已中止（没有改动任何文件）。");
                Finish(3);
                return 3;
            }
        }
        Say("      ZCode 已退出。");

        /* 5. 依次安装 */
        Say("");
        Say("[5/5] 开始安装...");

        int failures = 0;

        /* 5.1 TypeBar：状态条 + 注册 MCP 工具 token_usage */
        Say("");
        Say("  (1/3) Zcode-Status-TypeBar v" + PLUGINS[0].Version + " —— token 用量状态条");
        if (!RunAsarInstaller(python, extracted["Zcode-Status-TypeBar"], "--force"))
        {
            Say("      [x] 失败");
            failures++;
        }

        /* 5.2 Daily-Use：今日用量区块
         * 用 --standalone 而不是 --force：它除了跳过进程检查，还会把输出切成
         * UTF-8（外层控制台是 GBK 时中文提示才不乱码）。 */
        Say("");
        Say("  (2/3) Zcode-Status-Daily-Use v" + PLUGINS[1].Version + " —— 设置页「今日用量」");
        if (!RunAsarInstaller(python, extracted["Zcode-Status-Daily-Use"], "--standalone"))
        {
            Say("      [x] 失败");
            failures++;
        }

        /* 5.3 搜索插件：原生插件，走 zcode CLI，不碰 app.asar。
         * 已经装好且启用时 zcode CLI 会报 already installed，那不算失败。 */
        Say("");
        Say("  (3/3) ZCode-Search-Aggregation v" + PLUGINS[2].Version + " —— 多引擎并行搜索");
        if (!InstallSearchPlugin(extracted["ZCode-Search-Aggregation"]))
        {
            Say("      [x] 失败");
            failures++;
        }

        /* 6. 汇总 */
        Say("");
        Say("==================================================");
        if (failures == 0)
        {
            Say("  三个插件全部安装成功。");
            Say("==================================================");
            Say("");
            Say("  · 状态条：打开任意聊天会话，输入框下方出现 token 用量");
            Say("  · 今日用量：设置 → 用量，顶栏正下方出现「今日用量」卡片");
            Say("  · 搜索聚合：对话里问问题时会自动多引擎并行搜索");
            Say("");
            if (!noRestart)
            {
                Say("  正在启动 ZCode...");
                StartZCode(zcodeExe);
                Say("  已启动。");
            }
            else
            {
                Say("  （--no-restart：未自动启动 ZCode）");
            }
            Finish(0);
            return 0;
        }
        else
        {
            Say("  有 " + failures + " 个插件安装失败，请看上面的输出。");
            Say("==================================================");
            Say("");
            Say("  常见原因：");
            Say("    · ZCode 没完全退出（托盘进程还在）→ 完全退出后重跑本 exe");
            Say("    · app.asar 所在目录不可写 → 用管理员身份运行本 exe");
            Say("    · 上次安装有残留 → 先跑各插件的 uninstall.bat 再重跑");
            if (!noRestart) StartZCode(zcodeExe);
            Finish(1);
            return 1;
        }
    }

    /* ---------------------------------------------------------------- 下载 */

    /* 下载（或复用已下载的）zip，校验 SHA256，解压到独立目录 */
    private static bool FetchPlugin(PluginSpec p, string workDir, bool offline, out string outDir)
    {
        outDir = null;
        string fileName = p.Name + "-v" + p.Version + ".zip";
        string zipPath = Path.Combine(workDir, fileName);
        outDir = Path.Combine(workDir, p.Name + "-v" + p.Version);

        if (File.Exists(zipPath))
        {
            string have = Sha256File(zipPath);
            if (have == p.Sha256)
            {
                Say("      " + p.Name + " v" + p.Version + "：已有缓存，哈希一致");
                if (ExtractZip(zipPath, outDir)) return true;
            }
            else
            {
                Say("      " + p.Name + " v" + p.Version + "：缓存哈希不符，重新下载");
                try { File.Delete(zipPath); } catch { }
            }
        }

        if (offline)
        {
            Say("      [x] " + p.Name + "：本地没有可用的包，且处于 --offline 模式");
            return false;
        }

        Say("      下载 " + p.Name + " v" + p.Version + " ...");
        if (!DownloadFile(p.Url, zipPath))
        {
            Say("      [x] 下载失败: " + p.Url);
            return false;
        }
        string got = Sha256File(zipPath);
        if (got != p.Sha256)
        {
            Say("      [x] 校验失败：期望 " + p.Sha256.Substring(0, 16) + "…，"
                + "实得 " + got.Substring(0, 16) + "…");
            try { File.Delete(zipPath); } catch { }
            return false;
        }
        Say("      校验通过 (" + new FileInfo(zipPath).Length + " 字节)");

        if (!ExtractZip(zipPath, outDir)) return false;
        return true;
    }

    private static bool DownloadFile(string url, string dest)
    {
        try
        {
            ServicePointManager.SecurityProtocol =
                (SecurityProtocolType)3072 | (SecurityProtocolType)12288;  // TLS 1.2 / 1.3
        }
        catch { }

        var client = new WebClient();
        client.Headers.Add("User-Agent", "ZCode-Plugin-Installer/1.0");
        try
        {
            client.DownloadFile(url, dest);
            return true;
        }
        catch (Exception e)
        {
            Say("      [x] " + e.Message);
            return false;
        }
        finally { client.Dispose(); }
    }

    private static string Sha256File(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(path))
        {
            return BitConverter.ToString(sha.ComputeHash(fs))
                .Replace("-", "").ToLowerInvariant();
        }
    }

    /* 解压。zip 里是 <插件名>/<文件> 的一层目录结构。
     * 顺带做 zip slip 防护：条目名解析后必须仍在目标目录内。 */
    private static bool ExtractZip(string zipPath, string destDir)
    {
        try
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            Directory.CreateDirectory(destDir);
            string root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Say("      [x] zip 内含越界路径，已中止: " + entry.FullName);
                        return false;
                    }
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Say("      [x] 解压失败: " + e.Message);
            return false;
        }
    }

    /* ---------------------------------------------------------------- 安装 */

    /* 跑一个插件的 install.py，把它的输出原样透出 */
    private static bool RunAsarInstaller(string python, string pkgDir, string flag)
    {
        string script = Path.Combine(pkgDir, "install.py");
        if (!File.Exists(script))
        {
            Say("      [x] 包里没有 install.py: " + script);
            return false;
        }
        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "\"install.py\" " + flag,
            WorkingDirectory = pkgDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        try
        {
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                var text = outTask.Result + errTask.Result;
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    if (line.Trim().Length > 0) Say("      " + line.TrimEnd());
                return p.ExitCode == 0;
            }
        }
        catch (Exception e)
        {
            Say("      [x] 启动安装器失败: " + e.Message);
            return false;
        }
    }

    /* 搜索插件：原生插件，用 zcode CLI 装。
     * zcode.cjs 在 resources/glm 下，需要 node。 */
    private static bool InstallSearchPlugin(string pkgDir)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string zcodeCjs = Path.Combine(local, @"Programs\ZCode\resources\glm\zcode.cjs");
        if (!File.Exists(zcodeCjs))
        {
            Say("      [x] 找不到 zcode.cjs: " + zcodeCjs);
            return false;
        }
        string node = Which("node.exe");
        if (node == null)
        {
            Say("      [x] 找不到 node.exe，无法调用 zcode CLI");
            return false;
        }

        /* 先查是否已装且启用；已装就不重复装（zcode CLI 重复装会报错） */
        var list = RunCapture(node, zcodeCjs, "plugins", "list");
        if (list.ExitCode == 0 && list.Output.Contains("zcode-search-aggregation"))
        {
            Say("      已安装：" + FirstPluginLine(list.Output, "zcode-search-aggregation"));
            return true;
        }

        /* 装。marketplace 指向插件根目录（zip 解压出来的那个目录），
         * 需要先确保 marketplace 已注册。 */
        var mkt = RunCapture(node, zcodeCjs, "plugins", "marketplace", "list");
        if (mkt.ExitCode == 0 && !mkt.Output.Contains("dev-zcode-plugins"))
        {
            Say("      注册 marketplace dev-zcode-plugins...");
            var add = RunCapture(node, zcodeCjs, "plugins", "marketplace", "add", pkgDir);
            if (add.ExitCode != 0)
            {
                Say("      [x] 注册 marketplace 失败");
                foreach (var line in add.Error.Replace("\r\n", "\n").Split('\n'))
                    if (line.Trim().Length > 0) Say("        " + line.TrimEnd());
                return false;
            }
        }

        var r = RunCapture(node, zcodeCjs, "plugins", "install", "zcode-search-aggregation");
        foreach (var line in r.Output.Replace("\r\n", "\n").Split('\n'))
            if (line.Trim().Length > 0) Say("      " + line.TrimEnd());
        if (r.ExitCode != 0)
        {
            foreach (var line in r.Error.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) Say("      " + line.TrimEnd());
            return false;
        }
        return true;
    }

    /* ---------------------------------------------------------------- 工具 */

    private static void Say(string s)
    {
        Console.WriteLine(s);
        Log.Add(s);
    }

    private static void Finish(int code)
    {
        /* 落一份日志：控制台窗口关了之后还能查 */
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZCode-Plugin-Installer");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "install-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllLines(logPath, Log.ToArray(), new UTF8Encoding(false));
            Say("");
            Say("日志: " + logPath);
        }
        catch { }

        /* 留窗口给人看结果 */
        try
        {
            Console.WriteLine("");
            Console.Write("按回车键关闭本窗口...");
            Console.ReadLine();
        }
        catch { }
    }

    private static string FindPython()
    {
        var candidates = new List<string>();
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var minor in new[] { "313", "312", "311", "310" })
                candidates.Add(Path.Combine(local, @"Programs\Python\Python" + minor + @"\python.exe"));
        }
        catch { }
        foreach (var c in candidates)
            if (File.Exists(c) && HasPython3(c)) return c;

        /* 退到 PATH 里的 python / py */
        foreach (var exe in new[] { "python.exe", "python3.exe" })
        {
            var p = Which(exe);
            if (p != null && HasPython3(p)) return p;
        }
        var py = Which("py.exe");
        if (py != null)
        {
            foreach (var minor in new[] { "3.13", "3.12", "3.11", "3" })
            {
                var r = RunCapture(py, "-" + minor, "-c", "import sys;print(sys.executable)");
                if (r.ExitCode == 0 && r.Output.Trim().Length > 0) return r.Output.Trim();
            }
        }
        return null;
    }

    private static bool HasPython3(string exe)
    {
        var r = RunCapture(exe, "-c", "import sys;sys.exit(0 if sys.version_info>=(3,8) else 1)");
        return r.ExitCode == 0;
    }

    private static string FindZCode()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, @"Programs\ZCode\ZCode.exe"),
            Path.Combine(local, @"ZCode\ZCode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ZCode\ZCode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"ZCode\ZCode.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    private static string Which(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Trim().Length == 0) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static bool IsZCodeRunning()
    {
        try { return Process.GetProcessesByName("ZCode").Length > 0; }
        catch { return false; }
    }

    private static bool WaitForExit(int seconds)
    {
        var sw = Stopwatch.StartNew();
        var lastReport = -1;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (!IsZCodeRunning()) return true;
            var left = (int)(seconds - sw.Elapsed.TotalSeconds);
            if (left % 15 == 0 && left != lastReport)
            {
                Say("      还剩 " + (left / 60) + " 分 " + (left % 60) + " 秒...");
                lastReport = left;
            }
            Thread.Sleep(PollIntervalMs);
        }
        return !IsZCodeRunning();
    }

    private static string FirstPluginLine(string text, string needle)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            if (line.Contains(needle)) return line.Trim();
        return needle;
    }

    private static void StartZCode(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Say("      [x] 启动 ZCode 失败: " + e.Message);
        }
    }

    private struct CaptureResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    private static CaptureResult RunCapture(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.Arguments += "\"" + a + "\" ";
        try
        {
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEnd();
                var e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return new CaptureResult { ExitCode = p.ExitCode, Output = o, Error = e };
            }
        }
        catch (Exception ex)
        {
            return new CaptureResult { ExitCode = -1, Output = "", Error = ex.Message };
        }
    }
}

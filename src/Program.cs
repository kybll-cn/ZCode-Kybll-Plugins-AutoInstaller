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
            Version = "1.1.1",
            Url = "https://cnb.cool/kybll/Zcode-Status-TypeBar/-/releases/download/v1.1.1/Zcode-Status-TypeBar-v1.1.1.zip",
            Sha256 = "ef2447d58bba49c39f116e3d7a3ac1a143c3b3c8ce00d3133c1cdd23ddddb8a3",
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
        /* --zcode-dir <路径>：手动指定 ZCode 安装目录，定位失败时的兜底。 */
        string zcodeDirArg = GetArgValue(args, "--zcode-dir");

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

        /* 2. 找 ZCode —— 定位要稳，因为别人的机器上安装路径、用户名都可能不同。
         * 手动指定（--zcode-dir）优先于自动探测：用户明确说了在哪，就听用户的。 */
        ZCodeInfo zc = null;
        if (zcodeDirArg != null)
        {
            zc = MakeZCodeInfo(Path.Combine(zcodeDirArg, "ZCode.exe"), "命令行指定");
            if (zc == null)
                Say("      [x] 指定的目录里找不到 ZCode.exe 或 resources\\app.asar: " + zcodeDirArg);
        }
        if (zc == null) zc = FindZCode();
        if (zc == null)
        {
            Say("[x] 没有找到 ZCode。");
            Say("");
            Say("    试过的位置：");
            Say("      · 正在运行的 ZCode 进程");
            Say("      · 桌面与开始菜单里的 ZCode 快捷方式");
            Say("      · 卸载注册表项");
            Say("      · 常见的默认安装目录");
            Say("      · 全盘按 resources\\app.asar 特征扫描");
            Say("");
            Say("    如果 ZCode 装在非常规位置，可以把安装目录作为参数传进来：");
            Say("      Install-ZCode-Plugins.exe --zcode-dir \"D:\\Apps\\ZCode\"");
            Finish(2);
            return 2;
        }
        Say("[2/5] ZCode: " + zc.ExePath);
        Say("      定位方式: " + zc.Source);
        Say("      app.asar: " + zc.AsarPath);
        Say("      zcode CLI: " + zc.CliPath);

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
        if (!RunAsarInstaller(python, extracted["Zcode-Status-TypeBar"], "--force", zc.AsarPath))
        {
            Say("      [x] 失败");
            failures++;
        }

        /* 5.2 Daily-Use：今日用量区块
         * 用 --standalone 而不是 --force：它除了跳过进程检查，还会把输出切成
         * UTF-8（外层控制台是 GBK 时中文提示才不乱码）。 */
        Say("");
        Say("  (2/3) Zcode-Status-Daily-Use v" + PLUGINS[1].Version + " —— 设置页「今日用量」");
        if (!RunAsarInstaller(python, extracted["Zcode-Status-Daily-Use"], "--standalone", zc.AsarPath))
        {
            Say("      [x] 失败");
            failures++;
        }

        /* 5.3 搜索插件：原生插件，走 zcode CLI，不碰 app.asar。
         * 已经装好且启用时 zcode CLI 会报 already installed，那不算失败。 */
        Say("");
        Say("  (3/3) ZCode-Search-Aggregation v" + PLUGINS[2].Version + " —— 多引擎并行搜索");
        string mktDir = Path.Combine(workDir, "marketplace");
        if (!InstallSearchPlugin(extracted["ZCode-Search-Aggregation"], zc, mktDir))
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
                StartZCode(zc.ExePath);
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
            if (!noRestart) StartZCode(zc.ExePath);
            Finish(1);
            return 1;
        }
    }

    /* ---------------------------------------------------------------- 下载 */

    /* 下载（或复用已下载的）zip，校验 SHA256，解压并定位真正的插件根目录。
     *
     * 解压后不能直接用解压目录当插件目录：release 的 zip 里有一层顶层目录
     * （Zcode-Status-TypeBar/install.py），而插件的 install.py 在里面一层。
     * 早先直接找 <解压目录>\install.py，自然找不到 —— 报"包里没有 install.py"。
     * 所以这里探测实际根目录：候选里看谁有 install.py / .zcode-plugin。
     */
    private static bool FetchPlugin(PluginSpec p, string workDir, bool offline, out string outDir)
    {
        outDir = null;
        string fileName = p.Name + "-v" + p.Version + ".zip";
        string zipPath = Path.Combine(workDir, fileName);
        string extractDir = Path.Combine(workDir, p.Name + "-v" + p.Version);

        if (File.Exists(zipPath))
        {
            string have = Sha256File(zipPath);
            if (have == p.Sha256)
            {
                Say("      " + p.Name + " v" + p.Version + "：已有缓存，哈希一致");
                if (ExtractAndLocate(zipPath, extractDir, p, out outDir)) return true;
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

        return ExtractAndLocate(zipPath, extractDir, p, out outDir);
    }

    /* 解压 + 定位插件根目录 */
    private static bool ExtractAndLocate(string zipPath, string extractDir, PluginSpec p, out string pluginRoot)
    {
        pluginRoot = null;
        if (!ExtractZip(zipPath, extractDir)) return false;

        pluginRoot = LocatePluginRoot(extractDir, p);
        if (pluginRoot == null)
        {
            Say("      [x] 解压后找不到插件根目录（既没有 install.py 也没有 .zcode-plugin）");
            return false;
        }
        /* 顶层多了一层目录时提一句，方便排障 */
        if (!string.Equals(pluginRoot, extractDir, StringComparison.OrdinalIgnoreCase))
            Say("      插件根目录: " + Path.GetFileName(pluginRoot));
        return true;
    }

    /* 在解压目录里找真正的插件根目录。
     * 判据是"这个目录看起来像一个插件"：有 install.py（asar 插件）
     * 或有 .zcode-plugin\plugin.json（原生插件）。
     * 先看自己，再看一层子目录。 */
    private static string LocatePluginRoot(string extractDir, PluginSpec p)
    {
        if (LooksLikePluginDir(extractDir)) return extractDir;
        try
        {
            foreach (var d in Directory.GetDirectories(extractDir))
                if (LooksLikePluginDir(d)) return d;
        }
        catch { }
        return null;
    }

    private static bool LooksLikePluginDir(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "install.py"))) return true;
            if (File.Exists(Path.Combine(dir, ".zcode-plugin", "plugin.json"))) return true;
            if (File.Exists(Path.Combine(dir, "package.json"))) return true;
        }
        catch { }
        return false;
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

    /* 跑一个插件的 install.py，把它的输出原样透出。
     * asarPath 显式传过去 —— 不让插件自己猜安装在哪儿，它那套猜测逻辑
     * 只覆盖默认路径，别人机器上装在 D 盘就找不到。 */
    private static bool RunAsarInstaller(string python, string pkgDir, string flag, string asarPath)
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
            Arguments = "\"install.py\" " + flag + " --asar \"" + asarPath + "\"",
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
     *
     * zcode CLI 的 marketplace 机制：marketplace 是一个**目录**，里面要有
     * marketplace.json，条目里的 source 是**相对于该目录**的路径。
     * 所以这里现搭一个 marketplace 目录：把三个插件都放进去，再写一份
     * marketplace.json 指向搜索插件。
     *
     * 早先直接拿插件目录当 marketplace 用会报
     * "Marketplace manifest not found in directory" —— 目录里没有 marketplace.json。
     * 之前还指向本机的 D:\Project\ZCode-Plugins（开发目录），换台机器就没有了。
     */
    private static bool InstallSearchPlugin(string pkgDir, ZCodeInfo zc, string marketplaceDir)
    {
        string zcodeCjs = zc.CliPath;
        if (!File.Exists(zcodeCjs))
        {
            Say("      [x] 找不到 zcode.cjs: " + zcodeCjs);
            return false;
        }
        string node = Which("node.exe");
        if (node == null)
        {
            Say("      [x] 找不到 node.exe，无法调用 zcode CLI");
            Say("          ZCode 自带 node，但它不在 PATH 上。");
            return false;
        }

        /* 先查是否已装且启用；已装就不重复装（zcode CLI 重复装会报错） */
        var list = RunCapture(node, zcodeCjs, "plugins", "list");
        if (list.ExitCode == 0 && list.Output.Contains("zcode-search-aggregation"))
        {
            Say("      已安装：" + FirstPluginLine(list.Output, "zcode-search-aggregation"));
            return true;
        }

        /* 现搭 marketplace 目录 */
        if (!BuildMarketplace(marketplaceDir, pkgDir))
        {
            Say("      [x] 搭建 marketplace 目录失败");
            return false;
        }
        Say("      marketplace 目录: " + marketplaceDir);

        const string mktName = "zcode-auto-installer";
        var mkt = RunCapture(node, zcodeCjs, "plugins", "marketplace", "list");
        bool haveMkt = mkt.ExitCode == 0 && mkt.Output.Contains(mktName);
        if (!haveMkt)
        {
            Say("      注册 marketplace " + mktName + "...");
            var add = RunCapture(node, zcodeCjs, "plugins", "marketplace", "add", marketplaceDir);
            if (add.ExitCode != 0)
            {
                Say("      [x] 注册 marketplace 失败");
                foreach (var line in add.Error.Replace("\r\n", "\n").Split('\n'))
                    if (line.Trim().Length > 0) Say("        " + line.TrimEnd());
                return false;
            }
        }

        var r = RunCapture(node, zcodeCjs, "plugins", "install",
            "zcode-search-aggregation@" + mktName);
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

    /* 搭一个 marketplace 目录：搜索插件放进去 + 一份 marketplace.json。
     * source 用相对路径（相对 marketplace 目录），CLI 会按这个解析。 */
    private static bool BuildMarketplace(string mktDir, string searchPluginDir)
    {
        try
        {
            if (Directory.Exists(mktDir)) Directory.Delete(mktDir, true);
            Directory.CreateDirectory(mktDir);

            /* 把插件目录整个拷进去（插件名作为子目录） */
            string dest = Path.Combine(mktDir, "ZCode-Search-Aggregation");
            CopyDirectory(searchPluginDir, dest);

            string json = "{\n"
                + "  \"name\": \"zcode-auto-installer\",\n"
                + "  \"plugins\": [\n"
                + "    {\n"
                + "      \"name\": \"zcode-search-aggregation\",\n"
                + "      \"source\": \"./ZCode-Search-Aggregation\",\n"
                + "      \"version\": \"1.0.0\",\n"
                + "      \"description\": \"Parallel multi-engine web search for ZCode: Baidu, Bing, 360, Sogou, GitHub, Google, Yahoo and DuckDuckGo in one call, deduped and grouped, with automatic mainland-only fallback when the global internet is unreachable.\"\n"
                + "    }\n"
                + "  ]\n"
                + "}\n";
            File.WriteAllText(Path.Combine(mktDir, "marketplace.json"), json, new UTF8Encoding(false));
            return true;
        }
        catch (Exception e)
        {
            Say("      [x] " + e.Message);
            return false;
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /* ---------------------------------------------------------------- 工具 */

    private static void Say(string s)
    {
        Console.WriteLine(s);
        Log.Add(s);
    }

    /* 取命令行参数的值。写成 --name value 或 --name=value 都认。 */
    private static string GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i].Substring(name.Length + 1);
        }
        return null;
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

    /* ---------------------------------------------------------------- 定位 ZCode */

    /* 定位结果。Dir 是安装目录（ZCode.exe 与 resources\ 都在它下面）。 */
    private class ZCodeInfo
    {
        public string Dir;        // 安装目录
        public string ExePath;    // ZCode.exe
        public string AsarPath;   // <Dir>\resources\app.asar
        public string CliPath;    // <Dir>\resources\glm\zcode.cjs
        public string Source;     // 是怎么找到的（写给用户看）
    }

    /* 定位 ZCode 安装目录。
     *
     * 为什么不能只查默认路径：目标机器上用户名、盘符、装法都可能不同 ——
     * 装在 D 盘、用 zip 免安装、装在 Program Files、公司统一部署到某个目录。
     * 所以这里按可靠性从高到低试多个来源，且每个来源都靠"特征"确认
     * （目录里确实有 resources\app.asar），而不是靠名字猜。
     *
     * 来源顺序：
     *   1. 正在运行的 ZCode 进程的可执行文件路径 —— 最准，进程跑着就说明装在这
     *   2. 桌面 / 开始菜单 / 公共目录里的快捷方式目标
     *   3. 卸载注册表项（DisplayIcon / InstallLocation 里的路径）
     *   4. 常见默认目录
     *   5. 全盘按 resources\app.asar 特征找（慢，放最后）
     */
    private static ZCodeInfo FindZCode()
    {
        var results = new List<ZCodeInfo>();

        /* 1. 运行中的进程 */
        try
        {
            foreach (var p in Process.GetProcessesByName("ZCode"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    var info = MakeZCodeInfo(path, "运行中的进程");
                    if (info != null) results.Add(info);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }

        /* 2. 快捷方式 */
        foreach (var lnk in FindShortcuts())
        {
            string target = ResolveShortcut(lnk);
            if (target == null) continue;
            var info = MakeZCodeInfo(target, "快捷方式 " + lnk);
            if (info != null) results.Add(info);
        }

        /* 3. 卸载注册表项 */
        foreach (var path in FindRegistryPaths())
        {
            var info = MakeZCodeInfo(path, "卸载注册表项");
            if (info != null) results.Add(info);
        }

        /* 4. 常见默认目录 */
        foreach (var dir in DefaultZCodeDirs())
        {
            var info = MakeZCodeInfo(Path.Combine(dir, "ZCode.exe"), "默认安装目录");
            if (info != null) results.Add(info);
        }

        /* 5. 全盘扫（慢，最后） */
        foreach (var dir in ScanForZCode())
        {
            var info = MakeZCodeInfo(Path.Combine(dir, "ZCode.exe"), "全盘扫描");
            if (info != null) results.Add(info);
        }

        /* 去重：同一个目录只留第一条（来源最可靠的那个） */
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (seen.Add(r.Dir)) return r;
        }
        return null;
    }

    /* 确认这个 exe 确实是一个 ZCode 安装：同目录下必须有 resources\app.asar。
     * 这一步是关键 —— 它把"名字叫 ZCode 的东西"和"真的 ZCode"区分开。 */
    private static ZCodeInfo MakeZCodeInfo(string exePath, string source)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            string full = Path.GetFullPath(exePath);
            if (!File.Exists(full)) return null;
            if (!string.Equals(Path.GetFileName(full), "ZCode.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            string dir = Path.GetDirectoryName(full);
            string asar = Path.Combine(dir, "resources", "app.asar");
            if (!File.Exists(asar)) return null;

            return new ZCodeInfo
            {
                Dir = dir,
                ExePath = full,
                AsarPath = asar,
                CliPath = Path.Combine(dir, "resources", "glm", "zcode.cjs"),
                Source = source,
            };
        }
        catch { return null; }
    }

    /* 桌面 / 开始菜单 / 公共目录下的快捷方式 */
    private static List<string> FindShortcuts()
    {
        var list = new List<string>();
        try
        {
            var dirs = new List<string>();
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string start = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            string commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (desktop != null) dirs.Add(desktop);
            if (start != null) dirs.Add(start);
            if (commonStart != null) dirs.Add(commonStart);
            if (commonDesktop != null) dirs.Add(commonDesktop);

            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                try
                {
                    foreach (var f in Directory.GetFiles(d, "*.lnk", SearchOption.AllDirectories))
                        list.Add(f);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /* 读 .lnk 的目标路径。用 WScript.Shell COM —— 系统自带，不需要额外依赖。
     * 读不出来（比如快捷方式指向 UWP 应用）就返回 null。 */
    private static string ResolveShortcut(string lnkPath)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut == null) return null;
                try
                {
                    string target = shortcutTypeInvoke(shortcut, "TargetPath");
                    if (!string.IsNullOrEmpty(target) && File.Exists(target)) return target;
                }
                finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut); } catch { } }
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { } }
        }
        catch { }
        return null;
    }

    private static string shortcutTypeInvoke(object shortcut, string prop)
    {
        try
        {
            return (string)shortcut.GetType().InvokeMember(prop,
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
        }
        catch { return null; }
    }

    /* 卸载注册表项里的路径。
     * InstallLocation 常常是空的，所以也看 DisplayIcon（形如 "C:\...\uninstallerIcon.ico,0"）。 */
    private static List<string> FindRegistryPaths()
    {
        var paths = new List<string>();
        var roots = new[]
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var root in roots)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root))
                {
                    var baseKey = key;
                    if (baseKey == null)
                    {
                        try { baseKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root); } catch { }
                    }
                    if (baseKey == null) continue;
                    foreach (var sub in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sk = baseKey.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                object name = sk.GetValue("DisplayName");
                                if (name == null || name.ToString().IndexOf("ZCode",
                                        StringComparison.OrdinalIgnoreCase) < 0) continue;
                                object loc = sk.GetValue("InstallLocation");
                                if (loc != null && !string.IsNullOrEmpty(loc.ToString()))
                                    paths.Add(loc.ToString());
                                object icon = sk.GetValue("DisplayIcon");
                                if (icon != null)
                                {
                                    string s = icon.ToString();
                                    int comma = s.LastIndexOf(',');
                                    if (comma > 0) s = s.Substring(0, comma);
                                    s = s.Trim('"');
                                    if (s.Length > 0)
                                        paths.Add(Path.GetDirectoryName(s));
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        return paths;
    }

    /* 常见默认安装目录。全部用 Environment 取，不写死用户名。 */
    private static List<string> DefaultZCodeDirs()
    {
        var dirs = new List<string>();
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            dirs.Add(Path.Combine(local, @"Programs\ZCode"));
            dirs.Add(Path.Combine(local, @"ZCode"));
            dirs.Add(Path.Combine(programFiles, @"ZCode"));
            dirs.Add(Path.Combine(programFilesX86, @"ZCode"));
        }
        catch { }
        return dirs;
    }

    /* 全盘按 resources\app.asar 特征找。
     * 慢（要遍历目录树），所以只在前面几个来源都没命中时才跑。
     * 只看名字像安装目录的（ZCode / zcode），且限制深度 4 层 ——
     * 装得再深的应用通常也不会超过这个数。 */
    private static List<string> ScanForZCode()
    {
        var found = new List<string>();
        try
        {
            foreach (var dr in DriveInfo.GetDrives())
            {
                if (!dr.IsReady) continue;
                string root = dr.RootDirectory.FullName;
                if (root.StartsWith(@"\\")) continue;   // 跳过网络盘，太慢
                try
                {
                    foreach (var d in ScanDirs(root, 0, 4))
                    {
                        string name = Path.GetFileName(d);
                        if (!name.Equals("ZCode", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(Path.Combine(d, "resources", "app.asar")))
                        {
                            found.Add(d);
                            if (found.Count >= 3) return found;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return found;
    }

    /* 按层数限制地收集目录名（不是所有子目录，是名字像安装目录的那些） */
    private static List<string> ScanDirs(string dir, int depth, int maxDepth)
    {
        var result = new List<string>();
        if (depth > maxDepth) return result;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return result; }
        foreach (var s in subs)
        {
            string name = Path.GetFileName(s);
            if (name.StartsWith("$", StringComparison.Ordinal)) continue;   // 跳过系统目录
            if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(s);
            result.AddRange(ScanDirs(s, depth + 1, maxDepth));
        }
        return result;
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

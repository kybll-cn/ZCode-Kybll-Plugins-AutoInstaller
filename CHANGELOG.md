# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.1.0]

修掉 v1.0.0 在别人机器上跑不起来的三处问题 —— v1.0.0 是在我自己的机器上写的，
路径假设全按本机来的，换台电脑就露馅了。

### 修复

- **ZCode 安装目录定位改成多来源探测**。原先只查 `%LOCALAPPDATA%\Programs\ZCode` 和
  `Program Files` 几个硬编码路径 —— 别人机器上用户名、盘符、装法都不同，装在 D 盘
  或 zip 免安装就完全找不到。
  现在按可靠性依次试：运行中的进程 → 桌面/开始菜单的快捷方式 → 卸载注册表项 →
  常见默认目录 → 全盘特征扫描。
  **每个来源都靠特征确认**（目录里确实有 `resources\app.asar`），不是靠名字匹配 ——
  所以快捷方式被改名成"我的AI工具"照样能认出来。
  找不准时可以用 `--zcode-dir <路径>` 手动指定（优先级高于自动探测）。

- **定位到的 `app.asar` 路径会显式传给两个插件的 `install.py`（`--asar`）**。
  插件自带的路径猜测只覆盖默认目录，在别人机器上会改错文件 —— 现在由本安装器
  说了算。`zcode CLI`（`resources\glm\zcode.cjs`）也用同一个安装目录。

- **解压后自动定位真正的插件根目录**。release 的 zip 里有一层顶层目录
  （`Zcode-Status-TypeBar/install.py`），原先直接找 `<解压目录>\install.py` 自然找不到，
  报"包里没有 install.py"。现在探测实际根目录（看谁有 `install.py` / `.zcode-plugin`）。

- **搜索插件改用自建 marketplace**。原先把 marketplace 指向本机的
  `D:\Project\ZCode-Plugins`（开发目录），别的机器上根本没有；而且 marketplace
  目录里必须有 `marketplace.json`，光有插件目录不行，会报
  "Marketplace manifest not found in directory"。
  现在安装器自己搭一个 marketplace 目录（插件拷进去 + 现写一份 `marketplace.json`），
  与本机目录无关。

### 踩过的坑

- **`marketplace add <目录>` 要求目录里有 `marketplace.json`**，条目里的 `source` 是
  **相对该目录**的路径，且不能逃出该目录（CLI 用 `path.relative` 校验）。
  直接拿插件目录当 marketplace 用会失败。
- **zip 顶层目录**：用 `git archive --prefix=<插件名>/` 打的包会带一层目录，
  解压后不能直接拿解压目录当插件目录。
- 定位 ZCode 时**别只靠快捷方式名字**：用户可能把快捷方式改成任意名字。
  用 `resources\app.asar` 这个特征判断才靠谱。

## [1.0.0]

首版。一个 exe 装好三个 ZCode 插件，多机通用。

### 装什么

| 插件 | 版本 | 作用 |
| --- | --- | --- |
| Zcode-Status-TypeBar | v1.1.0 | 输入框下方的 token 用量状态条 |
| Zcode-Status-Daily-Use | v1.1.0 | 设置 → 用量 里的「今日用量」卡片 |
| ZCode-Search-Aggregation | v1.0.0 | 多引擎并行搜索 |

### 设计

- **插件包从 CNB release 下载**，不依赖本机有源码目录 —— 所以这个 exe 拷到任何一台
  装了 ZCode 的 Windows 上都能直接跑。
- **逐个校验 SHA256**：清单（版本 / URL / SHA256）写死在 `src/Program.cs` 的 `PLUGINS` 表里。
  下载后哈希对不上就中止，不拿未知内容去改用户的 `app.asar`。
- **用 C# 而不是 Python**：exe 要能双击就跑，不该要求对方先装 Python 3.8+。
  编译用 .NET Framework 自带的 `csc.exe`，不需要 Visual Studio。
  （但两个 `app.asar` 插件的安装器本身是 Python 脚本，所以目标机器仍需 Python 3.8+。）
- **等 ZCode 退出再改 `app.asar`**：文件被进程占着时替换会撞锁（`WinError 5`）。
  提示用户从托盘退出，然后轮询等待，上限 30 分钟。
- **zip slip 防护**：解压时校验每个条目解析后的路径仍在目标目录内。

### 踩过的坑

- **`app.asar` 用 `open('r+b')` 是能打开的** —— 所以"试一下能不能写"这个判断方法在这里会给出
  错误结论。真正失败的是随后的 `os.replace`（文件句柄在替换那一刻才生效）。
  别再用"能不能打开"来判断能不能装。
- **ZCode 升到 3.16.2 后两个 asar 插件都装不上了**。根因是 asar 头部格式变了：
  JSON 尾部多出 2 字节填充，于是 `c = d + 6`（3.15.3 是 `d + 4`），`d` 也不再是 4 的倍数。
  原来的 `parse_asar` 用 `c != d + 4` 严格断言，直接把新版 `app.asar` 判成"不是有效的 asar"。
  已在 `Zcode-Status-Daily-Use` v1.1.0 与 `Zcode-Status-TypeBar` 里改为按 JSON 解码器
  实际消耗的字节数定位数据区。
- **TypeBar 的 MCP server 会注册两次**。早期版本装在 `~/.zcode/zcode-token-usage-statusbar`，
  后来代码改成 `Zcode-Status-TypeBar`，旧目录残留，于是 `~/.zcode/cli/config.json` 的
  `mcp.servers` 里出现两条指向同一脚本的记录。装完顺手清掉了旧目录与旧条目。

### 命令行开关

| 参数 | 作用 |
| --- | --- |
| `--dry-run` | 只走完下载与校验，不等进程、不改任何东西 |
| `--offline` | 不联网，只用本地已缓存且哈希正确的包 |
| `--no-restart` | 装完不自动启动 ZCode |

### 构建

```
build.bat
```

用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 编译，不需要 Visual Studio。
升级插件版本时改 `src/Program.cs` 里的 `PLUGINS` 表（版本 / URL / SHA256）后重新编译。

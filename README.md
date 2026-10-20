# ZCode-Kybll-Plugins-AutoInstaller

一个 exe，装好三个 ZCode 插件。**拷到任何一台装了 ZCode 的 Windows 上，双击就行** —— 插件包从 CNB release 现下现装，不依赖本机有源码。

| 插件 | 作用 | 形态 |
| --- | --- | --- |
| [Zcode-Status-TypeBar](https://cnb.cool/kybll/Zcode-Status-TypeBar) | 输入框下方的 token 用量状态条（速度 / 首字 / 缓存 / 上下文 / 会话累计 / 今日合计） | 改 `app.asar` |
| [Zcode-Status-Daily-Use](https://cnb.cool/kybll/Zcode-Status-Daily-Use) | 设置 → 用量 里的「今日用量」卡片（当天概览 + 24 小时分布 + 按模型/按会话） | 改 `app.asar` |
| [ZCode-Search-Aggregation](https://cnb.cool/kybll/ZCode-Search-Aggregation) | 多引擎并行搜索（百度 / 必应 / 360 / 搜狗 / GitHub / Google / Yahoo / DuckDuckGo） | 原生插件 |

---

## 用法

1. 从 [Releases](https://cnb.cool/kybll/ZCode-Kybll-Plugins-AutoInstaller/-/releases) 下载 `Install-ZCode-Plugins.exe`。
2. 双击。
3. 看到提示后**完全退出 ZCode**：
   - 关掉所有 ZCode 窗口；
   - 在系统托盘（右下角隐藏图标里）右键 ZCode 图标 → **退出**。

   只关窗口是不够的 —— 托盘进程还在时会占着 `app.asar`，安装必定失败。
4. 安装器会自己接上继续装，装完自动拉起 ZCode。

**前置条件**：Windows + Python 3.8 或更新的 3.x（两个改 `app.asar` 的插件主体是 Python 脚本）。
没装 Python 时安装器会给出下载提示；装 Python 时记得勾选 *Add python.exe to PATH*。

**权限**：`app.asar` 装在 `C:\Users\<你>\AppData\Local\Programs\ZCode\resources\`，一般用户权限就能写。
装在 `Program Files` 的话需要用管理员身份运行。

---

## 它到底做了什么

本项目只是一个**调度器**，真正的安装逻辑在三个插件各自的 `install.py` 里（那部分才有测试覆盖）。
本程序负责：

1. 找 Python 与 ZCode；
2. 从 CNB release 下载三个 zip，**逐个校验 SHA256**，对不上就中止（不拿未知内容去改 `app.asar`）；
3. 解压（有 zip slip 防护：条目路径越出目标目录直接拒绝）；
4. 等 ZCode 退出（轮询进程，上限 30 分钟）；
5. 依次调用三个插件的安装器；
6. 汇总结果，必要时重新拉起 ZCode。

命令��开关：

| 参数 | 作用 |
| --- | --- |
| `--dry-run` | 只走完下载与校验，不等进程、不改任何东西。用来验证网络与包完整性。 |
| `--offline` | 不联网，只用本地已缓存且哈希正确的包。 |
| `--no-restart` | 装完不自动启动 ZCode。 |
| `--zcode-dir <路径>` | 手动指定 ZCode 安装目录。**优先级高于自动探测**。 |

日志写在 `%LOCALAPPDATA%\ZCode-Plugin-Installer\install-<时间戳>.log`。

---

## 怎么找 ZCode 的安装目录

别人机器上的用户名、盘符、装法都可能不同 —— 装在 D 盘、zip 免安装、装在 Program Files、
公司统一部署到某个目录。所以这里**不猜路径**，按可靠性从高到低试多个来源，
而且每个来源都靠**特征**确认（目录里确实有 `resources\app.asar`），不是靠名字猜：

| 顺序 | 来源 | 说明 |
| --- | --- | --- |
| 1 | 正在运行的 ZCode 进程 | 最准。进程跑着就说明装在这儿，路径直接读进程的 exe 路径。 |
| 2 | 桌面 / 开始菜单 / 公共目录里的快捷方式 | 递归找所有 `.lnk`，读它们的目标。**快捷方式改名叫什么都行** —— 判断依据是目标目录里有没有 `resources\app.asar`，不是名字里有没有 "ZCode"。 |
| 3 | 卸载注册表项 | `DisplayName` 含 ZCode 的项，看 `InstallLocation` 和 `DisplayIcon`。 |
| 4 | 常见默认目录 | `%LOCALAPPDATA%\Programs\ZCode` 等，全部用环境变量取，不写死用户名。 |
| 5 | 全盘扫描 | 只找名字叫 `ZCode` 的目录且里面有 `resources\app.asar`，限深 4 层。慢，放最后。 |

找到之后，**`app.asar` 的路径会显式传给两个插件的 `install.py`（`--asar`）**，
不指望插件自己猜 —— 插件自带的猜测逻辑只覆盖默认路径，在别人机器上会找错。

`zcode CLI`（`resources\glm\zcode.cjs`）也用同一个安装目录，不写死路径。

全都没找到时，安装器会列出试过的位置，并提示用 `--zcode-dir` 手动指定。

---

## 为什么必须等 ZCode 退出

前两个插件的生效方式，是在 `app.asar` 主进程入口（`out/main/index.js`）末尾追加一行动态 `import`。
`app.asar` 是 ZCode 进程打开着的文件，替换那一步在 Windows 上会撞上文件锁（`WinError 5`）。

有个坑：**`app.asar` 用 `open('r+b')` 是能打开的**，所以「试一下能不能写」这个判断方法在这里会给出
错误结论 —— 真正失败的是随后的 `os.replace`。别用它来判断。

---

## 从源码构建

```bat
build.bat
```

用 .NET Framework 自带的 `csc.exe` 编译，不需要装 Visual Studio：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:exe -optimize+ ^
    -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll ^
    -out:Install-ZCode-Plugins.exe src\Program.cs
```

改了插件版本怎么办：改 `src/Program.cs` 顶部的 `PLUGINS` 表（版本、URL、SHA256 三个字段），
重新编译。SHA256 用 `certutil -hashfile <file> SHA256` 或 `sha256sum` 算。

---

## License

GNU Affero General Public License v3.0 —— 与被安装的两个 `app.asar` 插件保持一致。

本项目会下载并执行上述三个插件的安装脚本，其中两个插件会读取本机的
`~/.zcode/cli/db/db.sqlite`（只读），属于对用户本地数据的程序性访问，故沿用 AGPL 而非 MIT。

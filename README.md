# 右键菜单管理 · C# / .NET 10 / WPF

> ⚠️ **本项目由 AI 协助完成，状态不稳定。** 可能存在尚未发现的缺陷，请自行评估使用风险。

Windows 10 / 11 右键菜单管理工具。直接读写真实注册表，开关即时生效，每次写入前先抓取现场快照，程序重启后依然可以逐条撤销。

主要能力：

- **64 位 + 32 位双视图扫描**：按 hive × 视图各扫一遍并合并，32 位（Wow6432Node）里注册的第三方 Shell 扩展不再漏掉，行上带"32 位"徽章，写入回原视图。
- **现场快照撤销**：写入前把受影响的键（自身值 + 一层子键）原样存下，随操作日志落盘 `history.json`；批量写入任一项失败会按现场整体回滚，不留半套状态。
- **按需提权续跑**：默认 `asInvoker`，扫描 / 备份 / 写 HKCU 全程无 UAC；仅写 HKLM 或全局 Blocked 时把操作队列写入临时文件 → UAC 提权重启 → `--resume-from-elevation` 自动续跑。
- **条目编辑器 + .reg 预览**：名称 / 命令 / 图标 / Shift / 位置 / 管理员包装就地编辑，对话框实时生成即将写入的 `.reg` 文本，可一键复制或另存脚本（UTF-16 LE + BOM）。
- **全量文件类型扫描**：除 8 个通配场景外，新增「文件类型」场景 —— 具体扩展名 / ProgID 上注册的项（含被其它工具写过 `LegacyDisable` / `ProgrammaticAccessOnly` 禁用标记的）全部可见可恢复；上千条按**所属程序分组显示**，配合“程序筛选”下拉与搜索框（支持按 exe/dll 名搜索）快速定位；"新建"菜单同时识别 ProgID 级 ShellNew（`txtfile\ShellNew`、`Folder\ShellNew` 这类不在扩展名键下的）。
- **自绘动画**：写入成功的行闪一次绿（启用）/ 红（禁用）光，列表载入按序号错峰淡入。

零第三方 NuGet 依赖：界面使用 .NET 9+ 内置的 Fluent 主题（`ThemeMode="System"`），自动跟随系统深浅色与强调色。

## 环境要求

- Windows 10 1809+ / Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（开发）
- .NET 10 桌面运行时（仅运行发布版时需要）

## 构建与运行

程序默认以普通权限运行（`asInvoker`），普通终端即可构建与运行，无需管理员：

```powershell
dotnet run --project ContextSmith
```

只有真正写入 HKLM 时才会弹一次 UAC，确认后窗口会带队列重启并自动把这批操作做完。

发布为单文件 exe：

```powershell
dotnet publish ContextSmith -c Release
# 输出：ContextSmith\bin\Release\net10.0-windows\win-x64\publish\ContextSmith.exe
```

如需免装运行时，把 csproj 中的 `SelfContained` 改为 `true`（体积约 70MB）。

## 目录结构

```
ContextSmith/
├── Models/
│   ├── MenuEntry       场景、菜单项模型（含 View / IconRaw / RunAsAdmin）
│   └── RegOp           .reg 预览用的最小写入描述 + KeySnapshot 现场模型
├── Services/
│   ├── MenuScanner     枚举 HKLM/HKCU × 64/32 位视图中真实存在的菜单项并合并
│   ├── MenuWriter      启用 / 禁用 / 新增 / 编辑 / 删除，写入前自动备份
│   ├── KeySnapshot     现场抓取与写回（撤销 / 批量回滚的核心）
│   ├── RegScript       生成 .reg 文本（UTF-16 LE + BOM），预览与另存脚本
│   ├── Elevation       asInvoker 权限判断 + runas 重启 + 待执行队列文件
│   ├── PendingOps      提权续跑的操作队列（System.Text.Json 序列化）
│   ├── BackupService   调用 reg.exe export / import（区分 /reg:32 与 /reg:64）
│   ├── TweakService    经典菜单、快捷方式箭头等系统优化
│   ├── ExplorerService SHChangeNotify、重启资源管理器、跳转 regedit
│   ├── IconLoader      ExtractIconEx 提取图标
│   └── Native          P/Invoke 声明
├── ViewModels/         MVVM（手写 ObservableObject / RelayCommand）
│   └── LogStore        操作日志 + 现场快照持久化到 history.json
├── Views/
│   ├── AddItemDialog   新建 / 编辑两用对话框，含实时 .reg 预览
│   └── Fx              行闪光、列表错峰淡入、抽屉滑动动画
└── MainWindow.xaml     主界面
```

## 实现要点

| 问题 | 处理方式 |
| --- | --- |
| HKCR 是合并视图 | 分别扫描 `HKLM\SOFTWARE\Classes` 与 `HKCU\SOFTWARE\Classes`，每一项记录真实 hive，写入时精确落回原处 |
| 32/64 位重定向 | 每个 hive 按 `RegistryView.Registry64` 与 `RegistryView.Registry32` 各扫一遍，按「hive + 逻辑路径」合并（64 位优先）；条目记住自己的视图，读、写、`reg export` 都回到同一视图 |
| Shell 命令禁用 | 写入 `LegacyDisable` 空字符串值，不删除任何数据 |
| 外壳扩展禁用 | 键名不是 GUID 时，在默认值 CLSID 前加 `-`（只影响当前位置）；键名本身就是 CLSID 时，写入 `Shell Extensions\Blocked`（全局生效，界面会标注"全局屏蔽"） |
| “新建”菜单禁用 | 用 `RegRenameKey` 把 `ShellNew` 重命名为 `ShellNew_CMMDisabled`，模板数据完整保留，并清除 Explorer 的 ShellNew 缓存 |
| 撤销 | 写入前抓取现场快照（自身值 + 一层子键，ShellEx 还含两边 Blocked 键），撤销 = 按现场精确写回；快照随日志落盘，重启后仍可撤销。ShellNew 因重命名导致路径变化，仅支持会话内撤销；系统优化走会话内闭包 |
| 批量失败 | 任一项写入失败，把本轮已抓取的现场整体还原，不留下半套状态 |
| 提权 | `asInvoker` 启动；写 HKLM / 全局 Blocked / 触碰 HKLM 的优化项 → 队列落盘 → UAC 重启 → `--resume-from-elevation` 续跑并合并成一条日志 |
| 生效 | 每次写入后调用 `SHChangeNotify(SHCNE_ASSOCCHANGED)`；外壳扩展与部分优化项需重启资源管理器，状态栏会提示 |
| 新增 / 编辑命令 | 新增写 HKCU；编辑就地改写原键；"以管理员运行"通过 `powershell Start-Process -Verb RunAs` 幂等包装 / 还原，并同步 `HasLUAShield` 盾牌 |

日志与快照：`%LocalAppData%\ContextSmith\history.json`
备份位置：`%LocalAppData%\ContextSmith\Backups`

## 已知限制

- Win11 新式菜单中由 MSIX 打包应用通过 `IExplorerCommand` 注册的项（如部分 UWP 应用）不在注册表中，本工具无法管理。
- “恢复经典菜单”使用的是非官方的 `{86ca1aa0-...}` 方案，截至 25H2 仍有效，但微软未承诺保留。
- 外壳扩展使用"CLSID 前加 -"方式禁用时，依赖 Explorer 对无效 CLSID 的跳过行为，个别扩展可能仍需改用全局屏蔽。
- 部分系统键由 TrustedInstaller 拥有，管理员也无法写入，程序会提示需先获取所有权。
- 提权续跑会以新进程重启窗口，当前窗口的筛选、选中状态不会带过去；队列本身不受影响。
- 以其他管理员账户提权运行时，HKCU 指向的是该管理员账户，而非当前登录用户。
- “以管理员运行”模板中，被右键的路径若含单引号，参数会被截断。

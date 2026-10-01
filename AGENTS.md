# AGENTS.md

## 项目概述

本项目是 Windows 10 / 11 的右键菜单管理工具（产品名「右键菜单管理」，仓库名 `ContextSmith`）。程序直接读写注册表，开关即时生效；写入前抓取现场快照，程序重启后仍可逐条撤销。

实现基于 WPF（.NET 10）。仓库曾做过一次完整的 Avalonia 12 迁移实验（双工程并存、移除 WPF、单文件打包三个提交），因单文件体积膨胀（29 MB 对 11 MB）决定还原为 WPF 版。实验代码完整保留在 git 历史中（`c9554a0` ～ `823b99c`），用 `git checkout 823b99c` 可查看当时全貌。

`README.md` 与当前实现同步；改动功能或目录结构后需同步更新。

## 仓库结构

```
ContextSmith/
├── ContextSmith/       主工程（解决方案内唯一项目）
│   ├── Models/               场景、菜单项模型
│   ├── Services/             扫描、写入、快照、备份、提权、系统优化、图标提取
│   ├── ViewModels/           MVVM 基础设施与主视图模型（LogStore 持久化日志）
│   ├── Views/                主窗口、新建/编辑对话框、Fx 动画
│   ├── Converters/           值转换器
│   ├── MainWindow.xaml App.xaml
│   └── app.manifest          asInvoker 清单
├── ContextSmith.slnx   解决方案
├── README.md
└── AGENTS.md
```

## 构建与运行

```powershell
# 构建
dotnet build ContextSmith.slnx

# 运行
dotnet run --project ContextSmith
```

发布：`dotnet publish ContextSmith -c Release` 输出单文件 `ContextSmith.exe`。

每次改动后的最低验证要求：编译 0 警告、0 错误；涉及界面时启动工程做冒烟检查，窗口应正常打开且不闪退。

## 编码约定

- 零第三方 NuGet 依赖：除 .NET 自带组件外不引入新的包；界面使用 .NET 内置 Fluent 主题（`ThemeMode="System"`）。
- 注释与文档使用中文，遵循仓库现有风格：全角标点，中文与英文、数字之间加半角空格。
- 提权语义不可破坏：程序默认以 `asInvoker` 运行，仅写入 HKLM 时写队列落盘、UAC 提权重启续跑。改动 `Elevation`、`PendingOps` 相关逻辑后，需实际走一遍提权流程。
- 注册表读写必须区分 64 位与 32 位视图，并把结果写回原视图；不允许绕过 `KeySnapshot` 现场快照直接写入。
- 还原决策记录：不要重新引入 Avalonia 或其它 UI 框架，除非维护者明确要求；单文件体积是选型约束。

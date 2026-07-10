# E-Detection Desktop

`desktop/` 包含 E-Detection 的 **C# + .NET 10 + WinUI 3** 原生桌面应用。检测、CSV 解析、规则执行、报告生成和事件处理均在进程内完成，不依赖 Python 或外部运行时桥接。

## 主要组件

- `EDetection.NativeCore/`：不依赖 WinUI 的原生 CSV 检测、事件、规则和报告核心；可在无交互 CI Runner 中运行。
- `EDetection.Desktop/`：WinUI 3 应用壳，负责界面、系统集成和设置，不承载检测规则。
- `EDetection.Desktop/Services/DesktopDiagnosticsService.cs`：输入、配置、输出目录与包完整性检查。
- `EDetection.Desktop/Services/DesktopHealthService.cs`：应用、通知、启动集成、设置存储和包健康状态。
- `EDetection.Desktop/Views` 与 `ViewModels`：检测工作台、设置、诊断、运行日志和报告历史。
- `EDetection.Desktop.Tests/`：仅引用 `EDetection.NativeCore` 的 `net10.0` 控制台冒烟测试；它会拒绝加载 WinUI 运行时。
- `scripts/`：发布、便携安装、安装器、包健康和桌面烟测。

## 构建与测试

```powershell
dotnet restore .\desktop\EDetection.Desktop.slnx
dotnet build .\desktop\EDetection.Desktop.slnx -c Debug
.\desktop\scripts\Test-DesktopNativeBackendSmoke.ps1 -NoBuild
```

## 发布

```powershell
.\desktop\scripts\Publish-Desktop.ps1 -RuntimeIdentifier win-x64
.\desktop\scripts\Test-DesktopPackageHealth.ps1 -PackagePath .\artifacts\desktop\win-x64\publish
.\desktop\scripts\Build-DesktopInstaller.ps1 -RuntimeIdentifier win-x64
.\desktop\scripts\Test-DesktopInstallerSmoke.ps1 -RuntimeIdentifier win-x64
```

`native-default` 是唯一支持的包形态。健康检查会拒绝 `python-runtime`、`python-wheelhouse`、`core` 和 `e_detection` 等旧资产，防止它们重新进入发布物。

本地构建安装器需要 Inno Setup 6。正式发布必须满足 GitHub 工作流的签名和发布姿态门禁。

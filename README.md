# EDetection 电气参数异常检测系统

EDetection 是一个面向电力监控/SCADA CSV 运行日报的 Windows 原生检测应用。项目现已完成从旧 Python 实现到 **C# + .NET 10 + WinUI 3** 的重构；仓库和发布包仅保留原生检测后端。

## 当前能力

- 递归扫描 CSV，跳过文件名含“高压”的高压侧日报。
- 识别 GBK、GB18030、UTF-8 与 UTF-8 BOM 编码，标准化常见中英文电气字段。
- 检测电压、电流、功率、功率因数、温度、数据冻结、设备离线、传感器缺失及 CT/PT 接线相关异常。
- 生成包含检测概览、异常明细、设备汇总、异常分类统计、传感器状态和检测配置的 `.xlsx` 报告。
- 提供 Windows 托盘、启动集成、任务栏进度、通知、设置迁移和安装包自检。

## 项目结构

```text
E-Detection-OSS/
├── desktop/
│   ├── EDetection.Desktop/       # WinUI 3 应用与原生检测后端
│   ├── EDetection.Desktop.Tests/ # 原生后端合同与报告烟测
│   ├── installer/                # Inno Setup 安装器定义
│   └── scripts/                  # 发布、健康检查和烟测脚本
├── config.json                   # 默认阈值与规则开关
├── global.json                   # .NET SDK 版本约束
└── README.md
```

## 本地构建

需要 Windows 10/11 x64、.NET 10 SDK 和 Windows App SDK 开发环境。

```powershell
dotnet restore .\desktop\EDetection.Desktop.slnx
dotnet build .\desktop\EDetection.Desktop.slnx -c Debug
.\desktop\scripts\Test-DesktopNativeBackendSmoke.ps1 -NoBuild
```

## 发布原生桌面版

```powershell
.\desktop\scripts\Publish-Desktop.ps1 -RuntimeIdentifier win-x64
.\desktop\scripts\Test-DesktopPackageHealth.ps1 -PackagePath .\artifacts\desktop\win-x64\publish
.\desktop\scripts\Build-DesktopInstaller.ps1 -RuntimeIdentifier win-x64
.\desktop\scripts\Test-DesktopInstallerSmoke.ps1 -RuntimeIdentifier win-x64
```

发布物为自包含的 WinUI 3 原生应用、便携 ZIP 与 Inno Setup 安装器；不包含 Python 运行时、Python 源码或 wheelhouse。正式 GitHub Release 仍须通过 Authenticode 签名门禁。

## 配置

`config.json` 保存检测阈值和规则开关，例如：

```json
{
  "V_MIN_THRESHOLD": 353.0,
  "V_MAX_THRESHOLD": 430.0,
  "I_MAX_THRESHOLD": 1000.0,
  "T_MAX_THRESHOLD": 70.0,
  "current_overload": true,
  "current_unbalance": false,
  "power_factor": false,
  "detail_output": false
}
```

配置由原生后端校验；缺失、无效或非有限数值会回退到安全默认值。

## 验证原则

原生后端已使用临时与历史真实数据目录做只读审计。对等验证、包健康检查、安装/升级烟测和发布姿态检查均以脚本输出为准；真实运行数据不会被项目脚本改写。

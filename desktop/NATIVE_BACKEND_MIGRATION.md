# 原生后端迁移完成记录

## 状态

E-Detection 已完成从 Python JSON Lines 进程桥接到进程内 **C# + .NET 10** 检测后端的迁移。`native-default` 是唯一支持的运行和发布配置；Python 后端、运行时、依赖锁定、兼容入口与对等测试基线已从仓库移除。

原生检测核心位于 `EDetection.NativeCore/`，不依赖 WinUI 或 Windows App SDK。`EDetection.Desktop/` 仅作为 WinUI 壳引用该核心；`EDetection.Desktop.Tests/` 是独立的 `net10.0` 控制台测试项目，仅引用核心，因此可在无交互 GitHub Actions Runner 中验证检测逻辑。

## 已验证能力

- 递归 CSV 发现、GBK/GB18030/UTF-8 编码选择、列名归一化和高压日报跳过。
- 电压、电流、功率、功率因数、温度、冻结、离线、传感器与 CT/PT 相关检测规则。
- 结构化运行事件、文件和目录统计、异常预览、设备与问题类型汇总。
- 原生 `.xlsx` 报告：检测概览、异常明细、设备汇总、异常分类统计、传感器状态和检测配置。
- 配置和设置迁移、托盘/通知/启动集成、包健康检查、便携安装和 Inno Setup 升级安装。

## 发布约束

- 所有发布和安装命令均固定使用唯一的原生发布配置。
- 包健康检查拒绝任何旧 Python bundle 目录或来源树文件。
- 正式 GitHub Release 仍须通过 Authenticode 签名、安装器烟测及发布姿态门禁。
- 真实数据目录只读审计是发布验证的一部分；审计工件不得含输入路径、设备名或遥测明文。
- 原生核心冒烟测试会在启动时断言未加载 `Microsoft.WindowsAppRuntime`、`Microsoft.UI.Xaml` 或 `WinUIEx`；CI 中不得以延长超时替代该隔离验证。

## 维护方向

后续规则、报告和 UI 改动只维护原生实现与 .NET 测试。任何重新引入 Python 运行时、进程桥接或 Python 依赖的变更都属于架构回退，必须经过单独设计审查。

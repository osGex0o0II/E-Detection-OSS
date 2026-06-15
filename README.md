# E-Detection 电气参数异常检测系统

E-Detection 用于批量分析电力监控/SCADA 系统导出的 CSV 运行日报，自动识别低压侧电压、电流、有功功率、功率因数、温度、采集冻结、设备离线、传感器缺失等异常，并输出结构化异常报告。

当前阶段重点是工程化改造：核心检测逻辑已经从旧 GUI 单文件中拆出，命令行、测试、后续 GUI 和未来 LLM 报告生成都可以复用同一套检测管线。

## 当前能力

- 递归扫描目录下所有 CSV 文件。
- 自动识别 GBK、GB18030、UTF-8 等常见 CSV 编码。
- 自动标准化中文字段头，例如 `综合楼1TM1-Uab`、`A相温度`、`有功功率`。
- 自动跳过文件名/路径包含“高压”的高压侧运行日报。
- 基于规则插件检测电压异常、电流过载、电流不平衡、数据冻结、CT 极性异常、功率因数过低、温度异常等。
- 输出 `.xlsx` 多 Sheet 报告，支持异常行填色、异常参数单元格标注、筛选和冻结窗格。
- 提供可测试的 Python 包接口和 CLI。

## 项目结构

```text
E-Detection-OSS/
├── core/
│   ├── rule_base.py            # DetectionContext 和 BaseRule
│   └── rules.py                # 规则插件实现
├── e_detection/
│   ├── settings.py             # 默认阈值、规则开关、配置校验
│   ├── pipeline.py             # 单文件读取、清洗、检测、报告格式化
│   ├── batch.py                # 目录级批处理
│   ├── cli.py                  # 命令行入口
│   └── __main__.py             # python -m e_detection
├── tests/
│   ├── test_rules.py           # 规则单元测试
│   ├── test_settings.py        # 配置校验测试
│   └── test_pipeline_samples.py# 真实样本 smoke test
├── E-anomaly_detector-Portable_version-v20260520.py
│                              # 兼容 GUI 入口，核心检测已转为导入 e_detection.pipeline
├── run_check.py                # CLI 兼容入口
├── run_check_v2.py             # A/B 规则方案对比入口
├── config.json                 # 默认阈值与规则开关
├── pyproject.toml              # 包元数据、依赖、测试配置
├── requirements.txt
└── requirements-dev.txt
```

## 快速开始

建议使用 Python 3.11+。Windows 11 64 位目标环境优先。

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements-dev.txt
```

运行单元测试：

```powershell
python -m pytest
```

命令行检测：

```powershell
python -m e_detection "D:\System Files\Desktop\2025-09-06_电力监控系统运行数据"
```

只查看摘要，不生成报告：

```powershell
python -m e_detection "D:\System Files\Desktop\2025-09-06_电力监控系统运行数据" --no-report --show-lines 5
```

使用兼容脚本：

```powershell
python run_check.py "D:\System Files\Desktop\2025-09-06_电力监控系统运行数据"
python run_check_v2.py "D:\System Files\Desktop\2025-09-06_电力监控系统运行数据"
```

## 配置文件

`config.json` 同时保存阈值和规则开关：

```json
{
    "V_MIN_THRESHOLD": 353.0,
    "V_MAX_THRESHOLD": 430.0,
    "I_MAX_THRESHOLD": 1000.0,
    "I_UNBALANCE_MAX_THRESHOLD": 0.15,
    "P_ACTIVE_MIN_THRESHOLD": 0.0,
    "PF_MIN_THRESHOLD": 0.9,
    "T_MIN_THRESHOLD": 0.0,
    "T_MAX_THRESHOLD": 70.0,
    "I_MIN_ACTIVE_THRESHOLD": 1.0,
    "FREEZE_COUNT_THRESHOLD": 3,
    "FREEZE_STD_THRESHOLD": 0.01,
    "V_IMBALANCE_THRESHOLD": 0.02,
    "current_overload": true,
    "current_unbalance": false,
    "power_factor": false,
    "detail_output": false
}
```

配置会通过 `e_detection.settings.normalize_config()` 校验。缺失、非法、NaN、无穷值会回退到默认值；旧版 `current` 开关会兼容映射到 `current_overload` 和 `current_unbalance`。

## 检测规则

规则实现位于 `core/rules.py`，均继承 `BaseRule`：

- `VoltageRule`：电压越限、三相不平衡、疑似 PT 接线异常。
- `CurrentOverloadRule`：负载状态下电流过大。
- `CurrentUnbalanceRule`：三相电流不平衡。
- `FreezeRule`：核心列数据冻结，排除备用设备误报。
- `PowerActiveRule`：有功功率异常、疑似 CT 极性异常。
- `PowerFactorRule`：功率因数过低。
- `TemperatureRule`：A/B/C 相温度越限。

新增规则时，优先放在 `core/rules.py`，并在 `e_detection.pipeline._detect_core_logic()` 中注册。

## 输出报告

CLI 默认在输入目录或 `--output-dir` 指定目录下生成：

```text
电气异常报告_YYYYMMDD_HHMMSS.xlsx
```

报告包含 `检测概览`、`异常明细`、`设备汇总`、`异常分类统计`、`传感器状态`、`检测配置` 等 Sheet。`异常明细` 会按严重等级填充行底色，并对触发异常的电气参数单元格做重点标注。

## 真实样本验证

当前样本目录：

```text
D:\System Files\Desktop\2025-09-06_电力监控系统运行数据
```

已知样本形态：

- CSV 总数：43
- 高压侧跳过：8
- 低压/其他：35
- 默认配置检测摘要：28 个正常文件、7 个异常文件、45 条异常记录、8 个跳过文件

样本 smoke test 会在该目录不存在时自动跳过，方便后续 CI 环境运行。

## 后续路线

优先级建议：

1. 继续把 GUI 内部拆成独立 `gui/` 模块，保留当前兼容入口。
2. 引入结构化异常对象，减少字符串拼接对统计逻辑的影响。
3. 增加 JSON 事实包，为 LLM 巡检报告提供稳定输入。
4. 增加 PyInstaller spec，面向 Windows 11 x64 输出发行包。
5. 预留 LLM 报告生成接口：将异常汇总转为结构化 JSON，再由 LLM 生成巡检报告和处置建议。

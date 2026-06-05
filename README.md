# E-Detection — Electrical Parameter Anomaly Detection System

**E-Detection** is a desktop application for the automated detection of anomalies in electrical monitoring data. It reads daily operation reports (CSV) from substation SCADA systems, identifies abnormal conditions across voltage, current, active power, power factor, and temperature parameters, and generates structured anomaly reports.

## Features

### Core Detection Engine (9 rules, plugin-based architecture)

| Rule | Description | Configurable |
|---|---|---|
| VoltageRule | Over/under-voltage, phase imbalance, total-zero shutdown filter, PT wiring anomaly detection | Always on |
| CurrentOverloadRule | Per-phase overcurrent detection | `current_overload` |
| CurrentUnbalanceRule | Three-phase current imbalance (Imax−Imin)/Imean | `current_unbalance` |
| FreezeRule | Data freeze detection (acquisition system failure); distinguishes standby (no-load) from true freeze | Always on |
| PowerActiveRule | Active power below minimum threshold; CT reverse-polarity detection (negative power) | Always on |
| PowerFactorRule | Power factor below minimum threshold | `power_factor` |
| TemperatureRule | Temperature out of range | Always on |
| SuddenChangeRule | Step-change detection (adjacent sample ratio exceedance) for short/open/sensor faults | `sudden_change` |
| CrossParamRule | Cross-parameter correlation: normal voltage + abnormal current deviation, three-phase simultaneous swell | `cross_param` |

### Key Improvements

- **Standby device filtering**: Transformers with zero load current and normal voltage are classified as "standby" rather than "data freeze"
- **PT wiring anomaly detection**: Identifies single-phase PT wiring issues (one phase deviates >30%, others <15%) distinct from general imbalance
- **CT reverse-polarity detection**: Detects negative active power with active load, indicating CT wiring errors
- **Sensor missing suppression**: All-zero columns (uninstalled sensors) are noted in the summary footer rather than repeated on every row

### Application Features

- Graphical user interface (CustomTkinter) with threshold configuration and rule toggles
- Dual-page layout: configuration page → automatic switch to report page upon completion
- Report page with "New Detection" / "Export Report" / "Open Report Folder" buttons
- CSV anomaly report with separated columns: `type` (category) and `detail` (specific values)
- Batch processing with recursive directory scanning, progress bar, and stop-on-demand
- Automatic encoding detection (GBK/UTF-8) for SCADA CSV exports
- High-voltage equipment automatic skip (filename keyword)

## Project Structure

```
E-Detection-OSS/
├── E-anomaly_detector-Portable_version-v20260520.py   # Main application (GUI + engine)
├── config.json                                         # Default thresholds and rule switches
├── core/
│   ├── __init__.py
│   ├── rule_base.py                                    # DetectionContext + BaseRule abstract class
│   └── rules.py                                        # 9 anomaly detection rule implementations
└── run_check.py / run_check_v2.py                      # CLI batch detection scripts
```

## Configuration (`config.json`)

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
    "detail_output": false,
    "power_factor": false,
    "current_unbalance": false,
    "current_overload": true
}
```

All thresholds are adjustable via the GUI. The last four keys are boolean rule switches.

## CSV Data Format

Input CSV files should contain columns that map to the following normalized names:

| Normalized Name | Typical Original Column Names |
|---|---|
| `Uab`, `Ubc`, `Uca` | Ua(V), A phase voltage, Uab, etc. |
| `Ia`, `Ib`, `Ic` | Ia(A), A phase current, Ia, etc. |
| `active_power` | Active power, P, etc. |
| `reactive_power` | Reactive power, Q, etc. |
| `power_factor` | Power factor, PF, etc. |
| `A_phase_temp`, `B_phase_temp`, `C_phase_temp` | A phase temperature, etc. |

The first column is expected to be a timestamp. The column name mapper uses regex matching and is tolerant of SCADA export variations.

## Requirements

- Python 3.10+
- pandas, numpy
- customtkinter
- chardet (optional, for encoding detection)

## Usage

### GUI

```bash
python E-anomaly_detector-Portable_version-v20260520.py
```

1. Select the directory containing CSV daily reports
2. (Optional) Select a report output directory
3. Configure thresholds and enable/disable optional rules
4. Click "Start Detection"
5. After completion, the report page displays automatically

### CLI

```bash
python run_check.py          # Basic batch detection
python run_check_v2.py       # With comparison analysis (A/B scheme)
```

## License

This project is released as open source software.

---

# E-Detection — 電気パラメータ異常検知システム

**E-Detection** は、電力監視データの異常を自動検出するデスクトップアプリケーションです。変電所 SCADA システムの日報（CSV）を読み取り、電圧・電流・有効電力・力率・温度の異常状態を識別し、構造化された異常レポートを生成します。

## 主な機能

### コア検出エンジン（9ルール、プラグインアーキテクチャ）

| ルール | 説明 | 設定可能 |
|---|---|---|
| VoltageRule | 過電圧/不足電圧、相不平衡、全相ゼロ停止フィルタ、PT配線異常検出 | 常時有効 |
| CurrentOverloadRule | 相別過電流検出 | `current_overload` |
| CurrentUnbalanceRule | 三相電流不平衡 (Imax−Imin)/Imean | `current_unbalance` |
| FreezeRule | データ凍結検出（収集システム障害）；無負荷待機状態と真の凍結を区別 | 常時有効 |
| PowerActiveRule | 有効電力下限値未満；CT逆極性検出（負の有効電力） | 常時有効 |
| PowerFactorRule | 力率下限値未満 | `power_factor` |
| TemperatureRule | 温度範囲外 | 常時有効 |
| SuddenChangeRule | 隣接サンプル間の急変検出（短絡/断線/センサー故障） | `sudden_change` |
| CrossParamRule | クロスパラメータ相関分析：電圧正常＋電流異常偏差、三相同時電圧上昇 | `cross_param` |

### 主な改善点

- **待機機器フィルタリング**: 負荷電流ゼロかつ電圧正常の変圧器を「待機中」と判定し、「データ凍結」と誤検出しない
- **PT配線異常検出**: 単相PT配線問題（1相が30%超乖離、他2相が15%未満）を通常の不平衡と区別して識別
- **CT逆極性検出**: 負荷がある状態で有効電力が負の場合、CT配線ミスとして検出
- **センサー未設置抑制**: 全ゼロ列（未設置センサー）はサマリーフッターに集約し、各行に繰り返し表示しない

### アプリケーション機能

- GUI（CustomTkinter）による閾値設定・ルール切替
- 2画面構成：設定画面 → 検出完了後自動でレポート画面に切替
- レポート画面：「新しい検出」「レポート出力」「レポートフォルダを開く」ボタン
- CSVレポート：`異常類型`（カテゴリ）と `異常詳情`（具体的な値）に分離
- 再帰的ディレクトリスキャン、プログレスバー、随時停止対応
- SCADAエクスポートCSVのエンコーディング自動検出（GBK/UTF-8）
- 高圧機器ファイルの自動スキップ

## 必要条件

- Python 3.10+
- pandas, numpy
- customtkinter
- chardet（オプション、エンコーディング検出用）

---

# E-Detection — 电气参数异常检测系统

**E-Detection** 是一款用于电力监控数据异常自动检测的桌面应用程序。它读取变电站 SCADA 系统的运行日報（CSV 文件），自动识别电压、电流、有功功率、功率因数、温度等参数的异常状态，并生成结构化的异常报告。

## 功能特性

### 核心检测引擎（9 条规则，插件化架构）

| 规则 | 说明 | 可配置开关 |
|---|---|---|
| VoltageRule | 电压越限、相不平衡、三相全零停机过滤、PT 接线异常检测 | 始终启用 |
| CurrentOverloadRule | 分相过流检测 | `current_overload` |
| CurrentUnbalanceRule | 三相电流不平衡度 (Imax−Imin)/Imean | `current_unbalance` |
| FreezeRule | 数据冻结检测（采集系统故障）；区分设备备用（无负载）与真实冻结 | 始终启用 |
| PowerActiveRule | 有功功率低于下限；CT 极性接反检测（负功率） | 始终启用 |
| PowerFactorRule | 功率因数低于下限 | `power_factor` |
| TemperatureRule | 温度越限 | 始终启用 |
| SuddenChangeRule | 相邻采样点突变检测，判别短路/断路/传感器故障 | `sudden_change` |
| CrossParamRule | 跨参数关联分析：电压正常但电流异常偏离、三相电压同步升高 | `cross_param` |

### 核心改进

- **设备备用过滤**: 负载电流为零且电压正常的变压器判定为"备用"，不再误报为"数据冻结"
- **PT 接线异常检测**: 识别单相 PT 接线问题（一相偏离 >30%、另两相 <15%），与普通电压不平衡区分
- **CT 极性接反检测**: 有负载情况下有功功率为负时标记"CT 极性异常"，指向接线错误
- **传感器未配置去噪**: 全零列（未安装传感器）仅在汇总尾部标注，不再逐行重复

### 应用功能

- 图形用户界面（CustomTkinter），支持阈值调节和规则开关
- 双页面布局：配置页 → 检测完成后自动切换至报告页
- 报告页含"新的检测"/"导出报告"/"打开报告目录"按钮
- CSV 异常报告列分离：`异常类型`（分类名）+ `异常详情`（具体数值）
- 批量递归扫描，进度条，支持随时停止
- 自动识别 CSV 编码（GBK/UTF-8）
- 高压设备文件自动跳过

## 项目结构

```
E-Detection-OSS/
├── E-anomaly_detector-Portable_version-v20260520.py   # 主程序（GUI + 检测引擎）
├── config.json                                         # 默认阈值与规则开关
├── core/
│   ├── __init__.py
│   ├── rule_base.py                                    # DetectionContext + BaseRule 抽象基类
│   └── rules.py                                        # 9 条异常检测规则实现
└── run_check.py / run_check_v2.py                      # 命令行批量检测脚本
```

## 配置文件 (`config.json`)

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
    "detail_output": false,
    "power_factor": false,
    "current_unbalance": false,
    "current_overload": true
}
```

所有阈值可在 GUI 中调整。末尾四项为规则开关（布尔值）。

## CSV 数据格式

输入 CSV 文件需包含可映射为以下标准名称的列：

| 标准名称 | 常见原始列名 |
|---|---|
| `Uab`, `Ubc`, `Uca` | Ua(V)、A相电压、Uab 等 |
| `Ia`, `Ib`, `Ic` | Ia(A)、A相电流、Ia 等 |
| `有功功率` | 有功功率、P、Active Power 等 |
| `无功功率` | 无功功率、Q、Reactive Power 等 |
| `功率因数` | 功率因数、PF 等 |
| `A相温度`, `B相温度`, `C相温度` | A相温度、A温度 等 |

首列为时间列。列名映射使用正则表达式宽泛匹配，可兼容不同 SCADA 厂家的导出格式。

## 使用方式

### GUI 模式

```bash
python E-anomaly_detector-Portable_version-v20260520.py
```

1. 选择包含 CSV 日報文件的目录
2. （可选）选择报告输出目录
3. 调整阈值，启用/禁用可选规则
4. 点击"开始检测"
5. 检测完成后自动显示报告页

### 命令行模式

```bash
python run_check.py          # 基础批量检测
python run_check_v2.py       # 含对比分析（A/B 方案）
```

## 环境要求

- Python 3.10+
- pandas, numpy
- customtkinter
- chardet（可选，用于编码检测）

## 开源许可

本项目以开源软件形式发布。

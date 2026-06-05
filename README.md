# E-Detection — 电气参数异常检测系统

**E-Detection** 是一款用于电力监控数据异常自动检测的桌面应用程序。它读取变电站 SCADA 系统导出的运行日報（CSV 格式），自动识别电压、电流、有功功率、功率因数、温度等电气参数的异常状态，并生成结构化的异常报告（CSV 文件）和检测汇总日志。

## 目录

- [功能特性](#功能特性)
- [项目结构](#项目结构)
- [架构设计](#架构设计)
- [检测规则详解](#检测规则详解)
- [配置文件](#配置文件)
- [CSV 输入格式](#csv-输入格式)
- [报告输出](#报告输出)
- [使用方式](#使用方式)
- [环境要求](#环境要求)
- [开源许可](#开源许可)

## 功能特性

### 应用层功能

- **图形用户界面**：基于 CustomTkinter 构建的现代化桌面窗口，支持阈值调节和规则开关
- **批量递归扫描**：自动遍历所选目录及子目录下所有 CSV 文件，配合进度条实时反馈处理进度
- **随时停止**：检测过程中可随时取消，已处理的结果会写入报告文件
- **编码自适应**：自动检测 CSV 文件编码（GBK/UTF-8/GB18030），兼容不同 SCADA 厂家的导出格式
- **高压设备自动跳过**：文件名含"高压"关键词的 CSV 文件自动跳过，避免高压侧阈值冲突
- **日志导出**：操作日志可导出为 TXT 文本文件

### 输出层功能

- **逐行实时日志**：每处理一个文件立即输出一行简述，异常类型自动归纳归并（最多 3 类）
- **CSV 异常报告**：输出 GBK 编码的 CSV 文件，包含 `来源文件`、`日期`、`异常类型`、`异常详情`、`异常值`、`时间` 及电气参数列
- **检测汇总日志**：所有文件处理完毕后输出按建筑→变压器聚合的汇总报告，含传感器未配置一览、采集故障、设备离线等特殊项

## 项目结构

```
E-Detection-OSS/
├── E-anomaly_detector-Portable_version-v20260520.py   # 主程序（GUI界面 + 检测引擎 + 报告生成）
├── config.json                                         # 默认阈值配置与规则开关
├── core/
│   ├── __init__.py                                     # 规则插件包
│   ├── rule_base.py                                    # DetectionContext 上下文 + BaseRule 抽象基类
│   └── rules.py                                        # 9 条异常检测规则的具体实现
├── run_check.py                                        # 命令行批量检测脚本（基础版）
└── run_check_v2.py                                     # 命令行批量检测脚本（含 A/B 方案对比分析）
```

### 核心文件说明

**`E-anomaly_detector-Portable_version-v20260520.py`**（约 2200 行）

主程序文件，包含以下模块级函数和类：

| 函数/类 | 职责 |
|---|---|
| `extract_date_from_filename()` | 从文件名提取日期字符串（YYYY-MM-DD 格式） |
| `_format_log_message()` | 将检测结果格式化为单行日志（含 tag 标签用于 UI 颜色渲染） |
| `clean_and_rename_columns()` | 将原始 CSV 列名通过正则映射标准化为 `Uab/Ia/有功功率` 等 |
| `_load_and_clean_data()` | CSV 加载管线：编码嗅探 → 读取 → 截断尾部统计行 → 列名标准化 → 数值清洗 |
| `_detect_core_logic()` | 遍历 9 条规则插件，合并异常掩码和描述文本 |
| `_is_only_freeze_types()` | 判断异常行是否仅含采集层面问题（冻结/恒定/传感器缺失） |
| `_summarize_types()` | 同根类型归纳（A/B/C相电压过低 → 电压过低(A/B/C相)），用于日志精简 |
| `_compact_type()` | 保留代表性数值的精简类型名，用于 CSV 异常类型列 |
| `_extract_anomaly_value()` | 从 DataFrame 行数据中提取 `参数=数值` 格式的异常值摘要 |
| `_format_detail_structured()` | 将原始异常详情按类型分组，用 `|` 分隔，用于 CSV 异常详情列 |
| `_format_anomaly_report()` | 生成异常 DataFrame（含类型/详情/异常值三列）+ 日志摘要 dict |
| `check_anomaly_in_file()` | 单文件完整检测管线：跳过→加载→检验→规则判定→报告格式化 |
| `_extract_building_and_transformer()` | 从文件路径提取建筑物名和变压器编号 |
| `_check_frozen_acquisition()` | 判断电压电流数据是否全程不变（疑似采集系统故障） |
| `_extract_transformer_issues()` | 从异常 DataFrame 提取按异常类别的聚合摘要（次数/详情/时间范围） |
| `ElectricalAnomalyDetectorApp` | CustomTkinter 主窗口类，含 UI 布局、检测线程、报告输出、日志渲染 |

**`core/rule_base.py`**

| 类 | 职责 |
|---|---|
| `DetectionContext` | 封装单次检测的 DataFrame，提供 `has_column()`、`series()`、`available()` 辅助方法 |
| `BaseRule` | 规则抽象基类，子类实现 `detect()` 方法返回 `(异常描述Series, 布尔掩码Series)` |

**`core/rules.py`**

9 条检测规则的具体实现（详见 [检测规则详解](#检测规则详解)）。

## 架构设计

### 插件化规则引擎

规则引擎采用策略模式（Strategy Pattern）+ 抽象基类：

```
BaseRule (ABC)
  ├── rule_key: str | None        # None = 始终启用，否则为 enabled_rules 字典中的键
  ├── is_enabled(enabled_rules)    # 判断当前规则是否启用
  └── detect(context, thresholds, enabled_rules) → (anomaly_text_series, anomaly_mask_series)
```

新增规则只需继承 `BaseRule` 并实现 `detect()` 方法，然后在 `_detect_core_logic()` 的规则列表中注册即可。

### 检测管线

```
CSV 文件
  ↓ (1) 文件名校验：含"高压"→ 跳过
  ↓ (2) 编码嗅探（chardet）+ pandas 读取
  ↓ (3) 列名标准化（正则映射：Ua(V)→Uab, A相电压→Uab, 等）
  ↓ (4) 数值清洗（无效值 -1.0 / 2867.2 标记，尾部统计行截断）
  ↓ (5) 离线检测（电压电流列 -1.0 占比 >50% → 设备离线）
  ↓ (6) 传感器缺省检测（无功功率/功率因数/温度列全零 → 记录到 extra_info）
  ↓ (7) 9 条规则并行判定 → 合并异常掩码 → _format_anomaly_report()
  ↓ (8) 输出：逐行日志 + 累积统计变量 + CSV 批量写入
```

### 报告生成三层模型

| 层级 | 触发时机 | 输出格式 | 关键函数 |
|---|---|---|---|
| 逐行日志 | 每处理完一个文件 | 一行简述，同根类型归纳，最多 3 类 | `_format_log_message()` + `_summarize_types()` |
| CSV 报告 | 每 50 个文件批量写入一次 | 每行 = 1 个异常点，含类型/详情/异常值三列 | `_format_anomaly_report()` + `_flush_anomaly_batches()` |
| 检测汇总 | 所有文件处理完毕后一次性输出 | 按建筑→变压器聚合，含传感器未配置一览 | `_output_detection_summary()` |

## 检测规则详解

### 始终启用的规则（rule_key = None）

#### VoltageRule — 电压异常检测

检测逻辑分三步：

1. **三相全零停机过滤**：三相电压均 < 1V 时判定为设备正常停机，不标记异常
2. **PT 接线异常检测**（新增）：当三相线电压中一相偏离中位数 > 30%、另两相偏离 < 15% 时，标记为"疑似 PT 接线异常"而非普通电压不平衡。典型场景为 PT 二次侧断线或接线错误（如数据中心 1TM4：Uab≈410V, Ubc≈267V, Uca≈206V）
3. **相电压不平衡检测**：单相偏离三相均值超过 `V_IMBALANCE_THRESHOLD`（默认 2%）时标记。已标记为 PT 异常的行不再重复标记为电压不平衡
4. **单相越限检测**：单相低于 `V_MIN_THRESHOLD` 或高于 `V_MAX_THRESHOLD` 时标记

相关阈值：`V_MIN_THRESHOLD`、`V_MAX_THRESHOLD`、`V_IMBALANCE_THRESHOLD`

#### FreezeRule — 数据冻结检测

1. **设备备用判定**（新增）：三相电流均 < `I_MIN_ACTIVE_THRESHOLD` 且三相电压在正常范围内时，判定为设备备用状态（如变压器空载运行），不标记为数据冻结。解决了旧版将 15 个备用变压器误报为"数据冻结"的问题
2. **核心列冻结**（Uab/Ubc/Uca/Ia/Ib/Ic/有功功率）：连续 `FREEZE_COUNT_THRESHOLD`（默认 3）个采样点的相邻差值 < `FREEZE_STD_THRESHOLD`（默认 0.01），且 ≥ 2 列同时满足才标记为"数据冻结"
3. **辅助列恒定**（无功功率/功率因数）：单独检测恒定，但全零列统一由上层汇总报告，不在每行重复标记

相关阈值：`FREEZE_COUNT_THRESHOLD`、`FREEZE_STD_THRESHOLD`、`I_MIN_ACTIVE_THRESHOLD`

#### PowerActiveRule — 有功功率异常检测

1. **CT 极性接反检测**（新增）：有功功率 < 0 且存在负载电流时标记为"CT 极性异常"。典型场景为电流互感器（CT）二次侧极性接反导致功率方向翻转（如综合楼 1TM4，多时段有功功率为 -59~-172kW）
2. **有功功率过低**：有功功率 < `P_ACTIVE_MIN_THRESHOLD` 且非 CT 极性问题时标记

相关阈值：`P_ACTIVE_MIN_THRESHOLD`、`I_MIN_ACTIVE_THRESHOLD`

#### TemperatureRule — 温度异常检测

分相检测温度是否超出 `T_MIN_THRESHOLD` ~ `T_MAX_THRESHOLD`（默认 0~70°C）范围。低于下限或高于上限均标记。

相关阈值：`T_MIN_THRESHOLD`、`T_MAX_THRESHOLD`

### 可配置开关的规则

#### CurrentOverloadRule — 电流过载检测（开关：`current_overload`）

分相检测电流是否超过 `I_MAX_THRESHOLD`（默认 1000A）。仅在存在负载（任一相电流 ≥ `I_MIN_ACTIVE_THRESHOLD`）时触发。

相关阈值：`I_MAX_THRESHOLD`、`I_MIN_ACTIVE_THRESHOLD`

#### CurrentUnbalanceRule — 电流不平衡检测（开关：`current_unbalance`）

计算三相电流不平衡度 `(Imax - Imin) / Imean`，超过 `I_UNBALANCE_MAX_THRESHOLD`（默认 15%）时标记。仅在存在负载且有 ≥ 2 相电流数据时触发。

相关阈值：`I_UNBALANCE_MAX_THRESHOLD`、`I_MIN_ACTIVE_THRESHOLD`

#### PowerFactorRule — 功率因数过低检测（开关：`power_factor`）

检测功率因数是否低于 `PF_MIN_THRESHOLD`（默认 0.90）。仅在存在负载且 CSV 中含有功率因数列时触发。

相关阈值：`PF_MIN_THRESHOLD`、`I_MIN_ACTIVE_THRESHOLD`

#### SuddenChangeRule — 数据突变检测（开关：`sudden_change`）

检测相邻采样点间的剧烈跳变，用于判别短路、断路或传感器故障。电流突变阈值 `IA_SKIP_THRESHOLD`（默认 0.5，即相邻点变化超过 50%），电压突变阈值 `V_SKIP_THRESHOLD`（默认 0.2）。

相关阈值：`IA_SKIP_THRESHOLD`、`V_SKIP_THRESHOLD`

#### CrossParamRule — 跨参数关联分析（开关：`cross_param`）

1. **电压正常但电流异常偏离**：三相电压均在正常范围内，但某相电流偏离日均均值超过 2.5 倍标准差
2. **三相电压同步升高**：三相电压同时较前 3 个采样点上升超过 10%，且当前值接近电压上限的 95%，可能为系统过电压前兆

## 配置文件

`config.json` 存储默认阈值和规则开关。所有参数均可在 GUI 中调整并即时生效。

```json
{
    "V_MIN_THRESHOLD": 353.0,           // 电压下限 (V)
    "V_MAX_THRESHOLD": 430.0,           // 电压上限 (V)
    "V_IMBALANCE_THRESHOLD": 0.02,      // 相电压不平衡度阈值 (2%)
    "I_MAX_THRESHOLD": 1000.0,          // 电流上限 (A)
    "I_UNBALANCE_MAX_THRESHOLD": 0.15,  // 电流不平衡度上限 (15%)
    "I_MIN_ACTIVE_THRESHOLD": 1.0,      // 电流激活下限 (A)，低于此值视为无负载
    "P_ACTIVE_MIN_THRESHOLD": 0.0,      // 有功功率下限 (kW)
    "PF_MIN_THRESHOLD": 0.9,            // 功率因数下限
    "T_MIN_THRESHOLD": 0.0,             // 温度下限 (°C)
    "T_MAX_THRESHOLD": 70.0,            // 温度上限 (°C)
    "FREEZE_COUNT_THRESHOLD": 3,        // 冻结判定连续采样点数
    "FREEZE_STD_THRESHOLD": 0.01,       // 冻结判定波动阈值
    "current_overload": true,           // 电流过载检测开关
    "current_unbalance": false,         // 电流不平衡检测开关
    "power_factor": false,              // 功率因数检测开关
    "detail_output": false              // 详细输出模式开关
}
```

### 阈值说明

| 参数 | 默认值 | 单位 | 说明 |
|---|---|---|---|
| `V_MIN_THRESHOLD` | 353.0 | V | 线电压下限，低于此值标记"电压过低" |
| `V_MAX_THRESHOLD` | 430.0 | V | 线电压上限，高于此值标记"电压过高" |
| `V_IMBALANCE_THRESHOLD` | 0.02 | 比例 | 单相偏离三相均值的比例阈值 |
| `I_MAX_THRESHOLD` | 1000.0 | A | 电流上限 |
| `I_UNBALANCE_MAX_THRESHOLD` | 0.15 | 比例 | 三相电流不平衡度 (Imax-Imin)/Imean |
| `I_MIN_ACTIVE_THRESHOLD` | 1.0 | A | 判断是否有负载的最小电流 |
| `P_ACTIVE_MIN_THRESHOLD` | 0.0 | kW | 有功功率下限 |
| `PF_MIN_THRESHOLD` | 0.90 | — | 功率因数下限 |
| `T_MIN_THRESHOLD` | 0.0 | °C | 温度下限 |
| `T_MAX_THRESHOLD` | 70.0 | °C | 温度上限 |
| `FREEZE_COUNT_THRESHOLD` | 3 | 个 | 连续多少采样点不变判定为冻结 |
| `FREEZE_STD_THRESHOLD` | 0.01 | — | 冻结判定的波动阈值 |

## CSV 输入格式

### 列名映射

输入 CSV 的首列为时间列。系统通过正则表达式将原始列名映射为标准名称：

| 标准名称 | 匹配规则（正则，大小写不敏感） | 常见原始列名示例 |
|---|---|---|
| `Uab` | `Uab`、`Ua(`、`Ua电压`、`A相电压`、`^Ua$` | Ua(V)、A相电压、Uab |
| `Ubc` | `Ubc`、`Ub(`、`Ub电压`、`B相电压`、`^Ub$` | Ub(V)、B相电压、Ubc |
| `Uca` | `Uca`、`Uc(`、`Uc电压`、`C相电压`、`^Uc$` | Uc(V)、C相电压、Uca |
| `Ia` | `Ia`、`A.*电流`、`^Ia$` | Ia(A)、A相电流 |
| `Ib` | `Ib`、`B.*电流`、`^Ib$` | Ib(A)、B相电流 |
| `Ic` | `Ic`、`C.*电流`、`^Ic$` | Ic(A)、C相电流 |
| `有功功率` | `有功` | 有功功率、P、Active Power |
| `无功功率` | `无功` | 无功功率、Q、Reactive Power |
| `功率因数` | `功率因数`、`PF` | 功率因数、PF |
| `A相温度` | `A.*温` | A相温度、A温度 |
| `B相温度` | `B.*温` | B相温度、B温度 |
| `C相温度` | `C.*温` | C相温度、C温度 |

无法匹配的列将被丢弃并在返回值中报告。同名列（如多设备合并 CSV）按行取非空均值合并。

### 特殊值处理

| 值 | 含义 | 处理方式 |
|---|---|---|
| `-1.0` | 设备离线标志 | 各列统计，加权占比 > 50% 判定设备离线 |
| `2867.2` | 温度传感器故障标志 | 仅在温度列中出现，标记为传感器故障 |

### 典型数据示例

```csv
,宿舍楼3TM1-Uab,宿舍楼3TM1-Ubc,宿舍楼3TM1-Uca,宿舍楼3TM1-Ia,宿舍楼3TM1-Ib,宿舍楼3TM1-Ic,宿舍楼3TM1-有功功率,宿舍楼3TM1-无功功率,宿舍楼3TM1-功率因数,宿舍楼3TM1-A相温度,宿舍楼3TM1-B相温度,宿舍楼3TM1-C相温度,
0时,411.2,411.1,411.1,0.0,0.0,0.0,0.0,0.0,0.0,51.3,55.5,50.7,
1时,411.2,411.1,411.1,0.0,0.0,0.0,0.0,0.0,0.0,51.3,55.5,50.7,
...
```

## 报告输出

### CSV 异常报告

输出文件命名格式：`电气异常报告_YYYYMMDD_HHMMSS.csv`，GBK 编码。

**列结构**：

| 列名 | 说明 | 示例 |
|---|---|---|
| `来源文件` | 原始 CSV 文件名 | 综合楼1TM4变压器运行日报_2026_05_19.csv |
| `日期` | 从文件名提取的日期 | 2026-05-19 |
| `异常类型` | 精简分类名（同根归纳，最多 3 类） | 电压过低(A/B/C相); 电压越限 |
| `异常详情` | 结构化详情（按类型分组，竖线分隔） | 电压过低: B相266.1V, C相205.2V \| PT接线异常: A相偏差53.9% |
| `异常值` | 从行数据提取的关键异常值 | 功率因数=0.0 |
| `时间` | 原始时间列的值 | 0时 |
| `Uab` ~ `C相温度` | 该时间点的电气参数快照 | 411.2 |

### 日志输出格式

**逐行日志**（每文件处理完实时显示）：

```
异常 综合楼1TM4 → 24条 [未配置: 无功功率, 功率因数] [CT极性异常; 电压越限; 电流不平衡]
正常 宿舍楼3TM1 [未配置: 无功功率, 功率因数]
跳过 高压设备: II标一段高压设备运行日报_2026_05_19.csv
```

**检测汇总**（所有文件处理完毕后一次性输出）：

```
============================================================
  E-Detection 检测汇总                      完成 · 12.34s
============================================================
  43文件 → 正常1 · 异常34(717条) · 跳过8

  综合楼
    1TM4  CT极性异常(24次)  电压越限(24次)  电流不平衡(24次)
    1TM5  电压越限(16次)  电流不平衡(16次)

  数据中心
    1TM4  疑似PT接线异常(24次)  电压越限(24次)

  ▎传感器未配置一览
    无功功率: 35个文件 · 功率因数: 35个文件 · A相温度: 5个文件

  ⚠ 采集故障: 宿舍楼(3TM2 · 3TM3 · 3TM4 · 3TM5)
  ⚠ 设备离线: 数据中心(1TM1 · 1TM2 · 1TM7 · 1TM8)
  ⏭ 跳过8: 高压设备8个
============================================================
```

## 使用方式

### GUI 模式

```bash
python E-anomaly_detector-Portable_version-v20260520.py
```

操作步骤：

1. 点击"选择输入目录"，选择包含 CSV 日報文件的文件夹（支持子目录递归扫描）
2. （可选）点击"选择报告目录"，自定义 CSV 报告的保存位置（默认与输入目录相同）
3. 根据需要调整阈值参数（电压上下限、电流上限、不平衡度阈值等）
4. 勾选/取消可选规则开关（电流过载检测、电流不平衡检测、功率因数过低检测、输出详细异常）
5. 点击"开始检测并生成异常报告"
6. 检测过程中可在日志区实时查看每文件处理结果，可随时点击"停止检测"
7. 检测完成后日志区自动显示汇总报告

### 命令行模式

**基础批量检测**：

```bash
python run_check.py
```

使用 `电力监控系统数据文件` 目录下的 CSV 文件，按预设阈值运行检测，结果输出到控制台和 `check_result.txt`。

**对比分析模式**：

```bash
python run_check_v2.py
```

运行两套方案对比——方案 A（仅始终启用规则）vs 方案 B（全部规则），并输出差异分析。

### 调用核心函数

```python
from E_anomaly_detector_Portable_version import check_anomaly_in_file

anomalies, log_data, cleaned_df, extra_info = check_anomaly_in_file(
    file_path="path/to/变压器运行日报.csv",
    thresholds={...},
    enabled_rules={...}
)

# anomalies: DataFrame, 异常报告
# log_data: dict, 含 count/types/filename/pure_freeze/freeze_filtered_count
# cleaned_df: DataFrame, 清洗后的原始数据
# extra_info: dict, 含 is_offline/sensor_faults/sensor_missing
```

## 环境要求

- **Python**：3.10 及以上版本
- **依赖包**：
  - `pandas` — 数据处理核心
  - `numpy` — 数值计算
  - `customtkinter` — GUI 框架
  - `chardet`（可选）— 用于 CSV 文件编码自动检测，未安装时使用预设编码列表
- **操作系统**：Windows（GUI 模式使用 Tkinter）；命令行模式跨平台兼容

安装依赖：

```bash
pip install pandas numpy customtkinter chardet
```

## 开源许可

本项目以开源软件形式发布。

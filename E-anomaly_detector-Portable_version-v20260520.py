import os
import sys
import re
import json
import time
import threading
import queue

_SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

from datetime import datetime
from typing import Tuple, List, Dict, Any
import glob
from tkinter import filedialog, messagebox, TclError

try:
    import customtkinter as ctk
    from customtkinter.windows.widgets.ctk_entry import CTkEntry
except ModuleNotFoundError as _ctk_err:
    raise

# 新增规则插件导入
try:
    from core.rule_base import DetectionContext
    from core.rules import (
        VoltageRule, CurrentOverloadRule, CurrentUnbalanceRule,
        FreezeRule, PowerActiveRule, PowerFactorRule, TemperatureRule,
        SuddenChangeRule, CrossParamRule,
    )
except ModuleNotFoundError as _import_err:
    raise

# --- 配置与常量 ---
ctk.set_appearance_mode("light")
ctk.set_default_color_theme("blue")

GREEN_COLOR = "#107c10"
GREY_COLOR = "#888888"
BLUE_COLOR = "#0078d4"

TEXTS = {
    'title': "E-Detection",
    'header_main': "蓝蓝の天空",
    'header_sub': "电气参数异常检测系统v20260520",
    'instruction': "请选择CSV文件目录，系统将自动递归扫描子目录并检测异常。",
    'folder_default': "未选择输入目录",
    'report_default': "默认: 与输入目录相同",
    'btn_select_input': "选择输入目录",
    'btn_select_report': "选择报告目录",
    'rule_main_title': "查看/修改检测规则和阈值",
    'rule_current_overload': "电流过大检测",
    'rule_current_unbalance': "电流不平衡检测",
    'rule_power_factor': "功率因数过低检测",
    'rule_detail_output': "输出详细异常",
    'rule_sudden_change': "数据突变检测",
    'rule_cross_param': "跨参数关联分析",
    'btn_start': "开始检测并生成异常报告",
    'btn_stop': "停止检测",
    'btn_apply': "应用修改",
    'status_ready': "状态：就绪",
    'log_start': "系统已启动，支持自定义报告路径、可选规则及阈值配置。",
    'log_applied': "阈值配置已应用并生效。详细修改如下:",
    'err_no_folder': "错误：请指定有效的数据源目录。",
    'err_no_file': "警告：指定路径未发现 CSV 数据文件。",
    'btn_clear_log': "清空日志",
    'btn_export_log': "导出日志",
    'log_title': "操作日志",
}

INVALID_VALUES = [-1.0, 2867.2]

RULE_CONFIG_KEYS = (
    'current_overload', 'current_unbalance', 'power_factor', 'detail_output',
    'sudden_change', 'cross_param',
)

PHASE_MAP = {
    'Uab': 'A相电压',
    'Ubc': 'B相电压',
    'Uca': 'C相电压',
    'Ia': 'A相电流',
    'Ib': 'B相电流',
    'Ic': 'C相电流',
}

TARGET_SHORT_NAMES_REPORT = ['Uab', 'Ubc', 'Uca', 'Ia', 'Ib', 'Ic', '有功功率', '无功功率', '功率因数', 'A相温度',
                             'B相温度', 'C相温度']

# --- 辅助函数 ---

def extract_date_from_filename(file_name: str) -> str:
    """从文件名中提取日期字符串，用于标注异常数据的来源日期。

    支持 YYYYMMDD、YYYY-MM-DD、YYYY_MM_DD 等多种格式。
    优先取最后一个匹配项（文件名中可能存在多段日期）。

    Args:
        file_name: CSV 文件名（含扩展名）。

    Returns:
        str: 格式为 "YYYY-MM-DD" 的日期字符串，无法提取时返回 "日期未知"。
    """
    date_pattern = r'(\d{4}[_\-]?[01]\d[_\-]?[0-3]\d|\d{8})'
    matches = re.findall(date_pattern, file_name)
    if not matches:
        return "日期未知"
    last_match = matches[-1]
    clean_date_str = re.sub(r'[_/\-]', '', last_match)
    return f"{clean_date_str[:4]}-{clean_date_str[4:6]}-{clean_date_str[6:]}" if len(
        clean_date_str) == 8 else last_match

def _format_log_message(
        file_name: str,
        anomalies_count: int = 0,
        error_msg: str | None = None,
        anomaly_types: str | None = None,
        pure_freeze: bool = False,
        freeze_filtered_count: int = 0,
        sensor_missing: list | None = None,
) -> Tuple[str, str]:
    """根据检测结果生成统一格式的日志内容与标签。

    将跳过/失败文件与正常/异常文件分流为不同的 tag，便于 UI 的 log 方法
    按颜色渲染。异常日志附加 anomaly_types 帮助快速浏览问题种类。
    纯冻结文件（仅含采集层面问题）静默跳过，减少日志噪音。

    Args:
        file_name: 检测文件名。
        anomalies_count: 异常条数（滤除冻结行后），默认 0。
        error_msg: 错误/跳过原因，None 表示无错误。
        anomaly_types: 异常类型描述（分号分隔），仅 anomalies_count > 0 时使用。
        pure_freeze: 该文件是否仅含冻结/恒定/传感器缺失（全被滤除）。
        freeze_filtered_count: 被滤除的冻结行数。
        sensor_missing: 全零的传感器列名列表。

    Returns:
        Tuple[str, str]: (格式化后的日志消息, 标签字符串)，标签为 error/skip/alert/info/pure_freeze。
    """
    error_message_content = error_msg if error_msg is not None else ""

    # 传感器缺失后缀（追加到正常/异常日志尾部）
    missing_suffix = ""
    if sensor_missing and len(sensor_missing) > 0:
        missing_suffix = f" [未配置: {', '.join(sensor_missing)}]"

    if error_message_content and "跳过" not in error_message_content and "读取失败" not in error_message_content:
        return f"失败 {file_name}: {error_message_content}", "error"
    elif "跳过" in error_message_content or "读取失败" in error_message_content:
        return error_message_content.replace("跳过高压", "跳过"), "skip"
    elif pure_freeze:
        return f"正常 {file_name}{missing_suffix}", "info"
    elif anomalies_count > 0:
        types_str = anomaly_types if anomaly_types is not None else ""
        tail = f" [+{freeze_filtered_count}采集]" if freeze_filtered_count > 0 else ""
        return f"异常 {file_name} → {anomalies_count} 条{tail}{missing_suffix} [{types_str}]", "alert"
    else:
        return f"正常 {file_name}{missing_suffix}", "info"

def _merge_duplicate_named_columns(df: Any) -> Any:
    """合并重命名后同名的多列：按行取非空均值，避免只保留首列导致数据丢失。"""
    import numpy as np
    import pandas as pd

    if df.empty or not df.columns.duplicated().any():
        return df

    merged: dict[str, Any] = {}
    for name in pd.Index(df.columns).unique():
        block = df.loc[:, df.columns == name]
        if block.shape[1] == 1:
            merged[name] = block.iloc[:, 0]
        else:
            merged[name] = block.mean(axis=1, skipna=True)
    out = pd.DataFrame(merged, index=df.index)
    return out

def clean_and_rename_columns(df) -> Tuple[Any, List[str]]:
    """将原始 CSV 列名重命名为检测核心依赖的标准列名。

    不同厂家/设备的 CSV 导出列名差异较大（如 "Ua(V)"、"A相电压"、"Ua" 均映射到 'Uab'），
    统一映射后下游检测逻辑无需处理多份列名变体。仅保留成功重命名的列，
    丢弃无法识别的列并在返回值中报告。

    Args:
        df: 原始 DataFrame，第一列为时间列。

    Returns:
        Tuple[Any, List[str]]: (仅含已重命名列和时间列的 DataFrame, 未匹配列名列表)。
    """
    import re

    # 更加宽泛的匹配规则
    RENAME_MAP = {
        r'Uab|Ua\(|Ua电压|A相电压|^Ua$': 'Uab',  # Ua(V)、A相电压、Ua
        r'Ubc|Ub\(|Ub电压|B相电压|^Ub$': 'Ubc',  # Ub(V)、B相电压、Ub
        r'Uca|Uc\(|Uc电压|C相电压|^Uc$': 'Uca',  # Uc(V)、C相电压、Uc
        r'Ia|A.*电流|^Ia$': 'Ia',  # Ia(A)、A相电流
        r'Ib|B.*电流|^Ib$': 'Ib',  # Ib(A)、B相电流
        r'Ic|C.*电流|^Ic$': 'Ic',  # Ic(A)、C相电流
        r'有功': '有功功率',  # 有功、P、Active Power
        r'无功': '无功功率',  # 无功、Q、Reactive Power
        r'功率因数|PF': '功率因数',  # 功率因数、PF
        r'A.*温': 'A相温度',  # A相温度、A温度
        r'B.*温': 'B相温度',  # B相温度、B温度
        r'C.*温': 'C相温度',  # C相温度、C温度
    }

    if df.empty:
        return df, []

    # 时间列保持不动（通常是第一列）
    time_col = df.columns[0]
    new_cols = {time_col: time_col}
    
    current_cols = df.columns.tolist()
    unmapped: List[str] = []
    
    for col in current_cols:
        if col == time_col:
            continue
        matched = False
        for pattern, target in RENAME_MAP.items():
            if re.search(pattern, str(col), re.IGNORECASE):
                new_cols[col] = target
                matched = True
                break
        if not matched:
            unmapped.append(col)
    
    # 只保留成功重命名的列和时间列
    final_df = df.rename(columns=new_cols)
    keep_cols = [c for c in new_cols.values() if c in final_df.columns]
    return final_df[keep_cols], unmapped

def _load_and_clean_data(file_path: str) -> Tuple[Any, str, Dict[str, Dict[str, int]]]:
    """Load raw CSV data and clean it into a normalized electrical data frame.

    Args:
        file_path: The path to the CSV file.

    Returns:
        A tuple containing the cleaned DataFrame, an error message string,
        and a dict of invalid value counts per column (e.g. {"Ia": {"-1.0": 100}}).

    Notes:
        This function performs encoding sniffing, time column detection, trailing statistics row removal, column renaming, and numeric normalization.
    """
    import pandas as pd
    import numpy as np
    from collections import defaultdict

    file_name = os.path.basename(file_path)
    
    # --- 1. 编码嗅探（读前4KB检测，避免多次完整读文件）---
    encodings_base = ['gbk', 'utf-8-sig', 'gb18030', 'utf-8']
    df = None
    last_read_error: Exception | None = None

    detected_enc = 'gb18030'
    try:
        with open(file_path, 'rb') as f:
            raw_head = f.read(4096)
        if raw_head[:3] == b'\xef\xbb\xbf':
            detected_enc = 'utf-8-sig'
        else:
            try:
                import chardet
                result = chardet.detect(raw_head)
                if result and result.get('encoding'):
                    enc = result['encoding'].lower()
                    if enc in ('ascii', 'iso-8859-1', 'windows-1252'):
                        detected_enc = 'utf-8'
                    elif enc in ('gb2312', 'gbk'):
                        detected_enc = 'gb18030'
                    elif enc:
                        detected_enc = enc
            except ImportError:
                pass
    except OSError:
        pass

    encodings = [detected_enc] + [e for e in encodings_base if e != detected_enc]
    for enc in encodings:
        try:
            df = pd.read_csv(file_path, encoding=enc, low_memory=False)
            break
        except (UnicodeDecodeError, pd.errors.ParserError, OSError, ValueError) as exc:
            last_read_error = exc
            continue
    
    if df is None or df.empty:
        detail = f" ({type(last_read_error).__name__}: {last_read_error})" if last_read_error else ""
        return pd.DataFrame(), f"跳过空文件或读取失败: {file_name}{detail}", {}

    # 清理原始列名空格
    df.columns = df.columns.astype(str).str.strip()

    # --- 2. 智能寻找时间列 ---
    time_col = None
    for col in df.columns:
        if re.search(r'时间|时刻|Date|Time', col, re.IGNORECASE):
            time_col = col
            break
    if not time_col:
        time_col = df.columns[0]

    # --- 3. 截断逻辑优化 ---
    # 清理尾部：自动识别并删除报表底部的统计合计行。
    mask = df[time_col].astype(str).str.contains(r'最大值|最小值|平均值|合计|Total|Max|Min', na=False)
    bad_indices = df.index[mask].tolist()
    if bad_indices:
        df = df.iloc[:bad_indices[0]].copy()
    
    if df.empty:
        return pd.DataFrame(), f"跳过：截断后数据为空: {file_name}", {}

    # --- 4. 列重命名与清洗 ---
    df, unmapped_cols = clean_and_rename_columns(df)
    # 合并重名列（多源列映射到同一标准名时按行均值融合）
    df = _merge_duplicate_named_columns(df)

    # 检查是否有必要列
    has_params = any(c in df.columns for c in ['Uab', 'Ubc', 'Uca', 'Ia', 'Ib', 'Ic'])
    if not has_params:
        return pd.DataFrame(), f"跳过：未匹配到关键参数。当前列：{list(df.columns[:5])}...", {}

    # --- 5. 数据转换：确保传入的是 Series ---
    invalid_stats: Dict[str, Dict[str, int]] = {}
    for col in df.columns:
        if col == time_col:
            continue
        
        target_series = df[col]
        
        # 再次防御性检查：确保 target_series 是 Series 而不是 DataFrame
        if isinstance(target_series, pd.DataFrame):
            target_series = target_series.iloc[:, 0]

        # 执行转换
        df[col] = pd.to_numeric(target_series, errors='coerce')
        
        # 无效值统计（在置空前收集）
        col_stats: Dict[str, int] = {}
        for inv_val in INVALID_VALUES:
            inv_key = str(inv_val)
            count = int((df[col] == inv_val).sum())
            if count > 0:
                col_stats[inv_key] = count
        if col_stats:
            invalid_stats[col] = col_stats

        # 无效值置空
        df.loc[df[col].isin(INVALID_VALUES), col] = np.nan
        df[col] = df[col].astype('float32')

    return df, "", invalid_stats

def _detect_core_logic(df: Any, thresholds: Dict[str, float], enabled_rules: Dict[str, bool]) -> Tuple[Any, Any]:
    """Detect electrical anomalies from cleaned data using rule plugins.

    Args:
        df: The cleaned DataFrame containing electrical measurements.
        thresholds: Threshold values for various checks.
        enabled_rules: Enabled detection rules.

    Returns:
        A tuple containing anomaly description series and a boolean anomaly flag series.
    """
    import pandas as pd

    anomalies_flags = pd.Series([False] * len(df), index=df.index)
    anomaly_types = pd.Series([""] * len(df), index=df.index)

    # 创建检测上下文
    context = DetectionContext(df)

    # 规则列表
    rules = [
        VoltageRule(),
        CurrentOverloadRule(),
        CurrentUnbalanceRule(),
        FreezeRule(),
        PowerActiveRule(),
        PowerFactorRule(),
        TemperatureRule(),
        SuddenChangeRule(),
        CrossParamRule(),
    ]

    # 执行所有启用的规则
    for rule in rules:
        if rule.is_enabled(enabled_rules):
            anomaly_series, anomaly_mask = rule.detect(context, thresholds, enabled_rules)
            anomalies_flags |= anomaly_mask
            anomaly_types = anomaly_types + anomaly_series

    return anomaly_types, anomalies_flags

def _is_only_freeze_types(types_str: str) -> bool:
    """判断异常类型字符串是否仅由冻结/恒定/传感器缺失等低优先级标签组成。

    这些标签代表采集系统自身的问题（而非电网参数异常），
    单独出现时不作为有效告警。

    Args:
        types_str: 以分号分隔的异常类型字符串（已去重排序）。

    Returns:
        bool: True 表示该行异常标签全部属于采集层面问题。
    """
    if not types_str or not types_str.strip():
        return False
    types_set = set(t.strip() for t in types_str.split(';') if t.strip())
    if not types_set:
        return False
    # 采集层面关键词：数据冻结、传感器缺失、数据恒定
    freeze_keywords = ['数据冻结', '传感器缺失', '数据恒定', '恒定', '缺失']
    for t in types_set:
        if not any(kw in t for kw in freeze_keywords):
            return False
    return True


def _summarize_types(types_str: str) -> str:
    """归纳异常类型，将同根类型合并，用于逐行日志精简显示。

    规则:
      - A/B/C 相同类异常（如A相电压过低 + B相电压过低 + C相电压过低）→ "电压过低(A/B/C相)"
      - PT接线异常 / CT极性异常等独特类型 → 保留原名
      - 最多归纳为 3 类，超出显示 "+N类"
    """
    if not types_str or not types_str.strip():
        return ""

    parts = [t.strip() for t in types_str.split(';') if t.strip()]
    if not parts:
        return ""

    # 按根类型分组（去掉 A/B/C相 前缀和括号内容）
    import re as _re
    groups: dict[str, list[str]] = {}
    for p in parts:
        base = _re.sub(r'\([^)]*\)', '', p).strip()
        if ':' in base:
            base = base.split(':')[0]
        # 提取根类型：去掉 A相/B相/C相 前缀
        root = _re.sub(r'[ABC]相', '', base).strip()
        if root not in groups:
            groups[root] = []
        groups[root].append(p)

    result: list[str] = []
    for root, items in sorted(groups.items(), key=lambda x: -len(x[1])):
        if len(items) == 1:
            # 单个类型：使用摘取根类型后的简化名
            single = items[0]
            single_clean = _re.sub(r'\([^)]*\)', '', single).strip()
            if ':' in single_clean:
                single_clean = single_clean.split(':')[0]
            result.append(single_clean)
        else:
            # 多个同根类型：提取相别
            phases = []
            phase_map = {'A': 'A', 'B': 'B', 'C': 'C'}
            for item in items:
                m = _re.match(r'[ABC]相', item)
                if m:
                    phases.append(m.group()[0])
            phase_str = '/'.join(sorted(set(phases))) + '相' if phases else ''
            result.append(f"{root}({phase_str})" if phase_str else root)

    if len(result) > 3:
        result = result[:3] + [f"+{len(result) - 3}类"]
    return "; ".join(result)


def _compact_type(types_str: str) -> str:
    """精简异常类型用于CSV报告的类型列：保留第一个代表性数值，每行最多3类。

    例: "A相电压过低(265.8V); A相电压过低(267.2V); B相电压过低(266.1V)" → "电压过低(A/B/C相)"
    """
    return _summarize_types(types_str)


def _extract_anomaly_value(types_str: str, df_row) -> str:
    """从异常详情列提取核心异常值，生成简短的 '参数=数值' 摘要。

    优先从 df_row 的电气参数列提取实际值；兜底从 types_str 中提取括号内数值。
    """
    import re as _re
    pairs: list[str] = []

    # 从 DataFrame 行中提取非空的电气参数
    for col in TARGET_SHORT_NAMES_REPORT:
        if col in df_row.index:
            v = df_row[col]
            try:
                fv = float(v)
                if not (fv == fv):  # NaN check
                    continue
                # 判断是否有对应的异常类型
                col_phase = PHASE_MAP.get(col, col)
                if col_phase in types_str or col in types_str:
                    pairs.append(f"{col}={fv:.1f}")
            except (ValueError, TypeError):
                continue

    # 如果从行数据中提取不到，从类型字符串中提取括号数值
    if not pairs:
        matches = _re.findall(r'\(([^)]+)\)', types_str)
        if matches:
            return ', '.join(matches[:6])

    return ', '.join(pairs[:8]) if pairs else ''


def _format_detail_structured(types_str: str) -> str:
    """将原始异常详情字符串结构化：按类型分组，用 | 分隔不同类型。

    例: "A相电压过低(265.8V); B相电压过低(267.2V); CT极性异常"
      → "电压过低: A相265.8V, B相267.2V | CT极性异常"
    """
    if not types_str or not types_str.strip():
        return ""

    parts = [t.strip() for t in types_str.split(';') if t.strip()]
    if not parts:
        return ""

    import re as _re

    # 按根类型分组
    groups: dict[str, list[str]] = {}
    for p in parts:
        base = _re.sub(r'\([^)]*\)', '', p).strip()
        if ':' in base:
            base = base.split(':')[0]
        root = _re.sub(r'[ABC]相', '', base).strip()
        if root not in groups:
            groups[root] = []
        groups[root].append(p)

    result: list[str] = []
    for root, items in groups.items():
        # 提取每个 item 的相别和数值
        sub: list[str] = []
        for item in items:
            # 提取相别
            m = _re.match(r'([ABC]相)', item)
            phase = m.group(1) if m else ''
            # 提取括号内数值
            val_m = _re.search(r'\(([^)]+)\)', item)
            val = val_m.group(1) if val_m else ''
            if phase and val:
                sub.append(f"{phase}{val}")
            elif val:
                sub.append(val)
            else:
                # 无括号数值：保留原类型名（去除坐标前缀）
                clean = _re.sub(r'\([^)]*\)', '', item).strip()
                if ':' in clean:
                    clean = clean.split(':')[0]
                sub.append(clean)

        if len(sub) == 1 and not _re.search(r'[ABC]相', sub[0]):
            # 单个无相别类型（如CT极性异常）
            result.append(sub[0])
        else:
            result.append(f"{root}: {', '.join(sub)}")

    return ' | '.join(result)


def _format_anomaly_report(df: Any, anomalies_flags: Any, anomaly_types: Any, file_name: str) -> Tuple[Any, str | dict]:
    """将检测结果格式化为最终异常报告 DataFrame 和日志摘要。

    去重并排序异常类型字符串，按 TARGET_SHORT_NAMES_REPORT 固定列序输出，
    同时过滤仅含冻结/恒定/传感器缺失的行（不写入 CSV），
    并生成用于汇总统计的 log_msg_data 字典。

    Args:
        df: 清洗后的原始 DataFrame。
        anomalies_flags: 布尔 Series，标记异常行。
        anomaly_types: 异常类型描述 Series（分号分隔，可能含重复前缀）。
        file_name: 来源文件名。

    Returns:
        Tuple[Any, str | dict]: (排序好的异常 DataFrame, 日志摘要 dict)。
        日志 dict 包含 count/types/filename/pure_freeze/freeze_filtered_count 五个 key。
    """
    import pandas as pd

    anomalies = df[anomalies_flags].copy()
    if anomalies.empty:
        log_msg_data = {
            'count': 0,
            'types': '',
            'filename': file_name,
            'pure_freeze': False,
            'freeze_filtered_count': 0,
        }
        return anomalies, log_msg_data

    def clean_anomaly_types(x: str) -> str:
        """提取异常分类名（去掉括号内的具体数值），去重排序。

        如 "A相电压过低(265.8V); CT极性异常" → "A相电压过低; CT极性异常"
        """
        parts = [t.strip() for t in x.split(';') if t.strip()]
        cleaned = []
        for p in parts:
            # 去掉括号及内部内容（数值/百分比/偏差等详情）
            import re as _re
            base = _re.sub(r'\([^)]*\)', '', p).strip()
            # 去掉冒号后的详情值（如 "电压不平衡:266.1:偏差0.349" → "电压不平衡"）
            if ':' in base:
                base = base.split(':')[0]
            if base and base not in cleaned:
                cleaned.append(base)
        return "; ".join(sorted(cleaned))

    anomalies['异常类型'] = anomaly_types[anomalies_flags].apply(_compact_type)

    # 异常详情列：结构化格式（按类型分组，| 分隔）
    anomalies['异常详情'] = anomaly_types[anomalies_flags].apply(
        lambda x: _format_detail_structured(x) if isinstance(x, str) else ""
    )

    # 异常值列：从行数据和详情中提取关键数值
    anomalies['异常值'] = anomalies.apply(
        lambda row: _extract_anomaly_value(row.get('异常详情', ''), row), axis=1
    )

    # --- 过滤仅冻结行：仅含数据冻结/传感器缺失/数据恒定的行不写入 CSV ---
    total_anomaly_count = len(anomalies)
    freeze_only_mask = anomalies['异常类型'].apply(_is_only_freeze_types)
    freeze_filtered_count = int(freeze_only_mask.sum())
    anomalies = anomalies[~freeze_only_mask].copy()
    pure_freeze = (total_anomaly_count > 0 and len(anomalies) == 0)

    if anomalies.empty:
        log_msg_data = {
            'count': 0,
            'types': '',
            'filename': file_name,
            'pure_freeze': pure_freeze,
            'freeze_filtered_count': freeze_filtered_count,
        }
        return anomalies, log_msg_data

    time_col = df.columns[0]
    file_date = extract_date_from_filename(file_name)
    anomalies.rename(columns={time_col: '时间'}, inplace=True)
    anomalies.insert(0, '日期', file_date)
    anomalies.insert(0, '来源文件', file_name)

    # 使用模块级 TARGET_SHORT_NAMES_REPORT 常量
    fixed_cols = ['来源文件', '日期', '异常类型', '异常详情', '异常值', '时间']
    data_cols = [c for c in anomalies.columns if c not in fixed_cols]
    rel_cols = [c for c in data_cols if anomalies[c].notna().any()]
    ordered_data_cols = [col for col in TARGET_SHORT_NAMES_REPORT if col in rel_cols]
    order = fixed_cols + ordered_data_cols

    types = _summarize_types(';'.join(anomalies['异常详情'].tolist()))
    log_msg_data = {
        'count': len(anomalies),
        'types': types,
        'filename': file_name,
        'pure_freeze': False,
        'freeze_filtered_count': freeze_filtered_count,
    }

    return anomalies[order], log_msg_data

def check_anomaly_in_file(file_path: str, thresholds: Dict[str, float], enabled_rules: Dict[str, bool]) -> Tuple[Any, str, Any, Dict]:
    """Evaluate a single CSV file for electrical anomalies.

    Args:
        file_path: Path to the CSV file to evaluate.
        thresholds: Threshold dictionary for voltage, current, power, freeze, and temperature.
        enabled_rules: Rule switches for optional detection logic and detail output.

    Returns:
        A tuple of (anomaly DataFrame, status message, cleaned DataFrame or None, extra_info dict).
        The cleaned DataFrame is returned for post-processing (e.g., frozen acquisition detection).
        extra_info contains 'is_offline' (bool) and 'sensor_faults' (list of column names).
    """
    import pandas as pd
    
    file_name = os.path.basename(file_path)

    # 快速过滤逻辑：通过文件名关键词避免高压数据导致的阈值冲突。
    if "高压" in file_name:
        return pd.DataFrame(), f"跳过高压设备: {file_name}", None, {}
    # ---------------------------------------
    
    # 1. 尝试加载
    df, error_msg, invalid_stats = _load_and_clean_data(file_path)
    if error_msg:
        return pd.DataFrame(), error_msg, None, {}

    # 2. 检查列（只要有电压或电流其一即可）
    has_v = any(c in df.columns for c in ['Uab', 'Ubc', 'Uca'])
    has_i = any(c in df.columns for c in ['Ia', 'Ib', 'Ic'])
    
    if not (has_v or has_i):
        return pd.DataFrame(), f"跳过：未识别到有效参数列。当前列名：{list(df.columns)}", None, {}

    # 3. 设备离线检测：基于 invalid_stats 判断（-1.0 占比 >50%）
    total_rows = len(df)
    is_offline = False
    if invalid_stats and total_rows > 0:
        key_cols = [c for c in ['Uab', 'Ubc', 'Uca', 'Ia', 'Ib', 'Ic'] if c in df.columns]
        if key_cols:
            offline_ratio = 0
            for col in key_cols:
                col_offline_count = 0
                if col in invalid_stats and '-1.0' in invalid_stats[col]:
                    col_offline_count = invalid_stats[col]['-1.0']
                offline_ratio += col_offline_count / (total_rows * len(key_cols))
            is_offline = offline_ratio > 0.5

    # 4. 传感器故障检测（温度 2867.2°C）
    sensor_faults: List[str] = []
    if invalid_stats:
        for col in ['A相温度', 'B相温度', 'C相温度']:
            if col in invalid_stats and '2867.2' in invalid_stats[col]:
                sensor_faults.append(col)

    # 4.5 传感器缺失检测：全零列（仅辅助列和温度列，核心电气列零值可能是备用状态）
    sensor_missing: List[str] = []
    for col in ['无功功率', '功率因数', 'A相温度', 'B相温度', 'C相温度']:
        if col in df.columns:
            s = df[col]
            if (s.fillna(0) == 0).all():
                sensor_missing.append(col)

    # 5. 执行正常检测
    anomaly_types, anomalies_flags = _detect_core_logic(df, thresholds, enabled_rules)
    anomalies, log_data = _format_anomaly_report(df, anomalies_flags, anomaly_types, file_name)

    # 6. 生成设备离线/传感器故障异常记录
    extra_anomaly_rows = []
    file_date = extract_date_from_filename(file_name)

    if is_offline:
        offline_cols = [c for c in ['Uab', 'Ubc', 'Uca', 'Ia', 'Ib', 'Ic']
                        if c in invalid_stats and '-1.0' in invalid_stats.get(c, {})]
        detail_off = ','.join(offline_cols) if offline_cols else '所有电气参数'
        row = {'来源文件': file_name, '日期': file_date, '异常类型': f'设备离线({detail_off})',
               '时间': file_date, 'Uab': None, 'Ubc': None, 'Uca': None,
               'Ia': None, 'Ib': None, 'Ic': None}
        extra_anomaly_rows.append(row)

    for sensor_col in sensor_faults:
        count = invalid_stats.get(sensor_col, {}).get('2867.2', 0)
        row = {'来源文件': file_name, '日期': file_date,
               '异常类型': f'传感器故障({sensor_col}:2867.2°C,共{count}点)',
               '时间': file_date, 'A相温度': None, 'B相温度': None, 'C相温度': None}
        extra_anomaly_rows.append(row)

    # 若检测到设备离线，用离线记录替代常规异常报告（避免大量电压过低噪声）
    if is_offline:
        anomalies = pd.DataFrame(extra_anomaly_rows)
        log_data = {'count': len(anomalies), 'types': '设备离线', 'filename': file_name,
                    'pure_freeze': False, 'freeze_filtered_count': 0}
    elif extra_anomaly_rows:
        extra_df = pd.DataFrame(extra_anomaly_rows)
        for col in extra_df.columns:
            if col not in anomalies.columns:
                anomalies[col] = None
        for col in anomalies.columns:
            if col not in extra_df.columns:
                extra_df[col] = None
        anomalies = pd.concat([anomalies, extra_df[anomalies.columns]], ignore_index=True, sort=False)
        if isinstance(log_data, dict):
            existing_types = log_data.get('types', '')
            sensor_types = '; '.join([f'传感器故障({c})' for c in sensor_faults])
            combined_types = (existing_types + '; ' + sensor_types).strip('; ')
            log_data['types'] = combined_types
            log_data['count'] = log_data.get('count', 0) + len(extra_anomaly_rows)

    extra_info = {
        'is_offline': is_offline,
        'sensor_faults': sensor_faults,
        'sensor_missing': sensor_missing,
    }

    return anomalies, log_data, df, extra_info


def _extract_building_and_transformer(fp: str, folder_path: str) -> Tuple[str, str]:
    """从文件路径中提取建筑物名和变压器编号。

    Args:
        fp: 文件的完整路径。
        folder_path: 检测根目录（用于计算相对路径）。

    Returns:
        (建筑物名, 变压器编号) 元组。若无法提取，返回 ("未知", "未知")。
    """
    import re

    try:
        rel = os.path.relpath(fp, folder_path)
    except ValueError:
        # 路径在不同驱动器上，直接取文件名所在目录
        rel = fp

    parts = rel.replace('\\', '/').split('/')
    # 建筑物名 = 相对于 folder_path 的第一级目录
    building = parts[0] if len(parts) > 1 else "根目录"
    file_name = os.path.basename(fp)

    # 从文件名中提取变压器编号（如 1TM1, 3TM4, 1TMsc1, 1TMc2, 充电桩 等）
    tm_match = re.search(r'(\d+TM[a-zA-Z]*\d*|充电桩)', file_name)
    transformer = tm_match.group(0) if tm_match else file_name.rsplit('.', 1)[0]

    return building, transformer


def _check_frozen_acquisition(df) -> bool:
    """判断电压和电流数据是否全程不变（疑似采集系统故障）。

    仅检查 df 中实际存在的电压列（Uab/Ubc/Uca）和电流列（Ia/Ib/Ic）。
    若所有存在的电压+电流列的标准差均为 0，返回 True。
    使用 DataFrame.std() 矢量化计算，避免逐列 for 循环。

    Args:
        df: 清洗后的 DataFrame，列名已规范为 Uab/Ubc/Uca/Ia/Ib/Ic。

    Returns:
        bool: True 表示所有电压+电流列的标准差均为 0，疑似采集系统冻结。
    """
    import numpy as np

    target_cols = [c for c in ['Uab', 'Ubc', 'Uca', 'Ia', 'Ib', 'Ic'] if c in df.columns]

    if not target_cols:
        return False

    subset = df[target_cols].dropna(how='all')
    if len(subset) < 2:
        return False

    return bool((subset.std(skipna=True).fillna(0) <= 0).all())


def _extract_transformer_issues(anomalies_df, df) -> Dict[str, Dict[str, Any]]:
    """从异常 DataFrame 中提取每个变压器的重点问题摘要（含数量和时间范围）。

    优先从 异常详情 列提取 detail 信息；若不存在则回退到 异常类型 列 + df 提取。
    """
    import pandas as pd
    import numpy as np

    issues: Dict[str, Dict[str, Any]] = {}

    if anomalies_df is None or anomalies_df.empty:
        return issues

    time_col = '时间' if '时间' in anomalies_df.columns else None
    detail_col = '异常详情' if '异常详情' in anomalies_df.columns else None
    type_col = '异常类型' if '异常类型' in anomalies_df.columns else None

    if type_col is None:
        return issues

    def _time_range(mask):
        if time_col and mask.any():
            idx = anomalies_df.loc[mask].index
            first = str(anomalies_df.loc[idx[0], time_col])
            if len(idx) >= 2:
                last = str(anomalies_df.loc[idx[-1], time_col])
                return first if first == last else f"{first}~{last}"
            return first
        return ""

    def _add_issue(key, mask, detail):
        if mask.any():
            issues[key] = {'count': int(mask.sum()), 'detail': detail, 'time_range': _time_range(mask)}

    # 搜索列：优先用 异常详情，其次 异常类型
    search_col = detail_col if detail_col in anomalies_df.columns else type_col

    # --- 电压越限 ---
    v_mask = anomalies_df[search_col].str.contains('电压', na=False) & \
             ~anomalies_df[search_col].str.contains('不平衡|PT接线', na=False)
    if v_mask.any():
        v_parts = []
        for col in ['Uab', 'Ubc', 'Uca']:
            if col in df.columns:
                vals = df.loc[v_mask[v_mask].index.intersection(df.index), col] if not v_mask.empty else pd.Series(dtype=float)
                if len(vals) > 0:
                    cmin, cmax = vals.min(), vals.max()
                    if not pd.isna(cmin):
                        v_parts.append(f"{cmin:.0f}~{cmax:.0f}V")
        _add_issue('电压越限', v_mask, ', '.join(v_parts) if v_parts else '')

    # --- PT接线异常 ---
    pt_mask = anomalies_df[search_col].str.contains('PT接线异常', na=False)
    _add_issue('疑似PT接线异常', pt_mask, '')

    # --- CT极性异常 ---
    ct_mask = anomalies_df[search_col].str.contains('CT极性异常', na=False)
    _add_issue('CT极性异常', ct_mask, '')

    # --- 电压不平衡 ---
    imb_mask = anomalies_df[search_col].str.contains('电压不平衡', na=False) & ~pt_mask
    _add_issue('电压不平衡', imb_mask, '')

    # --- 电流过载 ---
    ov_mask = anomalies_df[search_col].str.contains('电流过大|电流过载', na=False)
    i_detail = ''
    if ov_mask.any():
        i_parts = []
        for col in ['Ia', 'Ib', 'Ic']:
            if col in df.columns:
                idx_inter = ov_mask[ov_mask].index.intersection(df.index)
                vals = df.loc[idx_inter, col] if len(idx_inter) > 0 else pd.Series(dtype=float)
                if len(vals) > 0:
                    cmax = vals.max()
                    if not pd.isna(cmax):
                        i_parts.append(f"{cmax:.0f}A")
        i_detail = ', '.join(i_parts)
    _add_issue('电流过载', ov_mask, i_detail)

    # --- 电流不平衡 ---
    ub_mask = anomalies_df[search_col].str.contains('电流不平衡', na=False)
    _add_issue('电流不平衡', ub_mask, '')

    # --- 温度异常 ---
    t_mask = anomalies_df[search_col].str.contains('温度', na=False) & \
             ~anomalies_df[search_col].str.contains('传感器故障|传感器缺失', na=False)
    t_detail = ''
    if t_mask.any():
        t_parts = []
        for col in ['A相温度', 'B相温度', 'C相温度']:
            if col in df.columns:
                idx_inter = t_mask[t_mask].index.intersection(df.index)
                vals = df.loc[idx_inter, col] if len(idx_inter) > 0 else pd.Series(dtype=float)
                if len(vals) > 0:
                    cmin = vals.replace(0, np.nan).min(skipna=True)
                    cmax = vals.max()
                    if not pd.isna(cmin):
                        t_parts.append(f"{cmin:.0f}~{cmax:.0f}°C")
        t_detail = ', '.join(t_parts)
    _add_issue('温度异常', t_mask, t_detail)

    # --- 有功功率异常 ---
    p_mask = anomalies_df[search_col].str.contains('有功功率异常', na=False)
    p_detail = ''
    if p_mask.any() and '有功功率' in df.columns:
        idx_inter = p_mask[p_mask].index.intersection(df.index)
        if len(idx_inter) > 0:
            pmin = df.loc[idx_inter, '有功功率'].min()
            p_detail = f"最低{pmin:.1f}kW" if not pd.isna(pmin) else ''
    _add_issue('有功功率异常', p_mask, p_detail)

    # --- 功率因数过低 ---
    pf_mask = anomalies_df[search_col].str.contains('功率因数过低', na=False)
    pf_detail = ''
    if pf_mask.any() and '功率因数' in df.columns:
        idx_inter = pf_mask[pf_mask].index.intersection(df.index)
        if len(idx_inter) > 0:
            pfmin = df.loc[idx_inter, '功率因数'].min()
            pf_detail = f"最低{pfmin:.2f}" if not pd.isna(pfmin) else ''
    _add_issue('功率因数过低', pf_mask, pf_detail)

    # --- 数据突变 ---
    sc_mask = anomalies_df[search_col].str.contains('突变', na=False)
    _add_issue('数据突变', sc_mask, '')

    # --- 关联异常 ---
    cp_mask = anomalies_df['异常类型'].str.contains('关联异常|同步升高|异常偏离', na=False)
    _add_issue('关联异常', cp_mask, '')

    # --- 数据冻结 ---
    fz_mask = anomalies_df['异常类型'].str.contains('数据冻结', na=False)
    _add_issue('数据冻结', fz_mask, '')

    return issues


# --- GUI 界面 (GUI Application Class) ---

class ElectricalAnomalyDetectorApp(ctk.CTk):
    """电气参数异常检测系统 GUI 主窗口。

    基于 CustomTkinter，提供目录选择、阈值配置、规则开关、实时日志、
    进度条、暂停/停止检测等功能。检测输出为 CSV 异常报告。
    """

    def __init__(self):
        """初始化主窗口：几何尺寸、默认阈值、规则状态、UI 布局。"""
        super().__init__()

        screen_width = self.winfo_screenwidth()
        screen_height = self.winfo_screenheight()

        new_width = int(screen_width * 0.50)
        new_height = int(screen_height * 0.85)

        center_x = int((screen_width - new_width) / 2)
        center_y = int((screen_height - new_height) / 2)

        self.title(TEXTS['title'])
        self.geometry(f"{new_width}x{new_height}+{center_x}+{center_y}")

        self.minsize(780, 580)
        self.configure(fg_color="#f5f5f5")

        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(0, weight=1)

        self.folder_path: str = ""
        self.report_path: str = ""
        self.initial_report_path: str = ""
        self.log_queue = queue.Queue()
        self._stop_detection = threading.Event()
        self.report_path_is_custom: bool = False

        self.V_MIN_THRESHOLD = 353.0
        self.V_MAX_THRESHOLD = 430.0
        self.I_MAX_THRESHOLD = 1000.0
        self.I_UNBALANCE_MAX_THRESHOLD = 0.15
        self.P_ACTIVE_MIN_THRESHOLD = 0.0
        self.PF_MIN_THRESHOLD = 0.90
        self.T_MIN_THRESHOLD = 0.0
        self.T_MAX_THRESHOLD = 70.0
        self.I_MIN_ACTIVE_THRESHOLD = 1.0
        self.FREEZE_COUNT_THRESHOLD = 3
        self.FREEZE_STD_THRESHOLD = 0.01
        self.V_IMBALANCE_THRESHOLD = 0.02

        self.enabled_rules = {
            'current_overload': True,
            'current_unbalance': False,
            'power_factor': False,
            'detail_output': False,
            'sudden_change': True,
            'cross_param': True,
        }

        self.DEFAULT_THRESHOLDS = {
            'V_MIN_THRESHOLD': 353.0, 'V_MAX_THRESHOLD': 430.0,
            'I_MAX_THRESHOLD': 1000.0, 'I_UNBALANCE_MAX_THRESHOLD': 0.15,
            'P_ACTIVE_MIN_THRESHOLD': 0.0, 'PF_MIN_THRESHOLD': 0.90,
            'T_MIN_THRESHOLD': 0.0, 'T_MAX_THRESHOLD': 70.0,
            'I_MIN_ACTIVE_THRESHOLD': 1.0,
            'FREEZE_COUNT_THRESHOLD': 3,
            'FREEZE_STD_THRESHOLD': 0.01,
            'V_IMBALANCE_THRESHOLD': 0.02,
            'current_overload': True, 'current_unbalance': False,
            'power_factor': False, 'detail_output': False,
            'sudden_change': True, 'cross_param': True,
        }

        self.config_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'config.json')
        self.load_config()

        self.report_label: ctk.CTkLabel | None = None
        self.folder_label: ctk.CTkLabel | None = None
        self.progress: ctk.CTkProgressBar | None = None
        self.status: ctk.CTkLabel | None = None
        self.log_text: ctk.CTkTextbox | None = None
        self.rules_toggle_btn: ctk.CTkButton | None = None
        self.rules_display_card: ctk.CTkFrame | None = None
        self.rules_display_content_frame: ctk.CTkFrame | None = None
        self.start_btn: ctk.CTkButton | None = None
        self.log_content_frame: ctk.CTkFrame | None = None
        self.threshold_entries: Dict[str, CTkEntry] = {}
        self.threshold_vars: Dict[str, ctk.StringVar] = {}

        self.rule_vars: Dict[str, ctk.BooleanVar] = {
            'current_overload': ctk.BooleanVar(value=True),
            'current_unbalance': ctk.BooleanVar(value=False),
            'power_factor': ctk.BooleanVar(value=False),
            'detail_output': ctk.BooleanVar(value=False),
            'sudden_change': ctk.BooleanVar(value=True),
            'cross_param': ctk.BooleanVar(value=True),
        }
        self.rule_checkboxes: Dict[str, ctk.CTkCheckBox] = {}

        # 用于存储标签引用，以便更新颜色
        self.threshold_labels: Dict[str, ctk.CTkLabel] = {}
        self.threshold_label_links: Dict[str, str] = {}

        self.last_report_file: str | None = None
        self.start_time: float | None = None
        self.end_time: float | None = None

        self.setup_ui()
        self.after(100, self._process_log_queue)

    def _format_value(self, key: str, value: float) -> str:
        """格式化阈值为显示字符串，功率因数/不平衡度/冻结波动保留两位小数，其余取整。"""
        if key in ['PF_MIN_THRESHOLD', 'I_UNBALANCE_MAX_THRESHOLD', 'FREEZE_STD_THRESHOLD',
                   'V_IMBALANCE_THRESHOLD']:
            return f"{value:.2f}"
        else:
            return f"{value:.0f}"

    def _build_thresholds_dict(self) -> Dict[str, float]:
        """将实例属性组装为规则检测所需的统一阈值字典。

        每个实例属性对应一个检测参数，由用户在 UI 中调整。该方法集中管理
        属性名到规则参数名的映射，避免 start_detection 中逐字内联拼装字典。

        Returns:
            Dict[str, float]: 键为大写阈值常量名、值为 numeric 阈值的字典。
        """
        return {
            'V_MIN_THRESHOLD': self.V_MIN_THRESHOLD,
            'V_MAX_THRESHOLD': self.V_MAX_THRESHOLD,
            'I_MAX_THRESHOLD': self.I_MAX_THRESHOLD,
            'I_UNBALANCE_MAX_THRESHOLD': self.I_UNBALANCE_MAX_THRESHOLD,
            'P_ACTIVE_MIN_THRESHOLD': self.P_ACTIVE_MIN_THRESHOLD,
            'PF_MIN_THRESHOLD': self.PF_MIN_THRESHOLD,
            'T_MIN_THRESHOLD': self.T_MIN_THRESHOLD,
            'T_MAX_THRESHOLD': self.T_MAX_THRESHOLD,
            'I_MIN_ACTIVE_THRESHOLD': self.I_MIN_ACTIVE_THRESHOLD,
            'FREEZE_COUNT_THRESHOLD': self.FREEZE_COUNT_THRESHOLD,
            'FREEZE_STD_THRESHOLD': self.FREEZE_STD_THRESHOLD,
            'V_IMBALANCE_THRESHOLD': self.V_IMBALANCE_THRESHOLD,
        }

    def _format_duration_text(self, duration: float, total_files: int) -> str:
        """格式化检测耗时字符串，含总时长和平均单文件耗时。"""
        avg = duration / total_files if total_files else 0.0
        return f"总耗时: {duration:.2f}s (平均 {avg:.2f}s/文件)"

    def on_closing(self) -> None:
        """窗口销毁前清理所有 StringVar 的 trace，避免退出时的 TclError。"""
        for var in self.threshold_vars.values():
            try:
                trace_id = None
                try:
                    # 优先使用 Tcl 9 的新 API
                    trace_id = var.trace_info()
                except AttributeError:
                    pass
                if trace_id is None:
                    try:
                        trace_id = var.trace_vinfo()
                    except AttributeError:
                        pass
                if trace_id:
                    for mode, func_name in trace_id:
                        var.trace_remove(mode, func_name)
            except TclError:
                pass

        self.destroy()

    def setup_ui(self) -> None:
        """构建并布局 GUI 的所有子控件（标题栏、目录卡片、规则面板、日志区、进度条等）。"""
        main = ctk.CTkFrame(self, corner_radius=0, fg_color="transparent")
        main.grid(row=0, column=0, sticky="nsew", padx=24, pady=24)
        main.grid_columnconfigure(0, weight=1)

        self._build_main_page(main)

    def _build_main_page(self, main) -> None:
        row_idx = 0

        title_frame = ctk.CTkFrame(main, height=80, fg_color="#0078d4", corner_radius=12)
        title_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 20));
        row_idx += 1
        title_frame.grid_propagate(False)

        ctk.CTkLabel(
            title_frame, text=TEXTS['header_main'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=24, weight="bold"),
            text_color="white"
        ).pack(side="left", padx=28, pady=22)
        ctk.CTkLabel(
            title_frame, text=TEXTS['header_sub'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=11),
            text_color="#c4e1ff"
        ).pack(side="right", padx=28, pady=22)

        ctk.CTkLabel(
            main, text=TEXTS['instruction'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12),
            text_color="#555555"
        ).grid(row=row_idx, column=0, sticky="w", padx=2, pady=(0, 16));
        row_idx += 1

        self._create_directory_cards(main, start_row=row_idx);
        row_idx += 2

        optional_controls_frame = ctk.CTkFrame(main, fg_color="transparent")
        optional_controls_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 10));
        row_idx += 1
        optional_controls_frame.grid_columnconfigure(0, weight=1)

        controls_inner_frame = ctk.CTkFrame(optional_controls_frame, fg_color="transparent")
        controls_inner_frame.grid(row=0, column=0, sticky="e")

        rules_list = [
            (TEXTS['rule_current_overload'], "current_overload"),
            (TEXTS['rule_current_unbalance'], "current_unbalance"),
            (TEXTS['rule_power_factor'], "power_factor"),
            (TEXTS['rule_detail_output'], "detail_output"),
            (TEXTS['rule_sudden_change'], "sudden_change"),
            (TEXTS['rule_cross_param'], "cross_param"),
        ]

        for label, key in rules_list:
            var = self.rule_vars[key]
            text_color = GREEN_COLOR if var.get() else "#333333"

            check = ctk.CTkCheckBox(
                controls_inner_frame, text=label,
                font=ctk.CTkFont(family="Microsoft YaHei UI", size=12),
                variable=var,
                command=lambda k=key, v=var: self._update_rule_state_and_display(k, v),
                text_color=text_color
            )
            check.pack(side="left", padx=10, pady=0)
            self.rule_checkboxes[key] = check

        rules_header_frame = ctk.CTkFrame(main, fg_color="transparent")
        rules_header_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 4));
        row_idx += 1
        rules_header_frame.grid_columnconfigure(0, weight=1)
        self._create_rules_header(rules_header_frame)

        self.rules_display_card = ctk.CTkFrame(main, fg_color="white", corner_radius=10, border_width=1,
                                               border_color="#e1e1e1")
        self.rules_display_card.grid(row=row_idx, column=0, sticky="ew", pady=(0, 14));
        row_idx += 1
        self.rules_display_card.grid_columnconfigure(0, weight=1)

        self.rules_display_content_frame = ctk.CTkFrame(self.rules_display_card, fg_color="#fafafa")
        self.rules_display_content_frame.pack(fill="x", padx=12, pady=12)

        # 首次创建阈值输入框
        self._create_rules_widgets()
        self._toggle_rules_panel(False)

        self.status = ctk.CTkLabel(
            main, text=TEXTS['status_ready'], anchor="w",
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=11),
            fg_color="#f5f5f5", text_color="#333333",
            corner_radius=8, height=36, padx=2
        )
        self.status.grid(row=row_idx, column=0, sticky="ew", pady=(0, 10));
        row_idx += 1

        self.progress = ctk.CTkProgressBar(main, height=6, progress_color="#0078d4")
        self.progress.grid(row=row_idx, column=0, sticky="ew", pady=(0, 10));
        row_idx += 1
        self.progress.set(0)

        self.start_btn = ctk.CTkButton(
            main, text=TEXTS['btn_start'], height=52,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=16, weight="bold"),
            fg_color="#0078d4", hover_color="#106ebe", corner_radius=10,
            command=self.start_detection_thread
        )
        self.start_btn.grid(row=row_idx, column=0, sticky="ew", pady=(0, 16));
        row_idx += 1

        log_header_frame = ctk.CTkFrame(main, fg_color="transparent")
        log_header_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 4));
        row_idx += 1
        log_header_frame.grid_columnconfigure(0, weight=1)
        log_header_frame.grid_columnconfigure(1, weight=0)
        self._create_log_header(log_header_frame)

        self.log_content_frame = ctk.CTkFrame(main, fg_color="white", corner_radius=10, border_width=1,
                                              border_color="#e1e1e1")
        self.log_content_frame.grid(row=row_idx, column=0, sticky="nsew", pady=(0, 0));
        row_idx += 1
        main.grid_rowconfigure(row_idx - 1, weight=1)

        self.log_text = ctk.CTkTextbox(
            self.log_content_frame,
            font=ctk.CTkFont(family="Consolas", size=10),
            wrap="word", corner_radius=8
        )
        self.log_text.pack(fill="both", expand=True, padx=10, pady=10)

        self._configure_log_tags()
        self._sync_rule_vars_from_config()

        self.log(TEXTS['log_start'], "info")

        self.protocol("WM_DELETE_WINDOW", self.on_closing)

    def _sync_rule_vars_from_config(self) -> None:
        """将 enabled_rules 同步到界面复选框。"""
        for key, var in self.rule_vars.items():
            var.set(self.enabled_rules.get(key, False))
            text_color = GREEN_COLOR if var.get() else "#333333"
            if key in self.rule_checkboxes:
                self.rule_checkboxes[key].configure(text_color=text_color)
        self._update_label_colors()

    def _update_rule_state_and_display(self, key: str, var: ctk.BooleanVar) -> None:
        """复选框回调：将规则开关同步到内部状态，并更新相关标签颜色。"""
        new_state = var.get()
        self.enabled_rules[key] = new_state
        text_color = GREEN_COLOR if new_state else "#333333"
        self.rule_checkboxes[key].configure(text_color=text_color)

        if key in ['current_overload', 'current_unbalance']:
            self._update_label_colors()

    def _apply_thresholds_changes(self) -> None:
        """校验所有阈值输入，合法值写入实例属性，非法值恢复默认并记录日志。"""
        valid_change = False
        threshold_details: List[str] = []

        RANGE_VALIDATION = {
            'V_MIN_THRESHOLD': (0.0, 1000.0), 'V_MAX_THRESHOLD': (0.0, 1000.0),
            'I_MAX_THRESHOLD': (0.0, 100000.0),
            'I_UNBALANCE_MAX_THRESHOLD': (0.0, 1.0), 'PF_MIN_THRESHOLD': (0.0, 1.0),
            'I_MIN_ACTIVE_THRESHOLD': (0.0, float('inf')),
            'T_MIN_THRESHOLD': (-50.0, 200.0), 'T_MAX_THRESHOLD': (-50.0, 200.0),
            'V_IMBALANCE_THRESHOLD': (0.0, 1.0),
            'FREEZE_STD_THRESHOLD': (0.0, 1.0),
            'FREEZE_COUNT_THRESHOLD': (1, 1000),
            'P_ACTIVE_MIN_THRESHOLD': (float('-inf'), float('inf')),
        }

        THRESHOLD_NAMES_MAP = {
            'V_MIN_THRESHOLD': '电压下限 (U_MIN)', 'V_MAX_THRESHOLD': '电压上限 (U_MAX)',
            'I_MAX_THRESHOLD': '电流上限 (I_MAX)',
            'T_MIN_THRESHOLD': '温度下限 (T_MIN)',
            'T_MAX_THRESHOLD': '温度上限 (T_MAX)',
            'I_UNBALANCE_MAX_THRESHOLD': '电流不平衡度上限 (I_UNBAL)',
            'P_ACTIVE_MIN_THRESHOLD': '有功功率下限 (P_MIN)',
            'PF_MIN_THRESHOLD': '功率因数下限 (PF_MIN)',
            'I_MIN_ACTIVE_THRESHOLD': '电流激活下限 (I_ACTIVE)',
            'V_IMBALANCE_THRESHOLD': '相电压不平衡度 (V_IMBALANCE)',
            'FREEZE_COUNT_THRESHOLD': '冻结持续时间 (FREEZE_COUNT)',
            'FREEZE_STD_THRESHOLD': '冻结波动阈值 (FREEZE_STD)',
        }

        for key, var in self.threshold_vars.items():
            original_value_str = var.get()
            is_valid_input = False

            try:
                value = float(original_value_str)
                is_valid_input = True

                if key in RANGE_VALIDATION:
                    min_val, max_val = RANGE_VALIDATION[key]
                    if not (min_val <= value <= max_val):
                        self.log(
                            f"错误：'{THRESHOLD_NAMES_MAP.get(key, key)}' 输入值 {value} 超出合理范围 [{min_val} 到 {max_val}]。",
                            "error")
                        is_valid_input = False

                if is_valid_input:
                    current_value = getattr(self, key)
                    if current_value != value:
                        threshold_details.append(
                            f"{THRESHOLD_NAMES_MAP.get(key, key)} 从 {self._format_value(key, current_value)} 更改为 {self._format_value(key, value)}"
                        )
                        setattr(self, key, value)
                        valid_change = True

            except ValueError:
                self.log(
                    f"警告：'{THRESHOLD_NAMES_MAP.get(key, key)}' 输入值 '{original_value_str}' 无效，已恢复为默认值。",
                    "alert")
                is_valid_input = False

            if not is_valid_input:
                default_val = self.DEFAULT_THRESHOLDS.get(key)
                self.threshold_vars[key].set(self._format_value(key, default_val))
                setattr(self, key, default_val)

        # --- 交叉验证：确保下限 < 上限 ---
        for lower_key, upper_key, name_pair in [
            ('V_MIN_THRESHOLD', 'V_MAX_THRESHOLD', '电压'),
            ('T_MIN_THRESHOLD', 'T_MAX_THRESHOLD', '温度'),
        ]:
            lower_val = getattr(self, lower_key)
            upper_val = getattr(self, upper_key)
            if lower_val >= upper_val:
                self.log(
                    f"错误：'{name_pair}' 下限 ({self._format_value(lower_key, lower_val)}) "
                    f"必须小于上限 ({self._format_value(upper_key, upper_val)})，已恢复默认值。",
                    "error"
                )
                default_lower = self.DEFAULT_THRESHOLDS[lower_key]
                default_upper = self.DEFAULT_THRESHOLDS[upper_key]
                setattr(self, lower_key, default_lower)
                setattr(self, upper_key, default_upper)
                self.threshold_vars[lower_key].set(self._format_value(lower_key, default_lower))
                self.threshold_vars[upper_key].set(self._format_value(upper_key, default_upper))
                threshold_details.append(
                    f"{name_pair}阈值违规，已恢复为默认值 ({self._format_value(lower_key, default_lower)} / "
                    f"{self._format_value(upper_key, default_upper)})"
                )
                valid_change = True

        # 关键：更新标签颜色
        self._update_label_colors()

        if threshold_details:
            self.log(TEXTS['log_applied'], "info")
            for detail in threshold_details:
                self.log(f"    - {detail}", "skip")
        elif not valid_change:
            self.log("没有检测到有效阈值修改，或无效输入已恢复为默认值。", "skip")

        self.save_config()

    def load_config(self) -> None:
        """从 config.json 加载阈值配置，若文件缺失或无效则使用默认值。"""
        loaded_thresholds = self.DEFAULT_THRESHOLDS.copy()

        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, 'r', encoding='utf-8') as config_file:
                    data = json.load(config_file)
                if isinstance(data, dict):
                    for key in self.DEFAULT_THRESHOLDS:
                        if key not in data:
                            continue
                        raw = data[key]
                        if key in RULE_CONFIG_KEYS:
                            loaded_thresholds[key] = bool(raw)
                        else:
                            try:
                                loaded_thresholds[key] = float(raw)
                            except (TypeError, ValueError):
                                pass
                    # 兼容旧配置：如果有 'current'，映射到新键
                    if 'current' in data:
                        current_value = data['current']
                        if isinstance(current_value, bool):
                            loaded_thresholds['current_overload'] = current_value
                            loaded_thresholds['current_unbalance'] = current_value
                else:
                    self.log(f"配置文件格式不正确，已使用默认阈值。", "error")
            except Exception as e:
                self.log(f"配置加载失败，已使用默认值: {e}", "error")

        for key, value in loaded_thresholds.items():
            if key in RULE_CONFIG_KEYS:
                self.enabled_rules[key] = bool(value)
            else:
                setattr(self, key, value)

    def save_config(self) -> None:
        """保存当前阈值配置到 config.json。"""
        config_data = {}
        for key in self.DEFAULT_THRESHOLDS:
            if key not in RULE_CONFIG_KEYS:
                val = getattr(self, key)
                # FREEZE_COUNT_THRESHOLD 保存为整数，避免类型不一致
                if key == 'FREEZE_COUNT_THRESHOLD':
                    val = int(val)
                config_data[key] = val
        for key in RULE_CONFIG_KEYS:
            config_data[key] = self.enabled_rules.get(key, False)
        try:
            config_dir = os.path.dirname(self.config_path)
            if config_dir and not os.path.exists(config_dir):
                os.makedirs(config_dir, exist_ok=True)

            with open(self.config_path, 'w', encoding='utf-8') as config_file:
                json.dump(config_data, config_file, ensure_ascii=False, indent=4)
            self.log(f"配置已保存：{os.path.basename(self.config_path)}", "info")
        except OSError as e:
            self.log(f"配置保存失败（权限或文件系统错误）：{e}", "error")
        except Exception as e:
            self.log(f"配置保存失败：{e}", "error")

    def _validate_and_update_threshold_callback(self, key: str, var: ctk.StringVar, event=None):
        """阈值输入实时校验回调。

        仅在输入过程中静默地尝试 float 转换，错误时忽略而不弹窗，
        避免用户在输入中途（如临时为空或输入非数字字符）被连续的异常中断。
        """
        try:
            float(var.get())
        except ValueError:
            pass
        except TclError:
            pass

    def _update_rules_display(self) -> None:
        """刷新规则面板的视觉状态，仅更新标签颜色，不销毁/重建控件。"""
        self._update_label_colors()

    def _update_label_colors(self) -> None:
        """按规则启用状态更新阈值标签的文字颜色（绿色=启用，灰色=禁用）。

        通过 winfo_exists 检测控件是否已销毁，避免多线程 TclError。
        """
        for key, label in self.threshold_labels.items():
            try:
                if not label.winfo_exists():
                    continue

                rule_link_type = self.threshold_label_links.get(key, 'core')

                color = GREEN_COLOR
                if rule_link_type == 'optional_link_current_overload':
                    is_enabled = self.enabled_rules.get('current_overload', True)
                    color = GREEN_COLOR if is_enabled else GREY_COLOR
                elif rule_link_type == 'optional_link_current_unbalance':
                    is_enabled = self.enabled_rules.get('current_unbalance', False)
                    color = GREEN_COLOR if is_enabled else GREY_COLOR
                elif rule_link_type == 'optional_link_pf':
                    is_enabled = self.enabled_rules.get('power_factor', False)
                    color = GREEN_COLOR if is_enabled else GREY_COLOR

                label.configure(text_color=color)
            except TclError:
                pass  # 忽略已销毁控件的错误

    def _create_rules_widgets(self) -> None:
        """在规则面板中构建阈值输入控件（标签、输入框、应用按钮）。

        将 11 个阈值按功能分为左栏（核心参数）和右栏（可选规则参数），
        同时为每个输入框绑定 trace_add 和焦点事件用于实时校验。
        """
        detail_frame = ctk.CTkFrame(self.rules_display_content_frame, fg_color="#fafafa")
        detail_frame.pack(fill="both", expand=True, padx=10, pady=5)

        detail_frame.grid_columnconfigure(0, weight=1)
        detail_frame.grid_columnconfigure(1, weight=1)

        left_frame = ctk.CTkFrame(detail_frame, fg_color="#fafafa")
        left_frame.grid(row=0, column=0, sticky="nsew", padx=(5, 10))
        right_frame = ctk.CTkFrame(detail_frame, fg_color="#fafafa")
        right_frame.grid(row=0, column=1, sticky="nsew", padx=(10, 5))

        editable_fields = [
            ('V_MIN_THRESHOLD', '电压 U_MIN: (下限)', left_frame, 'core'),
            ('V_MAX_THRESHOLD', '电压 U_MAX: (上限)', left_frame, 'core'),
            ('I_MAX_THRESHOLD', '电流 I_MAX: (上限)', left_frame, 'optional_link_current_overload'),
            ('T_MIN_THRESHOLD', '温度 T_MIN: (下限)', left_frame, 'core'),
            ('T_MAX_THRESHOLD', '温度 T_MAX: (上限)', left_frame, 'core'),
            ('I_UNBALANCE_MAX_THRESHOLD', '电流不平衡 (I_UNBAL): (上限)', right_frame, 'optional_link_current_unbalance'),
            ('P_ACTIVE_MIN_THRESHOLD', '有功功率 (P_MIN): (下限)', right_frame, 'core'),
            ('PF_MIN_THRESHOLD', '功率因数 (PF_MIN): (下限)', right_frame, 'optional_link_pf'),
            ('I_MIN_ACTIVE_THRESHOLD', '电流激活 (I_ACTIVE): (下限)', right_frame, 'optional_link_current_unbalance'),
            ('V_IMBALANCE_THRESHOLD', '相电压不平衡 (V_IMBAL): (偏差比)', right_frame, 'core'),
            ('FREEZE_COUNT_THRESHOLD', '冻结持续时间 (FREEZE_COUNT): (点数)', right_frame, 'core'),
            ('FREEZE_STD_THRESHOLD', '冻结波动阈值 (FREEZE_STD): (标准差)', right_frame, 'core'),
        ]

        current_row_left = 0
        current_row_right = 0
        for key, label, frame, rule_link_type in editable_fields:
            current_value = self._format_value(key, getattr(self, key))

            if key not in self.threshold_vars:
                self.threshold_vars[key] = ctk.StringVar()
            var = self.threshold_vars[key]

            var.set(current_value)

            if frame == left_frame:
                frame_row = current_row_left
                current_row_left += 1
            else:
                frame_row = current_row_right
                current_row_right += 1

            # 颜色初始设置
            color = GREEN_COLOR
            if rule_link_type == 'optional_link_current_overload' and not self.enabled_rules.get('current_overload', True):
                color = GREY_COLOR
            elif rule_link_type == 'optional_link_current_unbalance' and not self.enabled_rules.get('current_unbalance', False):
                color = GREY_COLOR
            elif rule_link_type == 'optional_link_pf' and not self.enabled_rules.get('power_factor', False):
                color = GREY_COLOR

            label_widget = ctk.CTkLabel(
                frame, text=label, anchor="w",
                font=ctk.CTkFont(family="Microsoft YaHei UI", size=11),
                text_color=color
            )
            label_widget.grid(row=frame_row, column=0, sticky="w", padx=18, pady=3)

            entry = ctk.CTkEntry(
                frame, width=80, height=24,
                font=ctk.CTkFont(family="Consolas", size=11),
                textvariable=var,
                fg_color="white",
                border_color="#909090",
                border_width=1
            )

            entry.grid(row=frame_row, column=1, sticky="w", padx=(0, 5), pady=3)

            # 存储引用
            self.threshold_entries[key] = entry
            self.threshold_labels[key] = label_widget
            self.threshold_label_links[key] = rule_link_type

            var.trace_add("write", lambda *args, k=key, v=var: self._validate_and_update_threshold_callback(k, v))
            entry.bind("<FocusOut>",
                       lambda event, k=key, v=var: self._validate_and_update_threshold_callback(k, v, event))
            entry.bind("<Return>",
                       lambda event, k=key, v=var: self._validate_and_update_threshold_callback(k, v, event))

        frame.grid_columnconfigure(0, weight=1)
        frame.grid_columnconfigure(1, weight=0)

        self.apply_btn = ctk.CTkButton(
            right_frame, text=TEXTS['btn_apply'], width=100, height=30,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color=BLUE_COLOR, hover_color="#106ebe", corner_radius=8,
            command=self._apply_thresholds_changes
        )
        self.apply_btn.grid(row=current_row_right, column=1, sticky="e", padx=(0, 5), pady=(15, 5))
        current_row_right += 1

        needed_padding = current_row_left - current_row_right
        if needed_padding > 0:
            for i in range(needed_padding):
                ctk.CTkFrame(right_frame, height=26, fg_color="#fafafa").grid(row=current_row_right + i, column=0,
                                                                              columnspan=2, sticky="ew")

        ctk.CTkFrame(left_frame, height=1, fg_color="transparent").grid(row=current_row_left, column=0, columnspan=2,
                                                                        sticky="ew", pady=2)
        ctk.CTkFrame(right_frame, height=1, fg_color="transparent").grid(row=current_row_right + needed_padding,
                                                                         column=0, columnspan=2, sticky="ew", pady=2)

        self.rules_display_card.update_idletasks()

    def _create_rules_header(self, parent: ctk.CTkFrame) -> None:
        """构建规则面板的折叠/展开按钮。"""
        self.rules_toggle_btn = ctk.CTkButton(
            parent, text=f"▽ {TEXTS['rule_main_title']}",
            height=34,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color="white", text_color="#333333", hover_color="#f0f0f0",
            anchor="w", corner_radius=10, border_width=1, border_color="#e1e1e1",
            command=self._toggle_rules_panel
        )
        self.rules_toggle_btn.grid(row=0, column=0, sticky="ew")

    def _toggle_rules_panel(self, state: bool = None) -> None:
        """切换规则面板的显示/隐藏，同时更新按钮文字为 ▽/△。"""
        is_mapped = self.rules_display_card.winfo_ismapped()
        target_state = not is_mapped if state is None else state

        if target_state:
            self.rules_display_card.grid()
            self.rules_toggle_btn.configure(text=f"△ {TEXTS['rule_main_title']}")
        else:
            self.rules_display_card.grid_remove()
            self.rules_toggle_btn.configure(text=f"▽ {TEXTS['rule_main_title']}")

    def _configure_log_tags(self) -> None:
        """为日志文本框注册颜色 tag，用于按日志级别（info/skip/alert/error/success）着色。"""
        if self.log_text:
            self.log_text.tag_config("info", foreground=GREEN_COLOR)
            self.log_text.tag_config("skip", foreground=GREY_COLOR)
            self.log_text.tag_config("alert", foreground="#d83b01")
            self.log_text.tag_config("error", foreground="#d13438")
            self.log_text.tag_config("success", foreground="#0078d4")

    def _create_log_header(self, parent: ctk.CTkFrame) -> None:
        """构建日志区域标题栏（含"操作日志"标签 + 导出/清空按钮）。"""
        ctk.CTkLabel(
            parent, text=TEXTS['log_title'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            text_color="#333333", anchor="w"
        ).grid(row=0, column=0, sticky="w", padx=2)

        ctk.CTkButton(
            parent, text=TEXTS['btn_export_log'], width=80, height=28,
            font=ctk.CTkFont(family="Microsoft Ya Hei UI", size=10),
            fg_color="transparent", border_width=1, border_color="#cccccc",
            text_color="#333333", hover_color="#f0f0f0", corner_radius=6,
            command=self.export_log
        ).grid(row=0, column=1, sticky="e", padx=(0, 6))

        ctk.CTkButton(
            parent, text=TEXTS['btn_clear_log'], width=80, height=28,
            font=ctk.CTkFont(family="Microsoft Ya Hei UI", size=10),
            fg_color="transparent", border_width=1, border_color="#cccccc",
            text_color="#333333", hover_color="#f0f0f0", corner_radius=6,
            command=self.clear_log
        ).grid(row=0, column=2, sticky="e")

    def _create_directory_cards(self, parent: ctk.CTkFrame, start_row: int) -> None:
        """构建输入目录和报告目录选择卡片（含标签 + 选择按钮）。"""
        folder_card = ctk.CTkFrame(parent, height=68, fg_color="white", corner_radius=10, border_width=1,
                                   border_color="#e1e1e1")
        folder_card.grid(row=start_row, column=0, sticky="ew", pady=(0, 14))
        folder_card.grid_propagate(False)
        folder_card.grid_columnconfigure(0, weight=1)

        self.folder_label = ctk.CTkLabel(
            folder_card, text=TEXTS['folder_default'], anchor="w",
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=13),
            text_color="#333333", padx=18
        )
        self.folder_label.grid(row=0, column=0, sticky="ew", padx=(18, 10), pady=14)

        ctk.CTkButton(
            folder_card, text=TEXTS['btn_select_input'], width=148, height=38,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color="#0078d4", hover_color="#106ebe", corner_radius=8,
            command=self.select_folder
        ).grid(row=0, column=1, padx=(0, 18), pady=14)

        report_card = ctk.CTkFrame(parent, height=68, fg_color="white", corner_radius=10, border_width=1,
                                   border_color="#e1e1e1")
        report_card.grid(row=start_row + 1, column=0, sticky="ew", pady=(0, 14))
        report_card.grid_propagate(False)
        report_card.grid_columnconfigure(0, weight=1)

        self.report_label = ctk.CTkLabel(
            report_card, text=TEXTS['report_default'], anchor="w",
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=13),
            text_color="#333333", padx=18
        )
        self.report_label.grid(row=0, column=0, sticky="ew", padx=(18, 10), pady=14)

        ctk.CTkButton(
            report_card, text=TEXTS['btn_select_report'], width=148, height=38,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color="#0078d4", hover_color="#106ebe", corner_radius=8,
            command=self.select_report_path
        ).grid(row=0, column=1, padx=(0, 18), pady=14)

    def select_report_path(self) -> None:
        """弹出目录选择对话框设置报告输出路径，并更新标签和日志。"""
        path = filedialog.askdirectory(title="请选择报告保存目录")
        if path:
            self.report_path = path
            self.report_path_is_custom = True
            self.initial_report_path = path
            display = path if len(path) < 60 else "..." + path[-57:]
            self.report_label.configure(text=display)
            self.log(f"报告目录已更改为：{os.path.basename(path)}", "info")

    def select_folder(self) -> None:
        """弹出目录选择对话框设置输入目录，并调 `set_folder` 完成配置。"""
        folder = filedialog.askdirectory(title="请选择包含 CSV 文件的目录")
        if folder:
            self.set_folder(folder)

    def set_folder(self, path: str) -> None:
        """设置输入目录并统计 CSV 文件数，同步更新报告路径（若未自定义）。

        Args:
            path: 用户选择的输入目录。
        """
        if not self.report_path_is_custom:
            self.report_path = path
            self.initial_report_path = path
            report_display = path if len(path) < 60 else "..." + path[-57:]
            self.report_label.configure(text=report_display)

        self.folder_path = path
        count = len(glob.glob(os.path.join(path, '**', '*.csv'), recursive=True))
        folder_display = path if len(path) < 60 else "..." + path[-57:]

        self.folder_label.configure(text=folder_display)
        self.status.configure(text=f"已选择目录 · 共发现 {count} 个 CSV 文件")
        self.log(f"输入目录：{os.path.basename(path)}（{count} 个文件）", "info")

    def clear_log(self) -> None:
        """清空日志文本框全部内容并写入确认消息。"""
        self.log_text.delete("1.0", "end")
        self.log("日志已清空", "skip")

    def export_log(self) -> None:
        """将日志文本框内容导出为 UTF-8 文本文件。"""
        log_data = self.log_text.get("1.0", "end-1c")
        if not log_data.strip():
            self.log("日志为空，未导出任何内容。", "skip")
            return

        initial_file = f"检测日志_{datetime.now().strftime('%Y%m%d_%H%M%S')}.txt"
        save_path = filedialog.asksaveasfilename(
            defaultextension=".txt",
            filetypes=[("Text files", "*.txt")],
            initialfile=initial_file,
            title="保存日志为"
        )
        if not save_path:
            return

        try:
            with open(save_path, 'w', encoding='utf-8') as file:
                file.write(log_data)
            self.log(f"日志已导出：{os.path.basename(save_path)}", "success")
        except Exception as e:
            self.log(f"日志导出失败：{e}", "error")

    def log(self, msg: str, tag: str = "info") -> None:
        """将日志消息放入队列，由主线程 `_process_log_queue` 定时渲染。

        使用队列而非直接更新 UI 是为了保证线程安全——检测线程也调用此方法。

        Args:
            msg: 日志消息文本。
            tag: tag 名称，对应 _configure_log_tags 中配置的颜色。
        """
        self.log_queue.put((msg, tag))

    def _process_log_queue(self) -> None:
        """定时从队列中取出日志消息并写入 Textbox，单次最多处理 50 条防止阻塞。"""
        processed = 0
        while not self.log_queue.empty() and processed < 50:
            msg, tag = self.log_queue.get()
            ts = datetime.now().strftime("%H:%M:%S")
            self.log_text.insert("end", f"[{ts}] {msg}\n", tag)
            self.log_text.see("end")
            processed += 1
        self.after(100, self._process_log_queue)

    def _flush_anomaly_batches(self, out_file: str, anomaly_batches: List[Any], write_header: bool) -> bool:
        """合并批量异常 DataFrames 并追加写入 CSV，写完后清空批次列表。

        Returns:
            bool: 写入后 header 标志（首次写入后置 False）。
        """
        if not anomaly_batches:
            return write_header

        import pandas as pd

        try:
            combined = pd.concat(anomaly_batches, ignore_index=True, sort=False)
            os.makedirs(self.report_path, exist_ok=True)
            combined.to_csv(out_file, index=False, encoding='gbk', mode='a', header=write_header)
            write_header = False
        except Exception as e:
            self.log(f"批量写入报告失败：{e}", "error")
        finally:
            anomaly_batches.clear()
        return write_header

    def start_detection_thread(self) -> None:
        """启动检测线程：先应用阈值修改，再在后台线程执行扫描。"""
        self._apply_thresholds_changes()

        if not self.folder_path:
            messagebox.showerror("输入错误", TEXTS['err_no_folder'])
            return
        self._stop_detection.clear()
        self.after(0, lambda: self._update_ui_state("start"))
        threading.Thread(target=self.start_detection, daemon=True).start()

    def request_stop_detection(self) -> None:
        """设置停止标志并禁用按钮，等待检测线程在当前文件处理完毕后退出。"""
        if not self._stop_detection.is_set():
            self._stop_detection.set()
            self.log("正在停止检测（将在当前文件处理完成后停止）...", "skip")
            self.start_btn.configure(state="disabled", text="正在停止...")

    def start_detection(self) -> None:
        """主检测循环：遍历所有 CSV 文件执行异常检测，实时更新进度并输出汇总报告。

        在后台线程中运行。支持暂停/恢复/取消。
        """
        import pandas as pd

        self.after(0, lambda: self._toggle_rules_panel(False))

        files = list(glob.iglob(os.path.join(self.folder_path, '**', '*.csv'), recursive=True))
        total = len(files)

        if not total:
            self.after(0, lambda: messagebox.showinfo("提示", TEXTS['err_no_file']))
            self.after(0, lambda: self._update_ui_state("finish"))
            return

        processed = 0
        written_records = 0
        involved_files = 0
        self.start_time = time.time()
        ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        out_file = os.path.join(self.report_path, f"电气异常报告_{ts}.csv")
        self.log(f"开始分析 {total} 个文件...", "info")

        current_thresholds = self._build_thresholds_dict()
        # 快照当前的规则启用状态，防止检测过程中切换复选框
        current_rules = dict(self.enabled_rules)

        anomaly_batches: List[Any] = []
        write_header = not os.path.exists(out_file)

        # --- 汇总跟踪变量 ---
        normal_count = 0
        skipped_files_with_reason: List[Tuple[str, str]] = []
        frozen_acquisition: List[Tuple[str, str]] = []
        transformer_issues: Dict[Tuple[str, str], Dict[str, Any]] = {}
        offline_devices: List[Tuple[str, str]] = []
        sensor_fault_list: List[Tuple[str, str, str]] = []
        sensor_missing_rates: Dict[str, List[float]] = {}

        update_counter = 0
        cancelled = False
        for fp in files:
            if self._stop_detection.is_set():
                cancelled = True
                break
            processed += 1
            file_name = os.path.basename(fp)

            update_counter += 1
            if update_counter % 20 == 0 or processed == total:
                self.after(0, lambda p=processed, t=total, name=file_name:
                           self._update_progress_status(p, t, name))

            anomalies, log_data, cleaned_df, extra_info = check_anomaly_in_file(fp, current_thresholds, current_rules)

            if isinstance(log_data, dict):
                msg, tag = _format_log_message(
                    file_name=log_data['filename'],
                    anomalies_count=log_data['count'],
                    anomaly_types=log_data['types'],
                    pure_freeze=log_data.get('pure_freeze', False),
                    freeze_filtered_count=log_data.get('freeze_filtered_count', 0),
                    sensor_missing=extra_info.get('sensor_missing', []),
                )
            elif "跳过" in log_data or "读取失败" in log_data:
                msg, tag = _format_log_message(
                    file_name=file_name,
                    error_msg=log_data,
                    sensor_missing=extra_info.get('sensor_missing', []),
                )
            else:
                msg = log_data
                tag = "info"

            self.log(msg, tag)

            # --- 更新汇总变量 ---
            bld, trans = _extract_building_and_transformer(fp, self.folder_path)

            if extra_info.get('is_offline'):
                offline_devices.append((bld, trans))
            for sensor_col in extra_info.get('sensor_faults', []):
                sensor_fault_list.append((bld, trans, sensor_col))

            if isinstance(log_data, dict) and anomalies is not None and not anomalies.empty:
                anomaly_batches.append(anomalies)
                written_records += len(anomalies)
                involved_files += 1
                self.last_report_file = out_file

                # 离线设备只写 CSV，不进入明细 / 不触发采集故障/传感器统计
                if not extra_info.get('is_offline'):
                    is_frozen = _check_frozen_acquisition(cleaned_df) if cleaned_df is not None else False
                    if is_frozen:
                        frozen_acquisition.append((bld, trans))

                    if not is_frozen:
                        if cleaned_df is not None:
                            for col in TARGET_SHORT_NAMES_REPORT:
                                if col in cleaned_df.columns:
                                    n_total = len(cleaned_df)
                                    n_missing = int(cleaned_df[col].isna().sum())
                                    if n_total > 0:
                                        rate = n_missing / n_total
                                        if col not in sensor_missing_rates:
                                            sensor_missing_rates[col] = []
                                        sensor_missing_rates[col].append(rate)

                        issues = _extract_transformer_issues(anomalies, cleaned_df)
                        if issues:
                            key = (bld, trans)
                            if key not in transformer_issues:
                                transformer_issues[key] = {}
                            transformer_issues[key].update(issues)

            elif "跳过" in log_data or "读取失败" in log_data:
                skipped_files_with_reason.append((file_name, log_data))
            elif tag == "info" and "正常" in msg:
                normal_count += 1

            if len(anomaly_batches) >= 50:
                write_header = self._flush_anomaly_batches(out_file, anomaly_batches, write_header)

        if anomaly_batches:
            write_header = self._flush_anomaly_batches(out_file, anomaly_batches, write_header)

        self.end_time = time.time()
        duration = self.end_time - self.start_time if self.start_time else 0.0
        duration_text = self._format_duration_text(duration, processed if cancelled else total)

        # --- 输出汇总 ---
        self._output_detection_summary(
            total=processed if cancelled else total,
            normal_count=normal_count,
            written_records=written_records,
            transformer_issues=transformer_issues,
            frozen_acquisition=frozen_acquisition,
            offline_devices=offline_devices,
            sensor_faults=sensor_fault_list,
            skipped_files_with_reason=skipped_files_with_reason,
            sensor_missing_rates=sensor_missing_rates,
            cancelled=cancelled,
            duration_text=duration_text,
        )

        if cancelled:
            summary_text = (
                f"检测已取消：已处理 {processed}/{total} 文件，异常 {written_records} 条；{duration_text}"
            )
            self.log(summary_text, "alert")
        else:
            summary_text = (
                f"检测完成：{total} 文件，异常 {written_records} 条，异常文件 {involved_files} 个；{duration_text}"
            )
        self.after(0, lambda s=summary_text: self.status.configure(text=s))

        if cancelled:
            self.after(0, lambda p=processed, t=total: messagebox.showinfo(
                "检测已取消",
                f"已处理 {p}/{t} 个文件。\n部分结果可能已写入报告文件。",
            ))
        elif written_records > 0:
            self.log(f"异常报告已生成：{os.path.basename(out_file)}，共写入 {written_records} 条异常记录。", "success")
            self.after(0, lambda out=out_file, count=written_records: messagebox.showinfo(
                "检测完成",
                f"共发现 {count} 条异常记录\n\n"
                f"报告已保存至：\n{out}"
            ))
        elif not cancelled:
            self.log("全部文件正常，未发现异常数据", "info")
            self.after(0, lambda: messagebox.showinfo("检测完成", "未发现任何异常参数，所有文件均正常。"))

        if cancelled and total:
            self.after(0, lambda p=processed, t=total: self.progress.set(p / t))
        elif not cancelled:
            self.after(0, lambda: self.progress.set(1))
        self.after(0, lambda: self._update_ui_state("finish"))

    def _output_detection_summary(
        self,
        total: int,
        normal_count: int,
        written_records: int,
        transformer_issues: Dict[Tuple[str, str], Dict[str, Dict[str, Any]]],
        frozen_acquisition: List[Tuple[str, str]],
        offline_devices: List[Tuple[str, str]],
        sensor_faults: List[Tuple[str, str, str]],
        skipped_files_with_reason: List[Tuple[str, str]],
        sensor_missing_rates: Dict[str, List[float]],
        cancelled: bool,
        duration_text: str,
    ) -> None:
        """输出紧凑型检测汇总报告：建筑 → 变压器 → 故障类型（含次数、参数范围、时间范围）。"""
        from collections import defaultdict

        skipped_count = len(skipped_files_with_reason)
        anomaly_files = len(transformer_issues)
        status = "已取消" if cancelled else "完成"
        short_duration = duration_text.split('(')[0].strip().replace('总耗时: ', '')

        self.log("", "info")
        self.log("=" * 60, "info")
        self.log(f"  E-Detection 检测汇总                      {status} · {short_duration}", "info")
        self.log("=" * 60, "info")
        self.log(f"  {total}文件 → 正常{normal_count} · 异常{anomaly_files}({written_records}条) · 跳过{skipped_count}", "info")

        if not transformer_issues and not frozen_acquisition and not offline_devices and not sensor_faults:
            if normal_count == total - skipped_count:
                self.log("", "info")
                self.log("  ✓ 未发现任何异常", "success")
            self.log("=" * 60, "info")
            self.log("", "info")
            return

        # --- 按建筑物分组（紧凑格式：变压器一行汇总） ---
        building_data: Dict[str, List[Tuple[str, Dict[str, Dict[str, Any]]]]] = defaultdict(list)
        for (bld, trans), issues in transformer_issues.items():
            building_data[bld].append((trans, issues))

        for bld in sorted(building_data.keys()):
            self.log("", "info")
            self.log(f"  {bld}", "info")
            for trans, issues in sorted(building_data[bld], key=lambda x: x[0]):
                sorted_issues = sorted(issues.items(), key=lambda x: x[1]['count'], reverse=True)
                issue_parts = [f"{tp}({inf['count']}次)" for tp, inf in sorted_issues]
                self.log(f"    {trans}  {'  '.join(issue_parts)}", "alert")

        # --- 特殊项 ---
        self.log("", "info")

        # 传感器未配置一览（改进5：结构化的传感器缺失表）
        if sensor_missing_rates:
            self.log("  ▎传感器未配置一览", "heading")
            missing_items = []
            for col in TARGET_SHORT_NAMES_REPORT:
                rates = sensor_missing_rates.get(col, [])
                if not rates:
                    continue
                n_files = len(rates)
                missing_items.append(f"{col}: {n_files}个文件")
            if missing_items:
                self.log(f"    {' · '.join(missing_items)}", "skip")
            self.log("", "info")

        if frozen_acquisition:
            by_b: Dict[str, List[str]] = defaultdict(list)
            for bld, trans in frozen_acquisition:
                by_b[bld].append(trans)
            parts = [f"{b}({' · '.join(sorted(set(t_list)))})" for b, t_list in sorted(by_b.items())]
            self.log(f"  ⚠ 采集故障: {'  '.join(parts)}", "alert")

        if offline_devices:
            by_b: Dict[str, List[str]] = defaultdict(list)
            for bld, trans in offline_devices:
                by_b[bld].append(trans)
            parts = [f"{b}({' · '.join(sorted(set(t_list)))})" for b, t_list in sorted(by_b.items())]
            self.log(f"  ⚠ 设备离线: {'  '.join(parts)}", "alert")

        if sensor_faults:
            by_b: Dict[str, List[str]] = defaultdict(list)
            for bld, trans, col in sensor_faults:
                by_b[bld].append(f"{trans}/{col}")
            parts = [f"{b}({' · '.join(sorted(set(d_list)))})" for b, d_list in sorted(by_b.items())]
            self.log(f"  ⚠ 传感器故障: {'  '.join(parts)}", "alert")

        if skipped_files_with_reason:
            reason_counts: Dict[str, int] = defaultdict(int)
            for _, reason in skipped_files_with_reason:
                if "高压" in reason:
                    reason_counts["高压设备"] += 1
                elif "未识别" in reason:
                    reason_counts["未识别字段"] += 1
                elif "读取失败" in reason:
                    reason_counts["读取失败"] += 1
                else:
                    reason_counts["其它"] += 1
            parts = [f"{r}{c}个" for r, c in sorted(reason_counts.items(), key=lambda x: x[1], reverse=True)]
            self.log(f"  ⏭ 跳过{skipped_count}: {' · '.join(parts)}", "skip")

        self.log("=" * 60, "info")
        self.log("", "info")

    def _update_progress_status(self, processed: int, total: int, filename: str) -> None:
        """更新进度条和状态标签。

        Args:
            processed: 已处理文件数。
            total: 总文件数。
            filename: 当前文件名。
        """
        self.progress.set(processed / total)
        self.status.configure(text=f"处理中：{processed}/{total} | {filename}")

    def _update_ui_state(self, state: str) -> None:
        """切换 UI 控件状态（开始→运行中 或 运行中→完成）。

        "start": 按钮变为"停止检测"、禁用阈值输入和复选框。
        "finish": 恢复按钮和输入控件。
        """
        if state == "start":
            self.start_btn.configure(
                state="normal",
                text=TEXTS['btn_stop'],
                fg_color="#d83b01",
                hover_color="#a4262c",
                command=self.request_stop_detection,
            )
            self.progress.set(0)
            # 禁用规则复选框，防止检测过程中切换
            for chk in self.rule_checkboxes.values():
                chk.configure(state="disabled")
            # 禁用阈值输入框和应用按钮
            for entry in self.threshold_entries.values():
                entry.configure(state="disabled")
            if hasattr(self, 'apply_btn') and self.apply_btn.winfo_exists():
                self.apply_btn.configure(state="disabled")
        elif state == "finish":
            self.start_btn.configure(
                state="normal",
                text=TEXTS['btn_start'],
                fg_color="#0078d4",
                hover_color="#106ebe",
                command=self.start_detection_thread,
            )
            self._stop_detection.clear()
            # 恢复规则复选框
            for chk in self.rule_checkboxes.values():
                chk.configure(state="normal")
            # 恢复阈值输入框和应用按钮
            for entry in self.threshold_entries.values():
                entry.configure(state="normal")
            if hasattr(self, 'apply_btn') and self.apply_btn.winfo_exists():
                self.apply_btn.configure(state="normal")

if __name__ == '__main__':
    app = ElectricalAnomalyDetectorApp()
    app.mainloop()
"""Single-file detection pipeline for E-Detection.

This module contains the data loading, normalization, rule execution, and
report shaping logic that used to live in the legacy GUI script. Keep GUI
code out of this module so the detector can be tested and reused by CLI,
batch jobs, and future report generators.
"""
from __future__ import annotations

import os
import re
from typing import Any, Dict, List, Tuple

from core.rule_base import DetectionContext
from core.rules import (
    CurrentOverloadRule,
    CurrentUnbalanceRule,
    FreezeRule,
    PowerActiveRule,
    PowerFactorRule,
    TemperatureRule,
    VoltageRule,
)

from .settings import INVALID_VALUES, PHASE_MAP, TARGET_SHORT_NAMES_REPORT


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

    # 未配置传感器后缀（追加到正常/异常日志尾部）
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
            numeric_block = block.apply(pd.to_numeric, errors="coerce")
            merged[name] = numeric_block.mean(axis=1, skipna=True)
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
    keep_indices = [index for index, column in enumerate(current_cols) if column in new_cols]
    final_df = df.iloc[:, keep_indices].copy()
    final_df.columns = [new_cols[current_cols[index]] for index in keep_indices]
    return _merge_duplicate_named_columns(final_df), unmapped

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
    """精简异常类型用于报告类型列：保留第一个代表性数值，每行最多3类。

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
    同时过滤仅含冻结/恒定/传感器缺失的行（不进入异常明细），
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

    # --- 过滤仅冻结行：仅含数据冻结/传感器缺失/数据恒定的行不进入异常明细 ---
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



    # --- 数据冻结 ---
    fz_mask = anomalies_df['异常类型'].str.contains('数据冻结', na=False)
    _add_issue('数据冻结', fz_mask, '')

    return issues

"""Internal report model shared by Excel and LLM report generation."""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any

import pandas as pd

from .settings import DEFAULT_CONFIG, TARGET_SHORT_NAMES_REPORT, normalize_config

DETAIL_PREFIX_COLUMNS = ["建筑", "相对路径", "变压器"]
VOLTAGE_COLUMNS = ["Uab", "Ubc", "Uca"]
CURRENT_COLUMNS = ["Ia", "Ib", "Ic"]
TEMP_COLUMNS = ["A相温度", "B相温度", "C相温度"]


@dataclass(frozen=True)
class DetectionRun:
    input_dir: str
    output_dir: str
    total_files: int
    processed_files: int
    normal_files: int
    anomaly_files: int
    anomaly_records: int
    skipped_files: int
    generated_at: str
    duration: str = ""


@dataclass(frozen=True)
class IssueFinding:
    issue_type: str
    count: int


@dataclass(frozen=True)
class DeviceFinding:
    building: str
    transformer: str
    anomaly_records: int
    main_issue_types: str
    first_time: Any
    last_time: Any
    highest_severity: str
    priority: str


@dataclass(frozen=True)
class SensorFinding:
    source_file: str
    building: str
    relative_path: str
    transformer: str
    is_offline: str = "否"
    sensor_faults: str = ""
    sensor_missing: str = ""
    status: str = ""
    reason: str = ""


@dataclass
class ReportContext:
    run: DetectionRun
    details: pd.DataFrame
    device_summary: pd.DataFrame
    type_statistics: pd.DataFrame
    sensor_status: pd.DataFrame
    config: dict[str, Any]
    messages: list[str] = field(default_factory=list)

    @property
    def high_risk_devices(self) -> pd.DataFrame:
        if self.device_summary.empty:
            return self.device_summary
        return self.device_summary[self.device_summary["最高严重等级"] == "高"]


def enrich_anomalies(
    anomalies: pd.DataFrame,
    building: str,
    transformer: str,
    relative_path: str = "",
) -> pd.DataFrame:
    """Add location columns used by downstream reports."""
    if anomalies is None or anomalies.empty:
        return pd.DataFrame()

    enriched = anomalies.copy()
    values = {
        "建筑": building,
        "相对路径": relative_path,
        "变压器": transformer,
    }
    for index, (column, value) in enumerate(values.items()):
        if column not in enriched.columns:
            enriched.insert(index, column, value)
        else:
            enriched[column] = enriched[column].fillna(value)
    return enriched


def build_report_context(
    anomalies: pd.DataFrame,
    summary: dict[str, Any],
    config: dict[str, Any] | None = None,
    sensor_status_rows: list[dict[str, Any]] | None = None,
    skipped_details: list[dict[str, Any]] | None = None,
    messages: list[str] | None = None,
) -> ReportContext:
    """Build the internal fact model used by Excel and LLM surfaces."""
    normalized_config = normalize_config(config or DEFAULT_CONFIG)
    details = prepare_details(anomalies, normalized_config)
    sensor_status = pd.DataFrame((sensor_status_rows or []) + (skipped_details or []))
    run = DetectionRun(
        input_dir=str(summary.get("input_dir", "")),
        output_dir=str(summary.get("output_dir", "")),
        total_files=int(summary.get("total_files", 0) or 0),
        processed_files=int(summary.get("processed_files", 0) or 0),
        normal_files=int(summary.get("normal_files", 0) or 0),
        anomaly_files=int(summary.get("anomaly_files", 0) or 0),
        anomaly_records=int(summary.get("anomaly_records", 0) or 0),
        skipped_files=int(summary.get("skipped_files", 0) or 0),
        generated_at=str(summary.get("generated_at", "")),
        duration=str(summary.get("duration", "")),
    )
    return ReportContext(
        run=run,
        details=details,
        device_summary=build_device_summary(details),
        type_statistics=build_type_statistics(details),
        sensor_status=sensor_status,
        config=normalized_config,
        messages=messages or [],
    )


def prepare_details(anomalies: pd.DataFrame, config: dict[str, Any]) -> pd.DataFrame:
    """Return enriched anomaly details with severity and recommended action."""
    if anomalies is None or anomalies.empty:
        columns = DETAIL_PREFIX_COLUMNS + [
            "来源文件",
            "日期",
            "时间",
            "严重等级",
            "异常类型",
            "异常详情",
            "异常值",
            "建议处置",
        ] + TARGET_SHORT_NAMES_REPORT
        return pd.DataFrame(columns=columns)

    details = anomalies.copy()
    for column in DETAIL_PREFIX_COLUMNS:
        if column not in details.columns:
            details[column] = ""

    details["严重等级"] = details.apply(classify_severity, axis=1)
    details["建议处置"] = details.apply(recommend_action, axis=1)

    ordered = (
        DETAIL_PREFIX_COLUMNS
        + [
            "来源文件",
            "日期",
            "时间",
            "严重等级",
            "异常类型",
            "异常详情",
            "异常值",
        ]
        + [column for column in TARGET_SHORT_NAMES_REPORT if column in details.columns]
        + ["建议处置"]
    )
    remaining = [column for column in details.columns if column not in ordered]
    return details[ordered + remaining]


def classify_severity(row: pd.Series) -> str:
    text = row_text(row)
    if any(token in text for token in ["设备离线", "PT接线异常", "CT极性异常"]):
        return "高"
    if any(token in text for token in ["数据冻结", "数据恒定", "传感器缺失"]):
        return "低"
    return "中"


def recommend_action(row: pd.Series) -> str:
    text = row_text(row)
    if "PT接线异常" in text:
        return "优先检查 PT 二次接线、端子松动和采样通道配置。"
    if "CT极性异常" in text:
        return "检查 CT 二次侧极性、功率方向和接线相序。"
    if "设备离线" in text:
        return "核查设备通信、电源状态和采集网关在线状态。"
    if "电压" in text:
        return "复核电压采样、负载状态和对应回路运行方式。"
    if "电流" in text:
        return "检查负载分配、三相平衡和额定容量匹配情况。"
    if "功率因数" in text:
        return "检查无功补偿、负载性质和功率因数采样配置。"
    if "温度" in text:
        return "检查柜内散热、负载水平和温度传感器安装状态。"
    if any(token in text for token in ["数据冻结", "数据恒定"]):
        return "优先排查采集链路、网关缓存和测点刷新周期。"
    if "传感器" in text:
        return "核查传感器配置、接线和量程映射。"
    return "结合现场运行方式复核该时段数据。"


def affected_columns(row: pd.Series, config: dict[str, Any]) -> set[str]:
    text = row_text(row)
    affected: set[str] = set()

    v_min = float(config["V_MIN_THRESHOLD"])
    v_max = float(config["V_MAX_THRESHOLD"])
    for column in VOLTAGE_COLUMNS:
        value = as_float(row.get(column))
        if value is not None and (value < v_min or value > v_max):
            affected.add(column)
    if "PT接线异常" in text and not affected:
        affected.update(column for column in VOLTAGE_COLUMNS if column in row.index)

    i_max = float(config["I_MAX_THRESHOLD"])
    for column in CURRENT_COLUMNS:
        value = as_float(row.get(column))
        if value is not None and value > i_max:
            affected.add(column)
    if "电流" in text and not affected:
        affected.update(column for column in CURRENT_COLUMNS if column in row.index)

    if "CT极性异常" in text or "有功功率" in text:
        affected.add("有功功率")
        if "CT极性异常" in text:
            affected.update(column for column in CURRENT_COLUMNS if column in row.index)

    pf_min = float(config["PF_MIN_THRESHOLD"])
    pf_value = as_float(row.get("功率因数"))
    if pf_value is not None and pf_value < pf_min:
        affected.add("功率因数")

    t_min = float(config["T_MIN_THRESHOLD"])
    t_max = float(config["T_MAX_THRESHOLD"])
    for column in TEMP_COLUMNS:
        value = as_float(row.get(column))
        if value is not None and (value < t_min or value > t_max):
            affected.add(column)
    if any(token in text for token in ["温度", "传感器故障"]):
        affected.update(column for column in TEMP_COLUMNS if column in row.index)

    if any(token in text for token in ["数据冻结", "数据恒定"]):
        affected.update(column for column in TARGET_SHORT_NAMES_REPORT if column in row.index)

    return affected


def build_device_summary(details: pd.DataFrame) -> pd.DataFrame:
    if details.empty:
        return pd.DataFrame(
            columns=[
                "建筑",
                "变压器",
                "异常记录数",
                "主要异常类型",
                "首次异常时间",
                "末次异常时间",
                "最高严重等级",
                "建议优先级",
            ]
        )

    rows: list[dict[str, Any]] = []
    for (building, transformer), group in details.groupby(["建筑", "变压器"], dropna=False):
        type_counts = type_counts_from_series(group["异常类型"].fillna(""))
        main_types = "；".join(f"{name}({count})" for name, count in type_counts[:5])
        severities = group["严重等级"].tolist()
        highest = max_severity(severities)
        rows.append(
            {
                "建筑": building,
                "变压器": transformer,
                "异常记录数": len(group),
                "主要异常类型": main_types,
                "首次异常时间": first_non_empty(group.get("时间")),
                "末次异常时间": last_non_empty(group.get("时间")),
                "最高严重等级": highest,
                "建议优先级": priority_from_severity(highest),
            }
        )
    return pd.DataFrame(rows).sort_values(["建议优先级", "异常记录数"], ascending=[True, False])


def build_type_statistics(details: pd.DataFrame) -> pd.DataFrame:
    if details.empty:
        return pd.DataFrame(columns=["统计维度", "名称", "数量"])

    rows: list[dict[str, Any]] = []
    for name, count in type_counts_from_series(details["异常类型"].fillna("")):
        rows.append({"统计维度": "异常类型", "名称": name, "数量": count})

    for building, group in details.groupby("建筑", dropna=False):
        rows.append({"统计维度": "建筑", "名称": building, "数量": len(group)})

    for transformer, group in details.groupby("变压器", dropna=False):
        rows.append({"统计维度": "变压器", "名称": transformer, "数量": len(group)})

    return pd.DataFrame(rows)


def row_text(row: pd.Series) -> str:
    return " ".join(
        str(row.get(column, ""))
        for column in ["异常类型", "异常详情", "异常值"]
        if pd.notna(row.get(column, ""))
    )


def as_float(value: Any) -> float | None:
    try:
        if pd.isna(value):
            return None
        return float(value)
    except (TypeError, ValueError):
        return None


def split_types(value: str) -> list[str]:
    return [item.strip() for item in re.split(r"[;；|]", value) if item.strip()]


def type_counts_from_series(values: pd.Series) -> list[tuple[str, int]]:
    counts: dict[str, int] = {}
    for value in values:
        for item in split_types(str(value)):
            counts[item] = counts.get(item, 0) + 1
    return sorted(counts.items(), key=lambda item: item[1], reverse=True)


def first_non_empty(series: pd.Series | None) -> Any:
    if series is None:
        return ""
    for value in series:
        if pd.notna(value) and str(value) != "":
            return value
    return ""


def last_non_empty(series: pd.Series | None) -> Any:
    if series is None:
        return ""
    for value in reversed(series.tolist()):
        if pd.notna(value) and str(value) != "":
            return value
    return ""


def max_severity(severities: list[str]) -> str:
    order = {"高": 0, "中": 1, "低": 2}
    if not severities:
        return "低"
    return min(severities, key=lambda item: order.get(item, 9))


def priority_from_severity(severity: str) -> str:
    return {"高": "P1", "中": "P2", "低": "P3"}.get(severity, "P3")

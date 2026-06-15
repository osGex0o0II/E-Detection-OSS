"""Excel report generation for E-Detection."""
from __future__ import annotations

import re
from pathlib import Path
from typing import Any

import pandas as pd
from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.worksheet import Worksheet

from .settings import DEFAULT_CONFIG, TARGET_SHORT_NAMES_REPORT, normalize_config

VOLTAGE_COLUMNS = ["Uab", "Ubc", "Uca"]
CURRENT_COLUMNS = ["Ia", "Ib", "Ic"]
TEMP_COLUMNS = ["A相温度", "B相温度", "C相温度"]
DETAIL_PREFIX_COLUMNS = ["建筑", "相对路径", "变压器"]

HEADER_FILL = PatternFill("solid", fgColor="1F4E78")
HEADER_FONT = Font(color="FFFFFF", bold=True)
THIN_BORDER = Border(
    left=Side(style="thin", color="D9E2F3"),
    right=Side(style="thin", color="D9E2F3"),
    top=Side(style="thin", color="D9E2F3"),
    bottom=Side(style="thin", color="D9E2F3"),
)
SEVERITY_FILLS = {
    "高": PatternFill("solid", fgColor="F4CCCC"),
    "中": PatternFill("solid", fgColor="FCE5CD"),
    "低": PatternFill("solid", fgColor="D9EAD3"),
}
PARAMETER_FILL = PatternFill("solid", fgColor="FFF2CC")
SECTION_FILL = PatternFill("solid", fgColor="D9EAF7")


def enrich_anomalies(
    anomalies: pd.DataFrame,
    building: str,
    transformer: str,
    relative_path: str = "",
) -> pd.DataFrame:
    """Add location columns used by Excel reports."""
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


def write_excel_report(
    report_path: str | Path,
    anomalies: pd.DataFrame,
    summary: dict[str, Any],
    config: dict[str, Any] | None = None,
    sensor_status_rows: list[dict[str, Any]] | None = None,
    skipped_details: list[dict[str, Any]] | None = None,
    messages: list[str] | None = None,
) -> Path:
    """Write a multi-sheet XLSX report with row and cell highlights."""
    report_file = Path(report_path)
    report_file.parent.mkdir(parents=True, exist_ok=True)

    normalized_config = normalize_config(config or DEFAULT_CONFIG)
    details = _prepare_details(anomalies, normalized_config)
    sensor_rows = sensor_status_rows or []
    skipped_rows = skipped_details or []

    workbook = Workbook()
    overview_ws = workbook.active
    overview_ws.title = "检测概览"

    _write_overview(overview_ws, summary, details, messages or [])
    _write_dataframe_sheet(
        workbook.create_sheet("异常明细"),
        details,
        normalized_config,
        highlight_details=True,
    )
    _write_dataframe_sheet(
        workbook.create_sheet("设备汇总"),
        _build_device_summary(details),
        normalized_config,
    )
    _write_dataframe_sheet(
        workbook.create_sheet("异常分类统计"),
        _build_type_statistics(details),
        normalized_config,
    )
    _write_dataframe_sheet(
        workbook.create_sheet("传感器状态"),
        pd.DataFrame(sensor_rows + skipped_rows),
        normalized_config,
    )
    _write_config_sheet(workbook.create_sheet("检测配置"), normalized_config, summary)

    workbook.save(report_file)
    return report_file


def _prepare_details(anomalies: pd.DataFrame, config: dict[str, Any]) -> pd.DataFrame:
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

    details["严重等级"] = details.apply(_classify_severity, axis=1)
    details["建议处置"] = details.apply(_recommend_action, axis=1)

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


def _classify_severity(row: pd.Series) -> str:
    text = _row_text(row)
    if any(token in text for token in ["设备离线", "PT接线异常", "CT极性异常"]):
        return "高"
    if any(token in text for token in ["数据冻结", "数据恒定", "传感器缺失"]):
        return "低"
    return "中"


def _recommend_action(row: pd.Series) -> str:
    text = _row_text(row)
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


def _affected_columns(row: pd.Series, config: dict[str, Any]) -> set[str]:
    text = _row_text(row)
    affected: set[str] = set()

    v_min = float(config["V_MIN_THRESHOLD"])
    v_max = float(config["V_MAX_THRESHOLD"])
    for column in VOLTAGE_COLUMNS:
        value = _as_float(row.get(column))
        if value is not None and (value < v_min or value > v_max):
            affected.add(column)
    if "PT接线异常" in text and not affected:
        affected.update(column for column in VOLTAGE_COLUMNS if column in row.index)

    i_max = float(config["I_MAX_THRESHOLD"])
    for column in CURRENT_COLUMNS:
        value = _as_float(row.get(column))
        if value is not None and value > i_max:
            affected.add(column)
    if "电流" in text and not affected:
        affected.update(column for column in CURRENT_COLUMNS if column in row.index)

    if "CT极性异常" in text or "有功功率" in text:
        affected.add("有功功率")
        if "CT极性异常" in text:
            affected.update(column for column in CURRENT_COLUMNS if column in row.index)

    pf_min = float(config["PF_MIN_THRESHOLD"])
    pf_value = _as_float(row.get("功率因数"))
    if pf_value is not None and pf_value < pf_min:
        affected.add("功率因数")

    t_min = float(config["T_MIN_THRESHOLD"])
    t_max = float(config["T_MAX_THRESHOLD"])
    for column in TEMP_COLUMNS:
        value = _as_float(row.get(column))
        if value is not None and (value < t_min or value > t_max):
            affected.add(column)
    if any(token in text for token in ["温度", "传感器故障"]):
        affected.update(column for column in TEMP_COLUMNS if column in row.index)

    if any(token in text for token in ["数据冻结", "数据恒定"]):
        affected.update(column for column in TARGET_SHORT_NAMES_REPORT if column in row.index)

    return affected


def _build_device_summary(details: pd.DataFrame) -> pd.DataFrame:
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
        type_counts = _type_counts(group["异常类型"].fillna(""))
        main_types = "；".join(f"{name}({count})" for name, count in type_counts[:5])
        severities = group["严重等级"].tolist()
        rows.append(
            {
                "建筑": building,
                "变压器": transformer,
                "异常记录数": len(group),
                "主要异常类型": main_types,
                "首次异常时间": _first_non_empty(group.get("时间")),
                "末次异常时间": _last_non_empty(group.get("时间")),
                "最高严重等级": _max_severity(severities),
                "建议优先级": _priority_from_severity(_max_severity(severities)),
            }
        )
    return pd.DataFrame(rows).sort_values(["建议优先级", "异常记录数"], ascending=[True, False])


def _build_type_statistics(details: pd.DataFrame) -> pd.DataFrame:
    if details.empty:
        return pd.DataFrame(columns=["统计维度", "名称", "数量"])

    rows: list[dict[str, Any]] = []
    for name, count in _type_counts(details["异常类型"].fillna("")):
        rows.append({"统计维度": "异常类型", "名称": name, "数量": count})

    for building, group in details.groupby("建筑", dropna=False):
        rows.append({"统计维度": "建筑", "名称": building, "数量": len(group)})

    for transformer, group in details.groupby("变压器", dropna=False):
        rows.append({"统计维度": "变压器", "名称": transformer, "数量": len(group)})

    return pd.DataFrame(rows)


def _write_overview(
    worksheet: Worksheet,
    summary: dict[str, Any],
    details: pd.DataFrame,
    messages: list[str],
) -> None:
    worksheet.append(["指标", "值"])
    for key, label in [
        ("input_dir", "输入目录"),
        ("output_dir", "输出目录"),
        ("total_files", "文件总数"),
        ("processed_files", "已处理文件"),
        ("normal_files", "正常文件"),
        ("anomaly_files", "异常文件"),
        ("anomaly_records", "异常记录"),
        ("skipped_files", "跳过文件"),
        ("generated_at", "生成时间"),
    ]:
        worksheet.append([label, summary.get(key, "")])

    worksheet.append([])
    worksheet.append(["高风险设备 Top 10", "异常记录数"])
    if not details.empty:
        high = details[details["严重等级"] == "高"]
        top = (
            high.groupby(["建筑", "变压器"], dropna=False)
            .size()
            .sort_values(ascending=False)
            .head(10)
        )
        for (building, transformer), count in top.items():
            worksheet.append([f"{building} / {transformer}", int(count)])

    if messages:
        worksheet.append([])
        worksheet.append(["逐文件结果摘录", ""])
        for message in messages[:20]:
            worksheet.append([message, ""])

    _style_tabular_sheet(worksheet)


def _write_dataframe_sheet(
    worksheet: Worksheet,
    dataframe: pd.DataFrame,
    config: dict[str, Any],
    highlight_details: bool = False,
) -> None:
    if dataframe is None or dataframe.empty:
        dataframe = pd.DataFrame(columns=list(dataframe.columns) if dataframe is not None else [])

    worksheet.append(list(dataframe.columns))
    for _, row in dataframe.iterrows():
        worksheet.append([_excel_value(row.get(column)) for column in dataframe.columns])

    _style_tabular_sheet(worksheet)

    if highlight_details and not dataframe.empty:
        column_index = {cell.value: cell.column for cell in worksheet[1]}
        for row_number, (_, row) in enumerate(dataframe.iterrows(), start=2):
            severity = str(row.get("严重等级", "中"))
            fill = SEVERITY_FILLS.get(severity)
            if fill:
                for cell in worksheet[row_number]:
                    cell.fill = fill
            for column in _affected_columns(row, config):
                if column in column_index:
                    worksheet.cell(row=row_number, column=column_index[column]).fill = PARAMETER_FILL


def _write_config_sheet(
    worksheet: Worksheet,
    config: dict[str, Any],
    summary: dict[str, Any],
) -> None:
    worksheet.append(["配置项", "值"])
    for key, value in config.items():
        worksheet.append([key, value])
    worksheet.append([])
    worksheet.append(["运行信息", "值"])
    for key, value in summary.items():
        worksheet.append([key, value])
    _style_tabular_sheet(worksheet)


def _style_tabular_sheet(worksheet: Worksheet) -> None:
    if worksheet.max_row >= 1:
        for cell in worksheet[1]:
            cell.fill = HEADER_FILL
            cell.font = HEADER_FONT
            cell.alignment = Alignment(horizontal="center", vertical="center")

    for row in worksheet.iter_rows():
        for cell in row:
            cell.border = THIN_BORDER
            cell.alignment = Alignment(vertical="top", wrap_text=True)

    worksheet.freeze_panes = "A2"
    if worksheet.max_column > 1 and worksheet.max_row > 1:
        worksheet.auto_filter.ref = worksheet.dimensions

    for column_cells in worksheet.columns:
        letter = get_column_letter(column_cells[0].column)
        width = 10
        for cell in column_cells:
            value = "" if cell.value is None else str(cell.value)
            width = max(width, min(len(value) + 2, 42))
        worksheet.column_dimensions[letter].width = width

    worksheet.sheet_view.showGridLines = False


def _row_text(row: pd.Series) -> str:
    return " ".join(
        str(row.get(column, ""))
        for column in ["异常类型", "异常详情", "异常值"]
        if pd.notna(row.get(column, ""))
    )


def _as_float(value: Any) -> float | None:
    try:
        if pd.isna(value):
            return None
        return float(value)
    except (TypeError, ValueError):
        return None


def _excel_value(value: Any) -> Any:
    if pd.isna(value):
        return None
    return value


def _split_types(value: str) -> list[str]:
    return [item.strip() for item in re.split(r"[;；|]", value) if item.strip()]


def _type_counts(values: pd.Series) -> list[tuple[str, int]]:
    counts: dict[str, int] = {}
    for value in values:
        for item in _split_types(str(value)):
            counts[item] = counts.get(item, 0) + 1
    return sorted(counts.items(), key=lambda item: item[1], reverse=True)


def _first_non_empty(series: pd.Series | None) -> Any:
    if series is None:
        return ""
    for value in series:
        if pd.notna(value) and str(value) != "":
            return value
    return ""


def _last_non_empty(series: pd.Series | None) -> Any:
    if series is None:
        return ""
    for value in reversed(series.tolist()):
        if pd.notna(value) and str(value) != "":
            return value
    return ""


def _max_severity(severities: list[str]) -> str:
    order = {"高": 0, "中": 1, "低": 2}
    if not severities:
        return "低"
    return min(severities, key=lambda item: order.get(item, 9))


def _priority_from_severity(severity: str) -> str:
    return {"高": "P1", "中": "P2", "低": "P3"}.get(severity, "P3")

"""Directory-level batch detection helpers."""
from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import date, datetime
from pathlib import Path
from typing import Any, Iterable

import pandas as pd

from .excel_report import write_excel_report_from_context
from .pipeline import check_anomaly_in_file
from .pipeline import _extract_building_and_transformer
from .report_model import ReportContext, build_report_context, enrich_anomalies
from .settings import DEFAULT_CONFIG, split_config

BatchEventHandler = Callable[[dict[str, Any]], None]


@dataclass
class BatchDetectionResult:
    input_dir: Path
    output_dir: Path
    total_files: int = 0
    processed_files: int = 0
    normal_files: int = 0
    skipped_files: int = 0
    anomaly_files: int = 0
    anomaly_records: int = 0
    report_path: Path | None = None
    report_context: ReportContext | None = None
    messages: list[str] = field(default_factory=list)
    sensor_status_rows: list[dict[str, Any]] = field(default_factory=list)
    skipped_details: list[dict[str, Any]] = field(default_factory=list)
    generated_at: str = ""
    duration_seconds: float = 0.0


def iter_csv_files(input_dir: str | Path) -> list[Path]:
    """Return CSV files below a directory in deterministic order."""
    root = Path(input_dir)
    return sorted(path for path in root.rglob("*.csv") if path.is_file())


def run_batch_detection(
    input_dir: str | Path,
    output_dir: str | Path | None = None,
    config: dict[str, Any] | None = None,
    write_report: bool = True,
    event_handler: BatchEventHandler | None = None,
) -> BatchDetectionResult:
    """Run detection on every CSV file below ``input_dir``."""
    root = Path(input_dir)
    report_dir = Path(output_dir) if output_dir else root
    thresholds, enabled_rules = split_config(config or DEFAULT_CONFIG)
    files = iter_csv_files(root)
    generated_at = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    started_at = time.perf_counter()
    result = BatchDetectionResult(
        input_dir=root,
        output_dir=report_dir,
        total_files=len(files),
        generated_at=generated_at,
    )
    _emit(
        event_handler,
        {
            "event": "run_started",
            "input_dir": str(root),
            "output_dir": str(report_dir),
            "total_files": result.total_files,
            "write_report": write_report,
            "generated_at": generated_at,
        },
    )

    anomaly_frames: list[pd.DataFrame] = []

    for path in files:
        building, transformer = _extract_building_and_transformer(str(path), str(root))
        relative_path = str(path.relative_to(root)) if path.is_relative_to(root) else path.name
        status = "normal"
        message = ""
        anomaly_count = 0
        anomaly_types = ""
        extra_info: dict[str, Any] = {}

        try:
            anomalies, log_data, _cleaned_df, extra_info = check_anomaly_in_file(
                str(path),
                thresholds,
                enabled_rules,
            )
        except Exception as exc:
            anomalies = pd.DataFrame()
            log_data = f"读取失败 {path.name}: {type(exc).__name__}: {exc}"

        result.processed_files += 1

        if extra_info:
            result.sensor_status_rows.append(
                {
                    "来源文件": path.name,
                    "建筑": building,
                    "相对路径": relative_path,
                    "变压器": transformer,
                    "是否离线": "是" if extra_info.get("is_offline") else "否",
                    "传感器故障": "、".join(extra_info.get("sensor_faults", [])),
                    "传感器未配置": "、".join(extra_info.get("sensor_missing", [])),
                }
            )

        if isinstance(log_data, dict):
            count = int(log_data.get("count", 0))
            anomaly_count = count
            anomaly_types = str(log_data.get("types", ""))
            if count > 0:
                status = "anomaly"
                message = f"异常 {path.name}: {count} 条 [{anomaly_types}]"
                result.anomaly_files += 1
                result.anomaly_records += count
                result.messages.append(message)
                if anomalies is not None and not anomalies.empty:
                    anomaly_frames.append(
                        enrich_anomalies(anomalies, building, transformer, relative_path)
                    )
            else:
                message = f"正常 {path.name}"
                result.normal_files += 1
                result.messages.append(message)
        else:
            status = "skipped"
            message = str(log_data)
            result.skipped_files += 1
            result.messages.append(message)
            result.skipped_details.append(
                {
                    "来源文件": path.name,
                    "建筑": building,
                    "相对路径": relative_path,
                    "变压器": transformer,
                    "状态": "跳过",
                    "原因": str(log_data),
                }
            )
        _emit(
            event_handler,
            {
                "event": "file_result",
                "source_file": path.name,
                "relative_path": relative_path,
                "building": building,
                "transformer": transformer,
                "status": status,
                "message": message,
                "anomaly_count": anomaly_count,
                "anomaly_types": anomaly_types,
                "processed_files": result.processed_files,
                "total_files": result.total_files,
            },
        )
        _emit(
            event_handler,
            {
                "event": "file_progress",
                "source_file": path.name,
                "processed_files": result.processed_files,
                "total_files": result.total_files,
                "percent": (
                    result.processed_files / result.total_files
                    if result.total_files
                    else 1.0
                ),
            },
        )

    has_reportable_findings = bool(
        anomaly_frames or result.sensor_status_rows or result.skipped_details
    )
    if has_reportable_findings:
        summary = {
            "input_dir": str(root),
            "output_dir": str(report_dir),
            "total_files": result.total_files,
            "processed_files": result.processed_files,
            "normal_files": result.normal_files,
            "anomaly_files": result.anomaly_files,
            "anomaly_records": result.anomaly_records,
            "skipped_files": result.skipped_files,
            "generated_at": generated_at,
        }
        anomaly_data = (
            pd.concat(anomaly_frames, ignore_index=True, sort=False)
            if anomaly_frames
            else pd.DataFrame()
        )
        result.report_context = build_report_context(
            anomaly_data,
            summary=summary,
            config=config or DEFAULT_CONFIG,
            sensor_status_rows=result.sensor_status_rows,
            skipped_details=result.skipped_details,
            messages=result.messages,
        )
        _emit(event_handler, _build_report_summary_event(result.report_context))

    if write_report and result.report_context is not None:
        report_dir.mkdir(parents=True, exist_ok=True)
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        report_path = report_dir / f"电气异常报告_{ts}.xlsx"
        result.report_path = write_excel_report_from_context(
            report_path,
            result.report_context,
        )
        _emit(
            event_handler,
            {
                "event": "report_written",
                "report_path": str(result.report_path),
                "anomaly_records": result.anomaly_records,
            },
        )

    result.duration_seconds = time.perf_counter() - started_at
    _emit(
        event_handler,
        {
            "event": "run_completed",
            "input_dir": str(result.input_dir),
            "output_dir": str(result.output_dir),
            "total_files": result.total_files,
            "processed_files": result.processed_files,
            "normal_files": result.normal_files,
            "anomaly_files": result.anomaly_files,
            "anomaly_records": result.anomaly_records,
            "skipped_files": result.skipped_files,
            "report_path": str(result.report_path) if result.report_path else None,
            "duration_seconds": result.duration_seconds,
            "generated_at": result.generated_at,
        },
    )
    return result


def summarize_messages(messages: Iterable[str], limit: int = 20) -> str:
    """Return a compact printable message block."""
    lines = list(messages)
    if len(lines) <= limit:
        return "\n".join(lines)
    shown = lines[:limit]
    shown.append(f"... 还有 {len(lines) - limit} 行")
    return "\n".join(shown)


def _emit(event_handler: BatchEventHandler | None, event: dict[str, Any]) -> None:
    if event_handler is not None:
        event_handler(event)


def _build_report_summary_event(context: ReportContext) -> dict[str, Any]:
    sensor_status = context.sensor_status
    sensor_overview = {
        "total_rows": int(len(sensor_status)),
        "offline_devices": _count_text_value(sensor_status, "是否离线", "是"),
        "sensor_fault_rows": _count_non_empty(sensor_status, "传感器故障"),
        "sensor_missing_rows": _count_non_empty(sensor_status, "传感器未配置"),
        "skipped_rows": _count_text_value(sensor_status, "状态", "跳过"),
    }
    return {
        "event": "report_summary",
        "device_count": int(len(context.device_summary)),
        "high_risk_devices": _records_from_dataframe(
            context.high_risk_devices,
            limit=10,
            columns={
                "建筑": "building",
                "变压器": "transformer",
                "异常记录数": "anomaly_records",
                "主要异常类型": "main_issue_types",
                "最高严重等级": "highest_severity",
                "建议优先级": "priority",
            },
        ),
        "top_issue_types": _records_from_dataframe(
            context.type_statistics[context.type_statistics["统计维度"] == "异常类型"]
            if not context.type_statistics.empty
            else context.type_statistics,
            limit=8,
            columns={
                "名称": "name",
                "数量": "count",
            },
        ),
        "sensor_overview": sensor_overview,
        "detail_preview_count": int(len(context.details)),
        "detail_preview": _records_from_dataframe(
            _detail_preview_dataframe(context.details),
            limit=100,
            columns={
                "建筑": "building",
                "变压器": "transformer",
                "相对路径": "relative_path",
                "来源文件": "source_file",
                "日期": "date",
                "时间": "time",
                "严重等级": "severity",
                "异常类型": "issue_type",
                "异常详情": "issue_detail",
                "异常值": "issue_value",
                "建议处置": "recommended_action",
            },
        ),
    }


def _records_from_dataframe(
    dataframe: pd.DataFrame,
    limit: int,
    columns: dict[str, str],
) -> list[dict[str, Any]]:
    if dataframe is None or dataframe.empty:
        return []

    records: list[dict[str, Any]] = []
    for _, row in dataframe.head(limit).iterrows():
        item: dict[str, Any] = {}
        for source, target in columns.items():
            if source in row.index:
                item[target] = _json_value(row[source])
        records.append(item)
    return records


def _json_value(value: Any) -> Any:
    if value is None:
        return None
    try:
        if pd.isna(value):
            return None
    except (TypeError, ValueError):
        pass
    if hasattr(value, "item"):
        value = value.item()
    if isinstance(value, datetime):
        return value.isoformat(sep=" ", timespec="seconds")
    if isinstance(value, date):
        return value.isoformat()
    return value


def _detail_preview_dataframe(dataframe: pd.DataFrame) -> pd.DataFrame:
    if dataframe is None or dataframe.empty or "严重等级" not in dataframe.columns:
        return dataframe

    preview = dataframe.copy()
    preview["_severity_rank"] = preview["严重等级"].map({"高": 0, "中": 1, "低": 2}).fillna(3)
    sorted_preview = preview.sort_values("_severity_rank", kind="stable")
    return sorted_preview.drop(columns=["_severity_rank"])


def _count_text_value(dataframe: pd.DataFrame, column: str, value: str) -> int:
    if dataframe is None or dataframe.empty or column not in dataframe.columns:
        return 0
    return int((dataframe[column].fillna("").astype(str) == value).sum())


def _count_non_empty(dataframe: pd.DataFrame, column: str) -> int:
    if dataframe is None or dataframe.empty or column not in dataframe.columns:
        return 0
    values = dataframe[column].fillna("").astype(str).str.strip()
    return int((values != "").sum())

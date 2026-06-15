"""Directory-level batch detection helpers."""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable

import pandas as pd

from .excel_report import enrich_anomalies, write_excel_report
from .pipeline import check_anomaly_in_file
from .pipeline import _extract_building_and_transformer
from .settings import DEFAULT_CONFIG, split_config


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
    messages: list[str] = field(default_factory=list)
    sensor_status_rows: list[dict[str, Any]] = field(default_factory=list)
    skipped_details: list[dict[str, Any]] = field(default_factory=list)


def iter_csv_files(input_dir: str | Path) -> list[Path]:
    """Return CSV files below a directory in deterministic order."""
    root = Path(input_dir)
    return sorted(path for path in root.rglob("*.csv") if path.is_file())


def run_batch_detection(
    input_dir: str | Path,
    output_dir: str | Path | None = None,
    config: dict[str, Any] | None = None,
    write_report: bool = True,
) -> BatchDetectionResult:
    """Run detection on every CSV file below ``input_dir``."""
    root = Path(input_dir)
    report_dir = Path(output_dir) if output_dir else root
    thresholds, enabled_rules = split_config(config or DEFAULT_CONFIG)
    files = iter_csv_files(root)
    result = BatchDetectionResult(
        input_dir=root,
        output_dir=report_dir,
        total_files=len(files),
    )

    anomaly_frames: list[pd.DataFrame] = []

    for path in files:
        anomalies, log_data, _cleaned_df, extra_info = check_anomaly_in_file(
            str(path),
            thresholds,
            enabled_rules,
        )
        result.processed_files += 1
        building, transformer = _extract_building_and_transformer(str(path), str(root))
        relative_path = str(path.relative_to(root)) if path.is_relative_to(root) else path.name

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
            if count > 0:
                result.anomaly_files += 1
                result.anomaly_records += count
                result.messages.append(
                    f"异常 {path.name}: {count} 条 [{log_data.get('types', '')}]"
                )
                if anomalies is not None and not anomalies.empty:
                    anomaly_frames.append(
                        enrich_anomalies(anomalies, building, transformer, relative_path)
                    )
            else:
                result.normal_files += 1
                result.messages.append(f"正常 {path.name}")
        else:
            result.skipped_files += 1
            result.messages.append(str(log_data))
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

    if write_report and anomaly_frames:
        report_dir.mkdir(parents=True, exist_ok=True)
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        report_path = report_dir / f"电气异常报告_{ts}.xlsx"
        result.report_path = write_excel_report(
            report_path,
            pd.concat(anomaly_frames, ignore_index=True, sort=False),
            summary={
                "input_dir": str(root),
                "output_dir": str(report_dir),
                "total_files": result.total_files,
                "processed_files": result.processed_files,
                "normal_files": result.normal_files,
                "anomaly_files": result.anomaly_files,
                "anomaly_records": result.anomaly_records,
                "skipped_files": result.skipped_files,
                "generated_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            },
            config=config or DEFAULT_CONFIG,
            sensor_status_rows=result.sensor_status_rows,
            skipped_details=result.skipped_details,
            messages=result.messages,
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

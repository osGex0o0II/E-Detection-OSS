from __future__ import annotations

import pandas as pd

from e_detection.report_model import build_report_context, enrich_anomalies
from e_detection.settings import DEFAULT_CONFIG


def _summary() -> dict[str, object]:
    return {
        "input_dir": "input",
        "output_dir": "output",
        "total_files": 1,
        "processed_files": 1,
        "normal_files": 0,
        "anomaly_files": 1,
        "anomaly_records": 1,
        "skipped_files": 0,
        "generated_at": "2026-06-15 00:00:00",
    }


def test_build_report_context_summarizes_devices_and_types():
    anomalies = pd.DataFrame(
        [
            {
                "来源文件": "demo.csv",
                "日期": "2025-09-06",
                "异常类型": "电压异常; 疑似PT接线异常",
                "异常详情": "Uab过低",
                "异常值": "Uab=320",
                "时间": "0时",
                "Uab": 320.0,
            }
        ]
    )
    enriched = enrich_anomalies(anomalies, "建筑A", "1TM1", "demo.csv")

    context = build_report_context(enriched, _summary(), DEFAULT_CONFIG)

    assert context.run.anomaly_records == 1
    assert context.details.loc[0, "严重等级"] == "高"
    assert context.device_summary.loc[0, "建议优先级"] == "P1"
    assert "疑似PT接线异常" in context.device_summary.loc[0, "主要异常类型"]
    assert "电压异常" in set(context.type_statistics["名称"])


def test_build_report_context_keeps_sensor_and_skipped_rows():
    context = build_report_context(
        pd.DataFrame(),
        _summary(),
        DEFAULT_CONFIG,
        sensor_status_rows=[{"来源文件": "a.csv", "是否离线": "否", "传感器未配置": "功率因数"}],
        skipped_details=[{"来源文件": "b.csv", "状态": "跳过", "原因": "跳过高压设备"}],
    )

    assert len(context.sensor_status) == 2
    assert "传感器未配置" in context.sensor_status.columns

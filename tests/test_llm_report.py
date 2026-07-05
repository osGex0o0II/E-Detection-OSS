from __future__ import annotations

import pandas as pd

from e_detection.llm_context import build_llm_messages, build_llm_prompt
from e_detection.llm_report import generate_llm_report
from e_detection.report_model import build_report_context, enrich_anomalies
from e_detection.settings import DEFAULT_CONFIG


def _context():
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
    return build_report_context(
        enriched,
        {
            "input_dir": "input",
            "output_dir": "output",
            "total_files": 1,
            "processed_files": 1,
            "normal_files": 0,
            "anomaly_files": 1,
            "anomaly_records": 1,
            "skipped_files": 0,
            "generated_at": "2026-06-15 00:00:00",
        },
        DEFAULT_CONFIG,
    )


def test_build_llm_prompt_contains_facts_not_raw_data():
    prompt = build_llm_prompt(_context())

    assert "不要编造未给出的数据" in prompt
    assert "异常记录: 1" in prompt
    assert "建筑A / 1TM1" in prompt
    assert "疑似PT接线异常" in prompt


def test_build_llm_messages_uses_system_and_user_roles():
    messages = build_llm_messages(_context())

    assert [message["role"] for message in messages] == ["system", "user"]
    assert "电气运维诊断助手" in messages[0]["content"]


def test_generate_llm_report_template_markdown():
    report = generate_llm_report(_context())

    assert report.startswith("# 电气运行异常巡检报告")
    assert "高优先级问题" in report
    assert "建筑A / 1TM1" in report

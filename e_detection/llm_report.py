"""LLM report generation facade.

The current implementation returns a deterministic Markdown report. A future
embedded LLM client can be injected without changing detection or Excel logic.
"""
from __future__ import annotations

from typing import Protocol

import pandas as pd

from .llm_context import build_llm_messages
from .report_model import ReportContext


class LLMClient(Protocol):
    def generate(self, messages: list[dict[str, str]]) -> str:
        """Generate a report from chat-style messages."""


def generate_llm_report(context: ReportContext, client: LLMClient | None = None) -> str:
    """Generate a Markdown inspection report from the internal report context."""
    if client is not None:
        return client.generate(build_llm_messages(context))
    return generate_template_report(context)


def generate_template_report(context: ReportContext) -> str:
    """Fallback report used before a concrete embedded LLM client is wired in."""
    run = context.run
    lines = [
        "# 电气运行异常巡检报告",
        "",
        "## 检测结论",
        (
            f"本次共检测 {run.total_files} 个 CSV 运行日报文件，"
            f"处理 {run.processed_files} 个，跳过 {run.skipped_files} 个，"
            f"发现 {run.anomaly_files} 个异常文件、{run.anomaly_records} 条异常记录。"
        ),
        "",
        "## 高优先级问题",
    ]

    high = context.high_risk_devices
    if high.empty:
        lines.append("未发现高严重等级异常设备。")
    else:
        for row in high.head(10).itertuples(index=False):
            row_data = row._asdict()
            lines.append(
                f"- **{row_data.get('建筑')} / {row_data.get('变压器')}**: "
                f"{row_data.get('主要异常类型')}，"
                f"{row_data.get('异常记录数')} 条，"
                f"时间范围 {row_data.get('首次异常时间')} 至 {row_data.get('末次异常时间')}。"
            )

    lines.extend(["", "## 分设备处置建议"])
    if context.device_summary.empty:
        lines.append("无异常设备需要处置。")
    else:
        for row in context.device_summary.head(20).itertuples(index=False):
            row_data = row._asdict()
            lines.append(
                f"- {row_data.get('建议优先级')} "
                f"{row_data.get('建筑')} / {row_data.get('变压器')}: "
                f"{row_data.get('主要异常类型')}。"
            )

    lines.extend(["", "## 采集/传感器问题"])
    notable = _notable_sensor_rows(context)
    if not notable:
        lines.append("未记录明显采集或传感器问题。")
    else:
        lines.extend(notable)

    lines.extend(
        [
            "",
            "## 后续复核建议",
            "- 优先复核 P1 设备的 PT/CT 接线、采样通道配置和现场运行方式。",
            "- 对持续 24 小时出现的同类异常，建议结合现场巡检记录确认是否为真实运行异常。",
            "- 对采集冻结、离线、传感器缺失类问题，先排查采集链路，再判定电气本体风险。",
        ]
    )
    return "\n".join(lines) + "\n"


def _notable_sensor_rows(context: ReportContext) -> list[str]:
    if context.sensor_status.empty:
        return []
    rows: list[str] = []
    for row in context.sensor_status.itertuples(index=False):
        row_data = row._asdict()
        bits = []
        for key in ["是否离线", "传感器故障", "传感器未配置", "原因"]:
            value = row_data.get(key)
            if _has_value(value) and not (key == "是否离线" and value != "是"):
                bits.append(f"{key}: {value}")
        if bits:
            rows.append(
                f"- {row_data.get('建筑', '')} / {row_data.get('变压器', '')} "
                f"({row_data.get('来源文件', '')}): {'; '.join(bits)}"
            )
    return rows[:20]


def _has_value(value: object) -> bool:
    if value is None:
        return False
    try:
        if pd.isna(value):
            return False
    except (TypeError, ValueError):
        pass
    return str(value).strip() != ""

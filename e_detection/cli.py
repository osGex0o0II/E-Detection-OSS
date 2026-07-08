"""Command-line entry point for E-Detection."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

from .batch import run_batch_detection, summarize_messages
from .llm_report import generate_llm_report
from .settings import load_config


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="E-Detection 电气参数异常检测")
    parser.add_argument("input_dir", help="包含 CSV 运行日报的目录")
    parser.add_argument(
        "-o",
        "--output-dir",
        help="报告输出目录，默认写入输入目录",
    )
    parser.add_argument(
        "-c",
        "--config",
        default="config.json",
        help="阈值和规则配置文件，默认 config.json",
    )
    parser.add_argument(
        "--no-report",
        action="store_true",
        help="只打印检测摘要，不生成 Excel 报告",
    )
    parser.add_argument(
        "--show-lines",
        type=int,
        default=20,
        help="最多打印多少行逐文件结果",
    )
    parser.add_argument(
        "--llm-summary",
        action="store_true",
        help="基于内部事实模型生成智能巡检报告预览",
    )
    parser.add_argument(
        "--json-events",
        action="store_true",
        help="以 JSON Lines 输出检测事件，供原生桌面壳进程桥接使用",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.json_events:
        _configure_json_event_output()
        try:
            config = load_config(args.config)
            result = run_batch_detection(
                input_dir=args.input_dir,
                output_dir=args.output_dir,
                config=config,
                write_report=not args.no_report,
                event_handler=_print_json_event,
            )
            if args.llm_summary:
                if result.report_context is None:
                    _print_json_event(
                        {
                            "event": "llm_report_skipped",
                            "reason": "未发现异常事实，暂不生成。",
                        }
                    )
                else:
                    _print_json_event(
                        {
                            "event": "llm_report",
                            "content": generate_llm_report(result.report_context),
                        }
                    )
            return 0
        except Exception as exc:
            _print_json_event(
                {
                    "event": "error",
                    "error_type": type(exc).__name__,
                    "message": str(exc),
                }
            )
            return 1

    config = load_config(args.config)
    result = run_batch_detection(
        input_dir=args.input_dir,
        output_dir=args.output_dir,
        config=config,
        write_report=not args.no_report,
    )

    print(summarize_messages(result.messages, limit=args.show_lines))
    print()
    print(
        "汇总: "
        f"{result.processed_files}/{result.total_files} 文件, "
        f"正常 {result.normal_files}, "
        f"异常文件 {result.anomaly_files}, "
        f"异常记录 {result.anomaly_records}, "
        f"跳过 {result.skipped_files}"
    )
    if result.report_path:
        print(f"报告: {Path(result.report_path)}")
    if args.llm_summary:
        if result.report_context is None:
            print()
            print("智能巡检报告: 未发现异常事实，暂不生成。")
        else:
            print()
            print(generate_llm_report(result.report_context))
    return 0


def _print_json_event(event: dict[str, Any]) -> None:
    print(json.dumps(event, ensure_ascii=False, sort_keys=True), flush=True)


def _configure_json_event_output() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")


if __name__ == "__main__":
    raise SystemExit(main())

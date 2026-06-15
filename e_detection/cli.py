"""Command-line entry point for E-Detection."""
from __future__ import annotations

import argparse
from pathlib import Path

from .batch import run_batch_detection, summarize_messages
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
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

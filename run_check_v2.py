# -*- coding: utf-8 -*-
"""Compare baseline and full rule sets on a CSV directory."""
from __future__ import annotations

import argparse
from pathlib import Path

from e_detection.batch import run_batch_detection
from e_detection.settings import DEFAULT_CONFIG, load_config


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="E-Detection A/B 规则方案对比")
    parser.add_argument("input_dir", help="包含 CSV 运行日报的目录")
    parser.add_argument("-c", "--config", default="config.json", help="配置文件路径")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    config = load_config(args.config)

    baseline_config = dict(config)
    baseline_config.update(
        {
            "current_overload": True,
            "current_unbalance": False,
            "power_factor": False,
            "detail_output": True,
        }
    )

    full_config = dict(config or DEFAULT_CONFIG)
    full_config.update(
        {
            "current_overload": True,
            "current_unbalance": True,
            "power_factor": True,
            "detail_output": True,
        }
    )

    baseline = run_batch_detection(args.input_dir, config=baseline_config, write_report=False)
    full = run_batch_detection(args.input_dir, config=full_config, write_report=False)

    print("方案 A: 基础规则")
    print(
        f"  文件 {baseline.processed_files}/{baseline.total_files}, "
        f"异常文件 {baseline.anomaly_files}, 异常记录 {baseline.anomaly_records}, "
        f"跳过 {baseline.skipped_files}"
    )
    print()
    print("方案 B: 全部可选规则")
    print(
        f"  文件 {full.processed_files}/{full.total_files}, "
        f"异常文件 {full.anomaly_files}, 异常记录 {full.anomaly_records}, "
        f"跳过 {full.skipped_files}"
    )
    print()
    print(f"差异: +{full.anomaly_records - baseline.anomaly_records} 条异常记录")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

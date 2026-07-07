from __future__ import annotations

import json
from pathlib import Path

import pandas as pd

from e_detection import batch, cli
from e_detection.batch import BatchDetectionResult
from e_detection.settings import DEFAULT_CONFIG


def test_run_batch_detection_emits_json_ready_events(tmp_path: Path, monkeypatch):
    csv_path = tmp_path / "demo.csv"
    csv_path.write_text("time,Uab\n0,380\n", encoding="utf-8")

    def fake_check_anomaly_in_file(file_path, thresholds, enabled_rules):
        return (
            pd.DataFrame(),
            {"count": 0, "types": "", "filename": Path(file_path).name},
            None,
            {},
        )

    monkeypatch.setattr(batch, "check_anomaly_in_file", fake_check_anomaly_in_file)
    events: list[dict[str, object]] = []

    result = batch.run_batch_detection(
        tmp_path,
        config=DEFAULT_CONFIG,
        write_report=False,
        event_handler=events.append,
    )

    assert result.processed_files == 1
    assert [event["event"] for event in events] == [
        "run_started",
        "file_result",
        "file_progress",
        "run_completed",
    ]
    assert events[0]["total_files"] == 1
    assert events[1]["source_file"] == "demo.csv"
    assert events[1]["status"] == "normal"
    assert events[-1]["processed_files"] == 1
    assert events[-1]["report_path"] is None


def test_cli_json_events_outputs_jsonl_only(tmp_path: Path, monkeypatch, capsys):
    def fake_run_batch_detection(
        input_dir,
        output_dir=None,
        config=None,
        write_report=True,
        event_handler=None,
    ):
        event_handler({"event": "run_started", "total_files": 0})
        event_handler({"event": "run_completed", "processed_files": 0})
        return BatchDetectionResult(
            input_dir=Path(input_dir),
            output_dir=Path(output_dir) if output_dir else Path(input_dir),
        )

    monkeypatch.setattr(cli, "run_batch_detection", fake_run_batch_detection)

    exit_code = cli.main(
        [
            "--json-events",
            "--no-report",
            "--config",
            str(tmp_path / "missing-config.json"),
            str(tmp_path),
        ]
    )

    assert exit_code == 0
    lines = capsys.readouterr().out.strip().splitlines()
    assert [json.loads(line)["event"] for line in lines] == [
        "run_started",
        "run_completed",
    ]
    assert "汇总" not in "\n".join(lines)


def test_cli_json_events_reports_invalid_config(tmp_path: Path, capsys):
    config_path = tmp_path / "broken-config.json"
    config_path.write_text("{not-json", encoding="utf-8")

    exit_code = cli.main(
        [
            "--json-events",
            "--no-report",
            "--config",
            str(config_path),
            str(tmp_path),
        ]
    )

    assert exit_code == 1
    events = [json.loads(line) for line in capsys.readouterr().out.strip().splitlines()]
    assert events == [
        {
            "event": "error",
            "error_type": "ValueError",
            "message": f"配置文件无法解析: {config_path} (Expecting property name enclosed in double quotes)",
        }
    ]


def test_run_batch_detection_emits_native_report_summary(tmp_path: Path, monkeypatch):
    csv_path = tmp_path / "demo.csv"
    csv_path.write_text("time,Uab\n0,320\n", encoding="utf-8")

    anomalies = pd.DataFrame(
        [
            {
                "来源文件": "demo.csv",
                "日期": "2026-07-03",
                "异常类型": "疑似PT接线异常; 电压异常",
                "异常详情": "Uab过低",
                "异常值": "Uab=320",
                "时间": "0时",
                "Uab": 320.0,
            }
        ]
    )

    def fake_check_anomaly_in_file(file_path, thresholds, enabled_rules):
        return (
            anomalies,
            {
                "count": 1,
                "types": "疑似PT接线异常; 电压异常",
                "filename": Path(file_path).name,
            },
            None,
            {"is_offline": False, "sensor_faults": [], "sensor_missing": ["功率因数"]},
        )

    monkeypatch.setattr(batch, "check_anomaly_in_file", fake_check_anomaly_in_file)
    events: list[dict[str, object]] = []

    batch.run_batch_detection(
        tmp_path,
        config=DEFAULT_CONFIG,
        write_report=False,
        event_handler=events.append,
    )

    summary = next(event for event in events if event["event"] == "report_summary")
    assert json.loads(json.dumps(summary, ensure_ascii=False))["event"] == "report_summary"
    assert summary["device_count"] == 1
    assert summary["high_risk_devices"][0]["building"] == "根目录"
    assert summary["high_risk_devices"][0]["highest_severity"] == "高"
    assert summary["top_issue_types"][0]["count"] == 1
    assert summary["sensor_overview"]["sensor_missing_rows"] == 1
    assert summary["detail_preview_count"] == 1
    assert summary["detail_preview"][0]["source_file"] == "demo.csv"
    assert summary["detail_preview"][0]["severity"] == "高"
    assert summary["detail_preview"][0]["issue_value"] == "Uab=320"
    assert summary["detail_preview"][0]["recommended_action"].startswith("优先检查 PT")


def test_run_batch_detection_continues_after_single_file_failure(tmp_path: Path, monkeypatch):
    bad_csv = tmp_path / "bad.csv"
    good_csv = tmp_path / "good.csv"
    bad_csv.write_text("time,Uab\n0,broken\n", encoding="utf-8")
    good_csv.write_text("time,Uab\n0,380\n", encoding="utf-8")

    def fake_check_anomaly_in_file(file_path, thresholds, enabled_rules):
        if Path(file_path).name == "bad.csv":
            raise ValueError("broken input")
        return (
            pd.DataFrame(),
            {"count": 0, "types": "", "filename": Path(file_path).name},
            None,
            {},
        )

    monkeypatch.setattr(batch, "check_anomaly_in_file", fake_check_anomaly_in_file)
    events: list[dict[str, object]] = []

    result = batch.run_batch_detection(
        tmp_path,
        config=DEFAULT_CONFIG,
        write_report=False,
        event_handler=events.append,
    )

    assert result.total_files == 2
    assert result.processed_files == 2
    assert result.normal_files == 1
    assert result.skipped_files == 1
    assert result.skipped_details[0]["来源文件"] == "bad.csv"
    assert "broken input" in result.skipped_details[0]["原因"]
    assert [event["status"] for event in events if event["event"] == "file_result"] == [
        "skipped",
        "normal",
    ]


def test_run_batch_detection_reports_summary_for_skipped_only_run(tmp_path: Path, monkeypatch):
    csv_path = tmp_path / "bad.csv"
    csv_path.write_text("time,Uab\n0,broken\n", encoding="utf-8")

    def fake_check_anomaly_in_file(file_path, thresholds, enabled_rules):
        raise ValueError("broken input")

    monkeypatch.setattr(batch, "check_anomaly_in_file", fake_check_anomaly_in_file)
    events: list[dict[str, object]] = []

    result = batch.run_batch_detection(
        tmp_path,
        config=DEFAULT_CONFIG,
        write_report=False,
        event_handler=events.append,
    )

    assert result.skipped_files == 1
    assert result.report_context is not None
    summary = next(event for event in events if event["event"] == "report_summary")
    assert summary["sensor_overview"]["skipped_rows"] == 1
    assert summary["detail_preview_count"] == 0
    assert events[-1]["skipped_files"] == 1


def test_run_batch_detection_reports_summary_for_sensor_only_issue(tmp_path: Path, monkeypatch):
    csv_path = tmp_path / "sensor.csv"
    csv_path.write_text("time,Uab\n0,380\n", encoding="utf-8")

    def fake_check_anomaly_in_file(file_path, thresholds, enabled_rules):
        return (
            pd.DataFrame(),
            {"count": 0, "types": "", "filename": Path(file_path).name},
            None,
            {"is_offline": False, "sensor_faults": [], "sensor_missing": ["功率因数"]},
        )

    monkeypatch.setattr(batch, "check_anomaly_in_file", fake_check_anomaly_in_file)
    events: list[dict[str, object]] = []

    result = batch.run_batch_detection(
        tmp_path,
        config=DEFAULT_CONFIG,
        write_report=False,
        event_handler=events.append,
    )

    assert result.normal_files == 1
    assert result.report_context is not None
    summary = next(event for event in events if event["event"] == "report_summary")
    assert summary["sensor_overview"]["sensor_missing_rows"] == 1
    assert summary["detail_preview_count"] == 0

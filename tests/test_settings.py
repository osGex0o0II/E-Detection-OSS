from __future__ import annotations

import math

import pytest

from e_detection.settings import DEFAULT_CONFIG, load_config, normalize_config, split_config


def test_normalize_config_uses_defaults_for_invalid_values():
    config = normalize_config(
        {
            "V_MIN_THRESHOLD": "bad",
            "V_MAX_THRESHOLD": math.nan,
            "current_overload": False,
            "detail_output": True,
        }
    )

    assert config["V_MIN_THRESHOLD"] == DEFAULT_CONFIG["V_MIN_THRESHOLD"]
    assert config["V_MAX_THRESHOLD"] == DEFAULT_CONFIG["V_MAX_THRESHOLD"]
    assert config["current_overload"] is False
    assert config["detail_output"] is True


def test_split_config_separates_thresholds_and_rule_switches():
    thresholds, rules = split_config({"FREEZE_COUNT_THRESHOLD": "5", "power_factor": True})

    assert thresholds["FREEZE_COUNT_THRESHOLD"] == 5
    assert isinstance(thresholds["FREEZE_COUNT_THRESHOLD"], int)
    assert rules["power_factor"] is True
    assert "V_MIN_THRESHOLD" in thresholds
    assert "detail_output" in rules


def test_old_current_switch_still_maps_to_new_rule_keys():
    config = normalize_config({"current": True})

    assert config["current_overload"] is True
    assert config["current_unbalance"] is True


def test_load_config_uses_defaults_when_missing(tmp_path):
    config = load_config(tmp_path / "missing.json")

    assert config == DEFAULT_CONFIG


def test_load_config_rejects_invalid_json(tmp_path):
    config_path = tmp_path / "broken.json"
    config_path.write_text("{not-json", encoding="utf-8")

    with pytest.raises(ValueError, match="配置文件无法解析"):
        load_config(config_path)


def test_load_config_rejects_non_object_root(tmp_path):
    config_path = tmp_path / "array.json"
    config_path.write_text("[]", encoding="utf-8")

    with pytest.raises(ValueError, match="根节点必须是对象"):
        load_config(config_path)

"""Configuration defaults and validation helpers."""
from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any

RULE_CONFIG_KEYS = (
    "current_overload",
    "current_unbalance",
    "power_factor",
    "detail_output",
)

THRESHOLD_KEYS = (
    "V_MIN_THRESHOLD",
    "V_MAX_THRESHOLD",
    "I_MAX_THRESHOLD",
    "I_UNBALANCE_MAX_THRESHOLD",
    "P_ACTIVE_MIN_THRESHOLD",
    "PF_MIN_THRESHOLD",
    "T_MIN_THRESHOLD",
    "T_MAX_THRESHOLD",
    "I_MIN_ACTIVE_THRESHOLD",
    "FREEZE_COUNT_THRESHOLD",
    "FREEZE_STD_THRESHOLD",
    "V_IMBALANCE_THRESHOLD",
)

DEFAULT_THRESHOLDS: dict[str, float] = {
    "V_MIN_THRESHOLD": 353.0,
    "V_MAX_THRESHOLD": 430.0,
    "I_MAX_THRESHOLD": 1000.0,
    "I_UNBALANCE_MAX_THRESHOLD": 0.15,
    "P_ACTIVE_MIN_THRESHOLD": 0.0,
    "PF_MIN_THRESHOLD": 0.90,
    "T_MIN_THRESHOLD": 0.0,
    "T_MAX_THRESHOLD": 70.0,
    "I_MIN_ACTIVE_THRESHOLD": 1.0,
    "FREEZE_COUNT_THRESHOLD": 3,
    "FREEZE_STD_THRESHOLD": 0.01,
    "V_IMBALANCE_THRESHOLD": 0.02,
}

DEFAULT_ENABLED_RULES: dict[str, bool] = {
    "current_overload": True,
    "current_unbalance": False,
    "power_factor": False,
    "detail_output": False,
}

DEFAULT_CONFIG: dict[str, float | bool] = {
    **DEFAULT_THRESHOLDS,
    **DEFAULT_ENABLED_RULES,
}

INVALID_VALUES = [-1.0, 2867.2]

PHASE_MAP = {
    "Uab": "A相电压",
    "Ubc": "B相电压",
    "Uca": "C相电压",
    "Ia": "A相电流",
    "Ib": "B相电流",
    "Ic": "C相电流",
    "A相温度": "A相温度",
    "B相温度": "B相温度",
    "C相温度": "C相温度",
}

TARGET_SHORT_NAMES_REPORT = [
    "Uab",
    "Ubc",
    "Uca",
    "Ia",
    "Ib",
    "Ic",
    "有功功率",
    "无功功率",
    "功率因数",
    "A相温度",
    "B相温度",
    "C相温度",
]


def normalize_config(raw: dict[str, Any] | None) -> dict[str, float | bool]:
    """Merge user config with defaults and coerce supported values."""
    config = DEFAULT_CONFIG.copy()
    if not isinstance(raw, dict):
        return config

    for key in THRESHOLD_KEYS:
        if key not in raw:
            continue
        try:
            value = float(raw[key])
        except (TypeError, ValueError):
            continue
        if math.isnan(value) or math.isinf(value):
            continue
        config[key] = int(value) if key == "FREEZE_COUNT_THRESHOLD" else value

    for key in RULE_CONFIG_KEYS:
        if key in raw:
            config[key] = bool(raw[key])

    # Backward compatibility for old configs that used one current switch.
    if isinstance(raw.get("current"), bool):
        config["current_overload"] = raw["current"]
        config["current_unbalance"] = raw["current"]

    return config


def split_config(config: dict[str, Any]) -> tuple[dict[str, float], dict[str, bool]]:
    """Return detector thresholds and enabled-rule switches."""
    normalized = normalize_config(config)
    thresholds = {key: float(normalized[key]) for key in THRESHOLD_KEYS}
    thresholds["FREEZE_COUNT_THRESHOLD"] = int(thresholds["FREEZE_COUNT_THRESHOLD"])
    rules = {key: bool(normalized[key]) for key in RULE_CONFIG_KEYS}
    return thresholds, rules


def load_config(path: str | Path) -> dict[str, float | bool]:
    """Load a config file, returning defaults only when it is missing."""
    config_path = Path(path)
    if not config_path.exists():
        return DEFAULT_CONFIG.copy()
    try:
        data = json.loads(config_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"配置文件无法解析: {config_path} ({exc.msg})") from exc
    except OSError as exc:
        raise OSError(f"配置文件不可读取: {config_path} ({exc})") from exc
    if not isinstance(data, dict):
        raise ValueError(f"配置文件格式错误: {config_path}，根节点必须是对象。")
    return normalize_config(data)


def save_config(path: str | Path, config: dict[str, Any]) -> None:
    """Write a normalized UTF-8 JSON config file."""
    config_path = Path(path)
    config_path.parent.mkdir(parents=True, exist_ok=True)
    normalized = normalize_config(config)
    config_path.write_text(
        json.dumps(normalized, ensure_ascii=False, indent=4) + "\n",
        encoding="utf-8",
    )

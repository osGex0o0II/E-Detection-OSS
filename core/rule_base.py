"""检测规则基类与上下文。"""
from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any, Dict, Tuple

import pandas as pd


class DetectionContext:
    """封装单次文件检测所用的数据帧与列访问辅助。"""

    def __init__(self, df: pd.DataFrame):
        self.df = df

    def has_column(self, name: str) -> bool:
        return name in self.df.columns

    def series(self, name: str) -> pd.Series:
        return self.df[name]

    def available(self, names: list[str]) -> list[str]:
        return [n for n in names if n in self.df.columns]


class BaseRule(ABC):
    """规则插件抽象基类。"""

    rule_key: str | None = None  # enabled_rules 中的键；None 表示始终启用

    def is_enabled(self, enabled_rules: Dict[str, bool]) -> bool:
        if self.rule_key is None:
            return True
        return bool(enabled_rules.get(self.rule_key, False))

    @abstractmethod
    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        """返回 (异常描述 Series, 异常布尔掩码 Series)。"""

    @staticmethod
    def _empty_result(df: pd.DataFrame) -> Tuple[pd.Series, pd.Series]:
        return (
            pd.Series([""] * len(df), index=df.index, dtype=object),
            pd.Series([False] * len(df), index=df.index),
        )

    @staticmethod
    def _merge_labels(
        df: pd.DataFrame,
        mask: pd.Series,
        label: str,
        detail: str | None = None,
        detail_output: bool = False,
    ) -> Tuple[pd.Series, pd.Series]:
        text = detail if detail_output and detail else label
        series = pd.Series([""] * len(df), index=df.index, dtype=object)
        series.loc[mask] = text + "; "
        return series, mask.fillna(False)

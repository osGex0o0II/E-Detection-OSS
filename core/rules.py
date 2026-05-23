"""电气参数异常检测规则实现。"""
from __future__ import annotations

from typing import Dict, Tuple

import numpy as np
import pandas as pd

from core.rule_base import BaseRule, DetectionContext

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


def _current_active_mask(context: DetectionContext, thresholds: Dict[str, float]) -> pd.Series:
    """任一相电流超过激活阈值则视为有负载。"""
    i_min = thresholds.get("I_MIN_ACTIVE_THRESHOLD", 1.0)
    cols = context.available(["Ia", "Ib", "Ic"])
    if not cols:
        return pd.Series([True] * len(context.df), index=context.df.index)
    active = pd.Series([False] * len(context.df), index=context.df.index)
    for col in cols:
        active |= context.series(col).fillna(0) >= i_min
    return active


class VoltageRule(BaseRule):
    rule_key = None

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        v_min = thresholds.get("V_MIN_THRESHOLD", 372.0)
        v_max = thresholds.get("V_MAX_THRESHOLD", 428.0)
        detail = enabled_rules.get("detail_output", False)
        combined_mask = pd.Series([False] * len(df), index=df.index)
        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)

        for col in context.available(["Uab", "Ubc", "Uca"]):
            s = context.series(col)
            low = s < v_min
            high = s > v_max
            mask = (low | high).fillna(False)
            if not mask.any():
                continue
            phase = PHASE_MAP.get(col, col)
            if detail:
                msg = pd.Series([""] * len(df), index=df.index, dtype=object)
                msg.loc[low] = f"{phase}过低; "
                msg.loc[high] = f"{phase}过高; "
            else:
                msg = pd.Series(["电压异常; "] * len(df), index=df.index, dtype=object)
                msg.loc[~mask] = ""
            combined_mask |= mask
            combined_text = combined_text + msg.fillna("")

        return combined_text, combined_mask


class CurrentOverloadRule(BaseRule):
    rule_key = "current_overload"

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        i_max = thresholds.get("I_MAX_THRESHOLD", 1000.0)
        active = _current_active_mask(context, thresholds)
        detail = enabled_rules.get("detail_output", False)
        combined_mask = pd.Series([False] * len(df), index=df.index)
        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)

        for col in context.available(["Ia", "Ib", "Ic"]):
            mask = (context.series(col) > i_max).fillna(False) & active
            if not mask.any():
                continue
            phase = PHASE_MAP.get(col, col)
            label = f"{phase}过大; " if detail else "电流过大; "
            combined_mask |= mask
            combined_text.loc[mask] = combined_text.loc[mask] + label

        return combined_text, combined_mask


class CurrentUnbalanceRule(BaseRule):
    rule_key = "current_unbalance"

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        cols = context.available(["Ia", "Ib", "Ic"])
        if len(cols) < 2:
            return self._empty_result(df)

        limit = thresholds.get("I_UNBALANCE_MAX_THRESHOLD", 0.15)
        active = _current_active_mask(context, thresholds)
        frame = df[cols]
        i_max = frame.max(axis=1)
        i_min = frame.min(axis=1)
        i_mean = frame.mean(axis=1).replace(0, np.nan)
        unbalance = ((i_max - i_min) / i_mean).fillna(0)
        mask = (unbalance > limit).fillna(False) & active
        label = "电流不平衡; "
        text = pd.Series([""] * len(df), index=df.index, dtype=object)
        text.loc[mask] = label
        return text, mask


class FreezeRule(BaseRule):
    rule_key = None

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        count_need = int(thresholds.get("FREEZE_COUNT_THRESHOLD", 3))
        std_limit = thresholds.get("FREEZE_STD_THRESHOLD", 0.01)
        numeric_cols = context.available(
            ["Uab", "Ubc", "Uca", "Ia", "Ib", "Ic", "有功功率", "无功功率", "功率因数"]
        )
        if not numeric_cols:
            return self._empty_result(df)

        # 至少 2 个参数同时低波动才判冻结，避免单参数稳态负载误报
        col_masks: list[pd.Series] = []
        for col in numeric_cols:
            s = context.series(col)
            roll = s.rolling(count_need, min_periods=count_need)
            low_var = roll.std() <= std_limit
            window_full = roll.count() >= count_need
            col_masks.append((low_var & window_full).fillna(False))

        if not col_masks:
            return self._empty_result(df)

        frozen_count = col_masks[0].astype(int)
        for mask in col_masks[1:]:
            frozen_count = frozen_count + mask.astype(int)
        frozen = frozen_count >= 2

        label = "数据冻结; "
        text = pd.Series([""] * len(df), index=df.index, dtype=object)
        text.loc[frozen] = label
        return text, frozen


class PowerActiveRule(BaseRule):
    rule_key = None

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        if not context.has_column("有功功率"):
            return self._empty_result(df)

        p_min = thresholds.get("P_ACTIVE_MIN_THRESHOLD", 0.0)
        active = _current_active_mask(context, thresholds)
        mask = (context.series("有功功率") < p_min).fillna(False) & active
        label = "有功功率异常; "
        text = pd.Series([""] * len(df), index=df.index, dtype=object)
        text.loc[mask] = label
        return text, mask


class PowerFactorRule(BaseRule):
    rule_key = "power_factor"

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        if not context.has_column("功率因数"):
            return self._empty_result(df)

        pf_min = thresholds.get("PF_MIN_THRESHOLD", 0.90)
        active = _current_active_mask(context, thresholds)
        mask = (context.series("功率因数") < pf_min).fillna(False) & active
        label = "功率因数过低; "
        text = pd.Series([""] * len(df), index=df.index, dtype=object)
        text.loc[mask] = label
        return text, mask


class TemperatureRule(BaseRule):
    rule_key = None

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        t_min = thresholds.get("T_MIN_THRESHOLD", 0.0)
        t_max = thresholds.get("T_MAX_THRESHOLD", 70.0)
        detail = enabled_rules.get("detail_output", False)
        combined_mask = pd.Series([False] * len(df), index=df.index)
        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)

        for col in context.available(["A相温度", "B相温度", "C相温度"]):
            s = context.series(col)
            low = s < t_min
            high = s > t_max
            mask = (low | high).fillna(False)
            if not mask.any():
                continue
            phase = PHASE_MAP.get(col, col)
            if detail:
                msg = pd.Series([""] * len(df), index=df.index, dtype=object)
                msg.loc[low] = f"{phase}过低; "
                msg.loc[high] = f"{phase}过高; "
            else:
                msg = pd.Series(["温度异常; "] * len(df), index=df.index, dtype=object)
                msg.loc[~mask] = ""
            combined_mask |= mask
            combined_text = combined_text + msg.fillna("")

        return combined_text, combined_mask

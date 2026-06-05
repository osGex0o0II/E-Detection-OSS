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
    """电压异常检测：越限、相电压不平衡、三相全零停机过滤。始终启用。"""

    rule_key = None

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        v_min = thresholds.get("V_MIN_THRESHOLD", 353.0)
        v_max = thresholds.get("V_MAX_THRESHOLD", 407.0)
        v_imbalance = thresholds.get("V_IMBALANCE_THRESHOLD", 0.02)
        detail = enabled_rules.get("detail_output", False)
        combined_mask = pd.Series([False] * len(df), index=df.index)
        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)

        v_cols = context.available(["Uab", "Ubc", "Uca"])
        if not v_cols:
            return combined_text, combined_mask

        # ---- Step 1: 三相全零 → 设备正常停机（不标记异常）----
        if len(v_cols) >= 2:
            all_off = (df[v_cols] < 1.0).all(axis=1).fillna(False)
        else:
            all_off = pd.Series([False] * len(df), index=df.index)

        # ---- Step 2: 相电压不平衡检测（缺相/偏相）----
        if len(v_cols) >= 2:
            row_means = df[v_cols].mean(axis=1)
            active_mask = row_means > 30.0

            # PT 断线/接线异常检测：一相正常 + 另两相偏离超过 30%
            if len(v_cols) == 3:
                v_frame = df[v_cols].copy()
                v_median = v_frame.median(axis=1)
                for col in v_cols:
                    other_cols = [c for c in v_cols if c != col]
                    deviation = (v_frame[col] - v_median).abs() / v_median.replace(0, np.nan)
                    other_dev = ((v_frame[other_cols] - v_median.values.reshape(-1, 1)).abs()
                                 / v_median.replace(0, np.nan).values.reshape(-1, 1)).min(axis=1)
                    pt_anomaly = (active_mask.fillna(False) & (deviation > 0.30).fillna(False)
                                  & (other_dev < 0.15).fillna(False) & (~all_off))
                    if pt_anomaly.any():
                        phase = PHASE_MAP.get(col, col)
                        if detail:
                            dev_pct = deviation[pt_anomaly].mul(100).round(1).astype(str)
                            label = pd.Series([""] * len(df), index=df.index, dtype=object)
                            label[pt_anomaly] = dev_pct.apply(
                                lambda d: f"{phase}疑似PT接线异常(偏差{d}%); "
                            )
                            combined_text = combined_text + label.fillna("")
                        else:
                            combined_text.loc[pt_anomaly] = combined_text.loc[pt_anomaly] + "疑似PT接线异常; "
                        combined_mask |= pt_anomaly

            for col in v_cols:
                if not active_mask.any():
                    continue
                deviation = (df[col] - row_means).abs() / row_means.replace(0, np.nan)
                imbalance = active_mask.fillna(False) & (deviation > v_imbalance).fillna(False) & (~all_off)
                # 已标记为 PT 异常的行不再重复标记为不平衡
                imbalance = imbalance & (~combined_mask)
                if imbalance.any():
                    phase = PHASE_MAP.get(col, col)
                    if detail:
                        vals = df.loc[imbalance, col].round(1).astype(str)
                        dev_vals = deviation[imbalance].round(3).astype(str)
                        label = pd.Series([""] * len(df), index=df.index, dtype=object)
                        label[imbalance] = vals.str.cat(
                            dev_vals.values, sep=":偏差").apply(
                            lambda x: f"{phase}电压不平衡:{x}; "
                        )
                        combined_text = combined_text + label.fillna("")
                    else:
                        combined_text.loc[imbalance] = combined_text.loc[imbalance] + "相电压不平衡; "
                    combined_mask |= imbalance

        # ---- Step 3: 单相越限检测（排除三相全零行）----
        for col in v_cols:
            s = context.series(col)
            low = (s < v_min).fillna(False) & (~all_off)
            high = (s > v_max).fillna(False) & (~all_off)
            mask = low | high
            if not mask.any():
                continue
            phase = PHASE_MAP.get(col, col)
            if detail:
                msg = pd.Series([""] * len(df), index=df.index, dtype=object)
                low_vals = s[low].round(1).astype(str)
                high_vals = s[high].round(1).astype(str)
                msg[low] = low_vals.apply(lambda v: f"{phase}过低({v}V); ")
                msg[high] = high_vals.apply(lambda v: f"{phase}过高({v}V); ")
            else:
                msg = pd.Series(["电压异常; "] * len(df), index=df.index, dtype=object)
                msg.loc[~mask] = ""
            combined_mask |= mask
            combined_text = combined_text + msg.fillna("")

        return combined_text, combined_mask


class SuddenChangeRule(BaseRule):
    """数据突变检测：检测相邻采样点间的剧烈跳变，判别短路/断路/传感器故障。"""

    rule_key = "sudden_change"

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        # 电流突变阈值（比例）：相邻点变化超过此比例视为突变
        i_skip = thresholds.get("IA_SKIP_THRESHOLD", 0.5)
        # 电压突变阈值（比例）
        v_skip = thresholds.get("V_SKIP_THRESHOLD", 0.2)
        detail = enabled_rules.get("detail_output", False)

        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)
        combined_mask = pd.Series([False] * len(df), index=df.index)

        # 电流列突变检测
        for col in context.available(["Ia", "Ib", "Ic"]):
            s = context.series(col).fillna(method="ffill").fillna(0)
            diff_abs = s.diff().abs()
            # 避免除以零：使用前一时刻值 + 小常数
            denom = s.shift(1).abs() + 1e-6
            ratio = (diff_abs / denom).fillna(0)
            mask = ratio > i_skip
            if mask.any():
                phase = PHASE_MAP.get(col, col)
                if detail:
                    label = pd.Series([""] * len(df), index=df.index, dtype=object)
                    label[mask] = f"{phase}突变; "
                    combined_text = combined_text + label
                else:
                    combined_text.loc[mask] = combined_text.loc[mask] + "数据突变; "
                combined_mask |= mask

        # 电压列突变检测
        for col in context.available(["Uab", "Ubc", "Uca"]):
            s = context.series(col).fillna(method="ffill").fillna(0)
            diff_abs = s.diff().abs()
            denom = s.shift(1).abs() + 1e-6
            ratio = (diff_abs / denom).fillna(0)
            mask = ratio > v_skip
            if mask.any():
                phase = PHASE_MAP.get(col, col)
                if detail:
                    label = pd.Series([""] * len(df), index=df.index, dtype=object)
                    label[mask] = f"{phase}突变; "
                    combined_text = combined_text + label
                else:
                    combined_text.loc[mask] = combined_text.loc[mask] + "数据突变; "
                combined_mask |= mask

        return combined_text, combined_mask


class CrossParamRule(BaseRule):
    """跨参数关联分析：发现单独参数正常但组合模式异常的情况。"""

    rule_key = "cross_param"

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        v_min = thresholds.get("V_MIN_THRESHOLD", 353.0)
        v_max = thresholds.get("V_MAX_THRESHOLD", 407.0)
        detail = enabled_rules.get("detail_output", False)

        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)
        combined_mask = pd.Series([False] * len(df), index=df.index)

        # ---- 规则 1：电压正常但电流大幅偏离日均均值 ----
        # 电压在正常范围内
        v_cols = context.available(["Uab", "Ubc", "Uca"])
        i_cols = context.available(["Ia", "Ib", "Ic"])
        if v_cols and i_cols:
            v_ok = pd.Series([True] * len(df), index=df.index)
            for vcol in v_cols:
                v_ok &= (context.series(vcol) >= v_min) & (context.series(vcol) <= v_max)
            v_ok = v_ok.fillna(True)

            for icol in i_cols:
                s = context.series(icol).fillna(0)
                mean_i = s.mean()
                if mean_i == 0:
                    continue
                std_i = s.std()
                # 偏离均值 2.5σ 且电压正常
                i_abnormal = (s - mean_i).abs() > 2.5 * std_i
                mask = v_ok & i_abnormal
                if mask.any():
                    phase = PHASE_MAP.get(icol, icol)
                    if detail:
                        label = pd.Series([""] * len(df), index=df.index, dtype=object)
                        label[mask] = f"{phase}异常偏离（电压正常）; "
                        combined_text = combined_text + label
                    else:
                        combined_text.loc[mask] = combined_text.loc[mask] + "关联异常; "
                    combined_mask |= mask

        # ---- 规则 2：三相电压同时升高（系统过电压前兆）----
        if len(v_cols) >= 3:
            v_frame = df[v_cols].fillna(method="ffill").fillna(0)
            # 三相同步升高：三相比较前 3 个点的均值，均上升且超过阈值比例
            v_now = v_frame.iloc[:, :3].mean(axis=1)
            v_prev = v_frame.iloc[:, :3].shift(3).mean(axis=1)
            swell_ratio = ((v_now - v_prev) / v_prev.replace(0, np.nan)).fillna(0)
            # 三相电压同时上升超过 10% 且当前值接近上限
            mask = (swell_ratio > 0.10) & (v_now > v_max * 0.95)
            mask = mask.fillna(False)
            if mask.any():
                if detail:
                    label = pd.Series([""] * len(df), index=df.index, dtype=object)
                    label[mask] = "三相电压同步升高; "
                    combined_text = combined_text + label
                else:
                    combined_text.loc[mask] = combined_text.loc[mask] + "关联异常; "
                combined_mask |= mask

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

    # 核心列：冻结判定仅在这些列上触发，避免辅助列恒值误报
    CORE_COLS = ["Uab", "Ubc", "Uca", "Ia", "Ib", "Ic", "有功功率"]
    # 辅助列：单独处理，恒值视为"传感器数据缺失"，不参与冻结判定
    AUX_COLS = ["无功功率", "功率因数"]

    def detect(
        self,
        context: DetectionContext,
        thresholds: Dict[str, float],
        enabled_rules: Dict[str, bool],
    ) -> Tuple[pd.Series, pd.Series]:
        df = context.df
        count_need = int(thresholds.get("FREEZE_COUNT_THRESHOLD", 3))
        std_limit = thresholds.get("FREEZE_STD_THRESHOLD", 0.01)
        detail = enabled_rules.get("detail_output", False)
        i_min_active = thresholds.get("I_MIN_ACTIVE_THRESHOLD", 1.0)
        v_min = thresholds.get("V_MIN_THRESHOLD", 353.0)
        v_max = thresholds.get("V_MAX_THRESHOLD", 407.0)

        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)
        combined_mask = pd.Series([False] * len(df), index=df.index)

        # ---- 设备备用判定：三相电流均低于激活阈值 + 电压正常 → 非冻结 ----
        i_cols = context.available(["Ia", "Ib", "Ic"])
        v_cols = context.available(["Uab", "Ubc", "Uca"])
        standby_mask = pd.Series([False] * len(df), index=df.index)
        if i_cols:
            i_all_low = pd.Series([True] * len(df), index=df.index)
            for col in i_cols:
                i_all_low &= context.series(col).fillna(0).abs() < i_min_active
            if v_cols:
                v_all_normal = pd.Series([True] * len(df), index=df.index)
                for col in v_cols:
                    s = context.series(col)
                    v_all_normal &= (s >= v_min) & (s <= v_max)
                standby_mask = i_all_low & v_all_normal.fillna(True)
            else:
                standby_mask = i_all_low

        # ---- 辅助列：全零 → 记录但不逐行标记（由上层汇总） ----
        core_available = context.available(self.CORE_COLS)
        aux_available = context.available(self.AUX_COLS)

        # 辅助列非全零时的恒定检测
        for col in aux_available:
            s = context.series(col)
            if (s.fillna(0) == 0).all():
                continue  # 全零列由上层统一处理，不逐行标记

            flat = s.diff().abs().fillna(0) < std_limit
            aux_frozen = flat.rolling(count_need, min_periods=count_need).sum() >= count_need
            aux_frozen = aux_frozen.fillna(False)
            if aux_frozen.any():
                if detail:
                    label = pd.Series([""] * len(df), index=df.index, dtype=object)
                    label[aux_frozen] = f"{col}恒定; "
                    combined_text = combined_text + label
                else:
                    combined_text.loc[aux_frozen] = combined_text.loc[aux_frozen] + "数据恒定; "
                combined_mask |= aux_frozen

        # --- 核心列：冻结判定（排除备用状态） ---
        if not core_available:
            return combined_text, combined_mask

        col_masks: list[pd.Series] = []
        for col in core_available:
            s = context.series(col)
            # 全零 → 传感器缺失（由上层汇总，不逐行标记）
            if (s.fillna(0) == 0).all():
                continue

            flat = s.diff().abs().fillna(0) < std_limit
            frozen = flat.rolling(count_need, min_periods=count_need).sum() >= count_need
            col_masks.append(frozen.fillna(False))

        if col_masks:
            frozen_count = col_masks[0].astype(int)
            for mask in col_masks[1:]:
                frozen_count = frozen_count + mask.astype(int)
            frozen = (frozen_count >= 2) & (~standby_mask)

            label = "数据冻结; "
            combined_text.loc[frozen] = combined_text.loc[frozen] + label
            combined_mask |= frozen

        return combined_text, combined_mask


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
        i_min_active = thresholds.get("I_MIN_ACTIVE_THRESHOLD", 1.0)
        active = _current_active_mask(context, thresholds)
        detail = enabled_rules.get("detail_output", False)

        p_series = context.series("有功功率")
        combined_text = pd.Series([""] * len(df), index=df.index, dtype=object)
        combined_mask = pd.Series([False] * len(df), index=df.index)

        # CT 极性接反检测：有功功率 < 0 且电流 > 激活阈值
        p_negative = p_series < 0
        ct_reverse = p_negative.fillna(False) & active
        if ct_reverse.any():
            combined_mask |= ct_reverse
            if detail:
                label = pd.Series([""] * len(df), index=df.index, dtype=object)
                label[ct_reverse] = "CT极性异常; "
                combined_text = combined_text + label
            else:
                combined_text.loc[ct_reverse] = "CT极性异常; "

        # 有功功率过低检测（非负值部分）
        mask = (p_series < p_min).fillna(False) & active & (~ct_reverse)
        if mask.any():
            label = "有功功率异常; " if detail else "有功功率异常; "
            combined_mask |= mask
            combined_text.loc[mask] = combined_text.loc[mask] + label

        return combined_text, combined_mask


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

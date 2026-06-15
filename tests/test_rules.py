"""单元测试：7 个异常检测规则类。"""
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

import math
import numpy as np
import pandas as pd
import pytest

from core.rule_base import DetectionContext
from core.rules import (
    VoltageRule, CurrentOverloadRule, CurrentUnbalanceRule,
    FreezeRule, PowerActiveRule, PowerFactorRule, TemperatureRule,
)


def _ctx(df: pd.DataFrame) -> DetectionContext:
    return DetectionContext(df)


# ── 公用阈值 ──
TH = {
    'V_MIN_THRESHOLD': 353.0,
    'V_MAX_THRESHOLD': 430.0,
    'V_IMBALANCE_THRESHOLD': 0.02,
    'I_MAX_THRESHOLD': 1000.0,
    'I_MIN_ACTIVE_THRESHOLD': 1.0,
    'I_UNBALANCE_MAX_THRESHOLD': 0.15,
    'P_ACTIVE_MIN_THRESHOLD': 0.0,
    'PF_MIN_THRESHOLD': 0.90,
    'T_MIN_THRESHOLD': 0.0,
    'T_MAX_THRESHOLD': 70.0,
    'FREEZE_COUNT_THRESHOLD': 3,
    'FREEZE_STD_THRESHOLD': 0.01,
}
ER = {'detail_output': True, 'current_overload': True, 'current_unbalance': True, 'power_factor': True}
ER_BRIEF = dict(ER, **{'detail_output': False})


# ================================================================
# VoltageRule
# ================================================================
class TestVoltageRule:
    def test_normal(self):
        df = pd.DataFrame({'Uab': [380.0, 381.0], 'Ubc': [379.0, 380.0], 'Uca': [381.0, 382.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert not mask.any()
        assert all(t == '' for t in text)

    def test_low_voltage(self):
        df = pd.DataFrame({'Uab': [340.0, 350.0, 380.0], 'Ubc': [341.0, 351.0, 379.0], 'Uca': [342.0, 352.0, 381.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert mask.iloc[0] and mask.iloc[1] and not mask.iloc[2]
        assert '过低' in text.iloc[0] and '过低' in text.iloc[1]

    def test_high_voltage(self):
        df = pd.DataFrame({'Uab': [435.0, 380.0], 'Ubc': [436.0, 379.0], 'Uca': [434.0, 381.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert mask.iloc[0] and not mask.iloc[1]
        assert '过高' in text.iloc[0]

    def test_boundary_no_trigger(self):
        df = pd.DataFrame({'Uab': [353.0, 430.0], 'Ubc': [354.0, 429.0], 'Uca': [355.0, 428.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_three_phase_off_ignored(self):
        df = pd.DataFrame({'Uab': [0.0, 0.5], 'Ubc': [0.0, 0.3], 'Uca': [0.0, 0.4]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_imbalance(self):
        df = pd.DataFrame({'Uab': [380.0, 380.0], 'Ubc': [379.0, 360.0], 'Uca': [381.0, 381.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert mask.iloc[1] and not mask.iloc[0]
        assert '电压' in text.iloc[1]

    def test_missing_columns(self):
        df = pd.DataFrame({'Ia': [10.0, 20.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_empty_df(self):
        df = pd.DataFrame({'Uab': pd.Series(dtype=float), 'Ubc': pd.Series(dtype=float), 'Uca': pd.Series(dtype=float)})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_detail_output_false(self):
        df = pd.DataFrame({'Uab': [340.0, 380.0], 'Ubc': [341.0, 379.0], 'Uca': [342.0, 381.0]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER_BRIEF)
        assert mask.iloc[0] and not mask.iloc[1]
        assert '电压异常' in text.iloc[0]


# ================================================================
# CurrentOverloadRule
# ================================================================
class TestCurrentOverloadRule:
    def test_normal(self):
        df = pd.DataFrame({'Ia': [100.0, 200.0], 'Ib': [110.0, 210.0], 'Ic': [105.0, 205.0]})
        text, mask = CurrentOverloadRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_overload(self):
        th = dict(TH, I_MAX_THRESHOLD=150.0)
        df = pd.DataFrame({'Ia': [100.0, 200.0], 'Ib': [110.0, 210.0], 'Ic': [105.0, 205.0]})
        text, mask = CurrentOverloadRule().detect(_ctx(df), th, ER)
        assert mask.iloc[1] and not mask.iloc[0]
        assert '过大' in text.iloc[1]

    def test_inactive_load_ignored(self):
        th = dict(TH, I_MAX_THRESHOLD=150.0)
        df = pd.DataFrame({'Ia': [0.0, 0.5], 'Ib': [0.0, 0.3], 'Ic': [0.0, 0.4]})
        text, mask = CurrentOverloadRule().detect(_ctx(df), th, ER)
        assert not mask.any()

    def test_single_phase(self):
        th = dict(TH, I_MAX_THRESHOLD=150.0)
        df = pd.DataFrame({'Ia': [100.0, 200.0]})
        text, mask = CurrentOverloadRule().detect(_ctx(df), th, ER)
        assert mask.iloc[1] and not mask.iloc[0]

    def test_missing_columns(self):
        df = pd.DataFrame({'Uab': [380.0]})
        text, mask = CurrentOverloadRule().detect(_ctx(df), TH, ER)
        assert not mask.any()


# ================================================================
# CurrentUnbalanceRule
# ================================================================
class TestCurrentUnbalanceRule:
    def test_balanced(self):
        df = pd.DataFrame({'Ia': [100.0, 101.0], 'Ib': [102.0, 99.0], 'Ic': [101.0, 100.0]})
        text, mask = CurrentUnbalanceRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    @pytest.mark.parametrize('ia,ib,ic,expected', [
        (100.0, 200.0, 50.0, True),
        (100.0, 105.0, 103.0, False),
        (0.0, 0.0, 0.0, False),
    ])
    def test_unbalance(self, ia, ib, ic, expected):
        df = pd.DataFrame({'Ia': [ia], 'Ib': [ib], 'Ic': [ic]})
        text, mask = CurrentUnbalanceRule().detect(_ctx(df), TH, ER)
        assert mask.any() == expected

    def test_less_than_two_phases_skips(self):
        df = pd.DataFrame({'Ia': [100.0, 200.0]})
        text, mask = CurrentUnbalanceRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_missing_columns(self):
        df = pd.DataFrame({'Uab': [380.0]})
        text, mask = CurrentUnbalanceRule().detect(_ctx(df), TH, ER)
        assert not mask.any()


# ================================================================
# FreezeRule
# ================================================================
class TestFreezeRule:
    def test_normal_variation(self):
        np.random.seed(42)
        data = {c: 380.0 + np.random.randn(10) * 5 for c in ['Uab', 'Ubc', 'Uca']}
        data.update({c: 100.0 + np.random.randn(10) * 10 for c in ['Ia', 'Ib', 'Ic']})
        data['有功功率'] = 50.0 + np.random.randn(10) * 10
        df = pd.DataFrame(data)
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_frozen_core_cols(self):
        data = {c: [380.0] * 10 for c in ['Uab', 'Ubc', 'Uca']}
        data.update({c: [100.0] * 10 for c in ['Ia', 'Ib', 'Ic']})
        data['有功功率'] = [50.0] * 10
        df = pd.DataFrame(data)
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert mask.any()
        assert '冻结' in ','.join(text[text != ''].tolist())

    def test_standby_equipment_not_frozen(self):
        data = {c: [380.0] * 10 for c in ['Uab', 'Ubc', 'Uca']}
        data.update({c: [0.0] * 10 for c in ['Ia', 'Ib', 'Ic']})
        data['有功功率'] = [0.0] * 10
        df = pd.DataFrame(data)
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_all_zero_aux_columns(self):
        data = {c: [380.0] * 10 for c in ['Uab', 'Ubc', 'Uca']}
        data.update({c: [100.0] * 10 for c in ['Ia', 'Ib', 'Ic']})
        data['有功功率'] = [50.0] * 10
        data['无功功率'] = [0.0] * 10
        data['功率因数'] = [0.0] * 10
        df = pd.DataFrame(data)
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert mask.any()
        assert '冻结' in ','.join(text[text != ''].tolist())

    def test_frozen_aux_cols(self):
        data = {c: [380.0] * 10 for c in ['Uab', 'Ubc', 'Uca']}
        data.update({c: [100.0] * 10 for c in ['Ia', 'Ib', 'Ic']})
        data['有功功率'] = [50.0] * 10
        data['无功功率'] = [20.0] * 10
        data['功率因数'] = [0.9] * 10
        df = pd.DataFrame(data)
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert mask.any()

    def test_missing_core_cols_returns_empty(self):
        df = pd.DataFrame({'无功功率': [0.0, 0.0]})
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_short_df_rolling_returns_empty(self):
        df = pd.DataFrame({'Uab': [380.0, 381.0], 'Ia': [100.0, 101.0]})
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_count_need_exceeds_length(self):
        th = dict(TH, FREEZE_COUNT_THRESHOLD=10)
        df = pd.DataFrame({'Uab': [380.0] * 5, 'Ia': [100.0] * 5})
        text, mask = FreezeRule().detect(_ctx(df), th, ER)
        assert not mask.any()

    def test_single_column_not_enough_for_freeze(self):
        df = pd.DataFrame({'Uab': [380.0] * 10})
        text, mask = FreezeRule().detect(_ctx(df), TH, ER)
        assert not mask.any()


# ================================================================
# PowerActiveRule
# ================================================================
class TestPowerActiveRule:
    def test_normal(self):
        df = pd.DataFrame({'Ia': [100.0, 200.0], '有功功率': [50.0, 100.0]})
        text, mask = PowerActiveRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_ct_reverse(self):
        df = pd.DataFrame({'Ia': [100.0, 200.0], '有功功率': [-50.0, -100.0]})
        text, mask = PowerActiveRule().detect(_ctx(df), TH, ER)
        assert mask.any()
        assert 'CT极性' in text.iloc[0]

    def test_low_active_power(self):
        th = dict(TH, P_ACTIVE_MIN_THRESHOLD=30.0)
        df = pd.DataFrame({'Ia': [100.0, 200.0], '有功功率': [10.0, 50.0]})
        text, mask = PowerActiveRule().detect(_ctx(df), th, ER)
        assert mask.iloc[0] and not mask.iloc[1]
        assert '有功功率异常' in text.iloc[0]

    def test_missing_column_returns_empty(self):
        df = pd.DataFrame({'Ia': [100.0]})
        text, mask = PowerActiveRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_zero_power_no_active_returns_empty(self):
        df = pd.DataFrame({'Ia': [0.0, 0.0], '有功功率': [0.0, 0.0]})
        text, mask = PowerActiveRule().detect(_ctx(df), TH, ER)
        assert not mask.any()


# ================================================================
# PowerFactorRule
# ================================================================
class TestPowerFactorRule:
    def test_normal(self):
        df = pd.DataFrame({'Ia': [100.0, 200.0], '功率因数': [0.95, 0.93]})
        text, mask = PowerFactorRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_low_pf(self):
        df = pd.DataFrame({'Ia': [100.0, 200.0], '功率因数': [0.80, 0.95]})
        text, mask = PowerFactorRule().detect(_ctx(df), TH, ER)
        assert mask.iloc[0] and not mask.iloc[1]
        assert '功率因数过低' in text.iloc[0]

    def test_missing_column_returns_empty(self):
        df = pd.DataFrame({'Ia': [100.0]})
        text, mask = PowerFactorRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_no_active_load_skips(self):
        df = pd.DataFrame({'Ia': [0.0, 0.0], '功率因数': [0.50, 0.60]})
        text, mask = PowerFactorRule().detect(_ctx(df), TH, ER)
        assert not mask.any()


# ================================================================
# TemperatureRule
# ================================================================
class TestTemperatureRule:
    def test_normal(self):
        df = pd.DataFrame({'A相温度': [25.0, 30.0], 'B相温度': [26.0, 31.0], 'C相温度': [27.0, 32.0]})
        text, mask = TemperatureRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_overheat(self):
        df = pd.DataFrame({'A相温度': [80.0, 25.0]})
        text, mask = TemperatureRule().detect(_ctx(df), TH, ER)
        assert mask.iloc[0] and not mask.iloc[1]
        assert '过高' in text.iloc[0]

    def test_subzero(self):
        th = dict(TH, T_MIN_THRESHOLD=-10.0)
        df = pd.DataFrame({'A相温度': [-20.0, 25.0], 'B相温度': [-21.0, 26.0], 'C相温度': [-19.0, 27.0]})
        text, mask = TemperatureRule().detect(_ctx(df), th, ER)
        assert mask.iloc[0] and not mask.iloc[1]
        assert '过低' in text.iloc[0]

    def test_missing_columns(self):
        df = pd.DataFrame({'Ia': [100.0]})
        text, mask = TemperatureRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_single_phase_temp(self):
        df = pd.DataFrame({'A相温度': [85.0, 25.0]})
        text, mask = TemperatureRule().detect(_ctx(df), TH, ER)
        assert mask.iloc[0] and not mask.iloc[1]


# ================================================================
# RuleBase
# ================================================================
class TestRuleBase:
    def test_is_enabled_with_rule_key(self):
        rule = CurrentOverloadRule()
        assert rule.is_enabled({'current_overload': True})
        assert not rule.is_enabled({'current_overload': False})
        assert not rule.is_enabled({})

    def test_is_enabled_no_key_always_true(self):
        rule = VoltageRule()
        assert rule.is_enabled({})
        assert rule.is_enabled({'current_overload': False})

    def test_disabled_rule_is_not_enabled(self):
        rule = CurrentOverloadRule()
        assert not rule.is_enabled({'current_overload': False, 'detail_output': False})

    def test_empty_result(self):
        df = pd.DataFrame({'Uab': [380.0, 381.0]})
        text, mask = CurrentOverloadRule._empty_result(df)
        assert not mask.any()
        assert all(t == '' for t in text)

    def test_merge_labels(self):
        df = pd.DataFrame({'Uab': [380.0, 381.0]})
        s, m = CurrentOverloadRule._merge_labels(df, pd.Series([True, False]), "test")
        assert s.iloc[0] == "test; "
        assert s.iloc[1] == ""
        assert m.iloc[0] and not m.iloc[1]


# ================================================================
# Edge Cases
# ================================================================
class TestEdgeCases:
    def test_nan_in_threshold_rejected(self):
        th_with_nan = dict(TH)
        th_with_nan['V_MIN_THRESHOLD'] = math.nan
        df = pd.DataFrame({'Uab': [340.0, 380.0], 'Ubc': [341.0, 379.0], 'Uca': [342.0, 381.0]})
        text, mask = VoltageRule().detect(_ctx(df), th_with_nan, ER)
        assert not mask.any()

    def test_all_nan_dataframe(self):
        df = pd.DataFrame({'Uab': [math.nan, math.nan], 'Ia': [math.nan, math.nan]})
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert not mask.any()

    def test_mixed_types(self):
        df = pd.DataFrame({'Uab': [380.0, None, 'N/A'], 'Ia': [100.0, None, 'N/A']})
        for c in df.columns:
            df[c] = pd.to_numeric(df[c], errors='coerce')
        text, mask = VoltageRule().detect(_ctx(df), TH, ER)
        assert isinstance(mask, pd.Series)

    def test_rule_key_values(self):
        assert VoltageRule.rule_key is None
        assert CurrentOverloadRule.rule_key == 'current_overload'
        assert CurrentUnbalanceRule.rule_key == 'current_unbalance'
        assert FreezeRule.rule_key is None
        assert PowerActiveRule.rule_key is None
        assert PowerFactorRule.rule_key == 'power_factor'
        assert TemperatureRule.rule_key is None

    def test_rules_import_via_package(self):
        from core import rules as rules_mod
        assert hasattr(rules_mod, 'VoltageRule')
        assert hasattr(rules_mod, 'CurrentOverloadRule')

    def test_detect_returns_tuple_of_series(self):
        rule = VoltageRule()
        df = pd.DataFrame({'Uab': [380.0], 'Ubc': [379.0], 'Uca': [381.0]})
        result = rule.detect(_ctx(df), TH, ER)
        assert isinstance(result, tuple) and len(result) == 2
        assert isinstance(result[0], pd.Series)
        assert isinstance(result[1], pd.Series)

    def test_thresholds_dict_completeness(self):
        expected = {'V_MIN_THRESHOLD', 'V_MAX_THRESHOLD', 'V_IMBALANCE_THRESHOLD', 'I_MAX_THRESHOLD',
                    'I_MIN_ACTIVE_THRESHOLD', 'I_UNBALANCE_MAX_THRESHOLD', 'P_ACTIVE_MIN_THRESHOLD',
                    'PF_MIN_THRESHOLD', 'T_MIN_THRESHOLD', 'T_MAX_THRESHOLD',
                    'FREEZE_COUNT_THRESHOLD', 'FREEZE_STD_THRESHOLD'}
        assert set(TH.keys()) == expected

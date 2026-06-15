import pytest
import pandas as pd

# ── 全局阈值（所有规则共用） ──
@pytest.fixture
def TH():
    return {
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


@pytest.fixture
def ER():
    return {'detail_output': True, 'current_overload': True, 'current_unbalance': True, 'power_factor': True}


@pytest.fixture
def v3_df():
    return pd.DataFrame({'Uab': [380.0, 381.0], 'Ubc': [379.0, 380.0], 'Uca': [381.0, 382.0]})


@pytest.fixture
def i3_df():
    return pd.DataFrame({'Ia': [100.0, 200.0], 'Ib': [110.0, 210.0], 'Ic': [105.0, 205.0]})

from __future__ import annotations

import pandas as pd

from e_detection.pipeline import clean_and_rename_columns


def test_clean_and_rename_columns_merges_duplicate_aliases_as_numeric_mean():
    dataframe = pd.DataFrame(
        {
            "time": ["0", "1"],
            "Ua(V)": ["320", "322"],
            "A相电压": ["340", "342"],
            "Ib": ["10", "11"],
        }
    )

    cleaned, unmapped = clean_and_rename_columns(dataframe)

    assert unmapped == []
    assert list(cleaned.columns) == ["time", "Uab", "Ib"]
    assert cleaned["Uab"].tolist() == [330.0, 332.0]
    assert cleaned["Ib"].tolist() == ["10", "11"]

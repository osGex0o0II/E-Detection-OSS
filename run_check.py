# -*- coding: utf-8 -*-
"""Compatibility wrapper for the E-Detection CLI."""
from __future__ import annotations

from e_detection.cli import main


if __name__ == "__main__":
    raise SystemExit(main())

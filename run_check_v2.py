# -*- coding: utf-8 -*-
"""全面分析脚本：使用正确规则键名，逐文件输出详细检测结果。"""
import os, sys, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import importlib.util
spec = importlib.util.spec_from_file_location("app", os.path.join(os.path.dirname(__file__), "E-anomaly_detector-Portable_version-v20260520.py"))
app = importlib.util.module_from_spec(spec)
spec.loader.exec_module(app)
check_anomaly_in_file = app.check_anomaly_in_file

data_dir = os.path.join(os.path.dirname(__file__), "电力监控系统数据文件")
thresholds = {
    'V_MIN_THRESHOLD': 372.0,
    'V_MAX_THRESHOLD': 428.0,
    'I_MIN_ACTIVE_THRESHOLD': 1.0,
    'I_MAX_THRESHOLD': 3150.0,
    'I_UNBALANCE_MAX_THRESHOLD': 0.3,
    'PF_MIN_THRESHOLD': 0.8,
    'T_MAX_THRESHOLD': 120.0,
}

# 方案 A：仅冻结+电压+温度+过载+有功（始终启用的规则）
enabled_baseline = {
    'detail_output': True,
}

# 方案 B：全部规则启用（使用正确的 rule_key 键名）
enabled_full = {
    'detail_output': True,
    'current_overload': True,
    'current_unbalance': True,
    'power_factor': True,
}

csv_files = sorted([f for f in os.listdir(data_dir) if f.endswith('.csv')])

print("=" * 60)
print("方案 A：仅始终启用规则（冻结/电压/温度/过载/有功）")
print("=" * 60)
total_a = 0
for f in csv_files:
    fp = os.path.join(data_dir, f)
    anomalies, log_data, df_clean, _ = check_anomaly_in_file(fp, thresholds, enabled_baseline)
    if isinstance(log_data, dict):
        total_a += log_data['count']
        if log_data['count'] > 0:
            print(f"  [{log_data['count']:>3}条] {f}: {log_data['types']}")
    elif '跳过' not in str(log_data):
        print(f"  [ERR] {f}: {log_data}")
print(f"  合计: {total_a} 条\n")

print("=" * 60)
print("方案 B：全部规则启用（含电流不平衡、功率因数）")
print("=" * 60)
total_b = 0
for f in csv_files:
    fp = os.path.join(data_dir, f)
    anomalies, log_data, df_clean, _ = check_anomaly_in_file(fp, thresholds, enabled_full)
    if isinstance(log_data, dict):
        total_b += log_data['count']
        if log_data['count'] > 0:
            print(f"  [{log_data['count']:>3}条] {f}: {log_data['types']}")
    elif '跳过' not in str(log_data):
        print(f"  [ERR] {f}: {log_data}")
print(f"  合计: {total_b} 条\n")

# 新增差异分析
print("=" * 60)
print("差异分析（B 比 A 多检出的异常）")
print("=" * 60)
diff_total = 0
for f in csv_files:
    fp = os.path.join(data_dir, f)
    _, log_a, _, _ = check_anomaly_in_file(fp, thresholds, enabled_baseline)
    _, log_b, _, _ = check_anomaly_in_file(fp, thresholds, enabled_full)
    if isinstance(log_a, dict) and isinstance(log_b, dict):
        diff = log_b['count'] - log_a['count']
        if diff > 0:
            types_a = set(log_a.get('types', '').split('; '))
            types_b = set(log_b.get('types', '').split('; '))
            new_types = types_b - types_a
            diff_total += diff
            print(f"  [{diff:>3}条 新增] {f}: 新增类型={new_types}")
print(f"  差异合计: {diff_total} 条\n")

# 冻结误报专项分析：逐文件检查哪些列导致冻结判定
print("=" * 60)
print("冻结误报分析：检查导致冻结判定的列（仅对有负载变化的设备）")
print("=" * 60)
import pandas as pd
import numpy as np

import chardet

for f in csv_files:
    fp = os.path.join(data_dir, f)
    # 自动检测编码
    with open(fp, 'rb') as fb:
        raw = fb.read(4096)
        enc = chardet.detect(raw)['encoding'] or 'gbk'
    df = pd.read_csv(fp, encoding=enc, index_col=0)
    
    # 跳过高压设备
    if any('高压' in c for c in df.columns):
        continue
    
    numeric_cols = ['Uab', 'Ubc', 'Uca', 'Ia', 'Ib', 'Ic', '有功功率', '无功功率', '功率因数']
    available = [c for c in numeric_cols if any(c in col for col in df.columns)]
    
    freeze_cols = []
    for col in available:
        matched = [c for c in df.columns if col in c]
        if not matched:
            continue
        s = pd.to_numeric(df[matched[0]], errors='coerce')
        # 检查全列波动
        global_std = s.std()
        if global_std <= 0.01:
            freeze_cols.append(f"{col}(std={global_std:.4f})")
    
    if freeze_cols:
        # 同时检查电流/有功是否有变化
        ia_cols = [c for c in df.columns if 'Ia' in c]
        p_cols = [c for c in df.columns if '有功功率' in c]
        has_current_change = False
        has_power_change = False
        if ia_cols:
            s = pd.to_numeric(df[ia_cols[0]], errors='coerce')
            has_current_change = s.std() > 1.0
        if p_cols:
            s = pd.to_numeric(df[p_cols[0]], errors='coerce')
            has_power_change = abs(s.std()) > 0.1
        
        if has_current_change or has_power_change:
            print(f"  ⚠ 疑似误报 [{f}]: 冻结列={freeze_cols}, 电流变化={'是' if has_current_change else '否'}, 有功变化={'是' if has_power_change else '否'}")

print("\n分析完成。")
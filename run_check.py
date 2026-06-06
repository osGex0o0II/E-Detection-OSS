# -*- coding: utf-8 -*-
import os, sys, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Dynamically import the main module
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
enabled_rules = {
    'current_unbalance': True,
    'power_factor': True,
    'detail_output': True,
}

csv_files = sorted([f for f in os.listdir(data_dir) if f.endswith('.csv')])
results = []
total_anomalies = 0

for f in csv_files:
    fp = os.path.join(data_dir, f)
    anomalies, log_data, df_clean, _ = check_anomaly_in_file(fp, thresholds, enabled_rules)
    if isinstance(log_data, dict):
        if log_data['count'] > 0:
            total_anomalies += log_data['count']
            line = f"{f}: 发现 {log_data['count']} 条异常，类型: {log_data['types']}"
        else:
            line = f"{f}: 正常"
    else:
        line = f"{f}: {log_data}"
    results.append(line)
    print(line)

print(f"\n--- 总计异常记录数: {total_anomalies} ---")
results.append(f"\n总计异常记录数: {total_anomalies}")

with open(os.path.join(os.path.dirname(__file__), 'check_result.txt'), 'w', encoding='utf-8') as fout:
    fout.write('\n'.join(results) + '\n')
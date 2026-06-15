import os
import sys
import json
import time
import threading
import queue

_SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

from datetime import datetime
from typing import Tuple, List, Dict, Any
import glob
from tkinter import filedialog, messagebox, TclError

try:
    import customtkinter as ctk
    from customtkinter.windows.widgets.ctk_entry import CTkEntry
except ModuleNotFoundError as _ctk_err:
    raise

# --- 配置与常量 ---
ctk.set_appearance_mode("light")
ctk.set_default_color_theme("blue")

GREEN_COLOR = "#107c10"
GREY_COLOR = "#888888"
BLUE_COLOR = "#0078d4"

TEXTS = {
    'title': "E-Detection",
    'header_main': "蓝蓝的天空",
    'header_sub': "电气参数异常检测系统v20260520",
    'instruction': "请选择CSV文件目录，系统将自动递归扫描子目录并检测异常。",
    'folder_default': "未选择输入目录",
    'report_default': "默认: 与输入目录相同",
    'btn_select_input': "选择输入目录",
    'btn_select_report': "选择报告目录",
    'rule_main_title': "查看/修改检测规则和阈值",
    'rule_current_overload': "电流过大检测",
    'rule_current_unbalance': "电流不平衡检测",
    'rule_power_factor': "功率因数过低检测",
    'rule_detail_output': "输出详细异常",
    'btn_start': "开始检测并生成异常报告",
    'btn_stop': "停止检测",
    'btn_apply': "应用修改",
    'status_ready': "状态：就绪",
    'log_start': "系统已启动，支持自定义报告路径、可选规则及阈值配置。",
    'log_applied': "阈值配置已应用并生效。详细修改如下:",
    'err_no_folder': "错误：请指定有效的数据源目录。",
    'err_no_file': "警告：指定路径未发现 CSV 数据文件。",
    'btn_clear_log': "清空日志",
    'btn_export_log': "导出日志",
    'log_title': "操作日志",
}

from e_detection.settings import (
    DEFAULT_CONFIG,
    RULE_CONFIG_KEYS,
    TARGET_SHORT_NAMES_REPORT,
    normalize_config,
)
from e_detection.excel_report import enrich_anomalies, write_excel_report
from e_detection.pipeline import (
    _check_frozen_acquisition,
    _detect_core_logic,
    _extract_anomaly_value,
    _extract_building_and_transformer,
    _extract_transformer_issues,
    _format_anomaly_report,
    _format_detail_structured,
    _format_log_message,
    _is_only_freeze_types,
    _load_and_clean_data,
    _summarize_types,
    check_anomaly_in_file,
    clean_and_rename_columns,
    extract_date_from_filename,
)

# --- GUI 界面 (GUI Application Class) ---

class ElectricalAnomalyDetectorApp(ctk.CTk):
    """电气参数异常检测系统 GUI 主窗口。

    基于 CustomTkinter，提供目录选择、阈值配置、规则开关、实时日志、
    进度条、暂停/停止检测等功能。检测输出为 Excel 异常报告。
    """

    def __init__(self):
        """初始化主窗口：几何尺寸、默认阈值、规则状态、UI 布局。"""
        super().__init__()

        screen_width = self.winfo_screenwidth()
        screen_height = self.winfo_screenheight()

        new_width = int(screen_width * 0.50)
        new_height = int(screen_height * 0.85)

        center_x = int((screen_width - new_width) / 2)
        center_y = int((screen_height - new_height) / 2)

        self.title(TEXTS['title'])
        self.geometry(f"{new_width}x{new_height}+{center_x}+{center_y}")

        self.minsize(780, 580)
        self.configure(fg_color="#f5f5f5")

        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(0, weight=1)

        self.folder_path: str = ""
        self.report_path: str = ""
        self.initial_report_path: str = ""
        self.log_queue = queue.Queue()
        self._stop_detection = threading.Event()
        self._lock = threading.Lock()
        self.report_path_is_custom: bool = False

        self.V_MIN_THRESHOLD = 353.0
        self.V_MAX_THRESHOLD = 430.0
        self.I_MAX_THRESHOLD = 1000.0
        self.I_UNBALANCE_MAX_THRESHOLD = 0.15
        self.P_ACTIVE_MIN_THRESHOLD = 0.0
        self.PF_MIN_THRESHOLD = 0.90
        self.T_MIN_THRESHOLD = 0.0
        self.T_MAX_THRESHOLD = 70.0
        self.I_MIN_ACTIVE_THRESHOLD = 1.0
        self.FREEZE_COUNT_THRESHOLD = 3
        self.FREEZE_STD_THRESHOLD = 0.01
        self.V_IMBALANCE_THRESHOLD = 0.02

        self.enabled_rules = {
            'current_overload': True,
            'current_unbalance': False,
            'power_factor': False,
            'detail_output': False,
        }

        self.DEFAULT_THRESHOLDS = dict(DEFAULT_CONFIG)

        self.config_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'config.json')
        self.load_config()

        self.report_label: ctk.CTkLabel | None = None
        self.folder_label: ctk.CTkLabel | None = None
        self.progress: ctk.CTkProgressBar | None = None
        self.status: ctk.CTkLabel | None = None
        self.log_text: ctk.CTkTextbox | None = None
        self.rules_toggle_btn: ctk.CTkButton | None = None
        self.rules_display_card: ctk.CTkFrame | None = None
        self.rules_display_content_frame: ctk.CTkFrame | None = None
        self.start_btn: ctk.CTkButton | None = None
        self.log_content_frame: ctk.CTkFrame | None = None
        self.threshold_entries: Dict[str, CTkEntry] = {}
        self.threshold_vars: Dict[str, ctk.StringVar] = {}

        self.rule_vars: Dict[str, ctk.BooleanVar] = {
            'current_overload': ctk.BooleanVar(value=True),
            'current_unbalance': ctk.BooleanVar(value=False),
            'power_factor': ctk.BooleanVar(value=False),
            'detail_output': ctk.BooleanVar(value=False),
        }
        self.rule_checkboxes: Dict[str, ctk.CTkCheckBox] = {}

        # 用于存储标签引用，以便更新颜色
        self.threshold_labels: Dict[str, ctk.CTkLabel] = {}
        self.threshold_label_links: Dict[str, str] = {}

        self.last_report_file: str | None = None
        self.start_time: float | None = None
        self.end_time: float | None = None

        self.setup_ui()
        self.after(100, self._process_log_queue)

    def _format_value(self, key: str, value: float) -> str:
        """格式化阈值为显示字符串，功率因数/不平衡度/冻结波动保留两位小数，其余取整。"""
        if key in ['PF_MIN_THRESHOLD', 'I_UNBALANCE_MAX_THRESHOLD', 'FREEZE_STD_THRESHOLD',
                   'V_IMBALANCE_THRESHOLD']:
            return f"{value:.2f}"
        else:
            return f"{value:.0f}"

    def _build_thresholds_dict(self) -> Dict[str, float]:
        """将实例属性组装为规则检测所需的统一阈值字典。

        每个实例属性对应一个检测参数，由用户在 UI 中调整。该方法集中管理
        属性名到规则参数名的映射，避免 start_detection 中逐字内联拼装字典。

        Returns:
            Dict[str, float]: 键为大写阈值常量名、值为 numeric 阈值的字典。
        """
        return {
            'V_MIN_THRESHOLD': self.V_MIN_THRESHOLD,
            'V_MAX_THRESHOLD': self.V_MAX_THRESHOLD,
            'I_MAX_THRESHOLD': self.I_MAX_THRESHOLD,
            'I_UNBALANCE_MAX_THRESHOLD': self.I_UNBALANCE_MAX_THRESHOLD,
            'P_ACTIVE_MIN_THRESHOLD': self.P_ACTIVE_MIN_THRESHOLD,
            'PF_MIN_THRESHOLD': self.PF_MIN_THRESHOLD,
            'T_MIN_THRESHOLD': self.T_MIN_THRESHOLD,
            'T_MAX_THRESHOLD': self.T_MAX_THRESHOLD,
            'I_MIN_ACTIVE_THRESHOLD': self.I_MIN_ACTIVE_THRESHOLD,
            'FREEZE_COUNT_THRESHOLD': self.FREEZE_COUNT_THRESHOLD,
            'FREEZE_STD_THRESHOLD': self.FREEZE_STD_THRESHOLD,
            'V_IMBALANCE_THRESHOLD': self.V_IMBALANCE_THRESHOLD,
        }

    def _format_duration_text(self, duration: float, total_files: int) -> str:
        """格式化检测耗时字符串，含总时长和平均单文件耗时。"""
        avg = duration / total_files if total_files > 0 else 0.0
        return f"总耗时: {duration:.2f}s (平均 {avg:.2f}s/文件)"

    def on_closing(self) -> None:
        """窗口销毁前清理所有 StringVar 的 trace，避免退出时的 TclError。"""
        for var in self.threshold_vars.values():
            try:
                trace_id = None
                try:
                    # 优先使用 Tcl 9 的新 API
                    trace_id = var.trace_info()
                except AttributeError:
                    pass
                if trace_id is None:
                    try:
                        trace_id = var.trace_vinfo()
                    except AttributeError:
                        pass
                if trace_id:
                    for mode, func_name in trace_id:
                        var.trace_remove(mode, func_name)
            except TclError:
                pass

        self.destroy()

    def setup_ui(self) -> None:
        """构建并布局 GUI 的所有子控件（标题栏、目录卡片、规则面板、日志区、进度条等）。"""
        main = ctk.CTkFrame(self, corner_radius=0, fg_color="transparent")
        main.grid(row=0, column=0, sticky="nsew", padx=24, pady=24)
        main.grid_columnconfigure(0, weight=1)

        self._build_main_page(main)

    def _build_main_page(self, main) -> None:
        row_idx = 0

        title_frame = ctk.CTkFrame(main, height=80, fg_color="#0078d4", corner_radius=12)
        title_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 20));
        row_idx += 1
        title_frame.grid_propagate(False)

        ctk.CTkLabel(
            title_frame, text=TEXTS['header_main'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=24, weight="bold"),
            text_color="white"
        ).pack(side="left", padx=28, pady=22)
        ctk.CTkLabel(
            title_frame, text=TEXTS['header_sub'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=11),
            text_color="#c4e1ff"
        ).pack(side="right", padx=28, pady=22)

        ctk.CTkLabel(
            main, text=TEXTS['instruction'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12),
            text_color="#555555"
        ).grid(row=row_idx, column=0, sticky="w", padx=2, pady=(0, 16));
        row_idx += 1

        self._create_directory_cards(main, start_row=row_idx);
        row_idx += 2

        optional_controls_frame = ctk.CTkFrame(main, fg_color="transparent")
        optional_controls_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 10));
        row_idx += 1
        optional_controls_frame.grid_columnconfigure(0, weight=1)

        controls_inner_frame = ctk.CTkFrame(optional_controls_frame, fg_color="transparent")
        controls_inner_frame.grid(row=0, column=0, sticky="e")

        rules_list = [
            (TEXTS['rule_current_overload'], "current_overload"),
            (TEXTS['rule_current_unbalance'], "current_unbalance"),
            (TEXTS['rule_power_factor'], "power_factor"),
            (TEXTS['rule_detail_output'], "detail_output"),
        ]

        for label, key in rules_list:
            var = self.rule_vars[key]
            text_color = GREEN_COLOR if var.get() else "#333333"

            check = ctk.CTkCheckBox(
                controls_inner_frame, text=label,
                font=ctk.CTkFont(family="Microsoft YaHei UI", size=12),
                variable=var,
                command=lambda k=key, v=var: self._update_rule_state_and_display(k, v),
                text_color=text_color
            )
            check.pack(side="left", padx=10, pady=0)
            self.rule_checkboxes[key] = check

        rules_header_frame = ctk.CTkFrame(main, fg_color="transparent")
        rules_header_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 4));
        row_idx += 1
        rules_header_frame.grid_columnconfigure(0, weight=1)
        self._create_rules_header(rules_header_frame)

        self.rules_display_card = ctk.CTkFrame(main, fg_color="white", corner_radius=10, border_width=1,
                                               border_color="#e1e1e1")
        self.rules_display_card.grid(row=row_idx, column=0, sticky="ew", pady=(0, 14));
        row_idx += 1
        self.rules_display_card.grid_columnconfigure(0, weight=1)

        self.rules_display_content_frame = ctk.CTkFrame(self.rules_display_card, fg_color="#fafafa")
        self.rules_display_content_frame.pack(fill="x", padx=12, pady=12)

        # 首次创建阈值输入框
        self._create_rules_widgets()
        self._toggle_rules_panel(False)

        self.status = ctk.CTkLabel(
            main, text=TEXTS['status_ready'], anchor="w",
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=11),
            fg_color="#f5f5f5", text_color="#333333",
            corner_radius=8, height=36, padx=2
        )
        self.status.grid(row=row_idx, column=0, sticky="ew", pady=(0, 10));
        row_idx += 1

        self.progress = ctk.CTkProgressBar(main, height=6, progress_color="#0078d4")
        self.progress.grid(row=row_idx, column=0, sticky="ew", pady=(0, 10));
        row_idx += 1
        self.progress.set(0)

        self.start_btn = ctk.CTkButton(
            main, text=TEXTS['btn_start'], height=52,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=16, weight="bold"),
            fg_color="#0078d4", hover_color="#106ebe", corner_radius=10,
            command=self.start_detection_thread
        )
        self.start_btn.grid(row=row_idx, column=0, sticky="ew", pady=(0, 16));
        row_idx += 1

        log_header_frame = ctk.CTkFrame(main, fg_color="transparent")
        log_header_frame.grid(row=row_idx, column=0, sticky="ew", pady=(0, 4));
        row_idx += 1
        log_header_frame.grid_columnconfigure(0, weight=1)
        log_header_frame.grid_columnconfigure(1, weight=0)
        self._create_log_header(log_header_frame)

        self.log_content_frame = ctk.CTkFrame(main, fg_color="white", corner_radius=10, border_width=1,
                                              border_color="#e1e1e1")
        self.log_content_frame.grid(row=row_idx, column=0, sticky="nsew", pady=(0, 0));
        row_idx += 1
        main.grid_rowconfigure(row_idx - 1, weight=1)

        self.log_text = ctk.CTkTextbox(
            self.log_content_frame,
            font=ctk.CTkFont(family="Consolas", size=10),
            wrap="word", corner_radius=8
        )
        self.log_text.pack(fill="both", expand=True, padx=10, pady=10)

        self._configure_log_tags()
        self._sync_rule_vars_from_config()

        self.log(TEXTS['log_start'], "info")

        self.protocol("WM_DELETE_WINDOW", self.on_closing)

    def _sync_rule_vars_from_config(self) -> None:
        """将 enabled_rules 同步到界面复选框。"""
        for key, var in self.rule_vars.items():
            var.set(self.enabled_rules.get(key, False))
            text_color = GREEN_COLOR if var.get() else "#333333"
            if key in self.rule_checkboxes:
                self.rule_checkboxes[key].configure(text_color=text_color)
        self._update_label_colors()

    def _update_rule_state_and_display(self, key: str, var: ctk.BooleanVar) -> None:
        """复选框回调：将规则开关同步到内部状态，并更新相关标签颜色。"""
        new_state = var.get()
        self.enabled_rules[key] = new_state
        text_color = GREEN_COLOR if new_state else "#333333"
        self.rule_checkboxes[key].configure(text_color=text_color)

        if key in ['current_overload', 'current_unbalance']:
            self._update_label_colors()

    def _apply_thresholds_changes(self) -> None:
        """校验所有阈值输入，合法值写入实例属性，非法值恢复默认并记录日志。"""
        valid_change = False
        threshold_details: List[str] = []

        RANGE_VALIDATION = {
            'V_MIN_THRESHOLD': (0.0, 1000.0), 'V_MAX_THRESHOLD': (0.0, 1000.0),
            'I_MAX_THRESHOLD': (0.0, 100000.0),
            'I_UNBALANCE_MAX_THRESHOLD': (0.0, 1.0), 'PF_MIN_THRESHOLD': (0.0, 1.0),
            'I_MIN_ACTIVE_THRESHOLD': (0.001, float('inf')),
            'T_MIN_THRESHOLD': (-50.0, 200.0), 'T_MAX_THRESHOLD': (-50.0, 200.0),
            'V_IMBALANCE_THRESHOLD': (0.001, 1.0),
            'FREEZE_STD_THRESHOLD': (0.0, 1.0),
            'FREEZE_COUNT_THRESHOLD': (1, 1000),
            'P_ACTIVE_MIN_THRESHOLD': (float('-inf'), float('inf')),
        }

        THRESHOLD_NAMES_MAP = {
            'V_MIN_THRESHOLD': '电压下限 (U_MIN)', 'V_MAX_THRESHOLD': '电压上限 (U_MAX)',
            'I_MAX_THRESHOLD': '电流上限 (I_MAX)',
            'T_MIN_THRESHOLD': '温度下限 (T_MIN)',
            'T_MAX_THRESHOLD': '温度上限 (T_MAX)',
            'I_UNBALANCE_MAX_THRESHOLD': '电流不平衡度上限 (I_UNBAL)',
            'P_ACTIVE_MIN_THRESHOLD': '有功功率下限 (P_MIN)',
            'PF_MIN_THRESHOLD': '功率因数下限 (PF_MIN)',
            'I_MIN_ACTIVE_THRESHOLD': '电流激活下限 (I_ACTIVE)',
            'V_IMBALANCE_THRESHOLD': '相电压不平衡度 (V_IMBALANCE)',
            'FREEZE_COUNT_THRESHOLD': '冻结持续时间 (FREEZE_COUNT)',
            'FREEZE_STD_THRESHOLD': '冻结波动阈值 (FREEZE_STD)',
        }

        for key, var in self.threshold_vars.items():
            original_value_str = var.get()
            is_valid_input = False

            try:
                value = float(original_value_str)
                is_valid_input = True

                if key in RANGE_VALIDATION:
                    min_val, max_val = RANGE_VALIDATION[key]
                    if not (min_val <= value <= max_val):
                        self.log(
                            f"错误：'{THRESHOLD_NAMES_MAP.get(key, key)}' 输入值 {value} 超出合理范围 [{min_val} 到 {max_val}]。",
                            "error")
                        is_valid_input = False

                if is_valid_input:
                    current_value = getattr(self, key)
                    if current_value != value:
                        threshold_details.append(
                            f"{THRESHOLD_NAMES_MAP.get(key, key)} 从 {self._format_value(key, current_value)} 更改为 {self._format_value(key, value)}"
                        )
                        setattr(self, key, value)
                        valid_change = True

            except ValueError:
                self.log(
                    f"警告：'{THRESHOLD_NAMES_MAP.get(key, key)}' 输入值 '{original_value_str}' 无效，已恢复为默认值。",
                    "alert")
                is_valid_input = False

            if not is_valid_input:
                default_val = self.DEFAULT_THRESHOLDS.get(key)
                self.threshold_vars[key].set(self._format_value(key, default_val))
                setattr(self, key, default_val)

        # --- 交叉验证：确保下限 < 上限 ---
        for lower_key, upper_key, name_pair in [
            ('V_MIN_THRESHOLD', 'V_MAX_THRESHOLD', '电压'),
            ('T_MIN_THRESHOLD', 'T_MAX_THRESHOLD', '温度'),
        ]:
            lower_val = getattr(self, lower_key)
            upper_val = getattr(self, upper_key)
            if lower_val >= upper_val:
                self.log(
                    f"错误：'{name_pair}' 下限 ({self._format_value(lower_key, lower_val)}) "
                    f"必须小于上限 ({self._format_value(upper_key, upper_val)})，已恢复默认值。",
                    "error"
                )
                default_lower = self.DEFAULT_THRESHOLDS[lower_key]
                default_upper = self.DEFAULT_THRESHOLDS[upper_key]
                setattr(self, lower_key, default_lower)
                setattr(self, upper_key, default_upper)
                self.threshold_vars[lower_key].set(self._format_value(lower_key, default_lower))
                self.threshold_vars[upper_key].set(self._format_value(upper_key, default_upper))
                threshold_details.append(
                    f"{name_pair}阈值违规，已恢复为默认值 ({self._format_value(lower_key, default_lower)} / "
                    f"{self._format_value(upper_key, default_upper)})"
                )
                valid_change = True

        # 关键：更新标签颜色
        self._update_label_colors()

        if threshold_details:
            self.log(TEXTS['log_applied'], "info")
            for detail in threshold_details:
                self.log(f"    - {detail}", "skip")
        elif not valid_change:
            self.log("没有检测到有效阈值修改，或无效输入已恢复为默认值。", "skip")

        self.save_config()

    def load_config(self) -> None:
        """从 config.json 加载阈值配置，若文件缺失或无效则使用默认值。"""
        loaded_thresholds = dict(DEFAULT_CONFIG)

        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, 'r', encoding='utf-8') as config_file:
                    data = json.load(config_file)
                if isinstance(data, dict):
                    loaded_thresholds = normalize_config(data)
                else:
                    self.log(f"配置文件格式不正确，已使用默认阈值。", "error")
            except Exception as e:
                self.log(f"配置加载失败，已使用默认值: {e}", "error")

        for key, value in loaded_thresholds.items():
            if key in RULE_CONFIG_KEYS:
                self.enabled_rules[key] = bool(value)
            else:
                setattr(self, key, value)

    def save_config(self) -> None:
        """保存当前阈值配置到 config.json。"""
        config_data = {}
        for key in self.DEFAULT_THRESHOLDS:
            if key not in RULE_CONFIG_KEYS:
                val = getattr(self, key)
                # FREEZE_COUNT_THRESHOLD 保存为整数，避免类型不一致
                if key == 'FREEZE_COUNT_THRESHOLD':
                    val = int(val)
                config_data[key] = val
        for key in RULE_CONFIG_KEYS:
            config_data[key] = self.enabled_rules.get(key, False)
        try:
            config_dir = os.path.dirname(self.config_path)
            if config_dir and not os.path.exists(config_dir):
                os.makedirs(config_dir, exist_ok=True)

            with open(self.config_path, 'w', encoding='utf-8') as config_file:
                json.dump(config_data, config_file, ensure_ascii=False, indent=4)
            self.log(f"配置已保存：{os.path.basename(self.config_path)}", "info")
        except OSError as e:
            self.log(f"配置保存失败（权限或文件系统错误）：{e}", "error")
        except Exception as e:
            self.log(f"配置保存失败：{e}", "error")

    def _validate_and_update_threshold_callback(self, key: str, var: ctk.StringVar, event=None):
        """阈值输入实时校验回调。

        仅在输入过程中静默地尝试 float 转换，错误时忽略而不弹窗，
        避免用户在输入中途（如临时为空或输入非数字字符）被连续的异常中断。
        """
        try:
            float(var.get())
        except ValueError:
            pass
        except TclError:
            pass

    def _update_rules_display(self) -> None:
        """刷新规则面板的视觉状态，仅更新标签颜色，不销毁/重建控件。"""
        self._update_label_colors()

    def _update_label_colors(self) -> None:
        """按规则启用状态更新阈值标签的文字颜色（绿色=启用，灰色=禁用）。

        通过 winfo_exists 检测控件是否已销毁，避免多线程 TclError。
        """
        for key, label in self.threshold_labels.items():
            try:
                if not label.winfo_exists():
                    continue

                rule_link_type = self.threshold_label_links.get(key, 'core')

                color = GREEN_COLOR
                if rule_link_type == 'optional_link_current_overload':
                    is_enabled = self.enabled_rules.get('current_overload', True)
                    color = GREEN_COLOR if is_enabled else GREY_COLOR
                elif rule_link_type == 'optional_link_current_unbalance':
                    is_enabled = self.enabled_rules.get('current_unbalance', False)
                    color = GREEN_COLOR if is_enabled else GREY_COLOR
                elif rule_link_type == 'optional_link_pf':
                    is_enabled = self.enabled_rules.get('power_factor', False)
                    color = GREEN_COLOR if is_enabled else GREY_COLOR

                label.configure(text_color=color)
            except TclError:
                pass  # 忽略已销毁控件的错误

    def _create_rules_widgets(self) -> None:
        """在规则面板中构建阈值输入控件（标签、输入框、应用按钮）。

        将 11 个阈值按功能分为左栏（核心参数）和右栏（可选规则参数），
        同时为每个输入框绑定 trace_add 和焦点事件用于实时校验。
        """
        detail_frame = ctk.CTkFrame(self.rules_display_content_frame, fg_color="#fafafa")
        detail_frame.pack(fill="both", expand=True, padx=10, pady=5)

        detail_frame.grid_columnconfigure(0, weight=1)
        detail_frame.grid_columnconfigure(1, weight=1)

        left_frame = ctk.CTkFrame(detail_frame, fg_color="#fafafa")
        left_frame.grid(row=0, column=0, sticky="nsew", padx=(5, 10))
        right_frame = ctk.CTkFrame(detail_frame, fg_color="#fafafa")
        right_frame.grid(row=0, column=1, sticky="nsew", padx=(10, 5))

        editable_fields = [
            ('V_MIN_THRESHOLD', '电压 U_MIN: (下限)', left_frame, 'core'),
            ('V_MAX_THRESHOLD', '电压 U_MAX: (上限)', left_frame, 'core'),
            ('I_MAX_THRESHOLD', '电流 I_MAX: (上限)', left_frame, 'optional_link_current_overload'),
            ('T_MIN_THRESHOLD', '温度 T_MIN: (下限)', left_frame, 'core'),
            ('T_MAX_THRESHOLD', '温度 T_MAX: (上限)', left_frame, 'core'),
            ('I_UNBALANCE_MAX_THRESHOLD', '电流不平衡 (I_UNBAL): (上限)', right_frame, 'optional_link_current_unbalance'),
            ('P_ACTIVE_MIN_THRESHOLD', '有功功率 (P_MIN): (下限)', right_frame, 'core'),
            ('PF_MIN_THRESHOLD', '功率因数 (PF_MIN): (下限)', right_frame, 'optional_link_pf'),
            ('I_MIN_ACTIVE_THRESHOLD', '电流激活 (I_ACTIVE): (下限)', right_frame, 'optional_link_current_unbalance'),
            ('V_IMBALANCE_THRESHOLD', '相电压不平衡 (V_IMBAL): (偏差比)', right_frame, 'core'),
            ('FREEZE_COUNT_THRESHOLD', '冻结持续时间 (FREEZE_COUNT): (点数)', right_frame, 'core'),
            ('FREEZE_STD_THRESHOLD', '冻结波动阈值 (FREEZE_STD): (标准差)', right_frame, 'core'),
        ]

        current_row_left = 0
        current_row_right = 0
        for key, label, frame, rule_link_type in editable_fields:
            current_value = self._format_value(key, getattr(self, key))

            if key not in self.threshold_vars:
                self.threshold_vars[key] = ctk.StringVar()
            var = self.threshold_vars[key]

            var.set(current_value)

            if frame == left_frame:
                frame_row = current_row_left
                current_row_left += 1
            else:
                frame_row = current_row_right
                current_row_right += 1

            # 颜色初始设置
            color = GREEN_COLOR
            if rule_link_type == 'optional_link_current_overload' and not self.enabled_rules.get('current_overload', True):
                color = GREY_COLOR
            elif rule_link_type == 'optional_link_current_unbalance' and not self.enabled_rules.get('current_unbalance', False):
                color = GREY_COLOR
            elif rule_link_type == 'optional_link_pf' and not self.enabled_rules.get('power_factor', False):
                color = GREY_COLOR

            label_widget = ctk.CTkLabel(
                frame, text=label, anchor="w",
                font=ctk.CTkFont(family="Microsoft YaHei UI", size=11),
                text_color=color
            )
            label_widget.grid(row=frame_row, column=0, sticky="w", padx=18, pady=3)

            entry = ctk.CTkEntry(
                frame, width=80, height=24,
                font=ctk.CTkFont(family="Consolas", size=11),
                textvariable=var,
                fg_color="white",
                border_color="#909090",
                border_width=1
            )

            entry.grid(row=frame_row, column=1, sticky="w", padx=(0, 5), pady=3)

            # 存储引用
            self.threshold_entries[key] = entry
            self.threshold_labels[key] = label_widget
            self.threshold_label_links[key] = rule_link_type

            var.trace_add("write", lambda *args, k=key, v=var: self._validate_and_update_threshold_callback(k, v))
            entry.bind("<FocusOut>",
                       lambda event, k=key, v=var: self._validate_and_update_threshold_callback(k, v, event))
            entry.bind("<Return>",
                       lambda event, k=key, v=var: self._validate_and_update_threshold_callback(k, v, event))

        frame.grid_columnconfigure(0, weight=1)
        frame.grid_columnconfigure(1, weight=0)

        self.apply_btn = ctk.CTkButton(
            right_frame, text=TEXTS['btn_apply'], width=100, height=30,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color=BLUE_COLOR, hover_color="#106ebe", corner_radius=8,
            command=self._apply_thresholds_changes
        )
        self.apply_btn.grid(row=current_row_right, column=1, sticky="e", padx=(0, 5), pady=(15, 5))
        current_row_right += 1

        needed_padding = current_row_left - current_row_right
        if needed_padding > 0:
            for i in range(needed_padding):
                ctk.CTkFrame(right_frame, height=26, fg_color="#fafafa").grid(row=current_row_right + i, column=0,
                                                                              columnspan=2, sticky="ew")

        ctk.CTkFrame(left_frame, height=1, fg_color="transparent").grid(row=current_row_left, column=0, columnspan=2,
                                                                        sticky="ew", pady=2)
        ctk.CTkFrame(right_frame, height=1, fg_color="transparent").grid(row=current_row_right + needed_padding,
                                                                         column=0, columnspan=2, sticky="ew", pady=2)

        self.rules_display_card.update_idletasks()

    def _create_rules_header(self, parent: ctk.CTkFrame) -> None:
        """构建规则面板的折叠/展开按钮。"""
        self.rules_toggle_btn = ctk.CTkButton(
            parent, text=f"▽ {TEXTS['rule_main_title']}",
            height=34,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color="white", text_color="#333333", hover_color="#f0f0f0",
            anchor="w", corner_radius=10, border_width=1, border_color="#e1e1e1",
            command=self._toggle_rules_panel
        )
        self.rules_toggle_btn.grid(row=0, column=0, sticky="ew")

    def _toggle_rules_panel(self, state: bool = None) -> None:
        """切换规则面板的显示/隐藏，同时更新按钮文字为 ▽/△。"""
        is_mapped = self.rules_display_card.winfo_ismapped()
        target_state = not is_mapped if state is None else state

        if target_state:
            self.rules_display_card.grid()
            self.rules_toggle_btn.configure(text=f"△ {TEXTS['rule_main_title']}")
        else:
            self.rules_display_card.grid_remove()
            self.rules_toggle_btn.configure(text=f"▽ {TEXTS['rule_main_title']}")

    def _configure_log_tags(self) -> None:
        """为日志文本框注册颜色 tag，用于按日志级别（info/skip/alert/error/success）着色。"""
        if self.log_text:
            self.log_text.tag_config("info", foreground=GREEN_COLOR)
            self.log_text.tag_config("skip", foreground=GREY_COLOR)
            self.log_text.tag_config("alert", foreground="#d83b01")
            self.log_text.tag_config("error", foreground="#d13438")
            self.log_text.tag_config("success", foreground="#0078d4")

    def _create_log_header(self, parent: ctk.CTkFrame) -> None:
        """构建日志区域标题栏（含"操作日志"标签 + 导出/清空按钮）。"""
        ctk.CTkLabel(
            parent, text=TEXTS['log_title'],
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            text_color="#333333", anchor="w"
        ).grid(row=0, column=0, sticky="w", padx=2)

        ctk.CTkButton(
            parent, text=TEXTS['btn_export_log'], width=80, height=28,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=10),
            fg_color="transparent", border_width=1, border_color="#cccccc",
            text_color="#333333", hover_color="#f0f0f0", corner_radius=6,
            command=self.export_log
        ).grid(row=0, column=1, sticky="e", padx=(0, 6))

        ctk.CTkButton(
            parent, text=TEXTS['btn_clear_log'], width=80, height=28,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=10),
            fg_color="transparent", border_width=1, border_color="#cccccc",
            text_color="#333333", hover_color="#f0f0f0", corner_radius=6,
            command=self.clear_log
        ).grid(row=0, column=2, sticky="e")

    def _create_directory_cards(self, parent: ctk.CTkFrame, start_row: int) -> None:
        """构建输入目录和报告目录选择卡片（含标签 + 选择按钮）。"""
        folder_card = ctk.CTkFrame(parent, height=68, fg_color="white", corner_radius=10, border_width=1,
                                   border_color="#e1e1e1")
        folder_card.grid(row=start_row, column=0, sticky="ew", pady=(0, 14))
        folder_card.grid_propagate(False)
        folder_card.grid_columnconfigure(0, weight=1)

        self.folder_label = ctk.CTkLabel(
            folder_card, text=TEXTS['folder_default'], anchor="w",
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=13),
            text_color="#333333", padx=18
        )
        self.folder_label.grid(row=0, column=0, sticky="ew", padx=(18, 10), pady=14)

        ctk.CTkButton(
            folder_card, text=TEXTS['btn_select_input'], width=148, height=38,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color="#0078d4", hover_color="#106ebe", corner_radius=8,
            command=self.select_folder
        ).grid(row=0, column=1, padx=(0, 18), pady=14)

        report_card = ctk.CTkFrame(parent, height=68, fg_color="white", corner_radius=10, border_width=1,
                                   border_color="#e1e1e1")
        report_card.grid(row=start_row + 1, column=0, sticky="ew", pady=(0, 14))
        report_card.grid_propagate(False)
        report_card.grid_columnconfigure(0, weight=1)

        self.report_label = ctk.CTkLabel(
            report_card, text=TEXTS['report_default'], anchor="w",
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=13),
            text_color="#333333", padx=18
        )
        self.report_label.grid(row=0, column=0, sticky="ew", padx=(18, 10), pady=14)

        ctk.CTkButton(
            report_card, text=TEXTS['btn_select_report'], width=148, height=38,
            font=ctk.CTkFont(family="Microsoft YaHei UI", size=12, weight="bold"),
            fg_color="#0078d4", hover_color="#106ebe", corner_radius=8,
            command=self.select_report_path
        ).grid(row=0, column=1, padx=(0, 18), pady=14)

    def select_report_path(self) -> None:
        """弹出目录选择对话框设置报告输出路径，并更新标签和日志。"""
        path = filedialog.askdirectory(title="请选择报告保存目录")
        if path:
            self.report_path = path
            self.report_path_is_custom = True
            self.initial_report_path = path
            display = path if len(path) < 60 else "..." + path[-57:]
            self.report_label.configure(text=display)
            self.log(f"报告目录已更改为：{os.path.basename(path)}", "info")

    def select_folder(self) -> None:
        """弹出目录选择对话框设置输入目录，并调 `set_folder` 完成配置。"""
        folder = filedialog.askdirectory(title="请选择包含 CSV 文件的目录")
        if folder:
            self.set_folder(folder)

    def set_folder(self, path: str) -> None:
        """设置输入目录并统计 CSV 文件数，同步更新报告路径（若未自定义）。

        Args:
            path: 用户选择的输入目录。
        """
        if not self.report_path_is_custom:
            self.report_path = path
            self.initial_report_path = path
            report_display = path if len(path) < 60 else "..." + path[-57:]
            self.report_label.configure(text=report_display)

        self.folder_path = path
        count = len(glob.glob(os.path.join(path, '**', '*.csv'), recursive=True))
        folder_display = path if len(path) < 60 else "..." + path[-57:]

        self.folder_label.configure(text=folder_display)
        self.status.configure(text=f"已选择目录 · 共发现 {count} 个 CSV 文件")
        self.log(f"输入目录：{os.path.basename(path)}（{count} 个文件）", "info")

    def clear_log(self) -> None:
        """清空日志文本框全部内容并写入确认消息。"""
        self.log_text.delete("1.0", "end")
        self.log("日志已清空", "skip")

    def export_log(self) -> None:
        """将日志文本框内容导出为 UTF-8 文本文件。"""
        log_data = self.log_text.get("1.0", "end-1c")
        if not log_data.strip():
            self.log("日志为空，未导出任何内容。", "skip")
            return

        initial_file = f"检测日志_{datetime.now().strftime('%Y%m%d_%H%M%S')}.txt"
        save_path = filedialog.asksaveasfilename(
            defaultextension=".txt",
            filetypes=[("Text files", "*.txt")],
            initialfile=initial_file,
            title="保存日志为"
        )
        if not save_path:
            return

        try:
            with open(save_path, 'w', encoding='utf-8') as file:
                file.write(log_data)
            self.log(f"日志已导出：{os.path.basename(save_path)}", "success")
        except Exception as e:
            self.log(f"日志导出失败：{e}", "error")

    def log(self, msg: str, tag: str = "info") -> None:
        """将日志消息放入队列，由主线程 `_process_log_queue` 定时渲染。

        使用队列而非直接更新 UI 是为了保证线程安全——检测线程也调用此方法。

        Args:
            msg: 日志消息文本。
            tag: tag 名称，对应 _configure_log_tags 中配置的颜色。
        """
        self.log_queue.put((msg, tag))

    def _process_log_queue(self) -> None:
        """定时从队列中取出日志消息并写入 Textbox，单次最多处理 50 条防止阻塞。"""
        processed = 0
        while not self.log_queue.empty() and processed < 50:
            msg, tag = self.log_queue.get()
            ts = datetime.now().strftime("%H:%M:%S")
            self.log_text.insert("end", f"[{ts}] {msg}\n", tag)
            self.log_text.see("end")
            processed += 1
        self.after(100, self._process_log_queue)

    def start_detection_thread(self) -> None:
        """启动检测线程：先应用阈值修改，快照规则状态，再在后台线程执行扫描。"""
        self._apply_thresholds_changes()

        if not self.folder_path:
            messagebox.showerror("输入错误", TEXTS['err_no_folder'])
            return
        self._stop_detection.clear()
        self.after(0, lambda: self._update_ui_state("start"))
        # 主线程快照规则状态，避免与工作线程竞争
        with self._lock:
            self._snapshot_enabled_rules = dict(self.enabled_rules)
        threading.Thread(target=self.start_detection, daemon=True).start()

    def request_stop_detection(self) -> None:
        """设置停止标志并禁用按钮，等待检测线程在当前文件处理完毕后退出。"""
        if not self._stop_detection.is_set():
            self._stop_detection.set()
            self.log("正在停止检测（将在当前文件处理完成后停止）...", "skip")
            self.start_btn.configure(state="disabled", text="正在停止...")

    def start_detection(self) -> None:
        """主检测循环：遍历所有 CSV 文件执行异常检测，实时更新进度并输出汇总报告。

        在后台线程中运行。支持暂停/恢复/取消。
        """
        import pandas as pd

        self.after(0, lambda: self._toggle_rules_panel(False))

        files = list(glob.iglob(os.path.join(self.folder_path, '**', '*.csv'), recursive=True))
        total = len(files)

        if not total:
            self.after(0, lambda: messagebox.showinfo("提示", TEXTS['err_no_file']))
            self.after(0, lambda: self._update_ui_state("finish"))
            return

        processed = 0
        written_records = 0
        involved_files = 0
        self.start_time = time.time()
        ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        out_file = os.path.join(self.report_path, f"电气异常报告_{ts}.xlsx")
        self.log(f"开始分析 {total} 个文件...", "info")

        current_thresholds = self._build_thresholds_dict()
        with self._lock:
            current_rules = dict(getattr(self, '_snapshot_enabled_rules', self.enabled_rules))

        anomaly_batches: List[Any] = []

        # --- 汇总跟踪变量 ---
        normal_count = 0
        skipped_files_with_reason: List[Tuple[str, str]] = []
        skipped_details: List[Dict[str, Any]] = []
        frozen_acquisition: List[Tuple[str, str]] = []
        transformer_issues: Dict[Tuple[str, str], Dict[str, Any]] = {}
        offline_devices: List[Tuple[str, str]] = []
        sensor_fault_list: List[Tuple[str, str, str]] = []
        sensor_missing_rates: Dict[str, List[float]] = {}
        sensor_status_rows: List[Dict[str, Any]] = []

        update_counter = 0
        cancelled = False
        for fp in files:
            if self._stop_detection.is_set():
                cancelled = True
                break
            processed += 1
            file_name = os.path.basename(fp)

            update_counter += 1
            if update_counter % 20 == 0 or processed == total:
                self.after(0, lambda p=processed, t=total, name=file_name:
                           self._update_progress_status(p, t, name))

            anomalies, log_data, cleaned_df, extra_info = check_anomaly_in_file(fp, current_thresholds, current_rules)

            if isinstance(log_data, dict):
                msg, tag = _format_log_message(
                    file_name=log_data['filename'],
                    anomalies_count=log_data['count'],
                    anomaly_types=log_data['types'],
                    pure_freeze=log_data.get('pure_freeze', False),
                    freeze_filtered_count=log_data.get('freeze_filtered_count', 0),
                    sensor_missing=extra_info.get('sensor_missing', []),
                )
            elif "跳过" in log_data or "读取失败" in log_data:
                msg, tag = _format_log_message(
                    file_name=file_name,
                    error_msg=log_data,
                    sensor_missing=extra_info.get('sensor_missing', []),
                )
            else:
                msg = log_data
                tag = "info"

            self.log(msg, tag)

            # --- 更新汇总变量 ---
            bld, trans = _extract_building_and_transformer(fp, self.folder_path)
            try:
                rel_path = os.path.relpath(fp, self.folder_path)
            except ValueError:
                rel_path = file_name

            if extra_info.get('is_offline'):
                offline_devices.append((bld, trans))
            for sensor_col in extra_info.get('sensor_faults', []):
                sensor_fault_list.append((bld, trans, sensor_col))
            if extra_info:
                sensor_status_rows.append({
                    "来源文件": file_name,
                    "建筑": bld,
                    "相对路径": rel_path,
                    "变压器": trans,
                    "是否离线": "是" if extra_info.get('is_offline') else "否",
                    "传感器故障": "、".join(extra_info.get('sensor_faults', [])),
                    "传感器未配置": "、".join(extra_info.get('sensor_missing', [])),
                })

            if isinstance(log_data, dict) and anomalies is not None and not anomalies.empty:
                anomaly_batches.append(enrich_anomalies(anomalies, bld, trans, rel_path))
                written_records += len(anomalies)
                involved_files += 1
                self.last_report_file = out_file

                # 离线设备只写 Excel 明细，不进入采集故障/传感器统计
                if not extra_info.get('is_offline'):
                    is_frozen = _check_frozen_acquisition(cleaned_df) if cleaned_df is not None else False
                    if is_frozen:
                        frozen_acquisition.append((bld, trans))

                    if not is_frozen:
                        if cleaned_df is not None:
                            for col in TARGET_SHORT_NAMES_REPORT:
                                if col in cleaned_df.columns:
                                    n_total = len(cleaned_df)
                                    n_missing = int(cleaned_df[col].isna().sum())
                                    if n_total > 0:
                                        rate = n_missing / n_total
                                        if col not in sensor_missing_rates:
                                            sensor_missing_rates[col] = []
                                        sensor_missing_rates[col].append(rate)

                        issues = _extract_transformer_issues(anomalies, cleaned_df)
                        if issues:
                            key = (bld, trans)
                            if key not in transformer_issues:
                                transformer_issues[key] = {}
                            transformer_issues[key].update(issues)

            elif "跳过" in log_data or "读取失败" in log_data:
                skipped_files_with_reason.append((file_name, log_data))
                skipped_details.append({
                    "来源文件": file_name,
                    "建筑": bld,
                    "相对路径": rel_path,
                    "变压器": trans,
                    "状态": "跳过",
                    "原因": log_data,
                })
            elif tag == "info" and "正常" in msg:
                normal_count += 1

        self.end_time = time.time()
        duration = self.end_time - self.start_time if self.start_time else 0.0
        duration_text = self._format_duration_text(duration, processed if cancelled else total)
        processed_total = processed if cancelled else total

        report_file = None
        if anomaly_batches:
            try:
                report_file = write_excel_report(
                    out_file,
                    pd.concat(anomaly_batches, ignore_index=True, sort=False),
                    summary={
                        "input_dir": self.folder_path,
                        "output_dir": self.report_path,
                        "total_files": total,
                        "processed_files": processed_total,
                        "normal_files": normal_count,
                        "anomaly_files": involved_files,
                        "anomaly_records": written_records,
                        "skipped_files": len(skipped_files_with_reason),
                        "generated_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                        "duration": duration_text,
                    },
                    config={**current_thresholds, **current_rules},
                    sensor_status_rows=sensor_status_rows,
                    skipped_details=skipped_details,
                )
                self.last_report_file = str(report_file)
            except Exception as e:
                self.log(f"Excel 报告生成失败：{e}", "error")

        # --- 输出汇总 ---
        self._output_detection_summary(
            total=processed_total,
            normal_count=normal_count,
            written_records=written_records,
            transformer_issues=transformer_issues,
            frozen_acquisition=frozen_acquisition,
            offline_devices=offline_devices,
            sensor_faults=sensor_fault_list,
            skipped_files_with_reason=skipped_files_with_reason,
            sensor_missing_rates=sensor_missing_rates,
            cancelled=cancelled,
            duration_text=duration_text,
        )

        if cancelled:
            summary_text = (
                f"检测已取消：已处理 {processed}/{total} 文件，异常 {written_records} 条；{duration_text}"
            )
            self.log(summary_text, "alert")
        else:
            summary_text = (
                f"检测完成：{total} 文件，异常 {written_records} 条，异常文件 {involved_files} 个；{duration_text}"
            )
        self.after(0, lambda s=summary_text: self.status.configure(text=s))

        if cancelled:
            self.after(0, lambda p=processed, t=total: messagebox.showinfo(
                "检测已取消",
                f"已处理 {p}/{t} 个文件。\n部分结果可能已写入 Excel 报告文件。",
            ))
        elif written_records > 0:
            display_file = str(report_file or out_file)
            self.log(f"Excel 异常报告已生成：{os.path.basename(display_file)}，共写入 {written_records} 条异常记录。", "success")
            self.after(0, lambda out=display_file, count=written_records: messagebox.showinfo(
                "检测完成",
                f"共发现 {count} 条异常记录\n\n"
                f"报告已保存至：\n{out}"
            ))
        elif not cancelled:
            self.log("全部文件正常，未发现异常数据", "info")
            self.after(0, lambda: messagebox.showinfo("检测完成", "未发现任何异常参数，所有文件均正常。"))

        if cancelled and total:
            self.after(0, lambda p=processed, t=total: self.progress.set(p / t))
        elif not cancelled:
            self.after(0, lambda: self.progress.set(1))
        self.after(0, lambda: self._update_ui_state("finish"))

    def _output_detection_summary(
        self,
        total: int,
        normal_count: int,
        written_records: int,
        transformer_issues: Dict[Tuple[str, str], Dict[str, Dict[str, Any]]],
        frozen_acquisition: List[Tuple[str, str]],
        offline_devices: List[Tuple[str, str]],
        sensor_faults: List[Tuple[str, str, str]],
        skipped_files_with_reason: List[Tuple[str, str]],
        sensor_missing_rates: Dict[str, List[float]],
        cancelled: bool,
        duration_text: str,
    ) -> None:
        """输出紧凑型检测汇总报告：建筑 → 变压器 → 故障类型（含次数、参数范围、时间范围）。"""
        from collections import defaultdict

        skipped_count = len(skipped_files_with_reason)
        anomaly_files = len(transformer_issues)
        status = "已取消" if cancelled else "完成"
        short_duration = duration_text.split('(')[0].strip().replace('总耗时: ', '')

        self.log("", "info")
        self.log("=" * 60, "info")
        self.log(f"  E-Detection 检测汇总                      {status} · {short_duration}", "info")
        self.log("=" * 60, "info")
        self.log(f"  {total}文件 → 正常{normal_count} · 异常{anomaly_files}({written_records}条) · 跳过{skipped_count}", "info")

        if not transformer_issues and not frozen_acquisition and not offline_devices and not sensor_faults:
            if normal_count == total - skipped_count:
                self.log("", "info")
                self.log("  ✓ 未发现任何异常", "success")
            self.log("=" * 60, "info")
            self.log("", "info")
            return

        # --- 按建筑物分组（紧凑格式：变压器一行汇总） ---
        building_data: Dict[str, List[Tuple[str, Dict[str, Dict[str, Any]]]]] = defaultdict(list)
        for (bld, trans), issues in transformer_issues.items():
            building_data[bld].append((trans, issues))

        for bld in sorted(building_data.keys()):
            self.log("", "info")
            self.log(f"  {bld}", "info")
            for trans, issues in sorted(building_data[bld], key=lambda x: x[0]):
                sorted_issues = sorted(issues.items(), key=lambda x: x[1]['count'], reverse=True)
                issue_parts = [f"{tp}({inf['count']}次)" for tp, inf in sorted_issues]
                self.log(f"    {trans}  {'  '.join(issue_parts)}", "alert")

        # --- 特殊项 ---
        self.log("", "info")

        # 传感器未配置一览（改进5：结构化的传感器缺失表）
        if sensor_missing_rates:
            self.log("  ▎传感器未配置一览", "heading")
            missing_items = []
            for col in TARGET_SHORT_NAMES_REPORT:
                rates = sensor_missing_rates.get(col, [])
                if not rates:
                    continue
                n_files = len(rates)
                missing_items.append(f"{col}: {n_files}个文件")
            if missing_items:
                self.log(f"    {' · '.join(missing_items)}", "skip")
            self.log("", "info")

        if frozen_acquisition:
            by_b: Dict[str, List[str]] = defaultdict(list)
            for bld, trans in frozen_acquisition:
                by_b[bld].append(trans)
            parts = [f"{b}({' · '.join(sorted(set(t_list)))})" for b, t_list in sorted(by_b.items())]
            self.log(f"  ⚠ 采集故障: {'  '.join(parts)}", "alert")

        if offline_devices:
            by_b: Dict[str, List[str]] = defaultdict(list)
            for bld, trans in offline_devices:
                by_b[bld].append(trans)
            parts = [f"{b}({' · '.join(sorted(set(t_list)))})" for b, t_list in sorted(by_b.items())]
            self.log(f"  ⚠ 设备离线: {'  '.join(parts)}", "alert")

        if sensor_faults:
            by_b: Dict[str, List[str]] = defaultdict(list)
            for bld, trans, col in sensor_faults:
                by_b[bld].append(f"{trans}/{col}")
            parts = [f"{b}({' · '.join(sorted(set(d_list)))})" for b, d_list in sorted(by_b.items())]
            self.log(f"  ⚠ 传感器故障: {'  '.join(parts)}", "alert")

        if skipped_files_with_reason:
            reason_counts: Dict[str, int] = defaultdict(int)
            for _, reason in skipped_files_with_reason:
                if "高压" in reason:
                    reason_counts["高压设备"] += 1
                elif "未识别" in reason:
                    reason_counts["未识别字段"] += 1
                elif "读取失败" in reason:
                    reason_counts["读取失败"] += 1
                else:
                    reason_counts["其它"] += 1
            parts = [f"{r}{c}个" for r, c in sorted(reason_counts.items(), key=lambda x: x[1], reverse=True)]
            self.log(f"  ⏭ 跳过{skipped_count}: {' · '.join(parts)}", "skip")

        self.log("=" * 60, "info")
        self.log("", "info")

    def _update_progress_status(self, processed: int, total: int, filename: str) -> None:
        """更新进度条和状态标签。

        Args:
            processed: 已处理文件数。
            total: 总文件数。
            filename: 当前文件名。
        """
        self.progress.set(processed / total if total > 0 else 0)
        self.status.configure(text=f"处理中：{processed}/{total} | {filename}")

    def _update_ui_state(self, state: str) -> None:
        """切换 UI 控件状态（开始→运行中 或 运行中→完成）。

        "start": 按钮变为"停止检测"、禁用阈值输入和复选框。
        "finish": 恢复按钮和输入控件。
        """
        if state == "start":
            self.start_btn.configure(
                state="normal",
                text=TEXTS['btn_stop'],
                fg_color="#d83b01",
                hover_color="#a4262c",
                command=self.request_stop_detection,
            )
            self.progress.set(0)
            # 禁用规则复选框，防止检测过程中切换
            for chk in self.rule_checkboxes.values():
                chk.configure(state="disabled")
            # 禁用阈值输入框和应用按钮
            for entry in self.threshold_entries.values():
                entry.configure(state="disabled")
            if hasattr(self, 'apply_btn') and self.apply_btn.winfo_exists():
                self.apply_btn.configure(state="disabled")
        elif state == "finish":
            self.start_btn.configure(
                state="normal",
                text=TEXTS['btn_start'],
                fg_color="#0078d4",
                hover_color="#106ebe",
                command=self.start_detection_thread,
            )
            self._stop_detection.clear()
            # 恢复规则复选框
            for chk in self.rule_checkboxes.values():
                chk.configure(state="normal")
            # 恢复阈值输入框和应用按钮
            for entry in self.threshold_entries.values():
                entry.configure(state="normal")
            if hasattr(self, 'apply_btn') and self.apply_btn.winfo_exists():
                self.apply_btn.configure(state="normal")

if __name__ == '__main__':
    app = ElectricalAnomalyDetectorApp()
    app.mainloop()

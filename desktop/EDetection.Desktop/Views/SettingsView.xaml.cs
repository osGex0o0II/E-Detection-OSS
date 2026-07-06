using EDetection.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace EDetection.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    private const double ExpandedNavigationWidth = 184;
    private const double CompactNavigationWidth = 152;
    private const double ExpandedLabelWidth = 200;
    private const double CompactLabelWidth = 168;
    private const double SettingsContentMaxWidth = 900;
    private const double CompactBodyBreakpoint = 960;
    private const double CompactContentBreakpoint = 720;

    private bool _suppressCategoryScroll;
    private bool _suppressScrollSync;
    private int _navigationScrollVersion;
    private readonly List<Grid> _settingRows = [];
    private readonly IReadOnlyList<SettingsSearchEntry> _searchEntries;

    public SettingsView()
    {
        InitializeComponent();
        _searchEntries = BuildSearchEntries();
        _suppressCategoryScroll = true;
        SettingsCategoryList.SelectedIndex = 0;
        _suppressCategoryScroll = false;
        Loaded += SettingsView_Loaded;
        RegisterKeyboardAccelerators();
    }

    public event EventHandler? BackRequested;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public void NavigateToThresholdSettings()
    {
        NavigateToSectionByName("ThresholdsSection");
    }

    public void NavigateToDetectionRules()
    {
        NavigateToSectionByName("DetectionRulesSection");
    }

    public void PrepareForDisplay(string? initialSectionName = null)
    {
        _suppressCategoryScroll = true;
        _suppressScrollSync = true;
        SettingsCategoryList.SelectedIndex = 0;
        _suppressCategoryScroll = false;
        SettingsScroll.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() =>
        {
            SettingsScroll.UpdateLayout();
            SettingsScroll.ChangeView(null, 0, null, disableAnimation: true);
            _suppressScrollSync = false;
            if (string.IsNullOrWhiteSpace(initialSectionName))
            {
                UpdateSelectedCategoryFromScroll();
                return;
            }

            NavigateToSectionByName(initialSectionName);
        });
    }

    public void PlayEntranceAnimation(bool forward)
    {
        SettingsRoot.Opacity = 0;
        SettingsRootTransform.TranslateX = forward ? 28 : -28;

        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var opacityAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EnableDependentAnimation = true,
        };
        var translateAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacityAnimation, SettingsRoot);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(translateAnimation, SettingsRootTransform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(translateAnimation, "TranslateX");
        storyboard.Children.Add(opacityAnimation);
        storyboard.Children.Add(translateAnimation);
        storyboard.Begin();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void OpenThresholdSettings_Click(object sender, RoutedEventArgs e) =>
        NavigateToThresholdSettings();

    private void OpenDetectionRules_Click(object sender, RoutedEventArgs e) =>
        NavigateToDetectionRules();

    private void RegisterKeyboardAccelerators()
    {
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, (_, e) =>
        {
            SettingsSearchBox.Focus(FocusState.Keyboard);
            e.Handled = true;
        });
        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
            {
                return;
            }

            SettingsSearchBox.Text = "";
            SettingsSearchBox.ItemsSource = null;
            SettingsCategoryList.Focus(FocusState.Keyboard);
            e.Handled = true;
        });
    }

    private void AddShortcut(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> invoked)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += invoked;
        KeyboardAccelerators.Add(accelerator);
    }

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var query = sender.Text.Trim();
        sender.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? null
            : _searchEntries
                .Where(entry => entry.Matches(query))
                .Take(8)
                .ToList();
    }

    private void SettingsSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not SettingsSearchEntry entry)
        {
            return;
        }

        sender.Text = entry.Title;
        NavigateToSearchEntry(entry);
    }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SettingsSearchEntry chosenEntry)
        {
            NavigateToSearchEntry(chosenEntry);
            return;
        }

        var query = args.QueryText.Trim();
        var entry = _searchEntries.FirstOrDefault(item => item.Matches(query));
        if (entry is not null)
        {
            NavigateToSearchEntry(entry);
        }
    }

    private void NavigateToSearchEntry(SettingsSearchEntry entry)
    {
        if (entry.ExpandMoreThresholds)
        {
            MoreThresholdsExpander.IsExpanded = true;
        }

        NavigateToSectionByName(entry.SectionName);
    }

    public void NavigateToSectionByName(string sectionName)
    {
        SelectCategoryBySection(sectionName);
        NavigateToSection(sectionName);
    }

    private void SettingsCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategoryScroll)
        {
            return;
        }

        if (SettingsCategoryList.SelectedItem is not ListViewItem { Tag: string sectionName }
            || SettingsScroll is null)
        {
            return;
        }

        NavigateToSection(sectionName);
    }

    private void SelectCategoryBySection(string sectionName)
    {
        for (var i = 0; i < SettingsCategoryList.Items.Count; i++)
        {
            if (SettingsCategoryList.Items[i] is ListViewItem { Tag: string itemSection }
                && string.Equals(itemSection, sectionName, StringComparison.Ordinal))
            {
                _suppressCategoryScroll = true;
                SettingsCategoryList.SelectedIndex = i;
                _suppressCategoryScroll = false;
                return;
            }
        }
    }

    private void NavigateToSection(string sectionName)
    {
        if (FindName(sectionName) is not FrameworkElement section)
        {
            return;
        }

        _suppressScrollSync = true;
        var navigationVersion = ++_navigationScrollVersion;
        section.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0,
        });
        _ = ResumeScrollSyncAfterNavigationAsync(navigationVersion);
    }

    private void SettingsScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_suppressScrollSync)
        {
            UpdateSelectedCategoryFromScroll();
        }
    }

    private void SettingsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void SettingsBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        CacheSettingRows();
        UpdateResponsiveLayout();
    }

    private void CacheSettingRows()
    {
        _settingRows.Clear();
        CollectSettingRows(SettingsContentStack);
    }

    private void CollectSettingRows(DependencyObject root)
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Grid grid
                && grid.MinHeight >= 64
                && grid.ColumnDefinitions.Count == 2)
            {
                _settingRows.Add(grid);
            }

            CollectSettingRows(child);
        }
    }

    private void UpdateResponsiveLayout()
    {
        CacheSettingRows();
        var availableWidth = SettingsBody.ActualWidth;
        if (availableWidth <= 0)
        {
            return;
        }

        var compact = availableWidth < CompactBodyBreakpoint;
        SettingsBody.Padding = compact
            ? new Thickness(18, 18, 18, 0)
            : new Thickness(24, 22, 24, 0);
        SettingsBody.ColumnSpacing = compact ? 18 : 24;
        SettingsNavColumn.Width = new GridLength(compact ? CompactNavigationWidth : ExpandedNavigationWidth);

        var contentWidth = SettingsScroll.ActualWidth;
        if (contentWidth <= 0)
        {
            return;
        }

        SettingsContentStack.Width = Math.Min(contentWidth, SettingsContentMaxWidth);
        SettingsContentStack.MaxWidth = SettingsContentMaxWidth;

        var labelWidth = contentWidth < CompactContentBreakpoint ? CompactLabelWidth : ExpandedLabelWidth;
        foreach (var row in _settingRows)
        {
            var compactRow = contentWidth < CompactContentBreakpoint;
            row.ColumnSpacing = compactRow ? 14 : 20;
            row.Padding = compactRow
                ? new Thickness(16, 10, 16, 10)
                : new Thickness(18, 10, 18, 10);
            row.ColumnDefinitions[0].Width = new GridLength(labelWidth);
        }
    }

    private void SettingsExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        DispatcherQueue.TryEnqueue(UpdateResponsiveLayout);
    }

    private async Task ResumeScrollSyncAfterNavigationAsync(int navigationVersion)
    {
        await Task.Delay(280);
        if (navigationVersion != _navigationScrollVersion)
        {
            return;
        }

        _suppressScrollSync = false;
        UpdateSelectedCategoryFromScroll();
    }

    private void UpdateSelectedCategoryFromScroll()
    {
        var activeIndex = GetActiveSectionIndex();
        if (activeIndex < 0 || SettingsCategoryList.SelectedIndex == activeIndex)
        {
            return;
        }

        _suppressCategoryScroll = true;
        SettingsCategoryList.SelectedIndex = activeIndex;
        _suppressCategoryScroll = false;
    }

    private int GetActiveSectionIndex()
    {
        var sections = GetSettingSections();
        if (sections.Length == 0)
        {
            return -1;
        }

        if (SettingsScroll.ScrollableHeight > 0
            && SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - 1)
        {
            return sections.Length - 1;
        }

        var viewportHeight = SettingsScroll.ViewportHeight > 0
            ? SettingsScroll.ViewportHeight
            : SettingsScroll.ActualHeight;
        var activationY = Math.Clamp(viewportHeight * 0.34, 120, 300);
        var activeIndex = 0;

        for (var i = 0; i < sections.Length; i++)
        {
            var sectionTop = sections[i]
                .TransformToVisual(SettingsScroll)
                .TransformPoint(new Point(0, 0))
                .Y;

            if (sectionTop <= activationY)
            {
                activeIndex = i;
                continue;
            }

            break;
        }

        return activeIndex;
    }

    private FrameworkElement[] GetSettingSections() =>
    [
        AppearanceSection,
        DefaultsSection,
        ThresholdsSection,
        DetectionRulesSection,
        ReportsSection,
        LogsSection,
        WindowSection,
        LlmSection,
        NotificationsSection,
        ProxySection,
        UpdatesSection,
        AdvancedSection,
    ];

    private static IReadOnlyList<SettingsSearchEntry> BuildSearchEntries() =>
    [
        new("外观", "外观", "外观 主题 背景 mica acrylic 深色 浅色 跟随系统", "AppearanceSection"),
        new("应用主题", "外观", "外观 主题 深色 浅色 跟随系统", "AppearanceSection"),
        new("窗口背景", "外观", "外观 背景 mica acrylic", "AppearanceSection"),
        new("检测目录", "检测", "检测 输入目录 报告目录 csv 路径", "DefaultsSection"),
        new("输入目录", "检测", "检测 输入目录 csv 路径", "DefaultsSection"),
        new("报告目录", "检测", "检测 报告目录 输出目录 路径", "DefaultsSection"),
        new("检测参数", "检测", "检测 参数 阈值 设置 规则", "DefaultsSection"),
        new("阈值设置", "阈值设置", "阈值 电压 电流 功率 温度 冻结 不平衡", "ThresholdsSection"),
        new("阈值配置文件", "阈值设置", "阈值 配置 文件 json config", "ThresholdsSection"),
        new("电压阈值", "阈值设置", "阈值 电压 下限 上限 不平衡", "ThresholdsSection"),
        new("电流阈值", "阈值设置", "阈值 电流 上限 不平衡 激活", "ThresholdsSection", true),
        new("功率因数阈值", "阈值设置", "阈值 功率 因数 下限", "ThresholdsSection"),
        new("温度阈值", "阈值设置", "阈值 温度 上限 下限", "ThresholdsSection", true),
        new("冻结阈值", "阈值设置", "阈值 冻结 持续点数 波动", "ThresholdsSection", true),
        new("检测规则", "检测规则", "规则 电流过载 电流不平衡 功率因数 详细异常输出", "DetectionRulesSection"),
        new("报告设置", "报告", "报告 excel 历史 清空", "ReportsSection"),
        new("运行记录", "运行记录", "日志 运行记录 保留 清空", "LogsSection"),
        new("窗口设置", "窗口", "窗口 托盘 自启动 通知 热键 快捷键", "WindowSection"),
        new("桌面通知测试", "窗口", "桌面通知 通知 测试 toast Windows 系统设置", "WindowSection"),
        new("Windows 通知设置", "窗口", "Windows 系统 通知 设置 ms-settings", "WindowSection"),
        new("智能助手", "智能助手", "llm ai 智能助手 模型 api key 代理 测试", "LlmSection"),
        new("LLM 服务地址", "智能助手", "llm endpoint 服务地址 模型 api key", "LlmSection"),
        new("消息推送", "消息推送", "ntfy 通知 推送 主题 token 优先级 代理 测试", "NotificationsSection"),
        new("网络代理", "网络代理", "代理 proxy 地址 认证 用户名 密码 测试", "ProxySection"),
        new("软件更新", "软件更新", "更新 版本 检查 通道 代理 更新源 release", "UpdatesSection"),
        new("更新代理", "软件更新", "更新 网络代理 proxy", "UpdatesSection"),
        new("高级设置", "高级", "高级 应用状态 检测组件 python 设置存储", "AdvancedSection"),
        new("检测组件", "高级", "高级 python 检测组件 运行程序", "AdvancedSection"),
    ];

    private async void BrowseInputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is null || ViewModel is null)
        {
            return;
        }

        ViewModel.InputDirectory = folder.Path;
        if (string.IsNullOrWhiteSpace(ViewModel.OutputDirectory))
        {
            ViewModel.OutputDirectory = folder.Path;
        }
    }

    private async void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null && ViewModel is not null)
        {
            ViewModel.OutputDirectory = folder.Path;
        }
    }

    private async void BrowseConfigPath_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync(".json");
        if (file is not null && ViewModel is not null)
        {
            ViewModel.ConfigPath = file.Path;
        }
    }

    private async void BrowsePythonExecutable_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync(".exe");
        if (file is not null && ViewModel is not null)
        {
            ViewModel.PythonExecutable = file.Path;
        }
    }

    private static async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (App.CurrentWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        }

        return await picker.PickSingleFolderAsync();
    }

    private static async Task<StorageFile?> PickFileAsync(params string[] fileTypes)
    {
        var picker = new FileOpenPicker();
        foreach (var fileType in fileTypes)
        {
            picker.FileTypeFilter.Add(fileType);
        }

        if (App.CurrentWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        }

        return await picker.PickSingleFileAsync();
    }

    private sealed record SettingsSearchEntry(
        string Title,
        string SectionTitle,
        string Keywords,
        string SectionName,
        bool ExpandMoreThresholds = false)
    {
        public bool Matches(string query) =>
            !string.IsNullOrWhiteSpace(query)
            && (Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => Title;
    }
}

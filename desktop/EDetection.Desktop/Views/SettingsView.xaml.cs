using EDetection.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EDetection.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    private const double ExpandedNavigationWidth = 184;
    private const double CompactNavigationWidth = 152;
    private const double IconNavigationWidth = 56;
    private const double ExpandedLabelWidth = 200;
    private const double CompactLabelWidth = 168;
    private const double SettingsContentMaxWidth = 900;
    private const double CompactBodyBreakpoint = 1040;
    private const double IconNavigationBreakpoint = 820;
    private const double CompactContentBreakpoint = 720;
    private const double StackedSettingsRowsBreakpoint = 780;
    private const double StackedActionBreakpoint = CompactContentBreakpoint;
    private const double StackedFooterBreakpoint = 820;
    private const double StackedThresholdBreakpoint = 640;

    private bool _suppressCategoryScroll;
    private bool _suppressScrollSync;
    private int _navigationScrollVersion;
    private readonly List<SettingRowLayoutState> _settingRows = [];
    private readonly IReadOnlyList<SettingsSearchEntry> _searchEntries;

    public SettingsView()
    {
        InitializeComponent();
        _searchEntries = BuildSearchEntries();
        _suppressCategoryScroll = true;
        SettingsCategoryList.SelectedIndex = 0;
        _suppressCategoryScroll = false;
        Loaded += SettingsView_Loaded;
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
            NavigateToSectionByName(string.IsNullOrWhiteSpace(initialSectionName)
                ? "DefaultsSection"
                : initialSectionName);
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

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmActionAsync(
                "恢复默认设置？",
                "将恢复检测阈值、目录、窗口和网络偏好。已保存的 API Key、Token 与代理密码不会删除。",
                "恢复默认"))
        {
            return;
        }

        ViewModel?.ResetSettingsToDefaultsCommand.Execute(null);
    }

    private async void ClearRecentReports_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmActionAsync("清空报告历史？", "这会移除应用中的报告历史记录，不会删除磁盘上的 Excel 报告。", "清空历史"))
        {
            ViewModel?.ClearRecentReportsCommand.Execute(null);
        }
    }

    private async void ClearRuntimeLogs_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmActionAsync("清空运行记录？", "这会移除应用中的运行记录，无法恢复。", "清空记录"))
        {
            ViewModel?.ClearRuntimeLogsCommand.Execute(null);
        }
    }

    private async Task<bool> ConfirmActionAsync(string title, string content, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        ExpandSearchEntryContainers(entry);
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateResponsiveLayout();
            SelectCategoryBySection(entry.SectionName);
            NavigateToElementByName(entry.TargetName ?? entry.SectionName);
        });
    }

    public void NavigateToSectionByName(string sectionName)
    {
        SelectCategoryBySection(sectionName);
        NavigateToElementByName(sectionName);
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

        NavigateToElementByName(sectionName);
    }

    private void SelectCategoryBySection(string sectionName)
    {
        var categorySectionName = GetCategorySectionName(sectionName);
        for (var i = 0; i < SettingsCategoryList.Items.Count; i++)
        {
            if (SettingsCategoryList.Items[i] is ListViewItem { Tag: string itemSection }
                && string.Equals(itemSection, categorySectionName, StringComparison.Ordinal))
            {
                _suppressCategoryScroll = true;
                SettingsCategoryList.SelectedIndex = i;
                _suppressCategoryScroll = false;
                return;
            }
        }
    }

    private void NavigateToElementByName(string elementName)
    {
        if (FindName(elementName) is not FrameworkElement element)
        {
            return;
        }

        _suppressScrollSync = true;
        var navigationVersion = ++_navigationScrollVersion;
        element.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0,
        });
        _ = ResumeScrollSyncAfterNavigationAsync(navigationVersion);
    }

    private void ExpandSearchEntryContainers(SettingsSearchEntry entry)
    {
        if (entry.ExpandMoreThresholds)
        {
            MoreThresholdsExpander.IsExpanded = true;
        }

        if (!string.IsNullOrWhiteSpace(entry.ExpanderName)
            && FindName(entry.ExpanderName) is Expander expander)
        {
            expander.IsExpanded = true;
        }

        UpdateResponsiveLayout();
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
        CollectSettingRows(SettingsContentStack);
    }

    private void CollectSettingRows(DependencyObject root)
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Grid grid
                && !_settingRows.Any(row => ReferenceEquals(row.Row, grid))
                && TryCreateSettingRowState(grid, out var rowState))
            {
                _settingRows.Add(rowState);
            }

            CollectSettingRows(child);
        }
    }

    private static bool TryCreateSettingRowState(Grid grid, out SettingRowLayoutState rowState)
    {
        rowState = default!;
        if (grid.MinHeight < 64 || grid.ColumnDefinitions.Count != 2 || grid.RowDefinitions.Count > 0)
        {
            return false;
        }

        var rowChildren = grid.Children.OfType<FrameworkElement>().ToArray();
        if (rowChildren.Length != 2
            || Grid.GetColumn(rowChildren[0]) != 0
            || Grid.GetColumn(rowChildren[1]) != 1)
        {
            return false;
        }

        rowState = SettingRowLayoutState.Create(grid, rowChildren[0], rowChildren[1]);
        return true;
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
        var iconOnlyNavigation = availableWidth < IconNavigationBreakpoint;
        SettingsBody.Padding = compact
            ? new Thickness(18, 18, 18, 0)
            : new Thickness(24, 22, 24, 0);
        SettingsBody.ColumnSpacing = iconOnlyNavigation ? 14 : compact ? 18 : 24;
        SettingsNavColumn.Width = new GridLength(iconOnlyNavigation
            ? IconNavigationWidth
            : compact ? CompactNavigationWidth : ExpandedNavigationWidth);
        UpdateNavigationRailLayout(iconOnlyNavigation);
        UpdateFooterLayout(availableWidth < StackedFooterBreakpoint);

        var contentWidth = SettingsScroll.ActualWidth;
        if (contentWidth <= 0)
        {
            return;
        }

        SettingsContentStack.Width = Math.Min(contentWidth, SettingsContentMaxWidth);
        SettingsContentStack.MaxWidth = SettingsContentMaxWidth;

        var stackSettingRows = contentWidth < StackedSettingsRowsBreakpoint;
        var labelWidth = contentWidth < CompactContentBreakpoint ? CompactLabelWidth : ExpandedLabelWidth;
        foreach (var row in _settingRows)
        {
            ApplySettingsRowLayout(row, stackSettingRows, labelWidth);
        }

        var stackActions = contentWidth < StackedActionBreakpoint;
        foreach (var grid in GetResponsiveActionGrids())
        {
            ApplyActionGridLayout(grid, stackActions);
        }

        var stackThresholds = contentWidth < StackedThresholdBreakpoint;
        ApplyThresholdGridLayout(PrimaryThresholdGrid, stackThresholds);
        ApplyThresholdGridLayout(MoreThresholdGrid, stackThresholds);
    }

    private void UpdateNavigationRailLayout(bool iconOnly)
    {
        SettingsSearchBox.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchBox.Width = iconOnly ? IconNavigationWidth : double.NaN;
        SettingsSearchBox.PlaceholderText = iconOnly ? "" : "搜索设置";
        SettingsNavRail.RowSpacing = iconOnly ? 0 : 10;

        foreach (var item in SettingsCategoryList.Items.OfType<ListViewItem>())
        {
            var title = FindFirstTextBlockText(item.Content as DependencyObject);
            if (!string.IsNullOrWhiteSpace(title))
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, title);
                ToolTipService.SetToolTip(item, title);
            }

            item.HorizontalContentAlignment = iconOnly
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch;
            item.Padding = iconOnly ? new Thickness(0) : new Thickness(12, 0, 12, 0);
            item.MinHeight = iconOnly ? 44 : 40;

            if (item.Content is DependencyObject content)
            {
                UpdateNavigationItemContent(content, iconOnly);
            }
        }
    }

    private static void UpdateNavigationItemContent(DependencyObject root, bool iconOnly)
    {
        if (root is StackPanel stack)
        {
            stack.Spacing = iconOnly ? 0 : 10;
            stack.HorizontalAlignment = iconOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }

        if (root is TextBlock textBlock)
        {
            textBlock.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        }

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            UpdateNavigationItemContent(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), iconOnly);
        }
    }

    private static string? FindFirstTextBlockText(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is TextBlock textBlock)
        {
            return textBlock.Text;
        }

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var text = FindFirstTextBlockText(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private void UpdateFooterLayout(bool stack)
    {
        SettingsFooter.Padding = stack
            ? new Thickness(18, 10, 18, 10)
            : new Thickness(24, 12, 24, 12);

        Grid.SetColumn(SettingsFeedbackBar, 0);
        Grid.SetRow(SettingsFeedbackBar, 0);
        SettingsFeedbackBar.HorizontalAlignment = stack ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        SettingsFeedbackBar.MaxWidth = stack ? double.PositiveInfinity : 520;

        Grid.SetColumn(SettingsFooterActions, stack ? 0 : 1);
        Grid.SetRow(SettingsFooterActions, stack ? 1 : 0);
        SettingsFooterActions.HorizontalAlignment = HorizontalAlignment.Right;

        SettingsFooterLayout.ColumnSpacing = stack ? 0 : 16;
        SettingsFooterLayout.RowSpacing = stack ? 10 : 0;
        SettingsFooterLayout.ColumnDefinitions[1].Width = stack
            ? new GridLength(0)
            : GridLength.Auto;
    }

    private Grid[] GetResponsiveActionGrids() =>
    [
        DetectionParametersActionGrid,
        DesktopNotificationActionGrid,
        StartupActionGrid,
        ProxyActionGrid,
        UpdateActionGrid,
    ];

    private static void ApplyActionGridLayout(Grid grid, bool stack)
    {
        grid.RowDefinitions.Clear();
        if (grid.ColumnDefinitions.Count < 2 || grid.Children.Count < 2)
        {
            return;
        }

        grid.ColumnSpacing = stack ? 0 : 8;
        grid.RowSpacing = stack ? 8 : 0;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = stack
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);

        var first = grid.Children[0] as FrameworkElement;
        var second = grid.Children[1] as FrameworkElement;
        if (first is null || second is null)
        {
            return;
        }

        Grid.SetColumn(first, 0);
        Grid.SetRow(first, 0);
        if (stack)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(second, 0);
            Grid.SetRow(second, 1);
            return;
        }

        Grid.SetColumn(second, 1);
        Grid.SetRow(second, 0);
    }

    private static void ApplySettingsRowLayout(SettingRowLayoutState state, bool stack, double labelWidth)
    {
        var row = state.Row;
        row.RowDefinitions.Clear();

        if (!stack)
        {
            state.Restore(labelWidth);
            return;
        }

        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnSpacing = 0;
        row.RowSpacing = 8;
        row.Padding = new Thickness(16, 12, 16, 12);
        state.LabelColumn.MinWidth = 0;
        state.LabelColumn.MaxWidth = double.PositiveInfinity;
        state.LabelColumn.Width = new GridLength(1, GridUnitType.Star);
        state.ControlColumn.MinWidth = 0;
        state.ControlColumn.MaxWidth = double.PositiveInfinity;
        state.ControlColumn.Width = new GridLength(0);

        Grid.SetRow(state.Label, 0);
        Grid.SetColumn(state.Label, 0);
        Grid.SetColumnSpan(state.Label, 2);
        Grid.SetRow(state.Control, 1);
        Grid.SetColumn(state.Control, 0);
        Grid.SetColumnSpan(state.Control, 2);
    }

    private static void ApplyThresholdGridLayout(Grid grid, bool stack)
    {
        if (grid.ColumnDefinitions.Count < 2)
        {
            return;
        }

        grid.ColumnSpacing = stack ? 0 : 14;
        grid.RowSpacing = stack ? 10 : 12;
        grid.Padding = stack
            ? new Thickness(16, 12, 16, 12)
            : new Thickness(18, 12, 18, 12);
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = stack
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);

        var itemIndex = 0;
        foreach (var child in grid.Children.OfType<FrameworkElement>())
        {
            Grid.SetRow(child, stack ? itemIndex : itemIndex / 2);
            Grid.SetColumn(child, stack ? 0 : itemIndex % 2);
            Grid.SetColumnSpan(child, stack ? 2 : 1);
            itemIndex++;
        }

        var requiredRows = stack
            ? itemIndex
            : (int)Math.Ceiling(itemIndex / 2.0);
        while (grid.RowDefinitions.Count < requiredRows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < grid.RowDefinitions.Count; i++)
        {
            grid.RowDefinitions[i].Height = i < requiredRows
                ? GridLength.Auto
                : new GridLength(0);
        }
    }

    private void SettingsExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateResponsiveLayout();
            DispatcherQueue.TryEnqueue(() =>
            {
                sender.UpdateLayout();
                UpdateResponsiveLayout();
            });
        });
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
        var activeSectionName = GetActiveCategorySectionName();
        if (string.IsNullOrWhiteSpace(activeSectionName))
        {
            return;
        }

        SelectCategoryBySection(activeSectionName);
    }

    private string? GetActiveCategorySectionName()
    {
        var sections = GetSettingSections();
        if (sections.Length == 0)
        {
            return null;
        }

        var activeIndex = 0;
        if (SettingsScroll.ScrollableHeight > 0
            && SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - 1)
        {
            activeIndex = sections.Length - 1;
            return GetCategorySectionName(sections[activeIndex].Name);
        }

        var viewportHeight = SettingsScroll.ViewportHeight > 0
            ? SettingsScroll.ViewportHeight
            : SettingsScroll.ActualHeight;
        var activationY = Math.Clamp(viewportHeight * 0.34, 120, 300);

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

        return GetCategorySectionName(sections[activeIndex].Name);
    }

    private FrameworkElement[] GetSettingSections() =>
    [
        DefaultsSection,
        ThresholdsSection,
        DetectionRulesSection,
        ReportsSection,
        LogsSection,
        WindowSection,
        LlmSection,
        NotificationsSection,
        AdvancedSection,
        ProxySection,
        UpdatesSection,
        AppearanceSection,
    ];

    private static string GetCategorySectionName(string sectionName) =>
        sectionName switch
        {
            "ThresholdsSection" or "DetectionRulesSection" => "DefaultsSection",
            "LogsSection" => "ReportsSection",
            "NotificationsSection" => "LlmSection",
            "UpdatesSection" => "ProxySection",
            "AdvancedSection" => "LlmSection",
            _ => sectionName,
        };

    private static IReadOnlyList<SettingsSearchEntry> BuildSearchEntries() =>
    [
        new("外观", "外观", "外观 主题 背景 mica acrylic 深色 浅色 跟随系统 诗词", "AppearanceSection"),
        new("应用主题", "外观", "外观 主题 深色 浅色 跟随系统", "AppearanceSection"),
        new("窗口背景", "外观", "外观 背景 mica acrylic", "AppearanceSection"),
        new("顶部诗词", "外观", "诗词 poetry palemoky 顶部 信息栏 来源", "AppearanceSection", TargetName: "PoetryOptionsExpander", ExpanderName: "PoetryOptionsExpander"),
        new("检测目录", "检测", "检测 输入目录 报告目录 csv 路径", "DefaultsSection"),
        new("输入目录", "检测", "检测 输入目录 csv 路径", "DefaultsSection"),
        new("报告目录", "检测", "检测 报告目录 输出目录 路径", "DefaultsSection"),
        new("检测参数", "检测", "检测 参数 阈值 设置 规则", "DefaultsSection"),
        new("阈值设置", "检测", "阈值 电压 电流 功率 温度 数据不变化 不平衡", "ThresholdsSection"),
        new("阈值文件路径", "检测", "阈值 配置 文件 json config 路径", "ThresholdsSection", TargetName: "ThresholdConfigPathRow"),
        new("电压阈值", "检测", "阈值 电压 下限 上限 不平衡", "ThresholdsSection", TargetName: "VoltageMinThresholdRow"),
        new("电流阈值", "检测", "阈值 电流 上限 不平衡 激活", "ThresholdsSection", true, "CurrentMaxThresholdRow"),
        new("功率因数阈值", "检测", "阈值 功率 因数 下限", "ThresholdsSection", TargetName: "PowerFactorThresholdRow"),
        new("温度阈值", "检测", "阈值 温度 上限 下限", "ThresholdsSection", true, "TemperatureMaxThresholdRow"),
        new("连续不变化点数", "检测", "阈值 冻结 数据不变化 连续点数 样本点 波动 容差", "ThresholdsSection", true, "FreezeCountThresholdRow"),
        new("温度下限", "检测", "阈值 温度 下限", "ThresholdsSection", true, "TemperatureMinThresholdRow"),
        new("电流激活下限", "检测", "阈值 电流 激活 下限", "ThresholdsSection", true, "CurrentActiveMinThresholdRow"),
        new("数据波动容差", "检测", "阈值 冻结 数据 波动 容差", "ThresholdsSection", true, "FreezeStdThresholdRow"),
        new("检测规则", "检测", "规则 电流过载 电流不平衡 功率因数 详细异常输出", "DetectionRulesSection"),
        new("报告设置", "报告与记录", "报告 excel 历史 清空", "ReportsSection"),
        new("运行记录", "报告与记录", "日志 运行记录 保留 清空", "LogsSection"),
        new("窗口设置", "通知与窗口", "窗口 托盘 自启动 启动应用 通知", "WindowSection"),
        new("Windows 启动应用设置", "通知与窗口", "Windows 系统 启动 应用 设置 startupapps", "WindowSection"),
        new("桌面通知测试", "通知与窗口", "桌面通知 通知 测试 toast Windows 系统设置", "WindowSection"),
        new("Windows 通知设置", "通知与窗口", "Windows 系统 通知 设置 ms-settings", "WindowSection"),
        new("智能助手", "智能助手与推送", "llm ai 智能助手 模型 api key 代理 测试", "LlmSection"),
        new("LLM 服务地址", "智能助手与推送", "llm endpoint 服务地址 模型 api key", "LlmSection", TargetName: "LlmEndpointRow", ExpanderName: "LlmConnectionExpander"),
        new("LLM API Key", "智能助手与推送", "llm api key 凭据 密钥", "LlmSection", TargetName: "LlmApiKeyRow", ExpanderName: "LlmConnectionExpander"),
        new("消息推送", "智能助手与推送", "ntfy 通知 推送 主题 token 优先级 代理 测试", "NotificationsSection", TargetName: "NotificationsSection"),
        new("ntfy 服务地址", "智能助手与推送", "ntfy 服务地址 topic token", "NotificationsSection", TargetName: "NtfyServerRow", ExpanderName: "NtfySettingsExpander"),
        new("ntfy Token", "智能助手与推送", "ntfy token 凭据 推送", "NotificationsSection", TargetName: "NtfyTokenRow", ExpanderName: "NtfySettingsExpander"),
        new("网络代理", "更新与网络", "代理 proxy 地址 认证 用户名 密码 测试 Windows 系统设置", "ProxySection"),
        new("Windows 代理设置", "更新与网络", "Windows 系统 网络 代理 设置 ms-settings", "ProxySection"),
        new("软件更新", "更新与网络", "更新 版本 检查 通道 代理 更新源 release", "UpdatesSection"),
        new("更新代理", "更新与网络", "更新 网络代理 proxy", "UpdatesSection", TargetName: "UpdateProxyRow", ExpanderName: "AdvancedUpdateExpander"),
        new("更新检查方式", "更新与网络", "更新 自动检查 手动检查", "UpdatesSection", TargetName: "UpdateChannelRow", ExpanderName: "AdvancedUpdateExpander"),
        new("更新源", "更新与网络", "更新源 feed release github", "UpdatesSection", TargetName: "UpdateFeedRow", ExpanderName: "AdvancedUpdateExpander"),
        new("维护", "智能助手与推送", "维护 应用状态 原生 检测核心 设置存储", "AdvancedSection"),
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

    private sealed record SettingRowLayoutState(
        Grid Row,
        FrameworkElement Label,
        FrameworkElement Control,
        ColumnDefinition LabelColumn,
        ColumnDefinition ControlColumn,
        GridLength OriginalLabelColumnWidth,
        GridLength OriginalControlColumnWidth,
        double OriginalLabelColumnMaxWidth,
        double OriginalControlColumnMaxWidth,
        double OriginalLabelColumnMinWidth,
        double OriginalControlColumnMinWidth,
        Thickness OriginalPadding,
        double OriginalColumnSpacing,
        double OriginalRowSpacing,
        int OriginalLabelRow,
        int OriginalLabelColumn,
        int OriginalLabelColumnSpan,
        int OriginalControlRow,
        int OriginalControlColumn,
        int OriginalControlColumnSpan)
    {
        public static SettingRowLayoutState Create(Grid row, FrameworkElement label, FrameworkElement control) =>
            new(
                row,
                label,
                control,
                row.ColumnDefinitions[0],
                row.ColumnDefinitions[1],
                row.ColumnDefinitions[0].Width,
                row.ColumnDefinitions[1].Width,
                row.ColumnDefinitions[0].MaxWidth,
                row.ColumnDefinitions[1].MaxWidth,
                row.ColumnDefinitions[0].MinWidth,
                row.ColumnDefinitions[1].MinWidth,
                row.Padding,
                row.ColumnSpacing,
                row.RowSpacing,
                Grid.GetRow(label),
                Grid.GetColumn(label),
                Grid.GetColumnSpan(label),
                Grid.GetRow(control),
                Grid.GetColumn(control),
                Grid.GetColumnSpan(control));

        public void Restore(double labelWidth)
        {
            Row.ColumnSpacing = OriginalColumnSpacing;
            Row.RowSpacing = OriginalRowSpacing;
            Row.Padding = OriginalPadding;

            LabelColumn.MinWidth = OriginalLabelColumnMinWidth;
            LabelColumn.MaxWidth = OriginalLabelColumnMaxWidth;
            LabelColumn.Width = OriginalLabelColumnWidth.IsAbsolute
                ? new GridLength(labelWidth)
                : OriginalLabelColumnWidth;
            ControlColumn.MinWidth = OriginalControlColumnMinWidth;
            ControlColumn.MaxWidth = OriginalControlColumnMaxWidth;
            ControlColumn.Width = OriginalControlColumnWidth;

            Grid.SetRow(Label, OriginalLabelRow);
            Grid.SetColumn(Label, OriginalLabelColumn);
            Grid.SetColumnSpan(Label, OriginalLabelColumnSpan);
            Grid.SetRow(Control, OriginalControlRow);
            Grid.SetColumn(Control, OriginalControlColumn);
            Grid.SetColumnSpan(Control, OriginalControlColumnSpan);
        }
    }

    private sealed record SettingsSearchEntry(
        string Title,
        string SectionTitle,
        string Keywords,
        string SectionName,
        bool ExpandMoreThresholds = false,
        string? TargetName = null,
        string? ExpanderName = null)
    {
        public bool Matches(string query) =>
            !string.IsNullOrWhiteSpace(query)
            && (Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => Title;
    }
}

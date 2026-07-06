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
    private const double ExpandedLabelWidth = 200;
    private const double CompactLabelWidth = 168;
    private const double SettingsContentMaxWidth = 900;
    private const double CompactBodyBreakpoint = 960;
    private const double CompactContentBreakpoint = 720;

    private bool _suppressCategoryScroll;
    private bool _suppressScrollSync;
    private int _navigationScrollVersion;
    private readonly List<Grid> _settingRows = [];

    public SettingsView()
    {
        InitializeComponent();
        _suppressCategoryScroll = true;
        SettingsCategoryList.SelectedIndex = 0;
        _suppressCategoryScroll = false;
        Loaded += SettingsView_Loaded;
    }

    public event EventHandler? BackRequested;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public void PrepareForDisplay()
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
            UpdateSelectedCategoryFromScroll();
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

        if (FindName(sectionName) is FrameworkElement section)
        {
            _suppressScrollSync = true;
            var navigationVersion = ++_navigationScrollVersion;
            section.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0,
            });
            _ = ResumeScrollSyncAfterNavigationAsync(navigationVersion);
        }
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
        if (_settingRows.Count > 0)
        {
            return;
        }

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
        ReportsSection,
        LogsSection,
        WindowSection,
        AdvancedSection,
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
}

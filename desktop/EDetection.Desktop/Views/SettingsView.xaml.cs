using EDetection.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EDetection.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    private const double CompactWidth = 640;

    public SettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void SettingsScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width < CompactWidth;
        ApplyHeaderLayout(compact);
        ApplyAppearanceLayout(compact);
        ApplyReportLayout(compact);
        ApplyLogLayout(compact);
        ApplyWindowOptionsLayout(compact);
        ApplyDesktopHealthLayout(compact);
    }

    private void ApplyHeaderLayout(bool compact)
    {
        HeaderGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        HeaderSummaryColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        HeaderHealthText.MaxWidth = compact ? double.PositiveInfinity : 260;
        Place(HeaderHealthText, compact ? 1 : 0, compact ? 0 : 1, compact ? 2 : 1);
    }

    private void ApplyAppearanceLayout(bool compact)
    {
        AppearanceGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        AppearanceColumn1.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(ThemeComboBox, 0, 0);
        Place(BackdropComboBox, compact ? 1 : 0, compact ? 0 : 1);
    }

    private void ApplyReportLayout(bool compact)
    {
        ReportSettingsGrid.RowDefinitions[2].Height = compact ? GridLength.Auto : new GridLength(0);
        ReportControlColumn.Width = compact ? GridLength.Auto : GridLength.Auto;
        ReportButtonColumn.Width = compact ? new GridLength(0) : GridLength.Auto;

        Place(ReportHistorySummary, 1, 0, compact ? 3 : 1);
        Place(ReportLimitComboBox, compact ? 2 : 1, compact ? 0 : 1);
        Place(ClearRecentReportsButton, compact ? 2 : 1, compact ? 1 : 2);
    }

    private void ApplyLogLayout(bool compact)
    {
        RuntimeLogGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        LogButtonColumn.Width = compact ? new GridLength(0) : GridLength.Auto;

        Place(RuntimeLogSummary, 0, 0, compact ? 3 : 1);
        Place(LogRetentionComboBox, compact ? 1 : 0, compact ? 0 : 1);
        Place(ClearRuntimeLogsButton, compact ? 1 : 0, compact ? 1 : 2);
    }

    private void ApplyWindowOptionsLayout(bool compact)
    {
        WindowOptionsColumn1.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        for (var i = 4; i < WindowOptionsGrid.RowDefinitions.Count; i++)
        {
            WindowOptionsGrid.RowDefinitions[i].Height = compact ? GridLength.Auto : new GridLength(0);
        }

        Place(CloseToTrayToggle, 0, 0, compact ? 2 : 1);
        Place(DesktopNotificationsToggle, compact ? 1 : 0, compact ? 0 : 1, compact ? 2 : 1);
        Place(StartMinimizedToggle, compact ? 2 : 1, 0, compact ? 2 : 1);
        Place(AutoStartToggle, compact ? 3 : 1, compact ? 0 : 1, compact ? 2 : 1);
        Place(StartupIntegrationText, compact ? 4 : 2, 0, 2);
        Place(GlobalHotkeyPanel, compact ? 5 : 3, 0, compact ? 2 : 1);
        Place(QuickActionsPanel, compact ? 6 : 3, compact ? 0 : 1, compact ? 2 : 1);
    }

    private void ApplyDesktopHealthLayout(bool compact)
    {
        DesktopHealthColumn1.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        for (var i = 4; i < DesktopHealthGrid.RowDefinitions.Count; i++)
        {
            DesktopHealthGrid.RowDefinitions[i].Height = compact ? GridLength.Auto : new GridLength(0);
        }

        if (compact)
        {
            Place(NotificationHealthText, 0, 0, 2);
            Place(StartupHealthText, 1, 0, 2);
            Place(SettingsHealthText, 2, 0, 2);
            Place(PackageHealthText, 3, 0, 2);
            Place(PythonBridgeHealthText, 4, 0, 2);
            Place(InstallHealthText, 5, 0, 2);
            Place(HotkeyHealthText, 6, 0, 2);
            return;
        }

        Place(NotificationHealthText, 0, 0);
        Place(StartupHealthText, 0, 1);
        Place(SettingsHealthText, 1, 0);
        Place(PackageHealthText, 1, 1);
        Place(PythonBridgeHealthText, 2, 0);
        Place(InstallHealthText, 2, 1);
        Place(HotkeyHealthText, 3, 0, 2);
    }

    private static void Place(FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private async void BrowseInputDirectory_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

    private async void BrowseOutputDirectory_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null && ViewModel is not null)
        {
            ViewModel.OutputDirectory = folder.Path;
        }
    }

    private async void BrowseConfigPath_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var file = await PickFileAsync(".json");
        if (file is not null && ViewModel is not null)
        {
            ViewModel.ConfigPath = file.Path;
        }
    }

    private async void BrowsePythonExecutable_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

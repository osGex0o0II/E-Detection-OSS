using EDetection.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EDetection.Desktop.Views;

public sealed partial class RunSetupView : UserControl
{
    public RunSetupView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public void SetCompactLayout(bool compact)
    {
        ApplyPathLayout(
            InputPathGrid,
            InputPathTextColumn,
            InputPathButtonColumn,
            InputDirectoryTextBox,
            InputDirectoryButton,
            compact);
        ApplyPathLayout(
            OutputPathGrid,
            OutputPathTextColumn,
            OutputPathButtonColumn,
            OutputDirectoryTextBox,
            OutputDirectoryButton,
            compact);
        ApplyRunCommandLayout(compact);
        ApplyReportFilterLayout(compact);
    }

    public async Task BrowseInputDirectoryAsync()
    {
        var folder = await PickFolderAsync();
        if (folder is not null && ViewModel is not null)
        {
            ViewModel.InputDirectory = folder.Path;
            if (string.IsNullOrWhiteSpace(ViewModel.OutputDirectory))
            {
                ViewModel.OutputDirectory = folder.Path;
            }
        }
    }

    public async Task BrowseConfigPathAsync()
    {
        var file = await PickFileAsync(".json");
        if (file is not null && ViewModel is not null)
        {
            ViewModel.ConfigPath = file.Path;
        }
    }

    private async void BrowseInputDirectory_Click(object sender, RoutedEventArgs e)
    {
        await BrowseInputDirectoryAsync();
    }

    private async void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null && ViewModel is not null)
        {
            ViewModel.OutputDirectory = folder.Path;
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

    private static void ApplyPathLayout(
        Grid grid,
        ColumnDefinition textColumn,
        ColumnDefinition buttonColumn,
        FrameworkElement textBox,
        FrameworkElement button,
        bool compact)
    {
        grid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        textColumn.Width = new GridLength(1, GridUnitType.Star);
        buttonColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(textBox, 0);
        Grid.SetColumn(textBox, 0);
        Grid.SetColumnSpan(textBox, compact ? 2 : 1);
        Grid.SetRow(button, compact ? 1 : 0);
        Grid.SetColumn(button, compact ? 0 : 1);
        Grid.SetColumnSpan(button, compact ? 2 : 1);
        button.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    private void ApplyReportFilterLayout(bool compact)
    {
        ReportHistoryFilterGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        ReportSearchColumn.Width = new GridLength(1, GridUnitType.Star);
        ReportFilterColumn.Width = compact ? new GridLength(0) : new GridLength(118);
        Grid.SetRow(ReportSearchBox, 0);
        Grid.SetColumn(ReportSearchBox, 0);
        Grid.SetColumnSpan(ReportSearchBox, compact ? 2 : 1);
        Grid.SetRow(ReportFilterComboBox, compact ? 1 : 0);
        Grid.SetColumn(ReportFilterComboBox, compact ? 0 : 1);
        Grid.SetColumnSpan(ReportFilterComboBox, compact ? 2 : 1);
        ReportFilterComboBox.MaxWidth = compact ? 180 : double.PositiveInfinity;
    }

    private void ApplyRunCommandLayout(bool compact)
    {
        RunCommandGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        RunPrimaryCommandColumn.Width = new GridLength(1, GridUnitType.Star);
        RunSecondaryCommandColumn.Width = compact ? new GridLength(0) : GridLength.Auto;

        Grid.SetRow(RunStartButton, 0);
        Grid.SetColumn(RunStartButton, 0);
        Grid.SetColumnSpan(RunStartButton, compact ? 2 : 1);
        Grid.SetRow(RunCancelButton, compact ? 1 : 0);
        Grid.SetColumn(RunCancelButton, compact ? 0 : 1);
        Grid.SetColumnSpan(RunCancelButton, compact ? 2 : 1);
    }
}

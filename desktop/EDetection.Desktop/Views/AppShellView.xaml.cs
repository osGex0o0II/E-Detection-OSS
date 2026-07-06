using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;
using EDetection.Desktop.ViewModels;
using System.ComponentModel;
using Windows.Foundation;
using Windows.System;

namespace EDetection.Desktop.Views;

public sealed partial class AppShellView : UserControl
{
    private readonly CommandPaletteService _commandPalette = new();
    private ContentDialog? _quickActionDialog;
    private TextBox? _quickActionSearchBox;
    private ListView? _quickActionListView;
    private MainViewModel? _subscribedViewModel;
    private bool? _lastCompactLayout;
    private bool _isSettingsPageVisible;
    private const double StackedStatusWidth = 1008;
    private const double CompactShellWidth = 1080;
    private const double ComfortableShellWidth = 1100;

    public AppShellView()
    {
        InitializeComponent();
        RegisterKeyboardAccelerators();
        Loaded += AppShellView_Loaded;
        DataContextChanged += AppShellView_DataContextChanged;
        ShellSettings.BackRequested += (_, _) => ShowWorkbench();
    }

    public FrameworkElement TitleBarElement => AppTitleBar;

    public FrameworkElement RootElement => Root;

    public event EventHandler? AboutRequested;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsPage();

    private async void QuickActionsButton_Click(object sender, RoutedEventArgs e) =>
        await ShowQuickActionsAsync();

    private void AboutButton_Click(object sender, RoutedEventArgs e) =>
        AboutRequested?.Invoke(this, EventArgs.Empty);

    private void AppShellView_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _subscribedViewModel = null;
        }

        if (args.NewValue is MainViewModel newViewModel)
        {
            newViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _subscribedViewModel = newViewModel;
        }

        UpdateQuickActionsToolTip();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedQuickActionsShortcutIndex)
            or nameof(MainViewModel.EnableQuickActionsShortcut))
        {
            UpdateQuickActionsToolTip();
        }
    }

    private void RegisterKeyboardAccelerators()
    {
        AddShortcut(VirtualKey.K, VirtualKeyModifiers.Control, async (_, e) =>
        {
            await TryShowQuickActionsAsync(0, e);
        });
        AddShortcut(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, async (_, e) =>
        {
            await TryShowQuickActionsAsync(1, e);
        });
        AddShortcut(VirtualKey.F5, VirtualKeyModifiers.None, (_, e) =>
        {
            e.Handled = TryExecuteCommand(StartButton.Command, StartButton.CommandParameter);
        });
        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, (_, e) =>
        {
            e.Handled = TryExecuteCommand(CancelButton.Command, CancelButton.CommandParameter);
        });
        AddShortcut(VirtualKey.F6, VirtualKeyModifiers.None, (_, e) =>
        {
            e.Handled = TryExecuteCommand(DiagnosticsButton.Command, DiagnosticsButton.CommandParameter);
        });
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, async (_, e) =>
        {
            e.Handled = true;
            await RunSetup.BrowseInputDirectoryAsync();
        });
        AddShortcut(VirtualKey.I, VirtualKeyModifiers.Control, (_, e) =>
        {
            e.Handled = true;
            AboutRequested?.Invoke(this, EventArgs.Empty);
        });
        AddShortcut(VirtualKey.Number1, VirtualKeyModifiers.Control, async (_, e) =>
        {
            e.Handled = true;
            await RunSetup.BrowseConfigPathAsync();
        });
        AddShortcut(VirtualKey.Number2, VirtualKeyModifiers.Control, async (_, e) =>
        {
            e.Handled = true;
            await RunSetup.BrowsePythonExecutableAsync();
        });
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, (_, e) =>
        {
            e.Handled = true;
            ShowSettingsPage();
        });
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async Task TryShowQuickActionsAsync(
        int shortcutIndex,
        KeyboardAcceleratorInvokedEventArgs e)
    {
        if (ViewModel?.EnableQuickActionsShortcut == false
            || ViewModel?.SelectedQuickActionsShortcutIndex != shortcutIndex)
        {
            return;
        }

        e.Handled = true;
        await ShowQuickActionsAsync();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    public void ShowSettingsPage(string? initialSectionName = null)
    {
        if (_isSettingsPageVisible)
        {
            if (!string.IsNullOrWhiteSpace(initialSectionName))
            {
                ShellSettings.NavigateToSectionByName(initialSectionName);
            }

            return;
        }

        _isSettingsPageVisible = true;
        StatusBand.Visibility = Visibility.Collapsed;
        WorkbenchLayout.Visibility = Visibility.Collapsed;
        ShellSettings.Visibility = Visibility.Visible;
        SettingsTitleBarButton.Visibility = Visibility.Collapsed;
        ShellSettings.PrepareForDisplay(initialSectionName);
        ShellSettings.PlayEntranceAnimation(forward: true);
        ShellSettings.Focus(FocusState.Programmatic);
    }

    public void ShowWorkbench()
    {
        if (!_isSettingsPageVisible)
        {
            return;
        }

        _isSettingsPageVisible = false;
        ShellSettings.Visibility = Visibility.Collapsed;
        StatusBand.Visibility = Visibility.Visible;
        WorkbenchLayout.Visibility = Visibility.Visible;
        SettingsTitleBarButton.Visibility = Visibility.Visible;
        ApplyResponsiveLayout(Root.ActualWidth);
        PlayWorkbenchEntranceAnimation();
    }

    private void PlayWorkbenchEntranceAnimation()
    {
        StatusBand.Opacity = 0;
        WorkbenchLayout.Opacity = 0;

        var storyboard = new Storyboard();
        foreach (var target in new FrameworkElement[] { StatusBand, WorkbenchLayout })
        {
            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
        }

        storyboard.Begin();
    }

    private void AppShellView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateQuickActionsToolTip();
        if (Root.ActualWidth < CompactShellWidth)
        {
            QueueCompactScrollReset();
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (_isSettingsPageVisible)
        {
            return;
        }

        UpdateQuickActionsToolTip();

        var compact = width < CompactShellWidth;
        var reduced = width < ComfortableShellWidth;
        var stackedStatus = compact && width < StackedStatusWidth;
        var layoutModeChanged = _lastCompactLayout != compact;
        _lastCompactLayout = compact;

        PrimaryCommandBar.DefaultLabelPosition = reduced
            ? CommandBarDefaultLabelPosition.Collapsed
            : CommandBarDefaultLabelPosition.Right;

        StatusLayout.RowDefinitions[1].Height = stackedStatus ? GridLength.Auto : new GridLength(0);
        StatusLayout.RowDefinitions[2].Height = compact ? GridLength.Auto : new GridLength(0);
        StatusLayout.ColumnSpacing = stackedStatus ? 0 : compact ? 12 : 18;
        StatusLayout.RowSpacing = stackedStatus ? 6 : 8;
        StatusBand.Padding = compact
            ? new Thickness(16, 8, 16, 8)
            : new Thickness(24, 8, 24, 8);
        WorkbenchLayout.Padding = compact
            ? new Thickness(16)
            : new Thickness(24);
        WorkbenchLayout.ColumnSpacing = reduced ? 16 : 20;
        WorkbenchLayout.RowSpacing = compact ? 16 : 0;

        if (compact)
        {
            StatusCommandColumn.Width = stackedStatus
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            StatusTextColumn.Width = stackedStatus
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            StatusProgressColumn.Width = stackedStatus
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            PrimaryCommandBar.MinHeight = 40;

            Grid.SetRow(PrimaryCommandBar, 0);
            Grid.SetColumn(PrimaryCommandBar, 0);
            Grid.SetColumnSpan(PrimaryCommandBar, stackedStatus ? 3 : 1);
            Grid.SetRow(StatusTextPanel, stackedStatus ? 1 : 0);
            Grid.SetColumn(StatusTextPanel, stackedStatus ? 0 : 1);
            Grid.SetColumnSpan(StatusTextPanel, stackedStatus ? 3 : 2);
            Grid.SetRow(ProgressPanel, 2);
            Grid.SetColumn(ProgressPanel, 0);
            Grid.SetColumnSpan(ProgressPanel, 3);

            SetupColumn.Width = new GridLength(1, GridUnitType.Star);
            WorkbenchColumn.Width = new GridLength(1, GridUnitType.Star);
            WorkbenchLayout.RowDefinitions[0].Height = GridLength.Auto;
            WorkbenchLayout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);

            Grid.SetRow(RunSetup, 0);
            Grid.SetColumn(RunSetup, 0);
            Grid.SetColumnSpan(RunSetup, 2);

            Grid.SetRow(WorkbenchScroll, 1);
            Grid.SetColumn(WorkbenchScroll, 0);
            Grid.SetColumnSpan(WorkbenchScroll, 2);
            RunSetup.SetCompactLayout(true);
            if (layoutModeChanged)
            {
                QueueCompactScrollReset();
            }

            return;
        }

        StatusCommandColumn.Width = GridLength.Auto;
        StatusTextColumn.Width = new GridLength(1, GridUnitType.Star);
        StatusProgressColumn.Width = reduced
            ? new GridLength(188)
            : new GridLength(220);
        PrimaryCommandBar.MinHeight = 0;

        Grid.SetRow(PrimaryCommandBar, 0);
        Grid.SetColumn(PrimaryCommandBar, 0);
        Grid.SetColumnSpan(PrimaryCommandBar, 1);
        Grid.SetRow(StatusTextPanel, 0);
        Grid.SetColumn(StatusTextPanel, 1);
        Grid.SetColumnSpan(StatusTextPanel, 1);
        Grid.SetRow(ProgressPanel, 0);
        Grid.SetColumn(ProgressPanel, 2);
        Grid.SetColumnSpan(ProgressPanel, 1);

        SetupColumn.Width = new GridLength(reduced ? 340 : 380);
        WorkbenchColumn.Width = new GridLength(1, GridUnitType.Star);
        WorkbenchLayout.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        WorkbenchLayout.RowDefinitions[1].Height = new GridLength(0);

        Grid.SetRow(RunSetup, 0);
        Grid.SetColumn(RunSetup, 0);
        Grid.SetColumnSpan(RunSetup, 1);

        Grid.SetRow(WorkbenchScroll, 0);
        Grid.SetColumn(WorkbenchScroll, 1);
        Grid.SetColumnSpan(WorkbenchScroll, 1);
        RunSetup.SetCompactLayout(false);
        if (layoutModeChanged)
        {
            WorkbenchScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    private void QueueCompactScrollReset()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            StartButton.Focus(FocusState.Programmatic);
            foreach (var delay in new[] { 0, 120, 360, 800 })
            {
                if (delay > 0)
                {
                    await Task.Delay(delay);
                }

                WorkbenchScroll.UpdateLayout();
                WorkbenchScroll.ChangeView(null, 0, null, disableAnimation: true);
            }
        });
    }

    private CommandPaletteContext BuildCommandPaletteContext() =>
        new(
            ViewModel,
            StartButton.Command,
            StartButton.CommandParameter,
            CancelButton.Command,
            CancelButton.CommandParameter,
            DiagnosticsButton.Command,
            DiagnosticsButton.CommandParameter,
            RunSetup.BrowseInputDirectoryAsync,
            RunSetup.BrowseConfigPathAsync,
            RunSetup.BrowsePythonExecutableAsync,
            OpenSettingsAsync,
            OpenThresholdSettingsAsync,
            OpenDetectionRulesAsync,
            OpenAboutAsync);

    private Task OpenSettingsAsync()
    {
        ShowSettingsPage();
        return Task.CompletedTask;
    }

    private Task OpenThresholdSettingsAsync()
    {
        ShowSettingsPage("ThresholdsSection");
        return Task.CompletedTask;
    }

    private Task OpenDetectionRulesAsync()
    {
        ShowSettingsPage("DetectionRulesSection");
        return Task.CompletedTask;
    }

    private Task OpenAboutAsync()
    {
        AboutRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task ShowQuickActionsAsync()
    {
        if (_quickActionDialog is not null)
        {
            return;
        }

        _quickActionSearchBox = new TextBox
        {
            PlaceholderText = "搜索命令",
        };
        _quickActionSearchBox.TextChanged += QuickActionSearchBox_TextChanged;
        _quickActionSearchBox.KeyDown += QuickActionSearchBox_KeyDown;

        _quickActionListView = new ListView
        {
            MaxHeight = 360,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            ItemTemplate = BuildQuickActionTemplate(),
        };
        _quickActionListView.ItemClick += QuickActionListView_ItemClick;

        var panel = new StackPanel
        {
            MinWidth = 520,
            Spacing = 12,
        };
        panel.Children.Add(_quickActionSearchBox);
        panel.Children.Add(_quickActionListView);

        _quickActionDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "快速操作",
            Content = panel,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.None,
        };

        RefreshQuickActionList();
        _quickActionDialog.Opened += (_, _) => _quickActionSearchBox.Focus(FocusState.Programmatic);
        _quickActionDialog.Closed += (_, _) =>
        {
            _quickActionDialog = null;
            _quickActionSearchBox = null;
            _quickActionListView = null;
        };

        await _quickActionDialog.ShowAsync();
    }

    private void UpdateQuickActionsToolTip()
    {
        var shortcutText = ViewModel?.QuickActionsShortcutText ?? "快速操作";
        var toolTip = shortcutText.Contains("已关闭", StringComparison.Ordinal)
            ? "快速操作"
            : shortcutText;
        ToolTipService.SetToolTip(QuickActionsTitleBarButton, toolTip);
    }

    private void QuickActionSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshQuickActionList();

    private async void QuickActionSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter)
        {
            e.Handled = true;
            await ExecuteSelectedQuickActionAsync();
        }
        else if (e.Key is VirtualKey.Down && _quickActionListView is { Items.Count: > 0 })
        {
            e.Handled = true;
            _quickActionListView.SelectedIndex = Math.Min(_quickActionListView.SelectedIndex + 1, _quickActionListView.Items.Count - 1);
            _quickActionListView.Focus(FocusState.Programmatic);
        }
    }

    private async void QuickActionListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandPaletteAction action)
        {
            await ExecuteQuickActionAsync(action);
        }
    }

    private async Task ExecuteSelectedQuickActionAsync()
    {
        if (_quickActionListView?.SelectedItem is CommandPaletteAction selected)
        {
            await ExecuteQuickActionAsync(selected);
            return;
        }

        if (_quickActionListView?.Items.FirstOrDefault() is CommandPaletteAction first)
        {
            await ExecuteQuickActionAsync(first);
        }
    }

    private async Task ExecuteQuickActionAsync(CommandPaletteAction action)
    {
        if (!action.IsEnabled)
        {
            return;
        }

        _quickActionDialog?.Hide();
        await action.ExecuteAsync();
    }

    private void RefreshQuickActionList()
    {
        if (_quickActionListView is null)
        {
            return;
        }

        var query = _quickActionSearchBox?.Text.Trim() ?? "";
        var actions = _commandPalette.Build(BuildCommandPaletteContext());
        var filtered = _commandPalette.Filter(actions, query);
        _quickActionListView.ItemsSource = filtered;
        if (filtered.Count > 0)
        {
            _quickActionListView.SelectedIndex = 0;
        }
    }

    private static DataTemplate BuildQuickActionTemplate()
    {
        const string xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
    <Grid ColumnSpacing="12" Padding="4,8">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="32" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <FontIcon Glyph="{Binding Glyph}" FontSize="16" VerticalAlignment="Center" />
        <StackPanel Grid.Column="1" Spacing="2">
            <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
            <TextBlock Text="{Binding Description}" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis" />
        </StackPanel>
        <StackPanel Grid.Column="2" MinWidth="64" HorizontalAlignment="Right" Spacing="2">
            <TextBlock Text="{Binding Category}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" FontSize="11" HorizontalAlignment="Right" />
            <TextBlock Text="{Binding Shortcut}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" HorizontalAlignment="Right" />
        </StackPanel>
    </Grid>
</DataTemplate>
""";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
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
            ScopeOwner = this,
        };
        accelerator.Invoked += invoked;
        KeyboardAccelerators.Add(accelerator);
    }

    private static bool TryExecuteCommand(System.Windows.Input.ICommand? command, object? parameter)
    {
        if (command is null || !command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }

}

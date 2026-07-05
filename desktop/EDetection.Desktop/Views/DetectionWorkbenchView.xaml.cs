using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EDetection.Desktop.Views;

public sealed partial class DetectionWorkbenchView : UserControl
{
    private const double CompactWidth = 660;

    public DetectionWorkbenchView()
    {
        InitializeComponent();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width < CompactWidth;
        ApplyMetricLayout(compact);
        ApplyFirstRunLayout(compact);
        ApplyTelemetryLayout(compact);
        ApplyActionLayouts(compact);
        ApplyRiskLayout(compact);
        ApplyDetailFilterLayout(compact);
    }

    private void ApplyMetricLayout(bool compact)
    {
        SetFourColumnGrid(MetricGrid, compact, MetricColumn0, MetricColumn1, MetricColumn2, MetricColumn3);
        PlaceWide(ProcessedMetricCard, 0);
        PlaceWide(AnomalyFilesMetricCard, 1);
        PlaceCompactOrWide(AnomalyRecordsMetricCard, compact, 1, 0, 2);
        PlaceCompactOrWide(SkippedMetricCard, compact, 1, 1, 3);
    }

    private void ApplyFirstRunLayout(bool compact)
    {
        FirstRunGuideLayout.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        FirstRunActionColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        FirstRunActionPanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        FirstRunActionPanel.Width = compact ? double.NaN : 188;

        Grid.SetRow(FirstRunActionPanel, compact ? 1 : 0);
        Grid.SetColumn(FirstRunActionPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(FirstRunActionPanel, compact ? 2 : 1);
    }

    private void ApplyTelemetryLayout(bool compact)
    {
        SetFourColumnGrid(RunTelemetryStatsGrid, compact, TelemetryColumn0, TelemetryColumn1, TelemetryColumn2, TelemetryColumn3);
        TelemetryColumn0.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(1.4, GridUnitType.Star);
        PlaceWide(TelemetryProgressPanel, 0);
        PlaceWide(TelemetrySpeedPanel, 1);
        PlaceCompactOrWide(TelemetryElapsedPanel, compact, 1, 0, 2);
        PlaceCompactOrWide(TelemetryRemainingPanel, compact, 1, 1, 3);
    }

    private void ApplyActionLayouts(bool compact)
    {
        ApplyTwoColumnActionLayout(
            CompletionActionsLayout,
            CompletionButtonPanel,
            CompletionButtonColumn,
            compact);
        ApplyTwoColumnActionLayout(
            FailureActionsLayout,
            FailureButtonPanel,
            FailureButtonColumn,
            compact);
    }

    private void ApplyRiskLayout(bool compact)
    {
        RiskLayout.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        HighRiskColumn.Width = new GridLength(1.4, GridUnitType.Star);
        IssueRankColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(HighRiskPanel, 0);
        Grid.SetColumn(HighRiskPanel, 0);
        Grid.SetColumnSpan(HighRiskPanel, compact ? 2 : 1);

        Grid.SetRow(IssueRankPanel, compact ? 1 : 0);
        Grid.SetColumn(IssueRankPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(IssueRankPanel, compact ? 2 : 1);
    }

    private void ApplyDetailFilterLayout(bool compact)
    {
        DetailFilterGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);

        if (compact)
        {
            DetailSearchColumn.Width = new GridLength(132);
            DetailSeverityColumn.Width = new GridLength(180);
            DetailIssueColumn.Width = new GridLength(1, GridUnitType.Star);
            Place(DetailSearchBox, 0, 0, 8);
            Place(SeverityFilter, 1, 0);
            Place(IssueTypeFilter, 1, 1);
            Place(ClearDetailFiltersButton, 1, 3);
            Place(OpenSelectedDetailSourceButton, 1, 4);
            Place(CopySelectedDetailSourcePathButton, 1, 5);
            Place(CopySelectedDetailButton, 1, 6);
            Place(CopyFilteredDetailsButton, 1, 7);
            return;
        }

        DetailSearchColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailSeverityColumn.Width = new GridLength(132);
        DetailIssueColumn.Width = new GridLength(150);
        Place(DetailSearchBox, 0, 0);
        Place(SeverityFilter, 0, 1);
        Place(IssueTypeFilter, 0, 2);
        Place(ClearDetailFiltersButton, 0, 3);
        Place(OpenSelectedDetailSourceButton, 0, 4);
        Place(CopySelectedDetailSourcePathButton, 0, 5);
        Place(CopySelectedDetailButton, 0, 6);
        Place(CopyFilteredDetailsButton, 0, 7);
    }

    private static void SetFourColumnGrid(
        Grid grid,
        bool compact,
        ColumnDefinition first,
        ColumnDefinition second,
        ColumnDefinition third,
        ColumnDefinition fourth)
    {
        grid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        first.Width = new GridLength(1, GridUnitType.Star);
        second.Width = new GridLength(1, GridUnitType.Star);
        third.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        fourth.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private static void ApplyTwoColumnActionLayout(
        Grid layout,
        StackPanel buttonPanel,
        ColumnDefinition buttonColumn,
        bool compact)
    {
        layout.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        buttonColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(buttonPanel, compact ? 1 : 0);
        Grid.SetColumn(buttonPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(buttonPanel, compact ? 2 : 1);
        buttonPanel.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private static void PlaceWide(FrameworkElement element, int column) =>
        Place(element, 0, column);

    private static void PlaceCompactOrWide(
        FrameworkElement element,
        bool compact,
        int compactRow,
        int compactColumn,
        int wideColumn) =>
        Place(element, compact ? compactRow : 0, compact ? compactColumn : wideColumn);

    private static void Place(FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using EDetection.NativeCore.Models;

namespace EDetection.NativeCore.Services;

public sealed partial class NativeDetectionBackendService : IDetectionBackendService
{
    private static readonly string[] ReportValueColumns =
    [
        "Uab",
        "Ubc",
        "Uca",
        "Ia",
        "Ib",
        "Ic",
        "有功功率",
        "无功功率",
        "功率因数",
        "A相温度",
        "B相温度",
        "C相温度",
    ];

    private static readonly IReadOnlyDictionary<string, string> PhaseMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Uab"] = "A相电压",
            ["Ubc"] = "B相电压",
            ["Uca"] = "C相电压",
            ["Ia"] = "A相电流",
            ["Ib"] = "B相电流",
            ["Ic"] = "C相电流",
            ["A相温度"] = "A相温度",
            ["B相温度"] = "B相温度",
            ["C相温度"] = "C相温度",
        };

    static NativeDetectionBackendService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<int> RunDetectionAsync(
        DetectionRequest request,
        IProgress<DetectionBackendEvent> progress,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (string.IsNullOrWhiteSpace(request.InputDirectory))
        {
            progress.Report(new DetectionBackendEvent
            {
                EventName = "error",
                ErrorType = nameof(ArgumentException),
                Message = "输入目录不能为空。",
            });
            return 1;
        }

        if (!Directory.Exists(request.InputDirectory))
        {
            progress.Report(new DetectionBackendEvent
            {
                EventName = "error",
                ErrorType = nameof(DirectoryNotFoundException),
                Message = $"输入目录不存在: {request.InputDirectory}",
            });
            return 1;
        }

        var inputRoot = Path.GetFullPath(request.InputDirectory);
        var outputRoot = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? inputRoot
            : Path.GetFullPath(request.OutputDirectory);
        var configResult = LoadConfig(request.ConfigPath);
        if (!configResult.IsSuccess)
        {
            progress.Report(new DetectionBackendEvent
            {
                EventName = "error",
                ErrorType = configResult.ErrorType,
                Message = configResult.ErrorMessage,
            });
            return 1;
        }

        var config = configResult.Config;
        var parityTraceKey = GetParityTraceKey();
        var files = Directory
            .EnumerateFiles(inputRoot, "*.csv", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var startedAt = DateTimeOffset.UtcNow;
        var normalFiles = 0;
        var skippedFiles = 0;
        var anomalyFiles = 0;
        var anomalyRecords = 0;
        var sensorStatusRows = new List<NativeSensorStatusRow>();
        var skippedStatusRows = new List<NativeSensorStatusRow>();
        var skippedDetails = 0;
        var detailPreview = new List<ReportDetailPreview>();
        IReadOnlyList<ReportDetailPreview> reportDetails = [];
        DetectionBackendEvent? reportSummary = null;
        string? reportPath = null;

        progress.Report(new DetectionBackendEvent
        {
            EventName = "run_started",
            InputDirectory = inputRoot,
            OutputDirectory = outputRoot,
            TotalFiles = files.Count,
            WriteReport = request.WriteReport,
            GeneratedAt = generatedAt,
        });

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = files[index];
            var fileName = Path.GetFileName(path);
            var relativePath = Path.GetRelativePath(inputRoot, path);
            var location = ExtractLocation(path, inputRoot);
            var isHighVoltage = fileName.Contains("高压", StringComparison.OrdinalIgnoreCase);
            var analysis = isHighVoltage
                ? NativeFileAnalysis.Skipped($"跳过高压设备: {fileName}")
                : AnalyzeFile(path, fileName, relativePath, location, config, parityTraceKey);
            if (parityTraceKey is not null && analysis.ParityTrace is null)
            {
                analysis = analysis with
                {
                    ParityTrace = BuildUnavailableParityTrace(parityTraceKey),
                };
            }

            if (isHighVoltage)
            {
                skippedFiles++;
                skippedDetails++;
                skippedStatusRows.Add(NativeSensorStatusRow.Skipped(fileName, relativePath, location, analysis.Message));
            }
            else if (analysis.Status == "skipped")
            {
                skippedFiles++;
                skippedDetails++;
                skippedStatusRows.Add(NativeSensorStatusRow.Skipped(fileName, relativePath, location, analysis.Message));
            }
            else if (analysis.AnomalyCount > 0)
            {
                anomalyFiles++;
                anomalyRecords += analysis.AnomalyCount;
                sensorStatusRows.Add(analysis.SensorStatus);
                AddDetailPreview(detailPreview, analysis.DetailPreview, relativePath);
            }
            else
            {
                normalFiles++;
                sensorStatusRows.Add(analysis.SensorStatus);
                AddDetailPreview(detailPreview, analysis.DetailPreview, relativePath);
            }

            progress.Report(new DetectionBackendEvent
            {
                EventName = "file_result",
                SourceFile = fileName,
                RelativePath = relativePath,
                Building = location.Building,
                Transformer = location.Transformer,
                Status = analysis.Status,
                Message = analysis.Message,
                ProcessedFiles = index + 1,
                TotalFiles = files.Count,
                AnomalyCount = analysis.AnomalyCount,
                AnomalyTypes = analysis.AnomalyTypes,
                NativeParityTrace = analysis.ParityTrace,
            });
            progress.Report(new DetectionBackendEvent
            {
                EventName = "file_progress",
                SourceFile = fileName,
                ProcessedFiles = index + 1,
                TotalFiles = files.Count,
                Percent = files.Count == 0 ? 1.0 : (double)(index + 1) / files.Count,
            });
        }

        if (anomalyRecords > 0 || sensorStatusRows.Count > 0 || skippedDetails > 0)
        {
            var sortedDetailPreview = detailPreview
                .OrderBy(static detail => SeverityRank(detail.Severity))
                .ToList();
            reportDetails = sortedDetailPreview;
            var deviceSummaries = BuildDeviceSummaries(sortedDetailPreview);
            reportSummary = new DetectionBackendEvent
            {
                EventName = "report_summary",
                InputDirectory = inputRoot,
                OutputDirectory = outputRoot,
                TotalFiles = files.Count,
                ProcessedFiles = files.Count,
                NormalFiles = normalFiles,
                AnomalyFiles = anomalyFiles,
                AnomalyRecords = anomalyRecords,
                SkippedFiles = skippedFiles,
                GeneratedAt = generatedAt,
                DeviceCount = deviceSummaries.Count,
                HighRiskDevices = deviceSummaries
                    .Where(static device => device.HighestSeverity == "高")
                    .Take(10)
                    .ToList(),
                TopIssueTypes = BuildTopIssueTypes(sortedDetailPreview),
                SensorOverview = new ReportSensorOverview
                {
                    TotalRows = sensorStatusRows.Count + skippedDetails,
                    OfflineDevices = sensorStatusRows.Count(static row => row.IsOffline),
                    SensorFaultRows = sensorStatusRows.Count(static row => row.SensorFaults.Count > 0),
                    SensorMissingRows = sensorStatusRows.Count(static row => row.SensorMissing.Count > 0),
                    SkippedRows = skippedDetails,
                },
                DetailPreviewCount = sortedDetailPreview.Count,
                DetailPreview = sortedDetailPreview.Take(100).ToList(),
            };
            progress.Report(reportSummary);
        }

        if (request.WriteReport && reportSummary is not null)
        {
            try
            {
                reportPath = WriteNativeExcelReport(
                    outputRoot,
                    generatedAt,
                    reportSummary,
                    config,
                    reportDetails,
                    sensorStatusRows.Concat(skippedStatusRows).ToList());
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                progress.Report(new DetectionBackendEvent
                {
                    EventName = "error",
                    ErrorType = ex.GetType().Name,
                    Message = $"报告写入失败: {outputRoot} ({ex.Message})",
                });
                return 1;
            }

            progress.Report(new DetectionBackendEvent
            {
                EventName = "report_written",
                ReportPath = reportPath,
                AnomalyRecords = anomalyRecords,
            });
        }

        progress.Report(new DetectionBackendEvent
        {
            EventName = "run_completed",
            InputDirectory = inputRoot,
            OutputDirectory = outputRoot,
            TotalFiles = files.Count,
            ProcessedFiles = files.Count,
            NormalFiles = normalFiles,
            AnomalyFiles = anomalyFiles,
            AnomalyRecords = anomalyRecords,
            SkippedFiles = skippedFiles,
            ReportPath = reportPath,
            DurationSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            GeneratedAt = generatedAt,
        });
        return 0;
    }

    private static NativeConfigLoadResult LoadConfig(string? configPath)
    {
        var config = new NativeDetectionConfig();
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return NativeConfigLoadResult.Success(config);
        }

        try
        {
            var resolvedPath = Path.GetFullPath(configPath);
            if (!File.Exists(resolvedPath))
            {
                return NativeConfigLoadResult.Success(config);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(resolvedPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return NativeConfigLoadResult.Error(
                    "ValueError",
                    $"配置文件格式错误: {resolvedPath}，根节点必须是对象。");
            }

            var root = document.RootElement;
            var currentOverloadEnabled = GetBool(root, "current_overload", config.CurrentOverloadEnabled);
            var currentUnbalanceEnabled = GetBool(root, "current_unbalance", config.CurrentUnbalanceEnabled);
            if (root.TryGetProperty("current", out var legacyCurrent)
                && legacyCurrent.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                currentOverloadEnabled = legacyCurrent.GetBoolean();
                currentUnbalanceEnabled = legacyCurrent.GetBoolean();
            }

            return NativeConfigLoadResult.Success(
                new NativeDetectionConfig(
                    GetFiniteDouble(root, "V_MIN_THRESHOLD", config.VoltageMinThreshold),
                    GetFiniteDouble(root, "V_MAX_THRESHOLD", config.VoltageMaxThreshold),
                    GetFiniteDouble(root, "V_IMBALANCE_THRESHOLD", config.VoltageImbalanceThreshold),
                    GetFiniteDouble(root, "I_MAX_THRESHOLD", config.CurrentMaxThreshold),
                    GetFiniteDouble(root, "I_UNBALANCE_MAX_THRESHOLD", config.CurrentUnbalanceMaxThreshold),
                    GetFiniteDouble(root, "I_MIN_ACTIVE_THRESHOLD", config.CurrentActiveMinThreshold),
                    GetFiniteDouble(root, "P_ACTIVE_MIN_THRESHOLD", config.ActivePowerMinThreshold),
                    GetFiniteDouble(root, "PF_MIN_THRESHOLD", config.PowerFactorMinThreshold),
                    GetFiniteDouble(root, "T_MIN_THRESHOLD", config.TemperatureMinThreshold),
                    GetFiniteDouble(root, "T_MAX_THRESHOLD", config.TemperatureMaxThreshold),
                    currentOverloadEnabled,
                    currentUnbalanceEnabled,
                    GetBool(root, "power_factor", config.PowerFactorEnabled),
                    GetBool(root, "detail_output", config.DetailOutputEnabled),
                    GetPositiveInt(root, "FREEZE_COUNT_THRESHOLD", config.FreezeCountThreshold),
                    GetFiniteDouble(root, "FREEZE_STD_THRESHOLD", config.FreezeStdThreshold)));
        }
        catch (JsonException ex)
        {
            return NativeConfigLoadResult.Error(
                "ValueError",
                $"配置文件无法解析: {ResolveDisplayPath(configPath)} ({ex.Message})");
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException)
        {
            return NativeConfigLoadResult.Error(
                ex.GetType().Name,
                $"配置文件不可读取: {ResolveDisplayPath(configPath)} ({ex.Message})");
        }
        catch (Exception ex) when (ex is FormatException
                                   or OverflowException
                                   or InvalidOperationException)
        {
            return NativeConfigLoadResult.Error(
                "ValueError",
                $"配置文件数值无效: {ResolveDisplayPath(configPath)} ({ex.Message})");
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return NativeConfigLoadResult.Error(
                ex.GetType().Name,
                $"配置文件路径错误: {configPath} ({ex.Message})");
        }
    }

    private static string ResolveDisplayPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return path;
        }
    }

    private static string WriteNativeExcelReport(
        string outputRoot,
        string generatedAt,
        DetectionBackendEvent summary,
        NativeDetectionConfig config,
        IReadOnlyList<ReportDetailPreview> reportDetails,
        IReadOnlyList<NativeSensorStatusRow> sensorRows)
    {
        Directory.CreateDirectory(outputRoot);
        var reportPath = GetUniqueReportPath(outputRoot);
        using var archive = ZipFile.Open(reportPath, ZipArchiveMode.Create);

        AddZipEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet3.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet4.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet5.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet6.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        AddZipEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        AddZipEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
        AddZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml());
        AddZipEntry(archive, "xl/styles.xml", BuildStylesXml());

        var details = reportDetails;
        AddZipEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(BuildOverviewRows(summary, generatedAt)));
        AddZipEntry(archive, "xl/worksheets/sheet2.xml", BuildDetailWorksheetXml(details));
        AddZipEntry(archive, "xl/worksheets/sheet3.xml", BuildWorksheetXml(BuildDeviceRows(details)));
        AddZipEntry(archive, "xl/worksheets/sheet4.xml", BuildWorksheetXml(BuildIssueRows(details)));
        AddZipEntry(archive, "xl/worksheets/sheet5.xml", BuildWorksheetXml(BuildSensorRows(sensorRows)));
        AddZipEntry(archive, "xl/worksheets/sheet6.xml", BuildWorksheetXml(BuildConfigRows(config, summary, generatedAt)));
        return reportPath;
    }

    private static string GetUniqueReportPath(string outputRoot)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(outputRoot, $"电气异常报告_{timestamp}.xlsx");
        if (!File.Exists(path))
        {
            return path;
        }

        for (var index = 1; index < 100; index++)
        {
            var candidate = Path.Combine(outputRoot, $"电气异常报告_{timestamp}_{index}.xlsx");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(outputRoot, $"电气异常报告_{timestamp}_{Guid.NewGuid():N}.xlsx");
    }

    private static void AddZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildWorkbookXml() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="检测概览" sheetId="1" r:id="rId1"/>
            <sheet name="异常明细" sheetId="2" r:id="rId2"/>
            <sheet name="设备汇总" sheetId="3" r:id="rId3"/>
            <sheet name="异常分类统计" sheetId="4" r:id="rId4"/>
            <sheet name="传感器状态" sheetId="5" r:id="rId5"/>
            <sheet name="检测配置" sheetId="6" r:id="rId6"/>
          </sheets>
        </workbook>
        """;

    private static string BuildWorkbookRelationshipsXml() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet3.xml"/>
          <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet4.xml"/>
          <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet5.xml"/>
          <Relationship Id="rId6" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet6.xml"/>
          <Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private static string BuildStylesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2">
            <font><sz val="11"/><name val="Calibri"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font>
          </fonts>
          <fills count="7">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFF4CCCC"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFCE5CD"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFD9EAD3"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFF2CC"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FFD9E2F3"/></left><right style="thin"><color rgb="FFD9E2F3"/></right><top style="thin"><color rgb="FFD9E2F3"/></top><bottom style="thin"><color rgb="FFD9E2F3"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="6">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="4" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="5" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="6" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private static string BuildWorksheetXml(
        IEnumerable<IReadOnlyList<object?>> rows,
        Func<int, int, object?, int?>? styleSelector = null)
    {
        var materializedRows = rows.ToList();
        var maxColumns = materializedRows.Count == 0 ? 0 : materializedRows.Max(static row => row.Count);
        var maxRows = materializedRows.Count;
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        AppendColumns(builder, materializedRows, maxColumns);
        builder.AppendLine("<sheetData>");

        var rowIndex = 1;
        foreach (var row in materializedRows)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowIndex}\">");
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var value = row[columnIndex];
                var styleIndex = rowIndex == 1
                    ? 1
                    : styleSelector?.Invoke(rowIndex, columnIndex + 1, value) ?? 0;
                AppendCell(builder, value, ColumnName(columnIndex + 1), rowIndex, styleIndex);
            }

            builder.AppendLine("</row>");
            rowIndex++;
        }

        builder.AppendLine("</sheetData>");
        if (maxColumns > 1 && maxRows > 1)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:{ColumnName(maxColumns)}{maxRows}\"/>");
        }

        builder.AppendLine("</worksheet>");
        return builder.ToString();
    }

    private static string BuildDetailWorksheetXml(IReadOnlyList<ReportDetailPreview> details)
    {
        var rows = BuildDetailRows(details);
        return BuildWorksheetXml(
            rows,
            (rowIndex, columnIndex, _) =>
            {
                if (rowIndex < 2 || rowIndex - 2 >= details.Count)
                {
                    return 0;
                }

                var detail = details[rowIndex - 2];
                var reportValueColumnIndex = columnIndex - 11;
                if (reportValueColumnIndex >= 0
                    && reportValueColumnIndex < ReportValueColumns.Length
                    && IsAffectedReportColumn(detail, ReportValueColumns[reportValueColumnIndex]))
                {
                    return 5;
                }

                return SeverityStyleIndex(detail.Severity);
            });
    }

    private static void AppendColumns(
        StringBuilder builder,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int maxColumns)
    {
        if (maxColumns == 0)
        {
            return;
        }

        builder.AppendLine("<cols>");
        for (var columnIndex = 1; columnIndex <= maxColumns; columnIndex++)
        {
            var width = 10;
            foreach (var row in rows)
            {
                if (columnIndex - 1 < row.Count)
                {
                    var value = row[columnIndex - 1]?.ToString() ?? "";
                    width = Math.Max(width, Math.Min(value.Length + 2, 42));
                }
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"<col min=\"{columnIndex}\" max=\"{columnIndex}\" width=\"{width}\" customWidth=\"1\"/>");
        }

        builder.AppendLine("</cols>");
    }

    private static void AppendCell(StringBuilder builder, object? value, string columnName, int rowIndex, int styleIndex)
    {
        var cellReference = $"{columnName}{rowIndex}";
        var styleAttribute = styleIndex > 0 ? $" s=\"{styleIndex}\"" : "";
        switch (value)
        {
            case null:
                builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellReference}\"{styleAttribute}/>");
                break;
            case int or long or double or float or decimal:
                builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellReference}\"{styleAttribute}><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>");
                break;
            default:
                builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellReference}\"{styleAttribute} t=\"inlineStr\"><is><t>{EscapeXml(EscapeExcelFormulaText(value.ToString() ?? ""))}</t></is></c>");
                break;
        }
    }

    private static string ColumnName(int columnNumber)
    {
        var name = "";
        while (columnNumber > 0)
        {
            columnNumber--;
            name = (char)('A' + columnNumber % 26) + name;
            columnNumber /= 26;
        }

        return name;
    }

    private static string EscapeXml(string value) => SecurityElement.Escape(value) ?? "";

    private static string EscapeExcelFormulaText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var stripped = value.TrimStart(' ');
        return value[0] is '\t' or '\r' or '\n'
               || stripped.StartsWith("=", StringComparison.Ordinal)
               || stripped.StartsWith("+", StringComparison.Ordinal)
               || stripped.StartsWith("-", StringComparison.Ordinal)
               || stripped.StartsWith("@", StringComparison.Ordinal)
            ? $"'{value}"
            : value;
    }

    private static int SeverityStyleIndex(string? severity) =>
        severity switch
        {
            "高" => 2,
            "中" => 3,
            "低" => 4,
            _ => 0,
        };

    private static bool IsAffectedReportColumn(ReportDetailPreview detail, string column)
    {
        var issueText = $"{detail.IssueType} {detail.IssueDetail}";
        if (!detail.ReportValues.ContainsKey(column))
        {
            return false;
        }

        if ((issueText.Contains("电压", StringComparison.Ordinal)
             || issueText.Contains("PT", StringComparison.Ordinal))
            && column is "Uab" or "Ubc" or "Uca")
        {
            return true;
        }

        if (issueText.Contains("电流", StringComparison.Ordinal)
            && column is "Ia" or "Ib" or "Ic")
        {
            return true;
        }

        if ((issueText.Contains("CT", StringComparison.Ordinal)
             || issueText.Contains("有功", StringComparison.Ordinal))
            && column is "Ia" or "Ib" or "Ic" or "有功功率")
        {
            return true;
        }

        if (issueText.Contains("功率因数", StringComparison.Ordinal)
            && column == "功率因数")
        {
            return true;
        }

        if ((issueText.Contains("温度", StringComparison.Ordinal)
             || issueText.Contains("传感器", StringComparison.Ordinal))
            && column is "A相温度" or "B相温度" or "C相温度")
        {
            return true;
        }

        if (issueText.Contains("冻结", StringComparison.Ordinal)
            || issueText.Contains("恒定", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildOverviewRows(
        DetectionBackendEvent summary,
        string generatedAt)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "指标", "值" },
            new object?[] { "输入目录", summary.InputDirectory },
            new object?[] { "输出目录", summary.OutputDirectory },
            new object?[] { "文件总数", summary.TotalFiles },
            new object?[] { "已处理文件", summary.ProcessedFiles },
            new object?[] { "正常文件", summary.NormalFiles },
            new object?[] { "异常文件", summary.AnomalyFiles },
            new object?[] { "异常记录", summary.AnomalyRecords },
            new object?[] { "跳过文件", summary.SkippedFiles },
            new object?[] { "生成时间", generatedAt },
            Array.Empty<object?>(),
            new object?[] { "高风险设备 Top 10", "异常记录数" },
        };

        foreach (var device in summary.HighRiskDevices ?? [])
        {
            rows.Add(new object?[] { $"{device.Building} / {device.Transformer} ({device.DevicePath})", device.AnomalyRecords });
        }

        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildDetailRows(IReadOnlyList<ReportDetailPreview> details)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "建筑", "相对路径", "变压器", "来源文件", "日期", "时间", "严重等级", "异常类型", "异常详情", "异常值" }
                .Concat(ReportValueColumns.Cast<object?>())
                .Concat(new object?[] { "建议处置" })
                .ToArray(),
        };
        foreach (var detail in details)
        {
            rows.Add(new object?[]
                {
                    detail.Building,
                    detail.RelativePath,
                    detail.Transformer,
                    detail.SourceFile,
                    detail.Date,
                    detail.Time,
                    detail.Severity,
                    detail.IssueType,
                    detail.IssueDetail,
                    detail.IssueValue,
                }
                .Concat(ReportValueColumns.Select(column =>
                    detail.ReportValues.TryGetValue(column, out var value) ? value : (object?)null))
                .Concat(new object?[] { detail.RecommendedAction })
                .ToArray());
        }

        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildDeviceRows(IReadOnlyList<ReportDetailPreview> details)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "建筑", "设备路径", "变压器", "异常记录数", "主要异常类型", "首次异常时间", "末次异常时间", "最高严重等级", "建议优先级" },
        };
        foreach (var device in BuildDeviceSummaries(details))
        {
            rows.Add(new object?[]
            {
                device.Building,
                device.DevicePath,
                device.Transformer,
                device.AnomalyRecords,
                device.MainIssueTypes,
                FirstDetailTime(details, device.Building, device.DevicePath, device.Transformer),
                LastDetailTime(details, device.Building, device.DevicePath, device.Transformer),
                device.HighestSeverity,
                device.Priority,
            });
        }

        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildIssueRows(IReadOnlyList<ReportDetailPreview> details)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "统计维度", "名称", "数量" },
        };
        foreach (var issue in BuildIssueTypeStatistics(details))
        {
            rows.Add(new object?[] { "异常类型", issue.Name, issue.Count });
        }

        foreach (var group in details
                     .GroupBy(static detail => string.IsNullOrWhiteSpace(detail.Building) ? "根目录" : detail.Building)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            rows.Add(new object?[] { "建筑", group.Key, group.Count() });
        }

        foreach (var group in details
                     .GroupBy(static detail => string.IsNullOrWhiteSpace(detail.Transformer) ? "未知" : detail.Transformer)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            rows.Add(new object?[] { "变压器", group.Key, group.Count() });
        }

        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildSensorRows(IReadOnlyList<NativeSensorStatusRow> sensorRows)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "来源文件", "建筑", "相对路径", "变压器", "是否离线", "传感器故障", "传感器未配置", "状态", "原因" },
        };
        foreach (var row in sensorRows)
        {
            rows.Add(new object?[]
            {
                row.SourceFile,
                row.Building,
                row.RelativePath,
                row.Transformer,
                row.IsOffline ? "是" : "否",
                string.Join("；", row.SensorFaults),
                string.Join("；", row.SensorMissing),
                row.Status,
                row.Reason,
            });
        }

        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildConfigRows(
        NativeDetectionConfig config,
        DetectionBackendEvent summary,
        string generatedAt)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "配置项", "值" },
            new object?[] { "backend_mode", "native" },
            new object?[] { "report_backend", nameof(NativeDetectionBackendService) },
            new object?[] { "V_MIN_THRESHOLD", config.VoltageMinThreshold },
            new object?[] { "V_MAX_THRESHOLD", config.VoltageMaxThreshold },
            new object?[] { "V_IMBALANCE_THRESHOLD", config.VoltageImbalanceThreshold },
            new object?[] { "I_MAX_THRESHOLD", config.CurrentMaxThreshold },
            new object?[] { "I_UNBALANCE_MAX_THRESHOLD", config.CurrentUnbalanceMaxThreshold },
            new object?[] { "I_MIN_ACTIVE_THRESHOLD", config.CurrentActiveMinThreshold },
            new object?[] { "P_ACTIVE_MIN_THRESHOLD", config.ActivePowerMinThreshold },
            new object?[] { "PF_MIN_THRESHOLD", config.PowerFactorMinThreshold },
            new object?[] { "T_MIN_THRESHOLD", config.TemperatureMinThreshold },
            new object?[] { "T_MAX_THRESHOLD", config.TemperatureMaxThreshold },
            new object?[] { "current_overload", config.CurrentOverloadEnabled.ToString(CultureInfo.InvariantCulture) },
            new object?[] { "current_unbalance", config.CurrentUnbalanceEnabled.ToString(CultureInfo.InvariantCulture) },
            new object?[] { "power_factor", config.PowerFactorEnabled.ToString(CultureInfo.InvariantCulture) },
            new object?[] { "detail_output", config.DetailOutputEnabled.ToString(CultureInfo.InvariantCulture) },
            new object?[] { "FREEZE_COUNT_THRESHOLD", config.FreezeCountThreshold },
            new object?[] { "FREEZE_STD_THRESHOLD", config.FreezeStdThreshold },
            Array.Empty<object?>(),
            new object?[] { "运行信息", "值" },
            new object?[] { "input_dir", summary.InputDirectory },
            new object?[] { "output_dir", summary.OutputDirectory },
            new object?[] { "total_files", summary.TotalFiles },
            new object?[] { "processed_files", summary.ProcessedFiles },
            new object?[] { "normal_files", summary.NormalFiles },
            new object?[] { "anomaly_files", summary.AnomalyFiles },
            new object?[] { "anomaly_records", summary.AnomalyRecords },
            new object?[] { "skipped_files", summary.SkippedFiles },
            new object?[] { "generated_at", generatedAt },
        };
        return rows;
    }

    private static NativeFileAnalysis AnalyzeFile(
        string path,
        string fileName,
        string relativePath,
        NativeLocation location,
        NativeDetectionConfig config,
        string? parityTraceKey)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = ReadCsvLines(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or DecoderFallbackException
                                   or ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return NativeFileAnalysis.Skipped(
                $"跳过空文件或读取失败: {fileName} ({ex.GetType().Name})");
        }

        if (lines.Count < 2)
        {
            return NativeFileAnalysis.Skipped($"跳过空文件或读取失败: {fileName}");
        }

        var headers = SplitCsvLine(lines[0]);
        var columns = headers
            .Select((header, index) => new { Name = NormalizeColumnName(header), Index = index })
            .ToList();
        if (!columns.Any(static column => IsKeyParameterColumn(column.Name)))
        {
            return NativeFileAnalysis.Skipped($"跳过：未匹配到关键参数。当前列：{string.Join(", ", headers.Take(5))}...");
        }

        var timeIndex = FindTimeColumnIndex(headers);
        var voltageColumnGroups = columns
            .Where(static column => IsVoltageColumn(column.Name))
            .GroupBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static group => new NativeColumnGroup(
                group.Key,
                group.Select(static column => column.Index).ToList()))
            .ToList();
        var currentColumnGroups = columns
            .Where(static column => IsCurrentColumn(column.Name))
            .GroupBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static group => new NativeColumnGroup(
                group.Key,
                group.Select(static column => column.Index).ToList()))
            .ToList();
        var activePowerColumnGroups = columns
            .Where(static column => column.Name == "有功功率")
            .GroupBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static group => new NativeColumnGroup(
                group.Key,
                group.Select(static column => column.Index).ToList()))
            .ToList();
        var powerFactorColumnGroups = columns
            .Where(static column => column.Name == "功率因数")
            .GroupBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static group => new NativeColumnGroup(
                group.Key,
                group.Select(static column => column.Index).ToList()))
            .ToList();
        var reactivePowerColumnGroups = columns
            .Where(static column => column.Name == "无功功率")
            .GroupBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static group => new NativeColumnGroup(
                group.Key,
                group.Select(static column => column.Index).ToList()))
            .ToList();
        var temperatureColumnGroups = columns
            .Where(static column => IsTemperatureColumn(column.Name))
            .GroupBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static group => new NativeColumnGroup(
                group.Key,
                group.Select(static column => column.Index).ToList()))
            .ToList();
        if (voltageColumnGroups.Count == 0 && currentColumnGroups.Count == 0)
        {
            return NativeFileAnalysis.Normal(fileName);
        }

        var anomalyRows = 0;
        // Derive a file's anomaly_types from the final structured
        // per-row "异常详情" text.  Keep the original row labels until the
        // file is complete so a mixed row (for example voltage + frozen data)
        // retains the same composite summary as the established detection contract.
        var anomalyTypeDetails = new List<string>();
        var rowAnalyses = new List<NativeRowAnalysis>();
        // The trace is intentionally populated only in diagnostic mode.  The
        // lists retain label order and row boundaries until they are HMACed;
        // no plaintext leaves this method.
        List<IReadOnlyList<string>>? rawRowLabels = parityTraceKey is null ? null : [];
        List<IReadOnlyList<string>>? reportableRowLabels = parityTraceKey is null ? null : [];
        var detailPreview = new List<ReportDetailPreview>();
        var keyColumnGroups = voltageColumnGroups
            .Concat(currentColumnGroups)
            .ToList();
        var offlineInvalidCounts = keyColumnGroups.ToDictionary(
            static group => group.Name,
            static _ => 0,
            StringComparer.Ordinal);
        var sensorColumnGroups = reactivePowerColumnGroups
            .Concat(powerFactorColumnGroups)
            .Concat(temperatureColumnGroups)
            .ToList();
        var sensorStates = sensorColumnGroups.ToDictionary(
            static group => group.Name,
            static _ => new NativeSensorColumnState(),
            StringComparer.Ordinal);
        var dataRows = 0;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = SplitCsvLine(line);
            if (timeIndex < cells.Count && IsStatisticsTailRow(cells[timeIndex]))
            {
                break;
            }

            var rowTime = timeIndex < cells.Count ? cells[timeIndex] : "";
            dataRows++;
            var rowVoltageValues = BuildRowVoltageValues(cells, voltageColumnGroups);
            var rowCurrentValues = BuildRowNumericValues(cells, currentColumnGroups);
            var rowActivePowerValues = BuildRowNumericValues(cells, activePowerColumnGroups);
            var rowPowerFactorValues = BuildRowNumericValues(cells, powerFactorColumnGroups);
            var rowTemperatureValues = BuildRowNumericValues(cells, temperatureColumnGroups);
            UpdateOfflineInvalidCounts(BuildRowMergedNumericValues(cells, voltageColumnGroups), offlineInvalidCounts);
            UpdateOfflineInvalidCounts(BuildRowMergedNumericValues(cells, currentColumnGroups), offlineInvalidCounts);
            // pandas' fillna(0) treats blank sensor cells as zero when
            // deciding whether a configured sensor is missing. Observe every
            // configured sensor for every data row, including blank cells.
            UpdateSensorStates(cells, sensorColumnGroups, sensorStates);
            var rowFreezeCoreValues = BuildRowNumericValues(
                cells,
                voltageColumnGroups
                    .Concat(currentColumnGroups)
                    .Concat(activePowerColumnGroups)
                    .ToList());
            var rowFreezeAuxValues = BuildRowNumericValues(
                cells,
                reactivePowerColumnGroups
                    .Concat(powerFactorColumnGroups)
                    .ToList());
            var rowTypes = DetectVoltageAnomalyTypes(rowVoltageValues, voltageColumnGroups.Count, config)
                .Concat(DetectCurrentAnomalyTypes(rowCurrentValues, config))
                .Concat(DetectActivePowerAnomalyTypes(rowActivePowerValues, rowCurrentValues, config))
                .Concat(DetectPowerFactorAnomalyTypes(rowPowerFactorValues, rowCurrentValues, config))
                .Concat(DetectTemperatureAnomalyTypes(rowTemperatureValues, config))
                .ToList();
            rawRowLabels?.Add(rowTypes.ToList());
            var reportValues = BuildReportValues(
                rowVoltageValues,
                rowCurrentValues,
                rowActivePowerValues,
                rowPowerFactorValues,
                rowTemperatureValues,
                BuildRowNumericValues(cells, reactivePowerColumnGroups));
            rowAnalyses.Add(new NativeRowAnalysis(rowTime, rowTypes, rowFreezeCoreValues, rowFreezeAuxValues, reportValues));
        }

        var freezeRowTypes = DetectFreezeRowTypes(rowAnalyses, config);
        for (var rowIndex = 0; rowIndex < rowAnalyses.Count; rowIndex++)
        {
            var rowTypes = rowAnalyses[rowIndex]
                .AnomalyTypes
                .Concat(freezeRowTypes[rowIndex])
                .ToList();
            if (rowTypes.Count == 0 || rowTypes.All(IsFreezeOnlyType))
            {
                continue;
            }

            var reportableRowTypes = rowTypes
                .OrderBy(IssueRuleOrder)
                .ToList();
            reportableRowLabels?.Add(reportableRowTypes);
            anomalyTypeDetails.Add(BuildStructuredIssueDetail(reportableRowTypes));

            if (!config.DetailOutputEnabled)
            {
                var compactType = BuildCompactIssueType(reportableRowTypes);
                detailPreview.Add(BuildDetailPreview(
                    fileName,
                    relativePath,
                    location,
                    compactType,
                    BuildCompactIssueDetail(reportableRowTypes),
                    SeverityForIssueType(compactType),
                    BuildCompactIssueValue(reportableRowTypes, rowAnalyses[rowIndex]),
                    rowAnalyses[rowIndex].Time,
                    rowAnalyses[rowIndex].ReportValues));
                anomalyRows++;
                continue;
            }

            var detailTypes = BuildDetailPreviewIssueTypes(reportableRowTypes, rowAnalyses[rowIndex], config);

            foreach (var type in detailTypes)
            {
                detailPreview.Add(BuildDetailPreview(
                    fileName,
                    relativePath,
                    location,
                    type,
                    BuildRowIssueDetail(type, rowAnalyses[rowIndex], config),
                    SeverityForIssueType(type),
                    BuildIssueValue(type, rowAnalyses[rowIndex], config),
                    rowAnalyses[rowIndex].Time,
                    rowAnalyses[rowIndex].ReportValues));
            }

            anomalyRows++;
        }

        var offlineColumns = offlineInvalidCounts
            .Where(static pair => pair.Value > 0)
            .Select(static pair => pair.Key)
            .OrderBy(static column => Array.IndexOf(ReportValueColumns, column))
            .ThenBy(static column => column, StringComparer.Ordinal)
            .ToList();
        var offlineRatio = dataRows > 0 && keyColumnGroups.Count > 0
            ? (double)offlineInvalidCounts.Values.Sum() / (dataRows * keyColumnGroups.Count)
            : 0.0;
        var isOffline = offlineRatio > 0.5;
        var sensorFaults = sensorStates
            .Where(static pair => IsTemperatureColumn(pair.Key) && pair.Value.FaultCount > 0)
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new NativeSensorFault(pair.Key, pair.Value.FaultCount))
            .ToList();
        var sensorMissing = sensorStates
            .Where(static pair => pair.Value.HasObservedCell && !pair.Value.HasNonZeroValidValue)
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        var sensorStatus = new NativeSensorStatusRow(
            fileName,
            location.Building,
            relativePath,
            location.Transformer,
            isOffline,
            sensorFaults.Select(static fault => fault.Name).ToList(),
            sensorMissing,
            isOffline ? "离线" : sensorFaults.Count > 0 || sensorMissing.Count > 0 ? "异常" : "正常",
            BuildSensorStatusReason(isOffline, sensorFaults, sensorMissing));

        if (isOffline)
        {
            var offlineDetail = offlineColumns.Count > 0
                ? string.Join(",", offlineColumns)
                : "所有电气参数";
            var offlineIssueType = $"设备离线({offlineDetail})";
            detailPreview.Add(BuildDetailPreview(
                fileName,
                relativePath,
                location,
                offlineIssueType,
                offlineIssueType,
                "高",
                "",
                ExtractDateFromFileName(fileName)));
            foreach (var fault in sensorFaults)
            {
                var sensorFaultIssueType = BuildSensorFaultIssueType(fault);
                detailPreview.Add(BuildDetailPreview(
                    fileName,
                    relativePath,
                    location,
                    sensorFaultIssueType,
                    sensorFaultIssueType,
                    "中",
                    "",
                    ExtractDateFromFileName(fileName)));
            }

            return NativeFileAnalysis.Anomaly(
                fileName,
                1 + sensorFaults.Count,
                "设备离线",
                sensorMissing,
                sensorStatus,
                detailPreview,
                BuildParityTrace(
                    parityTraceKey,
                    dataRows,
                    rawRowLabels,
                    reportableRowLabels,
                    anomalyTypeDetails,
                    "设备离线"));
        }

        if (sensorFaults.Count > 0)
        {
            anomalyRows += sensorFaults.Count;
        }

        foreach (var fault in sensorFaults)
        {
            var sensorFaultIssueType = BuildSensorFaultIssueType(fault);
            detailPreview.Add(BuildDetailPreview(
                fileName,
                relativePath,
                location,
                sensorFaultIssueType,
                sensorFaultIssueType,
                "中",
                "",
                ExtractDateFromFileName(fileName)));
        }

        var anomalyTypes = BuildFileAnomalyTypes(anomalyTypeDetails);
        if (sensorFaults.Count > 0)
        {
            var sensorTypes = string.Join("; ", sensorFaults.Select(BuildSensorFaultSummaryType));
            anomalyTypes = string.IsNullOrWhiteSpace(anomalyTypes)
                ? sensorTypes
                : $"{anomalyTypes}; {sensorTypes}";
        }

        var parityTrace = BuildParityTrace(
            parityTraceKey,
            dataRows,
            rawRowLabels,
            reportableRowLabels,
            anomalyTypeDetails,
            anomalyTypes);

        return anomalyRows > 0
            ? NativeFileAnalysis.Anomaly(
                fileName,
                anomalyRows,
                anomalyTypes,
                sensorMissing,
                sensorStatus,
                detailPreview,
                parityTrace)
            : NativeFileAnalysis.Normal(fileName, sensorMissing, sensorStatus, parityTrace);
    }

    private static void AddDetailPreview(
        List<ReportDetailPreview> target,
        IReadOnlyList<ReportDetailPreview> details,
        string relativePath)
    {
        foreach (var detail in details)
        {
            detail.RelativePath ??= relativePath;
            target.Add(detail);
        }
    }

    private static Dictionary<string, double> BuildRowVoltageValues(
        IReadOnlyList<string> cells,
        IReadOnlyList<NativeColumnGroup> voltageColumnGroups)
    {
        return BuildRowNumericValues(cells, voltageColumnGroups);
    }

    private static Dictionary<string, double> BuildRowNumericValues(
        IReadOnlyList<string> cells,
        IReadOnlyList<NativeColumnGroup> columnGroups)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in BuildRowMergedNumericValues(cells, columnGroups))
        {
            if (!IsInvalidValue(value))
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static IReadOnlyList<string> BuildDetailPreviewIssueTypes(
        IReadOnlyList<string> rowTypes,
        NativeRowAnalysis row,
        NativeDetectionConfig config)
    {
        if (!config.DetailOutputEnabled)
        {
            return rowTypes;
        }

        var detailTypes = new List<string>();
        var detailedPtTypes = rowTypes
            .Where(static type => TryGetDetailedPtIssue(type, out _, out _))
            .ToList();
        if (detailedPtTypes.Count > 0)
        {
            detailTypes.Add(string.Join(
                "; ",
                detailedPtTypes
                    .Select(static type => TryGetDetailedPtIssue(type, out var compactType, out _) ? compactType : type)
                    .Concat(rowTypes.Where(static type => TryGetDetailedVoltageIssue(type, out _, out _, out _)))
                    .Distinct(StringComparer.Ordinal)));
            detailTypes.AddRange(rowTypes.Where(static type =>
                !TryGetDetailedPtIssue(type, out _, out _)
                && !TryGetDetailedVoltageIssue(type, out _, out _, out _)
                && !TryGetDetailedImbalanceIssue(type, out _, out _)));
            return detailTypes.Distinct(StringComparer.Ordinal).ToList();
        }

        foreach (var detailType in new[] { "电压过低", "电压过高" })
        {
            var phaseTypes = VoltagePhaseColumns()
                .Select(phaseColumn => $"{phaseColumn.Phase}{detailType}")
                .Where(rowTypes.Contains)
                .ToList();
            if (phaseTypes.Count == 0)
            {
                continue;
            }

            detailTypes.Add(CompactDetailedVoltageIssueType(detailType, phaseTypes, row));
        }

        detailTypes.AddRange(rowTypes
            .Where(static type => !TryGetDetailedVoltageIssue(type, out _, out _, out _))
            .Select(static type => TryGetDetailedImbalanceIssue(type, out var compactType, out _) ? compactType : type));
        return detailTypes.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string BuildCompactIssueType(IReadOnlyList<string> issueTypes)
    {
        var groups = GroupIssueTypes(issueTypes);
        var result = new List<string>();
        foreach (var group in groups.OrderByDescending(static group => group.Value.Count))
        {
            var root = group.Key;
            var items = group.Value;
            if (items.Count == 1)
            {
                result.Add(CleanIssueType(items[0]));
                continue;
            }

            var phases = items
                .Select(GetIssuePhase)
                .Where(static phase => !string.IsNullOrWhiteSpace(phase))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            result.Add(phases.Count > 0 ? $"{root}({string.Join("/", phases)}相)" : root);
        }

        return result.Count <= 3
            ? string.Join("; ", result)
            : string.Join("; ", result.Take(3).Append($"+{result.Count - 3}类"));
    }

    private static string BuildCompactIssueDetail(IReadOnlyList<string> issueTypes)
    {
        var result = new List<string>();
        foreach (var group in GroupIssueTypes(issueTypes))
        {
            var items = group.Value;
            var parts = items.Select(item =>
            {
                var phase = GetIssuePhase(item);
                var parenthesis = Regex.Match(item, "\\(([^)]*)\\)");
                if (!string.IsNullOrWhiteSpace(phase) && parenthesis.Success)
                {
                    return $"{phase}{parenthesis.Groups[1].Value}";
                }

                if (parenthesis.Success)
                {
                    return parenthesis.Groups[1].Value;
                }

                return CleanIssueType(item);
            }).ToList();

            result.Add(items.Count == 1 && string.IsNullOrWhiteSpace(GetIssuePhase(parts[0]))
                ? parts[0]
                : $"{group.Key}: {string.Join(", ", parts)}");
        }

        return string.Join(" | ", result);
    }

    private static string BuildCompactIssueValue(IReadOnlyList<string> issueTypes, NativeRowAnalysis row) =>
        string.Join(
            ", ",
            ReportValueColumns
                .Where(column => row.ReportValues.ContainsKey(column)
                    && issueTypes.Any(type => type.Contains(column, StringComparison.Ordinal)
                        || (PhaseMap.TryGetValue(column, out var phase) && type.Contains(phase, StringComparison.Ordinal))))
                .Select(column => $"{column}={row.ReportValues[column].ToString("F1", CultureInfo.InvariantCulture)}")
                .Take(8));

    // These two helpers preserve the established structured-detail and
    // summary sequence. It is tempting
    // to summarize the already-compacted type labels, which loses the original
    // long-standing behaviour for a row containing multiple issue families:
    // its structured detail uses " | ", which remains one summary token.
    private static string BuildStructuredIssueDetail(IEnumerable<string> issueTypes)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var issueType in issueTypes)
        {
            var root = Regex.Replace(CleanIssueType(issueType), "[ABC]相", "").Trim();
            if (!groups.TryGetValue(root, out var items))
            {
                items = [];
                groups.Add(root, items);
            }

            items.Add(issueType);
        }

        var result = new List<string>();
        foreach (var (root, items) in groups)
        {
            var parts = new List<string>();
            foreach (var item in items)
            {
                var phase = GetIssuePhase(item);
                var parenthesis = Regex.Match(item, "\\(([^)]*)\\)");
                if (!string.IsNullOrWhiteSpace(phase) && parenthesis.Success)
                {
                    parts.Add($"{phase}相{parenthesis.Groups[1].Value}");
                }
                else if (parenthesis.Success)
                {
                    parts.Add(parenthesis.Groups[1].Value);
                }
                else
                {
                    parts.Add(CleanIssueType(item));
                }
            }

            result.Add(parts.Count == 1 && !Regex.IsMatch(parts[0], "[ABC]相")
                ? parts[0]
                : $"{root}: {string.Join(", ", parts)}");
        }

        return string.Join(" | ", result);
    }

    private static string BuildFileAnomalyTypes(IEnumerable<string> structuredDetails)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var detail in structuredDetails)
        {
            // The summary pipeline splits on semicolons only: a structured
            // "A | B" detail deliberately remains one summary token.
            foreach (var part in detail.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = CleanIssueType(part);
                var root = Regex.Replace(clean, "[ABC]相", "").Trim();
                if (!groups.TryGetValue(root, out var items))
                {
                    items = [];
                    groups.Add(root, items);
                }

                items.Add(part);
            }
        }

        var result = new List<string>();
        foreach (var (root, items) in groups.OrderByDescending(static pair => pair.Value.Count))
        {
            if (items.Count == 1)
            {
                result.Add(CleanIssueType(items[0]));
                continue;
            }

            var phases = items
                .Select(GetIssuePhase)
                .Where(static phase => !string.IsNullOrWhiteSpace(phase))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            result.Add(phases.Count > 0 ? $"{root}({string.Join("/", phases)}相)" : root);
        }

        return result.Count <= 3
            ? string.Join("; ", result)
            : string.Join("; ", result.Take(3).Append($"+{result.Count - 3}类"));
    }

    // A value (rather than merely "1") is used as the HMAC key so the hashes
    // cannot be dictionary-reversed from the small, known issue-label
    // vocabulary.  Operators comparing separate backends must supply the same
    // locally held random key to both processes.
    private static string? GetParityTraceKey()
    {
        var value = Environment.GetEnvironmentVariable("EDETECTION_NATIVE_PARITY_TRACE");
        return string.IsNullOrWhiteSpace(value)
               || value.Equals("0", StringComparison.Ordinal)
               || value.Equals("false", StringComparison.OrdinalIgnoreCase)
               || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static NativeParityTrace BuildUnavailableParityTrace(string key) =>
        new()
        {
            AnalysisState = "not_analyzed",
            FinalAnomalyTypesHash = ComputeParityTraceHash(key, "not-analyzed"),
        };

    private static NativeParityTrace? BuildParityTrace(
        string? key,
        int dataRowCount,
        IReadOnlyList<IReadOnlyList<string>>? rawRowLabels,
        IReadOnlyList<IReadOnlyList<string>>? reportableRowLabels,
        IReadOnlyList<string> structuredDetails,
        string anomalyTypes)
    {
        if (key is null)
        {
            return null;
        }

        var rawRows = rawRowLabels ?? [];
        var reportableRows = reportableRowLabels ?? [];
        return new NativeParityTrace
        {
            DataRowCount = dataRowCount,
            RawRowLabelCount = rawRows.Sum(static labels => labels.Count),
            ReportableRowLabelCount = reportableRows.Sum(static labels => labels.Count),
            StructuredDetailCount = structuredDetails.Count,
            RawRowLabelHashes = HashParityTraceRows(key, "raw", rawRows),
            ReportableRowLabelHashes = HashParityTraceRows(key, "reportable", reportableRows),
            StructuredDetailHashes = structuredDetails
                .Select((detail, index) => ComputeParityTraceHash(key, $"detail\u001f{index}\u001f{detail}"))
                .ToList(),
            FinalAnomalyTypesHash = ComputeParityTraceHash(key, $"summary\u001f{anomalyTypes}"),
        };
    }

    private static List<string> HashParityTraceRows(
        string key,
        string stage,
        IReadOnlyList<IReadOnlyList<string>> rows) =>
        rows.Select((labels, index) => ComputeParityTraceHash(
                key,
                $"{stage}\u001f{index}\u001f{string.Join("\u001e", labels)}"))
            .ToList();

    private static string ComputeParityTraceHash(string key, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(keyBytes, payloadBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }

    private static Dictionary<string, List<string>> GroupIssueTypes(IEnumerable<string> issueTypes)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var issueType in issueTypes)
        {
            var root = Regex.Replace(CleanIssueType(issueType), "[ABC]相", "");
            if (!groups.TryGetValue(root, out var items))
            {
                items = [];
                groups.Add(root, items);
            }

            items.Add(issueType);
        }

        return groups;
    }

    private static string CleanIssueType(string issueType)
    {
        var clean = Regex.Replace(issueType, "\\([^)]*\\)", "").Trim();
        var colon = clean.IndexOf(':');
        return colon >= 0 ? clean[..colon].Trim() : clean;
    }

    private static string GetIssuePhase(string issueType) =>
        issueType.StartsWith("A相", StringComparison.Ordinal) ? "A"
        : issueType.StartsWith("B相", StringComparison.Ordinal) ? "B"
        : issueType.StartsWith("C相", StringComparison.Ordinal) ? "C"
        : "";

    private static int IssueRuleOrder(string issueType) =>
        issueType.Contains("电压", StringComparison.Ordinal) || issueType.Contains("PT接线", StringComparison.Ordinal) ? 0
        : issueType.Contains("电流过大", StringComparison.Ordinal) ? 1
        : issueType.Contains("电流不平衡", StringComparison.Ordinal) ? 2
        : IsFreezeOnlyType(issueType) ? 3
        : issueType.Contains("CT", StringComparison.Ordinal) || issueType.Contains("有功功率", StringComparison.Ordinal) ? 4
        : issueType.Contains("功率因数", StringComparison.Ordinal) ? 5
        : issueType.Contains("温度", StringComparison.Ordinal) ? 6
        : 7;

    private static Dictionary<string, double> BuildRowMergedNumericValues(
        IReadOnlyList<string> cells,
        IReadOnlyList<NativeColumnGroup> columnGroups)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in columnGroups)
        {
            var numericValues = new List<double>();
            foreach (var index in group.Indexes)
            {
                if (index < cells.Count && TryParseDouble(cells[index], out var parsed))
                {
                    numericValues.Add(parsed);
                }
            }

            if (numericValues.Count > 0)
            {
                values[group.Name] = numericValues.Average();
            }
        }

        return values;
    }

    private static Dictionary<string, double> BuildReportValues(
        params IReadOnlyDictionary<string, double>[] valueSets)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var valueSet in valueSets)
        {
            foreach (var (key, value) in valueSet)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string BuildRowIssueDetail(string issueType, NativeRowAnalysis row, NativeDetectionConfig config) =>
        config.DetailOutputEnabled && issueType.Contains("电压疑似PT接线异常", StringComparison.Ordinal)
            ? BuildDetailedPtIssueDetail(issueType, row)
            : config.DetailOutputEnabled && TryGetDetailedImbalanceIssue(issueType, out _, out _)
            ? BuildDetailedImbalanceIssueDetail(issueType)
            : config.DetailOutputEnabled && TryGetCompactDetailedVoltageIssue(issueType, out var compactDetailType)
            ? BuildCompactVoltageIssueDetail(issueType, compactDetailType, row)
            : config.DetailOutputEnabled && TryGetDetailedVoltageIssue(issueType, out var voltageColumn, out var phase, out var detailType)
            && row.ReportValues.TryGetValue(voltageColumn, out var voltageValue)
            ? $"{detailType}: {phase}{voltageValue.ToString("F1", CultureInfo.InvariantCulture)}V"
            : issueType;

    private static string BuildIssueValue(string issueType, NativeRowAnalysis row, NativeDetectionConfig config)
    {
        if (config.DetailOutputEnabled
            && issueType.Contains("PT接线异常", StringComparison.Ordinal))
        {
            return "";
        }

        if (config.DetailOutputEnabled
            && TryBuildDetailedImbalanceIssueValue(issueType, row, out var imbalanceValue))
        {
            return imbalanceValue;
        }

        if (!config.DetailOutputEnabled
            || TryGetDetailedVoltageIssue(issueType, out _, out _, out _)
            || TryGetCompactDetailedVoltageIssue(issueType, out _))
        {
            return "";
        }

        return string.Join(
            ", ",
            ReportValueColumns
                .Where(column => row.ReportValues.ContainsKey(column)
                    && (issueType.Contains(column, StringComparison.Ordinal)
                        || (PhaseMap.TryGetValue(column, out var phase)
                            && issueType.Contains(phase, StringComparison.Ordinal))))
                .Select(column => $"{column}={row.ReportValues[column].ToString("F1", CultureInfo.InvariantCulture)}"));
    }

    private static string SummaryIssueType(string issueType) =>
        issueType.Contains("电压疑似PT接线异常", StringComparison.Ordinal)
            ? "电压疑似PT接线异常"
            : TryGetDetailedImbalanceIssue(issueType, out _, out _)
                ? "电压电压不平衡"
            : TryGetCompactDetailedVoltageIssue(issueType, out var compactDetailType)
                ? compactDetailType
                : TryGetDetailedVoltageIssue(issueType, out _, out _, out var detailType)
                    ? detailType
                    : issueType;

    private static string CompactDetailedVoltageIssueType(
        string detailType,
        IReadOnlyList<string> phaseTypes,
        NativeRowAnalysis row)
    {
        if (phaseTypes.Count == 1)
        {
            return phaseTypes[0];
        }

        var phases = VoltagePhaseColumns()
            .Where(phaseColumn => phaseTypes.Contains($"{phaseColumn.Phase}{detailType}")
                                  && row.ReportValues.ContainsKey(phaseColumn.Column))
            .Select(static phaseColumn => phaseColumn.Phase[0].ToString())
            .ToList();
        return phases.Count > 0
            ? $"{detailType}({string.Join("/", phases)}相)"
            : detailType;
    }

    private static bool TryGetCompactDetailedVoltageIssue(string issueType, out string detailType)
    {
        if (issueType.StartsWith("电压过低(", StringComparison.Ordinal)
            || issueType == "电压过低")
        {
            detailType = "电压过低";
            return true;
        }

        if (issueType.StartsWith("电压过高(", StringComparison.Ordinal)
            || issueType == "电压过高")
        {
            detailType = "电压过高";
            return true;
        }

        detailType = "";
        return false;
    }

    private static string BuildCompactVoltageIssueDetail(string issueType, string detailType, NativeRowAnalysis row)
    {
        var phases = CompactDetailedVoltageIssuePhases(issueType, detailType)
            .ToHashSet(StringComparer.Ordinal);
        var values = VoltagePhaseColumns()
            .Where(phaseColumn => phases.Contains(phaseColumn.Phase)
                                  && row.ReportValues.ContainsKey(phaseColumn.Column))
            .Select(phaseColumn =>
                $"{phaseColumn.Phase}{row.ReportValues[phaseColumn.Column].ToString("F1", CultureInfo.InvariantCulture)}V")
            .ToList();
        return values.Count > 0
            ? $"{detailType}: {string.Join(", ", values)}"
            : detailType;
    }

    private static string BuildDetailedPtIssueDetail(string issueType, NativeRowAnalysis row)
    {
        var details = new List<string>();
        var ptValues = SplitIssueTypes(issueType)
            .Select(type =>
            {
                if (!TryGetDetailedPtIssue(type, out var compactType, out var detailValue))
                {
                    return "";
                }

                return string.IsNullOrWhiteSpace(detailValue)
                    ? BuildCompactPtDetailValue(compactType, row)
                    : detailValue;
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (ptValues.Count > 0)
        {
            details.Add($"电压疑似PT接线异常: {string.Join(", ", ptValues)}");
        }

        foreach (var detailType in new[] { "电压过低", "电压过高" })
        {
            var phaseValues = SplitIssueTypes(issueType)
                .Where(type => TryGetDetailedVoltageIssue(type, out _, out _, out var voltageDetailType)
                               && voltageDetailType == detailType)
                .Select(type => TryGetDetailedVoltageIssue(type, out var voltageColumn, out var phase, out _)
                                && row.ReportValues.TryGetValue(voltageColumn, out var voltageValue)
                    ? $"{phase}{voltageValue.ToString("F1", CultureInfo.InvariantCulture)}V"
                    : "")
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (phaseValues.Count > 0)
            {
                details.Add($"{detailType}: {string.Join(", ", phaseValues)}");
            }
        }

        return details.Count > 0 ? string.Join(" | ", details) : issueType;
    }

    private static string BuildCompactPtDetailValue(string compactType, NativeRowAnalysis row)
    {
        foreach (var (phase, column) in VoltagePhaseColumns())
        {
            if (compactType != $"{phase}电压疑似PT接线异常"
                || !row.ReportValues.TryGetValue(column, out var value))
            {
                continue;
            }

            var voltageValues = VoltagePhaseColumns()
                .Where(phaseColumn => row.ReportValues.ContainsKey(phaseColumn.Column))
                .Select(phaseColumn => row.ReportValues[phaseColumn.Column])
                .ToList();
            var median = Median(voltageValues);
            if (median == 0)
            {
                return "";
            }

            var deviation = Math.Abs(value - median) / median * 100.0;
            return $"{phase}偏差{deviation.ToString("F1", CultureInfo.InvariantCulture)}%";
        }

        return "";
    }

    private static string BuildDetailedImbalanceIssueDetail(string issueType)
    {
        var values = SplitIssueTypes(issueType)
            .Select(static type => TryGetDetailedImbalanceIssue(type, out var compactType, out _) ? compactType : "")
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return values.Count > 0
            ? $"电压电压不平衡: {string.Join(", ", values)}"
            : issueType;
    }

    private static bool TryBuildDetailedImbalanceIssueValue(
        string issueType,
        NativeRowAnalysis row,
        out string issueValue)
    {
        var values = SplitIssueTypes(issueType)
            .Select(type =>
            {
                if (!TryGetDetailedImbalanceIssue(type, out _, out var voltageColumn)
                    || !row.ReportValues.TryGetValue(voltageColumn, out var value))
                {
                    return "";
                }

                return $"{voltageColumn}={value.ToString("F1", CultureInfo.InvariantCulture)}";
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        issueValue = string.Join(", ", values);
        return values.Count > 0;
    }

    private static IReadOnlyList<string> CompactDetailedVoltageIssuePhases(string issueType, string detailType)
    {
        if (issueType == detailType)
        {
            return VoltagePhaseColumns()
                .Select(static phaseColumn => phaseColumn.Phase)
                .ToList();
        }

        if (!issueType.StartsWith($"{detailType}(", StringComparison.Ordinal)
            || !issueType.EndsWith("相)", StringComparison.Ordinal))
        {
            return [];
        }

        var phaseText = issueType.Substring(detailType.Length + 1, issueType.Length - detailType.Length - 3);
        var phaseSet = phaseText
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static phase => $"{phase}相")
            .ToHashSet(StringComparer.Ordinal);
        return VoltagePhaseColumns()
            .Where(phaseColumn => phaseSet.Contains(phaseColumn.Phase))
            .Select(static phaseColumn => phaseColumn.Phase)
            .ToList();
    }

    private static bool TryGetDetailedPtIssue(string issueType, out string compactType, out string detailValue)
    {
        foreach (var (phase, _) in VoltagePhaseColumns())
        {
            var prefix = $"{phase}电压疑似PT接线异常(偏差";
            if (issueType.StartsWith(prefix, StringComparison.Ordinal)
                && issueType.EndsWith("%)", StringComparison.Ordinal))
            {
                var percent = issueType.Substring(prefix.Length, issueType.Length - prefix.Length - 2);
                compactType = $"{phase}电压疑似PT接线异常";
                detailValue = $"{phase}偏差{percent}%";
                return true;
            }

            if (issueType == $"{phase}电压疑似PT接线异常")
            {
                compactType = issueType;
                detailValue = "";
                return true;
            }
        }

        compactType = "";
        detailValue = "";
        return false;
    }

    private static bool TryGetDetailedImbalanceIssue(string issueType, out string compactType, out string voltageColumn)
    {
        foreach (var (phase, column) in VoltagePhaseColumns())
        {
            var compact = $"{phase}电压电压不平衡";
            if (issueType == compact || issueType.StartsWith($"{compact}:", StringComparison.Ordinal))
            {
                compactType = compact;
                voltageColumn = column;
                return true;
            }
        }

        compactType = "";
        voltageColumn = "";
        return false;
    }

    private static bool TryGetDetailedVoltageIssue(
        string issueType,
        out string voltageColumn,
        out string phase,
        out string detailType)
    {
        foreach (var (prefix, column) in VoltagePhaseColumns())
        {
            if (issueType == $"{prefix}电压过低")
            {
                voltageColumn = column;
                phase = prefix;
                detailType = "电压过低";
                return true;
            }

            if (issueType == $"{prefix}电压过高")
            {
                voltageColumn = column;
                phase = prefix;
                detailType = "电压过高";
                return true;
            }
        }

        voltageColumn = "";
        phase = "";
        detailType = "";
        return false;
    }

    private static IReadOnlyList<(string Phase, string Column)> VoltagePhaseColumns() =>
    [
        ("A相", "Uab"),
        ("B相", "Ubc"),
        ("C相", "Uca"),
    ];

    private static string PhaseForVoltageColumn(string column) =>
        VoltagePhaseColumns()
            .FirstOrDefault(phaseColumn => phaseColumn.Column == column)
            .Phase ?? column;

    private static string PhaseForCurrentColumn(string column) =>
        column switch
        {
            "Ia" => "A相",
            "Ib" => "B相",
            "Ic" => "C相",
            _ => column,
        };

    private static IReadOnlyList<string> DetectVoltageAnomalyTypes(
        Dictionary<string, double> values,
        int voltageColumnCount,
        NativeDetectionConfig config)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var types = new List<string>();
        // A row with a missing phase is not an all-off row. The data model
        // comparison yields False for NaN, so only rows where every discovered
        // voltage column has a numeric value may suppress voltage anomalies.
        var allOff = voltageColumnCount >= 2
            && values.Count == voltageColumnCount
            && values.Values.All(static value => value < 1.0);
        var mean = values.Values.Average();
        var active = mean > 30.0;
        var hasPtAnomaly = false;

        if (values.Count == 3 && active && !allOff)
        {
            var median = Median(values.Values);
            if (median != 0)
            {
                foreach (var (name, value) in values)
                {
                    var deviation = Math.Abs(value - median) / median;
                    var otherDeviation = values
                        .Where(pair => pair.Key != name)
                        .Select(pair => Math.Abs(pair.Value - median) / median)
                        .DefaultIfEmpty(0)
                        .Max();
                    if (deviation > 0.30 && otherDeviation < 0.15)
                    {
                        hasPtAnomaly = true;
                        if (config.DetailOutputEnabled)
                        {
                            var phase = PhaseForVoltageColumn(name);
                            types.Add(
                                $"{phase}电压疑似PT接线异常(偏差{(deviation * 100.0).ToString("F1", CultureInfo.InvariantCulture)}%)");
                        }
                        else
                        {
                            types.Add("疑似PT接线异常");
                        }

                        break;
                    }
                }
            }
        }

        if (values.Count >= 2 && active && !allOff && !hasPtAnomaly && mean != 0)
        {
            var imbalancedValues = values
                .Select(pair => new
                {
                    pair.Key,
                    pair.Value,
                    Deviation = Math.Abs(pair.Value - mean) / mean,
                })
                .Where(pair => pair.Deviation > config.VoltageImbalanceThreshold)
                .ToList();
            if (config.DetailOutputEnabled)
            {
                foreach (var pair in imbalancedValues)
                {
                    var phase = PhaseForVoltageColumn(pair.Key);
                    types.Add(
                        $"{phase}电压电压不平衡:{pair.Value.ToString("F1", CultureInfo.InvariantCulture)}:偏差{pair.Deviation.ToString("F3", CultureInfo.InvariantCulture)}");
                }
            }
            else if (imbalancedValues.Count > 0)
            {
                types.Add("相电压不平衡");
            }
        }

        if (!allOff)
        {
            if (config.DetailOutputEnabled)
            {
                foreach (var (phase, column) in VoltagePhaseColumns())
                {
                    if (!values.TryGetValue(column, out var value))
                    {
                        continue;
                    }

                    if (value < config.VoltageMinThreshold)
                    {
                        types.Add($"{phase}电压过低");
                    }
                    else if (value > config.VoltageMaxThreshold)
                    {
                        types.Add($"{phase}电压过高");
                    }
                }
            }
            else
            {
                foreach (var value in values.Values.Where(value => value < config.VoltageMinThreshold || value > config.VoltageMaxThreshold))
                {
                    types.Add("电压异常");
                }
            }
        }

        return types;
    }

    private static IReadOnlyList<string> DetectCurrentAnomalyTypes(
        Dictionary<string, double> values,
        NativeDetectionConfig config)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var types = new List<string>();
        var active = values.Values.Any(value => value >= config.CurrentActiveMinThreshold);
        if (!active)
        {
            return types;
        }

        if (config.CurrentOverloadEnabled
            && values.Values.Any(value => value > config.CurrentMaxThreshold))
        {
            if (config.DetailOutputEnabled)
            {
                types.AddRange(
                    values
                        .Where(pair => pair.Value > config.CurrentMaxThreshold)
                        .Select(pair => $"{PhaseForCurrentColumn(pair.Key)}电流过大"));
            }
            else
            {
                // The overload rule appends its generic label once
                // for every overloaded phase even when detail_output is off.
                // Preserve that row-level multiplicity for the later
                // structured-detail and file-summary stages.
                types.AddRange(values
                    .Where(pair => pair.Value > config.CurrentMaxThreshold)
                    .Select(static _ => "电流过大"));
            }
        }

        if (config.CurrentUnbalanceEnabled && values.Count >= 2)
        {
            var mean = values.Values.Average();
            if (mean != 0)
            {
                var unbalance = (values.Values.Max() - values.Values.Min()) / mean;
                if (unbalance > config.CurrentUnbalanceMaxThreshold)
                {
                    types.Add("电流不平衡");
                }
            }
        }

        return types;
    }

    private static IReadOnlyList<string> DetectActivePowerAnomalyTypes(
        Dictionary<string, double> activePowerValues,
        Dictionary<string, double> currentValues,
        NativeDetectionConfig config)
    {
        if (!activePowerValues.TryGetValue("有功功率", out var activePower)
            || !IsCurrentActive(currentValues, config))
        {
            return [];
        }

        if (activePower < 0)
        {
            return ["CT极性异常"];
        }

        return activePower < config.ActivePowerMinThreshold
            ? ["有功功率异常"]
            : [];
    }

    private static IReadOnlyList<string> DetectPowerFactorAnomalyTypes(
        Dictionary<string, double> powerFactorValues,
        Dictionary<string, double> currentValues,
        NativeDetectionConfig config)
    {
        if (!config.PowerFactorEnabled
            || !powerFactorValues.TryGetValue("功率因数", out var powerFactor)
            || !IsCurrentActive(currentValues, config))
        {
            return [];
        }

        return powerFactor < config.PowerFactorMinThreshold
            ? ["功率因数过低"]
            : [];
    }

    private static bool IsCurrentActive(Dictionary<string, double> currentValues, NativeDetectionConfig config) =>
        currentValues.Values.Any(value => value >= config.CurrentActiveMinThreshold);

    private static IReadOnlyList<string> DetectTemperatureAnomalyTypes(
        Dictionary<string, double> temperatureValues,
        NativeDetectionConfig config)
    {
        if (temperatureValues.Count == 0)
        {
            return [];
        }

        if (!temperatureValues.Values.Any(value =>
                value < config.TemperatureMinThreshold || value > config.TemperatureMaxThreshold))
        {
            return [];
        }

        if (!config.DetailOutputEnabled)
        {
            // TemperatureRule likewise appends the generic label for each
            // out-of-range phase before the pipeline compacts it.
            return temperatureValues
                .Where(pair => pair.Value < config.TemperatureMinThreshold
                               || pair.Value > config.TemperatureMaxThreshold)
                .Select(static _ => "温度异常")
                .ToList();
        }

        return temperatureValues
            .Where(pair => pair.Value < config.TemperatureMinThreshold
                           || pair.Value > config.TemperatureMaxThreshold)
            .Select(pair =>
                $"{pair.Key}{(pair.Value < config.TemperatureMinThreshold ? "过低" : "过高")}")
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> DetectFreezeRowTypes(
        IReadOnlyList<NativeRowAnalysis> rows,
        NativeDetectionConfig config)
    {
        var result = rows
            .Select(static _ => new List<string>())
            .ToList();
        if (rows.Count == 0)
        {
            return result;
        }

        foreach (var column in new[] { "无功功率", "功率因数" })
        {
            var values = rows.Select(row => GetOptionalValue(row.FreezeAuxValues, column)).ToList();
            if (values.All(static value => Math.Abs(value ?? 0) < 0.000_001))
            {
                continue;
            }

            var frozen = DetectFlatRun(values, config.FreezeCountThreshold, config.FreezeStdThreshold);
            for (var index = 0; index < frozen.Count; index++)
            {
                if (frozen[index])
                {
                    result[index].Add("数据恒定");
                }
            }
        }

        var coreMasks = new List<IReadOnlyList<bool>>();
        foreach (var column in new[] { "Uab", "Ubc", "Uca", "Ia", "Ib", "Ic", "有功功率" })
        {
            var values = rows.Select(row => GetOptionalValue(row.FreezeCoreValues, column)).ToList();
            if (values.All(static value => Math.Abs(value ?? 0) < 0.000_001))
            {
                continue;
            }

            coreMasks.Add(DetectFlatRun(values, config.FreezeCountThreshold, config.FreezeStdThreshold));
        }

        if (coreMasks.Count == 0)
        {
            return result;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var frozenCount = coreMasks.Count(mask => mask[rowIndex]);
            if (frozenCount >= 2 && !IsStandbyRow(rows[rowIndex], config))
            {
                result[rowIndex].Add("数据冻结");
            }
        }

        return result;
    }

    private static IReadOnlyList<bool> DetectFlatRun(
        IReadOnlyList<double?> values,
        int countNeed,
        double stdLimit)
    {
        var flat = new bool[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            if (index == 0)
            {
                flat[index] = true;
                continue;
            }

            // pandas evaluates a difference involving NaN as NaN, then
            // FreezeRule applies fillna(0) before the threshold comparison.
            // Therefore a missing current or previous sample is flat, rather
            // than being compared against a synthetic numeric zero.
            if (!values[index].HasValue || !values[index - 1].HasValue)
            {
                flat[index] = true;
                continue;
            }

            flat[index] = Math.Abs(values[index]!.Value - values[index - 1]!.Value) < stdLimit;
        }

        var frozen = new bool[values.Count];
        if (countNeed <= 0)
        {
            countNeed = 1;
        }

        var flatCount = 0;
        for (var index = 0; index < flat.Length; index++)
        {
            if (flat[index])
            {
                flatCount++;
            }

            if (index >= countNeed && flat[index - countNeed])
            {
                flatCount--;
            }

            frozen[index] = index + 1 >= countNeed && flatCount >= countNeed;
        }

        return frozen;
    }

    private static bool IsStandbyRow(NativeRowAnalysis row, NativeDetectionConfig config)
    {
        var currentColumns = new[] { "Ia", "Ib", "Ic" }
            .Where(column => row.FreezeCoreValues.ContainsKey(column))
            .ToList();
        if (currentColumns.Count == 0)
        {
            return false;
        }

        var allCurrentLow = currentColumns.All(column =>
            Math.Abs(GetOptionalValue(row.FreezeCoreValues, column) ?? 0) < config.CurrentActiveMinThreshold);
        if (!allCurrentLow)
        {
            return false;
        }

        var voltageColumns = new[] { "Uab", "Ubc", "Uca" }
            .Where(column => row.FreezeCoreValues.ContainsKey(column))
            .ToList();
        return voltageColumns.Count == 0
            || voltageColumns.All(column =>
                GetOptionalValue(row.FreezeCoreValues, column) is { } value
                && value >= config.VoltageMinThreshold
                && value <= config.VoltageMaxThreshold);
    }

    private static double? GetOptionalValue(IReadOnlyDictionary<string, double> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static bool IsFreezeOnlyType(string type) =>
        type.Contains("数据冻结", StringComparison.Ordinal)
        || type.Contains("数据恒定", StringComparison.Ordinal)
        || type.Contains("传感器缺失", StringComparison.Ordinal)
        || type.Contains("恒定", StringComparison.Ordinal)
        || type.Contains("缺失", StringComparison.Ordinal);

    private static void UpdateOfflineInvalidCounts(
        IReadOnlyDictionary<string, double> rowValues,
        Dictionary<string, int> counts)
    {
        foreach (var (name, value) in rowValues)
        {
            if (Math.Abs(value - -1.0) < 0.000_001 && counts.ContainsKey(name))
            {
                counts[name]++;
            }
        }
    }

    private static void UpdateSensorStates(
        IReadOnlyList<string> cells,
        IReadOnlyList<NativeColumnGroup> columnGroups,
        Dictionary<string, NativeSensorColumnState> states)
    {
        foreach (var group in columnGroups)
        {
            if (!states.TryGetValue(group.Name, out var state))
            {
                continue;
            }

            // A present header with a blank/missing cell is still an observed
            // sensor value; pandas later fills that NaN with zero for the
            // all-zero (missing sensor) check.
            state.HasObservedCell = true;
            var numericValues = group.Indexes
                .Where(index => index < cells.Count)
                .Select(index => TryParseDouble(cells[index], out var parsed) ? (double?)parsed : null)
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .ToList();
            if (numericValues.Count == 0)
            {
                continue;
            }

            // Merge duplicate aliases by averaging them
            // before invalid-value statistics and rule evaluation.
            var value = numericValues.Average();
            if (IsTemperatureColumn(group.Name) && Math.Abs(value - 2867.2) < 0.000_001)
            {
                state.FaultCount++;
            }

            if (!IsInvalidValue(value) && Math.Abs(value) > 0.000_001)
            {
                state.HasNonZeroValidValue = true;
            }
        }
    }

    private static IReadOnlyList<string> ReadCsvLines(string path)
    {
        foreach (var encoding in GetCandidateEncodings(path))
        {
            try
            {
                return File.ReadAllLines(path, encoding);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return File.ReadAllLines(path, Encoding.UTF8);
    }

    private static IEnumerable<Encoding> GetCandidateEncodings(string path)
    {
        var encodings = new List<Encoding>();
        AddEncoding(encodings, DetectPreferredEncoding(path));
        foreach (var encoding in new[]
                 {
                     GetStrictEncoding(936),
                     new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
                     GetStrictEncoding(54936),
                     new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                 })
        {
            AddEncoding(encodings, encoding);
        }

        return encodings;
    }

    private static Encoding DetectPreferredEncoding(string path)
    {
        var head = new byte[4096];
        var bytesRead = 0;
        try
        {
            using var stream = File.OpenRead(path);
            bytesRead = stream.Read(head, 0, head.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GetStrictEncoding(54936);
        }

        if (bytesRead >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        }

        return CanDecodeUtf8(head.AsSpan(0, bytesRead))
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            : GetStrictEncoding(54936);
    }

    private static bool CanDecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void AddEncoding(List<Encoding> encodings, Encoding encoding)
    {
        if (!encodings.Any(existing => existing.CodePage == encoding.CodePage))
        {
            encodings.Add(encoding);
        }
    }

    private static Encoding GetStrictEncoding(int codePage) =>
        Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

    private static string NormalizeColumnName(string header)
    {
        var normalized = header.Trim();
        if (normalized.Contains("Uab", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Ua(", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Ua电压", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Ua", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("A相电压", StringComparison.Ordinal))
        {
            return "Uab";
        }

        if (normalized.Contains("Ubc", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Ub(", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Ub电压", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Ub", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("B相电压", StringComparison.Ordinal))
        {
            return "Ubc";
        }

        if (normalized.Contains("Uca", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Uc(", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Uc电压", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Uc", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("C相电压", StringComparison.Ordinal))
        {
            return "Uca";
        }

        if (normalized.Contains("Ia", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("A相电流", StringComparison.Ordinal)
            || (normalized.Contains("A", StringComparison.OrdinalIgnoreCase) && normalized.Contains("电流", StringComparison.Ordinal)))
        {
            return "Ia";
        }

        if (normalized.Contains("Ib", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("B相电流", StringComparison.Ordinal)
            || (normalized.Contains("B", StringComparison.OrdinalIgnoreCase) && normalized.Contains("电流", StringComparison.Ordinal)))
        {
            return "Ib";
        }

        if (normalized.Contains("Ic", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("C相电流", StringComparison.Ordinal)
            || (normalized.Contains("C", StringComparison.OrdinalIgnoreCase) && normalized.Contains("电流", StringComparison.Ordinal)))
        {
            return "Ic";
        }

        if (normalized.Contains("有功", StringComparison.Ordinal))
        {
            return "有功功率";
        }

        if (normalized.Contains("无功", StringComparison.Ordinal))
        {
            return "无功功率";
        }

        if (normalized.Contains("功率因数", StringComparison.Ordinal)
            || normalized.Contains("PF", StringComparison.OrdinalIgnoreCase))
        {
            return "功率因数";
        }

        if (normalized.Contains("A", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("温", StringComparison.Ordinal))
        {
            return "A相温度";
        }

        if (normalized.Contains("B", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("温", StringComparison.Ordinal))
        {
            return "B相温度";
        }

        if (normalized.Contains("C", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("温", StringComparison.Ordinal))
        {
            return "C相温度";
        }

        return normalized;
    }

    private static bool IsVoltageColumn(string normalizedHeader) =>
        normalizedHeader is "Uab" or "Ubc" or "Uca";

    private static bool IsCurrentColumn(string normalizedHeader) =>
        normalizedHeader is "Ia" or "Ib" or "Ic";

    private static bool IsTemperatureColumn(string normalizedHeader) =>
        normalizedHeader is "A相温度" or "B相温度" or "C相温度";

    private static bool IsKeyParameterColumn(string normalizedHeader) =>
        normalizedHeader is "Uab" or "Ubc" or "Uca" or "Ia" or "Ib" or "Ic";

    private static int FindTimeColumnIndex(IReadOnlyList<string> headers)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            if (header.Contains("时间", StringComparison.Ordinal)
                || header.Contains("时刻", StringComparison.Ordinal)
                || header.Contains("Date", StringComparison.OrdinalIgnoreCase)
                || header.Contains("Time", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static bool IsStatisticsTailRow(string value) =>
        value.Contains("最大值", StringComparison.Ordinal)
        || value.Contains("最小值", StringComparison.Ordinal)
        || value.Contains("平均值", StringComparison.Ordinal)
        || value.Contains("合计", StringComparison.Ordinal)
        || value.Contains("Total", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Max", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Min", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var currentChar = line[index];
            if (currentChar == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Add('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (currentChar == ',' && !inQuotes)
            {
                cells.Add(new string(current.ToArray()).Trim());
                current.Clear();
            }
            else
            {
                current.Add(currentChar);
            }
        }

        cells.Add(new string(current.ToArray()).Trim());
        return cells;
    }

    private static bool TryParseDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
        || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);

    private static bool IsInvalidValue(double value) =>
        Math.Abs(value - -1.0) < 0.000_001
        || Math.Abs(value - 2867.2) < 0.000_001;

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static double GetFiniteDouble(JsonElement root, string key, double fallback)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            return fallback;
        }

        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when TryParseDouble(value.GetString() ?? "", out var stringValue) => stringValue,
            _ => fallback,
        };
        return double.IsFinite(parsed) ? parsed : fallback;
    }

    private static bool GetBool(JsonElement root, string key, bool fallback)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int GetPositiveInt(JsonElement root, string key, int fallback)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            return fallback;
        }

        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue) => stringValue,
            _ => fallback,
        };
        return parsed > 0 ? parsed : fallback;
    }

    private static ReportDetailPreview BuildDetailPreview(
        string fileName,
        string relativePath,
        NativeLocation location,
        string issueType,
        string issueDetail,
        string severity,
        string issueValue,
        string? time = null,
        IReadOnlyDictionary<string, double>? reportValues = null) =>
        new()
        {
            Building = location.Building,
            Transformer = location.Transformer,
            RelativePath = relativePath,
            SourceFile = fileName,
            Date = ExtractDateFromFileName(fileName),
            Time = time,
            Severity = severity,
            IssueType = issueType,
            IssueDetail = issueDetail,
            IssueValue = issueValue,
            RecommendedAction = RecommendedActionForIssueType(issueType),
            ReportValues = reportValues is null
                ? []
                : reportValues.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
        };

    private static string SeverityForIssueType(string issueType)
    {
        if (issueType.Contains("设备离线", StringComparison.Ordinal)
            || issueType.Contains("PT接线异常", StringComparison.Ordinal)
            || issueType.Contains("CT", StringComparison.Ordinal)
            || issueType.Contains("CT极性异常", StringComparison.Ordinal))
        {
            return "高";
        }

        return IsFreezeOnlyType(issueType) ? "低" : "中";
    }

    private static string RecommendedActionForIssueType(string issueType)
    {
        if (issueType.Contains("PT接线异常", StringComparison.Ordinal))
        {
            return "优先检查 PT 二次接线、端子松动和采样通道配置。";
        }

        if (issueType.Contains("CT极性异常", StringComparison.Ordinal))
        {
            return "检查 CT 二次侧极性、功率方向和接线相序。";
        }

        if (issueType.Contains("设备离线", StringComparison.Ordinal))
        {
            return "核查设备通信、电源状态和采集网关在线状态。";
        }

        if (issueType.Contains("电压", StringComparison.Ordinal))
        {
            return "复核电压采样、负载状态和对应回路运行方式。";
        }

        if (issueType.Contains("电流", StringComparison.Ordinal))
        {
            return "检查负载分配、三相平衡和额定容量匹配情况。";
        }

        if (issueType.Contains("功率因数", StringComparison.Ordinal))
        {
            return "检查无功补偿、负载性质和功率因数采样配置。";
        }

        if (issueType.Contains("温度", StringComparison.Ordinal))
        {
            return "检查柜内散热、负载水平和温度传感器安装状态。";
        }

        if (issueType.Contains("数据冻结", StringComparison.Ordinal)
            || issueType.Contains("数据恒定", StringComparison.Ordinal))
        {
            return "优先排查采集链路、网关缓存和测点刷新周期。";
        }

        if (issueType.Contains("传感器", StringComparison.Ordinal))
        {
            return "核查传感器配置、接线和量程映射。";
        }

        return "结合现场运行方式复核该时段数据。";
    }

    private static string BuildSensorStatusReason(
        bool isOffline,
        IReadOnlyList<NativeSensorFault> sensorFaults,
        IReadOnlyList<string> sensorMissing)
    {
        var reasons = new List<string>();
        if (isOffline)
        {
            reasons.Add("关键电气参数离线值超过50%");
        }

        if (sensorFaults.Count > 0)
        {
            reasons.Add($"传感器故障: {string.Join("；", sensorFaults.Select(static fault => $"{fault.Name}({fault.Count})"))}");
        }

        if (sensorMissing.Count > 0)
        {
            reasons.Add($"传感器未配置: {string.Join("；", sensorMissing)}");
        }

        return string.Join("；", reasons);
    }

    private static string BuildSensorFaultSummaryType(NativeSensorFault fault) =>
        $"传感器故障({fault.Name})";

    private static string BuildSensorFaultIssueType(NativeSensorFault fault) =>
        $"传感器故障({fault.Name}:2867.2°C,共{fault.Count}点)";

    private static string ExtractDateFromFileName(string fileName)
    {
        var matches = DatePattern().Matches(fileName);
        if (matches.Count == 0)
        {
            return "日期未知";
        }

        var candidate = matches[^1].Value
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("/", "", StringComparison.Ordinal);
        if (candidate.Length != 8)
        {
            return "日期未知";
        }

        return $"{candidate[..4]}-{candidate.Substring(4, 2)}-{candidate.Substring(6, 2)}";
    }

    private static NativeLocation ExtractLocation(string path, string inputRoot)
    {
        var relativePath = Path.GetRelativePath(inputRoot, path);
        var parts = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var building = parts.Length > 1 ? parts[0] : "根目录";
        var fileStem = Path.GetFileNameWithoutExtension(path);
        var match = TransformerRegex().Match(Path.GetFileName(path));
        var transformer = match.Success ? match.Value : fileStem;
        return new NativeLocation(building, transformer);
    }

    [GeneratedRegex("(\\d+TM[a-zA-Z]*\\d*|充电桩)", RegexOptions.CultureInvariant)]
    private static partial Regex TransformerRegex();

    [GeneratedRegex("(\\d{4}[_\\-]?[01]\\d[_\\-]?[0-3]\\d|\\d{8})", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    private static IReadOnlyList<ReportDeviceSummary> BuildDeviceSummaries(
        IReadOnlyList<ReportDetailPreview> details)
    {
        return details
            .GroupBy(
                static detail => new
                {
                    Building = string.IsNullOrWhiteSpace(detail.Building) ? "根目录" : detail.Building,
                    DevicePath = DevicePathFromRelativePath(detail.RelativePath),
                    Transformer = string.IsNullOrWhiteSpace(detail.Transformer) ? "未知" : detail.Transformer,
                })
            .Select(static group =>
            {
                var severities = group.Select(static detail => detail.Severity ?? "").ToList();
                var issueTypes = group
                    .SelectMany(static detail => SplitIssueTypes(detail.IssueType))
                    .GroupBy(static issueType => issueType, StringComparer.Ordinal)
                    .Select(static issueGroup => new { Name = issueGroup.Key, Count = issueGroup.Count() })
                    .OrderByDescending(static issue => issue.Count)
                    .Take(5)
                    .Select(static issue => $"{issue.Name}({issue.Count})")
                    .ToList();
                var highestSeverity = severities.Contains("高", StringComparer.Ordinal)
                    ? "高"
                    : severities.Contains("中", StringComparer.Ordinal)
                        ? "中"
                        : "低";
                return new ReportDeviceSummary
                {
                    Building = group.Key.Building,
                    DevicePath = group.Key.DevicePath,
                    Transformer = group.Key.Transformer,
                    AnomalyRecords = group.Count(),
                    MainIssueTypes = string.Join("；", issueTypes),
                    HighestSeverity = highestSeverity,
                    Priority = PriorityForSeverity(highestSeverity),
                };
            })
            .OrderBy(static device => PriorityRank(device.Priority))
            .ThenByDescending(static device => device.AnomalyRecords)
            .ToList();
    }

    private static List<ReportIssueType> BuildTopIssueTypes(IReadOnlyList<ReportDetailPreview> details)
    {
        return BuildIssueTypeStatistics(details)
            .Take(8)
            .ToList();
    }

    private static List<ReportIssueType> BuildIssueTypeStatistics(IReadOnlyList<ReportDetailPreview> details)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var issueType in details.SelectMany(static detail => SplitIssueTypes(detail.IssueType)))
        {
            if (!counts.ContainsKey(issueType))
            {
                counts[issueType] = 0;
                order.Add(issueType);
            }

            counts[issueType]++;
        }

        return order
            .Select(name => new ReportIssueType { Name = name, Count = counts[name] })
            .OrderByDescending(static issue => issue.Count)
            .ToList();
    }

    private static string FirstDetailTime(
        IReadOnlyList<ReportDetailPreview> details,
        string building,
        string devicePath,
        string transformer) =>
        details
            .Where(detail => DetailMatchesDevice(detail, building, devicePath, transformer))
            .Select(static detail => detail.TimeText)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string LastDetailTime(
        IReadOnlyList<ReportDetailPreview> details,
        string building,
        string devicePath,
        string transformer) =>
        details
            .Where(detail => DetailMatchesDevice(detail, building, devicePath, transformer))
            .Select(static detail => detail.TimeText)
            .LastOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static bool DetailMatchesDevice(
        ReportDetailPreview detail,
        string building,
        string devicePath,
        string transformer) =>
        string.Equals(
            string.IsNullOrWhiteSpace(detail.Building) ? "根目录" : detail.Building,
            building,
            StringComparison.Ordinal)
        && string.Equals(DevicePathFromRelativePath(detail.RelativePath), devicePath, StringComparison.Ordinal)
        && string.Equals(
            string.IsNullOrWhiteSpace(detail.Transformer) ? "未知" : detail.Transformer,
            transformer,
            StringComparison.Ordinal);

    private static string DevicePathFromRelativePath(string? relativePath)
    {
        var raw = (relativePath ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("/", StringComparison.Ordinal)
            || (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':'))
        {
            return "根目录";
        }

        var parts = raw
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => part != ".")
            .ToArray();
        if (parts.Length <= 1 || parts.Any(static part => part == ".."))
        {
            return "根目录";
        }

        return string.Join('/', parts[..^1]);
    }

    private static IEnumerable<string> SplitIssueTypes(string? issueTypes) =>
        (issueTypes ?? "")
            .Split([';', '；', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string PriorityForSeverity(string severity) =>
        severity switch
        {
            "高" => "P1",
            "中" => "P2",
            _ => "P3",
        };

    private static int PriorityRank(string priority) =>
        priority switch
        {
            "P1" => 0,
            "P2" => 1,
            "P3" => 2,
            _ => 3,
        };

    private static int SeverityRank(string? severity) =>
        severity switch
        {
            "高" => 0,
            "中" => 1,
            "低" => 2,
            _ => 3,
        };

    private sealed record NativeDetectionConfig(
        double VoltageMinThreshold = 353.0,
        double VoltageMaxThreshold = 430.0,
        double VoltageImbalanceThreshold = 0.02,
        double CurrentMaxThreshold = 1000.0,
        double CurrentUnbalanceMaxThreshold = 0.15,
        double CurrentActiveMinThreshold = 1.0,
        double ActivePowerMinThreshold = 0.0,
        double PowerFactorMinThreshold = 0.90,
        double TemperatureMinThreshold = 0.0,
        double TemperatureMaxThreshold = 70.0,
        bool CurrentOverloadEnabled = true,
        bool CurrentUnbalanceEnabled = false,
        bool PowerFactorEnabled = false,
        bool DetailOutputEnabled = false,
        int FreezeCountThreshold = 3,
        double FreezeStdThreshold = 0.01);

    private sealed record NativeConfigLoadResult(
        NativeDetectionConfig Config,
        bool IsSuccess,
        string? ErrorType = null,
        string? ErrorMessage = null)
    {
        public static NativeConfigLoadResult Success(NativeDetectionConfig config) =>
            new(config, true);

        public static NativeConfigLoadResult Error(string errorType, string message) =>
            new(new NativeDetectionConfig(), false, errorType, message);
    }

    private sealed record NativeColumnGroup(
        string Name,
        IReadOnlyList<int> Indexes);

    private sealed record NativeLocation(
        string Building,
        string Transformer);

    private sealed record NativeRowAnalysis(
        string Time,
        IReadOnlyList<string> AnomalyTypes,
        IReadOnlyDictionary<string, double> FreezeCoreValues,
        IReadOnlyDictionary<string, double> FreezeAuxValues,
        IReadOnlyDictionary<string, double> ReportValues);

    private sealed record NativeFileAnalysis(
        string Status,
        string Message,
        int AnomalyCount = 0,
        string AnomalyTypes = "",
        NativeSensorStatusRow? SensorStatus = null,
        IReadOnlyList<ReportDetailPreview>? DetailPreview = null,
        NativeParityTrace? ParityTrace = null)
    {
        public NativeSensorStatusRow SensorStatus { get; } = SensorStatus ?? NativeSensorStatusRow.Empty;

        public IReadOnlyList<ReportDetailPreview> DetailPreview { get; } = DetailPreview ?? [];

        public static NativeFileAnalysis Normal(
            string fileName,
            IReadOnlyList<string>? sensorMissing = null,
            NativeSensorStatusRow? sensorStatus = null,
            NativeParityTrace? parityTrace = null) =>
            new(
                "normal",
                $"正常 {fileName}{BuildMissingSuffix(sensorMissing)}",
                SensorStatus: sensorStatus,
                ParityTrace: parityTrace);

        public static NativeFileAnalysis Anomaly(
            string fileName,
            int anomalyCount,
            string anomalyTypes,
            IReadOnlyList<string>? sensorMissing = null,
            NativeSensorStatusRow? sensorStatus = null,
            IReadOnlyList<ReportDetailPreview>? detailPreview = null,
            NativeParityTrace? parityTrace = null) =>
            new(
                "anomaly",
                $"异常 {fileName}: {anomalyCount} 条{BuildMissingSuffix(sensorMissing)} [{anomalyTypes}]",
                anomalyCount,
                anomalyTypes,
                sensorStatus,
                detailPreview,
                parityTrace);

        public static NativeFileAnalysis Skipped(string message) =>
            new("skipped", message);

        private static string BuildMissingSuffix(IReadOnlyList<string>? sensorMissing) =>
            sensorMissing is { Count: > 0 }
                ? $" [未配置: {string.Join(", ", sensorMissing)}]"
                : "";
    }

    private sealed record NativeSensorStatusRow(
        string SourceFile,
        string Building,
        string RelativePath,
        string Transformer,
        bool IsOffline,
        IReadOnlyList<string> SensorFaults,
        IReadOnlyList<string> SensorMissing,
        string Status,
        string Reason)
    {
        public static NativeSensorStatusRow Empty { get; } = new("", "", "", "", false, [], [], "", "");

        public static NativeSensorStatusRow Skipped(
            string fileName,
            string relativePath,
            NativeLocation location,
            string reason) =>
            new(fileName, location.Building, relativePath, location.Transformer, false, [], [], "跳过", reason);
    }

    private sealed record NativeSensorFault(
        string Name,
        int Count);

    private sealed class NativeSensorColumnState
    {
        public bool HasObservedCell { get; set; }

        public bool HasNonZeroValidValue { get; set; }

        public int FaultCount { get; set; }
    }
}

[CmdletBinding()]
param(
    [ValidateSet('All', 'Migration', 'Poetry')]
    [string]$CaseName = 'All'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\desktop\settings-migration-smoke'))
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\desktop'))
$expectedPrefix = $expectedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $artifactRoot.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a settings smoke directory outside '$expectedRoot': $artifactRoot"
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}

$harnessRoot = Join-Path $artifactRoot 'harness'
$settingsRoot = Join-Path $artifactRoot 'settings'
New-Item -ItemType Directory -Path $harnessRoot, $settingsRoot -Force | Out-Null

function ConvertTo-XmlAttribute([string]$Value) {
    return [System.Security.SecurityElement]::Escape($Value)
}

$nativeProject = ConvertTo-XmlAttribute (Join-Path $repoRoot 'desktop\EDetection.NativeCore\EDetection.NativeCore.csproj')
$linkedSources = @(
    'desktop\EDetection.Desktop\GlobalUsings.cs',
    'desktop\EDetection.Desktop\Models\AppSettings.cs',
    'desktop\EDetection.Desktop\Models\RecentReport.cs',
    'desktop\EDetection.Desktop\Models\PoetryStatusSnapshot.cs',
    'desktop\EDetection.Desktop\Models\SettingsServiceVersion.cs',
    'desktop\EDetection.Desktop\Services\SettingsService.cs',
    'desktop\EDetection.Desktop\Services\PoetryStatusService.cs',
    'desktop\EDetection.Desktop\Services\EndpointSecurity.cs'
)
$compileItems = ($linkedSources | ForEach-Object {
    $sourcePath = ConvertTo-XmlAttribute (Join-Path $repoRoot $_)
    "    <Compile Include=`"$sourcePath`" Link=`"$([System.IO.Path]::GetFileName($_))`" />"
}) -join [Environment]::NewLine

$projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$nativeProject" />
$compileItems
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $harnessRoot 'SettingsSmoke.csproj') -Value $projectText -Encoding utf8NoBOM

$programText = @'
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

var caseName = args[0];
var settingsRoot = args[1];
if (caseName is "All" or "Migration")
{
    RunMigration(settingsRoot);
    Console.WriteLine("PASS settings-migration-keeps-poetry-disabled");
}

if (caseName is "All" or "Poetry")
{
    await RunPoetryAsync();
    Console.WriteLine("PASS poetry-handler-and-endpoint-policy");
}

static void RunMigration(string settingsRoot)
{
    Environment.SetEnvironmentVariable("EDETECTION_DESKTOP_SETTINGS_DIR", settingsRoot);
    Directory.CreateDirectory(settingsRoot);
    var settingsPath = Path.Combine(settingsRoot, "settings.json");
    File.WriteAllText(
        settingsPath,
        """
        {
          "SettingsVersion": 7,
          "EnablePoetryStatus": false,
          "PoetryServiceUrl": "",
          "SelectedPoetryLanguageIndex": 0
        }
        """);

    var loaded = new SettingsService().Load();
    Assert(!loaded.EnablePoetryStatus, "Migrating v7 settings must not opt the user into poetry networking.");
    Assert(loaded.SettingsVersion == SettingsService.CurrentSettingsVersion, "Migration must update the settings version.");
    using var persisted = JsonDocument.Parse(File.ReadAllText(settingsPath));
    Assert(
        !persisted.RootElement.GetProperty("EnablePoetryStatus").GetBoolean(),
        "The migrated settings file must persist poetry as disabled.");
}

static async Task RunPoetryAsync()
{
    var method = typeof(PoetryStatusService).GetMethod(
        "GetRandomAsync",
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        [typeof(string), typeof(int), typeof(HttpMessageHandler), typeof(CancellationToken)],
        modifiers: null)
        ?? throw new InvalidOperationException(
            "PoetryStatusService.GetRandomAsync must accept an HttpMessageHandler.");
    var service = new PoetryStatusService();

    using var httpsHandler = new RecordingHandler();
    var live = await InvokePoetryAsync(method, service, "https://poetry.example/", httpsHandler);
    Assert(!live.IsFallback, "A valid HTTPS response must use the injected handler result.");
    Assert(httpsHandler.RequestCount == 1, "The injected handler must receive the poetry request.");

    using var localHandler = new RecordingHandler();
    var local = await InvokePoetryAsync(method, service, "http://127.0.0.1:8080/", localHandler);
    Assert(!local.IsFallback, "Local HTTP endpoints must remain available for development.");
    Assert(localHandler.RequestCount == 1, "The local request must use the injected handler.");

    using var insecureHandler = new RecordingHandler();
    var insecure = await InvokePoetryAsync(method, service, "http://example.com/", insecureHandler);
    Assert(insecure.IsFallback, "A non-local HTTP endpoint must be rejected without a request.");
    Assert(insecureHandler.RequestCount == 0, "Rejected HTTP endpoints must not reach the network handler.");
}

static async Task<PoetryStatusSnapshot> InvokePoetryAsync(
    MethodInfo method,
    PoetryStatusService service,
    string endpoint,
    HttpMessageHandler handler)
{
    var task = method.Invoke(service, [endpoint, 0, handler, CancellationToken.None])
        as Task<PoetryStatusSnapshot>;
    return await (task ?? throw new InvalidOperationException("Poetry request must return the expected task type."));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class RecordingHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"data":{"title":"Test","content":["Line"],"author":{"name":"Author"},"dynasty":{"name":"Era"}}}
                """,
                Encoding.UTF8,
                "application/json"),
        };
        return Task.FromResult(response);
    }
}
'@
Set-Content -LiteralPath (Join-Path $harnessRoot 'Program.cs') -Value $programText -Encoding utf8NoBOM

& dotnet run --project (Join-Path $harnessRoot 'SettingsSmoke.csproj') -c Debug -- $CaseName $settingsRoot
if ($LASTEXITCODE -ne 0) {
    throw "Settings migration smoke failed with exit code $LASTEXITCODE."
}

if ($CaseName -in @('All', 'Poetry')) {
    $viewModelText = Get-Content -LiteralPath (Join-Path $repoRoot 'desktop\EDetection.Desktop\ViewModels\MainViewModel.cs') -Raw
    if (-not $viewModelText.Contains('_networkProxy.BuildHandler(this, useProxy: true)', [System.StringComparison]::Ordinal)) {
        throw 'Poetry refresh must build a handler through the shared proxy service after opt-in.'
    }
    if ($viewModelText -notmatch 'GetRandomAsync\(\s*PoetryServiceUrl,\s*SelectedPoetryLanguageIndex,\s*handler') {
        throw 'Poetry refresh must pass the shared proxy handler to PoetryStatusService.'
    }
}

Write-Host "Desktop settings and optional-network smoke passed: $CaseName"

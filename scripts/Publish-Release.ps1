[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repository,
    [string]$OutputDirectory,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspace 'src\4thIBTracker\4thIBTracker.csproj'
$defaultSettings = Join-Path $workspace 'src\4thIBTracker\appsettings.json'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $Version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use MAJOR.MINOR.PATCH, for example 1.2.3. Received: '$Version'"
}

if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = [string]$env:GITHUB_REPOSITORY
}
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $remote = & git -C $workspace remote get-url origin 2>$null
    if ($LASTEXITCODE -eq 0 -and $remote -match 'github\.com[/:](?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?$') {
        $Repository = $Matches.repo
    }
}
if (-not [string]::IsNullOrWhiteSpace($Repository) -and
    $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must use owner/name format. Received: '$Repository'"
}

# Release defaults must never contain unit configuration. Existing users receive
# their real settings from AppData, so blank embedded defaults are intentional.
$settings = Get-Content -LiteralPath $defaultSettings -Raw | ConvertFrom-Json
$privateValues = [Collections.Generic.List[string]]::new()
foreach ($sheet in $settings.Spreadsheets.PSObject.Properties) {
    if (-not [string]::IsNullOrWhiteSpace([string]$sheet.Value.Id)) {
        $privateValues.Add("Spreadsheets.$($sheet.Name).Id")
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$sheet.Value.Tab)) {
        $privateValues.Add("Spreadsheets.$($sheet.Name).Tab")
    }
}
foreach ($name in @('FillInFormId', 'OrbatUrl')) {
    if (-not [string]::IsNullOrWhiteSpace([string]$settings.$name)) {
        $privateValues.Add($name)
    }
}
foreach ($name in @('AddressFrom', 'SignOff', 'SignOffPhrase')) {
    if (-not [string]::IsNullOrWhiteSpace([string]$settings.Platoon.$name)) {
        $privateValues.Add("Platoon.$name")
    }
}
if (@($settings.Platoon.NcoTrackerPositions).Count -gt 0) {
    $privateValues.Add('Platoon.NcoTrackerPositions')
}
if (@($settings.Platoon.OutstandingCourseExclusions).Count -gt 0) {
    $privateValues.Add('Platoon.OutstandingCourseExclusions')
}
foreach ($property in $settings.Forum.PSObject.Properties) {
    if ($property.Value -is [string] -and
        -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        $privateValues.Add("Forum.$($property.Name)")
    }
    elseif ($property.Value -is [Collections.IEnumerable] -and
            $property.Value -isnot [string] -and @($property.Value).Count -gt 0) {
        $privateValues.Add("Forum.$($property.Name)")
    }
}
if (@($settings.BrowserTabs).Count -gt 0) {
    $privateValues.Add('BrowserTabs')
}
if ($privateValues.Count -gt 0) {
    throw "Embedded appsettings contains private defaults: $($privateValues -join ', ')"
}

$credentialFiles = @(Get-ChildItem -LiteralPath $workspace -Recurse -Force -File -Filter 'credentials*.json')
if ($credentialFiles.Count -gt 0) {
    throw 'Refusing to publish while credentials.json exists inside the workspace.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspace "artifacts\release\v$Version"
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspace $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$allowedOutputRoots = @('artifacts', 'dist', 'publish') | ForEach-Object {
    [IO.Path]::GetFullPath((Join-Path $workspace $_))
}
$outputIsAllowed = $false
foreach ($allowedRoot in $allowedOutputRoots) {
    $allowedPrefix = $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if ($OutputDirectory.Equals($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $OutputDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $outputIsAllowed = $true
        break
    }
}
if (-not $outputIsAllowed) {
    throw 'Publish output must be within the workspace artifacts, dist, or publish directory.'
}
if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not $Force) {
        throw "Output already exists: $OutputDirectory. Pass -Force to replace generated output."
    }
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

$arguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:Version=$Version",
    '--output', $OutputDirectory
)
if (-not [string]::IsNullOrWhiteSpace($Repository)) {
    $arguments += "-p:UpdateRepository=$Repository"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $OutputDirectory '4thIBTracker.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'The publisher did not produce 4thIBTracker.exe.'
}
$unexpected = @(Get-ChildItem -LiteralPath $OutputDirectory -Force -File |
    Where-Object Name -ne '4thIBTracker.exe')
if ($unexpected.Count -gt 0) {
    throw "Unexpected release sidecars were produced: $($unexpected.Name -join ', ')"
}

$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = Join-Path $OutputDirectory '4thIBTracker.exe.sha256'
[IO.File]::WriteAllText(
    $checksum,
    "$hash  4thIBTracker.exe`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Release v$Version ready: $OutputDirectory"
if ([string]::IsNullOrWhiteSpace($Repository)) {
    Write-Warning 'No GitHub repository was detected. This build cannot check for updates.'
}
else {
    Write-Host "Updater repository: $Repository"
}
Write-Host "SHA-256: $hash"

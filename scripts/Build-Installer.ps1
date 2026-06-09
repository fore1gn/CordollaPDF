param(
    [string]$Version = "0.1.0",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$Publisher = "CordollaPDF",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "CordollaPDF\CordollaPDF.csproj"
$publishDir = Join-Path $repoRoot ("artifacts\publish\{0}-single" -f $RuntimeIdentifier)
$installerDir = Join-Path $repoRoot "artifacts\installer"
$issPath = Join-Path $repoRoot "installer\CordollaPDF.iss"
$assemblyVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"

if (-not $SkipPublish) {
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $publishArgs = @(
        "publish"
        $projectPath
        "-c", $Configuration
        "-r", $RuntimeIdentifier
        "--self-contained", "true"
        "-p:PublishSingleFile=true"
        "-p:IncludeNativeLibrariesForSelfExtract=true"
        "-p:Version=$Version"
        "-p:AssemblyVersion=$assemblyVersion"
        "-p:FileVersion=$assemblyVersion"
        "-o", $publishDir
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }
}

$exePath = Join-Path $publishDir "CordollaPDF.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish output is missing $exePath"
}

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)

$isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $isccPath) {
    throw "Inno Setup 6 was not found. Install it, then rerun this script."
}

$compilerArgs = @(
    "/DAppName=CordollaPDF"
    "/DAppVersion=$Version"
    "/DAppPublisher=$Publisher"
    "/DAppExeName=CordollaPDF.exe"
    "/DPublishDir=$publishDir"
    "/DOutputDir=$installerDir"
    $issPath
)

& $isccPath @compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed."
}

Write-Host ""
Write-Host "Installer created in $installerDir"

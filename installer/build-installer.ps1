$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\single-file"
$outputDir = Join-Path $repoRoot "artifacts\installer"
$installerPath = Join-Path $outputDir "ChatWorkbenchSetup.exe"
$issPath = Join-Path $PSScriptRoot "ChatWorkbench.iss"
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup compiler was not found. Install it with: winget install --id JRSoftware.InnoSetup"
}

New-Item -ItemType Directory -Force -Path $publishDir, $outputDir | Out-Null

dotnet publish "$repoRoot\GPTBackup.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir

& $iscc `
    "/DRepoRoot=$repoRoot" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    $issPath

if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer build failed. Expected output: $installerPath"
}

Get-Item -LiteralPath $installerPath

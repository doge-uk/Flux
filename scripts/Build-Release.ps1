[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$SkipTests,
    [string]$GitHubRepository = $env:FLUX_UPDATE_REPOSITORY,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$localDotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactRoot 'Flux-win-x64'
$archivePath = Join-Path $artifactRoot 'Flux-win-x64.zip'

if (-not $publishDirectory.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to package outside the Flux workspace.'
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$msbuildProperties = @()
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
    if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'GitHubRepository must use the owner/repository format.'
    }
    $msbuildProperties += "-p:FluxUpdateRepository=$GitHubRepository"
}
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Version must use the major.minor.patch format.'
    }
    $msbuildProperties += "-p:Version=$Version"
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

& $dotnet build (Join-Path $projectRoot 'Flux.slnx') --configuration Release --nologo @msbuildProperties
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

if (-not $SkipTests) {
    & $dotnet run --project (Join-Path $projectRoot 'tests\Flux.Core.Tests\Flux.Core.Tests.csproj') --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Behavioral tests failed.' }
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $dotnet publish (Join-Path $projectRoot 'src\Flux.Windows\Flux.Windows.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained $selfContained `
    --output $publishDirectory `
    --nologo `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    @msbuildProperties
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256

Write-Host "Flux package: $archivePath"
Write-Host "SHA-256: $($hash.Hash)"
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
    Write-Host "Update feed: https://github.com/$GitHubRepository/releases/latest"
}

param([switch]$Test, [switch]$HardwareTest, [switch]$VerifyAncWrite)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Push-Location $PSScriptRoot
try {
    $dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $fallback = Join-Path $PSScriptRoot '.tools/nuget-fallback'
    New-Item -ItemType Directory -Force $fallback | Out-Null
    & $dotnet restore src/Nothing.Desktop --force "-p:RestoreFallbackFolders=$fallback"
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $dotnet publish src/Nothing.Desktop -c Release --no-restore --self-contained false -o artifacts/Nothing.Desktop
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($Test -or $HardwareTest -or $VerifyAncWrite) {
        & $dotnet restore tests/Nothing.Tests --force "-p:RestoreFallbackFolders=$fallback"
        if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
        $arguments = @('run','--project','tests/Nothing.Tests','-c','Release','--no-restore','--')
        if ($HardwareTest -or $VerifyAncWrite) { $arguments += '--hardware' }
        if ($VerifyAncWrite) { $arguments += '--verify-anc-write' }
        & $dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    Write-Host 'Ready: artifacts/Nothing.Desktop/Nothing.Desktop.exe'
} finally { Pop-Location }

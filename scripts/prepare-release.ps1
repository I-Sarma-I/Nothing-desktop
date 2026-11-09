param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

try {
    $output = Join-Path $root 'artifacts/release/win-x64'
    $archive = Join-Path $root "artifacts/Nothing.Desktop-$Version-win-x64.zip"

    dotnet publish src/Nothing.Desktop/Nothing.Desktop.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        "-p:Version=$Version" `
        --output $output

    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }

    Copy-Item LICENSE, README.md -Destination $output

    Compress-Archive `
        -Path "$output/*" `
        -DestinationPath $archive `
        -Force

    Write-Host "Packaged: $archive"
}
finally {
    Pop-Location
}
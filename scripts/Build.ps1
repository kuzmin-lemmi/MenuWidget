param([string]$DotnetPath, [switch]$Capture, [Alias('PublishPrototype')][switch]$PublishApp)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (!$DotnetPath) {
        $localSdk = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
        $DotnetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli'
    & $DotnetPath restore MenuWidget.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $DotnetPath build MenuWidget.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotnetPath run --project experiments/MenuWidget.Probes -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Technical probes failed.' }
    if ($Capture) {
        & $DotnetPath run --project src/MenuWidget.Prototype -c Release --no-build --no-restore -- --capture docs/design
        if ($LASTEXITCODE -ne 0) { throw 'UI capture failed.' }
    }
    if ($PublishApp) {
        & $DotnetPath publish src/MenuWidget.Prototype -c Release -r win-x64 --self-contained true -o artifacts/app
        if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    }
} finally { Pop-Location }

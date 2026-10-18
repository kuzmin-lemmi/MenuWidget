param([int]$Port=18749)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$stage=Join-Path $repoRoot 'artifacts\stage1'
$state=Join-Path $env:LOCALAPPDATA 'MenuWidget.Stage1Probe'
$install=Join-Path $env:LOCALAPPDATA 'Programs\MenuWidgetStage1Probe'
$results=[Collections.Generic.List[object]]::new()
function Check([string]$name,[bool]$ok) { $results.Add(@{name=$name;passed=$ok}); if(!$ok){throw ('Failed: '+$name)}; Write-Output ('PASS '+$name) }
function Install([string]$file,[string]$label,[string]$extra='') {
    $log=Join-Path $stage ($label+'.log')
    $p=Start-Process msiexec.exe -ArgumentList ('/i "'+$file+'" /qn /norestart '+$extra+' /L*v "'+$log+'"') -PassThru -Wait -WindowStyle Hidden
    return $p.ExitCode
}
function Version { return [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'MenuWidget.WindowsProbe.dll')).FileVersion }
New-Item -ItemType Directory -Path $state -Force | Out-Null
$userState=Join-Path $state 'synthetic-user-settings.json'
if(Test-Path -LiteralPath $userState){
    if((Get-Content -LiteralPath $userState -Raw | ConvertFrom-Json).synthetic -ne $true){throw 'Refusing to use non-synthetic data.'}
}else{ '{"synthetic":true,"anchor":"2026-10-12","reminder":"18:00","templates":["example-only"]}' | Set-Content -LiteralPath $userState -Encoding UTF8 }
$before=(Get-FileHash -LiteralPath $userState -Algorithm SHA256).Hash
$job=$null
try {
    Check 'version-1-installed' ((Version) -eq '0.0.1.0')
    # Local HTTP is a TEST FIXTURE only. Production update transport remains HTTPS/GitHub.
    $job=Start-Job -ArgumentList $stage,$Port -ScriptBlock {
        param($dir,$port)
        $listener=[Net.HttpListener]::new(); $listener.Prefixes.Add(('http://localhost:'+ $port +'/')); $listener.Start()
        try {
            while($listener.IsListening) {
                $pending=$listener.GetContextAsync()
                while(!$pending.Wait(250)) { }
                $context=$pending.Result; $name=$context.Request.Url.AbsolutePath.TrimStart('/')
                if($name -notmatch '^MenuWidget-Stage1-0\.0\.[123]\.msi$'){$context.Response.StatusCode=404;$context.Response.Close();continue}
                $stream=[IO.File]::OpenRead((Join-Path $dir $name))
                try{$context.Response.ContentLength64=$stream.Length;$stream.CopyTo($context.Response.OutputStream)}finally{$stream.Dispose();$context.Response.Close()}
            }
        }finally{$listener.Close()}
    }
    $download=Join-Path $stage 'downloaded-0.0.2.msi'
    $ready=$false
    for($i=0;$i -lt 20;$i++) {
        try {Invoke-WebRequest -Uri ('http://localhost:'+ $Port +'/MenuWidget-Stage1-0.0.2.msi') -OutFile $download; $ready=$true;break}catch{Start-Sleep -Milliseconds 250}
    }
    Check 'downloaded-installer' $ready
    $expected=(Get-FileHash -LiteralPath (Join-Path $stage 'MenuWidget-Stage1-0.0.2.msi') -Algorithm SHA256).Hash
    Check 'download-sha256-matches' ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -eq $expected)
    $corrupt=Join-Path $stage 'corrupted-fixture.msi'; Copy-Item -LiteralPath $download -Destination $corrupt
    $fs=[IO.File]::Open($corrupt,[IO.FileMode]::Open,[IO.FileAccess]::Write);try{$fs.Position=100;$fs.WriteByte(255)}finally{$fs.Dispose()}
    Check 'corrupt-download-rejected-before-install' ((Get-FileHash -LiteralPath $corrupt -Algorithm SHA256).Hash -ne $expected)
    Check 'upgrade-msi-exit-0' ((Install $download 'upgrade-0.0.2') -eq 0)
    Check 'version-2-installed' ((Version) -eq '0.0.2.0')
    Check 'settings-preserved-after-upgrade' ((Get-FileHash -LiteralPath $userState).Hash -eq $before)
    $failCode=Install (Join-Path $stage 'MenuWidget-Stage1-0.0.3.msi') 'rollback-0.0.3' 'STAGE1FAIL=1'
    Check 'intentional-install-failure' ($failCode -eq 1603)
    Check 'rollback-restores-version-2' ((Version) -eq '0.0.2.0')
    Check 'settings-preserved-after-rollback' ((Get-FileHash -LiteralPath $userState).Hash -eq $before)
    $downgrade=Install (Join-Path $stage 'MenuWidget-Stage1-0.0.1.msi') 'downgrade-0.0.1'
    Check 'downgrade-blocked' ($downgrade -eq 1603)
    Check 'version-2-survives-downgrade' ((Version) -eq '0.0.2.0')
    # Verify recovered executable really runs and registers (not only file version).
    $p=Start-Process (Join-Path $install 'MenuWidget.WindowsProbe.exe') -ArgumentList '--self-check' -PassThru -WindowStyle Hidden
    if(!$p.WaitForExit(20000)){$p.Kill();throw 'Recovered app self-check timed out.'}
    Check 'recovered-app-runs' ($p.ExitCode -eq 0)
} finally {
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'upgrade-results.json') -Encoding UTF8
    if($job){Stop-Job $job; Remove-Job $job}
}

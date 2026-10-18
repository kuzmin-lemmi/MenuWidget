param([string]$Installer, [string]$MenuProfile, [string]$ExpectedVersion="0.2.2", [switch]$ResumeAfterUpgrade)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$acceptance=Join-Path $repoRoot 'artifacts\acceptance-installer'
$null=New-Item -ItemType Directory -Path $acceptance -Force
$installed=Join-Path $env:LOCALAPPDATA 'Programs\MenuWidget\MenuWidget.exe'
$profile=Join-Path $env:LOCALAPPDATA 'MenuWidget'
$desktopLink=Join-Path ([Environment]::GetFolderPath('Desktop')) 'MenuWidget.lnk'
$startLink=Join-Path ([Environment]::GetFolderPath('Programs')) 'MenuWidget.lnk'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$results=[Collections.Generic.List[object]]::new()
if($ResumeAfterUpgrade) { foreach($item in (Get-Content -LiteralPath (Join-Path $acceptance 'results.json') -Raw | ConvertFrom-Json)) { $results.Add(@{name=$item.name;passed=$item.passed}) } }
function Check([string]$name,[bool]$ok) { for($i=$results.Count-1;$i -ge 0;$i--) { if($results[$i].name -eq $name){$results.RemoveAt($i)} }; $results.Add(@{name=$name;passed=$ok}); if(!$ok){throw ('Failed: '+$name)}; Write-Output ('PASS '+$name) }
function Msi([string]$action,[string]$file,[string]$label) {
    $log=Join-Path $acceptance ($label+'.log')
    $p=Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList ($action+' "'+$file+'" /qn /norestart /L*v "'+$log+'"') -PassThru -WindowStyle Hidden
    if(!$p.WaitForExit(60000)){throw ('MSI timed out: '+$label)}
    return $p.ExitCode
}
function Health {
    $p=Start-Process -FilePath $installed -ArgumentList '--health-check' -PassThru -WindowStyle Hidden
    if(!$p.WaitForExit(15000)){throw 'Health check timed out.'}; return $p.ExitCode
}
function ExitApp {
    for($attempt=0;$attempt -lt 20;$attempt++) {
        try { $signal=[Threading.EventWaitHandle]::OpenExisting(('Local\MenuWidget.'+$sid+'.exit')); try { $null=$signal.Set() } finally { $signal.Dispose() }; return }
        catch { if($_.Exception.InnerException -isnot [Threading.WaitHandleCannotBeOpenedException]){throw}; Start-Sleep -Milliseconds 250 }
    }
    throw 'App did not expose its exit command within five seconds.'
}
function ProfileHashes {
    $table=@{}
    foreach($file in Get-ChildItem -LiteralPath $profile -Recurse -File | Where-Object { $_.Name -ne 'window.json' -and $_.Name -ne 'diagnostics.log' }) { $table[[IO.Path]::GetRelativePath($profile,$file.FullName)]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    return $table
}
function Preserved($before) {
    foreach($relative in $before.Keys) { $file=Join-Path $profile $relative; if(!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $before[$relative]){return $false} };return $true
}
try {
    if(Get-Process MenuWidget -ErrorAction SilentlyContinue){throw 'Close the existing MenuWidget before this explicit lifecycle acceptance run.'}
    Check 'previous-installed' (Test-Path -LiteralPath $installed)
    Check 'previous-starts-without-sdk' ((Health) -eq 0)
    if(!(Test-Path -LiteralPath $profile)) {
        if(!$MenuProfile){throw 'Provide a verified local menu profile for preservation checks.'}
        $null=New-Item -ItemType Directory -Path $profile
        Copy-Item -LiteralPath (Join-Path $MenuProfile 'templates') -Destination $profile -Recurse
        @{SchemaVersion=1;FirstWeekMonday='2026-10-12';ReminderEnabled=$false;ReminderTime='18:00:00';Topmost=$false;AutoStart=$false;OutputDirectory=(Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Меню для сайта')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8NoBOM
    }
    $before=ProfileHashes
    Check 'profile-has-settings-and-menu-copies' ($before.Count -ge 7)
    if(!$ResumeAfterUpgrade) {
    # Old builds incorrectly reject missing tasks. Seed real tasks before their startup;
    # the new build's diagnostic command also verifies idempotent removal twice.
    $prepare=Start-Process -FilePath (Join-Path $repoRoot ('artifacts/release-payload-'+$ExpectedVersion+'/MenuWidget.exe')) -ArgumentList ('--diagnostics-integrations "'+(Join-Path $acceptance 'prepare-previous.json')+'"') -PassThru -WindowStyle Hidden
    Check 'remove-missing-integrations-is-idempotent' ($prepare.WaitForExit(15000) -and $prepare.ExitCode -eq 0)
    $old=Start-Process -FilePath $installed -PassThru
    if(!$old.WaitForInputIdle(10000)){throw 'Previous app did not become ready.'}
    $oldTicks=$old.StartTime.ToUniversalTime().Ticks
    $runner=Join-Path $acceptance 'MenuWidget.Updater.exe'
    Copy-Item -LiteralPath (Join-Path (Split-Path $installed) 'MenuWidget.Updater.exe') -Destination $runner -Force
    $hash=(Get-FileHash -LiteralPath $Installer).Hash
    $arguments='"'+$Installer+'" "'+$hash+'" '+$old.Id+' '+$oldTicks+' "'+$installed+'"'
    $updater=Start-Process -FilePath $runner -ArgumentList $arguments -PassThru -WindowStyle Hidden
    ExitApp
    Check 'old-app-exits-for-update' ($old.WaitForExit(10000))
    Check 'external-updater-exits-successfully' ($updater.WaitForExit(60000) -and $updater.ExitCode -eq 0)
    Check 'new-version-installed' ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path (Split-Path $installed) 'MenuWidget.dll')).FileVersion -eq ($ExpectedVersion+'.0'))
    $restarted=$null
    for($attempt=0;$attempt -lt 20;$attempt++) { $restarted=Get-Process MenuWidget -ErrorAction SilentlyContinue | Where-Object Path -eq $installed | Select-Object -First 1; if($restarted){break}; Start-Sleep -Milliseconds 250 }
    Check 'updater-restarts-installed-app' ($null -ne $restarted)
    if(!$restarted.WaitForInputIdle(10000)){throw 'Restarted app did not become ready.'}
    ExitApp
    Check 'new-app-exits-cleanly' ($restarted.WaitForExit(10000))
    }
    Check 'profile-preserved-after-upgrade' (Preserved $before)
    Check 'desktop-and-start-shortcuts' ((Test-Path -LiteralPath $desktopLink) -and (Test-Path -LiteralPath $startLink))
    $diagnosticReport=Join-Path $acceptance 'integration-check.json'
    $diag=Start-Process -FilePath $installed -ArgumentList ('--diagnostics-integrations "'+$diagnosticReport+'"') -PassThru -WindowStyle Hidden
    Check 'production-integrations-register' ($diag.WaitForExit(15000) -and $diag.ExitCode -eq 0 -and (Test-Path -LiteralPath $diagnosticReport))
    $tasks=@(Get-ScheduledTask -TaskName ('MenuWidget.*.'+$sid) -ErrorAction SilentlyContinue)
    Check 'weekly-snooze-and-startup-task-paths' ($tasks.Count -eq 3 -and @($tasks | Where-Object { $_.Actions.Execute -ne $installed }).Count -eq 0)
    $startup=Get-ScheduledTask -TaskName ('MenuWidget.Startup.'+$sid)
    Check 'autostart-points-to-installed-app' ($startup.Actions.Execute -eq $installed -and $startup.Actions.Arguments -eq '--background')
    Check 'uninstall-success' ((Msi '/x' $Installer ('uninstall-'+$ExpectedVersion)) -eq 0)
    Check 'uninstall-removes-executable-and-shortcuts' (!(Test-Path -LiteralPath $installed) -and !(Test-Path -LiteralPath $desktopLink) -and !(Test-Path -LiteralPath $startLink))
    Check 'uninstall-removes-tasks' (@(Get-ScheduledTask -TaskName ('MenuWidget.*.'+$sid) -ErrorAction SilentlyContinue).Count -eq 0)
    Check 'uninstall-removes-autostart' (-not ((Get-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').GetValueNames() -contains 'MenuWidget'))
    Check 'uninstall-keeps-profile' (Preserved $before)
    Check 'reinstall-success' ((Msi '/i' $Installer ('reinstall-'+$ExpectedVersion)) -eq 0)
    Check 'reinstalled-app-loads-preserved-menus' ((Health) -eq 0)
    Check 'reinstall-keeps-profile' (Preserved $before)
} finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $acceptance 'results.json') -Encoding utf8NoBOM
}

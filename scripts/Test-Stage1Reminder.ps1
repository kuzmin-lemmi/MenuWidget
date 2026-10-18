param([int]$DelaySeconds=10)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $env:LOCALAPPDATA 'Programs\MenuWidgetStage1Probe\MenuWidget.WindowsProbe.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Install the isolated stage 1 probe first.'}
$service=New-Object -ComObject 'Schedule.Service'; $service.Connect()
$definition=$service.NewTask(0)
$definition.RegistrationInfo.Description='Temporary MenuWidget stage 1 delivery test; not the production reminder.'
$definition.Principal.LogonType=3; $definition.Principal.RunLevel=0
$definition.Settings.Enabled=$true; $definition.Settings.StartWhenAvailable=$true
$definition.Settings.DisallowStartIfOnBatteries=$false; $definition.Settings.StopIfGoingOnBatteries=$false
$definition.Settings.ExecutionTimeLimit='PT3M'
$trigger=$definition.Triggers.Create(1); $trigger.StartBoundary=(Get-Date).AddSeconds($DelaySeconds).ToString('yyyy-MM-ddTHH:mm:ss')
$action=$definition.Actions.Create(0); $action.Path=$exe; $action.Arguments='--notify'
$name='MenuWidget.Stage1Probe.'+[guid]::NewGuid().ToString('N')
$null=$service.GetFolder('\').RegisterTaskDefinition($name,$definition,6,$null,$null,3)
@{taskName=$name;due=$trigger.StartBoundary;exe=$exe} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts\stage1\reminder-task.json') -Encoding UTF8
Write-Output ('Registered isolated one-time test: '+$name+' at '+$trigger.StartBoundary)
Write-Output 'After observing the notification and activation, remove this exact task with the cleanup script.'

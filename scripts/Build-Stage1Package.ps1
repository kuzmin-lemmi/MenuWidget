param([ValidatePattern('^0\.0\.[123]$')][string]$Version='0.0.1', [string]$DotnetPath)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (!$DotnetPath) { $local=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'; $DotnetPath=if(Test-Path -LiteralPath $local){$local}else{(Get-Command dotnet).Source} }
    $env:DOTNET_CLI_HOME=Join-Path $repoRoot '.tools\cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $payload=Join-Path $repoRoot ('artifacts\stage1\payload-'+$Version)
    & $DotnetPath publish experiments/MenuWidget.WindowsProbe -c Release -r win-x64 --self-contained true -p:Version=$Version -o $payload
    if($LASTEXITCODE -ne 0){throw 'Notification probe publish failed.'}
    $wix=Join-Path $repoRoot '.tools\wix\wix.exe'
    if(!(Test-Path -LiteralPath $wix)) {
        & $DotnetPath tool install wix --version 4.0.6 --tool-path .tools/wix --configfile NuGet.Config
        if($LASTEXITCODE -ne 0){throw 'WiX installation failed.'}
    }
    $output=Join-Path $repoRoot 'artifacts\stage1'
    $source=Join-Path $output ('probe-'+$Version+'.wxs')
    $document=New-Object Xml.XmlDocument
    $ns='http://wixtoolset.org/schemas/v4/wxs'
    function Element($parent,[string]$name,[hashtable]$attributes=@{}) {
        $e=$document.CreateElement($name,$ns); foreach($key in $attributes.Keys){$e.SetAttribute($key,[string]$attributes[$key])}; $null=$parent.AppendChild($e); return $e
    }
    $wixRoot=Element $document 'Wix'
    $package=Element $wixRoot 'Package' @{Name='MenuWidget Stage 1 Probe'; Manufacturer='MenuWidget'; Version=$Version; UpgradeCode='C3C95874-5A6A-4556-A888-C4EA37CD0394'; Scope='perUser'; Language='1033'}
    $null=Element $package 'MajorUpgrade' @{Schedule='afterInstallInitialize'; DowngradeErrorMessage='A newer MenuWidget Stage 1 Probe is installed.'}
    $null=Element $package 'MediaTemplate' @{EmbedCab='yes'}
    $null=Element $package 'Property' @{Id='STAGE1FAIL'; Value='0'; Secure='yes'}
    $null=Element $package 'CustomAction' @{Id='FailForProbe'; Error='Intentional stage 1 rollback test.'}
    $sequence=Element $package 'InstallExecuteSequence'
    $null=Element $sequence 'Custom' @{Action='FailForProbe'; After='InstallFiles'; Condition='STAGE1FAIL = 1 AND NOT REMOVE'}
    $localAppData=Element $package 'StandardDirectory' @{Id='LocalAppDataFolder'}
    $programs=Element $localAppData 'Directory' @{Id='ProbePrograms'; Name='Programs'}
    $install=Element $programs 'Directory' @{Id='INSTALLFOLDER'; Name='MenuWidgetStage1Probe'}
    $feature=Element $package 'Feature' @{Id='Main'; Title='Stage 1 isolated technical probe'; Level='1'}
    $directories=@{''=$install}
    foreach($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
        $relative=[IO.Path]::GetRelativePath($payload,$file.FullName)
        $folder=[IO.Path]::GetDirectoryName($relative)
        $current=''; $parent=$install
        if($folder) {
            foreach($part in $folder.Split([IO.Path]::DirectorySeparatorChar)) {
                $current=if($current){$current+'\'+$part}else{$part}
                if(!$directories.ContainsKey($current)) { $directories[$current]=Element $parent 'Directory' @{Id=('D'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($current))).Substring(0,24)); Name=$part} }
                $parent=$directories[$current]
            }
        }
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('MenuWidget.Stage1Probe\'+$relative)))
        $id=$hash.Substring(0,24)
        # Per-user components use HKCU keypaths. File hashes remain MSI-managed.
        $component=Element $parent 'Component' @{Id=('C'+$id); Guid=([guid]::ParseExact($hash.Substring(0,32),'N').ToString())}
        $null=Element $component 'RegistryValue' @{Root='HKCU'; Key=('Software\MenuWidget\Stage1Probe\Components'); Name=$id; Type='integer'; Value='1'; KeyPath='yes'}
        $null=Element $component 'File' @{Id=('F'+$id); Source=$file.FullName; Name=$file.Name}
        $null=Element $feature 'ComponentRef' @{Id=('C'+$id)}
    }
    $document.Save($source)
    & $wix build $source -arch x64 -o (Join-Path $output ('MenuWidget-Stage1-'+$Version+'.msi'))
    if($LASTEXITCODE -ne 0){throw 'MSI build failed.'}
} finally {Pop-Location}

param([ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version='0.3.2', [string]$DotnetPath)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (!$DotnetPath) { $local=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'; $DotnetPath=if(Test-Path -LiteralPath $local){$local}else{(Get-Command dotnet).Source} }
    $env:DOTNET_CLI_HOME=Join-Path $repoRoot '.tools\cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $payload=Join-Path $repoRoot ('artifacts\release-payload-'+$Version)
    & (Join-Path $PSScriptRoot 'Build.ps1') -DotnetPath $DotnetPath -PublishApp -Version $Version -OutputDirectory $payload
    if($LASTEXITCODE -ne 0){throw 'App publish failed.'}
    $wix=Join-Path $repoRoot '.tools\wix\wix.exe'
    if(!(Test-Path -LiteralPath $wix)) {
        & $DotnetPath tool install wix --version 4.0.6 --tool-path .tools/wix --configfile NuGet.Config
        if($LASTEXITCODE -ne 0){throw 'WiX installation failed.'}
    }
    $output=Join-Path $repoRoot 'artifacts\release'
    $source=Join-Path $output ('MenuWidget-'+$Version+'.wxs')
    $null=New-Item -ItemType Directory -Path $output -Force
    $document=New-Object Xml.XmlDocument
    $ns='http://wixtoolset.org/schemas/v4/wxs'
    function Element($parent,[string]$name,[hashtable]$attributes=@{}) {
        $e=$document.CreateElement($name,$ns); foreach($key in $attributes.Keys){$e.SetAttribute($key,[string]$attributes[$key])}; $null=$parent.AppendChild($e); return $e
    }
    $wixRoot=Element $document 'Wix'
    $package=Element $wixRoot 'Package' @{Name='MenuWidget'; Manufacturer='MenuWidget'; Version=$Version; UpgradeCode='B89145BC-BEB4-4A0B-9842-B7458CC760EB'; Scope='perUser'; Language='1033'}
    $null=Element $package 'MajorUpgrade' @{Schedule='afterInstallInitialize'; DowngradeErrorMessage='A newer MenuWidget is already installed.'}
    $null=Element $package 'MediaTemplate' @{EmbedCab='yes'}
    $localAppData=Element $package 'StandardDirectory' @{Id='LocalAppDataFolder'}
    $programs=Element $localAppData 'Directory' @{Id='AppPrograms'; Name='Programs'}
    $install=Element $programs 'Directory' @{Id='INSTALLFOLDER'; Name='MenuWidget'}
    $feature=Element $package 'Feature' @{Id='Main'; Title='MenuWidget'; Level='1'}
    $directories=@{''=$install}
    foreach($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object Extension -ne '.pdb' | Sort-Object FullName) {
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
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('MenuWidget.Release\'+$relative)))
        $id=$hash.Substring(0,24)
        # Per-user components use HKCU keypaths. File hashes remain MSI-managed.
        $component=Element $parent 'Component' @{Id=('C'+$id); Guid=([guid]::ParseExact($hash.Substring(0,32),'N').ToString())}
        $null=Element $component 'RegistryValue' @{Root='HKCU'; Key=('Software\MenuWidget\Installer\Components'); Name=$id; Type='integer'; Value='1'; KeyPath='yes'}
        $null=Element $component 'File' @{Id=('F'+$id); Source=$file.FullName; Name=$file.Name}
        $null=Element $feature 'ComponentRef' @{Id=('C'+$id)}
        if($relative -eq 'MenuWidget.exe') { $mainFile='F'+$id }

    }
    if(!$mainFile){throw 'App executable missing.'}
    $desktop=Element $package 'StandardDirectory' @{Id='DesktopFolder'}
    $startMenu=Element $package 'StandardDirectory' @{Id='ProgramMenuFolder'}
    foreach($pair in @(@{Parent=$desktop;Id='DesktopShortcut'},@{Parent=$startMenu;Id='StartShortcut'})) {
        $component=Element $pair.Parent 'Component' @{Id=$pair.Id; Guid='*'}
        $null=Element $component 'RegistryValue' @{Root='HKCU';Key='Software\MenuWidget\Installer';Name=$pair.Id;Type='integer';Value='1';KeyPath='yes'}
        $null=Element $component 'Shortcut' @{Id=($pair.Id+'Link');Name='MenuWidget';Target='[INSTALLFOLDER]MenuWidget.exe';WorkingDirectory='INSTALLFOLDER'}
        $null=Element $feature 'ComponentRef' @{Id=$pair.Id}
    }
    $null=Element $package 'CustomAction' @{Id='RemoveUserIntegration';FileRef=$mainFile;ExeCommand='--uninstall-integrations [UserSID]';Return='ignore';Execute='immediate';Impersonate='yes'}
    $sequence=Element $package 'InstallExecuteSequence'
    $null=Element $sequence 'Custom' @{Action='RemoveUserIntegration';Before='RemoveFiles';Condition='REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE'}
    $document.Save($source)
    $installer=Join-Path $output 'MenuWidget-Setup-x64.msi'
    & $wix build $source -arch x64 -o $installer
    if($LASTEXITCODE -ne 0){throw 'MSI build failed.'}
    $hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    @{schemaVersion=1;version=$Version;architecture='x64';minimumWindowsBuild=22000;assetName='MenuWidget-Setup-x64.msi';size=(Get-Item -LiteralPath $installer).Length;sha256=$hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'update-manifest.json') -Encoding utf8NoBOM
    ($hash+'  MenuWidget-Setup-x64.msi') | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
    Write-Host ('Release files: '+$output)
} finally {Pop-Location}

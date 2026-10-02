[CmdletBinding()]
param([string]$SourceRepository = (Split-Path $PSScriptRoot -Parent),
      [ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
      [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
      [string]$OutputDirectory,
      [string]$BuildRoot = 'C:\build\ai-desktop-setup')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$SourceCommit) { $SourceCommit = (git -C $SourceRepository rev-parse HEAD).Trim(); if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit' } }
if ($OutputDirectory -and (!$Version -or (Test-Path $OutputDirectory))) { throw 'Staged release requires version and unused output directory' }
if (Test-Path $BuildRoot) { throw 'Build root must be fresh; existing outputs are never reused.' }
New-Item -ItemType Directory -Path $BuildRoot | Out-Null
$cohort = Join-Path $BuildRoot 'cohort'
$source = Join-Path $cohort 'source'
$logs = Join-Path $cohort 'logs'
$tools = Join-Path $BuildRoot 'tools'
New-Item -ItemType Directory -Path $cohort,$logs,$tools | Out-Null
$python = (Get-Command python).Source
$dotnet = (Get-Command dotnet).Source
function Run([string]$Name, [string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $Name" }
}
Run 'stage' $python @((Join-Path $SourceRepository 'build/candidate.py'),'stage','--source',$SourceRepository,'--destination',$source,'--commit',$SourceCommit)
$lock = Get-Content (Join-Path $source 'build/toolchain.json') -Raw | ConvertFrom-Json
$env:DOTNET_CLI_HOME = Join-Path $BuildRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $BuildRoot 'packages'
$env:TEMP = Join-Path $cohort 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:TEMP | Out-Null
Set-Location $source
if ((& $dotnet --version).Trim() -ne $lock.dotnet_sdk) { throw 'SDK version mismatch' }
if ((& $python -c 'import platform; print(platform.python_version())').Trim() -ne $lock.python) { throw 'Python version mismatch' }
$nsisZip = Join-Path $tools 'nsis.zip'
Invoke-WebRequest $lock.nsis.url -OutFile $nsisZip
if ((Get-FileHash $nsisZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.nsis.sha256) { throw 'NSIS archive digest mismatch' }
Expand-Archive -Path $nsisZip -DestinationPath $tools
$nsis = Join-Path $tools 'nsis-3.12\makensis.exe'
$env:AI_SETUP_MAKENSIS = $nsis
if ((& $nsis /VERSION).Trim() -ne 'v3.12') { throw 'NSIS version mismatch' }
$attachments = Join-Path $cohort 'attachments'
New-Item -ItemType Directory -Path $attachments | Out-Null
$sourceArchive = Join-Path $attachments 'nsis-3.12-src.tar.bz2'
Invoke-WebRequest $lock.nsis.source_url -OutFile $sourceArchive
if ((Get-FileHash $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.nsis.source_sha256) { throw 'NSIS source digest mismatch' }
[xml]$project = Get-Content (Join-Path $source 'App/AI.Desktop.Setup.csproj')
$projectVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($Version -and $Version -cne $projectVersion) { throw 'Tag version differs from project version' }
$Version = $projectVersion
$results = Join-Path $cohort 'results'
$output = Join-Path $cohort 'candidate'
New-Item -ItemType Directory -Path $results,$output | Out-Null
Run 'python-tests' $python @('-m','unittest','discover','-s','build','-p','test_*.py')
foreach ($framework in @('net10.0','net48')) {
    Run ('tests-'+$framework) $dotnet @('test','Tests/AI.Desktop.Setup.Tests.csproj','-c','Release','-f',$framework,'--nologo','-p:RestoreLockedMode=true','--logger',('trx;LogFileName='+$framework+'.trx'),'--results-directory',$results)
}
Run 'test-counts' $python @('build/candidate.py','trx',(Join-Path $results 'net10.0.trx'),(Join-Path $results 'net48.trx'))
foreach ($target in @(@('x64','net48'),@('ARM64','net481'))) {
    $arch = $target[0].ToLowerInvariant()
    Run ('publish-'+$arch) $dotnet @('publish','App/AI.Desktop.Setup.csproj','-c','Release',('-p:Platform='+$target[0]),'-f',$target[1],'-p:RestoreLockedMode=true',('-p:Version='+$Version),('-p:InformationalVersion='+$Version),'-p:Product=AI Desktop Setup','-p:IncludeSourceRevisionInInformationalVersion=false','--nologo','-o',(Join-Path $output ('publish-'+$arch)))
    # Retain the per-target graph before the next target changes project obj files.
    Copy-Item -Recurse (Join-Path $source 'App/obj') (Join-Path $cohort ('app-obj-'+$arch))
}
if ($OutputDirectory) {
    Run 'stage-payloads' $python @('scripts/package-windows.py',$output,'--makensis',$nsis,'--payload-only')
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    foreach ($arch in @('x64','arm64')) { Copy-Item -Recurse (Join-Path $output ('payload-'+$arch)) (Join-Path $OutputDirectory $arch) }
} else {
    Run 'package' $python @('scripts/package-windows.py',$output,'--makensis',$nsis)
}
$receipt = [ordered]@{
    schema_version=1; source_commit=$SourceCommit; source_repository=$env:GITHUB_REPOSITORY;
    workflow_run_id=$env:GITHUB_RUN_ID; workflow_run_attempt=$env:GITHUB_RUN_ATTEMPT;
    status='unsigned-preview'; signed=$false; native_acceptance='unverified';
    sdk=$lock.dotnet_sdk; python=$lock.python; nsis=$lock.nsis;
    runner_image=$env:ImageOS; runner_image_version=$env:ImageVersion;
    windows=[Environment]::OSVersion.Version.ToString();
    framework_release=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release;
    excluded_inputs=@('fresh package input store','SDK distribution','NSIS tool distribution','CLI home');
    files=@()
}
# Final inventory runs after log writers close; cohort.json is its explicit envelope.
$inventory = Join-Path $BuildRoot 'inventory.json'
& $python build/candidate.py inventory --root $cohort --output $inventory
if ($LASTEXITCODE -ne 0) { throw 'Inventory failed' }
$receipt.files = @(Get-Content $inventory -Raw | ConvertFrom-Json)
$receipt | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $cohort 'cohort.json') -Encoding utf8
Write-Output 'Unsigned preview cohort ready for independent source and artifact audit.'

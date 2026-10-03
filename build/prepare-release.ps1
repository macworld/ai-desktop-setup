# Install only into the disposable hosted runner's temporary directory.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/download-toolchain.ps1"
$lock=Get-Content "$PSScriptRoot/toolchain.json" -Raw | ConvertFrom-Json
$tools=Join-Path $env:RUNNER_TEMP ('release-tools-'+[guid]::NewGuid())
New-Item -ItemType Directory $tools | Out-Null
foreach ($entry in @(@('nsis',$lock.nsis),@('pester',$lock.pester))) {
    $name=$entry[0];$pin=$entry[1];$archive=Join-Path $tools ($name+'.zip')
    Get-PinnedToolchainDownload -Name "$name archive" -Uri $pin.url -OutFile $archive -Sha256 $pin.sha256
    Expand-Archive $archive (Join-Path $tools $name)
}
$nsis=Join-Path $tools 'nsis/nsis-3.12'
$nsis | Out-File $env:GITHUB_PATH -Append -Encoding utf8
"AI_SETUP_MAKENSIS=$nsis/makensis.exe" | Out-File $env:GITHUB_ENV -Append -Encoding utf8
"AI_SETUP_PESTER=$tools/pester/Pester.psd1" | Out-File $env:GITHUB_ENV -Append -Encoding utf8
$signtool=Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/signtool.exe" | Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (!$signtool) { throw 'Windows SDK SignTool is required' }
$signtool.Directory.FullName | Out-File $env:GITHUB_PATH -Append -Encoding utf8

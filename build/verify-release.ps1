[CmdletBinding()]
param(
    [ValidateSet('Inner','Final')][string]$Stage = 'Final',
    [string]$ReleaseDirectory, [string]$UnsignedReleaseDirectory,
    [string]$PayloadDirectory, [string]$UnsignedPayloadDirectory,
    [string]$SourceCommit, [string]$Version,
    [string]$SignerCertificateSha256 = $env:AI_SETUP_CERT_SHA256,
    [string]$WorkflowRunId = $env:GITHUB_RUN_ID,
    [string]$SignTool = 'signtool.exe', [string]$Output
)

function Assert-NativeExit([int]$Code) {
    if ($Code -ne 0) { throw "Native trust verification failed or warned: exit $Code" }
}
function Assert-Product([string]$Name, [string]$ActualVersion, [string]$ExpectedVersion) {
    if ($Name -cne 'AI Desktop Setup' -or $ActualVersion -cne $ExpectedVersion) { throw 'Signed product metadata mismatch' }
}
function Test-NativeSignature([string]$Path, [string]$CertificateSha256, [string]$ExpectedVersion, [string]$Tool) {
    if (!$IsWindows) { throw 'Native signature acceptance requires Windows' }
    if ($CertificateSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Expected certificate SHA256 is required' }
    $before=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    & $Tool verify /pa /all /v /tw $Path | Out-Host
    Assert-NativeExit $LASTEXITCODE
    $signature=Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or !$signature.SignerCertificate -or !$signature.TimeStamperCertificate) {
        throw 'Embedded trusted signature and timestamp required'
    }
    $identity=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData)).ToLowerInvariant()
    if ($identity -cne $CertificateSha256) { throw 'Unexpected signing certificate identity' }
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    Assert-Product $info.ProductName $info.ProductVersion $ExpectedVersion
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $before) { throw 'File changed during native verification' }
    return $before
}
function Invoke-ReleaseVerification {
    $ErrorActionPreference='Stop'
    Set-StrictMode -Version Latest
    if (!$IsWindows) { throw 'Release verification requires Windows' }
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $Version -notmatch '^\d+\.\d+\.\d+$' -or $WorkflowRunId -notmatch '^[1-9][0-9]*$') { throw 'Exact commit, version and original run required' }
    if (!$Output -or (Test-Path -LiteralPath $Output)) { throw 'Fresh verification output required' }
    $work=Join-Path ([IO.Path]::GetTempPath()) ('release-verification-'+[guid]::NewGuid())
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        if ($Stage -eq 'Inner') {
            $structure=Join-Path $work 'inner.json'
            & python "$PSScriptRoot/release-structure.py" inner --unsigned $UnsignedPayloadDirectory --signed $PayloadDirectory --output $structure
            Assert-NativeExit $LASTEXITCODE
            $payloads=Get-Content $structure -Raw | ConvertFrom-Json -AsHashtable
            foreach ($arch in @('x64','arm64')) {
                foreach ($name in @('AI.Desktop.Setup.exe','AI.Desktop.Setup.Core.dll')) {
                    $null=Test-NativeSignature (Join-Path $PayloadDirectory "$arch/$name") $SignerCertificateSha256 $Version $SignTool
                }
            }
            $report=[ordered]@{schema_version=1; source_commit=$SourceCommit; workflow_run_id=$WorkflowRunId; payloads=$payloads}
        } else {
            $expected=@("AI-Desktop-Setup-$Version-x64.exe","AI-Desktop-Setup-$Version-arm64.exe")
            $actual=@(Get-ChildItem -LiteralPath $ReleaseDirectory -File | ForEach-Object Name)
            if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'Exact two final EXEs required' }
            $artifacts=@()
            foreach ($arch in @('x64','arm64')) {
                $name="AI-Desktop-Setup-$Version-$arch.exe"
                $file=Join-Path $ReleaseDirectory $name
                $hash=Test-NativeSignature $file $SignerCertificateSha256 $Version $SignTool
                $extract=Join-Path $work $arch; $structure=Join-Path $work "$arch.json"
                & python "$PSScriptRoot/release-structure.py" final --unsigned (Join-Path $UnsignedReleaseDirectory $name) --signed $file --payload (Join-Path $PayloadDirectory $arch) --architecture $arch --extract $extract --output $structure
                Assert-NativeExit $LASTEXITCODE
                $decoded=Get-Content $structure -Raw | ConvertFrom-Json -AsHashtable
                foreach ($entry in $decoded.inner_files) {
                    $entry.signature_valid=$false
                    if ($entry.ownership -eq 'owned') {
                        $innerHash=Test-NativeSignature (Join-Path $extract $entry.path) $SignerCertificateSha256 $Version $SignTool
                        if ($innerHash -cne $entry.sha256) { throw 'Extracted inner digest changed' }
                        $entry.signature_valid=$true
                    }
                }
                if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Final outer changed' }
                $artifacts+=@{name=$name;sha256=$hash;product_name='AI Desktop Setup';product_version=$Version;architecture=$arch;outer_signature_valid=$true;inner_signatures_valid=$true;timestamp_valid=$true;inner_files=$decoded.inner_files;tooling_files=$decoded.tooling_files}
            }
            $report=[ordered]@{schema_version=1;signer_certificate_sha256=$SignerCertificateSha256;source_commit=$SourceCommit;workflow_run_id=$WorkflowRunId;artifacts=$artifacts}
        }
        $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
    } finally { Remove-Item -LiteralPath $work -Recurse -Force }
}
if ($MyInvocation.InvocationName -ne '.') { Invoke-ReleaseVerification }

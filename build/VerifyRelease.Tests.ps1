BeforeAll {
    . "$PSScriptRoot/verify-release.ps1"
}
Describe 'Native release policy (policy tests are not trust evidence)' {
    It 'rejects warning and failure exit codes including missing timestamp warning' {
        { Assert-NativeExit 2 } | Should -Throw
        { Assert-NativeExit 1 } | Should -Throw
        { Assert-NativeExit 0 } | Should -Not -Throw
    }
    It 'rejects wrong product or architecture identity' {
        { Assert-Product 'Other' '1.2.3' '1.2.3' } | Should -Throw
        { Assert-Product 'AI Desktop Setup' '1.2.4' '1.2.3' } | Should -Throw
        { Assert-Product 'AI Desktop Setup' '1.2.3' '1.2.3' } | Should -Not -Throw
    }
    It 'refuses native verification on unsupported hosts' -Skip:$IsWindows {
        { Test-NativeSignature "$PSHOME/pwsh" ('a'*64) '1.2.3' 'signtool' } | Should -Throw '*Windows*'
    }
}
Describe 'Actual signed release candidate' {
    It 'verifies both signed architectures and all extracted owned binaries' -Skip:(!$IsWindows -or !$env:AI_SETUP_NATIVE_CANDIDATE) {
        & "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $env:AI_SETUP_NATIVE_CANDIDATE -UnsignedReleaseDirectory $env:AI_SETUP_UNSIGNED_OUTER -PayloadDirectory $env:AI_SETUP_SIGNED_PAYLOAD -SourceCommit $env:AI_SETUP_SOURCE_COMMIT -Version $env:AI_SETUP_VERSION -SignerCertificateSha256 $env:AI_SETUP_CERT_SHA256 -WorkflowRunId $env:AI_SETUP_RUN_ID -Output "$TestDrive/verification.json"
        $report=Get-Content "$TestDrive/verification.json" -Raw | ConvertFrom-Json
        $report.artifacts.Count | Should -Be 2
        foreach ($a in $report.artifacts) { $a.outer_signature_valid | Should -BeTrue; $a.inner_signatures_valid | Should -BeTrue; $a.timestamp_valid | Should -BeTrue }
    }
}

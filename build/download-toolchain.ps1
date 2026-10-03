function Get-PinnedToolchainDownload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][uri]$Uri,
          [Parameter(Mandatory)][string]$OutFile,
          [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$Sha256)

    # SourceForge treats PowerShell's default Mozilla-style UA as a browser and
    # serves a download page. Identify this request as a command-line downloader.
    $response = Invoke-WebRequest -Uri $Uri -OutFile $OutFile -PassThru -UserAgent 'AI-Desktop-Setup-Toolchain/1'
    $actual = (Get-FileHash $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $contentType = ($response.Headers['Content-Type'] -join ';').Split(';')[0].Trim().ToLowerInvariant()
    if ($actual -cne $Sha256 -or $contentType -eq 'text/html') {
        $length = (Get-Item $OutFile).Length
        # Redirect query strings may contain transient download credentials.
        $finalUrl = $response.BaseResponse.RequestMessage.RequestUri.GetLeftPart([UriPartial]::Path)
        Remove-Item -LiteralPath $OutFile
        throw "$Name download rejected: expected-sha256=$Sha256; actual-sha256=$actual; status=$($response.StatusCode); content-type=$contentType; bytes=$length; final-url=$finalUrl"
    }
}

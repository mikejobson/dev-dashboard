<#
.SYNOPSIS
  Installs the DevDash agent on Windows and registers it as a scheduled task that starts when you
  log on and runs as you, with no console window. No administrator rights needed.

.DESCRIPTION
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/mikejobson/dev-dashboard/main/scripts/install.ps1))) --hub-url https://devdash.<your-tailnet>.ts.net --api-key <key>

  Every argument goes to `devdash-agent install` (see `devdash-agent install --help`).
  Run it again with no arguments to upgrade: the saved configuration is kept.

  Environment:
    DEVDASH_VERSION      release tag to install, e.g. v0.2.0 (default: the latest release)
    DEVDASH_INSTALL_DIR  where the binaries go (default: %LOCALAPPDATA%\DevDash\bin)
    DEVDASH_REPO         GitHub repository to download from (default: mikejobson/dev-dashboard)
    DEVDASH_BASE_URL     download from this URL prefix instead of GitHub Releases (mirrors, testing)
    DEVDASH_NO_SERVICE   set to 1 to only download the binaries and skip `devdash-agent install`
    DEVDASH_ALLOW_UNSIGNED  set to 1 to install a release that has no signature (v0.1.0, from before
                         releases were signed). The checksum is still checked.

  The download is checked twice: its SHA-256 against the release's checksums.txt, and checksums.txt
  against the release signature (once this script carries the release public key). After this first
  install the agent can update itself: `devdash-agent update`, or the Update button in the dashboard.
  `--updates off|notify|auto` sets how (default notify).
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# The public half of the key that signs releases. scripts/release-signing-key.sh writes it here and into
# the agent's source; the three copies must match.
# devdash-release-public-key:begin
$releasePublicKey = @'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXxcXOJGHjR/atn+NdoPztGKT+yXc
k0aXqWNBvb+qZtPvX3TI9qA4SAugIBET7+aTgkiY5g/1pS+Wq4C1LdERDg==
-----END PUBLIC KEY-----
'@
# devdash-release-public-key:end

# Verifies an ECDSA P-256 / SHA-256 signature (DER, as openssl writes it) with Windows' own crypto,
# so it needs neither openssl nor a newer PowerShell: this runs on Windows PowerShell 5.1 as well.
function Test-ReleaseSignature([string] $PublicKeyPem, [byte[]] $Data, [byte[]] $Signature) {
    $base64 = (($PublicKeyPem -split "`r?`n") | Where-Object { $_ -and $_ -notmatch '^-----' }) -join ''
    $spki = [Convert]::FromBase64String($base64)
    # A P-256 SubjectPublicKeyInfo is 91 bytes: a 26-byte header, then 0x04, X (32 bytes) and Y (32 bytes).
    if ($spki.Length -ne 91 -or $spki[26] -ne 4) { throw 'The release public key in this script is not a P-256 key.' }

    # BCRYPT_ECCKEY_BLOB for a public key: the magic "ECS1", the key length (32), then X and Y.
    $blob = New-Object byte[] 72
    [byte[]] $header = 0x45, 0x43, 0x53, 0x31, 0x20, 0, 0, 0
    [Array]::Copy($header, 0, $blob, 0, 8)
    [Array]::Copy($spki, 27, $blob, 8, 64)

    # The signature is DER: SEQUENCE { INTEGER r, INTEGER s }. Windows wants r and s as 32 bytes each.
    if ($Signature.Length -lt 8 -or $Signature[0] -ne 0x30) { return $false }
    $index = 2
    if ($Signature[1] -band 0x80) { $index = 2 + ($Signature[1] -band 0x7f) }
    $raw = New-Object byte[] 64
    foreach ($part in 0, 1) {
        if ($index + 2 -gt $Signature.Length -or $Signature[$index] -ne 0x02) { return $false }
        $length = [int] $Signature[$index + 1]
        $start = $index + 2
        if ($length -lt 1 -or $start + $length -gt $Signature.Length) { return $false }
        $index = $start + $length
        while ($length -gt 32 -and $Signature[$start] -eq 0) { $start++; $length-- }
        if ($length -gt 32) { return $false }
        [Array]::Copy($Signature, $start, $raw, $part * 32 + (32 - $length), $length)
    }

    $key = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $ecdsa = New-Object System.Security.Cryptography.ECDsaCng -ArgumentList $key
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return $ecdsa.VerifyHash($sha.ComputeHash($Data), $raw)
    }
    finally {
        $sha.Dispose(); $ecdsa.Dispose(); $key.Dispose()
    }
}

function Get-Setting([string] $Name, [string] $Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { $Default } else { $value }
}

$repo = Get-Setting 'DEVDASH_REPO' 'mikejobson/dev-dashboard'
$version = Get-Setting 'DEVDASH_VERSION' 'latest'
$installDir = Get-Setting 'DEVDASH_INSTALL_DIR' (Join-Path $env:LOCALAPPDATA 'DevDash\bin')

# PROCESSOR_ARCHITEW6432 is set when a 32-bit or emulated shell runs on a 64-bit machine.
$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
switch ($arch) {
    'AMD64' { $rid = 'win-x64' }
    'ARM64' { $rid = 'win-arm64' }
    default { throw "Unsupported Windows architecture: $arch" }
}

$asset = "devdash-agent-$rid.zip"
$baseOverride = Get-Setting 'DEVDASH_BASE_URL' ''
$base = if ($baseOverride) { $baseOverride.TrimEnd('/') } elseif ($version -eq 'latest') { "https://github.com/$repo/releases/latest/download" } else { "https://github.com/$repo/releases/download/$version" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("devdash-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading $asset ($version) from $base"
    $zip = Join-Path $tmp $asset
    Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $zip
    $sums = Join-Path $tmp 'checksums.txt'
    Invoke-WebRequest -UseBasicParsing -Uri "$base/checksums.txt" -OutFile $sums

    # checksums.txt has one "<sha256>  <file name>" line per asset.
    $expected = $null
    foreach ($line in Get-Content -LiteralPath $sums) {
        $parts = $line -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].Trim().TrimStart('*') -eq $asset) { $expected = $parts[0].Trim().ToLowerInvariant() }
    }
    if (-not $expected) { throw "checksums.txt has no entry for $asset" }
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Checksum mismatch for ${asset}: expected $expected, got $actual" }
    Write-Host 'Checksum OK'

    # The checksum only proves the archive is the one checksums.txt describes. The signature proves
    # checksums.txt itself came out of the release workflow.
    if ($releasePublicKey -match 'PLACEHOLDER') {
        Write-Host 'Note: this installer carries no release key yet, so the release signature was not checked.'
    }
    else {
        $sig = Join-Path $tmp 'checksums.txt.sig'
        $haveSignature = $true
        try { Invoke-WebRequest -UseBasicParsing -Uri "$base/checksums.txt.sig" -OutFile $sig } catch { $haveSignature = $false }
        if (-not $haveSignature) {
            if ((Get-Setting 'DEVDASH_ALLOW_UNSIGNED' '0') -ne '1') {
                throw "This release has no signature ($base/checksums.txt.sig). Releases after v0.1.0 are signed; refusing to install. To install an unsigned release anyway, set DEVDASH_ALLOW_UNSIGNED=1."
            }
            Write-Host 'Warning: this release has no signature; continuing because DEVDASH_ALLOW_UNSIGNED=1.'
        }
        elseif (-not (Test-ReleaseSignature $releasePublicKey ([IO.File]::ReadAllBytes($sums)) ([IO.File]::ReadAllBytes($sig)))) {
            throw 'The signature on checksums.txt is NOT valid for the DevDash release key. Nothing was installed.'
        }
        else {
            Write-Host 'Signature OK'
        }
    }

    $unpacked = Join-Path $tmp 'unpacked'
    Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
    Get-ChildItem -LiteralPath $unpacked -File | Unblock-File

    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    $agent = Join-Path $installDir 'devdash-agent.exe'

    # Upgrading: stop the running service so its files can be replaced. Windows will not overwrite a
    # running .exe but does allow renaming it, which covers an agent started some other way.
    if (Test-Path -LiteralPath $agent) {
        try { & $agent stop 2>$null | Out-Null } catch { }
        Start-Sleep -Seconds 1
    }
    foreach ($name in 'devdash-agent.exe', 'devdash-agentw.exe') {
        $target = Join-Path $installDir $name
        $old = "$target.old"
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $target) {
            try { Remove-Item -LiteralPath $target -Force } catch { Rename-Item -LiteralPath $target -NewName ([IO.Path]::GetFileName($old)) -Force }
        }
        Move-Item -LiteralPath (Join-Path $unpacked $name) -Destination $target -Force
    }
    Write-Host "Installed $(& $agent --version) to $agent"
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# Put the folder on the user's PATH (new terminals pick it up).
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not $userPath) { $userPath = '' }
if (($userPath -split ';') -notcontains $installDir) {
    [Environment]::SetEnvironmentVariable('Path', (($userPath.TrimEnd(';') + ';' + $installDir).TrimStart(';')), 'User')
    Write-Host "Added $installDir to your user PATH; open a new terminal to use `devdash-agent` by name."
}

if ((Get-Setting 'DEVDASH_NO_SERVICE' '0') -eq '1') {
    Write-Host "DEVDASH_NO_SERVICE=1: skipped the service. Run: `"$agent`" install --hub-url <url> --api-key <key>"
    return
}

& $agent install @args
if ($LASTEXITCODE -ne 0) { throw "devdash-agent install failed with exit code $LASTEXITCODE" }

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
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

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

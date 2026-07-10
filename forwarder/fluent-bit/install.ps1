# install.ps1 - Silent installer for the Yagura Fluent Bit forwarder kit.
#
# Installs Fluent Bit from an MSI placed next to this script (or -MsiPath),
# deploys the pre-configured forwarding config + Lua filter, registers
# Fluent Bit as a Windows service (auto start) and starts it. When an older
# Fluent Bit is already installed and the kit carries a newer MSI, the engine
# is upgraded in place (see step 1 below).
#
# Designed for unattended push (Intune / SCCM / GPO startup script) and
# manual admin execution. ASCII-only on purpose: avoids PowerShell 5.1
# BOM/encoding traps across distribution channels.
#
# Two kit forms (ADR-0008):
#   - Static kit (this repo's forwarder/fluent-bit/): the conf template still
#     contains the @@YAGURA_HOST@@ placeholder, so -YaguraHost is required
#     and this script performs the substitution below.
#   - Generated kit (Yagura admin UI, /admin/forwarder-kit): the conf is
#     already substituted server-side before packaging, so it has no
#     placeholders left. -YaguraHost is not needed in that case; if passed
#     anyway it is ignored with a warning (the kit is already destination-
#     bound and re-substituting would silently discard the server's values).
#
# Collection endpoint architecture (ADR-0009 decision 7 / delegation #4): Fluent
# Bit officially provides win64 (x64) and winarm64 (ARM64) MSIs. This script
# auto-detects the local machine's processor architecture (see
# Get-LocalMsiFilenamePattern below) and only considers the MSI matching that
# architecture when auto-discovering an MSI next to the script -- this lets a
# single kit folder carry both a win64 and a winarm64 MSI side by side (e.g.
# for a mixed x64/ARM64 fleet distributed via the same Intune/SCCM/GPO
# package) without the wrong-architecture MSI ever being picked. Windows x86
# (32-bit OS) is not supported by this kit or by Yagura itself (ADR-0009
# decision 1); this script fails with a clear message on that architecture
# rather than silently doing nothing. Explicit -MsiPath bypasses this
# detection (the admin's explicit choice); msiexec itself refuses to install
# a wrong-architecture MSI (Windows Installer ERROR_INSTALL_PLATFORM_UNSUPPORTED,
# verified for the Yagura MSI itself -- see installer/README.md, ARM64 section).
#
# Detection order (see Get-LocalMsiFilenamePattern): the primary source is
# WMI/CIM (Win32_Processor.Architecture), which is answered by the native WMI
# service and therefore reflects the actual machine regardless of the calling
# process's own emulation state. The PROCESSOR_ARCHITECTURE /
# PROCESSOR_ARCHITEW6432 environment variables are only a fallback: they are
# known to be unreliable when an x64-emulated process runs on an ARM64 host
# (both report AMD64 -- the ARCHITEW6432 promotion only covers classic 32-bit
# WOW64, not x64-on-ARM64 emulation), which would misdetect an ARM64 machine
# as x64 (PR #222 review, 2026-07-10).
#
# Usage:
#   powershell -NoProfile -File install.ps1 -YaguraHost 192.0.2.10
#   powershell -NoProfile -File install.ps1 -YaguraHost 192.0.2.10 -YaguraPort 514 -Channels "System,Application"
#   powershell -NoProfile -File install.ps1 -YaguraHost 192.0.2.10 -Mode tcp   (see TCP framing caveat below)
#   powershell -NoProfile -File install.ps1                                   (pre-configured kit)
#
# -Mode (udp / tcp, default udp): udp is subject to IP fragmentation loss for
# events larger than the path MTU (silent, no error on the sending side -- see
# Issue #156). tcp avoids that, but Fluent Bit's out_syslog plugin does not
# support RFC 6587 octet-counting framing over TCP; it terminates every message
# with a single LF instead (verified against the out_syslog source, 2026-07-09).
# Yagura's TCP receiver treats embedded LF bytes in a multi-line Windows event
# body (e.g. Security audit "\r\n"-separated fields) as message boundaries too,
# so a tcp-mode event containing embedded newlines can arrive split into several
# records. See docs/guides/forward-windows-eventlog.md for the full trade-off.
#
# Exit codes: 0 = success, 1 = failure, 3010 = success but reboot required (from msiexec).

[CmdletBinding()]
param(
    # No [Parameter(Mandatory = $true)]: PowerShell does not apply validation
    # attributes (ValidatePattern) to an unspecified default value, so an
    # empty default coexists with ValidatePattern below. Whether a value is
    # actually required is decided at runtime (see step 2) based on whether
    # the conf template still has the @@YAGURA_HOST@@ placeholder.
    [ValidatePattern('^[A-Za-z0-9\.\-:]+$')]
    [string]$YaguraHost = "",

    [ValidateRange(1, 65535)]
    [int]$YaguraPort = 514,

    [ValidatePattern('^[A-Za-z0-9,\- ]+$')]
    [string]$Channels = "System,Application",

    # udp (default): simple, but IP-fragments (and can silently drop) events
    # larger than the path MTU. tcp: avoids that, at the cost of the LF-framing
    # caveat documented above and in the guide.
    [ValidateSet('udp', 'tcp')]
    [string]$Mode = "udp",

    [string]$MsiPath = "",

    [string]$ServiceName = "fluent-bit"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = "C:\ProgramData\fluent-bit-yagura"
$fluentBitExe = "C:\Program Files\fluent-bit\bin\fluent-bit.exe"
$confTemplate = Join-Path $scriptDir "fluent-bit-yagura.conf"
$luaTemplate = Join-Path $scriptDir "winevt-severity.lua"
$confTarget = Join-Path $configDir "fluent-bit-yagura.conf"

function Log([string]$msg) {
    Write-Host ("[fluent-bit-yagura] {0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg)
}

function Fail([string]$msg) {
    Log ("ERROR: " + $msg)
    exit 1
}

# Known collectible channels (Issue #155). Must stay in sync with
# ForwarderKitConstraints.KnownChannels (src/Yagura.Web/ForwarderKit/ForwarderKitConstraints.cs) --
# the generated kit (server-side, via ForwarderKitRequest.TryNormalizeChannels) already rejects
# unknown/empty channel values; this static kit previously only checked the character set
# (ValidatePattern above), so a typo'd or empty channel silently never collected. This mirrors the
# server-side normalization so both kit forms have the same defense and produce the same conf output
# for the same logical channel set.
$knownChannels = @("System", "Application", "Security")

function Get-NormalizedChannels([string]$raw) {
    $parts = $raw -split "," | ForEach-Object { $_.Trim() }

    if (($parts | Where-Object { $_ -eq "" }).Count -gt 0) {
        Fail ("-Channels contains an empty element (stray/trailing/doubled comma?): '" + $raw + "'")
    }

    # PowerShell's -notcontains/-contains use -eq, which is case-insensitive by default.
    $unknown = $parts | Where-Object { $knownChannels -notcontains $_ } | Select-Object -Unique
    if ($unknown.Count -gt 0) {
        Fail ("-Channels contains unknown channel(s): " + ($unknown -join ", ") +
              ". Known channels: " + ($knownChannels -join ", "))
    }

    # Normalize to $knownChannels order and de-duplicate case-insensitively, matching
    # ForwarderKitRequest.TryNormalizeChannels.
    $requestedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$parts, [System.StringComparer]::OrdinalIgnoreCase)
    $normalized = $knownChannels | Where-Object { $requestedSet.Contains($_) }
    return ($normalized -join ",")
}

# --- 0. Preconditions -------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Administrator privileges are required."
}
foreach ($f in @($confTemplate, $luaTemplate)) {
    if (-not (Test-Path $f)) { Fail ("Kit file not found: " + $f) }
}

$rebootRequired = $false

# --- 1. Install or upgrade Fluent Bit MSI ------------------------------------
# Fresh machine: the MSI next to the script (or -MsiPath) is required and installed.
# Already installed: compare the installed engine version (fluent-bit.exe
# ProductVersion) with the version in the kit MSI's file name, and run the MSI
# only when the kit carries a NEWER version (in-place major upgrade -- verified
# with 4.0.14 -> 5.0.8 on 2026-07-10: msiexec /i over the old version exits 0,
# replaces the engine and preserves the kit's service definition; install.ps1
# recreates and restarts the service afterwards anyway). Without this
# comparison, rerunning a new kit on a machine that already has an older
# Fluent Bit would silently keep the old engine and only update config/service
# (PR #206 review, Issue #129 acceptance criterion).
# If either version cannot be determined, be conservative: keep the existing
# engine untouched (previous behavior) and say so loudly.

function Get-ParsedVersion([string]$raw) {
    # Accept "5.0.8" (kit MSI file name) and "4.0.14.0" (exe ProductVersion) alike.
    if ([string]::IsNullOrEmpty($raw)) { return $null }
    $m = [regex]::Match($raw, '^\d+(\.\d+){1,3}')
    if (-not $m.Success) { return $null }
    return [version]$m.Value
}

function Get-LocalMsiFilenamePattern {
    # Detect the local machine's processor architecture and return the
    # matching Fluent Bit MSI filename pattern (ADR-0009 decision 7 /
    # delegation #4).
    #
    # Primary source: WMI/CIM Win32_Processor.Architecture. The query is
    # answered by the native WMI service (a separate native process), so the
    # value describes the actual machine and is not skewed by the calling
    # PowerShell host's own bitness or emulation state. Documented values
    # (Win32_Processor class, learn.microsoft.com/en-us/windows/win32/
    # cimwin32prov/win32-processor, checked 2026-07-10): x86 = 0, ARM = 5,
    # x64 = 9, ARM64 = 12. Live-verified on this x64 dev machine
    # (Architecture = 9, 2026-07-10); the ARM64-side value (12) is from the
    # official documentation -- not machine-verified here (no ARM64 hardware
    # in this dev environment).
    #
    # Fallback (CIM unavailable/failed): PROCESSOR_ARCHITECTURE, promoted by
    # PROCESSOR_ARCHITEW6432 when present. PROCESSOR_ARCHITECTURE reports the
    # architecture of the *current process*, not the OS; ARCHITEW6432 only
    # covers the classic 32-bit WOW64 case. Known limitation of this fallback:
    # an x64-emulated process on an ARM64 host sees AMD64 in both variables
    # and would be misdetected as x64 (PR #222 review, 2026-07-10) -- which is
    # exactly why CIM is the primary source above.
    $cimArch = $null
    try {
        $cimArch = (Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop |
            Select-Object -First 1).Architecture
    } catch {
        Log ("WARN: CIM processor architecture query failed (" + $_.Exception.Message +
             "); falling back to environment variables.")
    }

    if ($null -ne $cimArch) {
        switch ([int]$cimArch) {
            9  { return "fluent-bit-*-win64.msi" }
            12 { return "fluent-bit-*-winarm64.msi" }
            default {
                Fail ("Unsupported processor architecture (Win32_Processor.Architecture = " + $cimArch + "). " +
                      "The Yagura forwarder kit supports Windows x64 (win64) and ARM64 (winarm64) only " +
                      "(ADR-0009); Windows x86 (32-bit) is not supported by Yagura or by this kit.")
            }
        }
    }

    $arch = $env:PROCESSOR_ARCHITECTURE
    if (-not [string]::IsNullOrEmpty($env:PROCESSOR_ARCHITEW6432)) {
        $arch = $env:PROCESSOR_ARCHITEW6432
    }

    switch ($arch) {
        "AMD64" { return "fluent-bit-*-win64.msi" }
        "ARM64" { return "fluent-bit-*-winarm64.msi" }
        default {
            Fail ("Unsupported or undetected processor architecture: '" + $arch + "'. " +
                  "The Yagura forwarder kit supports Windows x64 (win64) and ARM64 (winarm64) only " +
                  "(ADR-0009); Windows x86 (32-bit) is not supported by Yagura or by this kit.")
        }
    }
}

function Invoke-FluentBitMsi([string]$msiFile, [string]$action) {
    Log ($action + " MSI silently: " + $msiFile)
    $proc = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList @("/i", ('"{0}"' -f $msiFile), "/quiet", "/norestart") `
        -Wait -PassThru
    if ($proc.ExitCode -eq 3010) {
        $script:rebootRequired = $true
        Log "msiexec exit 3010 (success, reboot required)."
    } elseif ($proc.ExitCode -ne 0) {
        Fail ("msiexec failed with exit code " + $proc.ExitCode)
    }
    if (-not (Test-Path $fluentBitExe)) {
        Fail ("Fluent Bit executable not found after install: " + $fluentBitExe)
    }
    Log ($action + " completed (engine version: " +
         (Get-Item $fluentBitExe).VersionInfo.ProductVersion + ").")
}

# Resolve the kit MSI (explicit -MsiPath wins; otherwise newest matching file
# next to the script -- see Get-LocalMsiFilenamePattern above for how the
# architecture-specific filter is picked). On a fresh machine a missing MSI is
# fatal; on an installed machine it just means "config/service update only"
# (idempotent rerun).
$resolvedMsiPath = $null
$localMsiFilter = $null
if (-not [string]::IsNullOrEmpty($MsiPath)) {
    if (-not (Test-Path $MsiPath)) { Fail ("MSI not found: " + $MsiPath) }
    $resolvedMsiPath = (Get-Item $MsiPath).FullName
} else {
    $localMsiFilter = Get-LocalMsiFilenamePattern
    $msi = Get-ChildItem -Path $scriptDir -Filter $localMsiFilter |
        Sort-Object Name | Select-Object -Last 1
    if ($null -ne $msi) { $resolvedMsiPath = $msi.FullName }
}

if (-not (Test-Path $fluentBitExe)) {
    if ($null -eq $resolvedMsiPath) {
        Fail ("No " + $localMsiFilter + " found next to the script (matching this machine's " +
              "architecture). Place the correct MSI there or pass -MsiPath.")
    }
    Invoke-FluentBitMsi $resolvedMsiPath "Install"
} elseif ($null -eq $resolvedMsiPath) {
    Log ("Fluent Bit already installed: " + $fluentBitExe + " (no matching MSI in the kit; config/service update only)")
} else {
    $installedVersion = Get-ParsedVersion ((Get-Item $fluentBitExe).VersionInfo.ProductVersion)
    $kitVersion = Get-ParsedVersion ([regex]::Match((Split-Path -Leaf $resolvedMsiPath),
        '^fluent-bit-(.+)-(?:win64|winarm64)\.msi$').Groups[1].Value)
    if ($null -eq $installedVersion -or $null -eq $kitVersion) {
        Log ("WARN: could not compare versions (installed: '" +
             (Get-Item $fluentBitExe).VersionInfo.ProductVersion + "', kit MSI: '" +
             (Split-Path -Leaf $resolvedMsiPath) + "'); keeping the existing engine untouched.")
    } elseif ($kitVersion -gt $installedVersion) {
        Log ("Existing Fluent Bit " + $installedVersion + " is older than the kit MSI (" + $kitVersion + "); upgrading.")
        # Stop the service before replacing the engine. Upgrading while the
        # service is running does succeed (verified), but leaves the service in
        # a transient StopPending state via the Windows Installer restart
        # manager, which then breaks the Stop-Service call in the service
        # recreation step below (verified 2026-07-10). Stopping first keeps the
        # whole run deterministic; the service is recreated and started later.
        $runningService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $runningService -and $runningService.Status -ne "Stopped") {
            Log ("Stopping service '" + $ServiceName + "' before the engine upgrade.")
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
        }
        Invoke-FluentBitMsi $resolvedMsiPath "Upgrade"
    } else {
        Log ("Fluent Bit already installed (version " + $installedVersion +
             " >= kit MSI " + $kitVersion + "); skipping MSI.")
    }
}

# --- 2. Deploy config + Lua filter ------------------------------------------
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

$conf = [IO.File]::ReadAllText($confTemplate)
$isPreConfigured = -not $conf.Contains("@@YAGURA_HOST@@")

if ($isPreConfigured) {
    # Generated kit (ADR-0008): the destination is already baked in by the
    # Yagura admin UI. Deploy as-is without touching the other placeholders
    # either (@@YAGURA_PORT@@ / @@CHANNELS@@ are substituted together
    # server-side; a template with no host placeholder is never partially
    # substituted).
    if (-not [string]::IsNullOrEmpty($YaguraHost)) {
        Log "WARN: -YaguraHost was specified but this is a pre-configured kit (destination already baked in); the parameter is ignored."
    }
    Log "Pre-configured kit detected (no @@YAGURA_HOST@@ placeholder); deploying the bundled config as-is."
} else {
    if ([string]::IsNullOrEmpty($YaguraHost)) {
        Fail "-YaguraHost is required for this kit (the bundled config still has the @@YAGURA_HOST@@ placeholder)."
    }
    $normalizedChannels = Get-NormalizedChannels $Channels
    $modeValue = $Mode.ToLowerInvariant()
    $conf = $conf.Replace("@@YAGURA_HOST@@", $YaguraHost)
    $conf = $conf.Replace("@@YAGURA_PORT@@", [string]$YaguraPort)
    $conf = $conf.Replace("@@CHANNELS@@", $normalizedChannels)
    $conf = $conf.Replace("@@MODE@@", $modeValue)
}

# UTF-8 without BOM: Fluent Bit's config parser does not expect a BOM.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($confTarget, $conf, $utf8NoBom)
Copy-Item -Path $luaTemplate -Destination (Join-Path $configDir "winevt-severity.lua") -Force
if ($isPreConfigured) {
    Log ("Config deployed to " + $configDir + " (pre-configured kit; destination baked in by the template)")
} else {
    Log ("Config deployed to " + $configDir + " (target: " + $YaguraHost + ":" + $YaguraPort + " " + $modeValue + ", channels: " + $normalizedChannels + ")")
    if ($modeValue -eq "tcp") {
        Log ("WARN: -Mode tcp uses LF-delimited framing, not RFC 6587 octet-counting (Fluent Bit's " +
             "out_syslog plugin does not support it). Multi-line event bodies with embedded newlines " +
             "can arrive split into multiple records. See docs/guides/forward-windows-eventlog.md.")
    }
}

# --- 3. Validate config before touching the service -------------------------
$dryRun = Start-Process -FilePath $fluentBitExe `
    -ArgumentList @("-c", ('"{0}"' -f $confTarget), "--dry-run") `
    -Wait -PassThru -NoNewWindow
if ($dryRun.ExitCode -ne 0) {
    Fail ("Config validation failed (fluent-bit --dry-run exit " + $dryRun.ExitCode + "). See " + $confTarget)
}
Log "Config validated (--dry-run ok)."

# --- 4. Register Windows service (delayed auto start) -----------------------
# The Fluent Bit MSI (verified with 5.0.8) registers a 'fluent-bit' service
# pointing at the stock config. Passing a quoted binPath through sc.exe from
# PowerShell 5.1 is unreliable (quote mangling), so instead of editing the
# existing service we delete and recreate it via New-Service, which takes the
# quoted binPath as a plain .NET string.
$binPath = ('"{0}" -c "{1}"' -f $fluentBitExe, $confTarget)
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Log ("Service '" + $ServiceName + "' already exists; recreating with the kit configuration.")
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail ("sc.exe delete failed with exit code " + $LASTEXITCODE) }
    $deadline = (Get-Date).AddSeconds(15)
    while ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) { Fail ("Service '" + $ServiceName + "' is still present after delete (delete pending?).") }
        Start-Sleep -Seconds 1
    }
}
New-Service -Name $ServiceName `
    -BinaryPathName $binPath `
    -DisplayName "Fluent Bit (Yagura forwarder)" `
    -Description "Forwards Windows event logs to the Yagura syslog server via Fluent Bit." `
    -StartupType Automatic | Out-Null
# Delayed auto start (same as the MSI default): no quoting involved, safe via sc.exe.
& sc.exe config $ServiceName start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { Log "WARN: sc.exe config start= delayed-auto returned $LASTEXITCODE; service stays on plain auto start." }
Log ("Service '" + $ServiceName + "' registered (delayed auto start).")

# Restart automatically on failure (60s delay, reset counter after 1 day).
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { Log "WARN: sc.exe failure (recovery options) returned $LASTEXITCODE; continuing." }

# --- 5. Start and verify ----------------------------------------------------
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus("Running", (New-TimeSpan -Seconds 30))
Log ("Service '" + $ServiceName + "' is Running.")
Log "INSTALL_SUCCESS"

if ($rebootRequired) { exit 3010 }
exit 0

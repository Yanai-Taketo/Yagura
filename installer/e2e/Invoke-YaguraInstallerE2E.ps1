<#
.SYNOPSIS
    Yagura MSI のゼロ設定ファーストラン E2E(M9-2。Issue #75 / ADR-0006 基準 1 の証拠)。

.DESCRIPTION
    「インストール直後、DB 設定なしで SQLite により即受信・即閲覧できる」(CLAUDE.md 確定方針 2)を
    実 MSI で検証する。設定ファイルの手編集は一切行わない。

    実行手順(Full モード):
      1. MSI サイレントインストール(msiexec /i /qn。管理者権限が必要)
      2. Windows サービス "Yagura" が Running になるまで待機
      3. インストール状態の証拠採取(ファイアウォール規則 3 本・データルート・firewall-rules.ini)
      4. UDP 514 へ syslog テストメッセージ送出 → 閲覧リスナ(既定 http://localhost:8514/)の
         HTML に RunId トークンが現れることを確認
      5. アンインストール(msiexec /x /qn)
      6. 残置物確認(サービス消滅・ファイアウォール規則消滅・スタートメニュー消滅・
         データルート保持 = 設計どおり。installer/README.md の責務表参照)

    照合は必ず ASCII トークン(RunId)で行う。日本語本文の照合は en-US CI の
    コードページ(CP437)で文字化けして誤判定するため使用しない(旧リポジトリ PR #42 実障害)。

    結果は人間可読ログ(*.log.txt)と機械可読サマリ(*.summary.json)の 2 形式で
    -OutputDir へ保存する(ADR-0006 基準 1 の証拠形式)。msiexec の詳細ログも同じ場所に残す。

.PARAMETER MsiPath
    検証対象の MSI(Full / DryRun モード)。例: installer\bin\Release\ja-JP\Yagura.msi

.PARAMETER DryRun
    msiexec・サービス操作・ネットワーク送受信を一切行わず、手順の流れと
    出力(ログ・JSON)の配管だけを検証する(開発機での構文・フロー検証用)。

.PARAMETER SendVerifyOnly
    送出・照合部分(手順 4)のみを実行する。起動済みの Yagura.Host に対して
    -UdpPort / -ViewerBaseUrl を指定して使う(開発機での単体動作確認用。管理者権限不要)。

.EXAMPLE
    # CI / lab(管理者 PowerShell)
    .\Invoke-YaguraInstallerE2E.ps1 -MsiPath ..\bin\Release\ja-JP\Yagura.msi -OutputDir .\results

.EXAMPLE
    # 開発機: ドライラン
    .\Invoke-YaguraInstallerE2E.ps1 -MsiPath ..\bin\Release\ja-JP\Yagura.msi -DryRun

.EXAMPLE
    # 開発機: 送出・照合のみ(YAGURA_UDP_PORT=51514 / YAGURA_HTTP_PORT=58514 で起動した Host に対して)
    .\Invoke-YaguraInstallerE2E.ps1 -SendVerifyOnly -UdpPort 51514 -ViewerBaseUrl http://127.0.0.1:58514/
#>
[CmdletBinding(DefaultParameterSetName = 'Full')]
param(
    [Parameter(ParameterSetName = 'Full', Mandatory = $true)]
    [Parameter(ParameterSetName = 'DryRun', Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(ParameterSetName = 'DryRun', Mandatory = $true)]
    [switch]$DryRun,

    [Parameter(ParameterSetName = 'SendVerifyOnly', Mandatory = $true)]
    [switch]$SendVerifyOnly,

    # 既定値は configuration.md §4.2 の確定ポート(受信 UDP 514 / 閲覧 8514)。
    [int]$UdpPort = 514,
    [string]$ViewerBaseUrl = 'http://localhost:8514/',

    [string]$OutputDir = (Join-Path $PSScriptRoot 'results'),
    [int]$ServiceStartTimeoutSec = 120,
    [int]$VerifyTimeoutSec = 90,
    [int]$UninstallSettleTimeoutSec = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 共有状態
# ---------------------------------------------------------------------------
$script:RunId = 'YAGURA-E2E-' + [Guid]::NewGuid().ToString('N')
$script:LogLines = New-Object System.Collections.Generic.List[string]
$script:Steps = New-Object System.Collections.Generic.List[object]
$script:Failed = $false
$script:InstallCompleted = $false
$script:UninstallCompleted = $false
$script:StartedUtc = [DateTime]::UtcNow

if ($DryRun) { $script:Mode = 'DryRun' }
elseif ($SendVerifyOnly) { $script:Mode = 'SendVerifyOnly' }
else { $script:Mode = 'Full' }

# 既定パス(installer/README.md の責務表と一致させる)
$script:ServiceName = 'Yagura'
$script:DataRoot = Join-Path $env:ProgramData 'Yagura'
$script:InstallDir = Join-Path $env:ProgramFiles 'Yagura'
$script:StartMenuDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Yagura'
$script:FirewallDisplayNames = @(
    'Yagura Syslog (UDP 514)',
    'Yagura Syslog (TCP 514)',
    'Yagura Viewer (TCP 8514)'
)

# ---------------------------------------------------------------------------
# ユーティリティ(コンソール・ログ出力は ASCII のみ — CI の CP437 対策)
# ---------------------------------------------------------------------------
function Write-E2ELog {
    param([string]$Message)
    $line = '[{0:yyyy-MM-ddTHH:mm:ss.fffZ}] {1}' -f [DateTime]::UtcNow, $Message
    $script:LogLines.Add($line)
    Write-Host $line
}

function Invoke-E2EStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        # $true: 失敗しても後続を続ける(証拠採取系の残置物チェックで使用)
        [bool]$ContinueOnFailure = $false
    )

    Write-E2ELog ('STEP BEGIN: {0}' -f $Name)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'Passed'
    $detail = ''
    try {
        $detail = & $Action
        if ($null -eq $detail) { $detail = '' }
        $detail = [string]$detail
    }
    catch {
        $status = 'Failed'
        $detail = $_.Exception.Message
        $script:Failed = $true
    }
    $sw.Stop()

    $script:Steps.Add([pscustomobject]@{
        name       = $Name
        status     = $status
        detail     = $detail
        durationMs = [long]$sw.ElapsedMilliseconds
    })
    Write-E2ELog ('STEP END:   {0} -> {1} ({2} ms) {3}' -f $Name, $status, $sw.ElapsedMilliseconds, $detail)

    if ($status -eq 'Failed' -and -not $ContinueOnFailure) {
        throw ('Step failed: {0}' -f $Name)
    }
    return ($status -eq 'Passed')
}

function Add-SkippedStep {
    param([string]$Name, [string]$Reason)
    $script:Steps.Add([pscustomobject]@{
        name       = $Name
        status     = 'Skipped'
        detail     = $Reason
        durationMs = 0
    })
    Write-E2ELog ('STEP SKIP:  {0} ({1})' -f $Name, $Reason)
}

function Wait-E2ECondition {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Probe,
        [Parameter(Mandatory = $true)][int]$TimeoutSec,
        [int]$IntervalMs = 1000
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Probe) { return $true }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return (& $Probe)
}

function Send-SyslogDatagram {
    param([int]$Port, [string]$Message)
    $client = New-Object System.Net.Sockets.UdpClient
    try {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($Message)
        [void]$client.Send($bytes, $bytes.Length, '127.0.0.1', $Port)
    }
    finally {
        $client.Close()
    }
}

function Test-ViewerContainsToken {
    param([string]$BaseUrl, [string]$Token)
    try {
        # -UseBasicParsing: Windows PowerShell 5.1 互換(IE エンジン非依存)。
        $resp = Invoke-WebRequest -Uri $BaseUrl -UseBasicParsing -TimeoutSec 10
        return ($resp.Content.Contains($Token))
    }
    catch {
        # リスナ起動直後の一過性の接続失敗は呼び出し側のポーリングで吸収する。
        return $false
    }
}

function Invoke-Msiexec {
    param(
        [Parameter(Mandatory = $true)][string]$ArgumentString,
        [Parameter(Mandatory = $true)][string]$Description
    )
    Write-E2ELog ('msiexec {0}' -f $ArgumentString)
    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $ArgumentString -Wait -PassThru
    # 0 = 成功 / 3010 = 成功(要再起動)。それ以外は失敗として扱う。
    if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 3010) {
        throw ('{0} failed: msiexec exit code {1}' -f $Description, $proc.ExitCode)
    }
    return $proc.ExitCode
}

function Get-YaguraService {
    return Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
}

function Get-YaguraFirewallRules {
    # Get-NetFirewallRule はワイルドカード不一致でエラーを出すため SilentlyContinue で拾う。
    # 注意: return @(...) はパイプライン経由で配列が展開され、0 件時に $null・1 件時に
    # スカラーが返る(呼び出し側の .Count が StrictMode で失敗する——CI 初回実行
    # run 28749730036 の residue-firewall-removed で実際に発生。規則削除は成功していたのに
    # 0 件 = $null で落ちた)。呼び出し側で必ず @() に包むこと。
    return @(Get-NetFirewallRule -DisplayName 'Yagura*' -ErrorAction SilentlyContinue)
}

# ---------------------------------------------------------------------------
# 出力(証拠)の書き出し
# ---------------------------------------------------------------------------
function Write-E2EOutputs {
    $finishedUtc = [DateTime]::UtcNow
    $overall = 'Passed'
    if ($script:Failed) { $overall = 'Failed' }

    $stamp = $script:StartedUtc.ToString('yyyyMMdd-HHmmss')
    $baseName = 'installer-e2e-{0}' -f $stamp

    $summary = [pscustomobject]@{
        schemaVersion = 1
        runId         = $script:RunId
        mode          = $script:Mode
        overall       = $overall
        startedUtc    = $script:StartedUtc.ToString('o')
        finishedUtc   = $finishedUtc.ToString('o')
        msiPath       = $MsiPath
        udpPort       = $UdpPort
        viewerBaseUrl = $ViewerBaseUrl
        machine       = [pscustomobject]@{
            computerName = $env:COMPUTERNAME
            osVersion    = [System.Environment]::OSVersion.VersionString
            userName     = $env:USERNAME
        }
        steps         = $script:Steps
    }

    $jsonPath = Join-Path $OutputDir ($baseName + '.summary.json')
    $logPath = Join-Path $OutputDir ($baseName + '.log.txt')

    # UTF-8(BOM 付き)で書く: Windows PowerShell 5.1 の既定(UTF-16)を避けつつ、
    # BOM なし UTF-8 を ANSI と誤判定するツールにも安全な形式。
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($jsonPath, ($summary | ConvertTo-Json -Depth 6), $utf8Bom)
    [System.IO.File]::WriteAllLines($logPath, $script:LogLines, $utf8Bom)

    Write-Host ('Summary JSON: {0}' -f $jsonPath)
    Write-Host ('Log:          {0}' -f $logPath)
    Write-Host ('Overall:      {0}' -f $overall)
}

# ---------------------------------------------------------------------------
# 手順本体
# ---------------------------------------------------------------------------
$null = New-Item -ItemType Directory -Force -Path $OutputDir
$OutputDir = (Resolve-Path -Path $OutputDir).Path

Write-E2ELog ('Yagura installer E2E starting. RunId={0} Mode={1}' -f $script:RunId, $script:Mode)

try {
    if ($script:Mode -eq 'SendVerifyOnly') {
        # -------------------------------------------------------------------
        # 送出・照合のみ(開発機での単体動作確認)
        # -------------------------------------------------------------------
        [void](Invoke-E2EStep -Name 'send-and-verify' -Action {
            $token = $script:RunId
            $message = ('<134>yagura-e2e {0}' -f $token)
            # 送信回数は script スコープで数える(PowerShell の動的スコープでは
            # probe スクリプトブロック内のローカル代入が外側へ反映されないため)。
            $script:SendCount = 0
            $found = Wait-E2ECondition -TimeoutSec $VerifyTimeoutSec -IntervalMs 500 -Probe {
                Send-SyslogDatagram -Port $UdpPort -Message $message
                $script:SendCount++
                Start-Sleep -Milliseconds 300
                Test-ViewerContainsToken -BaseUrl $ViewerBaseUrl -Token $token
            }
            if (-not $found) {
                throw ('token {0} did not appear on {1} within {2}s (sent {3} datagrams to udp/{4})' -f $token, $ViewerBaseUrl, $VerifyTimeoutSec, $script:SendCount, $UdpPort)
            }
            return ('token {0} visible on {1} after {2} datagram(s) to udp/{3}' -f $token, $ViewerBaseUrl, $script:SendCount, $UdpPort)
        })
    }
    else {
        # -------------------------------------------------------------------
        # 事前条件
        # -------------------------------------------------------------------
        [void](Invoke-E2EStep -Name 'preflight' -Action {
            $resolvedMsi = $null
            if (Test-Path -Path $MsiPath) {
                $resolvedMsi = (Resolve-Path -Path $MsiPath).Path
            }
            elseif (-not $DryRun) {
                throw ('MSI not found: {0}' -f $MsiPath)
            }

            if (-not $DryRun) {
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                $principal = New-Object Security.Principal.WindowsPrincipal($identity)
                if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    throw 'administrator privileges are required for msiexec /qn (run elevated, or use -DryRun / -SendVerifyOnly)'
                }
                # 事前状態の確認: 既にサービスが居る環境では E2E の判定が汚染されるため中止する。
                if ($null -ne (Get-YaguraService)) {
                    throw ('service "{0}" already exists before install; aborting to avoid polluting the verdict' -f $script:ServiceName)
                }
            }
            if ($null -ne $resolvedMsi) {
                return ('msi={0} admin=ok' -f $resolvedMsi)
            }
            return 'dry-run (msi not resolved)'
        })

        # -------------------------------------------------------------------
        # 1. サイレントインストール
        # -------------------------------------------------------------------
        if ($DryRun) {
            Add-SkippedStep -Name 'install-msi' -Reason ('dry-run: would run msiexec /i "{0}" /qn /norestart /l*v <log>' -f $MsiPath)
        }
        else {
            [void](Invoke-E2EStep -Name 'install-msi' -Action {
                $msiFull = (Resolve-Path -Path $MsiPath).Path
                $msiLog = Join-Path $OutputDir 'msiexec-install.log'
                $exitCode = Invoke-Msiexec -Description 'install' -ArgumentString ('/i "{0}" /qn /norestart /l*v "{1}"' -f $msiFull, $msiLog)
                $script:InstallCompleted = $true
                return ('msiexec exit code {0} (log: msiexec-install.log)' -f $exitCode)
            })
        }

        # -------------------------------------------------------------------
        # 2. サービス Running 待機
        # -------------------------------------------------------------------
        if ($DryRun) {
            Add-SkippedStep -Name 'wait-service-running' -Reason ('dry-run: would poll service "{0}" until Running (timeout {1}s)' -f $script:ServiceName, $ServiceStartTimeoutSec)
        }
        else {
            [void](Invoke-E2EStep -Name 'wait-service-running' -Action {
                $running = Wait-E2ECondition -TimeoutSec $ServiceStartTimeoutSec -Probe {
                    $svc = Get-YaguraService
                    ($null -ne $svc -and $svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running)
                }
                if (-not $running) {
                    $svc = Get-YaguraService
                    $state = 'not found'
                    if ($null -ne $svc) { $state = [string]$svc.Status }
                    throw ('service "{0}" did not reach Running within {1}s (state: {2})' -f $script:ServiceName, $ServiceStartTimeoutSec, $state)
                }
                return ('service "{0}" is Running' -f $script:ServiceName)
            })
        }

        # -------------------------------------------------------------------
        # 3. インストール状態の証拠採取
        # -------------------------------------------------------------------
        if ($DryRun) {
            Add-SkippedStep -Name 'installed-state-evidence' -Reason 'dry-run: would record firewall rules / data root / firewall-rules.ini'
        }
        else {
            [void](Invoke-E2EStep -Name 'installed-state-evidence' -Action {
                $rules = @(Get-YaguraFirewallRules)
                $ruleNames = @($rules | ForEach-Object { $_.DisplayName })
                foreach ($expected in $script:FirewallDisplayNames) {
                    if ($ruleNames -notcontains $expected) {
                        throw ('expected firewall rule missing: {0} (found: {1})' -f $expected, ($ruleNames -join '; '))
                    }
                }
                if (-not (Test-Path -Path $script:DataRoot)) {
                    throw ('data root not created: {0}' -f $script:DataRoot)
                }
                $iniPath = Join-Path $script:DataRoot 'firewall-rules.ini'
                if (-not (Test-Path -Path $iniPath)) {
                    throw ('firewall-rules.ini not found in data root: {0}' -f $iniPath)
                }
                return ('firewall rules present ({0}), data root and firewall-rules.ini present' -f ($ruleNames -join '; '))
            })
        }

        # -------------------------------------------------------------------
        # 4. UDP 送出 -> 閲覧リスナで照合(ゼロ設定ファーストランの本丸)
        # -------------------------------------------------------------------
        if ($DryRun) {
            Add-SkippedStep -Name 'send-and-verify' -Reason ('dry-run: would send "<134>yagura-e2e {0}" to udp/{1} and poll {2}' -f $script:RunId, $UdpPort, $ViewerBaseUrl)
        }
        else {
            [void](Invoke-E2EStep -Name 'send-and-verify' -Action {
                $token = $script:RunId
                $message = ('<134>yagura-e2e {0}' -f $token)
                # 送信回数は script スコープで数える(SendVerifyOnly 側のコメント参照)。
                $script:SendCount = 0
                $found = Wait-E2ECondition -TimeoutSec $VerifyTimeoutSec -IntervalMs 500 -Probe {
                    Send-SyslogDatagram -Port $UdpPort -Message $message
                    $script:SendCount++
                    Start-Sleep -Milliseconds 300
                    Test-ViewerContainsToken -BaseUrl $ViewerBaseUrl -Token $token
                }
                if (-not $found) {
                    throw ('token {0} did not appear on {1} within {2}s (sent {3} datagrams to udp/{4})' -f $token, $ViewerBaseUrl, $VerifyTimeoutSec, $script:SendCount, $UdpPort)
                }
                return ('token {0} visible on {1} after {2} datagram(s) to udp/{3}' -f $token, $ViewerBaseUrl, $script:SendCount, $UdpPort)
            })

            # 対話機能の成立検証(PR #79 の実バグの再発防止): 閲覧 UI の照合は prerender HTML
            # だけでも通ってしまうため、circuit 確立に必須のフレームワークスクリプトが実際に
            # 配信されることを別ステップで確認する。blazor.web.js が 404 の場合、画面は表示
            # されるが対話機能(テーマ切替・検索操作)が全て沈黙する——UI を RCL に置く構成で
            # SDK の RequiresAspNetWebAssets 条件から漏れて実際に起きた(修正 = Yagura.Host.csproj)。
            [void](Invoke-E2EStep -Name 'verify-interactive-framework-script' -Action {
                $url = ('{0}/_framework/blazor.web.js' -f $ViewerBaseUrl.TrimEnd('/'))
                $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 15
                if ($response.StatusCode -ne 200) {
                    throw ('blazor.web.js returned HTTP {0} (expected 200) - interactive circuit cannot start' -f $response.StatusCode)
                }
                if ($response.Content.Length -lt 1024) {
                    throw ('blazor.web.js is suspiciously small ({0} bytes) - possible empty/stub response' -f $response.Content.Length)
                }
                return ('blazor.web.js served: HTTP 200, {0} bytes' -f $response.Content.Length)
            })
        }

        # -------------------------------------------------------------------
        # 5. アンインストール
        # -------------------------------------------------------------------
        if ($DryRun) {
            Add-SkippedStep -Name 'uninstall-msi' -Reason ('dry-run: would run msiexec /x "{0}" /qn /norestart /l*v <log>' -f $MsiPath)
        }
        else {
            [void](Invoke-E2EStep -Name 'uninstall-msi' -Action {
                $msiFull = (Resolve-Path -Path $MsiPath).Path
                $msiLog = Join-Path $OutputDir 'msiexec-uninstall.log'
                $exitCode = Invoke-Msiexec -Description 'uninstall' -ArgumentString ('/x "{0}" /qn /norestart /l*v "{1}"' -f $msiFull, $msiLog)
                $script:UninstallCompleted = $true
                return ('msiexec exit code {0} (log: msiexec-uninstall.log)' -f $exitCode)
            })
        }

        # -------------------------------------------------------------------
        # 6. 残置物確認(設計: installer/README.md の責務表)
        #    失敗しても後続チェックを続け、全結果を証拠として残す。
        # -------------------------------------------------------------------
        if ($DryRun) {
            Add-SkippedStep -Name 'residue-service-removed' -Reason 'dry-run'
            Add-SkippedStep -Name 'residue-firewall-removed' -Reason 'dry-run'
            Add-SkippedStep -Name 'residue-startmenu-removed' -Reason 'dry-run'
            Add-SkippedStep -Name 'residue-dataroot-retained' -Reason 'dry-run'
            Add-SkippedStep -Name 'residue-installdir-removed' -Reason 'dry-run'
        }
        else {
            [void](Invoke-E2EStep -Name 'residue-service-removed' -ContinueOnFailure $true -Action {
                # SCM の削除反映は非同期のことがあるため短時間ポーリングする。
                $gone = Wait-E2ECondition -TimeoutSec $UninstallSettleTimeoutSec -Probe {
                    ($null -eq (Get-YaguraService))
                }
                if (-not $gone) {
                    throw ('service "{0}" still present after uninstall' -f $script:ServiceName)
                }
                return ('service "{0}" removed' -f $script:ServiceName)
            })

            [void](Invoke-E2EStep -Name 'residue-firewall-removed' -ContinueOnFailure $true -Action {
                $rules = @(Get-YaguraFirewallRules)
                if ($rules.Count -gt 0) {
                    $names = @($rules | ForEach-Object { $_.DisplayName }) -join '; '
                    throw ('firewall rules still present after uninstall: {0}' -f $names)
                }
                return 'no Yagura* firewall rules remain'
            })

            [void](Invoke-E2EStep -Name 'residue-startmenu-removed' -ContinueOnFailure $true -Action {
                if (Test-Path -Path $script:StartMenuDir) {
                    throw ('start menu folder still present: {0}' -f $script:StartMenuDir)
                }
                return 'start menu folder removed'
            })

            [void](Invoke-E2EStep -Name 'residue-dataroot-retained' -ContinueOnFailure $true -Action {
                # 設計どおりの「保持」: ログは資産(installer/README.md)。サービスが受信・保存を
                # 行った後なので SQLite ファイル(yagura.db)が残っているはず。
                if (-not (Test-Path -Path $script:DataRoot)) {
                    throw ('data root was deleted by uninstall (should be retained): {0}' -f $script:DataRoot)
                }
                $dbPath = Join-Path $script:DataRoot 'yagura.db'
                if (-not (Test-Path -Path $dbPath)) {
                    throw ('yagura.db not retained in data root: {0}' -f $dbPath)
                }
                return ('data root retained with yagura.db: {0}' -f $script:DataRoot)
            })

            [void](Invoke-E2EStep -Name 'residue-installdir-removed' -ContinueOnFailure $true -Action {
                if (Test-Path -Path $script:InstallDir) {
                    $leftover = @(Get-ChildItem -Path $script:InstallDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
                    throw ('install dir still present: {0} ({1} file(s): {2})' -f $script:InstallDir, $leftover.Count, (($leftover | Select-Object -First 5) -join '; '))
                }
                return 'install dir removed'
            })
        }
    }
}
catch {
    # Invoke-E2EStep が既に Failed を記録している。想定外の例外もここで拾って記録する。
    if (-not $script:Failed) {
        $script:Failed = $true
        $script:Steps.Add([pscustomobject]@{
            name       = 'unexpected-error'
            status     = 'Failed'
            detail     = $_.Exception.Message
            durationMs = 0
        })
    }
    Write-E2ELog ('E2E aborted: {0}' -f $_.Exception.Message)
}
finally {
    # 失敗時の後片付け: インストール済みのままなら CI ランナー/lab を汚さないよう
    # アンインストールを試みる(結果は cleanup ステップとして記録するが合否には含めない)。
    if ($script:InstallCompleted -and -not $script:UninstallCompleted) {
        try {
            Write-E2ELog 'cleanup: attempting uninstall of leftover install'
            $msiFull = (Resolve-Path -Path $MsiPath).Path
            $msiLog = Join-Path $OutputDir 'msiexec-cleanup-uninstall.log'
            $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList ('/x "{0}" /qn /norestart /l*v "{1}"' -f $msiFull, $msiLog) -Wait -PassThru
            Write-E2ELog ('cleanup: msiexec exit code {0}' -f $proc.ExitCode)
            $script:Steps.Add([pscustomobject]@{
                name       = 'cleanup-uninstall'
                status     = 'Info'
                detail     = ('msiexec exit code {0}' -f $proc.ExitCode)
                durationMs = 0
            })
        }
        catch {
            Write-E2ELog ('cleanup: uninstall attempt failed: {0}' -f $_.Exception.Message)
        }
    }

    Write-E2EOutputs
}

if ($script:Failed) {
    exit 1
}
exit 0

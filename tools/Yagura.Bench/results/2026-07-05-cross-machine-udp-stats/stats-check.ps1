# Cross-machine UDP statistics verification (architecture.md 4.2 follow-up)
# Run on YAGURA-STG in an ADMIN PowerShell: .\stats-check.ps1
# Coordinates with the sender (dev machine) via Enter prompts.

$ErrorActionPreference = "Stop"
$port = 51999

# 【2026-07-18 訂正】受信総数の正しいプロパティ名は DatagramsReceived である。
# .NET の UdpStatistics に IncomingDatagrams というプロパティは存在せず（.NET 10 /
# .NET Framework 4.8 の両方でリフレクション確認済み）、PowerShell は存在しない
# プロパティを $null として黙って返すため、本スクリプトの当初版が出力していた
# "Incoming delta" は実トラフィックと無関係に構造的に常に 0 だった。
# 2026-07-05 の記録のうち受信総数に関する観測はこの読み取り誤りによる測定
# アーティファクトである（破棄側 Discarded は実在プロパティのため観測は有効）。
# 詳細: ADR-0016 改訂履歴 1 / results/2026-07-18-server-udp-stats-trigger-d/
function Get-UdpStats {
    $p = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
    $v4 = $p.GetUdpIPv4Statistics()
    [pscustomobject]@{ Incoming = $v4.DatagramsReceived; Discarded = $v4.IncomingDatagramsDiscarded; Errors = $v4.IncomingDatagramsWithErrors }
}

Write-Host "adding temporary firewall rule for UDP $port..."
New-NetFirewallRule -DisplayName "YaguraBenchStatsCheck" -Direction Inbound -Protocol UDP -LocalPort $port -Action Allow | Out-Null

try {
    $sock = New-Object System.Net.Sockets.UdpClient(New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, $port))
    $sock.Client.ReceiveTimeout = 500

    # --- Test A: receiver actively reading (expect Incoming to count the sent datagrams) ---
    $a0 = Get-UdpStats
    $recvCount = 0
    Write-Host ""
    Write-Host "=== TEST A READY (port $port, actively receiving) ===" -ForegroundColor Cyan
    Write-Host "Tell the sender to run Test A now. Press any key AFTER the sender reports done."
    while (-not [Console]::KeyAvailable) {
        try {
            $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
            [void]$sock.Receive([ref]$ep)
            $recvCount++
        } catch {}
    }
    [void][Console]::ReadKey($true)
    $a1 = Get-UdpStats
    Write-Host ("TEST A: app received={0} | Incoming delta={1} Discarded delta={2} Errors delta={3}" -f $recvCount, ($a1.Incoming - $a0.Incoming), ($a1.Discarded - $a0.Discarded), ($a1.Errors - $a0.Errors)) -ForegroundColor Green

    # --- Test B: receiver NOT reading (expect drops in Discarded or Errors - which one is the finding) ---
    $b0 = Get-UdpStats
    Write-Host ""
    Write-Host "=== TEST B READY (port $port, NOT receiving - buffer will overflow) ===" -ForegroundColor Cyan
    Write-Host "Tell the sender to run Test B now. Press any key AFTER the sender reports done."
    [void][Console]::ReadKey($true)
    $b1 = Get-UdpStats
    Write-Host ("TEST B: Incoming delta={0} Discarded delta={1} Errors delta={2}" -f ($b1.Incoming - $b0.Incoming), ($b1.Discarded - $b0.Discarded), ($b1.Errors - $b0.Errors)) -ForegroundColor Green
    Write-Host ""
    Write-Host "=== paste both TEST A and TEST B lines back ==="

    $sock.Close()
}
finally {
    Remove-NetFirewallRule -DisplayName "YaguraBenchStatsCheck" -ErrorAction SilentlyContinue
    Write-Host "firewall rule removed."
}

<#
.SYNOPSIS
    公開済みの旧版 MSI(リリース資産そのもの)から、現在のソースでビルドした MSI への
    メジャーアップグレードを実機で検証する E2E。

.DESCRIPTION
    同じソースツリーから版番号だけを下げて作った MSI では、新旧でアセンブリの中身が同一になる
    ため、「版だけ上がって中身が旧版のまま残るファイル」(Issue #479・#514)を原理的に捉えられ
    ない。また、旧版が作ったデータ(蓄積ログ・設定・暗号化値・監査記録・カウンタ・スプール)を
    新版が引き継げるかは、旧版のバイナリに実際にデータを作らせないと確かめられない。本スクリプトは
    公開済みのリリース資産を更新元にして、これらを 1 回の通しで検証する。

    手順:
      0. 更新元 MSI の照合(SHA-256 を固定値と .sha256 の両方で照合・Authenticode 署名の
         Status/署名者の拇印/タイムスタンプ・ProductVersion と UpgradeCode)
      A. 更新の成功経路
         1. 更新元をサイレントインストールし、受信できることを確認する
         2. 旧版にデータを作らせる: UDP/TCP のマーカー付きメッセージ・上限超過の TCP フレーム
            (永続化されるカウンタ)・監査記録・DPAPI で暗号化した SMTP パスワードを含む設定・
            未消化のスプールセグメント(DB の書き込みロックを握って退避させる)
         3. 停止状態でスナップショットを取り、新版 MSI で更新する
         4. 更新後の検証: 新版サービスの稼働と全リスナの bind 主体・製品エントリ 1 件・導入
            ファイル一式が新版 MSI の中身と一致・設定のバイト一致と暗号化値の復号・スプールの
            消化・蓄積ログの一致・受信と閲覧・監査記録の保全と追記・カウンタの連続性・FW 規則・
            実行アカウント・記憶プロパティ・ACL・旧版 MSI の再実行がダウングレードとして拒否
            されること
         5. アンインストール
      B. 失敗注入(ADR-0023 決定 6 / ADR-0025 基準 4 の「製品の存続」の失敗側)
         データルートを消して更新元を入れ直し、稼働中の更新元に対して installer-e2e と同じ
         「実在しない gMSA 形式のアカウント」で更新を失敗させる。旧版の製品エントリ・サービス・
         バイナリ一式・サービス構成・ACL が元に戻り、受信が再開することを確認する。A と分けるのは、
         失敗した更新の後始末が成功側の結果に混ざらないようにするため

    照合は ASCII トークンだけで行う(en-US ランナーのコードページで日本語照合が化けるため。
    Invoke-YaguraInstallerE2E.ps1 と同じ方針)。コンソール・ログの出力も ASCII に限る。

    結果は人間可読ログ(*.log.txt)と機械可読サマリ(*.summary.json)を -OutputDir へ保存する。
    サマリの notExercised には、この E2E で作れない項目とその理由を載せる。

.PARAMETER SourceMsiPath
    更新元 MSI(公開済みリリース資産。例: Yagura-0.5.0-x64.msi)。

.PARAMETER SourceSha256Path
    更新元と同時に公開された .sha256(「16 進ハッシュ + 空白 2 個 + ファイル名」の 1 行)。

.PARAMETER ExpectedSourceSha256
    呼び出し側が固定値として持つ更新元の SHA-256。.sha256 と同じ取得元から来た値だけで
    照合すると、取得元が差し替えられた場合に両方が揃って変わり検知できないため、別経路の
    固定値と二重に照合する。

.PARAMETER ExpectedSourceVersion
    更新元の ProductVersion(例: 0.5.0)。

.PARAMETER ExpectedSourceSignerThumbprint
    更新元の署名者証明書の拇印(SHA-1、16 進)。Subject(日本語)ではなく拇印で照合する。

.PARAMETER TargetMsiPath
    更新先 MSI(現在のソースからビルドしたもの)。ProductVersion は更新元より新しいこと。

.EXAMPLE
    .\Invoke-YaguraPublishedUpgradeE2E.ps1 -SourceMsiPath .\Yagura-0.5.0-x64.msi `
        -SourceSha256Path .\Yagura-0.5.0-x64.msi.sha256 -ExpectedSourceSha256 <hex> `
        -ExpectedSourceVersion 0.5.0 -ExpectedSourceSignerThumbprint <hex> `
        -TargetMsiPath ..\bin\Release\ja-JP\Yagura-x64.msi -OutputDir .\results
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceMsiPath,
    [Parameter(Mandatory = $true)][string]$SourceSha256Path,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceSignerThumbprint,
    [Parameter(Mandatory = $true)][string]$TargetMsiPath,

    # 既定値はスクリプト本文で解決する(Windows PowerShell 5.1 は param 既定値式の評価時点で
    # $PSScriptRoot が空のため。Invoke-YaguraInstallerE2E.ps1 と同じ理由)。
    [string]$OutputDir,

    # 既定値は configuration.md §4.2 の確定ポート。
    [int]$UdpPort = 514,
    [int]$TcpPort = 514,
    [string]$ViewerBaseUrl = 'http://localhost:8514/',
    [int]$AdminPort = 8515,

    [int]$ServiceStartTimeoutSec = 120,
    # DB の書き込みロックを握ったまま停止させるため、保存の打ち切りを待つ分だけ長く取る。
    [int]$ServiceStopTimeoutSec = 180,
    [int]$VerifyTimeoutSec = 90,
    [int]$SpoolTimeoutSec = 150
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 の Invoke-WebRequest は進捗表示で極端に遅くなる。
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path $PSScriptRoot 'results-published-upgrade'
}

# ---------------------------------------------------------------------------
# 共有状態
# ---------------------------------------------------------------------------
# 英数字だけにする: 閲覧の検索(LIKE)で特別な意味を持つ文字を含めないため。
$script:RunId = 'YGUPG' + [Guid]::NewGuid().ToString('N')
$script:LogLines = New-Object System.Collections.Generic.List[string]
$script:Steps = New-Object System.Collections.Generic.List[object]
$script:Failed = $false
$script:StartedUtc = [DateTime]::UtcNow
$script:State = @{}
$script:ProductInstalled = $false
$script:LockDb = [IntPtr]::Zero
$script:PreFailureInstallDir = @{}
$script:UpgradeStartedAt = [DateTime]::MaxValue

$script:ServiceName = 'Yagura'
$script:DataRoot = Join-Path $env:ProgramData 'Yagura'
$script:InstallDir = Join-Path $env:ProgramFiles 'Yagura'
$script:HostExePath = Join-Path $script:InstallDir 'Yagura.Host.exe'
$script:ConfigPath = Join-Path $script:DataRoot 'yagura.json'
$script:DbPath = Join-Path $script:DataRoot 'yagura.db'
$script:SpoolDir = Join-Path $script:DataRoot 'spool'
$script:AuditDir = Join-Path $script:DataRoot 'audit'
$script:MetadataPath = Join-Path $script:DataRoot 'observability-state.json'
$script:ForwarderDir = Join-Path $script:DataRoot 'forwarder'
$script:StartMenuAdminShortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Yagura\Yagura 管理.url'
$script:PublicDesktopShortcut = Join-Path $env:PUBLIC 'Desktop\Yagura.url'
$script:ViewerPort = ([Uri]$ViewerBaseUrl).Port
$script:FirewallDisplayNames = @(
    'Yagura Syslog (UDP 514)',
    'Yagura Syslog (TCP 514)',
    'Yagura Viewer (TCP 8514)'
)
# 旧版の SMTP パスワード保護と同じ用途別 entropy(src/Yagura.Host/Configuration/DpapiSecretProtector.cs)。
# 旧版の管理画面で保存する経路は対話 UI のため CI で踏めず、同じ算法で暗号化した値を置く。
$script:EmailPasswordEntropy = 'Yagura:Notification:Email:Smtp:Password:v1'
# 上限(64 KiB)を超え、かつ再同期の打ち切り(128 KiB)には届かない長さ。
$script:OversizedTcpFrameBytes = 70000

# この E2E で作れない・踏めない項目(サマリへ載せ、v1.0 時点の未確認事項として開示に回す)。
$script:NotExercised = @(
    [pscustomobject]@{
        item   = 'app-auth-admin-account'
        reason = 'The source version creates app-auth admin accounts only through the interactive admin screen (Blazor Server circuit); it has no CLI or HTTP API for it. Writing rows directly would fabricate the password hash format instead of exercising the product. Only the schema migration of the (empty) AdminAccounts table is verified.'
    },
    [pscustomobject]@{
        item   = 'dpapi-value-saved-by-source-admin-screen'
        reason = 'The SMTP password is encrypted by this script with the same algorithm as the source version (DPAPI LocalMachine, same entropy, dpapi: prefix), not saved through the source admin screen (interactive UI). The source version does not report its own decryption result outside the console, so only the target version decryption is observed.'
    },
    [pscustomobject]@{
        item   = 'sql-server-provider'
        reason = 'No SQL Server instance on the GitHub-hosted runner. Upgrading a promoted (SQL Server) installation, including its DPAPI-protected connection string, is not exercised.'
    },
    [pscustomobject]@{
        item   = 'gmsa-service-account'
        reason = 'No Active Directory on the runner. Upgrading an installation that runs as a gMSA is not exercised (the failure injection only uses a nonexistent gMSA-shaped name).'
    },
    [pscustomobject]@{
        item   = 'certificate-thumbprint-configuration'
        reason = 'TLS ingestion, viewer HTTPS and admin HTTPS (certificate store references) are not configured in this scenario.'
    },
    [pscustomobject]@{
        item   = 'long-running-state'
        reason = 'The upgrade source data is created minutes before the upgrade; the state of an installation operated for months is not reproduced.'
    },
    [pscustomobject]@{
        item   = 'upgrade-while-running-success-path'
        reason = 'The successful upgrade starts from a stopped service, because leaving unprocessed spool segments requires stopping the source version while the database is locked. Stopping a running source service by the target MSI is exercised by the failure injection (rolled back), and the running-service success path by the same-tree upgrade test in installer-e2e.'
    },
    [pscustomobject]@{
        item   = 'ingestion-gap-under-load'
        reason = 'The upgrade duration is recorded, but the ingestion gap under a known EPS load (ADR-0023 acceptance criterion 11) is not measured.'
    }
)

# ---------------------------------------------------------------------------
# ネイティブ呼び出し(DPAPI と OS 同梱の SQLite)
# ---------------------------------------------------------------------------
# Windows PowerShell 5.1 の Add-Type(C# 5)でもコンパイルできる構文に留める。
# SQLite は OS 同梱の winsqlite3.dll を使う——ランナーに追加の導入物を持ち込まないため。
$script:NativeSource = @'
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static class YaguraUpgradeE2ENative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectLocalMachine = 0x4;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] ProtectLocalMachine(byte[] data, byte[] entropy)
    {
        return RunDpapi(data, entropy, true);
    }

    public static byte[] UnprotectLocalMachine(byte[] data, byte[] entropy)
    {
        return RunDpapi(data, entropy, false);
    }

    private static byte[] RunDpapi(byte[] data, byte[] entropy, bool protect)
    {
        GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            DataBlob input = new DataBlob();
            input.Size = data.Length;
            input.Data = dataHandle.AddrOfPinnedObject();
            DataBlob optionalEntropy = new DataBlob();
            optionalEntropy.Size = entropy.Length;
            optionalEntropy.Data = entropyHandle.AddrOfPinnedObject();
            DataBlob output = new DataBlob();
            bool ok = protect
                ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden | CryptProtectLocalMachine, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                throw new InvalidOperationException((protect ? "CryptProtectData" : "CryptUnprotectData") + " failed: Win32 error " + Marshal.GetLastWin32Error());
            }

            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            dataHandle.Free();
            entropyHandle.Free();
        }
    }

    private const string SqliteLibrary = "winsqlite3.dll";
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteOpenReadWrite = 0x00000002;
    private const int SqliteNull = 5;

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteOpenV2(byte[] fileNameUtf8, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteCloseV2(IntPtr db);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteBusyTimeout(IntPtr db, int milliseconds);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqlitePrepareV2(IntPtr db, byte[] sqlUtf8, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteStep(IntPtr statement);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteFinalize(IntPtr statement);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteColumnCount(IntPtr statement);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteColumnType(IntPtr statement, int column);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SqliteColumnBlob(IntPtr statement, int column);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.StdCall)]
    private static extern int SqliteColumnBytes(IntPtr statement, int column);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.StdCall)]
    private static extern long SqliteColumnInt64(IntPtr statement, int column);

    [DllImport(SqliteLibrary, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SqliteErrorMessage(IntPtr db);

    public static IntPtr OpenDatabase(string path, bool readOnly, int busyTimeoutMilliseconds)
    {
        IntPtr db;
        int rc = SqliteOpenV2(Utf8Z(path), out db, readOnly ? SqliteOpenReadOnly : SqliteOpenReadWrite, IntPtr.Zero);
        if (rc != SqliteOk)
        {
            string message = db != IntPtr.Zero ? ReadUtf8(SqliteErrorMessage(db)) : string.Empty;
            if (db != IntPtr.Zero)
            {
                SqliteCloseV2(db);
            }

            throw new InvalidOperationException("sqlite3_open_v2 failed (" + rc + "): " + message);
        }

        SqliteBusyTimeout(db, busyTimeoutMilliseconds);
        return db;
    }

    public static void CloseDatabase(IntPtr db)
    {
        if (db != IntPtr.Zero)
        {
            SqliteCloseV2(db);
        }
    }

    public static void Execute(IntPtr db, string sql)
    {
        IntPtr statement = Prepare(db, sql);
        try
        {
            int rc;
            while ((rc = SqliteStep(statement)) == SqliteRow)
            {
            }

            if (rc != SqliteDone)
            {
                throw Error(db, "sqlite3_step", rc);
            }
        }
        finally
        {
            SqliteFinalize(statement);
        }
    }

    public static long QueryInt64(IntPtr db, string sql)
    {
        IntPtr statement = Prepare(db, sql);
        try
        {
            int rc = SqliteStep(statement);
            if (rc != SqliteRow)
            {
                throw Error(db, "sqlite3_step", rc);
            }

            return SqliteColumnInt64(statement, 0);
        }
        finally
        {
            SqliteFinalize(statement);
        }
    }

    // 行の全列を「型 1 バイト + 長さ 4 バイト + 値」で連結した SHA-256。NULL は型だけを積む
    // (空文字列と NULL を区別するため)。
    public static string QueryDigest(IntPtr db, string sql, out long rowCount)
    {
        rowCount = 0;
        IntPtr statement = Prepare(db, sql);
        try
        {
            using (SHA256 sha = SHA256.Create())
            {
                int columns = SqliteColumnCount(statement);
                int rc;
                while ((rc = SqliteStep(statement)) == SqliteRow)
                {
                    for (int i = 0; i < columns; i++)
                    {
                        int type = SqliteColumnType(statement, i);
                        if (type == SqliteNull)
                        {
                            byte[] nullMarker = new byte[] { (byte)type };
                            sha.TransformBlock(nullMarker, 0, nullMarker.Length, null, 0);
                            continue;
                        }

                        IntPtr pointer = SqliteColumnBlob(statement, i);
                        int length = SqliteColumnBytes(statement, i);
                        byte[] value = new byte[length];
                        if (length > 0)
                        {
                            Marshal.Copy(pointer, value, 0, length);
                        }

                        byte[] header = new byte[5];
                        header[0] = (byte)type;
                        BitConverter.GetBytes(length).CopyTo(header, 1);
                        sha.TransformBlock(header, 0, header.Length, null, 0);
                        sha.TransformBlock(value, 0, value.Length, null, 0);
                    }

                    rowCount++;
                }

                if (rc != SqliteDone)
                {
                    throw Error(db, "sqlite3_step", rc);
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }
        finally
        {
            SqliteFinalize(statement);
        }
    }

    private static IntPtr Prepare(IntPtr db, string sql)
    {
        IntPtr statement;
        int rc = SqlitePrepareV2(db, Utf8Z(sql), -1, out statement, IntPtr.Zero);
        if (rc != SqliteOk)
        {
            throw Error(db, "sqlite3_prepare_v2", rc);
        }

        return statement;
    }

    private static Exception Error(IntPtr db, string api, int rc)
    {
        return new InvalidOperationException(api + " failed (" + rc + "): " + ReadUtf8(SqliteErrorMessage(db)));
    }

    private static string ReadUtf8(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = 0;
        while (Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }

        byte[] buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    private static byte[] Utf8Z(string value)
    {
        return Encoding.UTF8.GetBytes(value + "\0");
    }

    // スプールセグメントは「長さ (int32 LE) + ペイロード + CRC-32 (uint32 LE)」のフレームの列
    // (src/Yagura.Storage/Spool/SpoolSegmentReader.cs)。消化側と同じく、長さと CRC が正しい
    // フレームだけを数える——途中で切れた末尾のレコードは消化されないため、期待件数に含めない。
    public static int CountSpoolRecordsContaining(byte[] segment, byte[] token, out bool corruptTail)
    {
        corruptTail = false;
        int count = 0;
        int position = 0;
        while (position < segment.Length)
        {
            if (segment.Length - position < 4)
            {
                corruptTail = true;
                break;
            }

            int payloadLength = BitConverter.ToInt32(segment, position);
            int payloadStart = position + 4;
            if (payloadLength < 0 || (long)payloadLength + 4 > segment.Length - payloadStart)
            {
                corruptTail = true;
                break;
            }

            uint storedCrc = BitConverter.ToUInt32(segment, payloadStart + payloadLength);
            if (storedCrc != ComputeCrc32(segment, payloadStart, payloadLength))
            {
                corruptTail = true;
                break;
            }

            if (IndexOf(segment, payloadStart, payloadLength, token) >= 0)
            {
                count++;
            }

            position = payloadStart + payloadLength + 4;
        }

        return count;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static uint ComputeCrc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = offset; i < offset + length; i++)
        {
            crc = (crc >> 8) ^ Crc32Table[(byte)(crc ^ data[i])];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static int IndexOf(byte[] data, int offset, int length, byte[] pattern)
    {
        int last = offset + length - pattern.Length;
        for (int i = offset; i <= last; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j])
            {
                j++;
            }

            if (j == pattern.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
'@

# ---------------------------------------------------------------------------
# ユーティリティ(コンソール・ログ出力は ASCII のみ)
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
        # $true: 失敗しても後続を続ける(更新後の検証は全項目の結果を証拠として残すため)
        [bool]$ContinueOnFailure = $false
    )

    Write-E2ELog ('STEP BEGIN: {0}' -f $Name)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $stepStatus = 'Passed'
    $stepDetail = ''
    try {
        $stepDetail = & $Action
        if ($null -eq $stepDetail) { $stepDetail = '' }
        $stepDetail = [string]$stepDetail
    }
    catch {
        $stepStatus = 'Failed'
        $stepDetail = $_.Exception.Message
        $script:Failed = $true
    }
    $sw.Stop()

    $script:Steps.Add([pscustomobject]@{
        name       = $Name
        status     = $stepStatus
        detail     = $stepDetail
        durationMs = [long]$sw.ElapsedMilliseconds
    })
    Write-E2ELog ('STEP END:   {0} -> {1} ({2} ms) {3}' -f $Name, $stepStatus, $sw.ElapsedMilliseconds, $stepDetail)

    if ($stepStatus -eq 'Failed' -and -not $ContinueOnFailure) {
        throw ('Step failed: {0}' -f $Name)
    }
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
    return [bool](& $Probe)
}

function Get-PropValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Token {
    param([string]$Suffix)
    # 接尾辞は 4 文字固定: どのトークンも他のトークンの部分文字列にならない。
    return $script:RunId + $Suffix
}

function Get-Sha256Hex {
    param([byte[]]$Bytes, [int]$Offset, [int]$Count)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes, $Offset, $Count)) -replace '-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Read-AllBytesShared {
    # サービスが書き込み中のファイル(スプール・監査記録)も読めるよう、共有を広く取る。
    param([string]$Path)
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    try {
        $buffer = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $buffer.Length) {
            $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
            if ($read -le 0) { break }
            $offset += $read
        }
        if ($offset -lt $buffer.Length) {
            $trimmed = New-Object byte[] $offset
            [Array]::Copy($buffer, $trimmed, $offset)
            $buffer = $trimmed
        }
        # 先頭のカンマ: byte[] をパイプラインで 1 バイトずつに展開させない。
        return , $buffer
    }
    finally {
        $stream.Dispose()
    }
}

function Get-VersionCore {
    # FileVersion / ProductVersion の先頭の数値部分(Major.Minor.Build)だけを取り出す。
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $match = [regex]::Match($Text, '^\s*(\d+)\.(\d+)\.(\d+)')
    if (-not $match.Success) { return $null }
    return ('{0}.{1}.{2}' -f [int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value)
}

function Invoke-Msiexec {
    param(
        [Parameter(Mandatory = $true)][string]$ArgumentString,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$ExpectFailure
    )
    Write-E2ELog ('msiexec {0}' -f $ArgumentString)
    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $ArgumentString -Wait -PassThru
    $succeeded = ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010)
    if ($ExpectFailure -and $succeeded) {
        throw ('{0} unexpectedly succeeded (msiexec exit code {1})' -f $Description, $proc.ExitCode)
    }
    if (-not $ExpectFailure -and -not $succeeded) {
        throw ('{0} failed: msiexec exit code {1}' -f $Description, $proc.ExitCode)
    }
    return $proc.ExitCode
}

function Get-MsiProperty {
    param([string]$MsiPath, [string]$Property)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))
        $query = "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '{0}'" -f $Property
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($query))
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { return $null }
        return [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        # 後続の msiexec が同じパッケージを開けるよう、データベースのハンドルを確実に手放す。
        if ($null -ne $record) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
        if ($null -ne $view) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) }
        if ($null -ne $database) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

# --- サービス ---------------------------------------------------------------
function Get-YaguraService {
    return Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
}

function Test-YaguraServiceStatus {
    param([string]$ExpectedStatus)
    $svc = Get-YaguraService
    return ($null -ne $svc -and [string]$svc.Status -eq $ExpectedStatus)
}

function Wait-YaguraServiceStatus {
    param([string]$ExpectedStatus, [int]$TimeoutSec)
    return (Wait-E2ECondition -TimeoutSec $TimeoutSec -Probe { Test-YaguraServiceStatus -ExpectedStatus $ExpectedStatus })
}

function Get-YaguraServiceState {
    $svc = Get-YaguraService
    if ($null -eq $svc) { return 'not found' }
    return [string]$svc.Status
}

function Stop-YaguraServiceAndWait {
    $svc = Get-YaguraService
    if ($null -eq $svc) { throw 'service "Yagura" not found' }
    if ([string]$svc.Status -eq 'Running') {
        # ServiceController.Stop() は停止要求だけを送って戻る。完了は下で上限付きで待つ
        # (Stop-Service は上限なしに待ち続けるため)。
        $svc.Stop()
    }
    if (-not (Wait-YaguraServiceStatus -ExpectedStatus 'Stopped' -TimeoutSec $ServiceStopTimeoutSec)) {
        throw ('service "Yagura" did not stop within {0}s (state: {1})' -f $ServiceStopTimeoutSec, (Get-YaguraServiceState))
    }
}

function Start-YaguraServiceAndWait {
    Start-Service -Name $script:ServiceName
    if (-not (Wait-YaguraServiceStatus -ExpectedStatus 'Running' -TimeoutSec $ServiceStartTimeoutSec)) {
        throw ('service "Yagura" did not reach Running within {0}s (state: {1})' -f $ServiceStartTimeoutSec, (Get-YaguraServiceState))
    }
}

function Get-YaguraServiceProcess {
    # 稼働中のサービスのプロセス(PID・実行ファイル・起動時刻・ファイル版)。
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='Yagura'"
    if ($null -eq $svc -or [int]$svc.ProcessId -eq 0) { return $null }
    $proc = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId={0}' -f [int]$svc.ProcessId)
    if ($null -eq $proc) { return $null }
    $path = [string]$proc.ExecutablePath
    $fileVersion = $null
    if (-not [string]::IsNullOrEmpty($path) -and (Test-Path -LiteralPath $path)) {
        $fileVersion = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    }
    return [pscustomobject]@{
        ProcessId   = [int]$svc.ProcessId
        Path        = $path
        StartedAt   = $proc.CreationDate
        FileVersion = $fileVersion
    }
}

function Get-InstalledHostFileVersion {
    if (-not (Test-Path -LiteralPath $script:HostExePath)) { return $null }
    return (Get-Item -LiteralPath $script:HostExePath).VersionInfo.FileVersion
}

function Get-InstalledYaguraProducts {
    # 「アプリと機能」の情報源(アンインストール情報のレジストリ)。Win32_Product は列挙だけで
    # 全製品の再構成検証を誘発するため使わない。
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($root in $roots) {
        if (-not (Test-Path -Path $root)) { continue }
        foreach ($key in @(Get-ChildItem -Path $root -ErrorAction SilentlyContinue)) {
            $props = Get-ItemProperty -Path $key.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $props) { continue }
            # 完全一致: 前方一致だと将来の別製品(例: フォワーダ)を数えてしまう。
            if ([string](Get-PropValue $props 'DisplayName') -ceq 'Yagura') {
                $found.Add([pscustomobject]@{
                    ProductCode    = $key.PSChildName
                    DisplayVersion = [string](Get-PropValue $props 'DisplayVersion')
                })
            }
        }
    }
    return $found.ToArray()
}

# --- 送受信 -----------------------------------------------------------------
function Send-SyslogUdp {
    param([string]$Message)
    $client = New-Object System.Net.Sockets.UdpClient
    try {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($Message)
        [void]$client.Send($bytes, $bytes.Length, '127.0.0.1', $UdpPort)
    }
    finally {
        $client.Close()
    }
}

function Send-SyslogTcp {
    # 非透過フレーミング(LF 区切り)で送る。-OversizedBytes を与えると、上限を超える 1 行を
    # 先に送る(破棄されてカウンタ TcpMessageOversizedDiscarded が増え、後続の行は受信される)。
    param([string[]]$Lines, [int]$OversizedBytes = 0)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $TcpPort)
        $stream = $client.GetStream()
        if ($OversizedBytes -gt 0) {
            $oversized = [System.Text.Encoding]::ASCII.GetBytes('<134>' + ('X' * $OversizedBytes) + "`n")
            $stream.Write($oversized, 0, $oversized.Length)
        }
        foreach ($line in $Lines) {
            $bytes = [System.Text.Encoding]::ASCII.GetBytes($line + "`n")
            $stream.Write($bytes, 0, $bytes.Length)
        }
        $stream.Flush()
        $client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)
        Start-Sleep -Milliseconds 200
    }
    finally {
        $client.Close()
    }
}

function New-SyslogLine {
    param([string]$Token)
    return ('<134>yagura-upgrade-e2e {0}' -f $Token)
}

function Get-ViewerTokenCount {
    # 閲覧の CSV エクスポート(検索条件 q で絞る)で、トークンを含む行数を数える。
    # 検索画面の HTML は入力欄に q を書き戻すため、HTML での照合は常に一致してしまう。
    param([string]$Token)
    $url = '{0}/search/export.csv?q={1}' -f $ViewerBaseUrl.TrimEnd('/'), [Uri]::EscapeDataString($Token)
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 15
    }
    catch {
        return -1
    }
    $text = [System.Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
    return @($text -split "`n" | Where-Object { $_.Contains($Token) }).Count
}

function Test-ViewerSearchPageContainsToken {
    # 条件なしの検索画面(最新ログの prerender)にトークンが出ること。q を付けない。
    param([string]$Token)
    try {
        $resp = Invoke-WebRequest -Uri ('{0}/search' -f $ViewerBaseUrl.TrimEnd('/')) -UseBasicParsing -TimeoutSec 15
        return $resp.Content.Contains($Token)
    }
    catch {
        return $false
    }
}

function Wait-TokenIngested {
    # 見えるまで送り直す(受信開始直後の取りこぼしを吸収する)。戻り値は送信回数。
    param([string]$Token, [scriptblock]$Send, [int]$TimeoutSec = $VerifyTimeoutSec)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $sendCount = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        & $Send
        $sendCount++
        Start-Sleep -Milliseconds 700
        if ((Get-ViewerTokenCount -Token $Token) -ge 1) { return $sendCount }
    }
    throw ('token {0} did not become visible via {1}search/export.csv within {2}s ({3} send(s))' -f $Token, $ViewerBaseUrl, $TimeoutSec, $sendCount)
}

function Get-HttpStatusCode {
    param([string]$Url)
    try {
        $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 15
        return [int]$resp.StatusCode
    }
    catch {
        $response = Get-PropValue $_.Exception 'Response'
        if ($null -ne $response) { return [int]$response.StatusCode }
        return -1
    }
}

function Get-FreeTcpPort {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

# --- データルートの観測 -----------------------------------------------------
function Get-AuditSnapshot {
    $files = New-Object System.Collections.Generic.List[object]
    $rejections = 0
    if (Test-Path -LiteralPath $script:AuditDir) {
        foreach ($file in @(Get-ChildItem -LiteralPath $script:AuditDir -File -Filter 'audit*.jsonl' | Sort-Object Name)) {
            $bytes = Read-AllBytesShared -Path $file.FullName
            $rejections += (Get-AuditRejectionCount -Bytes $bytes)
            $files.Add([pscustomobject]@{
                Name   = $file.Name
                Length = [long]$bytes.Length
                Sha256 = (Get-Sha256Hex -Bytes $bytes -Offset 0 -Count $bytes.Length)
            })
        }
    }
    return [pscustomobject]@{ Files = $files.ToArray(); ViewerAdminRejections = $rejections }
}

function Get-AuditRejectionCount {
    # 閲覧リスナへの管理系要求の拒否(監査 3001)のうち、本スクリプトが起こした形のもの。
    param([byte[]]$Bytes)
    $count = 0
    foreach ($line in ([System.Text.Encoding]::UTF8.GetString($Bytes) -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $entry = $line | ConvertFrom-Json } catch { continue }
        if ((Get-PropValue $entry 'EventId') -eq 3001 -and
            [string](Get-PropValue $entry 'AttemptedPath') -eq '/admin' -and
            (Get-PropValue $entry 'ReachedListenerPort') -eq $script:ViewerPort) {
            $count++
        }
    }
    return $count
}

function Invoke-AuditedViewerAdminRequest {
    # 閲覧リスナ経由の管理画面要求は 404 で拒否され、監査 3001 が 1 件記録される
    # (UI を操作せずに監査記録を作れる経路)。
    $code = Get-HttpStatusCode -Url ('{0}/admin' -f $ViewerBaseUrl.TrimEnd('/'))
    if ($code -ne 404) {
        throw ('GET {0}admin on the viewer listener returned HTTP {1} (expected 404 with an audit record)' -f $ViewerBaseUrl, $code)
    }
}

function Get-CounterSnapshot {
    # 発生箇所別カウンタの永続化値(サービスが定期的・停止時に書く)。置換中の一瞬は読めない
    # ことがあるため数回試す。
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $script:MetadataPath)) { return $null }
            $text = [System.Text.Encoding]::UTF8.GetString((Read-AllBytesShared -Path $script:MetadataPath))
            $document = $text | ConvertFrom-Json
            $counters = @{}
            $section = Get-PropValue $document 'Counters'
            if ($null -ne $section) {
                foreach ($property in $section.PSObject.Properties) {
                    if ($null -ne $property.Value) { $counters[$property.Name] = [long]$property.Value }
                }
            }
            return $counters
        }
        catch {
            Start-Sleep -Milliseconds 300
        }
    }
    throw ('could not read {0}' -f $script:MetadataPath)
}

function Format-Counters {
    param([hashtable]$Counters)
    if ($null -eq $Counters) { return '(none)' }
    return ((@($Counters.Keys) | Sort-Object | ForEach-Object { '{0}={1}' -f $_, $Counters[$_] }) -join ', ')
}

function Get-SpoolPendingFiles {
    if (-not (Test-Path -LiteralPath $script:SpoolDir)) { return @() }
    return @(Get-ChildItem -LiteralPath $script:SpoolDir -File | Where-Object { $_.Name -like '*.seg' -or $_.Name -like '*.seg.draining' })
}

function Get-SpoolTokenOccurrences {
    # 未消化のセグメントのうち、トークンを本文に持つ完全なレコードの件数(レコードの本文は
    # UTF-8 の平文で入っている)。書き込み途中の末尾は数えない。
    param([string]$Token)
    $tokenBytes = [System.Text.Encoding]::ASCII.GetBytes($Token)
    $total = 0
    $names = New-Object System.Collections.Generic.List[string]
    $corruptTails = New-Object System.Collections.Generic.List[string]
    foreach ($file in @(Get-SpoolPendingFiles)) {
        $corrupt = $false
        $count = [YaguraUpgradeE2ENative]::CountSpoolRecordsContaining((Read-AllBytesShared -Path $file.FullName), $tokenBytes, [ref]$corrupt)
        if ($corrupt) { $corruptTails.Add($file.Name) }
        if ($count -gt 0) {
            $names.Add($file.Name)
            $total += $count
        }
    }
    return [pscustomobject]@{ Occurrences = $total; Files = $names.ToArray(); CorruptTails = $corruptTails.ToArray() }
}

function Invoke-YaguraDbQuery {
    # 読み取り専用で開く(WAL のため稼働中のサービスとも並行に読める)。
    param([scriptblock]$Body)
    $db = [YaguraUpgradeE2ENative]::OpenDatabase($script:DbPath, $true, 15000)
    try {
        return (& $Body $db)
    }
    finally {
        [YaguraUpgradeE2ENative]::CloseDatabase($db)
    }
}

function Get-DbSnapshot {
    # 蓄積ログの件数と、Id が上限以下の全行の内容のダイジェスト。
    param([long]$UpToId = -1)
    return (Invoke-YaguraDbQuery -Body {
        param($db)
        $maxId = [YaguraUpgradeE2ENative]::QueryInt64($db, 'SELECT IFNULL(MAX(Id), 0) FROM LogRecords')
        $limit = $UpToId
        if ($limit -lt 0) { $limit = $maxId }
        $rows = [long]0
        $sql = ('SELECT Id, ReceivedAt, SourceAddress, SourcePort, Protocol, ParseStatus, DeviceTimestamp, Facility, Severity, ' +
            'Hostname, AppName, ProcId, MsgId, StructuredData, Message, Raw FROM LogRecords WHERE Id <= {0} ORDER BY Id') -f $limit
        $digest = [YaguraUpgradeE2ENative]::QueryDigest($db, $sql, [ref]$rows)
        [pscustomobject]@{
            MaxId        = $maxId
            DigestUpToId = $limit
            DigestRows   = $rows
            Digest       = $digest
        }
    })
}

function Get-DbTokenCount {
    param([string]$Token)
    return (Invoke-YaguraDbQuery -Body {
        param($db)
        [YaguraUpgradeE2ENative]::QueryInt64($db, ("SELECT COUNT(*) FROM LogRecords WHERE instr(Message, '{0}') > 0" -f $Token))
    })
}

function Get-AdminAccountsState {
    return (Invoke-YaguraDbQuery -Body {
        param($db)
        [pscustomobject]@{
            SchemaVersion = [YaguraUpgradeE2ENative]::QueryInt64($db, 'SELECT IFNULL(MAX(Version), 0) FROM AdminAccountsSchemaVersion WHERE Id = 1')
            AccountCount  = [YaguraUpgradeE2ENative]::QueryInt64($db, 'SELECT COUNT(*) FROM AdminAccounts')
        }
    })
}

function Unlock-YaguraDatabase {
    if ($script:LockDb -ne [IntPtr]::Zero) {
        try { [YaguraUpgradeE2ENative]::Execute($script:LockDb, 'ROLLBACK') } catch { Write-E2ELog ('ROLLBACK failed: {0}' -f $_.Exception.Message) }
        [YaguraUpgradeE2ENative]::CloseDatabase($script:LockDb)
        $script:LockDb = [IntPtr]::Zero
    }
}

function Get-FileSha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-FileHashMap {
    # ディレクトリ配下の全ファイル(相対パス -> SHA-256)。
    param([string]$Root)
    $map = @{}
    $rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    foreach ($file in @(Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\')
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Compare-FileHashMap {
    param([hashtable]$Expected, [hashtable]$Actual)
    $differences = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($Expected.Keys | Sort-Object)) {
        if (-not $Actual.ContainsKey($key)) {
            $differences.Add(('missing: {0}' -f $key))
        }
        elseif ($Actual[$key] -ne $Expected[$key]) {
            $differences.Add(('content differs: {0}' -f $key))
        }
    }
    foreach ($key in @($Actual.Keys | Sort-Object)) {
        if (-not $Expected.ContainsKey($key)) {
            $differences.Add(('unexpected: {0}' -f $key))
        }
    }
    return $differences.ToArray()
}

function Get-YaguraEventLogTexts {
    # Application ログのうちソース Yagura の事象(警告以上が載る)。本文が取れない場合は挿入文字列を連結する。
    param([datetime]$Since)
    $events = @()
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $script:ServiceName; StartTime = $Since } -ErrorAction Stop)
    }
    catch {
        if ([string]$_.FullyQualifiedErrorId -notlike '*NoMatchingEventsFound*') { throw }
        $events = @()
    }
    $texts = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $events) {
        $text = $null
        try { $text = $entry.Message } catch { $text = $null }
        if ([string]::IsNullOrEmpty($text)) {
            $text = (@($entry.Properties | ForEach-Object { [string]$_.Value }) -join ' ')
        }
        $texts.Add([pscustomobject]@{ Id = $entry.Id; Level = $entry.Level; TimeCreated = $entry.TimeCreated; Text = [string]$text })
    }
    return $texts.ToArray()
}

function Get-ServiceAccountAceLines {
    param([string]$Path)
    $lines = @(& icacls $Path 2>$null)
    return [pscustomobject]@{
        All     = $lines
        Service = @($lines | Where-Object { $_ -match 'NT SERVICE\\Yagura' -or $_ -match 'S-1-5-80-' })
    }
}

function Assert-DataRootAcl {
    $acl = Get-ServiceAccountAceLines -Path $script:DataRoot
    if ($acl.Service.Count -eq 0) {
        throw ('virtual service account ACE missing on {0}: {1}' -f $script:DataRoot, ($acl.All -join ' | '))
    }
    if (-not ($acl.Service -match '\(M\)') -and -not ($acl.Service -match '\(F\)')) {
        throw ('virtual service account lacks modify on {0}: {1}' -f $script:DataRoot, ($acl.Service -join ' | '))
    }
    return ($acl.Service -join ' | ')
}

function Assert-ForwarderAcl {
    $acl = Get-ServiceAccountAceLines -Path $script:ForwarderDir
    if ($acl.Service.Count -eq 0) {
        throw ('service account ACE missing on {0}: {1}' -f $script:ForwarderDir, ($acl.All -join ' | '))
    }
    if ($acl.Service -match '\(F\)' -or $acl.Service -match '\(M\)' -or $acl.Service -match '\(W\)') {
        throw ('service account can write to {0} (expected read-only): {1}' -f $script:ForwarderDir, ($acl.Service -join ' | '))
    }
    if (-not ($acl.Service -match '\(R\)')) {
        throw ('service account cannot read {0}: {1}' -f $script:ForwarderDir, ($acl.Service -join ' | '))
    }
    return ($acl.Service -join ' | ')
}

function Assert-FirewallRules {
    # @() で包む: 0 件で $null・1 件でスカラーになるため。
    $rules = @(Get-NetFirewallRule -DisplayName 'Yagura*' -ErrorAction SilentlyContinue)
    foreach ($expected in $script:FirewallDisplayNames) {
        $matching = @($rules | Where-Object { $_.DisplayName -eq $expected })
        if ($matching.Count -ne 1) {
            throw ('firewall rule "{0}" must exist exactly once, found {1} (all: {2})' -f $expected, $matching.Count, (@($rules | ForEach-Object { $_.DisplayName }) -join '; '))
        }
        $profileTokens = @(([string]$matching[0].Profile) -split ',' | ForEach-Object { $_.Trim() } | Sort-Object)
        if ($profileTokens.Count -ne 2 -or $profileTokens[0] -ne 'Domain' -or $profileTokens[1] -ne 'Private') {
            throw ('firewall rule "{0}" must have profile Domain+Private exactly, found "{1}"' -f $expected, ([string]$matching[0].Profile))
        }
    }
    return ('{0} rule(s): {1}' -f $rules.Count, (@($rules | ForEach-Object { $_.DisplayName }) -join '; '))
}

function Get-ListenerOwners {
    # 受信・閲覧・管理の各ポートを bind しているプロセス ID。
    $owners = [ordered]@{}
    $owners[('udp/{0}' -f $UdpPort)] = @(@(Get-NetUDPEndpoint -LocalPort $UdpPort -ErrorAction SilentlyContinue) | ForEach-Object { [int]$_.OwningProcess } | Sort-Object -Unique)
    foreach ($port in @(@($TcpPort, $script:ViewerPort, $AdminPort) | Sort-Object -Unique)) {
        $owners[('tcp/{0}' -f $port)] = @(@(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) | ForEach-Object { [int]$_.OwningProcess } | Sort-Object -Unique)
    }
    return $owners
}

function Format-ListenerOwners {
    param($Owners)
    return ((@($Owners.Keys) | ForEach-Object { '{0}={1}' -f $_, (@($Owners[$_]) -join '+') }) -join ', ')
}

function Write-SeedConfiguration {
    # 旧版が知っているキーだけで設定を作る。既存の設定ファイルがあれば他の節は残す。
    param([string]$ProtectedPassword, [int]$SmtpPort)
    $root = New-Object psobject
    if (Test-Path -LiteralPath $script:ConfigPath) {
        $raw = [System.IO.File]::ReadAllText($script:ConfigPath)
        if (-not [string]::IsNullOrWhiteSpace($raw)) { $root = $raw | ConvertFrom-Json }
    }
    foreach ($name in @('Retention', 'Notification')) {
        if ($null -ne $root.PSObject.Properties[$name]) { $root.PSObject.Properties.Remove($name) }
    }
    $root | Add-Member -NotePropertyName 'Retention' -NotePropertyValue ([ordered]@{ Days = 45 })
    $root | Add-Member -NotePropertyName 'Notification' -NotePropertyValue ([ordered]@{
        Email = [ordered]@{
            Enabled = $true
            From    = 'yagura-upgrade-e2e@example.invalid'
            To      = @('ops@example.invalid')
            Smtp    = [ordered]@{
                Host     = '127.0.0.1'
                Port     = $SmtpPort
                # none: 資格情報あり + required 以外は、復号に成功した場合にだけ警告が出る
                # (更新後の新版が実際に復号できたことの観測点に使う)。
                Security = 'none'
                Username = 'yagura-upgrade-e2e'
                Password = $ProtectedPassword
            }
        }
        SourceSilence = [ordered]@{
            Watchlist = @(
                [ordered]@{ Address = '192.0.2.10'; Label = 'yagura-upgrade-e2e'; ThresholdMinutes = 1440 }
            )
        }
    })
    $json = $root | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($script:ConfigPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------
# 出力(証拠)の書き出し
# ---------------------------------------------------------------------------
function Write-E2EOutputs {
    $finishedUtc = [DateTime]::UtcNow
    $overall = 'Passed'
    if ($script:Failed) { $overall = 'Failed' }

    $stamp = $script:StartedUtc.ToString('yyyyMMdd-HHmmss')
    $baseName = 'installer-published-upgrade-e2e-{0}' -f $stamp

    $facts = [ordered]@{}
    foreach ($key in @($script:State.Keys | Sort-Object)) {
        $facts[$key] = $script:State[$key]
    }

    $summary = [pscustomobject]@{
        schemaVersion  = 1
        runId          = $script:RunId
        overall        = $overall
        startedUtc     = $script:StartedUtc.ToString('o')
        finishedUtc    = $finishedUtc.ToString('o')
        sourceMsiPath  = $SourceMsiPath
        targetMsiPath  = $TargetMsiPath
        machine        = [pscustomobject]@{
            computerName = $env:COMPUTERNAME
            osVersion    = [System.Environment]::OSVersion.VersionString
            userName     = $env:USERNAME
        }
        facts          = $facts
        steps          = $script:Steps
        notExercised   = $script:NotExercised
    }

    $jsonPath = Join-Path $OutputDir ($baseName + '.summary.json')
    $logPath = Join-Path $OutputDir ($baseName + '.log.txt')
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($jsonPath, ($summary | ConvertTo-Json -Depth 6), $utf8Bom)
    [System.IO.File]::WriteAllLines($logPath, $script:LogLines, $utf8Bom)

    Write-Host ('Summary JSON: {0}' -f $jsonPath)
    Write-Host ('Log:          {0}' -f $logPath)
    Write-Host ('Overall:      {0}' -f $overall)
}

function Remove-InstalledYaguraProducts {
    # どちらの版が残っているかは中断した位置で変わるため、新版 → 旧版の順に試す
    # (ProductCode が違う MSI では /x が何もしない)。戻り値は残った製品の数。
    param([string]$LogPrefix)
    foreach ($candidate in @(@{ Path = $TargetMsiPath; Label = 'target' }, @{ Path = $SourceMsiPath; Label = 'source' })) {
        if (@(Get-InstalledYaguraProducts).Count -eq 0) { break }
        if (-not (Test-Path -LiteralPath $candidate.Path)) { continue }
        $log = Join-Path $OutputDir ('msiexec-{0}-{1}.log' -f $LogPrefix, $candidate.Label)
        $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList ('/x "{0}" /qn /norestart /l*v "{1}"' -f (Resolve-Path -LiteralPath $candidate.Path).Path, $log) -Wait -PassThru
        Write-E2ELog ('{0} ({1} MSI): msiexec exit code {2}' -f $LogPrefix, $candidate.Label, $proc.ExitCode)
    }
    return @(Get-InstalledYaguraProducts).Count
}

function Install-SourceProduct {
    # 更新元をサイレントインストールし、更新元のバイナリで稼働していることまで確かめる。
    param([string]$LogName)
    $log = Join-Path $OutputDir $LogName
    [void](Invoke-Msiexec -ArgumentString ('/i "{0}" /qn /norestart /l*v "{1}"' -f $SourceMsiPath, $log) -Description 'install of the published source version')
    $script:ProductInstalled = $true
    if (-not (Wait-YaguraServiceStatus -ExpectedStatus 'Running' -TimeoutSec $ServiceStartTimeoutSec)) {
        throw ('source service did not reach Running within {0}s (state: {1})' -f $ServiceStartTimeoutSec, (Get-YaguraServiceState))
    }
    $fileVersion = Get-InstalledHostFileVersion
    if ((Get-VersionCore $fileVersion) -ne (Get-VersionCore $script:State['sourceVersion'])) {
        throw ('installed Yagura.Host.exe FileVersion {0} is not the source version {1}' -f $fileVersion, $script:State['sourceVersion'])
    }
    $products = @(Get-InstalledYaguraProducts)
    if ($products.Count -ne 1 -or $products[0].DisplayVersion -ne $script:State['sourceVersion']) {
        throw ('expected one product entry with version {0}, found: {1}' -f $script:State['sourceVersion'], (@($products | ForEach-Object { '{0} {1}' -f $_.ProductCode, $_.DisplayVersion }) -join '; '))
    }
    $script:State['sourceProductCode'] = $products[0].ProductCode
    $script:State['sourceHostFileVersion'] = $fileVersion
    return ('installed {0} (product {1}, Yagura.Host.exe {2}), service Running' -f $script:State['sourceVersion'], $products[0].ProductCode, $fileVersion)
}

# ---------------------------------------------------------------------------
# 手順本体
# ---------------------------------------------------------------------------
$null = New-Item -ItemType Directory -Force -Path $OutputDir
$OutputDir = (Resolve-Path -Path $OutputDir).Path

Write-E2ELog ('Yagura published-version upgrade E2E starting. RunId={0}' -f $script:RunId)

try {
    # -----------------------------------------------------------------------
    # 0. 事前条件と更新元 MSI の照合(ここが落ちたら以降は何もしない)
    # -----------------------------------------------------------------------
    Invoke-E2EStep -Name 'preflight' -Action {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'administrator privileges are required (msiexec /qn, service control, data root access)'
        }
        foreach ($path in @($SourceMsiPath, $SourceSha256Path, $TargetMsiPath)) {
            if (-not (Test-Path -LiteralPath $path)) { throw ('file not found: {0}' -f $path) }
        }
        # .NET と COM の API は相対パスを PowerShell の現在位置ではなくプロセスの作業ディレクトリで
        # 解決するため、以降で使う前に絶対パスへ直す。
        $script:SourceMsiPath = (Resolve-Path -LiteralPath $SourceMsiPath).Path
        $script:SourceSha256Path = (Resolve-Path -LiteralPath $SourceSha256Path).Path
        $script:TargetMsiPath = (Resolve-Path -LiteralPath $TargetMsiPath).Path
        # 既存の製品・データが残る環境では、どの版が作ったデータかを判定できない。
        if ($null -ne (Get-YaguraService)) { throw 'service "Yagura" already exists; a clean machine is required' }
        if (@(Get-InstalledYaguraProducts).Count -ne 0) { throw 'a Yagura product is already installed; a clean machine is required' }
        if (Test-Path -LiteralPath $script:DataRoot) { throw ('data root {0} already exists; a clean machine is required' -f $script:DataRoot) }

        $winsqlite = Join-Path $env:SystemRoot 'System32\winsqlite3.dll'
        if (-not (Test-Path -LiteralPath $winsqlite)) {
            throw ('{0} not found (needed to lock the database and to read stored logs)' -f $winsqlite)
        }
        if (-not ('YaguraUpgradeE2ENative' -as [type])) {
            # Windows PowerShell 5.1 の Add-Type はコンパイラ警告もエラーとして扱う。
            Add-Type -TypeDefinition $script:NativeSource -Language CSharp -IgnoreWarnings
        }
        $script:State['winsqlite3Version'] = [string](Get-Item -LiteralPath $winsqlite).VersionInfo.FileVersion
        return ('admin=ok, clean machine, winsqlite3 {0}' -f $script:State['winsqlite3Version'])
    }

    Invoke-E2EStep -Name 'source-msi-integrity' -Action {
        $actual = Get-FileSha256 -Path $SourceMsiPath
        $sidecarFields = @(([System.IO.File]::ReadAllText($SourceSha256Path)).Trim() -split '\s+')
        if ($sidecarFields.Count -ne 2) {
            throw ('unexpected .sha256 format: {0}' -f ($sidecarFields -join ' '))
        }
        if ($actual -ne $ExpectedSourceSha256) {
            throw ('SHA-256 of the source MSI ({0}) does not match the pinned value ({1})' -f $actual, $ExpectedSourceSha256)
        }
        if ($sidecarFields[0] -ne $actual) {
            throw ('SHA-256 of the source MSI ({0}) does not match the published .sha256 ({1})' -f $actual, $sidecarFields[0])
        }
        if ($sidecarFields[1] -ne (Split-Path -Leaf $SourceMsiPath)) {
            throw ('the published .sha256 names "{0}", not "{1}"' -f $sidecarFields[1], (Split-Path -Leaf $SourceMsiPath))
        }
        $script:State['sourceSha256'] = $actual
        return ('SHA-256 {0} matches the pinned value and the published .sha256' -f $actual)
    }

    Invoke-E2EStep -Name 'source-msi-signature' -Action {
        $signature = Get-AuthenticodeSignature -LiteralPath $SourceMsiPath
        if ([string]$signature.Status -ne 'Valid') {
            throw ('Authenticode status is {0}: {1}' -f $signature.Status, $signature.StatusMessage)
        }
        $thumbprint = [string]$signature.SignerCertificate.Thumbprint
        if ($thumbprint -ne $ExpectedSourceSignerThumbprint) {
            throw ('signer certificate thumbprint {0} does not match the expected {1}' -f $thumbprint, $ExpectedSourceSignerThumbprint)
        }
        if ($null -eq $signature.TimeStamperCertificate) {
            throw 'the signature has no timestamp (it would stop validating when the certificate expires)'
        }
        $script:State['sourceSignerThumbprint'] = $thumbprint
        $script:State['sourceTimestamperThumbprint'] = [string]$signature.TimeStamperCertificate.Thumbprint
        return ('Valid, signer {0}, timestamped (TSA certificate {1})' -f $thumbprint, $script:State['sourceTimestamperThumbprint'])
    }

    Invoke-E2EStep -Name 'msi-versions' -Action {
        $sourceVersion = Get-MsiProperty -MsiPath $SourceMsiPath -Property 'ProductVersion'
        $targetVersion = Get-MsiProperty -MsiPath $TargetMsiPath -Property 'ProductVersion'
        $sourceUpgradeCode = Get-MsiProperty -MsiPath $SourceMsiPath -Property 'UpgradeCode'
        $targetUpgradeCode = Get-MsiProperty -MsiPath $TargetMsiPath -Property 'UpgradeCode'
        if ($sourceVersion -ne $ExpectedSourceVersion) {
            throw ('source ProductVersion {0} is not the expected {1}' -f $sourceVersion, $ExpectedSourceVersion)
        }
        # 同じ版だと MajorUpgrade にならず、2 つの製品が並んで入る。
        if (-not ([version]$targetVersion -gt [version]$sourceVersion)) {
            throw ('target ProductVersion {0} must be newer than the source {1}' -f $targetVersion, $sourceVersion)
        }
        if ($sourceUpgradeCode -ne $targetUpgradeCode) {
            throw ('UpgradeCode differs (source {0}, target {1}); the target would not upgrade the source' -f $sourceUpgradeCode, $targetUpgradeCode)
        }
        $script:State['sourceVersion'] = $sourceVersion
        $script:State['targetVersion'] = $targetVersion
        $script:State['upgradeCode'] = $targetUpgradeCode
        return ('source {0} -> target {1}, UpgradeCode {2}' -f $sourceVersion, $targetVersion, $targetUpgradeCode)
    }

    # =======================================================================
    # A. 更新の成功経路(新規に入れた更新元から)
    #    途中で落ちても B の失敗注入は別に行う(片方の失敗でもう片方の証拠を失わない)。
    # =======================================================================
    try {
        # -------------------------------------------------------------------
        # A-1. 更新元のインストールと受信
        # -------------------------------------------------------------------
        Invoke-E2EStep -Name 'source-install' -Action {
            return (Install-SourceProduct -LogName 'msiexec-source-install.log')
        }

        Invoke-E2EStep -Name 'source-ingest-udp' -Action {
            $token = Get-Token 'UDPA'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogUdp -Message (New-SyslogLine -Token $token) }
            return ('token {0} visible after {1} datagram(s)' -f $token, $sends)
        }

        # -------------------------------------------------------------------
        # A-2. 旧版にデータを作らせる
        # -------------------------------------------------------------------
        Invoke-E2EStep -Name 'seed-tcp-oversized-frame-and-marker' -Action {
            $token = Get-Token 'TCPB'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogTcp -Lines @(New-SyslogLine -Token $token) -OversizedBytes $script:OversizedTcpFrameBytes }
            return ('token {0} visible after {1} TCP connection(s), each preceded by a {2}-byte oversized frame' -f $token, $sends, $script:OversizedTcpFrameBytes)
        }

        Invoke-E2EStep -Name 'seed-audit-record' -Action {
            Invoke-AuditedViewerAdminRequest
            $recorded = Wait-E2ECondition -TimeoutSec 30 -Probe { (Get-AuditSnapshot).ViewerAdminRejections -ge 1 }
            if (-not $recorded) { throw ('no audit record (3001) appeared under {0}' -f $script:AuditDir) }
            $snapshot = Get-AuditSnapshot
            return ('audit record written ({0} file(s): {1})' -f $snapshot.Files.Count, (@($snapshot.Files | ForEach-Object { '{0}={1}B' -f $_.Name, $_.Length }) -join ', '))
        }

        Invoke-E2EStep -Name 'seed-configuration' -Action {
            Stop-YaguraServiceAndWait
            $entropy = [System.Text.Encoding]::UTF8.GetBytes($script:EmailPasswordEntropy)
            # 使い捨ての値(実在の資格情報ではない)。ログには出さない。
            $plaintext = 'e2e-' + [Guid]::NewGuid().ToString('N')
            $cipher = [YaguraUpgradeE2ENative]::ProtectLocalMachine([System.Text.Encoding]::UTF8.GetBytes($plaintext), $entropy)
            $roundTrip = [System.Text.Encoding]::UTF8.GetString([YaguraUpgradeE2ENative]::UnprotectLocalMachine($cipher, $entropy))
            if ($roundTrip -ne $plaintext) { throw 'DPAPI round trip failed in the test harness itself' }
            $smtpPort = Get-FreeTcpPort
            Write-SeedConfiguration -ProtectedPassword ('dpapi:' + [Convert]::ToBase64String($cipher)) -SmtpPort $smtpPort
            Start-YaguraServiceAndWait

            # 旧版がこの設定で起動し、受信を続けること。
            $token = Get-Token 'CONF'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogUdp -Message (New-SyslogLine -Token $token) }
            $script:State['smtpPort'] = $smtpPort
            return ('configuration written (Retention:Days, Notification:Email with a dpapi: password, SourceSilence watchlist); source restarted and received token {0} after {1} datagram(s)' -f $token, $sends)
        }

        # 送り直しのループは「見えた」時点で抜けるため、直前に送った分がまだ保存途中のことがある。
        # 件数が落ち着いてから基準にする(落ち着く前に DB をロックすると、その分がスプール経由で
        # 更新後に増え、件数の一致を誤って崩す)。
        Invoke-E2EStep -Name 'seed-viewer-baseline' -Action {
            $suffixes = @('UDPA', 'TCPB', 'CONF')
            $previous = $null
            $counts = $null
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ($true) {
                $counts = [ordered]@{}
                foreach ($suffix in $suffixes) { $counts[$suffix] = Get-ViewerTokenCount -Token (Get-Token $suffix) }
                $current = (@($counts.Values) | ForEach-Object { [string]$_ }) -join ','
                if ($null -ne $previous -and $current -eq $previous) { break }
                if ([DateTime]::UtcNow -ge $deadline) { throw ('row counts did not settle within 60s (last: {0})' -f $current) }
                $previous = $current
                Start-Sleep -Seconds 3
            }
            foreach ($suffix in $suffixes) {
                if ($counts[$suffix] -lt 1) { throw ('token {0} is not visible before the upgrade (count {1})' -f (Get-Token $suffix), $counts[$suffix]) }
            }
            $script:State['viewerCountsBeforeUpgrade'] = $counts
            return ('rows per token before the upgrade: {0}' -f ((@($counts.Keys) | ForEach-Object { '{0}={1}' -f $_, $counts[$_] }) -join ', '))
        }

        # 未消化のスプールセグメントを残す: DB の書き込みロックを握ると、保存がタイムアウトして
        # メッセージがスプールへ退避される。ロックを握ったまま停止させれば、退避分は DB に入らずに残る。
        # 送るのは 1 通ずつにする: 保存待ちのバッチを抱えたまま停止させると、停止の打ち切りと退避が
        # 競合してセグメントの末尾が途中で切れうる(検証の側が作った不具合を拾ってしまう)。
        Invoke-E2EStep -Name 'seed-spool-backlog' -Action {
            $token = Get-Token 'SPOL'
            $script:LockDb = [YaguraUpgradeE2ENative]::OpenDatabase($script:DbPath, $false, 15000)
            [YaguraUpgradeE2ENative]::Execute($script:LockDb, 'BEGIN IMMEDIATE')

            $script:SpoolSendCount = 0
            $deadline = [DateTime]::UtcNow.AddSeconds($SpoolTimeoutSec)
            $spooled = $null
            while ([DateTime]::UtcNow -lt $deadline -and ($null -eq $spooled -or $spooled.Occurrences -eq 0)) {
                Send-SyslogUdp -Message (New-SyslogLine -Token $token)
                $script:SpoolSendCount++
                # 保存の打ち切り(バッチのタイムアウトまたは SQLite の busy 待ち)を待ってから再送する。
                $resendAt = [DateTime]::UtcNow.AddSeconds(45)
                while ([DateTime]::UtcNow -lt $resendAt -and [DateTime]::UtcNow -lt $deadline) {
                    Start-Sleep -Seconds 3
                    $spooled = Get-SpoolTokenOccurrences -Token $token
                    if ($spooled.Occurrences -gt 0) { break }
                }
            }
            if ($null -eq $spooled -or $spooled.Occurrences -eq 0) {
                throw ('token {0} was not evacuated to {1} within {2}s while the database was locked ({3} datagram(s) sent)' -f $token, $script:SpoolDir, $SpoolTimeoutSec, $script:SpoolSendCount)
            }
            # 再送した分の退避が済むまで待つ(件数が 2 回続けて同じになるまで)。
            $settled = $spooled.Occurrences
            for ($i = 0; $i -lt 20; $i++) {
                Start-Sleep -Seconds 5
                $now = (Get-SpoolTokenOccurrences -Token $token).Occurrences
                if ($now -eq $settled -and $now -ge $script:SpoolSendCount) { break }
                $settled = $now
            }

            Stop-YaguraServiceAndWait
            Unlock-YaguraDatabase

            $remaining = Get-SpoolTokenOccurrences -Token $token
            if ($remaining.Occurrences -eq 0) {
                throw 'the spooled records disappeared while the source service was stopping'
            }
            $inDatabase = Get-DbTokenCount -Token $token
            if ($inDatabase -ne 0) {
                throw ('{0} row(s) with token {1} are already in the database; the spool is not the only copy' -f $inDatabase, $token)
            }
            $script:State['spoolSendCount'] = $script:SpoolSendCount
            $script:State['spoolOccurrencesBeforeUpgrade'] = $remaining.Occurrences
            $script:State['spoolFilesBeforeUpgrade'] = $remaining.Files
            $script:State['spoolCorruptTailsBeforeUpgrade'] = $remaining.CorruptTails
            return ('{0} complete record(s) with token {1} left in {2} segment(s) ({3}); none in the database; {4} datagram(s) sent; segments with a torn tail: {5}' -f $remaining.Occurrences, $token, $remaining.Files.Count, ($remaining.Files -join ', '), $script:SpoolSendCount, $remaining.CorruptTails.Count)
        }

        # -------------------------------------------------------------------
        # A-3. 停止状態のスナップショット
        # -------------------------------------------------------------------
        Invoke-E2EStep -Name 'snapshot-before-upgrade' -Action {
            if (-not (Test-YaguraServiceStatus -ExpectedStatus 'Stopped')) { throw ('service is not stopped (state: {0})' -f (Get-YaguraServiceState)) }

            $script:State['configSha256BeforeUpgrade'] = Get-FileSha256 -Path $script:ConfigPath

            $counters = Get-CounterSnapshot
            if ($null -eq $counters) { throw ('{0} not found' -f $script:MetadataPath) }
            foreach ($required in @('TcpMessageOversizedDiscarded', 'SpoolEvacuated')) {
                if (-not $counters.ContainsKey($required) -or $counters[$required] -lt 1) {
                    throw ('counter {0} is not positive before the upgrade (continuity would be checked against zero): {1}' -f $required, (Format-Counters $counters))
                }
            }
            $script:State['countersBeforeUpgrade'] = $counters

            $audit = Get-AuditSnapshot
            if ($audit.Files.Count -eq 0) { throw 'no audit file before the upgrade' }
            $script:State['auditBeforeUpgrade'] = $audit

            $db = Get-DbSnapshot
            $script:State['dbBeforeUpgrade'] = $db

            $accounts = Get-AdminAccountsState
            $script:State['adminAccountsBeforeUpgrade'] = $accounts

            return ('config {0}; counters: {1}; audit: {2} file(s), {3} rejection record(s); LogRecords rows<= {4}: {5} (digest {6}); AdminAccounts schema v{7}, {8} account(s)' -f
                $script:State['configSha256BeforeUpgrade'], (Format-Counters $counters), $audit.Files.Count, $audit.ViewerAdminRejections,
                $db.DigestUpToId, $db.DigestRows, $db.Digest, $accounts.SchemaVersion, $accounts.AccountCount)
        }

        # -------------------------------------------------------------------
        # A-4. 更新
        # -------------------------------------------------------------------
        Invoke-E2EStep -Name 'upgrade-to-target' -Action {
            $script:UpgradeStartedAt = Get-Date
            $log = Join-Path $OutputDir 'msiexec-upgrade.log'
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $exitCode = Invoke-Msiexec -ArgumentString ('/i "{0}" /qn /norestart /l*v "{1}"' -f $TargetMsiPath, $log) -Description ('major upgrade from {0} to {1}' -f $script:State['sourceVersion'], $script:State['targetVersion'])
            $sw.Stop()
            $script:State['upgradeMsiexecMs'] = [long]$sw.ElapsedMilliseconds
            return ('msiexec exit code {0} in {1} ms (from a stopped service; this is not an ingestion-gap measurement)' -f $exitCode, $sw.ElapsedMilliseconds)
        }

        # -------------------------------------------------------------------
        # A-5. 更新後の検証(全項目の結果を残すため、失敗しても続ける)
        # -------------------------------------------------------------------
        Invoke-E2EStep -Name 'verify-target-service-running' -ContinueOnFailure $true -Action {
            if (-not (Wait-YaguraServiceStatus -ExpectedStatus 'Running' -TimeoutSec $ServiceStartTimeoutSec)) {
                throw ('service is not Running after the upgrade (state: {0}); the old product removal may have stopped or deleted the new service' -f (Get-YaguraServiceState))
            }
            $process = Get-YaguraServiceProcess
            if ($null -eq $process) { throw 'could not resolve the service process' }
            if ($process.Path -ne $script:HostExePath) { throw ('service process image is {0}, expected {1}' -f $process.Path, $script:HostExePath) }
            if ((Get-VersionCore $process.FileVersion) -ne (Get-VersionCore $script:State['targetVersion'])) {
                throw ('the running service binary is version {0}, not the target {1}' -f $process.FileVersion, $script:State['targetVersion'])
            }
            if ($process.StartedAt -lt $script:UpgradeStartedAt) {
                throw ('the service process started at {0:o}, before the upgrade began ({1:o})' -f $process.StartedAt, $script:UpgradeStartedAt)
            }
            $script:State['targetServicePid'] = $process.ProcessId
            $script:State['targetHostFileVersion'] = $process.FileVersion
            return ('target service Running (pid {0}, Yagura.Host.exe {1}, started {2:o})' -f $process.ProcessId, $process.FileVersion, $process.StartedAt)
        }

        Invoke-E2EStep -Name 'verify-listeners-bound-by-target-process' -ContinueOnFailure $true -Action {
            if (-not $script:State.ContainsKey('targetServicePid')) { throw 'target service process unknown (see verify-target-service-running)' }
            $expectedPid = [int]$script:State['targetServicePid']
            $script:ListenerOwners = $null
            $bound = Wait-E2ECondition -TimeoutSec $VerifyTimeoutSec -Probe {
                $script:ListenerOwners = Get-ListenerOwners
                $ok = $true
                foreach ($key in @($script:ListenerOwners.Keys)) {
                    $pids = @($script:ListenerOwners[$key])
                    if ($pids.Count -eq 0 -or @($pids | Where-Object { $_ -ne $expectedPid }).Count -gt 0) { $ok = $false }
                }
                $ok
            }
            if (-not $bound) {
                throw ('not every listener is bound by the target process {0}: {1}' -f $expectedPid, (Format-ListenerOwners $script:ListenerOwners))
            }
            return ('all listeners bound by pid {0}: {1}' -f $expectedPid, (Format-ListenerOwners $script:ListenerOwners))
        }

        Invoke-E2EStep -Name 'verify-single-product-entry' -ContinueOnFailure $true -Action {
            $products = @(Get-InstalledYaguraProducts)
            if ($products.Count -ne 1) {
                throw ('expected exactly 1 product entry, found {0}: {1}' -f $products.Count, (@($products | ForEach-Object { '{0} {1}' -f $_.ProductCode, $_.DisplayVersion }) -join '; '))
            }
            if ($products[0].DisplayVersion -ne $script:State['targetVersion']) {
                throw ('the product entry shows version {0}, expected {1}' -f $products[0].DisplayVersion, $script:State['targetVersion'])
            }
            if ($products[0].ProductCode -eq $script:State['sourceProductCode']) {
                throw ('the product code is still the source one ({0})' -f $products[0].ProductCode)
            }
            return ('one product entry: {0} {1}' -f $products[0].ProductCode, $products[0].DisplayVersion)
        }

        # 版だけ上がって中身が旧版のまま残るファイル(Issue #479・#514)と、旧版にしか無いファイルの
        # 取り残しを捉える。期待値は更新先 MSI の管理者用展開(システムを変更しない)から作る。
        Invoke-E2EStep -Name 'verify-installed-files-match-target-msi' -ContinueOnFailure $true -Action {
            $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('yagura-upgrade-e2e-admin-' + $script:RunId)
            try {
                $log = Join-Path $OutputDir 'msiexec-target-admin-extract.log'
                [void](Invoke-Msiexec -ArgumentString ('/a "{0}" /qn TARGETDIR="{1}" /l*v "{2}"' -f $TargetMsiPath, $extractRoot, $log) -Description 'administrative extraction of the target MSI')
                $hostExe = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter 'Yagura.Host.exe')
                if ($hostExe.Count -ne 1) { throw ('expected one Yagura.Host.exe in the administrative image, found {0}' -f $hostExe.Count) }
                $expected = Get-FileHashMap -Root $hostExe[0].DirectoryName
                $actual = Get-FileHashMap -Root $script:InstallDir
                $differences = @(Compare-FileHashMap -Expected $expected -Actual $actual)
                if ($differences.Count -gt 0) {
                    throw ('installed files differ from the target MSI ({0} difference(s)): {1}' -f $differences.Count, (($differences | Select-Object -First 15) -join '; '))
                }
                return ('all {0} installed file(s) are byte-identical to the target MSI; no leftover file from the source' -f $actual.Count)
            }
            finally {
                Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        Invoke-E2EStep -Name 'verify-configuration-preserved' -ContinueOnFailure $true -Action {
            $after = Get-FileSha256 -Path $script:ConfigPath
            if ($after -ne $script:State['configSha256BeforeUpgrade']) {
                throw ('yagura.json changed during the upgrade ({0} -> {1})' -f $script:State['configSha256BeforeUpgrade'], $after)
            }
            return ('yagura.json byte-identical ({0})' -f $after)
        }

        # 資格情報あり + Security=none の警告は、パスワードの復号に成功した場合にだけ出る
        # (失敗すると Password キーの警告を出してメール通知を無効化し、Security の判定まで進まない)。
        Invoke-E2EStep -Name 'verify-dpapi-value-decrypted-by-target' -ContinueOnFailure $true -Action {
            $script:TargetEventTexts = @()
            $observed = Wait-E2ECondition -TimeoutSec 60 -Probe {
                $script:TargetEventTexts = @(Get-YaguraEventLogTexts -Since $script:UpgradeStartedAt)
                @($script:TargetEventTexts | Where-Object { $_.Text.Contains('Notification:Email:Smtp:Security') -and $_.Text.Contains(' none ') }).Count -gt 0
            }
            $failures = @($script:TargetEventTexts | Where-Object { $_.Text.Contains('Notification:Email:Smtp:Password') })
            if ($failures.Count -gt 0) {
                throw ('the target version reported a problem with Notification:Email:Smtp:Password (event id {0}); the dpapi: value was not decrypted' -f $failures[0].Id)
            }
            if (-not $observed) {
                throw ('no Notification:Email:Smtp:Security warning from the target version within 60s ({0} Yagura event(s) since the upgrade); decryption success could not be observed' -f $script:TargetEventTexts.Count)
            }
            return 'the target version decrypted the dpapi: SMTP password written under the source version (Security=none warning observed, no Password warning)'
        }

        Invoke-E2EStep -Name 'verify-configuration-keys-accepted' -ContinueOnFailure $true -Action {
            $texts = @(Get-YaguraEventLogTexts -Since $script:UpgradeStartedAt)
            $keys = @(
                'Retention:Days', 'Notification:SourceSilence', 'Notification:Email:Enabled', 'Notification:Email:From',
                'Notification:Email:To', 'Notification:Email:Smtp:Host', 'Notification:Email:Smtp:Port', 'Notification:Email:Smtp:Username'
            )
            $hits = New-Object System.Collections.Generic.List[string]
            foreach ($key in $keys) {
                foreach ($entry in @($texts | Where-Object { $_.Text.Contains($key) })) {
                    $hits.Add(('{0} (event id {1})' -f $key, $entry.Id))
                }
            }
            if ($hits.Count -gt 0) {
                throw ('the target version warned about configuration written under the source version: {0}' -f ($hits -join '; '))
            }
            return ('no warning about the seeded keys among {0} Yagura event(s) since the upgrade' -f $texts.Count)
        }

        Invoke-E2EStep -Name 'verify-spool-backlog-drained' -ContinueOnFailure $true -Action {
            $token = Get-Token 'SPOL'
            $expected = [int]$script:State['spoolOccurrencesBeforeUpgrade']
            $script:SpoolViewerCount = -1
            $drained = Wait-E2ECondition -TimeoutSec $SpoolTimeoutSec -IntervalMs 2000 -Probe {
                $script:SpoolViewerCount = Get-ViewerTokenCount -Token $token
                ((Get-SpoolTokenOccurrences -Token $token).Occurrences -eq 0) -and ($script:SpoolViewerCount -ge $expected)
            }
            if (-not $drained) {
                $left = Get-SpoolTokenOccurrences -Token $token
                throw ('spool backlog not drained within {0}s: {1} record(s) still in {2}; {3} of {4} visible in the viewer' -f $SpoolTimeoutSec, $left.Occurrences, ($left.Files -join ', '), $script:SpoolViewerCount, $expected)
            }
            $script:State['spoolRowsAfterUpgrade'] = $script:SpoolViewerCount
            return ('all {0} spooled record(s) written by the source were drained by the target ({1} row(s) visible)' -f $expected, $script:SpoolViewerCount)
        }

        Invoke-E2EStep -Name 'verify-stored-logs-preserved' -ContinueOnFailure $true -Action {
            $before = $script:State['dbBeforeUpgrade']
            $after = Get-DbSnapshot -UpToId $before.DigestUpToId
            if ($after.DigestRows -ne $before.DigestRows -or $after.Digest -ne $before.Digest) {
                throw ('stored rows with Id <= {0} changed: {1} rows / {2} -> {3} rows / {4}' -f $before.DigestUpToId, $before.DigestRows, $before.Digest, $after.DigestRows, $after.Digest)
            }
            $baseline = $script:State['viewerCountsBeforeUpgrade']
            $mismatches = New-Object System.Collections.Generic.List[string]
            foreach ($suffix in @($baseline.Keys)) {
                $count = Get-ViewerTokenCount -Token (Get-Token $suffix)
                if ($count -ne $baseline[$suffix]) { $mismatches.Add(('{0}: {1} -> {2}' -f $suffix, $baseline[$suffix], $count)) }
            }
            if ($mismatches.Count -gt 0) {
                throw ('rows visible in the viewer changed: {0}' -f ($mismatches -join '; '))
            }
            return ('all {0} stored row(s) with Id <= {1} are identical (digest {2}); viewer counts unchanged ({3})' -f $after.DigestRows, $before.DigestUpToId, $after.Digest, ((@($baseline.Keys) | ForEach-Object { '{0}={1}' -f $_, $baseline[$_] }) -join ', '))
        }

        Invoke-E2EStep -Name 'verify-admin-accounts-schema-migrated' -ContinueOnFailure $true -Action {
            $before = $script:State['adminAccountsBeforeUpgrade']
            # 新版はスキーマ移行を起動経路の外で行う(ADR-0023 決定 1)ため、少し待つ。
            $script:AdminAccountsAfter = $null
            $migrated = Wait-E2ECondition -TimeoutSec 60 -Probe {
                $script:AdminAccountsAfter = Get-AdminAccountsState
                $script:AdminAccountsAfter.SchemaVersion -gt $before.SchemaVersion
            }
            if (-not $migrated) {
                throw ('AdminAccounts schema version did not advance from v{0} (now v{1})' -f $before.SchemaVersion, $script:AdminAccountsAfter.SchemaVersion)
            }
            if ($script:AdminAccountsAfter.AccountCount -ne $before.AccountCount) {
                throw ('AdminAccounts row count changed: {0} -> {1}' -f $before.AccountCount, $script:AdminAccountsAfter.AccountCount)
            }
            return ('AdminAccounts schema v{0} -> v{1}, {2} account(s) (an account created under the source is not exercised; see notExercised)' -f $before.SchemaVersion, $script:AdminAccountsAfter.SchemaVersion, $script:AdminAccountsAfter.AccountCount)
        }

        Invoke-E2EStep -Name 'verify-ingestion-udp-and-viewer' -ContinueOnFailure $true -Action {
            $token = Get-Token 'UDPC'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogUdp -Message (New-SyslogLine -Token $token) }
            if (-not (Test-ViewerSearchPageContainsToken -Token $token)) {
                throw ('token {0} is stored but not on the search page' -f $token)
            }
            # 閲覧画面は prerender だけでも表示されるため、対話機能に要るスクリプトの配信を別に見る。
            $framework = Invoke-WebRequest -Uri ('{0}/_framework/blazor.web.js' -f $ViewerBaseUrl.TrimEnd('/')) -UseBasicParsing -TimeoutSec 15
            if ([int]$framework.StatusCode -ne 200 -or $framework.Content.Length -lt 1024) {
                throw ('blazor.web.js returned HTTP {0}, {1} bytes (the interactive circuit cannot start)' -f $framework.StatusCode, $framework.Content.Length)
            }
            return ('UDP token {0} received and shown on the search page after {1} datagram(s); blazor.web.js served' -f $token, $sends)
        }

        Invoke-E2EStep -Name 'verify-ingestion-tcp' -ContinueOnFailure $true -Action {
            $token = Get-Token 'TCPD'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogTcp -Lines @(New-SyslogLine -Token $token) -OversizedBytes $script:OversizedTcpFrameBytes }
            return ('TCP token {0} received after {1} connection(s)' -f $token, $sends)
        }

        Invoke-E2EStep -Name 'verify-audit-preserved-and-appended' -ContinueOnFailure $true -Action {
            $before = $script:State['auditBeforeUpgrade']
            foreach ($file in $before.Files) {
                $path = Join-Path $script:AuditDir $file.Name
                if (-not (Test-Path -LiteralPath $path)) { throw ('audit file {0} disappeared during the upgrade' -f $file.Name) }
                $bytes = Read-AllBytesShared -Path $path
                if ($bytes.Length -lt $file.Length) { throw ('audit file {0} shrank ({1} -> {2} bytes)' -f $file.Name, $file.Length, $bytes.Length) }
                if ((Get-Sha256Hex -Bytes $bytes -Offset 0 -Count ([int]$file.Length)) -ne $file.Sha256) {
                    throw ('the first {0} bytes of audit file {1} changed during the upgrade' -f $file.Length, $file.Name)
                }
            }
            Invoke-AuditedViewerAdminRequest
            $appended = Wait-E2ECondition -TimeoutSec 30 -Probe { (Get-AuditSnapshot).ViewerAdminRejections -gt $before.ViewerAdminRejections }
            if (-not $appended) { throw 'the target version did not append an audit record (3001)' }
            $after = Get-AuditSnapshot
            return ('{0} audit file(s) from the source preserved byte-for-byte; target appended (rejection records {1} -> {2})' -f $before.Files.Count, $before.ViewerAdminRejections, $after.ViewerAdminRejections)
        }

        # 新版は永続化値を初期値として読み込み、その上に積む。更新後に上限超過フレームを送って
        # いるので、TcpMessageOversizedDiscarded は更新前より必ず大きくなる。
        Invoke-E2EStep -Name 'verify-counters-continuous' -ContinueOnFailure $true -Action {
            $before = $script:State['countersBeforeUpgrade']
            $script:CountersAfter = $null
            $continuous = Wait-E2ECondition -TimeoutSec 60 -IntervalMs 2000 -Probe {
                $script:CountersAfter = Get-CounterSnapshot
                if ($null -eq $script:CountersAfter) { return $false }
                foreach ($key in @($before.Keys)) {
                    if (-not $script:CountersAfter.ContainsKey($key) -or $script:CountersAfter[$key] -lt $before[$key]) { return $false }
                }
                $script:CountersAfter['TcpMessageOversizedDiscarded'] -gt $before['TcpMessageOversizedDiscarded']
            }
            if (-not $continuous) {
                throw ('counters are not continuous across the upgrade. before: {0} / after: {1}' -f (Format-Counters $before), (Format-Counters $script:CountersAfter))
            }
            return ('counters continued from the source values. before: {0} / after: {1}' -f (Format-Counters $before), (Format-Counters $script:CountersAfter))
        }

        Invoke-E2EStep -Name 'verify-firewall-rules' -ContinueOnFailure $true -Action {
            return (Assert-FirewallRules)
        }

        Invoke-E2EStep -Name 'verify-service-account-records' -ContinueOnFailure $true -Action {
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='Yagura'"
            if ($service.StartName -ne 'NT SERVICE\Yagura') { throw ('service StartName is "{0}"' -f $service.StartName) }
            $recorded = Get-PropValue (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Yagura' -ErrorAction SilentlyContinue) 'ServiceAccount'
            if ($recorded -ne 'NT SERVICE\Yagura') { throw ('HKLM\SOFTWARE\Yagura\ServiceAccount is "{0}"' -f $recorded) }
            $iniPath = Join-Path $script:DataRoot 'service-account.ini'
            if (-not (Test-Path -LiteralPath $iniPath) -or (Get-Content -LiteralPath $iniPath -Raw) -notmatch [regex]::Escape('Account=NT SERVICE\Yagura')) {
                throw ('service-account.ini missing or not recording NT SERVICE\Yagura: {0}' -f $iniPath)
            }
            return 'service runs as NT SERVICE\Yagura; registry record and service-account.ini written by the target'
        }

        Invoke-E2EStep -Name 'verify-remembered-options-and-shortcuts' -ContinueOnFailure $true -Action {
            $key = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Yagura' -ErrorAction SilentlyContinue
            foreach ($name in @('FirewallOptIn', 'DesktopShortcutOptIn')) {
                $value = [string](Get-PropValue $key $name)
                if ($value -ne '1') { throw ('HKLM\SOFTWARE\Yagura\{0} is "{1}" (the source opt-in was not carried over)' -f $name, $value) }
            }
            foreach ($path in @($script:StartMenuAdminShortcut, $script:PublicDesktopShortcut)) {
                if (-not (Test-Path -LiteralPath $path)) { throw ('shortcut missing after the upgrade: {0}' -f $path) }
            }
            return 'FirewallOptIn=1 and DesktopShortcutOptIn=1 carried over; start menu admin shortcut and desktop viewer shortcut present'
        }

        Invoke-E2EStep -Name 'verify-data-root-and-forwarder-acl' -ContinueOnFailure $true -Action {
            return ('data root: {0} / forwarder: {1}' -f (Assert-DataRootAcl), (Assert-ForwarderAcl))
        }

        Invoke-E2EStep -Name 'verify-downgrade-rejected' -ContinueOnFailure $true -Action {
            $before = Get-YaguraServiceProcess
            $log = Join-Path $OutputDir 'msiexec-downgrade-attempt.log'
            $exitCode = Invoke-Msiexec -ArgumentString ('/i "{0}" /qn /norestart /l*v "{1}"' -f $SourceMsiPath, $log) -Description 'running the source MSI over the target (downgrade)' -ExpectFailure
            $products = @(Get-InstalledYaguraProducts)
            if ($products.Count -ne 1 -or $products[0].DisplayVersion -ne $script:State['targetVersion']) {
                throw ('product entries changed by the rejected downgrade: {0}' -f (@($products | ForEach-Object { '{0} {1}' -f $_.ProductCode, $_.DisplayVersion }) -join '; '))
            }
            $after = Get-YaguraServiceProcess
            if ($null -eq $before -or $null -eq $after -or $before.ProcessId -ne $after.ProcessId) {
                throw 'the service process changed during the rejected downgrade'
            }
            return ('downgrade rejected (msiexec exit code {0}); product and service (pid {1}) untouched' -f $exitCode, $after.ProcessId)
        }

        Invoke-E2EStep -Name 'upgrade-cleanup-uninstall' -ContinueOnFailure $true -Action {
            $log = Join-Path $OutputDir 'msiexec-upgrade-cleanup-uninstall.log'
            [void](Invoke-Msiexec -ArgumentString ('/x "{0}" /qn /norestart /l*v "{1}"' -f $TargetMsiPath, $log) -Description 'uninstall of the upgraded product')
            $script:ProductInstalled = $false
            return 'uninstalled'
        }
    }
    catch {
        # 失敗は Invoke-E2EStep が記録済み。B の失敗注入へ進む。
        Write-E2ELog ('upgrade scenario aborted: {0}' -f $_.Exception.Message)
    }
    finally {
        try { Unlock-YaguraDatabase } catch { Write-E2ELog ('releasing the database lock failed: {0}' -f $_.Exception.Message) }
    }

    # =======================================================================
    # B. 失敗注入: 更新が失敗しても旧版が残り、受信が再開すること
    #    A と混ぜない(失敗した更新の後に成功させると、成功側の結果に失敗の後始末が混ざる)。
    #    新しく入れ直した更新元を起点にする。
    #    実在しない gMSA 形式のアカウントは静的検証を通り、InstallServices より後
    #    (ACL 付替または StartServices)で失敗する。データルートには触れない細工なので、
    #    ロールバック後の旧版は元の構成のまま起動できる(installer-e2e と同じ手法)。
    # =======================================================================
    try {
        Invoke-E2EStep -Name 'failure-reset-machine' -Action {
            if ((Remove-InstalledYaguraProducts -LogPrefix 'failure-reset-uninstall') -ne 0) {
                throw ('a Yagura product is still installed: {0}' -f (@(Get-InstalledYaguraProducts | ForEach-Object { '{0} {1}' -f $_.ProductCode, $_.DisplayVersion }) -join '; '))
            }
            $script:ProductInstalled = $false
            if (-not (Wait-E2ECondition -TimeoutSec 60 -Probe { $null -eq (Get-YaguraService) })) {
                throw 'service "Yagura" still exists after uninstall'
            }
            # 前の経路が作ったデータ(新版が移行済みの DB を含む)を、入れ直す更新元に読ませない。
            if (Test-Path -LiteralPath $script:DataRoot) {
                Remove-Item -LiteralPath $script:DataRoot -Recurse -Force
            }
            return ('no product installed, data root {0} removed' -f $script:DataRoot)
        }

        Invoke-E2EStep -Name 'failure-source-install' -Action {
            return (Install-SourceProduct -LogName 'msiexec-failure-source-install.log')
        }

        Invoke-E2EStep -Name 'failure-source-ingest-udp' -Action {
            $token = Get-Token 'FBAS'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogUdp -Message (New-SyslogLine -Token $token) }
            return ('token {0} visible after {1} datagram(s)' -f $token, $sends)
        }

        Invoke-E2EStep -Name 'failure-attempt' -Action {
            $script:PreFailureInstallDir = Get-FileHashMap -Root $script:InstallDir
            $log = Join-Path $OutputDir 'msiexec-failure-attempt.log'
            $exitCode = Invoke-Msiexec -ArgumentString ('/i "{0}" /qn /norestart YAGURA_SERVICE_ACCOUNT=YAGURAE2E\nosuchgmsa$ /l*v "{1}"' -f $TargetMsiPath, $log) -Description 'sabotaged upgrade from the source version' -ExpectFailure
            return ('sabotaged upgrade (running source service) failed as expected (msiexec exit code {0})' -f $exitCode)
        }

        Invoke-E2EStep -Name 'failure-source-product-survives' -ContinueOnFailure $true -Action {
            $products = @(Get-InstalledYaguraProducts)
            if ($products.Count -eq 0) {
                throw 'no Yagura product remains after the failed upgrade (the old product was removed before the new one failed)'
            }
            if ($products.Count -ne 1 -or $products[0].ProductCode -ne $script:State['sourceProductCode'] -or $products[0].DisplayVersion -ne $script:State['sourceVersion']) {
                throw ('expected only the source product {0} ({1}), found: {2}' -f $script:State['sourceProductCode'], $script:State['sourceVersion'], (@($products | ForEach-Object { '{0} {1}' -f $_.ProductCode, $_.DisplayVersion }) -join '; '))
            }
            return ('the source product {0} ({1}) is the only entry' -f $products[0].ProductCode, $products[0].DisplayVersion)
        }

        Invoke-E2EStep -Name 'failure-source-service-running' -ContinueOnFailure $true -Action {
            if (-not (Wait-YaguraServiceStatus -ExpectedStatus 'Running' -TimeoutSec $ServiceStartTimeoutSec)) {
                throw ('service is not Running after the rolled-back upgrade (state: {0})' -f (Get-YaguraServiceState))
            }
            $process = Get-YaguraServiceProcess
            if ($null -eq $process) { throw 'could not resolve the service process' }
            if ($process.Path -ne $script:HostExePath) {
                throw ('service process image is {0}, expected {1}' -f $process.Path, $script:HostExePath)
            }
            if ((Get-VersionCore $process.FileVersion) -ne (Get-VersionCore $script:State['sourceVersion'])) {
                throw ('the running service binary is version {0}, not the source {1} (the rollback did not restore the old binaries)' -f $process.FileVersion, $script:State['sourceVersion'])
            }
            return ('source service Running again (pid {0}, Yagura.Host.exe {1})' -f $process.ProcessId, $process.FileVersion)
        }

        Invoke-E2EStep -Name 'failure-install-dir-restored' -ContinueOnFailure $true -Action {
            $after = Get-FileHashMap -Root $script:InstallDir
            $differences = @(Compare-FileHashMap -Expected $script:PreFailureInstallDir -Actual $after)
            if ($differences.Count -gt 0) {
                throw ('install directory differs from before the failed upgrade ({0} difference(s)): {1}' -f $differences.Count, (($differences | Select-Object -First 10) -join '; '))
            }
            return ('all {0} file(s) under {1} are byte-identical to before the failed upgrade' -f $after.Count, $script:InstallDir)
        }

        Invoke-E2EStep -Name 'failure-service-config-rolled-back' -ContinueOnFailure $true -Action {
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='Yagura'"
            if ($service.StartName -ne 'NT SERVICE\Yagura') {
                throw ('service StartName is "{0}" after the rolled-back upgrade (expected NT SERVICE\Yagura)' -f $service.StartName)
            }
            # 更新元はこの記録を書かない。残っていれば失敗した更新の書き込みが戻っていない。
            $recorded = Get-PropValue (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Yagura' -ErrorAction SilentlyContinue) 'ServiceAccount'
            if ($null -ne $recorded) {
                throw ('HKLM\SOFTWARE\Yagura\ServiceAccount="{0}" remains after the rolled-back upgrade' -f $recorded)
            }
            return 'service runs as NT SERVICE\Yagura; no ServiceAccount record left behind'
        }

        Invoke-E2EStep -Name 'failure-data-root-acl-restored' -ContinueOnFailure $true -Action {
            return ('data root still grants the virtual service account modify: {0}' -f (Assert-DataRootAcl))
        }

        Invoke-E2EStep -Name 'failure-ingestion-resumes' -ContinueOnFailure $true -Action {
            $token = Get-Token 'FAIL'
            $sends = Wait-TokenIngested -Token $token -Send { Send-SyslogUdp -Message (New-SyslogLine -Token $token) }
            $earlier = Get-ViewerTokenCount -Token (Get-Token 'FBAS')
            if ($earlier -lt 1) { throw ('the token received before the failed upgrade is no longer visible (count {0})' -f $earlier) }
            return ('ingestion resumed (token {0} after {1} datagram(s)); the token received before the failed upgrade is still visible' -f $token, $sends)
        }

        Invoke-E2EStep -Name 'failure-cleanup-uninstall' -ContinueOnFailure $true -Action {
            # 残っているのは更新元のため、更新元の MSI で消す。
            $log = Join-Path $OutputDir 'msiexec-failure-cleanup-uninstall.log'
            [void](Invoke-Msiexec -ArgumentString ('/x "{0}" /qn /norestart /l*v "{1}"' -f $SourceMsiPath, $log) -Description 'uninstall after the failure scenario')
            $script:ProductInstalled = $false
            return 'uninstalled'
        }
    }
    catch {
        Write-E2ELog ('failure-injection scenario aborted: {0}' -f $_.Exception.Message)
    }
}
catch {
    # 事前条件の失敗は Invoke-E2EStep が記録済みのはず。想定外の例外もここで記録する。
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
    # ロックを握ったまま中断すると、後片付けのアンインストールや後続の調査が DB を開けない。
    try { Unlock-YaguraDatabase } catch { Write-E2ELog ('releasing the database lock failed: {0}' -f $_.Exception.Message) }

    if ($script:ProductInstalled) {
        # 結果は合否に含めない(ランナーを汚さないための後片付け)。
        try {
            $left = Remove-InstalledYaguraProducts -LogPrefix 'cleanup-uninstall'
            $script:Steps.Add([pscustomobject]@{
                name       = 'cleanup-uninstall'
                status     = 'Info'
                detail     = ('{0} product(s) left after cleanup' -f $left)
                durationMs = 0
            })
        }
        catch {
            Write-E2ELog ('cleanup failed: {0}' -f $_.Exception.Message)
        }
    }

    foreach ($item in $script:NotExercised) {
        Write-E2ELog ('NOT EXERCISED: {0} - {1}' -f $item.item, $item.reason)
    }
    Write-E2EOutputs
}

if ($script:Failed) {
    exit 1
}
exit 0

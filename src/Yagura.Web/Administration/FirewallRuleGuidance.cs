using System.Globalization;

namespace Yagura.Web.Administration;

/// <summary>
/// 後から有効化する待ち受け（TLS 受信・管理リスナのリモート HTTPS）に必要な、Windows
/// ファイアウォールの受信許可規則を作るコマンドを組み立てる（configuration.md §4.3）。
/// </summary>
/// <remarks>
/// 規則を作るのは運用者であり、Yagura はコマンドを表示するだけ——サービスアカウントに
/// ファイアウォールの書き換え権限を持たせない。規則の有無は判定しない（突合は Host の
/// 起動時に行う）。
/// </remarks>
public static class FirewallRuleGuidance
{
    /// <summary>TLS 受信の既定ポート（RFC 5425 の標準ポート。<c>Ingestion:Tls:Port</c> 未設定時）。</summary>
    public const int DefaultIngestionTlsPort = 6514;

    /// <summary>管理リスナのリモート HTTPS の既定ポート（<c>Admin:Https:Port</c> 未設定時）。</summary>
    public const int DefaultAdminRemoteHttpsPort = 8516;

    /// <summary>サービス本体の実行ファイル名（インストーラの規則が <c>-Program</c> で限定する対象）。</summary>
    public const string HostExecutableFileName = "Yagura.Host.exe";

    /// <summary>TLS 受信の規則表示名。{0} にポート番号が入る（Yagura 名前空間 = 「Yagura」始まり）。</summary>
    public const string IngestionTlsRuleNameFormat = "Yagura Syslog (TLS {0})";

    /// <summary>管理リモート HTTPS の規則表示名。{0} にポート番号が入る（Yagura 名前空間 = 「Yagura」始まり）。</summary>
    public const string AdminRemoteHttpsRuleNameFormat = "Yagura Admin Remote (HTTPS {0})";

    /// <summary>TLS 受信の案内（保存済みのポートと実行中の本体から組み立てる）。</summary>
    public static FirewallRuleCommand ForIngestionTls(string? persistedPort, string? processPath) =>
        Create(IngestionTlsRuleNameFormat, ResolveEffectivePort(persistedPort, DefaultIngestionTlsPort), processPath);

    /// <summary>管理リモート HTTPS の案内（保存済みのポートと実行中の本体から組み立てる）。</summary>
    public static FirewallRuleCommand ForAdminRemoteHttps(string? persistedPort, string? processPath) =>
        Create(AdminRemoteHttpsRuleNameFormat, ResolveEffectivePort(persistedPort, DefaultAdminRemoteHttpsPort), processPath);

    /// <summary>
    /// 保存済みのポート文字列から、再起動後に待ち受けるポートを求める。未設定・不正値は既定値
    /// （設定の読み込みが不正値を既定値で継続させるのと同じ解釈——configuration.md §8）。
    /// </summary>
    public static int ResolveEffectivePort(string? persistedPort, int defaultPort)
    {
        if (!string.IsNullOrWhiteSpace(persistedPort)
            && int.TryParse(persistedPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535)
        {
            return port;
        }

        return defaultPort;
    }

    /// <summary>
    /// 実行中のプロセスが Yagura 本体のときだけ、その実行ファイルのパスを返す。
    /// 別のプロセス（dotnet 経由の実行・テストホスト等）のパスで <c>-Program</c> を限定すると、
    /// 規則が本体の通信に一致せず、受信が届かないまま気づけない——そのときは null を返し、
    /// 呼び出し側は <c>-Program</c> を省いた形を示す。
    /// </summary>
    public static string? ResolveHostExecutablePath(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        // サーバ（Windows）のパス区切りで判定する。Path.GetFileName は実行 OS の区切りに依存する。
        var separatorIndex = processPath.LastIndexOfAny(['\\', '/']);
        var fileName = separatorIndex >= 0 ? processPath[(separatorIndex + 1)..] : processPath;

        return string.Equals(fileName, HostExecutableFileName, StringComparison.OrdinalIgnoreCase)
            ? processPath
            : null;
    }

    /// <summary>
    /// インストーラが作る規則と同じ条件（受信・許可・Domain + Private・実行ファイル限定）の
    /// <c>New-NetFirewallRule</c> を組み立てる。<paramref name="programPath"/> が null なら
    /// <c>-Program</c> を省く。
    /// </summary>
    public static string BuildNewNetFirewallRuleCommand(string displayName, int port, string? programPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var portText = port.ToString(CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"New-NetFirewallRule -DisplayName {QuotePowerShellLiteral(displayName)}",
            $"  -Direction Inbound -Action Allow -Protocol TCP -LocalPort {portText}",
            "  -Profile Domain,Private",
        };

        if (programPath is not null)
        {
            lines.Add($"  -Program {QuotePowerShellLiteral(programPath)}");
        }

        // 行継続のバッククォートは行末に置く（後ろに空白があると継続にならず、貼り付けた時点で
        // 途中までのコマンドが実行される）。
        return string.Join(" `\n", lines);
    }

    private static FirewallRuleCommand Create(string ruleNameFormat, int port, string? processPath)
    {
        var programPath = ResolveHostExecutablePath(processPath);
        var displayName = string.Format(CultureInfo.InvariantCulture, ruleNameFormat, port);
        return new FirewallRuleCommand(port, programPath, BuildNewNetFirewallRuleCommand(displayName, port, programPath));
    }

    // 単一引用符の文字列は展開されない。中の単一引用符は 2 つ重ねて表す（PowerShell の規則）。
    private static string QuotePowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>受信許可規則の案内 1 件分（画面表示用）。</summary>
/// <param name="Port">規則で開けるポート（保存済みの設定から求めた値）。</param>
/// <param name="ProgramPath">
/// <c>-Program</c> に指定した本体のパス。特定できなかった場合は null（コマンドは <c>-Program</c> なし）。
/// </param>
/// <param name="Command">コピーして実行する PowerShell コマンド。</param>
public sealed record FirewallRuleCommand(int Port, string? ProgramPath, string Command);

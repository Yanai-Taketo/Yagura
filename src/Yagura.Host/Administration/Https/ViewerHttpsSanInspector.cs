using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration.Https;

/// <summary>
/// SAN ホスト名整合の助言検査（ADR-0022 決定 4）の計算部。「証明書を入れたのにブラウザ警告が
/// 出続ける」典型事故（短名 <c>https://サーバ名:8514/</c> でのアクセス × FQDN のみの SAN）を
/// 保存前に検出する。
/// </summary>
/// <remarks>
/// <para>
/// <b>SAN 読み出しの契約配置（ADR-0022 委任 8 の確定）</b>: 共有列挙契約
/// （<see cref="CertificateCandidate"/>）へのフィールド追加ではなく、<b>選択後の拇印による
/// 個別読み出し</b>を採る。理由: ①SAN は閲覧 HTTPS 画面だけが使う情報であり、共有 record へ
/// 足すと既存 2 画面（管理 HTTPS・TLS 受信）の列挙にも読み出しコストと露出面（ADR-0012 決定 8 の
/// 列挙 API は既定無認証 loopback で到達可能）を増やす。②検査は保存前検証・保存後の再確認
/// （<see cref="IViewerHttpsAdminService.InspectCertificateAsync"/>）と同じ「選択済み 1 枚」への
/// 操作であり、列挙（全候補）とはアクセスパターンが異なる。既存 2 画面の表示・挙動は変えない
/// （委任 8 の条件どおり）。
/// </para>
/// <para>
/// <b>検査の限界</b>: 対象はホスト名（dNSName）のみ——IP アドレスでの
/// アクセス・複数 NIC・エイリアス（CNAME）は射程外（助言であり網羅を約束しない）。ワイルド
/// カード（<c>*.example.com</c>）は公的 CA の標準に合わせ<b>単一ラベルの展開のみ</b>整合と
/// みなす（<c>a.example.com</c> は一致・<c>a.b.example.com</c> と短名 <c>a</c> は不一致）。
/// 「SAN に短名があればブラウザ警告なしになる」という主張のブラウザ実機検証（GPO 配布ルート下の
/// Chrome/Edge・Firefox の enterprise roots 依存）は lab ゲート（委任 4）——本検査の警告文言は
/// その通過までは断定形を避ける（UiText 側）。
/// </para>
/// </remarks>
public static class ViewerHttpsSanInspector
{
    /// <summary>
    /// サーバ自身の名前（NetBIOS 短名 + FQDN）を返す。ドメイン非参加等で両者が実質同一なら 1 件。
    /// </summary>
    public static IReadOnlyList<string> GetLocalServerNames()
    {
        var names = new List<string>();

        var shortName = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(shortName))
        {
            names.Add(shortName);
        }

        // FQDN: IPGlobalProperties の HostName + DomainName（プライマリ DNS サフィックス）。
        // Dns.GetHostEntry による自己解決は DNS 到達不能時に例外・遅延の面を作るため使わない
        // （検査は保存操作の同期経路で走る）。
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrWhiteSpace(properties.DomainName))
            {
                var fqdn = $"{properties.HostName}.{properties.DomainName}";
                if (!names.Contains(fqdn, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(fqdn);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // FQDN が取れなくても短名だけで検査を継続する（検査は助言——網羅を約束しない）。
        }

        return names;
    }

    /// <summary>
    /// 証明書の SAN（dNSName）とサーバ名一覧を突き合わせる。証明書は呼び出し側が破棄する。
    /// </summary>
    public static ViewerHttpsSanInspection Inspect(X509Certificate2 certificate, IReadOnlyList<string> serverNames)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(serverNames);

        var sanExtension = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var dnsNames = sanExtension?.EnumerateDnsNames().ToArray() ?? [];

        var missing = serverNames
            .Where(name => !dnsNames.Any(dns => MatchesDnsName(dns, name)))
            .ToArray();

        return new ViewerHttpsSanInspection(
            RequiredServerNames: serverNames,
            MissingServerNames: missing,
            CertificateDnsNames: dnsNames,
            HasSanExtension: sanExtension is not null);
    }

    /// <summary>
    /// dNSName 1 件とホスト名の一致判定（大文字小文字を区別しない）。ワイルドカードは
    /// 左端 1 ラベル（<c>*.</c> 始まり）のみ・単一ラベルの展開のみ許す（RFC 6125 の一般的な
    /// ブラウザ実装に合わせる——<c>*.example.com</c> は <c>a.example.com</c> に一致し、
    /// <c>a.b.example.com</c> や裸の <c>example.com</c> には一致しない）。
    /// </summary>
    internal static bool MatchesDnsName(string dnsName, string hostName)
    {
        if (string.IsNullOrWhiteSpace(dnsName) || string.IsNullOrWhiteSpace(hostName))
        {
            return false;
        }

        if (string.Equals(dnsName, hostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dnsName.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = dnsName[1..]; // ".example.com"
            if (!hostName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var label = hostName[..^suffix.Length];
            return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal);
        }

        return false;
    }
}

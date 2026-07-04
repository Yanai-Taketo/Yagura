namespace Yagura.Host.Configuration;

/// <summary>
/// 検証・3 分類の適用・環境変数上書きをすべて終えた、起動に使う最終的な設定値。
/// </summary>
/// <remarks>
/// データルートは設定ファイル自体の置き場所を決める入力であり、ファイル内キーではなく
/// 環境変数 <see cref="Yagura.Host.YaguraHostEnvironment.DataRootEnvironmentVariable"/> と
/// 既定値（<c>%ProgramData%\Yagura</c>）のみで解決する（configuration.md §2）。
/// </remarks>
/// <param name="DataRoot">データルートの絶対パス。</param>
/// <param name="UdpBindAddress">UDP 受信リスナの bind アドレス（検証・縮小適用済み）。</param>
/// <param name="UdpPort">UDP 受信リスナのポート（検証済み）。</param>
/// <param name="TcpBindAddress">TCP 受信リスナの bind アドレス（検証・縮小適用済み。M4-1）。</param>
/// <param name="TcpPort">TCP 受信リスナのポート（検証済み。M4-1）。</param>
/// <param name="HttpPort">閲覧 HTTP リスナのポート（検証済み）。</param>
/// <param name="SqliteFileName">データルート配下の SQLite ファイル名（検証済み）。</param>
public sealed record ResolvedYaguraConfiguration(
    string DataRoot,
    string UdpBindAddress,
    int UdpPort,
    string TcpBindAddress,
    int TcpPort,
    int HttpPort,
    string SqliteFileName);

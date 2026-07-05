namespace Yagura.Web.Circuits;

/// <summary>
/// circuit 統治の既定値（security.md §2.2。M8-4。Issue #71）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本クラスの値はすべて仮値である</b>（security.md §7 の確定待ち SEC-1・SEC-8）。
/// SEC-1（上限）は「circuit あたりのサーバ側メモリの実測 × ターゲット環境の余裕 + 障害時の
/// 同時閲覧数の想定」で、SEC-8（無操作回収）は「掲示運用と両立することの確認」で確定する。
/// 確定するまで設定キーは設けない（値が固まる前にキーを公開すると additive-only 規約
/// （configuration.md §1）により既定値変更の互換負債を先に負うため——確定後に設定キー化を
/// 判断し、その際は KnownKeys / ConfigurationKeyMetadata / configuration.md §8 の 3 点を
/// 同時更新する）。
/// </para>
/// </remarks>
public static class CircuitGovernanceDefaults
{
    /// <summary>
    /// 閲覧リスナの circuit 数上限（SEC-1 仮値: 100。障害時の閲覧集中——NOC 等で同時多数が
    /// 一斉に画面を開く——を想定し、小規模環境が既定値で案内画面に当たらない程度に大きく取る）。
    /// </summary>
    public const int ViewerCircuitLimit = 100;

    /// <summary>
    /// 管理リスナの circuit 数上限（SEC-1 仮値: 5。loopback 限定で同時利用者は実質 1 人のため
    /// 小さな固定値でよい——security.md §2.2。ブラウザの複数タブ + 予備の余裕分）。
    /// </summary>
    public const int AdminCircuitLimit = 5;

    /// <summary>
    /// 閲覧リスナの無操作 circuit の回収タイムアウト（SEC-8 仮値: 8 時間。掲示用途——操作せず
    /// 表示し続ける正当な利用——を殺さない長め。「操作」の定義は circuit 上の入力活動
    /// （UI イベント・JS interop 応答等の inbound activity）であり、サーバからの表示更新の
    /// 受信は操作に数えない——この定義自体も SEC-8 の確定対象）。
    /// </summary>
    public static readonly TimeSpan ViewerIdleTimeout = TimeSpan.FromHours(8);

    /// <summary>
    /// 管理リスナの無操作 circuit の回収タイムアウト（SEC-8 仮値: 30 分。実質 1 人運用であり、
    /// 放置された管理画面が枠を占有して管理者自身がロックアウトされることを防ぐ短め——
    /// security.md §2.2「管理リスナの上限到達時の復旧経路」。上限到達時の最終手段は
    /// サービス再起動ではなくこの回収の経過待ちである）。
    /// </summary>
    public static readonly TimeSpan AdminIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>無操作回収の走査間隔（実装都合の値。SEC-8 のタイムアウト精度はこの間隔に依存する）。</summary>
    public static readonly TimeSpan IdleScanInterval = TimeSpan.FromMinutes(1);
}

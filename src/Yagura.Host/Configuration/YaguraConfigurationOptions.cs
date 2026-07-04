namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイル（既定 <c>yagura.json</c>。<see cref="YaguraConfigurationLoader"/> 参照）の
/// JSON 構造にそのままバインドする POCO。configuration.md §8 の設定スキーマ一覧のうち、
/// 現時点で実在する項目（受信・UI・永続化の一部）のみをモデル化する（additive-only の起点。
/// §8 の他区分——流量制御・スプール・保持期間・通知——は当該機能の実装時に追加する）。
/// </summary>
/// <remarks>
/// 本クラスは「ファイルに書かれていた生の値」を保持する検証前の中間表現である。
/// 値の妥当性検証・3 分類（起動失敗 / 既定値継続 / 縮小継続）の適用・環境変数による
/// 上書きは <see cref="YaguraConfigurationLoader"/> が行い、検証済みの最終値は
/// <see cref="ResolvedYaguraConfiguration"/> にまとめる。
/// 各プロパティは <c>string?</c> 等の緩い型で受ける（JSON の型不一致・欠落を
/// バインド段階で例外にせず、後段の検証で「不正値」として一様に扱うため）。
/// </remarks>
public sealed class YaguraConfigurationOptions
{
    /// <summary>設定ファイル内の JSON セクション名（.NET 構成システムの規約に合わせルート直下）。</summary>
    public const string SectionName = "";

    /// <summary>§8「受信」区分。</summary>
    public IngestionOptions? Ingestion { get; set; }

    /// <summary>§8「UI」区分（現時点は閲覧ポートのみ。管理リスナ・HTTPS 証明書等は M6 以降で追加）。</summary>
    public ViewerOptions? Viewer { get; set; }

    /// <summary>§8「永続化」区分のうち組み込み DB の置き場所。データルート自体は §2 参照。</summary>
    public StorageOptions? Storage { get; set; }

    public sealed class IngestionOptions
    {
        /// <summary>UDP 受信リスナの設定。</summary>
        public UdpOptions? Udp { get; set; }

        public sealed class UdpOptions
        {
            /// <summary>bind するアドレス（文字列のまま保持し、検証段で <see cref="System.Net.IPAddress"/> 等へ変換する）。</summary>
            public string? BindAddress { get; set; }

            /// <summary>bind するポート。JSON の数値以外（文字列・範囲外）も受けられるよう <c>string?</c> で保持する。</summary>
            public string? Port { get; set; }
        }
    }

    public sealed class ViewerOptions
    {
        /// <summary>閲覧 HTTP リスナのポート。</summary>
        public string? HttpPort { get; set; }
    }

    public sealed class StorageOptions
    {
        /// <summary>
        /// データルート配下の SQLite ファイル名（既定 <c>yagura.db</c>）。パス区切りを含む値は
        /// 不正として既定値へフォールバックする（データルート脱出を防ぐ。§1「既定値で継続」）。
        /// </summary>
        public string? SqliteFileName { get; set; }
    }
}

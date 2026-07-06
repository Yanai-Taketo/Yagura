namespace Yagura.Web.ForwarderKit;

/// <summary>
/// 配置フォルダ内の Fluent Bit MSI 検出の契約（ADR-0008 設計条件 9・委任 #7）。
/// Web 層はデータルートの実パスを直接知らないため、<see cref="INicCandidateSource"/> と同型の
/// 抽象を介して Host 側（データルートを知る側）から実体を注入する。
/// </summary>
public interface IForwarderMsiSource
{
    /// <summary>配置フォルダのフルパス（データルート配下 `forwarder`。生成画面が常時表示する）。</summary>
    string FolderPath { get; }

    /// <summary>
    /// 配置フォルダ内の MSI を列挙し、検出状態を返す（<see cref="ForwarderMsiConstraints"/> の
    /// ファイル名パターンに一致するもののみを対象とする）。フォルダが存在しない場合も
    /// <see cref="ForwarderMsiLookupState.NotFound"/> として扱う（未検出。フォルダ作成はしない
    /// ——ADR-0008 実装 PR の申し送り。フォルダ作成・ACL 設定はインストーラの領分）。
    /// </summary>
    ForwarderMsiLookup Lookup();
}

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// 配置フォルダ内の Fluent Bit MSI 検出の契約（ADR-0008 設計条件 9・委任 #7・
/// ADR-0009 決定7・委任 #4）。Web 層はデータルートの実パスを直接知らないため、
/// <see cref="INicCandidateSource"/> と同型の抽象を介して Host 側（データルートを知る側）から
/// 実体を注入する。
/// </summary>
/// <remarks>
/// <b>誤アーキ配布への牽制（ADR-0008 委任 #4）</b>: <see cref="Lookup(ForwarderMsiArchitecture)"/>
/// は呼び出し時に指定したアーキテクチャの MSI のみを候補とする——同一の配置フォルダに
/// win64・ARM64 の MSI が混在していても、それ自体は「複数検出」エラーにならない
/// （収集対象端末のアーキが異なる以上、両方存在するのは正常な状態でありうる）。
/// これにより、生成 UI が「収集対象端末のアーキ」を明示選択した上で当該アーキの
/// <see cref="Lookup(ForwarderMsiArchitecture)"/> だけを呼ぶ設計そのものが、
/// 「ARM64 端末向けキットに誤って win64 MSI を同梱してしまう」失敗様式を構造的に防ぐ
/// （画面上の警告表示という後追いの牽制ではなく、選択したアーキ以外の MSI がそもそも
/// 検出対象に入らないという設計。ADR-0008 委任 #4 の「牽制の要否」への回答）。
/// </remarks>
public interface IForwarderMsiSource
{
    /// <summary>配置フォルダのフルパス（データルート配下 `forwarder`。生成画面が常時表示する）。</summary>
    string FolderPath { get; }

    /// <summary>
    /// 配置フォルダ内の MSI を列挙し、検出状態を返す（<paramref name="architecture"/> に対応する
    /// <see cref="ForwarderMsiConstraints"/> のファイル名パターンに一致するもののみを対象とする）。
    /// フォルダが存在しない場合も <see cref="ForwarderMsiLookupState.NotFound"/> として扱う
    /// （未検出。フォルダ作成はしない——ADR-0008 実装 PR の申し送り。フォルダ作成・ACL 設定は
    /// インストーラの領分）。
    /// </summary>
    ForwarderMsiLookup Lookup(ForwarderMsiArchitecture architecture);
}

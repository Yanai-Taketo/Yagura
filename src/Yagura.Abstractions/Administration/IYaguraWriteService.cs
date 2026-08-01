namespace Yagura.Abstractions.Administration;

/// <summary>
/// 「書き込み系サービス」であることを宣言するマーカーインターフェース（
/// security.md §1 L-5 の覆域限界の節「閲覧リスナ側のコンポーネントから書き込み系サービスへ
/// 到達できない分離を実装設計で定め、その分離自体もアーキテクチャテストの対象とする」の実装）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本インターフェースはメンバーを持たない（マーカーとしてのみ機能する）</b>。設定変更・
/// 本番昇格・保持期間変更・circuit の個別/全切断等（security.md §3 の「管理」役割の操作範囲）を
/// 実装するサービスは、具体の操作メソッドを独自に定義した上で本インターフェースを実装する
/// ことで「書き込み系」であることをアーキテクチャテストに申告する。
/// </para>
/// <para>
/// 本インターフェースを実装する契約（<see cref="ISetupWizardService"/>・
/// <see cref="IPromotionWizardService"/>・<see cref="ICircuitManagementService"/>）と実装クラス
/// （<c>Yagura.Host.Administration</c> のウィザードサービス群・
/// <c>Yagura.Web.Administration.CircuitManagementService</c>）が存在し、
/// <c>Yagura.Web.Tests.ArchitectureTests.ViewerComponentReferenceIsolationTests</c> が
/// 実在する書き込み系サービスへの誤参照を検出する。
/// </para>
/// <para>
/// <b>マーカーインターフェース方式を採った理由</b>: 名前空間規約（例:
/// 「<c>Yagura.Abstractions.Administration.*</c> 名前空間のクラスはすべて書き込み系」）でも
/// 同じ検査は成立するが、名前空間規約はクラスの実装意図を型システムでは表現せず、
/// 「うっかり読み取り専用の型を同じ名前空間に置いてしまう」事故を静的に防げない。
/// マーカーインターフェースは C# の型システムに載るため、実装漏れ・誤分類は
/// コンパイル時に判別できる（<c>typeof(IYaguraWriteService).IsAssignableFrom(type)</c>
/// で機械的に判定できる）。将来 M8 で書き込み系サービスを追加する実装者への負担も
/// 「インターフェースを 1 つ実装する」だけで済み、名前空間の配置規約を別途覚える
/// 必要がない。
/// </para>
/// <para>
/// <b>配置（<c>Yagura.Abstractions</c>）</b>: モジュール横断契約の最下層プロジェクトに置く
/// （architecture.md §1.1）。将来追加される書き込み系サービスの契約群も本名前空間が第一候補となる。
/// </para>
/// </remarks>
public interface IYaguraWriteService
{
}

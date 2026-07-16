using System.Net;

namespace Yagura.Ingestion.FlowControl;

/// <summary>
/// 実装を実行中に差し替えられる <see cref="IIngressGate"/> のラッパー（CF-4 層1。Issue #262）。
/// 設定ライブ再読み込みで <c>Ingestion:FlowControl:*</c> を無瞬断で反映するための差し込み口。
/// </summary>
/// <remarks>
/// 各リスナが保持するゲート参照はコンストラクタ固定（readonly）のため、閾値変更・有効/無効の
/// 切替をライブ反映するには「リスナが保持する参照は不変のまま、その中身が入れ替わる」構造が
/// 要る。本クラスがその 1 段の間接参照を提供する。差し替えは参照の原子的交換
/// （<see cref="Volatile"/>）であり、判定中のデータグラムは交換前後いずれかのゲートで
/// 一貫して判定される（ロック不要。閾値変更時に旧ゲートのバケット状態は破棄される——
/// 新ゲートは全送信元が満杯バケットから始まるため、切替が破棄を誘発することはない）。
/// </remarks>
public sealed class SwappableIngressGate : IIngressGate
{
    private IIngressGate _inner;

    public SwappableIngressGate(IIngressGate initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _inner = initial;
    }

    /// <summary>現在の実装（テスト・診断用の観測点）。</summary>
    public IIngressGate Current => Volatile.Read(ref _inner);

    /// <summary>実装を原子的に差し替える（設定ライブ再読み込みから呼ばれる）。</summary>
    public void Swap(IIngressGate replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        Volatile.Write(ref _inner, replacement);
    }

    /// <inheritdoc />
    public bool ShouldAdmit(IPAddress sourceAddress, ReadOnlySpan<byte> payload) =>
        Volatile.Read(ref _inner).ShouldAdmit(sourceAddress, payload);
}

namespace Yagura.Storage;

/// <summary>
/// 「条件があれば WHERE 句断片を積み、対応するパラメータを追加する」定型を共通化する
/// （QueryAsync / QuerySystemEventsAsync のフィルタ組み立て。database.md §1.2）。
/// SQL 文そのもの（列名・演算子・プレースホルダ記法）は呼び出し側が書く——本クラスは
/// 断片の蓄積と WHERE 句組み立てのみを担う。
/// </summary>
/// <remarks>
/// カーソル（キーセット）ページングの述語は provider ごとの方言差が実測に基づく意図的なもの
/// （SqlServerLogStore.Query.cs のコメント参照）のため、<see cref="Add"/> の対象外とする——
/// <see cref="AddRaw"/> で断片だけをそのまま積み、パラメータ追加は呼び出し側の責務のまま残す。
/// </remarks>
internal sealed class WhereClauseBuilder
{
    private readonly List<string> _clauses = [];

    /// <summary>
    /// WHERE 句断片 <paramref name="clause"/> を積み、対応するパラメータ追加処理
    /// <paramref name="addParameter"/> を呼び出す。
    /// </summary>
    public void Add(string clause, Action addParameter)
    {
        _clauses.Add(clause);
        addParameter();
    }

    /// <summary>方言差を含む等、呼び出し側が独自に組み立てた断片をそのまま積む。</summary>
    public void AddRaw(string clause) => _clauses.Add(clause);

    /// <summary>積んだ断片から <c>WHERE ... AND ...</c> を組み立てる。断片が無ければ空文字列。</summary>
    public string BuildWhereSql() =>
        _clauses.Count > 0 ? "WHERE " + string.Join(" AND ", _clauses) : string.Empty;
}

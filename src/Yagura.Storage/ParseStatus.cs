namespace Yagura.Storage;

/// <summary>
/// ログレコードの解析結果。排他の 3 値（database.md §2.1）。
/// 不完全（TCP 切断による途中終端）は解析失敗に優先する。
/// </summary>
public enum ParseStatus
{
    Parsed,
    ParseFailed,
    Incomplete,
}

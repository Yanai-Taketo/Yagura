namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <c>YAGURA_HTTP_PORT</c> 等の環境変数を読み書きするテストクラスを同一 xUnit コレクションへ
/// まとめ、並列実行させない（xUnit の既定はテストクラス単位で並列実行するが、環境変数は
/// プロセス全体で共有される可変状態のため、異なるクラスが同時に読み書きすると
/// 意図しない値の混入（フレーキーテスト）を招く）。
/// </summary>
/// <remarks>
/// <see cref="YaguraConfigurationLoaderTests"/>（M3-1）は <c>YAGURA_HTTP_PORT</c> 等を
/// 一時的に設定・復元するが、テスト完了直後（<c>Dispose</c>）まで設定されたままになる。
/// <see cref="YaguraConfigurationWriterTests"/>（M3-3）は環境変数を直接操作しないが、
/// 内部で <see cref="Yagura.Host.Configuration.YaguraConfigurationLoader.Load"/> を呼ぶ
/// テストがあり、これは環境変数を読む。両クラスが並列に走ると、書き込みテスト側が
/// 「ファイルに書いた値」ではなく「他方のテストが一時設定した環境変数の値」を読んでしまう
/// レースが発生しうる（実機で再現・特定した。本ファイルはその修正）。
/// 新しい設定関連テストクラスで環境変数を読み書きする場合は、本コレクションに参加させること。
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ConfigurationEnvironmentVariableTestCollection
{
    public const string Name = "Yagura.Host.Configuration environment variable tests";
}

using System.Globalization;
using Yagura.Abstractions.Administration;

namespace Yagura.Host.Administration;

/// <summary>
/// <see cref="IAdminSessionGenerationStore"/> のデータルート配下ファイル実装（ADR-0013 決定 2）。
/// </summary>
/// <remarks>
/// <para>
/// 世代番号をデータルート直下の単一ファイル（<c>admin-session-generation.txt</c>）に十進整数で保持する。
/// ファイルは他のデータルート配下と同じ ACL（security.md §5——SYSTEM/Administrators/サービスアカウントのみ）に
/// 継承され、`Users`/`Authenticated Users` の ACE を持たない（インストーラが親フォルダに設定した保護 ACL を
/// 継承する）。ファイルが存在しない初回は世代 0 として扱い、<see cref="Bump"/> 時に作成する。
/// </para>
/// <para>
/// <b>永続化と非対称（ADR-0013 決定 2）</b>: 現在値はメモリにキャッシュし、定常のサービス再起動では
/// ファイルから読み直して同じ世代番号で復帰する（既発行セッションは生存）。<see cref="Bump"/> のみが
/// 値を進めて全失効を起こす。プロセス内の書き込みは <see cref="_gate"/> で直列化する。
/// </para>
/// </remarks>
public sealed class FileAdminSessionGenerationStore : IAdminSessionGenerationStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private int _current;

    public FileAdminSessionGenerationStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _filePath = Path.Combine(dataRoot, "admin-session-generation.txt");
        _current = ReadFromDisk(_filePath);
    }

    /// <inheritdoc />
    public int CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public int Bump()
    {
        lock (_gate)
        {
            var next = _current + 1;
            // 一時ファイルへ書いて置換する（部分書き込みで世代番号が壊れないように）。
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, next.ToString(CultureInfo.InvariantCulture));
            File.Move(tempPath, _filePath, overwrite: true);
            _current = next;
            return next;
        }
    }

    private static int ReadFromDisk(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return 0;
            }

            var text = File.ReadAllText(filePath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : 0;
        }
        catch (IOException)
        {
            // 読み取り不能時は 0（既定世代）で開始する。次の Bump で新規作成される。
            return 0;
        }
    }
}

using System.Runtime.Versioning;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// MSI の ProductVersion を読み取る共用実装（ADR-0008 設計条件 9「ProductVersion を優先」）。
/// 検出側（<see cref="SystemForwarderMsiSource"/>）とアップロード側
/// （<see cref="SystemForwarderMsiStore"/>。ADR-0020 決定 3）の両方が同一の読み取り経路を使う——
/// 配置時と検出時で版判定の実装が食い違わないための単一ソース。
/// </summary>
/// <remarks>
/// <para>
/// <b>方式</b>: <c>MsiOpenDatabase</c>（<c>msi.dll</c>。読み取り専用 <c>MSIDBOPEN_READONLY</c>）で
/// MSI データベースを開き、<c>SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'</c> で
/// Property テーブルから直接読む。ProductVersion は Windows Installer プロパティテーブル
/// （MSI 自体は OLE 構造化ストレージ）に格納されており、この経路が正攻法である。
/// </para>
/// <para>
/// <b>MsiGetFileVersion を使ってはならない（実測で確定）</b>: 初版実装は
/// 「<c>MsiGetFileVersionW</c> を MSI ファイル自身に呼び出すと ProductVersion 相当を返す」という
/// 前提だったが、この前提は実機（Windows Server 2025）で成立しない——公式 fluent-bit MSI・
/// WiX 7 製 MSI のいずれに対しても <c>ERROR_FILE_INVALID</c> (1006) を返した。
/// <c>MsiGetFileVersion</c> の読み取り対象はファイルバージョンリソース（EXE/DLL 等）であり、
/// MSI データベースの ProductVersion プロパティではない。このため全 MSI がアップロード拒否
/// （<c>reason=product-version-unreadable</c>）になっていた。
/// </para>
/// <para>
/// 読み取れなかった場合（ハンドルの取得失敗・Property テーブルや ProductVersion 行の不在・
/// MSI でないファイル）・<c>msi.dll</c> が存在しない実行環境では <see langword="null"/> を返す。
/// null の扱いは呼び出し側の責務——検出側はファイル名由来の版へフォールバックし（補助手段）、
/// アップロード側は拒否する（クライアント申告のファイル名を信用しないため、版の根拠が
/// ProductVersion 以外に存在しない——ADR-0020 決定 3）。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ForwarderMsiProductVersionReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;

    internal static string? TryRead(string filePath)
    {
        try
        {
            return ReadFromPropertyTable(filePath);
        }
        catch (DllNotFoundException)
        {
            // msi.dll が存在しない実行環境（理論上は Windows には存在するが、テスト実行環境等の
            // 予期しない構成を安全側で吸収する）。
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadFromPropertyTable(string filePath)
    {
        if (NativeMethods.MsiOpenDatabase(filePath, NativeMethods.MsiDbOpenReadOnly, out var database) != ErrorSuccess)
        {
            // MSI（OLE 構造化ストレージ）として開けない = MSI でないファイル・破損等。
            return null;
        }

        try
        {
            if (NativeMethods.MsiDatabaseOpenView(
                    database,
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'",
                    out var view) != ErrorSuccess)
            {
                return null;
            }

            try
            {
                if (NativeMethods.MsiViewExecute(view, 0) != ErrorSuccess)
                {
                    return null;
                }

                // ProductVersion 行が無い場合は ERROR_NO_MORE_ITEMS (259) → null。
                if (NativeMethods.MsiViewFetch(view, out var record) != ErrorSuccess)
                {
                    return null;
                }

                try
                {
                    return ReadRecordString(record);
                }
                finally
                {
                    _ = NativeMethods.MsiCloseHandle(record);
                }
            }
            finally
            {
                _ = NativeMethods.MsiCloseHandle(view);
            }
        }
        finally
        {
            _ = NativeMethods.MsiCloseHandle(database);
        }
    }

    private static string? ReadRecordString(uint record)
    {
        var buffer = new System.Text.StringBuilder(64);
        // pcchValueBuf: 入力はバッファサイズ（終端 null 含む）、ERROR_MORE_DATA 時の出力は
        // 必要文字数（終端 null 含まない）。
        var size = (uint)buffer.Capacity;
        var result = NativeMethods.MsiRecordGetString(record, 1, buffer, ref size);

        if (result == ErrorMoreData)
        {
            buffer.EnsureCapacity((int)size + 1);
            size += 1;
            result = NativeMethods.MsiRecordGetString(record, 1, buffer, ref size);
        }

        return result == ErrorSuccess && buffer.Length > 0 ? buffer.ToString() : null;
    }

    /// <summary>
    /// Windows Installer の <c>msi.dll</c> P/Invoke 宣言。
    /// </summary>
    /// <remarks>
    /// <c>MSIHANDLE</c> は 64-bit プロセスでも 32-bit の <c>unsigned long</c> であるため
    /// <see cref="uint"/> で宣言する（<c>IntPtr</c> にすると out 引数で上位 4 バイトが
    /// 未初期化のまま残り得る）。
    /// </remarks>
    private static class NativeMethods
    {
        /// <summary><c>MSIDBOPEN_READONLY</c> = <c>(LPCTSTR)0</c>（msiquery.h の定義値）。</summary>
        public static readonly IntPtr MsiDbOpenReadOnly = IntPtr.Zero;

        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out uint phDatabase);

        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint MsiDatabaseOpenView(uint hDatabase, string szQuery, out uint phView);

        [System.Runtime.InteropServices.DllImport("msi.dll")]
        public static extern uint MsiViewExecute(uint hView, uint hRecord);

        [System.Runtime.InteropServices.DllImport("msi.dll")]
        public static extern uint MsiViewFetch(uint hView, out uint phRecord);

        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint MsiRecordGetString(
            uint hRecord,
            uint iField,
            System.Text.StringBuilder szValueBuf,
            ref uint pcchValueBuf);

        [System.Runtime.InteropServices.DllImport("msi.dll")]
        public static extern uint MsiCloseHandle(uint hAny);
    }
}

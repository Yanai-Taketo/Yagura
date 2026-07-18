using System.Text.Json;

namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイル（既定 <c>yagura.json</c>）の全体書き換え保存 API
/// （configuration.md §3。書き手はウィザード・手編集の 2 者——旧設計の第 3 の書き手
/// 「自動書き換え（平文の DPAPI 暗号化書き戻し）」は 2026-07-06 オーナー決定で行わない
/// （§2）——のうち、ウィザードが使う「保存」の受け皿）。
/// </summary>
/// <remarks>
/// <para>
/// <b>全体書き換え、部分パッチにしない理由</b>: §3 は保存フローを「読み込み → 変更 → 検証 →
/// 保存」と定めており、部分パッチ（JSON Patch 等でキー単位に差分適用する方式）は
/// 「他の書き手が同時に変更した無関係なキー」を巻き込む余地を残す。全体書き換えは
/// 「読み込んだ時点の全内容を土台に、変更後の全内容を丸ごと置き換える」ため、
/// 楽観競合検出（<see cref="ConfigurationVersionToken"/>）と組み合わせることで
/// 「読み込んだ後に何か変わっていたら丸ごと拒否する」という単純な安全性を得られる。
/// </para>
/// <para>
/// <b>原子性</b>: 一時ファイルへ書き込んだうえで <see cref="File.Replace(string, string, string?)"/>
/// により対象ファイルと入れ替える。<c>File.Replace</c> は同一ボリューム上でのファイル置換を
/// 単一の操作として行う Win32 <c>ReplaceFile</c> を呼び出し、書き込み途中の内容が対象パスに
/// 見える瞬間を作らない（停電・クラッシュで設定ファイルが半分だけ書かれた状態になることを防ぐ。
/// 依頼の「一時ファイル + File.Replace 等」に対応する）。バックアップファイル引数は
/// 使わない（<see langword="null"/>。設定ファイルの世代管理は本 Issue の範囲外）。
/// 対象ファイルがまだ存在しない初回保存では <c>File.Replace</c> ではなく
/// <see cref="File.Move(string, string, bool)"/>（overwrite: false）で一時ファイルを
/// そのまま配置先へ改名する（<c>File.Replace</c> は置換先の存在を前提とするため）。
/// </para>
/// <para>
/// <b>文字コード: UTF-8、BOM なし</b>。書き込む側は本リポジトリの既存 JSON ファイル
/// （<c>global.json</c> 等）と同じ BOM なしを採用し、手編集者が使う一般的なエディタ・
/// CLI ツール（一部は BOM 付き JSON の扱いに難がある）との親和性を優先する。
/// </para>
/// <para>
/// <b>読み込み側は BOM の有無を問わない</b>。.NET 構成システムの <c>AddJsonFile</c> は
/// BOM を許容し、本クラスの <see cref="Read(string)"/> も <c>JsonSerializer</c> へ渡す前に
/// BOM を読み飛ばして受理範囲を揃える（§1 の不変条件）。
/// <c>JsonSerializer.Deserialize(ReadOnlySpan{byte}, ...)</c> は BOM を読み飛ばさないため、
/// 呼び出し側での除去が必要である（Issue #344。以前このコメントは「構成システムと同じく
/// BOM を許容する」と記していたが、除去処理がなく実際には起動時クラッシュになっていた）。
/// </para>
/// </remarks>
public static class YaguraConfigurationWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>UTF-8 BOM のバイト列（<c>EF BB BF</c>）。</summary>
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// 読み取り用のオプション。configuration.md §1 の不変条件「読み手の受理範囲は一致していなければ
    /// ならない」に従い、.NET 構成システム（<c>AddJsonFile</c>）の受理範囲に合わせる（Issue #312）。
    /// </summary>
    /// <remarks>
    /// 差異は数値・真偽値だけではない（2026-07-18 に実測で確認）。末尾カンマとコメントは構成システムが
    /// 受理するのに <see cref="JsonSerializer"/> の既定は拒否し、同一キーの重複定義は**逆に**構成システムが
    /// 拒否する（<see cref="InvalidDataException"/>）のに <see cref="JsonSerializer"/> の既定は
    /// 後勝ちで受理する。§1 は重複キーを受理しないと定めたため、厳格な側（構成システム）に合わせる。
    /// </remarks>
    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowDuplicateProperties = false,
        Converters = { new ConfigurationValueStringConverter() },
    };

    /// <summary>
    /// 保存前の下準備として、データルート配下の設定ファイルを読み込む。
    /// ファイルが存在しない場合は既定値のみの空インスタンスと <see cref="ConfigurationVersionToken.FileAbsent"/> を返す
    /// （初回保存 = ウィザードの初期設定生成に対応する）。
    /// </summary>
    public static YaguraConfigurationFileSnapshot Read(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var path = GetConfigurationFilePath(dataRoot);

        if (!File.Exists(path))
        {
            return new YaguraConfigurationFileSnapshot(new YaguraConfigurationOptions(), ConfigurationVersionToken.FileAbsent);
        }

        var bytes = File.ReadAllBytes(path);

        // トークンは「ディスク上の内容」から計算する（BOM を除去する前のバイト列）。
        // ConfigurationVersionToken.FromFile も生のファイル内容をハッシュしており、
        // ここで除去後のバイト列を使うと BOM 付きファイルで両者のトークンが食い違い、
        // 楽観的競合検出（Save の expectedVersionToken 照合）が誤検知する。
        var token = ConfigurationVersionToken.FromContent(bytes);

        // UTF-8 BOM は読み飛ばしてから deserialize する（Issue #344）。
        // JsonSerializer.Deserialize(ReadOnlySpan<byte>, ...) は BOM を読み飛ばさず
        // 「'0xEF' is an invalid start of a value」で失敗する（実測で確認）。一方 .NET 構成システムの
        // AddJsonFile は BOM の有無に関わらず読めるため、除去しないと §1 の不変条件
        // 「2 つの読み手の受理範囲は一致していなければならない」が破れる（Issue #312 が
        // 成立させようとした不変条件そのもの）。BOM は Windows PowerShell 5.1 の
        // Set-Content -Encoding utf8 や一部のエディタが既定で付与するため、手編集で普通に踏み得る。
        var options = JsonSerializer.Deserialize<YaguraConfigurationOptions>(StripUtf8Bom(bytes), DeserializerOptions)
            ?? new YaguraConfigurationOptions();

        return new YaguraConfigurationFileSnapshot(options, token);
    }

    /// <summary>
    /// 先頭の UTF-8 BOM（<c>EF BB BF</c>）があれば取り除いた範囲を返す（Issue #344）。
    /// </summary>
    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;

    /// <summary>
    /// 設定全体を全体書き換えで保存する（楽観的な競合検出つき）。
    /// </summary>
    /// <param name="dataRoot">データルートの絶対パス。</param>
    /// <param name="options">保存する設定全体（読み込んだ内容を変更したもの）。</param>
    /// <param name="expectedVersionToken">
    /// 保存の下準備として読み込んだ時点の <see cref="ConfigurationVersionToken"/>
    /// （<see cref="Read"/> の戻り値、または初回保存の場合は <see cref="ConfigurationVersionToken.FileAbsent"/>）。
    /// </param>
    /// <returns>保存後のファイル内容に対応する新しい <see cref="ConfigurationVersionToken"/>。</returns>
    /// <exception cref="ConfigurationConflictException">
    /// 現在のファイルの内容が <paramref name="expectedVersionToken"/> と一致しない場合
    /// （読み込み後に外部変更があった）。この場合ファイルは変更されない。
    /// </exception>
    public static ConfigurationVersionToken Save(
        string dataRoot,
        YaguraConfigurationOptions options,
        ConfigurationVersionToken expectedVersionToken)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(expectedVersionToken);

        Directory.CreateDirectory(dataRoot);
        var path = GetConfigurationFilePath(dataRoot);

        // 楽観競合検出（§3）: 現在のファイル内容が「読み込み時点のトークン」と一致するかを
        // 保存の直前に検証する。一致しなければ外部変更が挟まっているため、上書きせず失敗を返す。
        var currentToken = ConfigurationVersionToken.FromFile(path);
        if (!currentToken.Equals(expectedVersionToken))
        {
            throw new ConfigurationConflictException(
                $"設定ファイル '{path}' は読み込み後に外部で変更されているため保存を中止しました。" +
                "最新の内容を再読み込みしてから変更をやり直してください（手編集または他の管理操作との競合）。");
        }

        var newContent = JsonSerializer.SerializeToUtf8Bytes(options, SerializerOptions);

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // UTF-8 BOM なしで書き込む（クラス側コメント参照）。
            File.WriteAllBytes(tempPath, newContent);

            if (File.Exists(path))
            {
                // File.Replace は同一ボリューム上での原子的な置換を Win32 ReplaceFile 経由で行う。
                // バックアップは作らない（destinationBackupFileName: null）。
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                // 初回保存: 置換対象がまだ存在しないため File.Replace は使えない。
                // 同一ボリューム上（同一ディレクトリ内の一時ファイル）の File.Move は
                // NTFS 上でリネームとして原子的に行われる。
                File.Move(tempPath, path, overwrite: false);
            }
        }
        catch
        {
            // 失敗時に一時ファイルの残骸を残さない（原子性の代替検証テストの対象）。
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        return ConfigurationVersionToken.FromContent(newContent);
    }

    private static string GetConfigurationFilePath(string dataRoot) =>
        Path.Combine(dataRoot, YaguraConfigurationLoader.ConfigurationFileName);
}

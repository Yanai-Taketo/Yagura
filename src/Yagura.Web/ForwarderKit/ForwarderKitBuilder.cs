using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// フォワーダ配布キット（ZIP）の組み立て（ADR-0008 設計条件 3・4・7・8・9）。
/// </summary>
/// <remarks>
/// <para>
/// テンプレートはビルド時に埋め込んだリソース（<c>Yagura.Web.csproj</c> の
/// <c>EmbeddedResource</c>。<c>forwarder/fluent-bit/</c> を単一ソースとする——ADR-0008 委任 #1）
/// から読み出す。生成は <b>メモリ上の <see cref="MemoryStream"/> 上で完結し、ディスクへ
/// 一時ファイルを書かない</b>（設計条件 7。MSI 同梱時も配置済みファイルを読み取って封入するのみ）。
/// 外部ネットワークへのアクセスも行わない（設計条件 7）。生成物に秘密情報は含めない
/// （設計条件 8——入る値は宛先ホスト・ポート・チャネル名・（同梱時のみ）MSI の来歴情報）。
/// </para>
/// </remarks>
public static class ForwarderKitBuilder
{
    private const string ResourcePrefix = "Yagura.Web.ForwarderKit.Templates.";

    /// <summary>ZIP 内のファイル名（README は生成テンプレートを README.md として梱包する）。</summary>
    private static class EntryNames
    {
        public const string Conf = "fluent-bit-yagura.conf";
        public const string InstallScript = "install.ps1";
        public const string UninstallScript = "uninstall.ps1";
        public const string LuaFilter = "winevt-severity.lua";
        public const string Readme = "README.md";
        public const string Generated = "GENERATED.txt";

        /// <summary>
        /// TLS 受信（<see cref="ForwarderKitMode.Tls"/>）選択時、<see cref="ForwarderKitRequest.TlsCaCertificatePem"/>
        /// が指定されていれば同梱する CA/サーバ証明書のファイル名（Issue #137）。
        /// </summary>
        public const string TlsCaCertificate = "yagura-tls-ca.pem";
    }

    /// <summary>
    /// 要求内容から ZIP を組み立てる。
    /// </summary>
    /// <param name="request">検証済みの生成要求。</param>
    /// <param name="generatedAt">生成時刻（サーバ現地時刻。オフセット付きで <c>GENERATED.txt</c> に記録する）。</param>
    /// <returns>ZIP アーカイブのバイト列。</returns>
    public static byte[] Build(ForwarderKitRequest request, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        var confTemplate = ReadTemplate(EntryNames.Conf);
        var installScript = ReadTemplate(EntryNames.InstallScript);
        var uninstallScript = ReadTemplate(EntryNames.UninstallScript);
        var luaFilter = ReadTemplate(EntryNames.LuaFilter);
        var readmeTemplate = ReadTemplate("README.generated.md");

        var conf = SubstituteConf(confTemplate, request);
        var readme = SubstituteReadme(readmeTemplate, request, generatedAt);
        var msiBytes = request.MsiBundle is { } bundle ? File.ReadAllBytes(bundle.FilePath) : null;

        // Kit-SHA256 の自己参照回避（ADR-0008 設計条件 9）: GENERATED.txt 自身に「ZIP 全体の
        // SHA256」を書き込む必要があるが、その値は GENERATED.txt の内容が確定しないと計算できず、
        // GENERATED.txt の内容（Kit-SHA256 の値）が確定しないと ZIP 全体のハッシュも計算できない
        // ——という循環が生じる。これを避けるため 2 段階で組み立てる:
        //   1. GENERATED.txt を含まない全エントリ（conf・install.ps1・uninstall.ps1・Lua・
        //      README・(同梱時)MSI）だけで ZIP を組み立て、その全体バイト列の SHA256 を
        //      「Kit-SHA256」として採用する。
        //   2. その値を書き込んだ GENERATED.txt を追加した最終 ZIP を組み立て直す。
        // 「GENERATED.txt を除く全エントリのハッシュ」と定義することで自己参照を断ち切る
        // （設計条件 9 の要求「自己参照を避ける実装をコメントで明示」への対応）。
        //
        // 各 ZIP エントリの LastWriteTime は generatedAt に固定する（BuildArchive → WriteEntry）。
        // 未指定だと ZipArchiveEntry は作成時のウォールクロック（DOS 日時形式・2 秒粒度）を書き込むため、
        // 生成タイミング次第で「GENERATED.txt を除く内容」のバイト列が変わり、Kit-SHA256 が
        // 同一入力でも揺れてしまう（ハッシュ計算用・最終成果物用の 2 回の BuildArchive が 2 秒境界を
        // またぐと不一致になる）。generatedAt へ固定することで同一入力→同一 Kit-SHA256 を保証する。
        var kitShaSourceBytes = BuildArchive(conf, installScript, uninstallScript, luaFilter, readme, msiBytes, request, generatedAt);
        var kitSha256 = Convert.ToHexStringLower(SHA256.HashData(kitShaSourceBytes));

        var generatedTxt = BuildGeneratedTxt(request, generatedAt, kitSha256);

        return BuildArchive(conf, installScript, uninstallScript, luaFilter, readme, msiBytes, request, generatedAt, generatedTxt);
    }

    private static byte[]? ResolveTlsCaCertificateBytes(ForwarderKitRequest request) =>
        request is { Mode: ForwarderKitMode.Tls, TlsCaCertificatePem: { } pem }
            ? Encoding.UTF8.GetBytes(pem)
            : null;

    /// <summary>
    /// 同梱有無を反映したファイル名（例 <c>yagura-forwarder-kit-20260707-with-msi.zip</c> /
    /// <c>-no-msi.zip</c>。ADR-0008 設計条件 9「開かずに棚卸しできるようにする」）。
    /// </summary>
    public static string BuildFileName(ForwarderKitRequest request, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suffix = request.IncludeMsi ? "with-msi" : "no-msi";
        return $"yagura-forwarder-kit-{generatedAt:yyyyMMdd}-{suffix}.zip";
    }

    /// <summary>
    /// ZIP 本体を組み立てる（<paramref name="generatedTxt"/> が <see langword="null"/> の間は
    /// Kit-SHA256 計算用の GENERATED.txt 抜きアーカイブ、非 null なら最終アーカイブ）。
    /// </summary>
    private static byte[] BuildArchive(
        string conf,
        string installScript,
        string uninstallScript,
        string luaFilter,
        string readme,
        byte[]? msiBytes,
        ForwarderKitRequest request,
        DateTimeOffset generatedAt,
        string? generatedTxt = null)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // conf・install.ps1・uninstall.ps1・Lua は Fluent Bit（または PowerShell）が
            // パースする実行時アーティファクトのため BOM なし（WriteEntry 既定。下記参照）。
            // README・GENERATED.txt は人間が読む専用の生成物のため BOM 付きにする
            // （Issue #127: Windows PowerShell 5.1 の Get-Content や既定コードページの
            // コンソールで日本語が文字化けするのを防ぐ）。
            WriteEntry(archive, EntryNames.Conf, conf, generatedAt);
            WriteEntry(archive, EntryNames.InstallScript, installScript, generatedAt);
            WriteEntry(archive, EntryNames.UninstallScript, uninstallScript, generatedAt);
            WriteEntry(archive, EntryNames.LuaFilter, luaFilter, generatedAt);
            WriteEntry(archive, EntryNames.Readme, readme, generatedAt, includeBom: true);

            if (msiBytes is not null && request.MsiBundle is { } bundle)
            {
                WriteBinaryEntry(archive, bundle.FileName, msiBytes, generatedAt);
            }

            var tlsCaCertificateBytes = ResolveTlsCaCertificateBytes(request);
            if (tlsCaCertificateBytes is not null)
            {
                WriteBinaryEntry(archive, EntryNames.TlsCaCertificate, tlsCaCertificateBytes, generatedAt);
            }

            if (generatedTxt is not null)
            {
                WriteEntry(archive, EntryNames.Generated, generatedTxt, generatedAt, includeBom: true);
            }
        }

        return memoryStream.ToArray();
    }

    /// <summary>転送方式の文字列表現（install.ps1 の <c>-Mode</c> の語彙と揃える）。</summary>
    private static string ModeSlug(ForwarderKitMode mode) => mode switch
    {
        ForwarderKitMode.Tcp => "tcp",
        ForwarderKitMode.Tls => "tls",
        _ => "udp",
    };

    private static string SubstituteConf(string template, ForwarderKitRequest request)
    {
        // Fluent Bit の out_syslog に Mode = tls という値は無い（docs.fluentbit.io/manual/
        // data-pipeline/outputs/syslog、確認日 2026-07-10）——TLS は Mode tcp + tls On の組み合わせで
        // 有効化する（install.ps1 の同ロジックと揃える。Issue #137）。
        var fluentBitModeValue = request.Mode == ForwarderKitMode.Tls ? "tcp" : ModeSlug(request.Mode);

        var tlsBlockLines = new List<string>();
        if (request.Mode == ForwarderKitMode.Tls)
        {
            var hasCaCertificate = request.TlsCaCertificatePem is not null;
            tlsBlockLines.Add("    tls                 On");
            // CA/サーバ証明書が同梱されていれば検証する（tls.verify On + tls.ca_file）。
            // 未指定の場合、OS 既定の信頼ストアには Yagura の自己署名/内部 CA 証明書が
            // 含まれないため検証は失敗する——本製品は自己署名証明書の生成支援を提供しない
            // 設計判断（configuration.md §6）と同じ理由で、検証済み CA を持たない導入時の
            // 既定は「暗号化のみ・検証なし」に倒し、生成 README/GENERATED.txt で明示する。
            tlsBlockLines.Add("    tls.verify          " + (hasCaCertificate ? "On" : "Off"));
            if (hasCaCertificate)
            {
                tlsBlockLines.Add("    tls.ca_file         C:\\ProgramData\\fluent-bit-yagura\\" + EntryNames.TlsCaCertificate);
            }
        }

        return template
            .Replace("@@YAGURA_HOST@@", request.Host)
            .Replace("@@YAGURA_PORT@@", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@@CHANNELS@@", request.ChannelsValue)
            .Replace("@@MODE@@", fluentBitModeValue)
            .Replace("@@TLS_BLOCK@@", string.Join("\n", tlsBlockLines));
    }

    private static string SubstituteReadme(string template, ForwarderKitRequest request, DateTimeOffset generatedAt)
    {
        var substituted = template
            .Replace("@@YAGURA_HOST@@", request.Host)
            .Replace("@@YAGURA_PORT@@", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@@CHANNELS@@", request.ChannelsValue)
            .Replace("@@GENERATED_AT@@", FormatTimestamp(generatedAt))
            .Replace("@@FLUENTBIT_VERSION@@", ForwarderKitConstraints.VerifiedFluentBitVersion)
            .Replace("@@YAGURA_VERSION@@", YaguraVersion)
            .Replace("@@MODE_LABEL@@", BuildModeLabel(request.Mode))
            .Replace("@@TLS_NOTE@@", BuildTlsReadmeNote(request));

        // MSI 同梱時 / 非同梱時で案内を出し分ける（ADR-0008 委任 #7・README.generated.md の
        // @@MSI_SECTION@@ プレースホルダ。プレースホルダ方式で Builder が差し込む）。
        return substituted.Replace("@@MSI_SECTION@@", BuildMsiReadmeSection(request));
    }

    private static string BuildModeLabel(ForwarderKitMode mode) => mode switch
    {
        ForwarderKitMode.Tcp => "syslog / TCP",
        ForwarderKitMode.Tls => "syslog over TLS / TCP",
        _ => "syslog / UDP",
    };

    /// <summary>
    /// TLS 選択時のみ表示する注記（CA 証明書の同梱有無で内容を出し分ける。Issue #137）。
    /// TLS 以外では空文字列——README.generated.md 側の <c>@@TLS_NOTE@@</c> 行は空行になる。
    /// </summary>
    private static string BuildTlsReadmeNote(ForwarderKitRequest request)
    {
        if (request.Mode != ForwarderKitMode.Tls)
        {
            return string.Empty;
        }

        if (request.TlsCaCertificatePem is not null)
        {
            return
                """
                > **TLS 検証: 有効。** Yagura サーバの CA/サーバ証明書がキットに同梱されており
                > （`yagura-tls-ca.pem`）、`install.ps1` がこれを配置して `tls.verify On` で
                > 検証します。証明書を更新した場合はキットを再生成してください。
                """;
        }

        return
            """
            > **TLS 検証: 無効（`tls.verify Off`）。** このキットには Yagura サーバの証明書が
            > 同梱されていません——通信は暗号化されますが、サーバの真正性は検証されません
            > （経路上の攻撃者が別の証明書を提示しても検知できません）。管理 UI の生成画面で
            > Yagura の TLS 受信証明書（PEM 形式）を貼り付けてキットを再生成すると、検証を
            > 有効化できます。
            """;
    }

    /// <summary>
    /// README の MSI セクション（同梱時: 同梱済みである旨 + 来歴 + 免責。非同梱時: 既存の
    /// 取得手順案内をそのまま維持——ADR-0008 設計条件 9「既定（非同梱）の全条件は同梱時も維持する」）。
    /// </summary>
    private static string BuildMsiReadmeSection(ForwarderKitRequest request)
    {
        if (request.MsiBundle is not { } bundle)
        {
            return
                """
                Fluent Bit の MSI 本体は**このキットに同梱されていません**。以下の手順で取得してください。

                ### 1. Fluent Bit MSI を取得する

                [packages.fluentbit.io](https://packages.fluentbit.io/) から、検証済み版 **@@FLUENTBIT_VERSION@@** の
                Windows 64bit MSI を取得します。

                ```powershell
                Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-@@FLUENTBIT_VERSION@@-win64.msi" `
                                  -OutFile ".\fluent-bit-@@FLUENTBIT_VERSION@@-win64.msi"
                ```

                ### 2. SHA256 で取得物を検証する

                `packages.fluentbit.io` は版ごとの `.sha256` を配布していない場合があります。その場合は、
                以下のいずれかで取得元の正当性を確認してください。

                - Fluent Bit 公式サイト・公式 GitHub リリースページに掲載されたハッシュ値と突合する
                - 社内の信頼できるミラー・パッケージ管理基盤(Chocolatey・社内リポジトリ等)経由で取得する

                取得した MSI のハッシュ値は次のコマンドで確認できます。

                ```powershell
                Get-FileHash ".\fluent-bit-@@FLUENTBIT_VERSION@@-win64.msi" -Algorithm SHA256
                ```

                **入手元が確認できない MSI は導入しないでください。** `.sha256` が提供されていないことは
                検証を省略してよい理由にはなりません。

                ### 3. MSI をキットと同じフォルダに置き、サイレント導入を実行する(管理者権限)
                """
                    .Replace("@@FLUENTBIT_VERSION@@", ForwarderKitConstraints.VerifiedFluentBitVersion);
        }

        var officialHashLine = bundle.OfficialHashMatch switch
        {
            OfficialHashMatchResult.Match => "公式配布 SHA256 と**一致**しました。",
            OfficialHashMatchResult.Mismatch => "**公式配布 SHA256 と一致しませんでした。取得元・改ざんの有無を確認してください。**",
            _ => "公式配布 SHA256 との照合は未実施です（Yagura に公式ハッシュが未設定）。",
        };

        var versionLine = bundle.VersionMismatch
            ? $"**注意: 同梱 MSI の版（{bundle.ProductVersion ?? "不明"}）は検証済み版（{ForwarderKitConstraints.VerifiedFluentBitVersion}）と異なります。** " +
              "管理者が生成画面で明示確認のうえ同梱されています。"
            : $"版: {bundle.ProductVersion ?? "不明"}（検証済み版 {ForwarderKitConstraints.VerifiedFluentBitVersion} と一致）。";

        return
            $"""
            **MSI は同梱済みです。`install.ps1` をそのまま実行できます（追加の取得手順は不要です）。**
            同梱 MSI は既にキットと同じフォルダにあります。

            - ファイル名: `{bundle.FileName}`
            - {versionLine}
            - SHA256: `{bundle.Sha256}`
            - {officialHashLine}

            **免責**: 同梱 MSI について Yagura は取得元・真正性・脆弱性対応の責任を負いません。
            Yagura は管理者が配置したファイルを梱包し、その来歴（ファイル名・版・SHA256）を
            記録するのみです。

            ### MSI をキットと同じフォルダに置き、サイレント導入を実行する(管理者権限)
            """;
    }

    private static string BuildGeneratedTxt(ForwarderKitRequest request, DateTimeOffset generatedAt, string kitSha256)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Yagura forwarder kit - generation metadata (ADR-0008)");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Generated-At: {FormatTimestamp(generatedAt)}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Destination: {request.Host}:{request.Port}/{ModeSlug(request.Mode)}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Channels: {request.ChannelsValue}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Yagura-Version: {YaguraVersion}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Fluent-Bit-Verified: {ForwarderKitConstraints.VerifiedFluentBitVersion}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Msi-Bundled: {(request.IncludeMsi ? "true" : "false")}");

        if (request.Mode == ForwarderKitMode.Tls)
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Tls-Ca-Certificate-Bundled: {(request.TlsCaCertificatePem is not null ? "true" : "false")}");
        }

        if (request.MsiBundle is { } bundle)
        {
            var officialMatchValue = bundle.OfficialHashMatch switch
            {
                OfficialHashMatchResult.Match => "yes",
                OfficialHashMatchResult.Mismatch => "no",
                _ => "unverified",
            };

            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Msi-FileName: {bundle.FileName}");
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Msi-ProductVersion: {bundle.ProductVersion ?? "unknown"}");
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Msi-SHA256: {bundle.Sha256}");
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Msi-OfficialHashMatch: {officialMatchValue}");
        }

        // Kit-SHA256: GENERATED.txt 自身を除く全エントリのハッシュ（本メソッド冒頭の Build の
        // コメント参照。自己参照を避けるための定義）。
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Kit-SHA256: {kitSha256}");

        return builder.ToString();
    }

    /// <summary>
    /// 生成日時の表記（ISO 8601・オフセット明示。サーバ現地時刻をそのまま出す——
    /// <see cref="DateTimeOffset.ToString(string)"/> の "O" は往復可能かつオフセットを含む）。
    /// </summary>
    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 生成元 Yagura のバージョン（アセンブリ情報バージョン。設定キーを追加せず、
    /// ビルド済みアセンブリから機械的に取得する）。
    /// </summary>
    private static string YaguraVersion =>
        typeof(ForwarderKitBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ForwarderKitBuilder).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string ReadTemplate(string fileName)
    {
        var assembly = typeof(ForwarderKitBuilder).Assembly;
        var resourceName = ResourcePrefix + fileName;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"埋め込みテンプレートが見つかりません: {resourceName}（Yagura.Web.csproj の EmbeddedResource 設定を確認してください）。");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// テキストエントリを ZIP へ書き込む。
    /// </summary>
    /// <param name="includeBom">
    /// <see langword="true"/> なら UTF-8 BOM を付与する。既定は <see langword="false"/>
    /// （BOM なし UTF-8）——<c>fluent-bit-yagura.conf</c> ・ <c>install.ps1</c> ・
    /// <c>uninstall.ps1</c> ・ <c>winevt-severity.lua</c> は Fluent Bit や PowerShell が
    /// パースする実行時アーティファクトであり、install.ps1 の設定書き出し（BOM なし UTF-8。
    /// Fluent Bit のパーサが BOM を想定しない）と同じ判断を適用する。
    /// <c>README.md</c> ・ <c>GENERATED.txt</c> は人間が読む専用の生成物であり、これらの
    /// プログラムはパースしないため <see langword="true"/> を渡す（Issue #127: Windows
    /// PowerShell 5.1 の既定 <c>Get-Content</c> や既定コードページのコンソールで日本語が
    /// 文字化けするのを防ぐ。BOM があれば PowerShell 5.1 も UTF-8 と正しく認識する）。
    /// </param>
    private static void WriteEntry(ZipArchive archive, string entryName, string content, DateTimeOffset generatedAt, bool includeBom = false)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        // LastWriteTime を generatedAt へ固定（未指定だとウォールクロックが入り Kit-SHA256 が
        // 非決定的になる——Build のコメント参照）。
        entry.LastWriteTime = generatedAt;
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: includeBom));
        writer.Write(content);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string entryName, byte[] content, DateTimeOffset generatedAt)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        // LastWriteTime を generatedAt へ固定（WriteEntry と同じ理由）。
        entry.LastWriteTime = generatedAt;
        using var entryStream = entry.Open();
        entryStream.Write(content);
    }
}

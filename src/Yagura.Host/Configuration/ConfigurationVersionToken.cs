using System.Security.Cryptography;

namespace Yagura.Host.Configuration;

/// <summary>
/// 設定ファイル 1 回分の内容に対応するバージョントークン（configuration.md §3 の
/// 楽観的な競合検出で使う「読み込み時に得たトークン」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>最終書き込み時刻ではなく内容ハッシュ（SHA-256）を選んだ理由</b>:
/// <list type="bullet">
/// <item>
/// ファイルシステムのタイムスタンプ解像度は環境依存である（NTFS は 100ns 単位で保持するが、
/// 実際に観測される粒度は API・ボリューム設定により異なりうる）。短時間に複数回書き込むと
/// 衝突しうる値に競合検出の正しさを依存させたくない。
/// </item>
/// <item>
/// <see cref="File.Replace(string, string, string?)"/>（本モジュールが採用する原子的置換）は
/// 内部的に一時ファイルの生成・削除を伴い、実装（.NET バージョン・ファイルシステム）によって
/// 最終書き込み時刻の扱いが変わりうる。内容ハッシュは置換の実装詳細に左右されない。
/// </item>
/// <item>
/// 内容ハッシュは「意味のある差分があったかどうか」を直接表す。仮に将来何らかの操作で
/// ファイルの内容を変えずにタイムスタンプだけが更新されるケース（バックアップ復元・
/// 属性変更等）があっても、誤って競合と判定しない。
/// </item>
/// </list>
/// トレードオフとして、ハッシュ計算のたびにファイル全体を読む必要があるが、設定ファイルは
/// 数十キー程度の小さい JSON であり無視できるコストである。
/// </para>
/// </remarks>
public sealed class ConfigurationVersionToken : IEquatable<ConfigurationVersionToken>
{
    private readonly byte[] _hash;

    private ConfigurationVersionToken(byte[] hash)
    {
        _hash = hash;
    }

    /// <summary>設定ファイルが存在しない状態（初回保存前）に対応するトークン。</summary>
    public static ConfigurationVersionToken FileAbsent { get; } = new(Array.Empty<byte>());

    /// <summary>ファイルの内容バイト列からトークンを計算する。</summary>
    public static ConfigurationVersionToken FromContent(ReadOnlySpan<byte> content)
    {
        return new ConfigurationVersionToken(SHA256.HashData(content));
    }

    /// <summary>指定パスのファイルを読み、存在すればそのトークンを、存在しなければ <see cref="FileAbsent"/> を返す。</summary>
    public static ConfigurationVersionToken FromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return FileAbsent;
        }

        var bytes = File.ReadAllBytes(path);
        return FromContent(bytes);
    }

    public bool Equals(ConfigurationVersionToken? other)
    {
        if (other is null)
        {
            return false;
        }

        return _hash.AsSpan().SequenceEqual(other._hash);
    }

    public override bool Equals(object? obj) => Equals(obj as ConfigurationVersionToken);

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.AddBytes(_hash);
        return hashCode.ToHashCode();
    }
}

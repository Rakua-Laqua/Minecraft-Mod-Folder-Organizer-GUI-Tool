using System.IO;

namespace ModLangOrganizer.Domain;

/// <summary>競合解決ロジック</summary>
public sealed class ConflictResolver
{
    /// <summary>競合コピー名を生成する</summary>
    /// <param name="baseName">元のファイル名 (例: en_us.json)</param>
    /// <param name="sourceTag">元jar識別子</param>
    /// <param name="destDir">出力先ディレクトリ</param>
    /// <returns>一意な競合コピー名</returns>
    public string BuildConflictName(string baseName, string sourceTag, string destDir)
    {
        var ext = Path.GetExtension(baseName);        // .json
        var nameNoExt = Path.GetFileNameWithoutExtension(baseName); // en_us

        // 安全なsourceTagに変換（ファイル名不正文字を除去）
        var safeTag = string.Concat(sourceTag.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        int n = 1;
        string candidate;
        do
        {
            candidate = $"{nameNoExt}.conflict.{safeTag}.{n}{ext}";
            n++;
        } while (File.Exists(Path.Combine(destDir, candidate)));

        return candidate;
    }
}

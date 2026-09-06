using System.IO;
using System.Text;
using System.Text.Json;

namespace ModLangOrganizer.Domain;

/// <summary>
/// JAR側のlangファイルを構造の正として、既存の翻訳値だけを維持するマージ処理。
/// JSONはJAR側のキー順・空白・改行をそのまま使い、トップレベルの同一キーの値トークンだけを差し替える。
/// legacy .langもJAR側の行順・コメント・改行を維持し、同一キーの値だけを差し替える。
/// </summary>
public sealed class LangFileMerger
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public LangFileMergeResult MergeTargetFromJar(string sourcePath, string destinationPath)
    {
        var extension = Path.GetExtension(sourcePath);

        try
        {
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                return MergeJson(sourcePath, destinationPath);

            if (extension.Equals(".lang", StringComparison.OrdinalIgnoreCase))
                return MergeLegacyLang(sourcePath, destinationPath);

            OverwriteFromJar(sourcePath, destinationPath);
            return LangFileMergeResult.Overwritten();
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidDataException or OverflowException)
        {
            throw new InvalidDataException(
                $"翻訳ファイルをマージできないため既存ファイルを保持します: {ex.Message}",
                ex);
        }
    }

    /// <summary>JAR側のファイルで出力先を置き換える。</summary>
    public void OverwriteFromJar(string sourcePath, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException($"出力先ディレクトリを取得できません: {destinationPath}");

        Directory.CreateDirectory(destinationDirectory);
        var tempPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>旧仕様で生成された *.conflict.* のlangコピーを直下から削除する。</summary>
    public int CleanupLegacyConflictCopies(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".lang", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(file);
            if (!nameWithoutExtension.Contains(".conflict.", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Delete(file);
            removed++;
        }

        return removed;
    }

    private static LangFileMergeResult MergeJson(string sourcePath, string destinationPath)
    {
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var existingBytes = File.ReadAllBytes(destinationPath);

        var sourcePayload = GetUtf8Payload(sourceBytes, out var sourceHasBom);
        var existingPayload = GetUtf8Payload(existingBytes, out _);

        // 不正なUTF-8バイト列による不正ファイル生成・破壊を防止
        StrictUtf8.GetString(sourcePayload);
        StrictUtf8.GetString(existingPayload);

        var existingValues = ReadTopLevelJsonValues(existingPayload, out var existingKeys);
        var replacements = BuildJsonReplacements(
            sourcePayload,
            existingValues,
            out var sourceKeys,
            out var preservedKeys);

        var outputBytes = ApplyJsonReplacements(sourcePayload, sourceHasBom, replacements);
        WriteBytesAtomically(destinationPath, outputBytes);

        return LangFileMergeResult.Merged(
            preservedKeys,
            sourceKeys.Except(existingKeys, StringComparer.Ordinal).Count(),
            existingKeys.Except(sourceKeys, StringComparer.Ordinal).Count(),
            CountLines(sourcePayload),
            CountLines(GetUtf8Payload(outputBytes, out _)));
    }

    private static Dictionary<string, byte[]> ReadTopLevelJsonValues(
        ReadOnlySpan<byte> json,
        out HashSet<string> keys)
    {
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        keys = new HashSet<string>(StringComparer.Ordinal);
        var reader = CreateJsonReader(json);
        var rootStarted = false;
        var rootEnded = false;
        string? pendingKey = null;

        while (reader.Read())
        {
            if (!rootStarted)
            {
                if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 0)
                    throw new InvalidDataException("lang JSONのルートがオブジェクトではありません。");

                rootStarted = true;
                continue;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                pendingKey = reader.GetString()
                    ?? throw new InvalidDataException("lang JSONに空のプロパティ名があります。");
                keys.Add(pendingKey);
                continue;
            }

            if (pendingKey != null && reader.CurrentDepth == 1)
            {
                if (IsScalarJsonToken(reader.TokenType))
                {
                    var start = checked((int)reader.TokenStartIndex);
                    var end = checked((int)reader.BytesConsumed);
                    values[pendingKey] = json.Slice(start, end - start).ToArray();
                }

                pendingKey = null;
            }

            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                rootEnded = true;
        }

        if (!rootStarted || !rootEnded)
            throw new InvalidDataException("lang JSONのルートオブジェクトを最後まで読み取れませんでした。");

        return values;
    }

    private static List<JsonReplacement> BuildJsonReplacements(
        ReadOnlySpan<byte> sourceJson,
        IReadOnlyDictionary<string, byte[]> existingValues,
        out HashSet<string> sourceKeys,
        out int preservedKeys)
    {
        var replacements = new List<JsonReplacement>();
        sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        preservedKeys = 0;

        var reader = CreateJsonReader(sourceJson);
        var rootStarted = false;
        var rootEnded = false;
        string? pendingKey = null;

        while (reader.Read())
        {
            if (!rootStarted)
            {
                if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 0)
                    throw new InvalidDataException("JAR内lang JSONのルートがオブジェクトではありません。");

                rootStarted = true;
                continue;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                pendingKey = reader.GetString()
                    ?? throw new InvalidDataException("JAR内lang JSONに空のプロパティ名があります。");
                sourceKeys.Add(pendingKey);
                continue;
            }

            if (pendingKey != null && reader.CurrentDepth == 1)
            {
                if (IsScalarJsonToken(reader.TokenType) &&
                    existingValues.TryGetValue(pendingKey, out var existingValue))
                {
                    var start = checked((int)reader.TokenStartIndex);
                    var end = checked((int)reader.BytesConsumed);
                    replacements.Add(new JsonReplacement(start, end - start, existingValue));
                    preservedKeys++;
                }

                pendingKey = null;
            }

            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                rootEnded = true;
        }

        if (!rootStarted || !rootEnded)
            throw new InvalidDataException("JAR内lang JSONのルートオブジェクトを最後まで読み取れませんでした。");

        return replacements;
    }

    private static byte[] ApplyJsonReplacements(
        ReadOnlySpan<byte> sourceJson,
        bool sourceHasBom,
        IReadOnlyList<JsonReplacement> replacements)
    {
        using var output = new MemoryStream(sourceJson.Length + (sourceHasBom ? Utf8Bom.Length : 0));
        if (sourceHasBom)
            output.Write(Utf8Bom);

        var cursor = 0;
        foreach (var replacement in replacements.OrderBy(r => r.Start))
        {
            if (replacement.Start < cursor || replacement.Start + replacement.Length > sourceJson.Length)
                throw new InvalidDataException("lang JSONの値置換範囲が不正です。");

            output.Write(sourceJson[cursor..replacement.Start]);
            output.Write(replacement.Value);
            cursor = replacement.Start + replacement.Length;
        }

        output.Write(sourceJson[cursor..]);
        return output.ToArray();
    }

    private static LangFileMergeResult MergeLegacyLang(string sourcePath, string destinationPath)
    {
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var existingBytes = File.ReadAllBytes(destinationPath);

        var sourcePayload = GetUtf8Payload(sourceBytes, out var sourceHasBom);
        var existingPayload = GetUtf8Payload(existingBytes, out _);
        var sourceText = StrictUtf8.GetString(sourcePayload);
        var existingText = StrictUtf8.GetString(existingPayload);

        var existingValues = ReadLegacyLangValues(existingText, out var existingKeys);
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var preservedKeys = 0;
        var output = new StringBuilder(sourceText.Length);

        foreach (var line in SplitLinesPreservingEndings(sourceText))
        {
            var content = line.Content;
            if (TryParseLegacyLangEntry(content, out var key, out var valueStart))
            {
                sourceKeys.Add(key);
                if (existingValues.TryGetValue(key, out var existingValue))
                {
                    output.Append(content.AsSpan(0, valueStart));
                    output.Append(existingValue);
                    preservedKeys++;
                }
                else
                {
                    output.Append(content);
                }
            }
            else
            {
                output.Append(content);
            }

            output.Append(line.Ending);
        }

        var outputPayload = StrictUtf8.GetBytes(output.ToString());
        var outputBytes = AddUtf8BomIfNeeded(outputPayload, sourceHasBom);
        WriteBytesAtomically(destinationPath, outputBytes);

        return LangFileMergeResult.Merged(
            preservedKeys,
            sourceKeys.Except(existingKeys, StringComparer.Ordinal).Count(),
            existingKeys.Except(sourceKeys, StringComparer.Ordinal).Count(),
            CountTextLines(sourceText),
            CountTextLines(output.ToString()));
    }

    private static Dictionary<string, string> ReadLegacyLangValues(
        string text,
        out HashSet<string> keys)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in SplitLinesPreservingEndings(text))
        {
            if (!TryParseLegacyLangEntry(line.Content, out var key, out var valueStart))
                continue;

            keys.Add(key);
            values[key] = line.Content[valueStart..];
        }

        return values;
    }

    private static bool TryParseLegacyLangEntry(string line, out string key, out int valueStart)
    {
        key = string.Empty;
        valueStart = -1;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return false;

        var separator = line.IndexOf('=');
        if (separator <= 0)
            return false;

        key = line[..separator].Trim();
        if (key.Length == 0)
            return false;

        valueStart = separator + 1;
        return true;
    }

    private static List<TextLine> SplitLinesPreservingEndings(string text)
    {
        var lines = new List<TextLine>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;

            var endingLength = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n'
                ? 2
                : 1;

            lines.Add(new TextLine(
                text[start..i],
                text.Substring(i, endingLength)));

            i += endingLength - 1;
            start = i + 1;
        }

        if (start < text.Length)
            lines.Add(new TextLine(text[start..], string.Empty));

        return lines;
    }

    private static Utf8JsonReader CreateJsonReader(ReadOnlySpan<byte> json) =>
        new(json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 128
        });

    private static bool IsScalarJsonToken(JsonTokenType tokenType) =>
        tokenType is JsonTokenType.String or
            JsonTokenType.Number or
            JsonTokenType.True or
            JsonTokenType.False or
            JsonTokenType.Null;

    private static ReadOnlySpan<byte> GetUtf8Payload(byte[] bytes, out bool hasBom)
    {
        hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        return hasBom ? bytes.AsSpan(Utf8Bom.Length) : bytes;
    }

    private static byte[] AddUtf8BomIfNeeded(byte[] payload, bool hasBom)
    {
        if (!hasBom)
            return payload;

        var output = new byte[Utf8Bom.Length + payload.Length];
        Utf8Bom.CopyTo(output, 0);
        payload.CopyTo(output, Utf8Bom.Length);
        return output;
    }

    private static void WriteBytesAtomically(string destinationPath, byte[] bytes)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException($"出力先ディレクトリを取得できません: {destinationPath}");

        Directory.CreateDirectory(destinationDirectory);
        var tempPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static int CountLines(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return 0;

        var count = 1;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                count++;
            }
            else if (bytes[i] == (byte)'\r' &&
                     (i + 1 >= bytes.Length || bytes[i + 1] != (byte)'\n'))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountTextLines(string text)
    {
        if (text.Length == 0)
            return 0;

        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
            else if (text[i] == '\r' &&
                     (i + 1 >= text.Length || text[i + 1] != '\n'))
            {
                count++;
            }
        }

        return count;
    }

    private sealed record JsonReplacement(int Start, int Length, byte[] Value);
    private sealed record TextLine(string Content, string Ending);
}

public sealed record LangFileMergeResult(
    bool WasMerged,
    bool WasOverwritten,
    bool UsedFallbackOverwrite,
    int PreservedKeys,
    int AddedKeys,
    int RemovedKeys,
    int SourceLineCount,
    int OutputLineCount,
    string? Warning)
{
    public static LangFileMergeResult Merged(
        int preservedKeys,
        int addedKeys,
        int removedKeys,
        int sourceLineCount,
        int outputLineCount) =>
        new(
            WasMerged: true,
            WasOverwritten: false,
            UsedFallbackOverwrite: false,
            PreservedKeys: preservedKeys,
            AddedKeys: addedKeys,
            RemovedKeys: removedKeys,
            SourceLineCount: sourceLineCount,
            OutputLineCount: outputLineCount,
            Warning: null);

    public static LangFileMergeResult Overwritten() =>
        new(false, true, false, 0, 0, 0, 0, 0, null);

    public static LangFileMergeResult FallbackOverwrite(string warning) =>
        new(false, true, true, 0, 0, 0, 0, 0, warning);
}

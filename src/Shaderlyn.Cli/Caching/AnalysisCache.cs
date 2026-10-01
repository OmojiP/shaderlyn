using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Caching;

/// <summary>
/// 実行のあいだで解析結果を持ち越す仕組み。
/// </summary>
/// <remarks>
/// <para>
/// <b>CI と pre-commit では、前回から変わっていないファイルを解析し直す必要が無い。</b>
/// 1 ファイルの解析には include の連鎖の展開が伴うため、
/// 変えていないファイルを飛ばせるかどうかで所要時間が桁で変わる。
/// </para>
/// <para>
/// <b>鍵に取り込むファイル一式を含めるのが要点である。</b>
/// シェーダー自身が変わっていなくても、取り込んでいるヘッダが変われば結果は変わる。
/// 取り込むファイル一式を見ないキャッシュは「ヘッダを直したのに指摘が消えない」あるいは
/// 「ヘッダを壊したのに指摘が出ない」という、静的解析として最悪の壊れ方をする。
/// </para>
/// <para>
/// <b>既定では作らない。</b>
/// 消し方の分からないキャッシュは、誤った結果が出たときに利用者を袋小路へ追い込む。
/// <c>--cache</c> で明示的に指定されたときだけ働き、
/// そのファイルを消せば必ず元の挙動に戻る。
/// </para>
/// <para>
/// <b>キャッシュファイルはバージョン管理へ入れるものではない。</b>
/// パスは絶対パスで記録する。取り込み先には Unity のインストール先も含まれ、
/// そこは元からマシンごとに違うためである。
/// 別のマシンへ持って行った場合は、鍵が一致せず全件が解析し直しになる。
/// </para>
/// </remarks>
internal sealed class AnalysisCache
{
    /// <summary>このツールが理解するキャッシュの書式のバージョン。</summary>
    public const int SupportedVersion = 2;

    /// <summary>ファイルが存在しない (あるいは読めない) ことを表すハッシュ。</summary>
    /// <remarks>
    /// 実際のハッシュと衝突しない値にしてある。
    /// 「無かった」ことを記録しないと、あとからヘッダが増えても使い回してしまう。
    /// </remarks>
    private const string MissingHash = "-";

    /// <summary>パスの比較に使う比較子。</summary>
    /// <remarks>
    /// Windows では大文字小文字が区別されない。
    /// セマンティックモデルが取り込み先を集める際も同じ比較をしている。
    /// </remarks>
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ConcurrentDictionary<string, Entry> _loaded;
    private readonly ConcurrentDictionary<string, Entry> _fresh = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _fileHashes = new(PathComparer);

    /// <summary>復元のために読んだ、取り込んだファイルのテキスト。</summary>
    private readonly ConcurrentDictionary<string, SourceText?> _includedTexts = new(PathComparer);
    private readonly string _key;
    private int _hits;
    private int _misses;

    private AnalysisCache(string key, ConcurrentDictionary<string, Entry> loaded)
    {
        _key = key;
        _loaded = loaded;
    }

    /// <summary>前回の結果を使い回せた件数。</summary>
    public int Hits => _hits;

    /// <summary>使い回せず解析した件数。</summary>
    public int Misses => _misses;

    /// <summary>読み込めた記録の件数。</summary>
    public int LoadedCount => _loaded.Count;

    /// <summary>
    /// キャッシュファイルを読み込む。
    /// </summary>
    /// <param name="filePath">キャッシュファイルのパス。存在しなくてよい。</param>
    /// <param name="key">解析ツールと設定を表す鍵。一致しない記録は捨てる。</param>
    /// <param name="warning">読み込めなかった場合の説明。捨てて続行してよい。</param>
    /// <returns>読み込んだキャッシュ。読めなかった場合は空のキャッシュ。</returns>
    /// <remarks>
    /// <b>読み込みの失敗は解析の失敗ではない。</b>
    /// キャッシュはあくまで速さのための仕組みであり、
    /// 壊れていたら捨てて全件を解析し直せば正しい結果になる。
    /// ベースライン (<see cref="Baseline.DiagnosticBaseline"/>) が読み込み失敗を
    /// ツールエラーにするのとは、ここが決定的に違う。
    /// </remarks>
    public static AnalysisCache Load(string filePath, string key, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(key);

        warning = null;
        ConcurrentDictionary<string, Entry> loaded = new(PathComparer);

        if (!File.Exists(filePath))
        {
            return new AnalysisCache(key, loaded);
        }

        AnalysisCacheDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                File.ReadAllText(filePath), AnalysisCacheJsonContext.Default.AnalysisCacheDocument);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = ex.Message;
            return new AnalysisCache(key, loaded);
        }

        // バージョンが違う・鍵が違うのは異常ではない。
        // 解析ツールを更新したか、設定を変えたかのどちらかであり、断りなく全件を解析し直す。
        if (document is null
            || document.Version != SupportedVersion
            || !string.Equals(document.Key, key, StringComparison.Ordinal))
        {
            return new AnalysisCache(key, loaded);
        }

        foreach (CachedFile file in document.Files)
        {
            if (TryRestoreEntry(document, file) is { } entry)
            {
                loaded[document.Sources[file.Source]] = entry;
            }
        }

        return new AnalysisCache(key, loaded);
    }

    /// <summary>
    /// 前回の結果を使い回せるかを調べる。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="diagnostics">使い回せた場合の診断。</param>
    /// <returns>使い回せた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// ファイル自身の内容と、取り込むファイル一式にあるすべてのファイルの内容を突き合わせる。
    /// 1 つでも違えば使い回さない。
    /// </remarks>
    public bool TryGetDiagnostics(SourceText text, out ImmutableArray<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);

        diagnostics = [];

        if (!_loaded.TryGetValue(Normalize(text.FilePath), out Entry? entry)
            || !string.Equals(entry.Hash, HashContent(text.Content), StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        foreach (IncludeStamp include in entry.Includes)
        {
            if (!string.Equals(HashFile(include.Path), include.Hash, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _misses);
                return false;
            }
        }

        ImmutableArray<Diagnostic>.Builder restored =
            ImmutableArray.CreateBuilder<Diagnostic>(entry.Diagnostics.Length);

        foreach (EntryDiagnostic diagnostic in entry.Diagnostics)
        {
            // 取り込んだヘッダを指す指摘は、そのヘッダを読み直して位置を作る。
            // 内容が記録したときと同じことは、上で取り込むファイル一式のハッシュを照合して確かめてある。
            SourceText? target = diagnostic.FilePath is { } other ? LoadIncludedText(other) : text;

            if (target is null || diagnostic.Start + diagnostic.Length > target.Length)
            {
                Interlocked.Increment(ref _misses);
                return false;
            }

            restored.Add(diagnostic.Restore(target));
        }

        // 次回の書き出しでも残るように、使い回した記録をそのまま持ち越す。
        _fresh[Normalize(text.FilePath)] = entry;
        Interlocked.Increment(ref _hits);
        diagnostics = restored.MoveToImmutable();
        return true;
    }

    /// <summary>
    /// 解析結果を記録する。
    /// </summary>
    /// <param name="text">解析したソーステキスト。</param>
    /// <param name="includePaths">解析中に解決した取り込み先。解決できなかったパスも含む。</param>
    /// <param name="diagnostics">そのファイルで報告された指摘。</param>
    /// <remarks>
    /// <para>
    /// <b>取り込むファイル一式の外を指す指摘があれば、そのファイルは記録しない。</b>
    /// 復元にはその指摘が指すファイルの内容が要る。
    /// 利用者が書いて取り込んだヘッダの指摘は記録する。ヘッダの内容は取り込むファイル一式の照合で確かめられる。
    /// </para>
    /// <para>
    /// <b>同じパスであれば、別のソーステキストを指していてもよい。</b>
    /// 埋め込み HLSL は元のファイルをマスクした複製の上で解析される。
    /// 複製は元のファイルと同じパス・同じ長さを持ち、
    /// 埋め込みコードの範囲では中身も一致するため、
    /// 位置を元のファイルの上に作り直しても同じ行を指す。
    /// </para>
    /// </remarks>
    public void Store(SourceText text, ImmutableArray<string> includePaths, ImmutableArray<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);

        ImmutableArray<EntryDiagnostic>.Builder entries =
            ImmutableArray.CreateBuilder<EntryDiagnostic>(diagnostics.Length);

        HashSet<string> included = new(includePaths.Select(Normalize), PathComparer);

        foreach (Diagnostic diagnostic in diagnostics)
        {
            string path = diagnostic.Location.FilePath;
            bool own = string.Equals(path, text.FilePath, StringComparison.Ordinal);

            if ((own && diagnostic.Location.Span.End > text.Length)
                || (!own && !included.Contains(Normalize(path)))
                || EntryDiagnostic.TryCreate(diagnostic, own ? null : path) is not { } entry)
            {
                return;
            }

            entries.Add(entry);
        }

        ImmutableArray<IncludeStamp>.Builder includes =
            ImmutableArray.CreateBuilder<IncludeStamp>(includePaths.Length);

        foreach (string include in includePaths)
        {
            string normalized = Normalize(include);
            includes.Add(new IncludeStamp(normalized, HashFile(normalized)));
        }

        _fresh[Normalize(text.FilePath)] = new Entry(
            HashContent(text.Content), includes.MoveToImmutable(), entries.MoveToImmutable());
    }

    /// <summary>
    /// キャッシュファイルを書き出す。
    /// </summary>
    /// <param name="filePath">書き出す先のパス。</param>
    /// <param name="error">書き出せなかった場合の説明。</param>
    /// <returns>書き出せた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>今回解析しなかったファイルの記録も残す。</b>
    /// 1 ファイルだけを指定して実行するたびに他のファイルの記録が消えるようでは、
    /// pre-commit で使ったあとの CI が全件解析になる。
    /// </para>
    /// <para>
    /// 一時ファイルへ書いてから置き換える。
    /// 書き出しの途中で中断されたキャッシュファイルが残ると、
    /// 次回は読み込みに失敗して全件解析になる。
    /// </para>
    /// </remarks>
    public bool TrySave(string filePath, out string? error)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        Dictionary<string, Entry> merged = new(_loaded, PathComparer);

        foreach (KeyValuePair<string, Entry> entry in _fresh)
        {
            merged[entry.Key] = entry.Value;
        }

        AnalysisCacheDocument document = new() { Version = SupportedVersion, Key = _key };
        Dictionary<string, int> sources = new(PathComparer);
        Dictionary<string, int> rules = new(StringComparer.Ordinal);

        // 出力は決定的に並べる。差分を見て中身を確かめられるようにするためである。
        foreach (KeyValuePair<string, Entry> pair in merged.OrderBy(p => p.Key, PathComparer))
        {
            // 消えたファイルの記録は捨てる。放っておくとキャッシュが際限なく育つ。
            if (!File.Exists(pair.Key))
            {
                continue;
            }

            document.Files.Add(pair.Value.ToDocument(
                Index(sources, pair.Key, document.Sources), sources, rules, document));
        }

        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(filePath)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = filePath + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(document, AnalysisCacheJsonContext.Default.AnalysisCacheDocument));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>取り込んだファイルを読む。同じ実行の中では 1 度しか読まない。</summary>
    /// <param name="path">指摘に記録されたパス。</param>
    /// <returns>読んだテキスト。読めない場合は <see langword="null"/>。</returns>
    /// <remarks>取り込みの解決と同じ形で読む。位置と周辺の行を同じに作るためである。</remarks>
    private SourceText? LoadIncludedText(string path)
        => _includedTexts.GetOrAdd(path, static p =>
        {
            try
            {
                return File.Exists(p) ? SourceText.From(File.ReadAllText(p), p) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        });

    /// <summary>パスを比較できる形に揃える。</summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>絶対パス。求められない場合は元のパス。</returns>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>文字列の内容からハッシュを求める。</summary>
    /// <param name="content">対象の内容。</param>
    /// <returns>16 桁の 16 進数。</returns>
    /// <remarks>
    /// <b><see cref="string.GetHashCode()"/> は使えない。</b>
    /// .NET の文字列ハッシュはプロセスごとに変わるため、
    /// 実行をまたいで比較する用途には使えない。
    /// </remarks>
    private static string HashContent(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)).AsSpan(0, 8));

    /// <summary>表に無ければ加えて、添字を返す。</summary>
    /// <param name="index">これまでに割り当てた添字。</param>
    /// <param name="value">対象の値。</param>
    /// <param name="table">値を並べる表。</param>
    /// <returns>割り当てられた添字。</returns>
    private static int Index(Dictionary<string, int> index, string value, List<string> table)
    {
        if (index.TryGetValue(value, out int existing))
        {
            return existing;
        }

        index[value] = table.Count;
        table.Add(value);
        return table.Count - 1;
    }

    /// <summary>記録されたファイル 1 つ分を復元する。</summary>
    /// <param name="document">キャッシュファイルの内容。</param>
    /// <param name="file">復元する記録。</param>
    /// <returns>復元した記録。復元できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 添字が範囲外であるような壊れた内容は、その記録だけを捨てる。
    /// キャッシュ全体を捨てるほどのことではない。
    /// </remarks>
    private static Entry? TryRestoreEntry(AnalysisCacheDocument document, CachedFile file)
    {
        if (file.Source < 0 || file.Source >= document.Sources.Count)
        {
            return null;
        }

        ImmutableArray<IncludeStamp>.Builder includes =
            ImmutableArray.CreateBuilder<IncludeStamp>(file.Includes.Count);

        foreach (CachedInclude include in file.Includes)
        {
            if (include.Source < 0 || include.Source >= document.Sources.Count)
            {
                return null;
            }

            includes.Add(new IncludeStamp(document.Sources[include.Source], include.Hash));
        }

        foreach (CachedDiagnostic diagnostic in file.Diagnostics)
        {
            if (diagnostic.File >= document.Sources.Count)
            {
                return null;
            }
        }

        ImmutableArray<EntryDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<EntryDiagnostic>(file.Diagnostics.Count);

        foreach (CachedDiagnostic diagnostic in file.Diagnostics)
        {
            if (diagnostic.Rule < 0 || diagnostic.Rule >= document.Rules.Count
                || EntryDiagnostic.TryRestore(
                    document.Rules[diagnostic.Rule],
                    diagnostic,
                    diagnostic.File >= 0 ? document.Sources[diagnostic.File] : null) is not { } restored)
            {
                return null;
            }

            diagnostics.Add(restored);
        }

        return new Entry(file.Hash, includes.MoveToImmutable(), diagnostics.MoveToImmutable());
    }

    /// <summary>ファイルの内容からハッシュを求める。同じ実行の中では 1 度しか読まない。</summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>16 桁の 16 進数。読めない場合は <see cref="MissingHash"/>。</returns>
    /// <remarks>
    /// 中規模プロジェクトでは同じヘッダを数百のシェーダーが取り込む。
    /// 覚えずに毎回読むと、キャッシュの照合そのものが解析より重くなる。
    /// </remarks>
    private string HashFile(string path)
        => _fileHashes.GetOrAdd(path, static p =>
        {
            try
            {
                using FileStream stream = File.OpenRead(p);
                return Convert.ToHexStringLower(SHA256.HashData(stream).AsSpan(0, 8));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return MissingHash;
            }
        });

    /// <summary>取り込んだファイル 1 つと、記録した時点での内容のハッシュ。</summary>
    /// <param name="Path">取り込んだファイルの絶対パス。</param>
    /// <param name="Hash">記録した時点での内容のハッシュ。</param>
    private readonly record struct IncludeStamp(string Path, string Hash);

    /// <summary>1 ファイル分の記録。</summary>
    /// <param name="Hash">記録した時点でのそのファイルの内容のハッシュ。</param>
    /// <param name="Includes">取り込むファイル一式。</param>
    /// <param name="Diagnostics">報告された指摘。</param>
    private sealed record Entry(
        string Hash,
        ImmutableArray<IncludeStamp> Includes,
        ImmutableArray<EntryDiagnostic> Diagnostics)
    {
        /// <summary>キャッシュファイルへ書き出す形へ直す。</summary>
        /// <param name="sourceIndex">このファイルのパスの添字。</param>
        /// <param name="sources">パスに割り当てた添字。</param>
        /// <param name="rules">ルール ID に割り当てた添字。</param>
        /// <param name="document">書き出し先。</param>
        /// <returns>書き出す内容。</returns>
        public CachedFile ToDocument(
            int sourceIndex,
            Dictionary<string, int> sources,
            Dictionary<string, int> rules,
            AnalysisCacheDocument document)
        {
            CachedFile file = new() { Source = sourceIndex, Hash = Hash };

            foreach (IncludeStamp include in Includes)
            {
                file.Includes.Add(new CachedInclude
                {
                    Source = Index(sources, include.Path, document.Sources),
                    Hash = include.Hash,
                });
            }

            foreach (EntryDiagnostic diagnostic in Diagnostics)
            {
                file.Diagnostics.Add(diagnostic.ToDocument(rules, sources, document));
            }

            return file;
        }
    }

    /// <summary>記録された指摘 1 件。</summary>
    /// <param name="Descriptor">
    /// 報告元のルール定義。<see cref="DiagnosticDescriptor.MessageFormat"/> には
    /// 組み立て終わったメッセージが入っている。
    /// </param>
    /// <param name="Start">指摘箇所の開始位置。</param>
    /// <param name="Length">指摘箇所の長さ。</param>
    /// <param name="Severity">実効重要度。</param>
    /// <param name="IsSeverityExplicit">報告元が重要度を明示しているかどうか。</param>
    /// <param name="SuggestedReplacement">指摘箇所を置き換える文字列。</param>
    /// <param name="FilePath">指摘箇所がある取り込んだファイル。解析したファイル自身なら <see langword="null"/>。</param>
    private sealed record EntryDiagnostic(
        DiagnosticDescriptor Descriptor,
        int Start,
        int Length,
        DiagnosticSeverity Severity,
        bool IsSeverityExplicit,
        string? SuggestedReplacement,
        string? FilePath)
    {
        /// <summary>診断から記録を作る。</summary>
        /// <param name="diagnostic">対象の診断。</param>
        /// <param name="filePath">指摘箇所がある取り込んだファイル。解析したファイル自身なら <see langword="null"/>。</param>
        /// <returns>作った記録。作れない場合は <see langword="null"/>。</returns>
        public static EntryDiagnostic? TryCreate(Diagnostic diagnostic, string? filePath)
        {
            string message = diagnostic.GetMessage();

            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return new EntryDiagnostic(
                Rebuild(diagnostic.Descriptor, message),
                diagnostic.Location.Span.Start,
                diagnostic.Location.Span.Length,
                diagnostic.Severity,
                diagnostic.IsSeverityExplicit,
                diagnostic.SuggestedReplacement,
                filePath);
        }

        /// <summary>キャッシュファイルの内容から記録を復元する。</summary>
        /// <param name="rule">ルール定義。</param>
        /// <param name="diagnostic">記録された指摘。</param>
        /// <param name="filePath">指摘箇所がある取り込んだファイル。解析したファイル自身なら <see langword="null"/>。</param>
        /// <returns>復元した記録。復元できない場合は <see langword="null"/>。</returns>
        public static EntryDiagnostic? TryRestore(CachedRule rule, CachedDiagnostic diagnostic, string? filePath)
        {
            if (!Enum.TryParse(rule.DefaultSeverity, out DiagnosticSeverity defaultSeverity)
                || !Enum.TryParse(diagnostic.Severity, out DiagnosticSeverity severity)
                || string.IsNullOrWhiteSpace(diagnostic.Message)
                || diagnostic.Start < 0
                || diagnostic.Length < 0)
            {
                return null;
            }

            DiagnosticDescriptor descriptor;
            try
            {
                descriptor = new DiagnosticDescriptor(
                    rule.Id,
                    rule.Title,
                    diagnostic.Message,
                    rule.Category,
                    defaultSeverity,
                    rule.Description,
                    rule.HelpLinkUri);
            }
            catch (ArgumentException)
            {
                return null;
            }

            return new EntryDiagnostic(
                descriptor,
                diagnostic.Start,
                diagnostic.Length,
                severity,
                diagnostic.IsSeverityExplicit,
                diagnostic.SuggestedReplacement,
                filePath);
        }

        /// <summary>記録から診断を組み立て直す。</summary>
        /// <param name="text">解析対象のソーステキスト。</param>
        /// <returns>組み立て直した診断。</returns>
        /// <remarks>
        /// 位置は今回読み込んだソーステキストの上に作り直す。
        /// 内容が一致していることは呼び出し側が確かめている。
        /// </remarks>
        public Diagnostic Restore(SourceText text)
        {
            Location location = Location.Create(text, new TextSpan(Start, Length));

            Diagnostic diagnostic = IsSeverityExplicit
                ? Diagnostic.Create(Descriptor, location, Severity)
                : Diagnostic.Create(Descriptor, location).WithSeverity(Severity);

            return SuggestedReplacement is { } replacement
                ? diagnostic.WithSuggestedReplacement(replacement)
                : diagnostic;
        }

        /// <summary>キャッシュファイルへ書き出す形へ直す。</summary>
        /// <param name="rules">ルール ID に割り当てた添字。</param>
        /// <param name="sources">パスに割り当てた添字。</param>
        /// <param name="document">書き出し先。</param>
        /// <returns>書き出す内容。</returns>
        public CachedDiagnostic ToDocument(
            Dictionary<string, int> rules,
            Dictionary<string, int> sources,
            AnalysisCacheDocument document)
        {
            if (!rules.TryGetValue(Descriptor.Id, out int index))
            {
                index = rules[Descriptor.Id] = document.Rules.Count;
                document.Rules.Add(new CachedRule
                {
                    Id = Descriptor.Id,
                    Title = Descriptor.Title,
                    Category = Descriptor.Category,
                    DefaultSeverity = Descriptor.DefaultSeverity.ToString(),
                    Description = Descriptor.Description,
                    HelpLinkUri = Descriptor.HelpLinkUri,
                });
            }

            return new CachedDiagnostic
            {
                Rule = index,
                File = FilePath is { } path ? Index(sources, path, document.Sources) : -1,
                Start = Start,
                Length = Length,
                Severity = Severity.ToString(),
                IsSeverityExplicit = IsSeverityExplicit,
                Message = Descriptor.MessageFormat,
                SuggestedReplacement = SuggestedReplacement,
            };
        }

        /// <summary>メッセージを差し替えたルール定義を作る。</summary>
        /// <param name="descriptor">元のルール定義。</param>
        /// <param name="message">組み立て終わったメッセージ。</param>
        /// <returns>差し替えたルール定義。</returns>
        /// <remarks>
        /// <see cref="Diagnostic.GetMessage"/> は引数が無ければ書式をそのまま返すため、
        /// 書式の位置へ完成したメッセージを入れておけば、
        /// 波括弧を含むメッセージでも書式として解釈されずに復元できる。
        /// </remarks>
        private static DiagnosticDescriptor Rebuild(DiagnosticDescriptor descriptor, string message)
            => new(
                descriptor.Id,
                descriptor.Title,
                message,
                descriptor.Category,
                descriptor.DefaultSeverity,
                descriptor.Description,
                descriptor.HelpLinkUri);
    }
}

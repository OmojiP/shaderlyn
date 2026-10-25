using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Unreal;

/// <summary>
/// エンジンとプロジェクトの C++ から、ビルドのときに生成される uniform buffer の宣言
/// (<c>/Engine/Generated/UniformBuffers/&lt;名前&gt;.ush</c>、<c>GeneratedUniformBuffers.ush</c>、<c>GeneratedInstancedStereo.ush</c>) を作る。
/// </summary>
/// <remarks>
/// <para>
/// エンジンは C++ の <c>BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT</c> などで書いた構造体から、コンパイルの直前に宣言を作る
/// (UE 5.5 の <c>ShaderParameters.cpp</c> の <c>CreateHLSLUniformBufferDeclaration</c>)。ディスクには無いので、
/// それを使うシェーダーは宣言が揃わず、宣言の検査ができなかった。同じ C++ を読んで宣言を作る。
/// </para>
/// <para>
/// <b>C++ のマクロは、Shaderlyn のプリプロセッサで、ファイルの中で定義された順に展開する。</b>
/// View のメンバーはマクロの表 (<c>VIEW_UNIFORM_BUFFER_MEMBER_TABLE</c>) で書かれ、表の中のマクロは
/// View と InstancedView で定義し直されている。<c>SHADER_PARAMETER</c> などは定義せずに残し、それを拾う。
/// <c>#if</c> はすべての分岐を読む (どれかの構成にあるメンバーは、宣言されているものとする)。<c>#include</c> は読まない。
/// </para>
/// <para>
/// 作る宣言は、エンジンのものと同じ名前 (<c>View_TranslatedWorldToClip</c> の cbuffer のメンバーと、
/// <c>View.TranslatedWorldToClip</c> で参照できる構造体) を持つ。エンジンは前処理の後に <c>View.X</c> を <c>View_X</c> へ
/// 文字列で置き換えるが (<c>CleanupUniformBufferCode</c>)、Shaderlyn は置き換えずに解析するので、構造体の変数として宣言する。
/// 定数バッファの詰め物 (<c>Padding</c>) とバインドレスの名前は作らない (名前の検査に要らない)。
/// </para>
/// </remarks>
public static partial class UnrealUniformBufferGenerator
{
    /// <summary>
    /// エンジンの C++ がシェーダーに定義するマクロの名前を、1 行に 1 つ書いたファイル。書き出す先の直下に置く。
    /// </summary>
    public const string EngineDefinesFileName = "ShaderlynEngineDefines.txt";

    /// <summary>Scene の uniform buffer の名前。</summary>
    private const string SceneUniformBufferName = "Scene";

    /// <summary>配列の要素数を読めなかったときに使う数。添字の範囲の検査で誤検出を出さないよう大きくする。</summary>
    private const int UnknownArraySize = 4096;

    /// <summary>
    /// C++ を読んで、生成される取り込みを書き出す。
    /// </summary>
    /// <param name="sourceRoots">
    /// C++ を探すフォルダー (エンジンの <c>Source</c> と <c>Plugins</c> と <c>Shaders/Shared</c>、プロジェクトの <c>Source</c> と <c>Plugins</c>)。
    /// </param>
    /// <param name="outputDirectory">書き出す先。<c>/Engine/Generated</c> に割り当てるフォルダー。</param>
    /// <param name="platformSettings">
    /// 解析するシェーダープラットフォームの設定 (<see cref="UnrealShaderLanguageVersions.ReadShaderPlatformSettings"/>)。
    /// パーミュテーションを読むとき、<c>FDataDrivenShaderPlatformInfo</c> の問い合わせに答えるのに使う。
    /// </param>
    /// <param name="consoleVariableOverrides">
    /// ini に書かれたコンソール変数の値 (<see cref="UnrealConsoleVariables.ReadOverrides"/>)。C++ に書いた既定値より優先する。
    /// </param>
    /// <param name="shaderFileExists">
    /// シェーダーのファイルの仮想パスを、解析で解決できるかを判定する手続き。解決できない頂点ファクトリ (割り当てていないプラグインのもの) は軸に入れない。
    /// <see langword="null"/> なら判定しない。
    /// </param>
    /// <returns>書き出した結果。</returns>
    public static UnrealUniformBufferGeneration Generate(
        IEnumerable<string> sourceRoots,
        string outputDirectory,
        ImmutableDictionary<string, string>? platformSettings = null,
        ImmutableDictionary<string, string>? consoleVariableOverrides = null,
        Func<string, bool>? shaderFileExists = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        ParsedSources parsed = Parse(sourceRoots);
        parsed.Classes.PlatformSettings = platformSettings;
        parsed.Classes.ConsoleVariableOverrides = consoleVariableOverrides;
        parsed.ShaderFileExists = shaderFileExists;
        return Write(parsed, outputDirectory);
    }

    /// <summary>C++ のテキストから、構造体の定義と uniform buffer の登録を読む (テスト用)。</summary>
    /// <param name="files">ファイルのパスと中身。</param>
    /// <param name="outputDirectory">書き出す先。</param>
    /// <param name="platformSettings">解析するシェーダープラットフォームの設定。</param>
    /// <param name="consoleVariableOverrides">ini に書かれたコンソール変数の値。</param>
    /// <returns>書き出した結果。</returns>
    internal static UnrealUniformBufferGeneration GenerateFromTexts(
        IEnumerable<(string Path, string Text)> files,
        string outputDirectory,
        ImmutableDictionary<string, string>? platformSettings = null,
        ImmutableDictionary<string, string>? consoleVariableOverrides = null)
    {
        ParsedSources parsed = new();
        parsed.Classes.PlatformSettings = platformSettings;
        parsed.Classes.ConsoleVariableOverrides = consoleVariableOverrides;

        foreach ((string path, string text) in files)
        {
            ParseFile(path, text, parsed, keepText: true);
        }

        return Write(parsed, outputDirectory);
    }

    // ------------------------------------------------------------------
    // 読み取り
    // ------------------------------------------------------------------

    /// <summary>読み取った構造体と登録。</summary>
    private sealed class ParsedSources
    {
        /// <summary>構造体の名前から定義 (同じ名前が別のファイルにあることがある)。</summary>
        public ConcurrentDictionary<string, ConcurrentBag<StructDefinition>> Structs { get; } = new(StringComparer.Ordinal);

        /// <summary>uniform buffer の名前から構造体の名前と、登録したファイル。</summary>
        public ConcurrentDictionary<string, (string Struct, string File)> UniformBuffers { get; } = new(StringComparer.Ordinal);

        /// <summary>Scene の uniform buffer のメンバーの名前から構造体の名前と、登録したファイル (<c>IMPLEMENT_SCENE_UB_STRUCT</c>)。</summary>
        public ConcurrentDictionary<string, (string Struct, string File)> SceneMembers { get; } = new(StringComparer.Ordinal);

        /// <summary>エンジンの C++ がシェーダーに定義するマクロの名前。</summary>
        public ConcurrentDictionary<string, byte> EngineDefines { get; } = new(StringComparer.Ordinal);

        /// <summary>シェーダーの登録 (<c>IMPLEMENT_GLOBAL_SHADER</c> など)。</summary>
        public ConcurrentBag<UnrealShaderRegistration> ShaderRegistrations { get; } = [];

        /// <summary>読めなかったシェーダーの登録のマクロの名前と数。</summary>
        public ConcurrentDictionary<string, int> UnreadShaderRegistrations { get; } = new(StringComparer.Ordinal);

        /// <summary>シェーダーのクラス (定義するマクロとパーミュテーション) を読むために集めたもの。</summary>
        public UnrealShaderClasses.Collector Classes { get; } = new();

        /// <summary>登録を包んだ独自のマクロの定義と呼び出し。すべてのファイルを読んでから展開する。</summary>
        public UnrealShaderRegistrations.MacroRegistrations RegistrationMacros { get; } = new();

        /// <summary>
        /// メンバーを 1 つずつマクロとして定義する列挙の名前 (<c>SetDefine(DebugViewShaderModeToString(static_cast&lt;EDebugViewShaderMode&gt;(i)), i)</c>)。
        /// </summary>
        public ConcurrentDictionary<string, byte> EnumsDefinedAsMacros { get; } = new(StringComparer.Ordinal);

        /// <summary>列挙の名前からメンバーの名前 (宣言の順)。<see cref="EnumsDefinedAsMacros"/> のメンバーを引く。</summary>
        public ConcurrentDictionary<string, ImmutableArray<string>> EnumMembers { get; } = new(StringComparer.Ordinal);

        /// <summary>頂点ファクトリの名前から登録 (<c>IMPLEMENT_VERTEX_FACTORY_TYPE</c>)。</summary>
        public ConcurrentDictionary<string, UnrealVertexFactory> VertexFactories { get; } = new(StringComparer.Ordinal);

        /// <summary>シェーダーのファイルを解析で解決できるかを判定する手続き (<see cref="Generate"/>)。</summary>
        public Func<string, bool>? ShaderFileExists { get; set; }

        /// <summary>読んだファイルの数。</summary>
        public int FileCount;
    }

    /// <summary>C++ の構造体 1 つ。</summary>
    /// <param name="Name">構造体の名前。</param>
    /// <param name="File">書いたファイル。</param>
    /// <param name="Members">メンバー。</param>
    /// <param name="UnknownMacros">メンバーとして読めなかったマクロ (別のファイルで定義されたもの)。</param>
    private sealed record StructDefinition(string Name, string File, ImmutableArray<MemberDefinition> Members, ImmutableArray<string> UnknownMacros);

    /// <summary>構造体のメンバー 1 つ (C++ のマクロ 1 つ)。</summary>
    /// <param name="Kind">種類。</param>
    /// <param name="Type">C++ の型 (定数) か、HLSL の型 (資源)、構造体の名前 (入れ子・取り込み)。</param>
    /// <param name="Name">メンバーの名前。</param>
    /// <param name="ArraySize">配列の要素数。配列でなければ 0。</param>
    private sealed record MemberDefinition(MemberKind Kind, string Type, string Name, int ArraySize);

    /// <summary>メンバーの種類。</summary>
    private enum MemberKind
    {
        /// <summary>定数バッファに入る値 (<c>SHADER_PARAMETER</c>)。</summary>
        Constant,

        /// <summary>4 つずつ詰めた値の配列 (<c>SHADER_PARAMETER_SCALAR_ARRAY</c>)。</summary>
        PackedScalarArray,

        /// <summary>テクスチャ・バッファ・サンプラー。</summary>
        Resource,

        /// <summary>入れ子の構造体 (<c>SHADER_PARAMETER_STRUCT</c>)。</summary>
        Nested,

        /// <summary>メンバーを平らに取り込む構造体 (<c>SHADER_PARAMETER_STRUCT_INCLUDE</c>)。</summary>
        Included,
    }

    /// <summary>読む C++ のファイルか (<c>.h</c> / <c>.cpp</c> / <c>.inl</c>)。</summary>
    /// <param name="path">パス。</param>
    /// <returns>読むなら <see langword="true"/>。</returns>
    internal static bool IsSourceFile(string path)
        => path.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".inl", StringComparison.OrdinalIgnoreCase);

    private static ParsedSources Parse(IEnumerable<string> sourceRoots)
    {
        ParsedSources parsed = new();
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

        IEnumerable<string> files = sourceRoots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", options))
            .Where(IsSourceFile);

        Parallel.ForEach(files, path =>
        {
            string text;

            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            if (!text.Contains("SHADER_PARAMETER_STRUCT", StringComparison.Ordinal)
                && !text.Contains("UNIFORM_BUFFER_STRUCT", StringComparison.Ordinal)
                && !text.Contains("SCENE_UB_STRUCT", StringComparison.Ordinal)
                && !text.Contains("SetDefine", StringComparison.Ordinal)
                && !text.Contains("SET_SHADER_DEFINE", StringComparison.Ordinal)
                && !text.Contains("SHADER_PERMUTATION_", StringComparison.Ordinal)
                && !text.Contains("IMPLEMENT_", StringComparison.Ordinal)
                && !text.Contains("ModifyCompilationEnvironment", StringComparison.Ordinal)
                && !text.Contains("PermutationDomain", StringComparison.Ordinal)
                && !text.Contains("enum class", StringComparison.Ordinal)
                && !text.Contains("FStaticShaderPlatform", StringComparison.Ordinal)
                && !IsSharedShaderHeader(path))
            {
                // ほかのファイルの列挙は、メンバーを 1 つずつマクロにするものがあるので、宣言だけを読む (DebugViewModeHelpers.h)。
                if (text.Contains("enum ", StringComparison.Ordinal))
                {
                    CollectEnums(WithoutCommentsAndDefines(text), parsed);
                }

                return;
            }

            Interlocked.Increment(ref parsed.FileCount);
            ParseFile(path, text, parsed, keepText: false);
        });

        return parsed;
    }

    /// <summary>列挙の宣言を読み、メンバーの名前を覚える (<see cref="ParsedSources.EnumMembers"/>)。</summary>
    /// <param name="code">コメントとマクロの定義を除いたコード。</param>
    /// <param name="parsed">覚える先。</param>
    private static void CollectEnums(string code, ParsedSources parsed)
    {
        foreach (Match match in EnumDeclaration().Matches(code))
        {
            parsed.EnumMembers.TryAdd(
                match.Groups["name"].Value,
                [
                    .. match.Groups["body"].Value.Split(',')
                        .Select(member => member.Split('=')[0].Trim())
                        .Where(member => member.Length > 0 && (char.IsLetter(member[0]) || member[0] == '_')),
                ]);
        }
    }

    /// <summary>C++ とシェーダーが共有するヘッダ (<c>Shaders/Shared</c> の下) かを判定する。</summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>共有するヘッダなら <see langword="true"/>。整数の <c>#define</c> を、クラスの値を読むのに使う。</returns>
    private static bool IsSharedShaderHeader(string path)
        => path.Replace('\\', '/').Contains("/Shaders/Shared/", StringComparison.OrdinalIgnoreCase);

    private static void ParseFile(string path, string text, ParsedSources parsed, bool keepText)
    {
        parsed.Classes.Add(path, text, keepText);

        // 登録はコメントとマクロの定義の外だけを読む (マクロの説明の例を拾わない)。
        string code = WithoutCommentsAndDefines(text);

        foreach (Match match in Registration().Matches(code))
        {
            parsed.UniformBuffers.TryAdd(match.Groups["name"].Value, (match.Groups["struct"].Value, path));
        }

        foreach (Match match in SceneMember().Matches(code))
        {
            parsed.SceneMembers.TryAdd(match.Groups["field"].Value, (match.Groups["struct"].Value, path));
        }

        UnrealShaderRegistrations.Collect(path, code, parsed.ShaderRegistrations, parsed.UnreadShaderRegistrations);
        UnrealVertexFactories.Collect(text, code, parsed.VertexFactories);
        UnrealShaderRegistrations.CollectMacros(path, text, code, parsed.RegistrationMacros);

        foreach (Match match in EngineDefine().Matches(code))
        {
            parsed.EngineDefines.TryAdd(match.Groups["name"].Value, 0);
        }

        // 列挙のメンバーを 1 つずつマクロにするループ。名前は列挙を引いてから足す (EnumMacroNames)。
        foreach (Match match in EnumDefine().Matches(code))
        {
            parsed.EnumsDefinedAsMacros.TryAdd(match.Groups["enum"].Value, 0);
        }

        if (code.Contains("enum", StringComparison.Ordinal))
        {
            CollectEnums(code, parsed);
        }

        if (!StructBegin().IsMatch(text))
        {
            return;
        }

        ImmutableArray<ShaderSyntaxToken> tokens = Expand(path, text);

        for (int i = 0; i < tokens.Length; i++)
        {
            if (!StructBeginName().IsMatch(tokens[i].Text)
                || i + 2 >= tokens.Length
                || tokens[i + 1].Kind != ShaderSyntaxKind.OpenParenToken
                || tokens[i + 2].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            string name = tokens[i + 2].Text;
            int bodyStart = SkipParenthesized(tokens, i + 1);
            int bodyEnd = bodyStart;

            while (bodyEnd < tokens.Length && !StructEndName().IsMatch(tokens[bodyEnd].Text))
            {
                bodyEnd++;
            }

            (ImmutableArray<MemberDefinition> members, ImmutableArray<string> unknown) = ParseMembers(tokens, bodyStart, bodyEnd);
            parsed.Structs.GetOrAdd(name, _ => []).Add(new StructDefinition(name, path, members, unknown));
            i = bodyEnd;
        }
    }

    /// <summary>
    /// ファイルの中で定義されたマクロを展開する。<c>#if</c> はすべての分岐を読み、<c>#include</c> は読まない。
    /// </summary>
    internal static ImmutableArray<ShaderSyntaxToken> Expand(string path, string text)
    {
        StringBuilder kept = new(text.Length);

        foreach (string line in LogicalLines(text))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith('#'))
            {
                string directive = trimmed[1..].TrimStart();

                // 定義と取り消しだけを残す。条件はすべての分岐を読むために消し、取り込みは追わない。
                if (!directive.StartsWith("define", StringComparison.Ordinal)
                    && !directive.StartsWith("undef", StringComparison.Ordinal))
                {
                    kept.Append('\n');
                    continue;
                }
            }

            kept.Append(line).Append('\n');
        }

        PreprocessorOptions options = new() { HonorPragmaOnce = false };
        return new ShaderPreprocessor(options).Preprocess(SourceText.From(kept.ToString(), path)).Tokens;
    }

    /// <summary>コメントと、<c>#</c> で始まる行 (マクロの定義など) を消したテキスト。</summary>
    private static string WithoutCommentsAndDefines(string text)
    {
        StringBuilder kept = new(text.Length);

        foreach (string line in LogicalLines(BlockComment().Replace(text, " ")))
        {
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            int comment = line.IndexOf("//", StringComparison.Ordinal);
            kept.Append(comment >= 0 ? line[..comment] : line).Append('\n');
        }

        return kept.ToString();
    }

    /// <summary>行の継続 (<c>\</c> と改行) を、改行を空白に置き換えてつなげた行を返す。</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        StringBuilder current = new();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.EndsWith('\\'))
            {
                current.Append(line, 0, line.Length - 1).Append(' ');
                continue;
            }

            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static (ImmutableArray<MemberDefinition> Members, ImmutableArray<string> Unknown) ParseMembers(
        ImmutableArray<ShaderSyntaxToken> tokens, int start, int end)
    {
        ImmutableArray<MemberDefinition>.Builder members = ImmutableArray.CreateBuilder<MemberDefinition>();
        ImmutableArray<string>.Builder unknown = ImmutableArray.CreateBuilder<string>();

        int i = start;

        while (i < end)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind != ShaderSyntaxKind.IdentifierToken || i + 1 >= end || tokens[i + 1].Kind != ShaderSyntaxKind.OpenParenToken)
            {
                i++;
                continue;
            }

            int after = SkipParenthesized(tokens, i + 1);
            List<string> arguments = SplitArguments(tokens, i + 2, after - 1);
            i = after;

            if (ToMember(token.Text, arguments) is { } member)
            {
                members.Add(member);
            }
            else if (!IgnoredMacros.Contains(token.Text))
            {
                unknown.Add(token.Text);
            }
        }

        return (members.ToImmutable(), unknown.ToImmutable());
    }

    /// <summary>メンバーにならないマクロ (RDG のアクセスの印、ほかの uniform buffer への参照)。</summary>
    private static readonly HashSet<string> IgnoredMacros = new(StringComparer.Ordinal)
    {
        "RDG_BUFFER_ACCESS", "RDG_BUFFER_ACCESS_DYNAMIC", "RDG_BUFFER_ACCESS_ARRAY",
        "RDG_TEXTURE_ACCESS", "RDG_TEXTURE_ACCESS_DYNAMIC", "RDG_TEXTURE_ACCESS_ARRAY",
        "SHADER_PARAMETER_RDG_BUFFER_UPLOAD", "SHADER_PARAMETER_STRUCT_REF", "SHADER_PARAMETER_RDG_UNIFORM_BUFFER",
        "SHADER_PARAMETER_STRUCT_ARRAY", "RENDER_TARGET_BINDING_SLOTS",
    };

    private static MemberDefinition? ToMember(string macro, List<string> arguments)
    {
        static int Size(string declaration) => EvaluateArraySize(declaration);

        return macro switch
        {
            "SHADER_PARAMETER" or "SHADER_PARAMETER_EX" when arguments.Count >= 2
                => new MemberDefinition(MemberKind.Constant, arguments[0], arguments[1], 0),
            "SHADER_PARAMETER_ARRAY" or "SHADER_PARAMETER_ARRAY_EX" when arguments.Count >= 3
                => new MemberDefinition(MemberKind.Constant, arguments[0], arguments[1], Size(arguments[2])),
            "SHADER_PARAMETER_SCALAR_ARRAY" when arguments.Count >= 3
                => new MemberDefinition(MemberKind.PackedScalarArray, arguments[0], arguments[1], (Size(arguments[2]) + 3) / 4),
            "SHADER_PARAMETER_STRUCT" when arguments.Count >= 2
                => new MemberDefinition(MemberKind.Nested, arguments[0], arguments[1], 0),
            "SHADER_PARAMETER_STRUCT_INCLUDE" when arguments.Count >= 2
                => new MemberDefinition(MemberKind.Included, arguments[0], arguments[1], 0),
            _ when ResourceMacro().IsMatch(macro) && arguments.Count >= 2
                => new MemberDefinition(
                    MemberKind.Resource,
                    arguments[0],
                    arguments[1],
                    macro.EndsWith("_ARRAY", StringComparison.Ordinal) && arguments.Count >= 3 ? Size(arguments[2]) : 0),
            _ => null,
        };
    }

    /// <summary><c>[4]</c> や <c>[2 * 3]</c> を数にする。読めなければ <see cref="UnknownArraySize"/>。</summary>
    private static int EvaluateArraySize(string declaration)
    {
        string inner = declaration.Trim().TrimStart('[').TrimEnd(']');
        int position = 0;

        try
        {
            long value = Sum();
            return position == inner.Length && value is > 0 and < int.MaxValue ? (int)value : UnknownArraySize;
        }
        catch (FormatException)
        {
            return UnknownArraySize;
        }
        catch (DivideByZeroException)
        {
            return UnknownArraySize;
        }

        // 整数と + - * / と括弧だけを読む。名前 (C++ の定数) があれば読めないものとする。
        long Sum()
        {
            long value = Product();

            while (Peek() is '+' or '-')
            {
                char op = inner[position++];
                long right = Product();
                value = op == '+' ? value + right : value - right;
            }

            return value;
        }

        long Product()
        {
            long value = Atom();

            while (Peek() is '*' or '/')
            {
                char op = inner[position++];
                long right = Atom();
                value = op == '*' ? value * right : value / right;
            }

            return value;
        }

        long Atom()
        {
            if (Peek() == '(')
            {
                position++;
                long value = Sum();

                if (Peek() != ')')
                {
                    throw new FormatException();
                }

                position++;
                return value;
            }

            int start = position;

            while (position < inner.Length && char.IsAsciiDigit(inner[position]))
            {
                position++;
            }

            if (position == start)
            {
                throw new FormatException();
            }

            long number = long.Parse(inner.AsSpan(start, position - start), CultureInfo.InvariantCulture);

            // 符号なしの印 (4u) は読み飛ばす。
            while (position < inner.Length && inner[position] is 'u' or 'U')
            {
                position++;
            }

            return number;
        }

        char Peek()
        {
            while (position < inner.Length && char.IsWhiteSpace(inner[position]))
            {
                position++;
            }

            return position < inner.Length ? inner[position] : '\0';
        }
    }

    /// <summary>開き括弧の位置から、対応する閉じ括弧の次の位置を返す。</summary>
    private static int SkipParenthesized(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseParenToken && --depth == 0)
            {
                return i + 1;
            }
        }

        return tokens.Length;
    }

    /// <summary>括弧の中を、最上位のカンマで分ける。<c>&lt;&gt;</c> の中のカンマでは分けない。</summary>
    private static List<string> SplitArguments(ImmutableArray<ShaderSyntaxToken> tokens, int start, int end)
    {
        List<string> arguments = [];
        StringBuilder current = new();
        int parens = 0;
        int angles = 0;
        ShaderSyntaxToken? previous = null;

        for (int i = start; i < end; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.CommaToken && parens == 0 && angles == 0)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                previous = null;
                continue;
            }

            parens += token.Kind == ShaderSyntaxKind.OpenParenToken ? 1 : token.Kind == ShaderSyntaxKind.CloseParenToken ? -1 : 0;
            angles += token.Text.Count(c => c == '<') - token.Text.Count(c => c == '>');
            angles = Math.Max(0, angles);

            // 識別子や数が続くときだけ空白を入れる (unsigned int、Texture2D<float4>)。
            if (previous is not null && IsWord(previous) && IsWord(token))
            {
                current.Append(' ');
            }

            current.Append(token.Text);
            previous = token;
        }

        if (current.Length > 0 || arguments.Count > 0)
        {
            arguments.Add(current.ToString().Trim());
        }

        return arguments;

        static bool IsWord(ShaderSyntaxToken token)
            => token.Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken;
    }

    // ------------------------------------------------------------------
    // 書き出し
    // ------------------------------------------------------------------

    /// <summary>C++ の型から、定数バッファに置く HLSL の型 (UE 5.5 の <c>TShaderParameterTypeInfo</c>)。</summary>
    private static readonly Dictionary<string, string> ConstantTypes = new(StringComparer.Ordinal)
    {
        ["float"] = "float",
        ["int32"] = "int",
        ["int"] = "int",
        ["uint32"] = "uint",
        ["uint"] = "uint",
        ["bool"] = "uint",
        ["FVector2f"] = "float2",
        ["FVector3f"] = "float3",
        ["FVector4f"] = "float4",
        ["FLinearColor"] = "float4",
        ["FQuat4f"] = "float4",
        ["FIntPoint"] = "int2",
        ["FIntVector2"] = "int2",
        ["FUintVector2"] = "uint2",
        ["FIntVector"] = "int3",
        ["FIntVector3"] = "int3",
        ["FUintVector3"] = "uint3",
        ["FIntVector4"] = "int4",
        ["FUintVector4"] = "uint4",
        ["FIntRect"] = "int4",
        ["FMatrix44f"] = "float4x4",
        ["FMatrix3x4"] = "float3x4",
        ["unsigned"] = "uint",
        ["unsigned int"] = "uint",
    };

    /// <summary>C++ の型を、定数バッファに置く HLSL の型にする。</summary>
    /// <param name="type">C++ の型。</param>
    /// <returns>HLSL の型。知らない型なら <see langword="null"/>。</returns>
    /// <remarks>
    /// InstancedView のメンバーは <c>TShaderParameterTypeInfo&lt;T&gt;::TInstancedType</c> の配列で書かれる
    /// (ビューごとの値を 4 成分のベクトルに入れる)。UE 5.5 の定義に合わせて、行列はそのまま、整数は int4 / uint4、ほかは float4 にする。
    /// </remarks>
    private static string? MapConstantType(string type)
    {
        if (ConstantTypes.TryGetValue(type, out string? mapped))
        {
            return mapped;
        }

        if (InstancedType().Match(type) is { Success: true } instanced)
        {
            return instanced.Groups["type"].Value switch
            {
                "FMatrix44f" => "float4x4",
                "int32" or "FIntPoint" or "FIntVector" or "FIntVector4" or "FIntRect" => "int4",
                "uint32" or "bool" or "FUintVector2" or "FUintVector3" or "FUintVector4" => "uint4",
                _ => "float4",
            };
        }

        return null;
    }

    /// <summary>詰めた配列の要素の型 (<c>TShaderParameterScalarArrayTypeInfo</c>)。</summary>
    private static string PackedType(string scalar) => scalar switch
    {
        "int32" or "int" => "int4",
        "uint32" or "uint" => "uint4",
        _ => "float4",
    };

    /// <summary>平らにしたメンバー 1 つ。</summary>
    /// <param name="Path">構造体の中の道筋 (入れ子の名前の並び)。</param>
    /// <param name="IsResource">資源か。</param>
    /// <param name="Type">HLSL の型。</param>
    /// <param name="ArraySize">配列の要素数。配列でなければ 0。</param>
    private sealed record FlatMember(ImmutableArray<string> Path, bool IsResource, string Type, int ArraySize);

    private static UnrealUniformBufferGeneration Write(ParsedSources parsed, string outputDirectory)
    {
        AddSceneUniformBuffer(parsed);
        UnrealShaderRegistrations.ExpandMacros(parsed.RegistrationMacros, parsed.ShaderRegistrations, parsed.UnreadShaderRegistrations);

        string uniformBuffers = Path.Combine(outputDirectory, "UniformBuffers");
        Directory.CreateDirectory(uniformBuffers);

        SortedSet<string> unknownTypes = new(StringComparer.Ordinal);
        SortedSet<string> incomplete = new(StringComparer.Ordinal);
        List<string> written = [];
        StringBuilder all = new();
        all.Append("// Shaderlyn が C++ から作った。すべての uniform buffer を取り込む (エンジンは、シェーダーが参照するものだけを取り込む)。\n");

        foreach ((string name, (string structName, string registeredIn)) in parsed.UniformBuffers.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (Resolve(parsed, structName, registeredIn) is not { } definition)
            {
                incomplete.Add($"{name} ({structName} の定義が見つからない)");
                continue;
            }

            StringBuilder declaration = new();
            string rootType = "FShaderlynUniformBuffer_" + name;
            List<string> reasons = [];

            List<FlatMember> constants = [];
            List<FlatMember> resources = [];
            StringBuilder structs = new();

            EmitStruct(parsed, definition, rootType, [], constants, resources, structs, unknownTypes, reasons, depth: 0);

            if (reasons.Count > 0)
            {
                incomplete.Add($"{name} ({string.Join(", ", reasons.Distinct(StringComparer.Ordinal))})");
            }

            declaration.Append(CultureInfo.InvariantCulture, $"// Shaderlyn が C++ ({Path.GetFileName(definition.File)} の {structName}) から作った。\n");
            declaration.Append("#pragma once\n");

            if (constants.Count > 0)
            {
                declaration.Append(CultureInfo.InvariantCulture, $"cbuffer {name}\n{{\n");

                foreach (FlatMember member in constants)
                {
                    declaration.Append(CultureInfo.InvariantCulture, $"\t{member.Type} {name}_{string.Join('_', member.Path)}{Dimension(member)};\n");
                }

                declaration.Append("}\n");
            }

            foreach (FlatMember member in resources)
            {
                declaration.Append(CultureInfo.InvariantCulture, $"{member.Type} {name}_{string.Join('_', member.Path)}{Dimension(member)};\n");
            }

            declaration.Append(structs);
            declaration.Append(CultureInfo.InvariantCulture, $"static const {rootType} {name};\n");

            string file = Path.Combine(uniformBuffers, name + ".ush");
            File.WriteAllText(file, declaration.ToString());
            written.Add(file);
            all.Append(CultureInfo.InvariantCulture, $"#include \"/Engine/Generated/UniformBuffers/{name}.ush\"\n");
        }

        string generatedUniformBuffers = Path.Combine(outputDirectory, "GeneratedUniformBuffers.ush");
        File.WriteAllText(generatedUniformBuffers, all.ToString());
        written.Add(generatedUniformBuffers);

        if (WriteInstancedStereo(parsed, outputDirectory, unknownTypes) is { } stereo)
        {
            written.Add(stereo);
        }

        foreach (string name in EnumMacroNames(parsed))
        {
            parsed.EngineDefines.TryAdd(name, 0);
        }

        string defines = Path.Combine(outputDirectory, EngineDefinesFileName);
        File.WriteAllText(defines, string.Concat(parsed.EngineDefines.Keys.Order(StringComparer.Ordinal).Select(name => name + "\n")));
        written.Add(defines);
        written.Add(UnrealShaderRegistrations.Write(outputDirectory, parsed.ShaderRegistrations));

        // メッシュに描くマテリアルのシェーダーは、頂点ファクトリを軸にする (UnrealVertexFactories)。
        ImmutableArray<UnrealVertexFactory> vertexFactories = UnrealVertexFactories.Order(
            parsed.VertexFactories.Values.Where(f => parsed.ShaderFileExists?.Invoke(f.ShaderFile) != false));
        parsed.Classes.VertexFactories = vertexFactories;

        // 頂点ファクトリの側の ShouldCompilePermutation は、シェーダーのステージで答える (Parameters.ShaderType->GetFrequency())。
        parsed.Classes.ClassFrequencies = parsed.ShaderRegistrations
            .GroupBy(r => UnrealShaderClasses.BareClassName(r.ShaderClass), StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.Select(r => r.Frequency).ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        ImmutableArray<UnrealShaderClass> classes = parsed.Classes.Resolve(parsed.ShaderRegistrations.Select(r => r.ShaderClass), out int missingClasses);
        written.Add(UnrealShaderClasses.Write(outputDirectory, classes, parsed.Classes.GlobalDefines));

        // 頂点ファクトリの定義するマクロは、クラスを読んでから求まる (ModifyCompilationEnvironment を実行する)。
        written.AddRange(UnrealVertexFactories.Write(outputDirectory, vertexFactories, parsed.Classes.VertexFactoryDefines));

        return new UnrealUniformBufferGeneration(
            parsed.FileCount,
            parsed.UniformBuffers.Count,
            parsed.EngineDefines.Count,
            parsed.ShaderRegistrations.Distinct().Count(),
            [.. written],
            [.. incomplete],
            [.. unknownTypes],
            parsed.UnreadShaderRegistrations.ToImmutableSortedDictionary(StringComparer.Ordinal),
            classes.Length,
            missingClasses);
    }

    /// <summary>
    /// Scene の uniform buffer を、<c>IMPLEMENT_SCENE_UB_STRUCT</c> で登録したメンバーから組み立てる (UE 5.5 の <c>SceneUniformBuffer.cpp</c>)。
    /// </summary>
    /// <remarks>
    /// Scene は C++ の構造体を持たない。描画の機能が、それぞれの構造体を名前付きのメンバーとして登録し、
    /// エンジンが名前の順に並べた入れ子の構造体を作る (<c>Scene.GPUScene.GPUSceneFrameNumber</c>)。
    /// </remarks>
    private static void AddSceneUniformBuffer(ParsedSources parsed)
    {
        if (parsed.SceneMembers.IsEmpty || parsed.UniformBuffers.ContainsKey(SceneUniformBufferName))
        {
            return;
        }

        const string structName = "FShaderlynSceneUniformParameters";
        string file = parsed.SceneMembers.Values.Select(member => member.File).Order(StringComparer.Ordinal).First();
        ImmutableArray<MemberDefinition> members =
        [
            .. parsed.SceneMembers
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new MemberDefinition(MemberKind.Nested, pair.Value.Struct, pair.Key, 0)),
        ];

        parsed.Structs.GetOrAdd(structName, _ => []).Add(new StructDefinition(structName, file, members, []));
        parsed.UniformBuffers.TryAdd(SceneUniformBufferName, (structName, file));
    }

    private static string Dimension(FlatMember member) => member.ArraySize > 0 ? $"[{member.ArraySize}]" : string.Empty;

    /// <summary>構造体の名前から定義を引く。同じ名前が複数あれば、登録したファイルと同じファイルのものを使う。</summary>
    private static StructDefinition? Resolve(ParsedSources parsed, string structName, string? preferredFile)
    {
        // 名前空間付きの参照 (LightFunctionAtlas::FLightFunctionAtlasGlobalParameters) は、名前空間を外して引く。
        // 構造体の定義は BEGIN_… の引数の名前で覚えており、囲む namespace は覚えていない。
        int qualifier = structName.LastIndexOf("::", StringComparison.Ordinal);

        if (qualifier >= 0)
        {
            structName = structName[(qualifier + 2)..];
        }

        if (!parsed.Structs.TryGetValue(structName, out ConcurrentBag<StructDefinition>? definitions) || definitions.IsEmpty)
        {
            return null;
        }

        StructDefinition[] all = [.. definitions];

        return all.Length == 1
            ? all[0]
            : all.FirstOrDefault(d => string.Equals(d.File, preferredFile, StringComparison.OrdinalIgnoreCase))
              ?? all.FirstOrDefault(d => preferredFile is not null
                                         && string.Equals(Path.GetFileNameWithoutExtension(d.File), Path.GetFileNameWithoutExtension(preferredFile), StringComparison.OrdinalIgnoreCase))
              ?? all.OrderBy(d => d.File, StringComparer.Ordinal).First();
    }

    /// <summary>構造体の型を書き、メンバーを平らにして集める。</summary>
    private static void EmitStruct(
        ParsedSources parsed,
        StructDefinition definition,
        string typeName,
        ImmutableArray<string> path,
        List<FlatMember> constants,
        List<FlatMember> resources,
        StringBuilder structs,
        SortedSet<string> unknownTypes,
        List<string> reasons,
        int depth)
    {
        if (depth > 16)
        {
            reasons.Add("入れ子が深すぎる");
            return;
        }

        NoteUnknownMacros(definition, reasons);

        StringBuilder fields = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        CollectFields(parsed, definition, typeName, path, constants, resources, structs, fields, seen, unknownTypes, reasons, depth);

        structs.Append(CultureInfo.InvariantCulture, $"struct {typeName}\n{{\n{fields}}};\n");
    }

    private static void NoteUnknownMacros(StructDefinition definition, List<string> reasons)
    {
        if (!definition.UnknownMacros.IsEmpty)
        {
            reasons.Add($"{definition.Name} の {string.Join(" / ", definition.UnknownMacros.Distinct(StringComparer.Ordinal))} を読めない");
        }
    }

    private static void CollectFields(
        ParsedSources parsed,
        StructDefinition definition,
        string typeName,
        ImmutableArray<string> path,
        List<FlatMember> constants,
        List<FlatMember> resources,
        StringBuilder structs,
        StringBuilder fields,
        HashSet<string> seen,
        SortedSet<string> unknownTypes,
        List<string> reasons,
        int depth)
    {
        foreach (MemberDefinition member in definition.Members)
        {
            switch (member.Kind)
            {
                case MemberKind.Nested:
                case MemberKind.Included:
                    if (Resolve(parsed, member.Type, definition.File) is not { } inner)
                    {
                        reasons.Add($"{member.Type} の定義が見つからない");
                        continue;
                    }

                    if (member.Kind == MemberKind.Included)
                    {
                        // 取り込んだ構造体のメンバーは、同じ階層に並ぶ。
                        NoteUnknownMacros(inner, reasons);
                        CollectFields(parsed, inner, typeName, path, constants, resources, structs, fields, seen, unknownTypes, reasons, depth + 1);
                        continue;
                    }

                    if (!seen.Add(member.Name))
                    {
                        continue;
                    }

                    string nestedType = typeName + "_" + member.Name;
                    EmitStruct(parsed, inner, nestedType, path.Add(member.Name), constants, resources, structs, unknownTypes, reasons, depth + 1);
                    fields.Append(CultureInfo.InvariantCulture, $"\t{nestedType} {member.Name};\n");
                    break;

                case MemberKind.Resource:
                    if (!seen.Add(member.Name))
                    {
                        continue;
                    }

                    resources.Add(new FlatMember(path.Add(member.Name), true, member.Type, member.ArraySize));
                    fields.Append(CultureInfo.InvariantCulture, $"\t{member.Type} {member.Name}{(member.ArraySize > 0 ? $"[{member.ArraySize}]" : string.Empty)};\n");
                    break;

                default:
                    if (!seen.Add(member.Name))
                    {
                        continue;
                    }

                    string type;

                    if (member.Kind == MemberKind.PackedScalarArray)
                    {
                        type = PackedType(member.Type);
                    }
                    else if (MapConstantType(member.Type) is { } mapped)
                    {
                        type = mapped;
                    }
                    else
                    {
                        // 知らない型。名前は宣言しておき (参照を誤りにしない)、型は分からないものとして float4 にする。
                        unknownTypes.Add(member.Type);
                        type = "float4";
                    }

                    constants.Add(new FlatMember(path.Add(member.Name), false, type, member.ArraySize));
                    fields.Append(CultureInfo.InvariantCulture, $"\t{type} {member.Name}{(member.ArraySize > 0 ? $"[{member.ArraySize}]" : string.Empty)};\n");
                    break;
            }
        }
    }

    /// <summary>
    /// <c>GeneratedInstancedStereo.ush</c> を書く (UE 5.5 の <c>ShaderCompiler.cpp</c> の <c>GenerateInstancedStereoCode</c>)。
    /// </summary>
    /// <remarks>
    /// <c>ViewState</c> は、View の先頭から InstancedView のメンバーの数だけのメンバーと、倍精度の行列・位置を持つ。
    /// 関数の本体は、宣言の検査に要る形だけにする (View の値を写す)。
    /// </remarks>
    private static string? WriteInstancedStereo(ParsedSources parsed, string outputDirectory, SortedSet<string> unknownTypes)
    {
        if (!parsed.UniformBuffers.TryGetValue("View", out (string Struct, string File) view)
            || Resolve(parsed, view.Struct, view.File) is not { } viewDefinition)
        {
            return null;
        }

        int count = parsed.UniformBuffers.TryGetValue("InstancedView", out (string Struct, string File) instanced)
                    && Resolve(parsed, instanced.Struct, instanced.File) is { } instancedDefinition
            ? instancedDefinition.Members.Length
            : viewDefinition.Members.Length;

        StringBuilder text = new();
        text.Append("// Shaderlyn が C++ (View と InstancedView) から作った。\n");
        text.Append("#pragma once\n");
        text.Append("struct ViewStateTileOffsetData\n{\n");
        text.Append("\tFLWCVector3 WorldCameraOrigin;\n\tFLWCVector3 WorldViewOrigin;\n\tFLWCVector3 PrevWorldCameraOrigin;\n");
        text.Append("\tFLWCVector3 PrevWorldViewOrigin;\n\tFLWCVector3 PreViewTranslation;\n\tFLWCVector3 PrevPreViewTranslation;\n};\n");
        text.Append("struct ViewState\n{\n");

        List<string> names = [];

        foreach (MemberDefinition member in viewDefinition.Members.Take(count))
        {
            string? type = member.Kind switch
            {
                MemberKind.Constant => MapConstantType(member.Type) ?? Unknown(member.Type),
                MemberKind.PackedScalarArray => PackedType(member.Type),
                MemberKind.Resource => member.Type,
                _ => null,
            };

            if (type is null)
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"\t{type} {member.Name}{(member.ArraySize > 0 ? $"[{member.ArraySize}]" : string.Empty)};\n");
            names.Add(member.Name);
        }

        text.Append("\tFDFInverseMatrix WorldToClip;\n\tFDFMatrix ClipToWorld;\n\tFDFMatrix ScreenToWorld;\n\tFDFMatrix PrevClipToWorld;\n");
        text.Append("\tFDFVector3 WorldCameraOrigin;\n\tFDFVector3 WorldViewOrigin;\n\tFDFVector3 PrevWorldCameraOrigin;\n");
        text.Append("\tFDFVector3 PrevWorldViewOrigin;\n\tFDFVector3 PreViewTranslation;\n\tFDFVector3 PrevPreViewTranslation;\n");
        text.Append("\tViewStateTileOffsetData TileOffset;\n};\n");
        text.Append("\tvoid FinalizeViewState(inout ViewState InOutView);\n");
        text.Append("ViewState GetPrimaryView()\n{\n\tViewState Result;\n");

        foreach (string name in names)
        {
            text.Append(CultureInfo.InvariantCulture, $"\tResult.{name} = View.{name};\n");
        }

        text.Append("\tFinalizeViewState(Result);\n\treturn Result;\n}\n");
        text.Append("#if (INSTANCED_STEREO || MOBILE_MULTI_VIEW)\n");
        text.Append("ViewState GetInstancedView(uint ViewIndex)\n{\n\treturn GetPrimaryView();\n}\n");
        text.Append("#endif\n");

        string file = Path.Combine(outputDirectory, "GeneratedInstancedStereo.ush");
        File.WriteAllText(file, text.ToString());
        return file;

        string Unknown(string type)
        {
            unknownTypes.Add(type);
            return "float4";
        }
    }

    [GeneratedRegex("""IMPLEMENT_(?:GLOBAL_SHADER_PARAMETER_STRUCT|(?:STATIC_|STATIC_AND_SHADER_)?UNIFORM_BUFFER_STRUCT(?:_EX)?)\s*\(\s*(?<struct>\w+)\s*,\s*"(?<name>\w+)"|IMPLEMENT_(?:UNIFORM_BUFFER_ALIAS_STRUCT|GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT)\s*\(\s*(?<struct>\w+)\s*,\s*"?(?<name>\w+)"?""", RegexOptions.CultureInvariant)]
    private static partial Regex Registration();

    [GeneratedRegex("""IMPLEMENT_SCENE_UB_STRUCT\s*\(\s*(?<struct>[\w:]+)\s*,\s*(?<field>\w+)\s*,""", RegexOptions.CultureInvariant)]
    private static partial Regex SceneMember();

    /// <summary>
    /// エンジンの C++ がシェーダーに定義するマクロ。<c>SetDefine(TEXT("NAME"), …)</c>、<c>SET_SHADER_DEFINE(Environment, NAME, …)</c>、
    /// パーミュテーションの次元 (<c>SHADER_PERMUTATION_BOOL("NAME")</c> など)。
    /// </summary>
    [GeneratedRegex(""""\bSetDefine(?:IfUnset)?\s*\(\s*(?:TEXT\s*\(\s*)?"(?<name>[A-Za-z_]\w*)"|\bSET_SHADER_DEFINE\s*\([^,()]*,\s*(?<name>[A-Za-z_]\w*)\s*,|\bSHADER_PERMUTATION_\w+\s*\(\s*(?:TEXT\s*\(\s*)?"(?<name>[A-Za-z_]\w*)"""", RegexOptions.CultureInvariant)]
    private static partial Regex EngineDefine();

    /// <summary>列挙のメンバーを 1 つずつマクロにするループの <c>SetDefine</c> (<c>SetDefine(XToString(static_cast&lt;EX&gt;(i)), i)</c>)。</summary>
    [GeneratedRegex("""SetDefine\(\s*[A-Za-z_]\w*ToString\s*\(\s*static_cast\s*<\s*(?<enum>[A-Za-z_]\w*)\s*>""", RegexOptions.CultureInvariant)]
    private static partial Regex EnumDefine();

    /// <summary>列挙の宣言 (<c>enum EX { A, B = 2, }</c>、<c>enum class EX : uint8 { … }</c>)。</summary>
    [GeneratedRegex("""\benum\s+(?:class\s+)?(?<name>[A-Za-z_]\w*)\s*(?::\s*[A-Za-z_]\w*\s*)?\{(?<body>[^{}]*)\}""", RegexOptions.CultureInvariant)]
    private static partial Regex EnumDeclaration();

    /// <summary>
    /// メンバーを 1 つずつマクロにする列挙の、メンバーの名前。数を表す最後のメンバー (<c>DVSM_MAX</c>) は除く (ループはその手前まで)。
    /// </summary>
    /// <param name="parsed">読んだもの。</param>
    /// <returns>マクロの名前。</returns>
    /// <remarks>
    /// <c>DebugViewModeRendering.cpp</c> は <c>for (int i = 0; i &lt; DVSM_MAX; ++i) SetDefine(DebugViewShaderModeToString(…(i)), i)</c> で、
    /// <c>DVSM_None</c> などをすべて定義する。名前が式から作られるので、ふつうの <c>SetDefine(TEXT("…"))</c> としては読めない。
    /// </remarks>
    private static IEnumerable<string> EnumMacroNames(ParsedSources parsed)
    {
        foreach (string name in parsed.EnumsDefinedAsMacros.Keys)
        {
            if (!parsed.EnumMembers.TryGetValue(name, out ImmutableArray<string> members))
            {
                continue;
            }

            foreach (string member in members.Where(m => !m.EndsWith("_MAX", StringComparison.Ordinal) && m is not ("MAX" or "Max" or "Num" or "Count")))
            {
                yield return member;
            }
        }
    }

    [GeneratedRegex("""^TShaderParameterTypeInfo<(?<type>\w+)>::TInstancedType$""", RegexOptions.CultureInvariant)]
    private static partial Regex InstancedType();

    [GeneratedRegex("""/\*.*?\*/""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockComment();

    [GeneratedRegex("""BEGIN_(?:GLOBAL_)?(?:SHADER_PARAMETER|UNIFORM_BUFFER)_STRUCT""", RegexOptions.CultureInvariant)]
    private static partial Regex StructBegin();

    [GeneratedRegex("""^BEGIN_(?:GLOBAL_)?(?:SHADER_PARAMETER|UNIFORM_BUFFER)_STRUCT(?:_WITH_CONSTRUCTOR)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex StructBeginName();

    [GeneratedRegex("""^END_(?:GLOBAL_)?(?:SHADER_PARAMETER|UNIFORM_BUFFER)_STRUCT$""", RegexOptions.CultureInvariant)]
    private static partial Regex StructEndName();

    [GeneratedRegex("""^SHADER_PARAMETER_(?:TEXTURE|SRV|UAV|SAMPLER|RDG_TEXTURE|RDG_TEXTURE_SRV|RDG_TEXTURE_NON_PIXEL_SRV|RDG_TEXTURE_UAV|RDG_BUFFER_SRV|RDG_BUFFER_UAV|RDG_BUFFER)(?:_ARRAY)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceMacro();
}

/// <summary>
/// <see cref="UnrealUniformBufferGenerator.Generate"/> の結果。
/// </summary>
/// <param name="SourceFileCount">読んだ C++ のファイルの数 (構造体か登録を含むもの)。</param>
/// <param name="UniformBufferCount">見つけた uniform buffer の数。</param>
/// <param name="EngineDefineCount">見つけた、エンジンの C++ がシェーダーに定義するマクロの数。</param>
/// <param name="WrittenFiles">書き出したファイル。</param>
/// <param name="Incomplete">メンバーを読み切れなかった uniform buffer (定義が無い、別のファイルのマクロでメンバーを書いている)。</param>
/// <param name="UnknownTypes">知らない C++ の型 (float4 として書いた)。</param>
/// <param name="ShaderRegistrationCount">見つけたシェーダーの登録 (<c>IMPLEMENT_GLOBAL_SHADER</c> など) の数。</param>
/// <param name="UnreadShaderRegistrations">読めなかったシェーダーの登録のマクロの名前と数 (パスをマクロの定義の中に書いた独自のマクロなど)。</param>
/// <param name="ShaderClassCount">定義するマクロとパーミュテーションを読んだ、登録されたシェーダーのクラスの数。</param>
/// <param name="MissingShaderClassCount">登録されているが、宣言が見つからなかったクラスの数。</param>
public sealed record UnrealUniformBufferGeneration(
    int SourceFileCount,
    int UniformBufferCount,
    int EngineDefineCount,
    int ShaderRegistrationCount,
    ImmutableArray<string> WrittenFiles,
    ImmutableArray<string> Incomplete,
    ImmutableArray<string> UnknownTypes,
    ImmutableSortedDictionary<string, int> UnreadShaderRegistrations,
    int ShaderClassCount,
    int MissingShaderClassCount);

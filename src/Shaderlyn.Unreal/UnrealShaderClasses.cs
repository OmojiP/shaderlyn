using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Unreal;

/// <summary>
/// C++ のシェーダーのクラス 1 つが、シェーダーに定義するマクロとパーミュテーション。
/// </summary>
/// <param name="Name">クラスの名前。</param>
/// <param name="Defines">
/// <c>ModifyCompilationEnvironment</c> の <c>SetDefine</c> で定義するマクロ。値が決まらないもの (条件付き、式) は <see langword="null"/>。
/// </param>
/// <param name="Permutations">パーミュテーションの次元のマクロと、とりうる値。値が決まらないものは <see langword="null"/>。</param>
/// <param name="CompilerFlags">立てるコンパイラのフラグ (<c>CFLAG_InlineRayTracing</c> など)。条件付きのものは <see langword="null"/>。</param>
public sealed record UnrealShaderClass(
    string Name,
    ImmutableSortedDictionary<string, string?> Defines,
    ImmutableSortedDictionary<string, ImmutableArray<int>?> Permutations,
    ImmutableSortedDictionary<string, string?> CompilerFlags)
{
    /// <summary>
    /// パーミュテーションの値の組によって整数の値が変わる定義とフラグ (<c>SetDefine(TEXT("LIGHT_SOURCE_MODE"), Mode)</c>) と、とりうる値。
    /// 組で値が変わらないものは <see cref="Defines"/> と <see cref="CompilerFlags"/> にある。
    /// </summary>
    public ImmutableSortedDictionary<string, ImmutableArray<int>> Varying { get; init; } =
        ImmutableSortedDictionary<string, ImmutableArray<int>>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// コンパイルされる組では同時に現れない値の組 (<c>SHAPE_SHADOW==0</c> と <c>LIGHT_SOURCE_MODE==2</c> など)。
    /// <c>ShouldCompilePermutation</c> で除かれた組と、組から決まる定義の値の組み合わせから求める。
    /// </summary>
    public ImmutableArray<KeyValuePair<string, string>> Exclusions { get; init; } = [];

    /// <summary>
    /// どの値の組も <c>ShouldCompilePermutation</c> で除かれ、解析するプラットフォーム (PC の Direct3D 12、SM6) ではコンパイルされないか。
    /// 組ごとに読めて、どの組でも <c>false</c> と決まったときだけ <see langword="true"/> にする。
    /// </summary>
    public bool NeverCompiled { get; init; }
}

/// <summary>
/// C++ のシェーダーのクラスから、シェーダーに定義するマクロとパーミュテーションを読む。
/// </summary>
/// <remarks>
/// <para>
/// <b>エンジンは、クラスごとに違うマクロでシェーダーをコンパイルする。</b>
/// <c>ModifyCompilationEnvironment</c> の <c>SetDefine</c> と、パーミュテーションの次元 (<c>SHADER_PERMUTATION_BOOL("X")</c> など) である。
/// これを知らないと、そのクラスでは決して通らない分岐を読み、誤りとして報告してしまう。
/// </para>
/// <para>
/// <b>C++ を実行はしない。書かれたとおりに読める範囲だけを読む。</b>
/// 継承をたどり、ほかのクラスの <c>ModifyCompilationEnvironment</c> の呼び出しは引数を渡して追う。
/// <c>if</c> の条件は、定数 (<c>true</c>、数、<c>Lumen::ERayTracingShaderDispatchType::Inline</c> のような列挙の値) と、
/// それを渡した引数・ローカル変数だけで決まるときに評価する。決まらない条件の中の <c>SetDefine</c> は、値が決まらないものにする。
/// </para>
/// <para>
/// クラスを作るマクロ (<c>IMPLEMENT_LUMEN_COMPUTE_RAYTRACING_SHADER(F)</c> など) は、ほかのファイルで定義されていても展開する。
/// </para>
/// </remarks>
public static partial class UnrealShaderClasses
{
    /// <summary>生成のフォルダーに書き出す一覧のファイルの名前。</summary>
    public const string FileName = "ShaderlynShaderClasses.tsv";

    /// <summary>値が決まらないことを表す欄。</summary>
    private const string UnknownValue = "?";

    /// <summary>呼び出しを追う深さの上限。</summary>
    private const int MaxCallDepth = 16;

    /// <summary>クラスを作るマクロや、評価に要る定義を集める。</summary>
    internal sealed class Collector
    {
        /// <summary>クラスを作るかもしれないマクロ (本体に <c>class</c> があるか、名前が <c>IMPLEMENT_</c> で始まる) の名前から、<c>#define</c> の行。</summary>
        private readonly ConcurrentDictionary<string, string> _macroCandidates = new(StringComparer.Ordinal);

        /// <summary>クラスを作るマクロの名前から、<c>#define</c> の行と、本体が使うほかのクラスを作るマクロ。</summary>
        private Dictionary<string, (string Line, ImmutableArray<string> Uses)>? _classMacros;

        /// <summary>クラスを読むファイル (パスと、テスト用に渡された中身)。</summary>
        private readonly ConcurrentBag<(string Path, string? Text)> _files = [];

        /// <summary>
        /// 解析するシェーダープラットフォームの設定 (<c>DataDrivenPlatformInfo.ini</c> の節)。
        /// <c>FDataDrivenShaderPlatformInfo::GetSupportsPrimitiveShaders</c> などに答えるのに使う。
        /// <see langword="null"/> なら答えない。
        /// </summary>
        public ImmutableDictionary<string, string>? PlatformSettings { get; set; }

        /// <summary>列挙 (<c>enum class</c>) の名前から、メンバーの数 (値を書いたメンバーがあれば <see langword="null"/>)。</summary>
        private readonly ConcurrentDictionary<string, int?> _enums = new(StringComparer.Ordinal);

        /// <summary>整数の定数の名前から値 (同じ名前で値が違えば <see langword="null"/>)。</summary>
        private readonly ConcurrentDictionary<string, int?> _constants = new(StringComparer.Ordinal);

        /// <summary>列挙のメンバー (<c>E::Member</c>) から値 (同じ名前で値が違うか、読めなければ <see langword="null"/>)。</summary>
        private readonly ConcurrentDictionary<string, int?> _enumMembers = new(StringComparer.Ordinal);

        /// <summary>
        /// シェーダープラットフォームを問う小さな関数 (<c>inline bool RHISupportsRenderTargetWriteMask(const FStaticShaderPlatform Platform)</c>) の
        /// 名前から、<c>return</c> の式 (同じ名前で式が違えば <see langword="null"/>)。
        /// </summary>
        private readonly ConcurrentDictionary<string, string?> _platformHelpers = new(StringComparer.Ordinal);

        /// <summary>ファイルを 1 つ見る。並行して呼んでよい。</summary>
        /// <param name="path">ファイルのパス。</param>
        /// <param name="text">中身。</param>
        /// <param name="keepText">中身を覚えておくか (テスト用。ふつうは後で読み直す)。</param>
        public void Add(string path, string text, bool keepText)
        {
            foreach (string line in LogicalLines(text))
            {
                if (MacroDefinition().Match(line) is { Success: true } macro
                    && (macro.Groups["name"].Value.StartsWith("IMPLEMENT_", StringComparison.Ordinal) || ClassMacro().IsMatch(line)))
                {
                    _macroCandidates.TryAdd(macro.Groups["name"].Value, line);
                }
            }

            foreach (Match match in EnumClass().Matches(text))
            {
                string body = Comment().Replace(match.Groups["body"].Value, " ");
                string[] members = [.. body.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0)];

                // SHADER_PERMUTATION_ENUM_CLASS は 0 から MAX の手前までを使う。最後のメンバーが数を表すなら除く。
                int? count = members.Any(m => m.Contains('=', StringComparison.Ordinal))
                    ? null
                    : members.Length > 0 && members[^1] is "MAX" or "Max" or "Num" or "NUM" or "Count" ? members.Length - 1 : members.Length;
                if (match.Groups["scoped"].Success)
                {
                    _enums.AddOrUpdate(match.Groups["name"].Value, count, (_, old) => old == count ? old : null);
                    _enums.AddOrUpdate(InFile(path, match.Groups["name"].Value), count, (_, old) => old == count ? old : null);
                }

                // メンバーの値 (E::Member)。パーミュテーションの値と比べる式 (Get<FDim>() == E::Member) を評価するのに使う。
                // 値を書いたメンバー (= 3) は、整数か、前のメンバーの名前のときだけ読む。読めなければ、そこから先は決めない。
                long? next = 0;
                Dictionary<string, long> known = new(StringComparer.Ordinal);

                foreach (string member in members)
                {
                    string[] parts = member.Split('=', 2, StringSplitOptions.TrimEntries);
                    string memberName = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? parts[0];

                    if (parts.Length == 2)
                    {
                        next = TryParseInteger(parts[1], out long written) ? written : known.TryGetValue(parts[1], out long alias) ? alias : null;
                    }

                    // 修飾の無い列挙 (enum EMode { PCM_None, … }) のメンバーは、名前だけでも書かれる (CollisionMode == PCM_DepthBuffer)。
                    IEnumerable<string> keys = match.Groups["scoped"].Success
                        ? [match.Groups["name"].Value + "::" + memberName]
                        : [match.Groups["name"].Value + "::" + memberName, memberName];

                    // クラスの中の列挙 (FTSRUpdateHistoryCS::EQuality) は、ほかのクラスに同じ名前の別の列挙がある。同じファイルのものを先に探す。
                    keys = [.. keys, .. keys.Select(k => InFile(path, k))];

                    if (next is not { } value)
                    {
                        foreach (string unknownKey in keys)
                        {
                            _enumMembers.AddOrUpdate(unknownKey, (int?)null, (_, _) => null);
                        }

                        continue;
                    }

                    known[memberName] = value;
                    int? stored = (int)value;

                    foreach (string memberKey in keys)
                    {
                        _enumMembers.AddOrUpdate(memberKey, stored, (_, old) => old == stored ? old : null);
                    }

                    next = value + 1;
                }
            }

            // 整数の定数 (constexpr int32 X = 3;) と、整数に定義したマクロ (#define NANITE_RASTER_BIN_INIT 0)。
            // マクロは、C++ とシェーダーが共有するヘッダ (Shaders/Shared/NaniteDefinitions.h) に多い。
            // 読めないと、SetDefine(TEXT("RASTER_BIN_PASS"), NANITE_RASTER_BIN_INIT) の値が分からない。
            // 同じ名前に違う値があれば、どれとも決めない。
            foreach (Match match in IntegerConstant().Matches(text).Concat(IntegerDefine().Matches(text)))
            {
                int? value = int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
                _constants.AddOrUpdate(match.Groups["name"].Value, value, (_, old) => old == value ? old : null);
            }

            // RHI のヘッダ (DataDrivenShaderPlatformInfo.h) の、プラットフォームの設定を返すだけの関数。
            // ShouldCompilePermutation が RHISupportsRenderTargetWriteMask(Parameters.Platform) のように呼ぶ。
            // 式を読めば FDataDrivenShaderPlatformInfo の設定に行き着くので、名前ごとに答えを書かずに済む。
            if (text.Contains("FStaticShaderPlatform", StringComparison.Ordinal))
            {
                foreach (Match match in PlatformHelper().Matches(text))
                {
                    if (PlatformHelperReturn().Match(Comment().Replace(match.Groups["body"].Value, " ")) is { Success: true } body)
                    {
                        string expression = body.Groups["expression"].Value.Trim();
                        _platformHelpers.AddOrUpdate(match.Groups["name"].Value, expression, (_, old) => old == expression ? old : null);
                    }
                }
            }

            if (text.Contains("ModifyCompilationEnvironment", StringComparison.Ordinal)
                || text.Contains("SHADER_PERMUTATION_", StringComparison.Ordinal)
                || text.Contains("PermutationDomain", StringComparison.Ordinal)
                || text.Contains("IMPLEMENT_", StringComparison.Ordinal))
            {
                _files.Add((path, keepText ? text : null));
            }
        }

        /// <summary>集めたファイルからクラスを読み、登録されたクラスのマクロとパーミュテーションを求める。</summary>
        /// <param name="classNames">求めるクラスの名前 (登録に書かれたもの)。</param>
        /// <param name="missing">見つからなかったクラスの数。</param>
        /// <returns>見つかったクラス。</returns>
        public ImmutableArray<UnrealShaderClass> Resolve(IEnumerable<string> classNames, out int missing)
        {
            Model model = new(_enums, _constants, _enumMembers, PlatformSettings, _platformHelpers);
            _classMacros = FindClassMacros();

            Parallel.ForEach(_files, file =>
            {
                string? text = file.Text;

                if (text is null)
                {
                    try
                    {
                        text = File.ReadAllText(file.Path);
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return;
                    }
                }

                model.ParseFile(file.Path, WithClassMacros(text));
            });

            List<UnrealShaderClass> found = [];
            missing = 0;

            // テンプレートのクラスは、登録に書かれた引数ごとに読む (TParticleSimulationPS<PCM_DepthBuffer>)。
            foreach (string name in classNames.Select(ClassKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (model.Evaluate(name) is { } evaluated)
                {
                    found.Add(evaluated);
                }
                else
                {
                    missing++;
                }
            }

            return [.. found];
        }

        /// <summary>
        /// クラスを作るマクロを決める。本体でクラスを作るものと、それを使うもの
        /// (<c>IMPLEMENT_LUMEN_RAYGEN_AND_COMPUTE_RAYTRACING_SHADERS</c> は 2 つのマクロを呼ぶだけ) である。
        /// </summary>
        private Dictionary<string, (string Line, ImmutableArray<string> Uses)> FindClassMacros()
        {
            HashSet<string> names = [.. _macroCandidates.Where(p => ClassMacro().IsMatch(p.Value)).Select(p => p.Key)];
            bool added = true;

            while (added)
            {
                added = false;

                foreach ((string name, string line) in _macroCandidates)
                {
                    if (!names.Contains(name) && names.Any(used => Body(line).Contains(used + "(", StringComparison.Ordinal)))
                    {
                        names.Add(name);
                        added = true;
                    }
                }
            }

            return names.ToDictionary(
                name => name,
                name =>
                {
                    string line = _macroCandidates[name];
                    return (line, names.Where(used => used != name && Body(line).Contains(used + "(", StringComparison.Ordinal)).ToImmutableArray());
                },
                StringComparer.Ordinal);

            static string Body(string line)
            {
                int close = line.IndexOf(')', StringComparison.Ordinal);
                return close >= 0 ? line[(close + 1)..] : line;
            }
        }

        /// <summary>ファイルが使う、ほかのファイルで定義されたクラスを作るマクロの定義を、先頭に足す。マクロが使うマクロも足す。</summary>
        private string WithClassMacros(string text)
        {
            Dictionary<string, (string Line, ImmutableArray<string> Uses)> macros = _classMacros!;
            Stack<string> pending = new(macros.Keys.Where(name => text.Contains(name + "(", StringComparison.Ordinal)));
            HashSet<string> included = new(StringComparer.Ordinal);
            List<string> lines = [];

            while (pending.TryPop(out string? name))
            {
                if (!included.Add(name))
                {
                    continue;
                }

                // ファイルの中で定義していれば、そちらを使う。
                if (!text.Contains("#define " + name, StringComparison.Ordinal))
                {
                    lines.Add(macros[name].Line);
                }

                foreach (string used in macros[name].Uses)
                {
                    pending.Push(used);
                }
            }

            return lines.Count == 0 ? text : string.Join('\n', lines.Order(StringComparer.Ordinal)) + "\n" + text;
        }
    }

    /// <summary>
    /// 登録のクラスの名前から、一覧の鍵を作る。名前空間を除き、テンプレートの引数は空白を詰めて残す
    /// (<c>::TFoo&lt; A, B &gt;</c> → <c>TFoo&lt;A,B&gt;</c>)。
    /// </summary>
    /// <param name="name">クラスの名前。</param>
    /// <returns>鍵。</returns>
    public static string ClassKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int angle = name.IndexOf('<', StringComparison.Ordinal);

        if (angle < 0)
        {
            return BareClassName(name);
        }

        string arguments = string.Concat(name[angle..].Where(c => !char.IsWhiteSpace(c)));
        return BareClassName(name[..angle]) + arguments;
    }

    /// <summary>ファイルの中で宣言した名前の鍵 (列挙を、同じファイルのものから探すのに使う)。</summary>
    private static string InFile(string file, string name) => file + "\u0001" + name;

    /// <summary>登録のクラスの名前から、テンプレートの引数を除く (<c>TFoo&lt;A&gt;</c> → <c>TFoo</c>)。</summary>
    /// <param name="name">クラスの名前。</param>
    /// <returns>除いた名前。</returns>
    internal static string BareClassName(string name)
    {
        int angle = name.IndexOf('<', StringComparison.Ordinal);
        string bare = (angle >= 0 ? name[..angle] : name).Trim();
        int scope = bare.LastIndexOf("::", StringComparison.Ordinal);
        return scope >= 0 ? bare[(scope + 2)..] : bare;
    }

    // ------------------------------------------------------------------
    // 読み取ったクラス
    // ------------------------------------------------------------------

    /// <summary>C++ のクラスの宣言。</summary>
    /// <param name="Name">名前。</param>
    /// <param name="File">宣言したファイル。</param>
    /// <param name="Bases">基底のクラス。</param>
    private sealed record ClassDeclaration(string Name, string File, ImmutableArray<string> Bases)
    {
        /// <summary>テンプレートの仮引数の名前 (<c>template&lt;EMode Mode&gt; class T</c> の <c>Mode</c>)。テンプレートでなければ空。</summary>
        public ImmutableArray<string> TemplateParameters { get; init; } = [];
    }

    /// <summary>パーミュテーションの次元 (<c>class FDim : SHADER_PERMUTATION_BOOL("X");</c>)。</summary>
    private sealed record Dimension(string Name, string? Owner, string File, string Macro, ImmutableArray<string> Arguments);

    /// <summary>パーミュテーションの組 (<c>using FPermutationDomain = TShaderPermutationDomain&lt;…&gt;;</c>)。</summary>
    private sealed record DomainAlias(string Name, string? Owner, string File, ImmutableArray<(string? Qualifier, string Name)> Members);

    /// <summary>
    /// 読む関数の定義 1 つ (<c>ModifyCompilationEnvironment</c>、<c>ShouldCompilePermutation</c>、<c>RemapPermutation</c>)。
    /// </summary>
    private sealed record ModifyFunction(string Owner, string File, ImmutableArray<string> Parameters, ShaderSyntaxToken[] Body);

    /// <summary>値。決まらなければ <see cref="Unknown"/>。</summary>
    private readonly record struct Value(ValueKind Kind, long Number, string? Text)
    {
        public static Value Unknown => new(ValueKind.Unknown, 0, null);

        public static Value FromBool(bool value) => new(ValueKind.Number, value ? 1 : 0, null);

        /// <summary>真偽。決まらなければ <see langword="null"/>。</summary>
        public bool? AsBool => Kind == ValueKind.Number ? Number != 0 : null;

        /// <summary>マクロの値として書く文字列。決まらなければ <see langword="null"/>。</summary>
        public string? AsDefine => Kind switch
        {
            ValueKind.Number => Number.ToString(CultureInfo.InvariantCulture),
            ValueKind.String => Text,
            _ => null,
        };
    }

    private enum ValueKind
    {
        Unknown,
        Number,
        String,

        /// <summary>列挙の値などの名前 (<c>Lumen::ERayTracingShaderDispatchType::Inline</c>)。同じ名前どうしだけを比べる。</summary>
        Symbol,

        /// <summary>パーミュテーションの値の組 (<c>FPermutationDomain</c>)。<see cref="Value.Text"/> に値を並べた文字列を持ち、それで比べる。</summary>
        Vector,
    }

    /// <summary>集めた宣言と、クラスの評価。</summary>
    private sealed partial class Model(
        ConcurrentDictionary<string, int?> enums,
        ConcurrentDictionary<string, int?> constants,
        ConcurrentDictionary<string, int?> enumMembers,
        ImmutableDictionary<string, string>? platformSettings,
        ConcurrentDictionary<string, string?> platformHelpers)
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<ClassDeclaration>> _classes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentBag<Dimension>> _dimensions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentBag<DomainAlias>> _aliases = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> _functions = new(StringComparer.Ordinal);

        /// <summary>クラスの名前から、<c>ShouldCompilePermutation</c> の定義。</summary>
        private readonly ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> _shouldCompile = new(StringComparer.Ordinal);

        /// <summary>クラスの名前から、<c>RemapPermutation</c> の定義。</summary>
        private readonly ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> _remap = new(StringComparer.Ordinal);

        /// <summary>マクロを展開したファイルから、クラス・次元・組・<c>ModifyCompilationEnvironment</c> を読む。</summary>
        public void ParseFile(string path, string text)
        {
            ShaderSyntaxToken[] tokens;

            try
            {
                tokens = [.. UnrealUniformBufferGenerator.Expand(path, text).Where(t => t.Kind != ShaderSyntaxKind.EndOfFileToken)];
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // C++ をシェーダーの前処理で読むので、読めない書き方がありうる。そのファイルは読まない。
                return;
            }

            int[] match = MatchBraces(tokens);
            List<(string Name, int End)> enclosing = [];

            // 直前の template<…> の仮引数と、その終わりの位置。すぐ後の class にだけ付ける。
            (ImmutableArray<string> Names, int End) template = ([], -1);

            for (int i = 0; i < tokens.Length; i++)
            {
                while (enclosing.Count > 0 && i > enclosing[^1].End)
                {
                    enclosing.RemoveAt(enclosing.Count - 1);
                }

                string? owner = enclosing.Count > 0 ? enclosing[^1].Name : null;
                string word = tokens[i].Text;

                if (word == "template" && i + 1 < tokens.Length && tokens[i + 1].Text == "<")
                {
                    int end = SkipAngles(tokens, i + 1);
                    template = ([.. SplitStrings(tokens, i + 2, end - 1).Select(ParameterName).Where(n => n.Length > 0)], end);
                    i = end - 1;
                }
                else if ((word == "class" || word == "struct") && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    ImmutableArray<string> parameters = template.End == i ? template.Names : [];

                    if (ReadClass(path, tokens, match, i, owner, parameters, out int bodyOpen, out string? name) && name is not null)
                    {
                        enclosing.Add((name, match[bodyOpen]));
                        i = bodyOpen;
                    }
                }
                else if (word == "using" && i + 3 < tokens.Length && tokens[i + 2].Kind == ShaderSyntaxKind.EqualsToken
                         && tokens[i + 3].Text == "TShaderPermutationDomain")
                {
                    AddAlias(path, tokens, tokens[i + 1].Text, owner, i + 4);
                }
                else if (word == "typedef" && i + 1 < tokens.Length && tokens[i + 1].Text == "TShaderPermutationDomain")
                {
                    int end = SkipAngles(tokens, i + 2);

                    if (end < tokens.Length && tokens[end].Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        AddAlias(path, tokens, tokens[end].Text, owner, i + 2);
                    }
                }
                else if (word == "ModifyCompilationEnvironment" && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    ReadFunction(path, tokens, match, i, owner, _functions);
                }
                else if (word == "ShouldCompilePermutation" && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    ReadFunction(path, tokens, match, i, owner, _shouldCompile);
                }
                else if (word == "RemapPermutation" && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    ReadFunction(path, tokens, match, i, owner, _remap);
                }
            }
        }

        /// <summary><c>class</c> の宣言を読む。次元なら次元として覚える。</summary>
        /// <returns>本体のあるクラスなら <see langword="true"/>。</returns>
        private bool ReadClass(
            string path,
            ShaderSyntaxToken[] tokens,
            int[] match,
            int start,
            string? owner,
            ImmutableArray<string> templateParameters,
            out int bodyOpen,
            out string? name)
        {
            bodyOpen = -1;
            name = null;
            int i = start + 1;

            // class RENDERER_API FName final : public FBase
            while (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                if (tokens[i].Text != "final")
                {
                    name = tokens[i].Text;
                }

                i++;
            }

            if (name is null || i >= tokens.Length)
            {
                return false;
            }

            List<string> bases = [];

            if (tokens[i].Kind == ShaderSyntaxKind.ColonToken)
            {
                if (i + 2 < tokens.Length && tokens[i + 1].Text.StartsWith("SHADER_PERMUTATION_", StringComparison.Ordinal)
                    && tokens[i + 2].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    int close = SkipParens(tokens, i + 2);
                    List<string> arguments = SplitStrings(tokens, i + 3, close - 1);
                    _dimensions.GetOrAdd(name, _ => []).Add(new Dimension(name, owner, path, tokens[i + 1].Text, [.. arguments]));
                    return false;
                }

                i++;
                StringBuilder current = new();
                int angles = 0;

                for (; i < tokens.Length && tokens[i].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); i++)
                {
                    string text = tokens[i].Text;

                    if (text is "<")
                    {
                        angles++;
                    }
                    else if (text is ">")
                    {
                        angles--;
                    }
                    else if (text is ">>")
                    {
                        angles -= 2;
                    }
                    else if (angles == 0 && tokens[i].Kind == ShaderSyntaxKind.CommaToken)
                    {
                        AddBase(bases, current);
                    }
                    else if (angles == 0 && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken
                             && text is not ("public" or "protected" or "private" or "virtual"))
                    {
                        current.Clear().Append(text);
                    }
                }

                AddBase(bases, current);
            }

            if (i >= tokens.Length || tokens[i].Kind != ShaderSyntaxKind.OpenBraceToken || match[i] < 0)
            {
                return false;
            }

            _classes.GetOrAdd(name, _ => []).Add(new ClassDeclaration(name, path, [.. bases]) { TemplateParameters = templateParameters });
            bodyOpen = i;
            return true;

            static void AddBase(List<string> bases, StringBuilder current)
            {
                if (current.Length > 0)
                {
                    bases.Add(current.ToString());
                    current.Clear();
                }
            }
        }

        /// <summary><c>TShaderPermutationDomain&lt;…&gt;</c> の引数を読んで、組として覚える。</summary>
        private void AddAlias(string path, ShaderSyntaxToken[] tokens, string name, string? owner, int open)
        {
            if (open >= tokens.Length || tokens[open].Text != "<")
            {
                return;
            }

            int end = SkipAngles(tokens, open);
            List<(string? Qualifier, string Name)> members = [];
            List<string> parts = [];
            int depth = 0;

            for (int i = open + 1; i < end - 1; i++)
            {
                string text = tokens[i].Text;

                if (text is "<")
                {
                    depth++;
                }
                else if (text is ">")
                {
                    depth--;
                }
                else if (depth == 0 && tokens[i].Kind == ShaderSyntaxKind.CommaToken)
                {
                    Add();
                }
                else if (depth == 0 && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && text != "typename")
                {
                    parts.Add(text);
                }
            }

            Add();
            _aliases.GetOrAdd(name, _ => []).Add(new DomainAlias(name, owner, path, [.. members]));

            void Add()
            {
                if (parts.Count > 0)
                {
                    members.Add((parts.Count > 1 ? parts[^2] : null, parts[^1]));
                    parts.Clear();
                }
            }
        }

        /// <summary>関数の定義を読む。呼び出しや宣言だけなら何もしない。</summary>
        private static void ReadFunction(
            string path,
            ShaderSyntaxToken[] tokens,
            int[] match,
            int at,
            string? enclosing,
            ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> into)
        {
            int close = SkipParens(tokens, at + 1);
            int body = close;

            while (body < tokens.Length && tokens[body].Text is "const" or "override" or "final")
            {
                body++;
            }

            if (body >= tokens.Length || tokens[body].Kind != ShaderSyntaxKind.OpenBraceToken || match[body] < 0)
            {
                return;
            }

            // 引数の並びに型が無ければ、関数の定義ではない (マクロの中の呼び出し { … } など)。
            List<string> parameters = SplitStrings(tokens, at + 2, close - 1);

            if (parameters.Any(p => !p.Contains(' ', StringComparison.Ordinal) && !p.Contains('&', StringComparison.Ordinal)))
            {
                return;
            }

            string? owner = at >= 2 && tokens[at - 1].Kind == ShaderSyntaxKind.ColonColonToken ? tokens[at - 2].Text : enclosing;

            if (owner is null)
            {
                return;
            }

            ImmutableArray<string> names = [.. parameters.Select(ParameterName)];
            into.GetOrAdd(owner, _ => []).Add(new ModifyFunction(owner, path, names, tokens[(body + 1)..match[body]]));
        }

        /// <summary>引数の宣言から名前を取る (<c>const FFoo&amp; Parameters = X</c> → <c>Parameters</c>)。</summary>
        private static string ParameterName(string declaration)
        {
            int equals = declaration.IndexOf('=', StringComparison.Ordinal);
            string head = (equals >= 0 ? declaration[..equals] : declaration).Trim();
            int last = head.Length;

            while (last > 0 && (char.IsLetterOrDigit(head[last - 1]) || head[last - 1] == '_'))
            {
                last--;
            }

            return head[last..];
        }

        // --------------------------------------------------------------
        // 評価
        // --------------------------------------------------------------

        /// <summary>パーミュテーションの値の組を並べて評価する上限。超えるクラスは、組ごとには評価しない。</summary>
        /// <remarks>
        /// 組ごとに <c>ShouldCompilePermutation</c> と <c>ModifyCompilationEnvironment</c> を実行する。
        /// Lumen の大きいクラスでも 1,000 前後に収まる。
        /// </remarks>
        private const int MaxEnumeratedPermutations = 4096;

        /// <summary>クラスのマクロとパーミュテーションを求める。</summary>
        /// <param name="key">クラスの鍵 (<see cref="ClassKey"/>)。テンプレートなら引数を含む。</param>
        /// <returns>求めたもの。クラスが見つからなければ <see langword="null"/>。</returns>
        /// <remarks>
        /// <para>
        /// <b>パーミュテーションの値の組ごとに、エンジンと同じ関数を実行する。</b>
        /// <c>ShouldCompilePermutation</c> が <see langword="false"/> を返す組 (<c>RemapPermutation</c> で書き換わる組を含む) はコンパイルされない。
        /// 残った組ごとに <c>ModifyCompilationEnvironment</c> を実行して、定義とフラグを求める。
        /// 組ごとに値の変わる定義は構成の軸にし、同時には現れない値の組は、同時には有効にならない組として書き出す
        /// (<see cref="UnrealShaderClass.Exclusions"/>)。
        /// </para>
        /// <para>
        /// 組が多すぎるか、値の分からない次元があれば、組ごとには評価せず、パーミュテーションの値によらない部分だけを読む (以前のとおり)。
        /// </para>
        /// </remarks>
        public UnrealShaderClass? Evaluate(string key)
        {
            if (!_classes.TryGetValue(BareClassName(key), out ConcurrentBag<ClassDeclaration>? declarations))
            {
                return null;
            }

            ClassDeclaration declaration = declarations.OrderBy(d => d.File, StringComparer.Ordinal).First();
            Dictionary<string, ShaderSyntaxToken[]> templateBindings = TemplateBindings(declaration, key);

            List<(string Dimension, string Macro, ImmutableArray<int>? Values)> axes = [];

            if (FindDomain(declaration, "FPermutationDomain", 0) is { } domain)
            {
                CollectDimensions(domain, axes, 0);
            }

            SortedDictionary<string, ImmutableArray<int>?> permutations = new(StringComparer.Ordinal);

            foreach ((_, string macro, ImmutableArray<int>? values) in axes)
            {
                permutations[macro] = values;
            }

            (ModifyFunction Function, ClassDeclaration Class)? modify = FindEntryFunction(declaration, _functions, 2, 0);

            if (axes.All(a => a.Values is { IsEmpty: false })
                && axes.Aggregate(1L, (product, a) => Math.Min(product * a.Values!.Value.Length, MaxEnumeratedPermutations + 1L)) <= MaxEnumeratedPermutations)
            {
                List<(Dictionary<string, long> Vector, Environment Environment)> results = Enumerate(declaration, modify, axes, templateBindings);

                if (results.Count > 0)
                {
                    return Summarize(key, axes, results);
                }

                // どの組も ShouldCompilePermutation が false と決めた。このプラットフォームではコンパイルされない
                // (RTWriteMaskDecode.usf。RHISupportsRenderTargetWriteMask は PC で false)。
                return new UnrealShaderClass(
                    key,
                    ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
                    permutations.ToImmutableSortedDictionary(StringComparer.Ordinal),
                    ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal))
                {
                    NeverCompiled = true,
                };
            }

            Environment environment = new();

            if (modify is { } entry)
            {
                Execute(entry.Function, entry.Class, new(templateBindings, StringComparer.Ordinal), environment, certain: true, depth: 0, vector: null);
            }

            return new UnrealShaderClass(
                key,
                environment.Defines.ToImmutableSortedDictionary(StringComparer.Ordinal),
                permutations.ToImmutableSortedDictionary(StringComparer.Ordinal),
                environment.Flags.ToImmutableSortedDictionary(StringComparer.Ordinal));
        }

        /// <summary>テンプレートのクラスの仮引数に、鍵に書かれた実引数を結び付ける。</summary>
        /// <param name="declaration">クラスの宣言。</param>
        /// <param name="key">クラスの鍵 (<c>TFoo&lt;A,B&gt;</c>)。</param>
        /// <returns>仮引数の名前から、実引数のトークン。テンプレートでないか、引数が無ければ空。</returns>
        private static Dictionary<string, ShaderSyntaxToken[]> TemplateBindings(ClassDeclaration declaration, string key)
        {
            Dictionary<string, ShaderSyntaxToken[]> bindings = new(StringComparer.Ordinal);
            int angle = key.IndexOf('<', StringComparison.Ordinal);

            if (declaration.TemplateParameters.IsEmpty || angle < 0 || !key.EndsWith('>'))
            {
                return bindings;
            }

            ShaderSyntaxToken[] tokens = Lex(key[(angle + 1)..^1]);
            List<ShaderSyntaxToken[]> arguments = SplitTokens(tokens, 0, tokens.Length);

            for (int i = 0; i < arguments.Count && i < declaration.TemplateParameters.Length; i++)
            {
                bindings[declaration.TemplateParameters[i]] = arguments[i];
            }

            return bindings;
        }

        /// <summary>パーミュテーションの値の組ごとに、コンパイルされるかと、定義とフラグを求める。</summary>
        /// <returns>コンパイルされる組と、その組の定義とフラグ。</returns>
        private List<(Dictionary<string, long> Vector, Environment Environment)> Enumerate(
            ClassDeclaration declaration,
            (ModifyFunction Function, ClassDeclaration Class)? modify,
            List<(string Dimension, string Macro, ImmutableArray<int>? Values)> axes,
            Dictionary<string, ShaderSyntaxToken[]> templateBindings)
        {
            List<(Dictionary<string, long> Vector, Environment Environment)> results = [];
            (ModifyFunction Function, ClassDeclaration Class)? shouldCompile = FindEntryFunction(declaration, _shouldCompile, 1, 0);
            int[] indices = new int[axes.Count];

            while (true)
            {
                Dictionary<string, long> vector = new(StringComparer.Ordinal);

                for (int k = 0; k < axes.Count; k++)
                {
                    vector[axes[k].Dimension] = axes[k].Values!.Value[indices[k]];
                }

                bool compiles = true;

                if (shouldCompile is { } should)
                {
                    Context checkContext = Execute(should.Function, should.Class, new(templateBindings, StringComparer.Ordinal), new Environment(), certain: true, depth: 0, vector);
                    compiles = checkContext.State.Return?.AsBool != false;
                }

                if (compiles)
                {
                    Environment environment = new();

                    if (modify is { } entry)
                    {
                        Execute(entry.Function, entry.Class, new(templateBindings, StringComparer.Ordinal), environment, certain: true, depth: 0, vector);
                    }

                    results.Add((vector, environment));
                }

                // 次の組へ。
                int position = axes.Count - 1;

                while (position >= 0 && ++indices[position] >= axes[position].Values!.Value.Length)
                {
                    indices[position] = 0;
                    position--;
                }

                if (position < 0)
                {
                    break;
                }
            }

            return results;
        }

        /// <summary>組ごとの結果を、クラスの一覧の形にまとめる。</summary>
        /// <param name="key">クラスの鍵。</param>
        /// <param name="axes">パーミュテーションの次元。</param>
        /// <param name="results">コンパイルされる組と、その組の定義とフラグ。</param>
        /// <returns>まとめたクラス。</returns>
        private static UnrealShaderClass Summarize(
            string key,
            List<(string Dimension, string Macro, ImmutableArray<int>? Values)> axes,
            List<(Dictionary<string, long> Vector, Environment Environment)> results)
        {
            SortedDictionary<string, ImmutableArray<int>?> permutations = new(StringComparer.Ordinal);
            SortedDictionary<string, ImmutableArray<int>> varying = new(StringComparer.Ordinal);

            // 組ごとの値。構成の軸になるもの (パーミュテーションと、組で値の変わる定義とフラグ) を、同時に現れるかを見るのに使う。
            List<Dictionary<string, long>> rows = [.. results.Select(_ => new Dictionary<string, long>(StringComparer.Ordinal))];

            foreach ((string dimension, string macro, _) in axes)
            {
                permutations[macro] = [.. results.Select(r => (int)r.Vector[dimension]).Distinct().Order()];

                for (int i = 0; i < results.Count; i++)
                {
                    rows[i][macro] = results[i].Vector[dimension];
                }
            }

            SortedDictionary<string, string?> defines = Collapse(results.Select(r => r.Environment.Defines).ToList(), "0", varying, rows);
            SortedDictionary<string, string?> flags = Collapse(results.Select(r => r.Environment.Flags).ToList(), "0", varying, rows);

            // 2 つ以上の値をとる名前の、同時には現れない値の組。
            List<string> names = [.. rows[0].Keys.Where(n => rows.Select(r => r[n]).Distinct().Skip(1).Any()).Order(StringComparer.Ordinal)];
            List<KeyValuePair<string, string>> exclusions = [];

            for (int a = 0; a < names.Count; a++)
            {
                for (int b = a + 1; b < names.Count; b++)
                {
                    HashSet<(long, long)> together = [.. rows.Select(r => (r[names[a]], r[names[b]]))];

                    foreach (long left in rows.Select(r => r[names[a]]).Distinct().Order())
                    {
                        foreach (long right in rows.Select(r => r[names[b]]).Distinct().Order())
                        {
                            if (!together.Contains((left, right)))
                            {
                                exclusions.Add(new(Atom(names[a], left), Atom(names[b], right)));
                            }
                        }
                    }
                }
            }

            return new UnrealShaderClass(
                key,
                defines.ToImmutableSortedDictionary(StringComparer.Ordinal),
                permutations.ToImmutableSortedDictionary(StringComparer.Ordinal),
                flags.ToImmutableSortedDictionary(StringComparer.Ordinal))
            {
                Varying = varying.ToImmutableSortedDictionary(StringComparer.Ordinal),
                Exclusions = [.. exclusions],
            };

            static string Atom(string name, long value) => name + "==" + value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 組ごとの定義 (またはフラグ) を、どの組でも同じ値のもの、組で整数の値が変わるもの、決まらないものに分ける。
        /// </summary>
        /// <param name="perVector">組ごとの定義。</param>
        /// <param name="absent">その組で定義しなかったときの値 (条件式では 0 と読まれる)。</param>
        /// <param name="varying">組で整数の値が変わる名前と値を足す先。</param>
        /// <param name="rows">組ごとの、構成の軸の値を足す先。</param>
        /// <returns>どの組でも同じ値のものと、決まらないもの (<see langword="null"/>)。</returns>
        private static SortedDictionary<string, string?> Collapse(
            List<SortedDictionary<string, string?>> perVector,
            string absent,
            SortedDictionary<string, ImmutableArray<int>> varying,
            List<Dictionary<string, long>> rows)
        {
            SortedDictionary<string, string?> collapsed = new(StringComparer.Ordinal);

            foreach (string name in perVector.SelectMany(d => d.Keys).Distinct(StringComparer.Ordinal))
            {
                List<string?> values = [.. perVector.Select(d => d.TryGetValue(name, out string? v) ? v : absent)];

                if (values.Any(v => v is null))
                {
                    collapsed[name] = null;
                }
                else if (values.Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    collapsed[name] = values[0];
                }
                else if (values.All(v => TryParseInteger(v!, out _)))
                {
                    long[] numbers = [.. values.Select(v => TryParseInteger(v!, out long n) ? n : 0)];
                    varying[name] = [.. numbers.Select(n => (int)n).Distinct().Order()];

                    for (int i = 0; i < rows.Count; i++)
                    {
                        rows[i][name] = numbers[i];
                    }
                }
                else
                {
                    collapsed[name] = null;
                }
            }

            return collapsed;
        }

        /// <summary>クラスの宣言を探す。同じ名前が複数あれば、指定したファイルのものを選ぶ。</summary>
        private ClassDeclaration? FindClass(string name, string? preferredFile)
            => _classes.TryGetValue(name, out ConcurrentBag<ClassDeclaration>? found)
                ? found.OrderBy(d => d.File == preferredFile ? 0 : 1).ThenBy(d => d.File, StringComparer.Ordinal).First()
                : null;

        /// <summary>
        /// エンジンが呼ぶ関数を探す。クラスに無ければ、基底のクラスのものを使う (C++ の名前の探し方)。
        /// </summary>
        /// <param name="declaration">クラス。</param>
        /// <param name="functions">探す関数の種類。</param>
        /// <param name="parameterCount">エンジンが渡す引数の数 (<c>ModifyCompilationEnvironment</c> は 2、<c>ShouldCompilePermutation</c> は 1)。</param>
        /// <param name="depth">基底をたどった深さ。</param>
        private (ModifyFunction Function, ClassDeclaration Class)? FindEntryFunction(
            ClassDeclaration declaration,
            ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> functions,
            int parameterCount,
            int depth)
        {
            if (depth > MaxCallDepth)
            {
                return null;
            }

            if (functions.TryGetValue(declaration.Name, out ConcurrentBag<ModifyFunction>? own))
            {
                ModifyFunction? entry = own
                    .Where(f => f.Parameters.Length == parameterCount)
                    .OrderBy(f => f.File == declaration.File ? 0 : 1)
                    .ThenBy(f => f.File, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (entry is not null)
                {
                    return (entry, declaration);
                }
            }

            foreach (string baseName in declaration.Bases)
            {
                if (FindClass(baseName, declaration.File) is { } baseClass
                    && FindEntryFunction(baseClass, functions, parameterCount, depth + 1) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>関数を探す。クラスに無ければ基底のクラスから探す。</summary>
        private (ModifyFunction Function, ClassDeclaration Class)? FindFunction(
            ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> functions,
            string className,
            string? preferredFile,
            int argumentCount,
            ModifyFunction? exclude,
            int depth)
        {
            if (depth > MaxCallDepth || FindClass(className, preferredFile) is not { } declaration)
            {
                // クラスとして宣言していない名前空間の関数 (FForwardLightingParameters::ModifyCompilationEnvironment など) も探す。
                return functions.TryGetValue(className, out ConcurrentBag<ModifyFunction>? loose)
                       && loose.FirstOrDefault(f => f != exclude && f.Parameters.Length == argumentCount) is { } function
                    ? (function, new ClassDeclaration(className, function.File, []))
                    : null;
            }

            if (functions.TryGetValue(declaration.Name, out ConcurrentBag<ModifyFunction>? own)
                && own
                    .Where(f => f != exclude && f.Parameters.Length == argumentCount)
                    .OrderBy(f => f.File == declaration.File ? 0 : 1)
                    .FirstOrDefault() is { } found)
            {
                return (found, declaration);
            }

            foreach (string baseName in declaration.Bases)
            {
                if (FindFunction(functions, baseName, declaration.File, argumentCount, exclude, depth + 1) is { } inherited)
                {
                    return inherited;
                }
            }

            return null;
        }

        /// <summary>関数の本体を実行する。</summary>
        /// <param name="function">関数。</param>
        /// <param name="owner">関数を持つクラス (<c>Super</c> と修飾の無い呼び出しを解決する)。</param>
        /// <param name="bindings">引数の名前から、渡した値の式。</param>
        /// <param name="environment">定義を足す先。</param>
        /// <param name="certain">必ず通るか。</param>
        /// <param name="depth">呼び出しの深さ。</param>
        /// <param name="vector">パーミュテーションの値の組 (次元のクラスの名前から値)。組ごとに評価しないなら <see langword="null"/>。</param>
        /// <param name="vectorNames">値の組を指す名前 (<c>RemapPermutation</c> の引数など)。</param>
        /// <returns>実行の文脈。戻り値と、値の組を決められなかったかを持つ。</returns>
        private Context Execute(
            ModifyFunction function,
            ClassDeclaration owner,
            Dictionary<string, ShaderSyntaxToken[]> bindings,
            Environment environment,
            bool certain,
            int depth,
            Dictionary<string, long>? vector,
            IEnumerable<string>? vectorNames = null)
        {
            Context context = new(function, owner, new(bindings, StringComparer.Ordinal), environment, depth)
            {
                Vector = vector,
            };

            context.VectorNames.UnionWith(vectorNames ?? []);

            if (depth > MaxCallDepth)
            {
                return context;
            }

            ShaderSyntaxToken[] body = function.Body;
            int[] match = MatchBraces(body);
            bool returned = false;
            ExecuteRange(body, match, 0, body.Length, context, certain, ref returned);
            return context;
        }

        /// <summary>実行の文脈。</summary>
        private sealed record Context(
            ModifyFunction Function,
            ClassDeclaration Owner,
            Dictionary<string, ShaderSyntaxToken[]> Bindings,
            Environment Environment,
            int Depth)
        {
            /// <summary>パーミュテーションの値の組。組ごとに評価しないなら <see langword="null"/>。<c>Set&lt;FDim&gt;()</c> で書き換わる。</summary>
            public Dictionary<string, long>? Vector { get; init; }

            /// <summary>値の組を指す名前 (<c>FPermutationDomain PermutationVector(…)</c> の <c>PermutationVector</c>)。</summary>
            public HashSet<string> VectorNames { get; } = new(StringComparer.Ordinal);

            /// <summary>戻り値と、値の組を決められなかったか。</summary>
            public ExecutionState State { get; } = new();
        }

        /// <summary>実行の結果のうち、文脈の外へ返すもの。</summary>
        private sealed class ExecutionState
        {
            /// <summary>必ず通る <c>return</c> の値。届かなければ <see langword="null"/>。</summary>
            public Value? Return { get; set; }

            /// <summary>値の組を、決まらない値で書き換えたか (<c>RemapPermutation</c> の結果が決まらない)。</summary>
            public bool VectorUnknown { get; set; }
        }

        /// <summary>文の並びを実行する。</summary>
        /// <param name="body">関数の本体のトークン。</param>
        /// <param name="match">波括弧の対応。</param>
        /// <param name="start">並びの先頭。</param>
        /// <param name="end">並びの終わり (含まない)。</param>
        /// <param name="context">実行の文脈。</param>
        /// <param name="certain">必ず通るか。</param>
        /// <param name="returned">必ず通る <c>return</c> に達したら立てる。決まらない <c>return</c> のあとは、必ず通るとは言えない。</param>
        private void ExecuteRange(ShaderSyntaxToken[] body, int[] match, int start, int end, Context context, bool certain, ref bool returned)
        {
            int i = start;

            while (i < end && !returned)
            {
                ShaderSyntaxToken token = body[i];

                if (token.Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    int close = match[i] < 0 ? end : match[i];
                    ExecuteRange(body, match, i + 1, close, context, certain, ref returned);
                    i = close + 1;
                    continue;
                }

                if (token.Text == "if" && i + 1 < end && body[i + 1].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    int conditionEnd = SkipParens(body, i + 1);
                    bool? condition = Evaluate(body[(i + 2)..(conditionEnd - 1)], context, 0).AsBool;
                    int thenEnd = StatementEnd(body, match, conditionEnd, end);
                    int elseStart = thenEnd;
                    int elseEnd = thenEnd;

                    if (thenEnd < end && body[thenEnd].Text == "else")
                    {
                        elseStart = thenEnd + 1;
                        elseEnd = StatementEnd(body, match, elseStart, end);
                    }

                    bool thenReturned = false;
                    bool elseReturned = false;

                    if (condition != false)
                    {
                        ExecuteRange(body, match, conditionEnd, thenEnd, context, certain && condition == true, ref thenReturned);
                    }

                    if (condition != true && elseEnd > elseStart)
                    {
                        ExecuteRange(body, match, elseStart, elseEnd, context, certain && condition == false, ref elseReturned);
                    }

                    // 片方の分岐だけが戻るなら、そのあとの文は必ずは通らない。
                    if (thenReturned || elseReturned)
                    {
                        if (certain && (condition == true ? thenReturned : condition == false ? elseReturned : thenReturned && elseReturned))
                        {
                            returned = true;
                        }
                        else
                        {
                            certain = false;
                        }
                    }

                    i = elseEnd;
                    continue;
                }

                if (token.Text is "for" or "while" or "switch" or "do")
                {
                    int bodyStart = token.Text == "do" ? i + 1 : (i + 1 < end && body[i + 1].Kind == ShaderSyntaxKind.OpenParenToken ? SkipParens(body, i + 1) : i + 1);
                    int bodyEnd = StatementEnd(body, match, bodyStart, end);
                    bool ignored = false;
                    ExecuteRange(body, match, bodyStart, bodyEnd, context, certain: false, ref ignored);

                    // do { } while (…); の while の部分を飛ばす。
                    i = token.Text == "do" ? StatementEnd(body, match, bodyEnd, end) : bodyEnd;
                    continue;
                }

                int statementEnd = StatementEnd(body, match, i, end);

                if (token.Text == "return")
                {
                    if (certain)
                    {
                        // return の式 (ShouldCompilePermutation の真偽、RemapPermutation の組)。
                        int expressionEnd = statementEnd > i + 1 && body[statementEnd - 1].Kind == ShaderSyntaxKind.SemicolonToken ? statementEnd - 1 : statementEnd;

                        if (expressionEnd > i + 1)
                        {
                            context.State.Return = Evaluate(body[(i + 1)..expressionEnd], context, 0);
                        }

                        returned = true;
                        return;
                    }

                    certain = false;
                    i = statementEnd;
                    continue;
                }

                ExecuteStatement(body[i..statementEnd], context, certain);
                i = statementEnd;
            }
        }

        /// <summary>式の文を実行する (定義、フラグ、呼び出し、ローカル変数、値の組の書き換え)。</summary>
        private void ExecuteStatement(ShaderSyntaxToken[] statement, Context context, bool certain)
        {
            for (int i = 0; i < statement.Length; i++)
            {
                string text = statement[i].Text;

                // 値の組の書き換え: PermutationVector.Set<FDim>(Value)。
                if (text == "Set" && i >= 2 && statement[i - 1].Kind == ShaderSyntaxKind.DotToken
                    && context.VectorNames.Contains(statement[i - 2].Text)
                    && i + 1 < statement.Length && statement[i + 1].Text == "<")
                {
                    int angleEnd = SkipAngles(statement, i + 1);

                    if (angleEnd < statement.Length && statement[angleEnd].Kind == ShaderSyntaxKind.OpenParenToken)
                    {
                        string dimension = LastIdentifier(statement, i + 2, angleEnd - 1);
                        int setClose = SkipParens(statement, angleEnd);
                        Value assigned = Evaluate(statement[(angleEnd + 1)..(setClose - 1)], context, 0);

                        if (context.Vector is { } vector && vector.ContainsKey(dimension))
                        {
                            if (certain && assigned.Kind == ValueKind.Number)
                            {
                                vector[dimension] = assigned.Number;
                            }
                            else
                            {
                                context.State.VectorUnknown = true;
                            }
                        }

                        i = setClose - 1;
                        continue;
                    }
                }

                if (i + 1 >= statement.Length || statement[i + 1].Kind != ShaderSyntaxKind.OpenParenToken)
                {
                    continue;
                }

                int close = SkipParens(statement, i + 1);
                List<ShaderSyntaxToken[]> arguments = SplitTokens(statement, i + 2, close - 1);

                if (text == "SetDefine" && arguments.Count == 2 && DefineName(arguments[0]) is { } name)
                {
                    Value value = Evaluate(arguments[1], context, 0);
                    context.Environment.Set(context.Environment.Defines, name, certain ? value.AsDefine : null);
                    i = close - 1;
                }
                else if (text == "SET_SHADER_DEFINE" && arguments.Count == 3 && arguments[1] is [{ Kind: ShaderSyntaxKind.IdentifierToken } macro])
                {
                    Value value = Evaluate(arguments[2], context, 0);
                    context.Environment.Set(context.Environment.Defines, macro.Text, certain ? value.AsDefine : null);
                    i = close - 1;
                }
                else if (text == "Add" && i >= 2 && statement[i - 2].Text == "CompilerFlags"
                         && arguments.Count == 1 && arguments[0] is [{ Kind: ShaderSyntaxKind.IdentifierToken } flag])
                {
                    context.Environment.Set(context.Environment.Flags, flag.Text, certain ? "1" : null);
                    i = close - 1;
                }
                else if (text == "ModifyCompilationEnvironment")
                {
                    Call(statement, i, arguments, context, certain);
                    i = close - 1;
                }
            }

            // 値の組の宣言: FPermutationDomain PermutationVector(Parameters.PermutationId);
            // 組ごとに評価するときは、どの宣言も今の組を指す (RemapPermutation で書き換えた組は、コンパイルされる組と同じである)。
            if (statement.Length >= 3
                && statement[0].Text.EndsWith("PermutationDomain", StringComparison.Ordinal)
                && statement[1].Kind == ShaderSyntaxKind.IdentifierToken
                && statement[2].Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.SemicolonToken)
            {
                context.VectorNames.Add(statement[1].Text);
                return;
            }

            int equals = Array.FindIndex(statement, t => t.Kind == ShaderSyntaxKind.EqualsToken);
            int end = statement.Length > 0 && statement[^1].Kind == ShaderSyntaxKind.SemicolonToken ? statement.Length - 1 : statement.Length;

            // ローカル変数: const bool bInline = DispatchType == E::Inline; と、その後の代入: Mode = E::Punctual;
            bool declares = equals >= 2 && statement[equals - 1].Kind == ShaderSyntaxKind.IdentifierToken
                            && statement[equals - 2].Kind == ShaderSyntaxKind.IdentifierToken
                            && !statement[..equals].Any(t => t.Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken);
            bool assigns = equals == 1 && statement[0].Kind == ShaderSyntaxKind.IdentifierToken && context.Bindings.ContainsKey(statement[0].Text);

            if (declares || assigns)
            {
                ShaderSyntaxToken[] initializer = ResolvePermutationGets(Substitute(statement[(equals + 1)..end], context.Bindings, 0), context);

                // 決まらない分岐の中で決めた値は、そのあとでは決まらない。
                if (certain)
                {
                    context.Bindings[statement[equals - 1].Text] = initializer;
                }
                else
                {
                    context.Bindings.Remove(statement[equals - 1].Text);
                }
            }
        }

        /// <summary>ほかの <c>ModifyCompilationEnvironment</c> の呼び出しを追う。</summary>
        private void Call(ShaderSyntaxToken[] statement, int at, List<ShaderSyntaxToken[]> arguments, Context context, bool certain)
        {
            if (ResolveCall(_functions, statement, at, arguments, context) is not { } found)
            {
                return;
            }

            Execute(found.Function, found.Class, found.Bindings, context.Environment, certain, context.Depth + 1, context.Vector);
        }

        /// <summary>呼び出しの相手を探し、引数を結び付ける。</summary>
        private (ModifyFunction Function, ClassDeclaration Class, Dictionary<string, ShaderSyntaxToken[]> Bindings)? ResolveCall(
            ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> functions,
            ShaderSyntaxToken[] statement,
            int at,
            List<ShaderSyntaxToken[]> arguments,
            Context context)
        {
            (ModifyFunction Function, ClassDeclaration Class)? target;
            int count = arguments.Count == 1 && arguments[0].Length == 0 ? 0 : arguments.Count;

            if (at >= 2 && statement[at - 1].Kind == ShaderSyntaxKind.ColonColonToken)
            {
                string qualifier = statement[at - 2].Text;

                target = qualifier == "Super"
                    ? context.Owner.Bases.Select(b => FindFunction(functions, b, context.Owner.File, count, null, context.Depth + 1)).FirstOrDefault(f => f is not null)
                    : FindFunction(functions, qualifier, context.Owner.File, count, context.Function, context.Depth + 1);
            }
            else if (at >= 1 && statement[at - 1].Kind is ShaderSyntaxKind.DotToken)
            {
                return null;
            }
            else
            {
                target = FindFunction(functions, context.Owner.Name, context.Owner.File, count, context.Function, context.Depth + 1);
            }

            if (target is not { } found)
            {
                return null;
            }

            Dictionary<string, ShaderSyntaxToken[]> bindings = new(StringComparer.Ordinal);

            for (int k = 0; k < count && k < found.Function.Parameters.Length; k++)
            {
                bindings[found.Function.Parameters[k]] = ResolvePermutationGets(Substitute(arguments[k], context.Bindings, 0), context);
            }

            return (found.Function, found.Class, bindings);
        }

        /// <summary>
        /// 式の中で呼ぶ関数を評価する。<c>ShouldCompilePermutation</c> と <c>RemapPermutation</c> は実行し、
        /// シェーダープラットフォームを問う関数は、解析するプラットフォーム (PC の Direct3D 12、SM6) として答える。
        /// </summary>
        /// <param name="tokens">式のトークン。</param>
        /// <param name="at">呼ぶ関数の名前の位置。</param>
        /// <param name="arguments">引数。</param>
        /// <param name="context">実行の文脈。</param>
        /// <returns>値。評価しない関数なら <see langword="null"/>。</returns>
        private Value? EvaluateCall(ShaderSyntaxToken[] tokens, int at, List<ShaderSyntaxToken[]> arguments, Context context)
        {
            string name = tokens[at].Text;

            if (PlatformFunctions.TryGetValue(name, out bool answer))
            {
                return Value.FromBool(answer);
            }

            if (at >= 2
                && tokens[at - 1].Kind == ShaderSyntaxKind.ColonColonToken
                && tokens[at - 2].Text == "FDataDrivenShaderPlatformInfo"
                && PlatformSetting(name) is { } setting)
            {
                return setting;
            }

            // RHISupportsRenderTargetWriteMask(Platform) など。式を評価する (中で FDataDrivenShaderPlatformInfo の設定を引く)。
            if (arguments.Count == 1
                && platformHelpers.TryGetValue(name, out string? helper) && helper is not null
                && context.Depth < MaxCallDepth)
            {
                return Evaluate(Lex(helper), context with { Depth = context.Depth + 1 }, context.Depth + 1);
            }

            // PC の Direct3D 12 (SM6) では、16 ビットの型は実行時に決まる (DataDrivenPlatformInfo の bSupportsRealTypes = RuntimeDependent)。
            if (name is "Supports16BitVALU" or "GetSupportsRealTypes"
                && enumMembers.TryGetValue("ERHIFeatureSupport::RuntimeDependent", out int? dependent) && dependent is { } support)
            {
                return new Value(ValueKind.Number, support, null);
            }

            if (name == "ShouldCompilePermutation" && context.Vector is not null)
            {
                if (ResolveCall(_shouldCompile, tokens, at, arguments, context) is not { } found)
                {
                    return null;
                }

                Context called = Execute(found.Function, found.Class, found.Bindings, new Environment(), certain: true, context.Depth + 1, context.Vector);
                return called.State.Return ?? Value.Unknown;
            }

            if (name == "RemapPermutation" && context.Vector is { } vector && arguments.Count == 1)
            {
                if (ResolveCall(_remap, tokens, at, arguments, context) is not { } found)
                {
                    return null;
                }

                // 書き換えた組を返す。引数の組 (今の組) は書き換えない。
                Dictionary<string, long> copy = new(vector, StringComparer.Ordinal);
                Context called = Execute(found.Function, found.Class, [], new Environment(), certain: true, context.Depth + 1, copy, found.Function.Parameters);
                return called.State.VectorUnknown ? Value.Unknown : VectorValue(copy);
            }

            return null;
        }

        /// <summary>シェーダープラットフォームを問う関数と、解析するプラットフォーム (PC の Direct3D 12、SM6) での答え。</summary>
        private static readonly Dictionary<string, bool> PlatformFunctions = new(StringComparer.Ordinal)
        {
            ["IsMobilePlatform"] = false,
            ["IsPCPlatform"] = true,
            ["IsD3DPlatform"] = true,
            ["IsMetalPlatform"] = false,
            ["IsMetalMobilePlatform"] = false,
            ["IsVulkanPlatform"] = false,
            ["IsVulkanMobilePlatform"] = false,
            ["IsOpenGLPlatform"] = false,
            ["IsAndroidPlatform"] = false,
            ["IsConsolePlatform"] = false,
            ["IsSimulatedPlatform"] = false,
            ["IsMobileDeferredShadingEnabled"] = false,
            ["IsFeatureLevelSupported"] = true,
            ["DoesPlatformSupportVirtualShadowMaps"] = true,
            ["DoesPlatformSupportNanite"] = true,
            ["DoesPlatformSupportLumenGI"] = true,
        };

        /// <summary>
        /// <c>FDataDrivenShaderPlatformInfo</c> の問い合わせ (<c>GetSupportsPrimitiveShaders</c>) に、プラットフォームの設定で答える。
        /// </summary>
        /// <param name="name">関数の名前。</param>
        /// <returns>値。設定が無いか、読めない値なら <see langword="null"/>。</returns>
        /// <remarks>
        /// <c>GetX</c> は設定の <c>bX</c> か <c>X</c> を返す。<c>true</c> / <c>false</c>、整数、
        /// <c>ERHIFeatureSupport</c> のメンバー (<c>RuntimeDependent</c>) を読む。
        /// 節に無い真偽の設定は、エンジンの既定値 (<c>SetDefaultValues</c>) とする。ほとんどは <c>false</c> である。
        /// </remarks>
        private Value? PlatformSetting(string name)
        {
            if (platformSettings is null || !name.StartsWith("Get", StringComparison.Ordinal) || name.Length <= 3)
            {
                return null;
            }

            string key = name[3..];

            if (platformSettings.TryGetValue("b" + key, out string? text) || platformSettings.TryGetValue(key, out text))
            {
                if (bool.TryParse(text, out bool flag))
                {
                    return Value.FromBool(flag);
                }

                if (TryParseInteger(text, out long number))
                {
                    return new Value(ValueKind.Number, number, null);
                }

                return enumMembers.TryGetValue("ERHIFeatureSupport::" + text, out int? support) && support is { } value
                    ? new Value(ValueKind.Number, value, null)
                    : null;
            }

            if (key.StartsWith("Supports", StringComparison.Ordinal) || key.StartsWith("Is", StringComparison.Ordinal)
                || key.StartsWith("Requires", StringComparison.Ordinal))
            {
                return Value.FromBool(DefaultTrueSettings.Contains("b" + key));
            }

            return null;
        }

        /// <summary>エンジンの既定値が <c>true</c> の、プラットフォームの設定 (UE 5.5 の <c>FGenericDataDrivenShaderPlatformInfo::SetDefaultValues</c>)。</summary>
        private static readonly HashSet<string> DefaultTrueSettings = new(StringComparer.Ordinal)
        {
            "bSupportsMSAA",
            "bSupportsUnrestrictedHalfFloatBuffers",
            "bSupportsDOFHybridScattering",
            "bSupportsHZBOcclusion",
            "bSupportsWaterIndirectDraw",
            "bSupportsAsyncPipelineCompilation",
            "bSupportsVertexShaderSRVs",
            "bSupportsManualVertexFetch",
            "bSupportsVolumeTextureAtomics",
            "bSupportsClipDistance",
            "bSupportsShaderPipelines",
        };

        /// <summary>値の組を、比べられる値にする。</summary>
        private static Value VectorValue(Dictionary<string, long> vector)
            => new(ValueKind.Vector, 0, string.Join(",", vector.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture))));

        /// <summary>式の中の、値の分かっている名前を置き換える。</summary>
        private static ShaderSyntaxToken[] Substitute(ShaderSyntaxToken[] tokens, Dictionary<string, ShaderSyntaxToken[]> bindings, int depth)
        {
            if (bindings.Count == 0 || depth > MaxCallDepth)
            {
                return tokens;
            }

            List<ShaderSyntaxToken> result = new(tokens.Length);

            for (int i = 0; i < tokens.Length; i++)
            {
                bool qualified = (i > 0 && tokens[i - 1].Kind is ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.DotToken)
                                 || (i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.ColonColonToken);

                if (!qualified && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && bindings.TryGetValue(tokens[i].Text, out ShaderSyntaxToken[]? bound))
                {
                    result.Add(Synthetic(ShaderSyntaxKind.OpenParenToken, "("));
                    result.AddRange(bound);
                    result.Add(Synthetic(ShaderSyntaxKind.CloseParenToken, ")"));
                }
                else
                {
                    result.Add(tokens[i]);
                }
            }

            return [.. result];
        }

        /// <summary>
        /// 値の組の次元を読む式 (<c>PermutationVector.Get&lt;FDim&gt;()</c>、入れ子の組の <c>.Get&lt;FBase&gt;().Get&lt;FDim&gt;()</c>) を、今の組の値に置き換える。
        /// </summary>
        private static ShaderSyntaxToken[] ResolvePermutationGets(ShaderSyntaxToken[] tokens, Context context)
        {
            if (context.Vector is not { } vector)
            {
                return tokens;
            }

            List<ShaderSyntaxToken> result = new(tokens.Length);

            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken && TryReadGetChain(tokens, i, out int chainEnd, out string? dimension)
                    && vector.TryGetValue(dimension, out long value))
                {
                    result.Add(Synthetic(ShaderSyntaxKind.NumericLiteralToken, value.ToString(CultureInfo.InvariantCulture)));
                    i = chainEnd - 1;
                    continue;
                }

                result.Add(tokens[i]);
            }

            return [.. result];
        }

        /// <summary><c>X.Get&lt;A&gt;().Get&lt;B&gt;()</c> を読む。</summary>
        /// <param name="tokens">トークン。</param>
        /// <param name="start">先頭 (X) の位置。</param>
        /// <param name="end">読み終えた次の位置。</param>
        /// <param name="dimension">最後に読む次元のクラスの名前 (修飾を除く)。</param>
        /// <returns>この形なら <see langword="true"/>。</returns>
        private static bool TryReadGetChain(ShaderSyntaxToken[] tokens, int start, out int end, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? dimension)
        {
            end = start + 1;
            dimension = null;

            while (end + 2 < tokens.Length
                   && tokens[end].Kind == ShaderSyntaxKind.DotToken
                   && tokens[end + 1].Text == "Get"
                   && tokens[end + 2].Text == "<")
            {
                int angleEnd = SkipAngles(tokens, end + 2);

                if (angleEnd + 1 >= tokens.Length
                    || tokens[angleEnd].Kind != ShaderSyntaxKind.OpenParenToken
                    || tokens[angleEnd + 1].Kind != ShaderSyntaxKind.CloseParenToken)
                {
                    return false;
                }

                dimension = LastIdentifier(tokens, end + 3, angleEnd - 1);
                end = angleEnd + 2;
            }

            return dimension is not null;
        }

        /// <summary>範囲の中の最後の識別子 (修飾された名前の最後の部分)。</summary>
        private static string LastIdentifier(ShaderSyntaxToken[] tokens, int start, int end)
        {
            for (int i = Math.Min(end, tokens.Length) - 1; i >= start; i--)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    return tokens[i].Text;
                }
            }

            return string.Empty;
        }

        /// <summary>式を評価する。</summary>
        private Value Evaluate(ShaderSyntaxToken[] tokens, Context context, int depth)
        {
            if (depth > MaxCallDepth)
            {
                return Value.Unknown;
            }

            ShaderSyntaxToken[] substituted = ResolvePermutationGets(Substitute(tokens, context.Bindings, 0), context);
            ExpressionReader reader = new(substituted, constants, enumMembers)
            {
                PreferredFile = context.Function.File,
                CallHook = (callTokens, at, arguments) => EvaluateCall(callTokens, at, arguments, context),
                IdentifierHook = name => context.Vector is { } vector && context.VectorNames.Contains(name) ? VectorValue(vector) : null,
            };

            Value value = reader.ReadConditional();
            return reader.AtEnd ? value : Value.Unknown;
        }

        /// <summary>組の次元を集める。入れ子の組もたどる。</summary>
        /// <param name="domain">組。</param>
        /// <param name="axes">次元のクラスの名前と、マクロの名前と、とりうる値を足す先。同じマクロは 1 度だけ足す。</param>
        /// <param name="depth">入れ子の深さ。</param>
        private void CollectDimensions(DomainAlias domain, List<(string Dimension, string Macro, ImmutableArray<int>? Values)> axes, int depth)
        {
            if (depth > MaxCallDepth)
            {
                return;
            }

            foreach ((string? qualifier, string name) in domain.Members)
            {
                if (FindDimension(qualifier, name, domain) is { } dimension)
                {
                    (string macro, ImmutableArray<int>? values) = DimensionValues(dimension);

                    if (macro.Length > 0 && !axes.Exists(a => a.Macro == macro))
                    {
                        axes.Add((dimension.Name, macro, values));
                    }
                }
                else if (FindAliasByName(qualifier, name, domain) is { } nested)
                {
                    CollectDimensions(nested, axes, depth + 1);
                }
            }
        }

        private Dimension? FindDimension(string? qualifier, string name, DomainAlias domain)
        {
            if (!_dimensions.TryGetValue(name, out ConcurrentBag<Dimension>? found))
            {
                return null;
            }

            return found
                .OrderBy(d => qualifier is not null && d.Owner == qualifier ? 0 : 1)
                .ThenBy(d => d.Owner == domain.Owner ? 0 : 1)
                .ThenBy(d => d.File == domain.File ? 0 : 1)
                .ThenBy(d => d.File, StringComparer.Ordinal)
                .First();
        }

        private DomainAlias? FindAliasByName(string? qualifier, string name, DomainAlias from)
        {
            if (!_aliases.TryGetValue(name, out ConcurrentBag<DomainAlias>? found))
            {
                return null;
            }

            return found
                .Where(a => a != from)
                .OrderBy(a => qualifier is not null && a.Owner == qualifier ? 0 : 1)
                .ThenBy(a => a.File == from.File ? 0 : 1)
                .ThenBy(a => a.File, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>クラスの組を探す。クラスに無ければ基底のクラスから探す。</summary>
        private DomainAlias? FindDomain(ClassDeclaration declaration, string name, int depth)
        {
            if (depth > MaxCallDepth)
            {
                return null;
            }

            if (_aliases.TryGetValue(name, out ConcurrentBag<DomainAlias>? found)
                && found.Where(a => a.Owner == declaration.Name).OrderBy(a => a.File == declaration.File ? 0 : 1).FirstOrDefault() is { } own)
            {
                return own;
            }

            foreach (string baseName in declaration.Bases)
            {
                if (FindClass(baseName, declaration.File) is { } baseClass && FindDomain(baseClass, name, depth + 1) is { } inherited)
                {
                    return inherited;
                }
            }

            return null;
        }

        /// <summary>次元のマクロの名前と、とりうる値。</summary>
        private (string Macro, ImmutableArray<int>? Values) DimensionValues(Dimension dimension)
        {
            ImmutableArray<string> arguments = dimension.Arguments;

            if (arguments.IsEmpty || DefineName(arguments[0]) is not { } macro)
            {
                return (string.Empty, null);
            }

            int? Integer(int index)
            {
                if (index >= arguments.Length)
                {
                    return null;
                }

                string text = arguments[index].Trim();

                if (TryParseInteger(text, out long value))
                {
                    return (int)value;
                }

                // 修飾した名前 (LumenRadianceCache::MaxBatchSize) は、定数を名前だけで覚えているので最後の部分で引く。
                return constants.TryGetValue(text, out int? constant) || constants.TryGetValue(LastSegment(text), out constant) ? constant : null;
            }

            ImmutableArray<int>? values = dimension.Macro switch
            {
                "SHADER_PERMUTATION_BOOL" => [0, 1],
                "SHADER_PERMUTATION_INT" when Integer(1) is { } count and > 0 and <= 256 => [.. Enumerable.Range(0, count)],
                "SHADER_PERMUTATION_RANGE_INT" when Integer(1) is { } first && Integer(2) is { } count and > 0 and <= 256 => [.. Enumerable.Range(first, count)],
                "SHADER_PERMUTATION_SPARSE_INT" when arguments.Length > 1 && Enumerable.Range(1, arguments.Length - 1).All(i => Integer(i) is not null)
                    => [.. Enumerable.Range(1, arguments.Length - 1).Select(i => Integer(i)!.Value)],
                "SHADER_PERMUTATION_ENUM_CLASS" when arguments.Length > 1
                                                    && (enums.TryGetValue(InFile(dimension.File, BareClassName(arguments[1])), out int? members)
                                                        || enums.TryGetValue(BareClassName(arguments[1]), out members))
                                                    && members is { } n and > 0
                    => [.. Enumerable.Range(0, n)],
                _ => null,
            };

            return (macro, values);
        }
    }

    /// <summary>定義と、フラグの集まり。</summary>
    private sealed class Environment
    {
        public SortedDictionary<string, string?> Defines { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, string?> Flags { get; } = new(StringComparer.Ordinal);

        /// <summary>値を決める。決まらない値 (<see langword="null"/>) は、それまでの値を上書きして決まらないものにする。</summary>
        public void Set(SortedDictionary<string, string?> target, string name, string? value) => target[name] = value;
    }

    /// <summary>式を読みながら評価する。</summary>
    private sealed class ExpressionReader(
        ShaderSyntaxToken[] tokens,
        ConcurrentDictionary<string, int?> constants,
        ConcurrentDictionary<string, int?> enumMembers)
    {
        private int _position;

        /// <summary>関数の呼び出しを評価するもの (式のトークン、名前の位置、引数)。評価しないなら <see langword="null"/> を返す。</summary>
        public Func<ShaderSyntaxToken[], int, List<ShaderSyntaxToken[]>, Value?>? CallHook { get; init; }

        /// <summary>修飾の無い名前を評価するもの (値の組を指す名前)。評価しないなら <see langword="null"/> を返す。</summary>
        public Func<string, Value?>? IdentifierHook { get; init; }

        /// <summary>列挙を先に探すファイル (式を書いた関数のファイル)。</summary>
        public string? PreferredFile { get; init; }

        public bool AtEnd => _position >= tokens.Length;

        private ShaderSyntaxToken? Peek => _position < tokens.Length ? tokens[_position] : null;

        public Value ReadConditional()
        {
            Value condition = ReadOr();

            if (Peek?.Kind != ShaderSyntaxKind.QuestionToken)
            {
                return condition;
            }

            _position++;
            Value whenTrue = ReadConditional();

            if (Peek?.Kind != ShaderSyntaxKind.ColonToken)
            {
                _position = tokens.Length + 1;
                return Value.Unknown;
            }

            _position++;
            Value whenFalse = ReadConditional();

            return condition.AsBool switch
            {
                true => whenTrue,
                false => whenFalse,
                _ => whenTrue == whenFalse ? whenTrue : Value.Unknown,
            };
        }

        private Value ReadOr()
        {
            Value left = ReadAnd();

            while (Peek?.Kind == ShaderSyntaxKind.BarBarToken)
            {
                _position++;
                Value right = ReadAnd();
                left = left.AsBool == true || right.AsBool == true ? Value.FromBool(true)
                    : left.AsBool == false && right.AsBool == false ? Value.FromBool(false)
                    : Value.Unknown;
            }

            return left;
        }

        private Value ReadAnd()
        {
            Value left = ReadEquality();

            while (Peek?.Kind == ShaderSyntaxKind.AmpersandAmpersandToken)
            {
                _position++;
                Value right = ReadEquality();
                left = left.AsBool == false || right.AsBool == false ? Value.FromBool(false)
                    : left.AsBool == true && right.AsBool == true ? Value.FromBool(true)
                    : Value.Unknown;
            }

            return left;
        }

        private Value ReadEquality()
        {
            Value left = ReadUnary();

            while (Peek?.Kind is ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken)
            {
                bool equals = Peek!.Kind == ShaderSyntaxKind.EqualsEqualsToken;
                _position++;
                Value right = ReadUnary();

                bool? same = (left.Kind, right.Kind) switch
                {
                    (ValueKind.Number, ValueKind.Number) => left.Number == right.Number,
                    (ValueKind.Vector, ValueKind.Vector) => left.Text == right.Text,
                    // 修飾の書き方は違いうる (Lumen::E::Inline と E::Inline)。最後の名前で比べる。
                    (ValueKind.Symbol, ValueKind.Symbol) => LastSegment(left.Text!) == LastSegment(right.Text!),
                    _ => null,
                };

                left = same is { } s ? Value.FromBool(s == equals) : Value.Unknown;
            }

            return left;
        }

        private Value ReadUnary()
        {
            if (Peek?.Kind == ShaderSyntaxKind.ExclamationToken)
            {
                _position++;
                Value operand = ReadUnary();
                return operand.AsBool is { } b ? Value.FromBool(!b) : Value.Unknown;
            }

            return ReadPrimary();
        }

        private Value ReadPrimary()
        {
            if (Peek is not { } token)
            {
                return Value.Unknown;
            }

            // 型の変換: (uint32)Mode、(ELightSourceMode)0。括弧の中が型の名前だけで、後ろに値が続く形。
            if (token.Kind == ShaderSyntaxKind.OpenParenToken && TrySkipCast())
            {
                return ReadUnary();
            }

            if (token.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                _position++;
                Value inner = ReadConditional();

                if (Peek?.Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    _position++;
                    return inner;
                }

                _position = tokens.Length + 1;
                return Value.Unknown;
            }

            if (token.Kind == ShaderSyntaxKind.NumericLiteralToken)
            {
                _position++;

                if (TryParseInteger(token.Text, out long number))
                {
                    return new Value(ValueKind.Number, number, null);
                }

                // 小数 (SetDefine(TEXT("TILE_VERTEX_SHADER"), 1.0f))。エンジンは %f で書く (1.000000)。
                return double.TryParse(token.Text.TrimEnd('f', 'F', 'h', 'H'), NumberStyles.Float, CultureInfo.InvariantCulture, out double real)
                    ? new Value(ValueKind.String, 0, real.ToString("F6", CultureInfo.InvariantCulture))
                    : Value.Unknown;
            }

            if (token.Kind == ShaderSyntaxKind.StringLiteralToken)
            {
                _position++;
                return new Value(ValueKind.String, 0, Unquote(token.Text));
            }

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                // 読めない式。残りを読まずに、決まらないものにする。
                _position = tokens.Length + 1;
                return Value.Unknown;
            }

            if (token.Text is "true" or "false")
            {
                _position++;
                return Value.FromBool(token.Text == "true");
            }

            // TEXT("1")
            if (token.Text == "TEXT" && _position + 3 < tokens.Length + 1
                && _position + 2 < tokens.Length
                && tokens[_position + 1].Kind == ShaderSyntaxKind.OpenParenToken
                && tokens[_position + 2].Kind == ShaderSyntaxKind.StringLiteralToken
                && _position + 3 < tokens.Length && tokens[_position + 3].Kind == ShaderSyntaxKind.CloseParenToken)
            {
                string text = Unquote(tokens[_position + 2].Text);
                _position += 4;
                return new Value(ValueKind.String, 0, text);
            }

            // 修飾された名前 (A::B::C)。
            StringBuilder name = new(token.Text);
            _position++;

            while (_position + 1 < tokens.Length
                   && tokens[_position].Kind == ShaderSyntaxKind.ColonColonToken
                   && tokens[_position + 1].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                name.Append("::").Append(tokens[_position + 1].Text);
                _position += 2;
            }

            // 呼び出しは、評価できるもの (ShouldCompilePermutation、プラットフォームを問う関数など) だけを評価する。
            if (Peek?.Kind == ShaderSyntaxKind.OpenParenToken && CallHook is { } call)
            {
                int nameAt = _position - 1;
                int close = SkipParens(tokens, _position);
                List<ShaderSyntaxToken[]> arguments = SplitTokens(tokens, _position + 1, close - 1);

                if (call(tokens, nameAt, arguments) is { } called)
                {
                    _position = close;
                    return called;
                }
            }

            // ほかの呼び出しやメンバーは評価しない。
            if (Peek?.Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenBracketToken
                || (Peek?.Kind == ShaderSyntaxKind.MinusToken && _position + 1 < tokens.Length && tokens[_position + 1].Kind == ShaderSyntaxKind.GreaterThanToken))
            {
                _position = tokens.Length + 1;
                return Value.Unknown;
            }

            string full = name.ToString();

            if (constants.TryGetValue(full, out int? constant) && constant is { } c)
            {
                return new Value(ValueKind.Number, c, null);
            }

            // 列挙のメンバー (E::Member、名前空間で修飾したものは最後の 2 つ。修飾の無い列挙は名前だけ)。
            // パーミュテーションの値と比べられるよう、整数にする。
            string memberKey = LastTwoSegments(full) ?? full;

            if ((PreferredFile is { } file && enumMembers.TryGetValue(InFile(file, memberKey), out int? memberValue)
                 || enumMembers.TryGetValue(memberKey, out memberValue))
                && memberValue is { } m)
            {
                return new Value(ValueKind.Number, m, null);
            }

            if (!full.Contains("::", StringComparison.Ordinal) && IdentifierHook?.Invoke(full) is { } identified)
            {
                return identified;
            }

            // 列挙の値 (E::A、または列挙の型の名前を含むもの) は、名前どうしで比べる。
            // 修飾の無い名前は変数かもしれないので、決まらないものにする。
            return full.Contains("::", StringComparison.Ordinal)
                ? new Value(ValueKind.Symbol, 0, full)
                : Value.Unknown;
        }

        /// <summary>型の変換の括弧 (<c>(uint32)</c>、<c>(Lumen::EMode)</c>) なら読み飛ばす。</summary>
        /// <returns>読み飛ばしたら <see langword="true"/>。</returns>
        private bool TrySkipCast()
        {
            int i = _position + 1;

            if (i >= tokens.Length || tokens[i].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                return false;
            }

            i++;

            while (i + 1 < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.ColonColonToken && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                i += 2;
            }

            // 後ろに値 (名前、数、括弧、否定) が続くときだけ、型の変換と読む。(A) || B の (A) は変換ではない。
            if (i + 1 >= tokens.Length
                || tokens[i].Kind != ShaderSyntaxKind.CloseParenToken
                || tokens[i + 1].Kind is not (ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.ExclamationToken))
            {
                return false;
            }

            _position = i + 1;
            return true;
        }

        /// <summary>修飾された名前の最後の 2 つ (<c>A::E::M</c> → <c>E::M</c>)。修飾が無ければ <see langword="null"/>。</summary>
        private static string? LastTwoSegments(string qualified)
        {
            int last = qualified.LastIndexOf("::", StringComparison.Ordinal);

            if (last < 0)
            {
                return null;
            }

            int previous = qualified.LastIndexOf("::", last - 1, StringComparison.Ordinal);
            return previous < 0 ? qualified : qualified[(previous + 2)..];
        }
    }

    // ------------------------------------------------------------------
    // 書き出しと読み込み
    // ------------------------------------------------------------------

    /// <summary>クラスの一覧を書き出す。</summary>
    /// <param name="outputDirectory">書き出す先 (生成のフォルダー)。</param>
    /// <param name="classes">クラス。</param>
    /// <returns>書き出したファイル。</returns>
    internal static string Write(string outputDirectory, IEnumerable<UnrealShaderClass> classes)
    {
        StringBuilder text = new();
        text.Append("# Shaderlyn が C++ のシェーダーのクラスから作った。クラス\t種類 (define / permutation / flag / varies / exclude)\t名前\t値 (? は決まらない)\n");
        text.Append("# varies はパーミュテーションの組で値の変わる定義とフラグ。exclude は、コンパイルされる組では同時に現れない 2 つの値。\n");

        foreach (UnrealShaderClass shaderClass in classes.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            // 定義もパーミュテーションも無いクラスも書く (見つかったことを残す)。
            text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tclass\t\t\n");

            if (shaderClass.NeverCompiled)
            {
                text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tnever\t\t\n");
            }

            foreach ((string name, string? value) in shaderClass.Defines)
            {
                text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tdefine\t{name}\t{value ?? UnknownValue}\n");
            }

            foreach ((string name, ImmutableArray<int>? values) in shaderClass.Permutations)
            {
                string written = values is { } v ? string.Join(' ', v.Select(i => i.ToString(CultureInfo.InvariantCulture))) : UnknownValue;
                text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tpermutation\t{name}\t{written}\n");
            }

            foreach ((string name, string? value) in shaderClass.CompilerFlags)
            {
                text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tflag\t{name}\t{value ?? UnknownValue}\n");
            }

            foreach ((string name, ImmutableArray<int> values) in shaderClass.Varying)
            {
                text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tvaries\t{name}\t{string.Join(' ', values.Select(i => i.ToString(CultureInfo.InvariantCulture)))}\n");
            }

            foreach ((string left, string right) in shaderClass.Exclusions)
            {
                text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\texclude\t{left}\t{right}\n");
            }
        }

        string file = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(file, text.ToString());
        return file;
    }

    /// <summary>書き出したクラスの一覧を読む。</summary>
    /// <param name="generatedDirectory">生成のフォルダー。</param>
    /// <returns>クラスの名前からクラス。一覧が無ければ空。</returns>
    public static ImmutableDictionary<string, UnrealShaderClass> Read(string generatedDirectory)
    {
        ArgumentNullException.ThrowIfNull(generatedDirectory);
        string file = Path.Combine(generatedDirectory, FileName);
        string[] lines;

        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            return ImmutableDictionary<string, UnrealShaderClass>.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ImmutableDictionary<string, UnrealShaderClass>.Empty;
        }

        Dictionary<string, (SortedDictionary<string, string?> Defines, SortedDictionary<string, ImmutableArray<int>?> Permutations, SortedDictionary<string, string?> Flags,
            SortedDictionary<string, ImmutableArray<int>> Varying, List<KeyValuePair<string, string>> Exclusions)> classes =
            new(StringComparer.Ordinal);
        HashSet<string> never = new(StringComparer.Ordinal);

        foreach (string[] parts in lines.Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).Where(p => p.Length >= 4))
        {
            if (!classes.TryGetValue(parts[0], out var entry))
            {
                entry = (new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), []);
                classes.Add(parts[0], entry);
            }

            string? value = parts[3] == UnknownValue ? null : parts[3];

            switch (parts[1])
            {
                case "define":
                    entry.Defines[parts[2]] = value;
                    break;
                case "flag":
                    entry.Flags[parts[2]] = value;
                    break;
                case "permutation":
                    entry.Permutations[parts[2]] = value is null
                        ? null
                        : [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture))];
                    break;
                case "varies" when value is not null:
                    entry.Varying[parts[2]] = [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture))];
                    break;
                case "exclude":
                    entry.Exclusions.Add(new(parts[2], parts[3]));
                    break;
                case "never":
                    never.Add(parts[0]);
                    break;
            }
        }

        return classes.ToImmutableDictionary(
            p => p.Key,
            p => new UnrealShaderClass(
                p.Key,
                p.Value.Defines.ToImmutableSortedDictionary(StringComparer.Ordinal),
                p.Value.Permutations.ToImmutableSortedDictionary(StringComparer.Ordinal),
                p.Value.Flags.ToImmutableSortedDictionary(StringComparer.Ordinal))
            {
                Varying = p.Value.Varying.ToImmutableSortedDictionary(StringComparer.Ordinal),
                Exclusions = [.. p.Value.Exclusions],
                NeverCompiled = never.Contains(p.Key),
            },
            StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------
    // トークンの補助
    // ------------------------------------------------------------------

    /// <summary>波括弧の対応 (開きの位置に閉じの位置、ほかは -1)。</summary>
    private static int[] MatchBraces(ShaderSyntaxToken[] tokens)
    {
        int[] match = new int[tokens.Length];
        Array.Fill(match, -1);
        Stack<int> open = new();

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                open.Push(i);
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseBraceToken && open.Count > 0)
            {
                match[open.Pop()] = i;
            }
        }

        return match;
    }

    /// <summary>開き括弧の位置から、対応する閉じ括弧の次の位置を返す。</summary>
    private static int SkipParens(ShaderSyntaxToken[] tokens, int open)
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

    /// <summary><c>&lt;</c> の位置から、対応する <c>&gt;</c> の次の位置を返す。</summary>
    private static int SkipAngles(ShaderSyntaxToken[] tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            depth += tokens[i].Text switch
            {
                "<" => 1,
                ">" => -1,
                ">>" => -2,
                _ => 0,
            };

            if (depth <= 0 && i > open)
            {
                return i + 1;
            }

            if (tokens[i].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken)
            {
                return i;
            }
        }

        return tokens.Length;
    }

    /// <summary>文の終わり (次の文の先頭) を返す。</summary>
    private static int StatementEnd(ShaderSyntaxToken[] body, int[] match, int start, int end)
    {
        if (start >= end)
        {
            return end;
        }

        if (body[start].Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            return match[start] < 0 ? end : Math.Min(end, match[start] + 1);
        }

        if (body[start].Text is "if" or "for" or "while" or "switch")
        {
            int after = start + 1 < end && body[start + 1].Kind == ShaderSyntaxKind.OpenParenToken ? SkipParens(body, start + 1) : start + 1;
            int inner = StatementEnd(body, match, after, end);

            return body[start].Text == "if" && inner < end && body[inner].Text == "else"
                ? StatementEnd(body, match, inner + 1, end)
                : inner;
        }

        int parens = 0;

        for (int i = start; i < end; i++)
        {
            switch (body[i].Kind)
            {
                case ShaderSyntaxKind.OpenParenToken:
                    parens++;
                    break;
                case ShaderSyntaxKind.CloseParenToken:
                    parens--;
                    break;
                case ShaderSyntaxKind.OpenBraceToken when match[i] >= 0:
                    // ラムダや初期化子の { } は文の中にある。
                    i = match[i];
                    break;
                case ShaderSyntaxKind.SemicolonToken when parens <= 0:
                    return i + 1;
            }
        }

        return end;
    }

    /// <summary>括弧の中を、最上位のカンマで分ける。</summary>
    private static List<ShaderSyntaxToken[]> SplitTokens(ShaderSyntaxToken[] tokens, int start, int end)
    {
        List<ShaderSyntaxToken[]> parts = [];
        int depth = 0;
        int partStart = start;

        for (int i = start; i < end; i++)
        {
            switch (tokens[i].Kind)
            {
                case ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.OpenBracketToken:
                    depth++;
                    break;
                case ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.CloseBracketToken:
                    depth--;
                    break;
                case ShaderSyntaxKind.CommaToken when depth == 0:
                    parts.Add(tokens[partStart..i]);
                    partStart = i + 1;
                    break;
            }
        }

        parts.Add(tokens[partStart..Math.Max(partStart, end)]);
        return parts;
    }

    /// <summary>括弧の中を最上位のカンマで分け、それぞれを文字列にする (識別子と数の間だけ空白を入れる)。</summary>
    private static List<string> SplitStrings(ShaderSyntaxToken[] tokens, int start, int end)
        => [.. SplitTokens(tokens, start, end).Select(part =>
        {
            StringBuilder text = new();

            for (int i = 0; i < part.Length; i++)
            {
                if (i > 0 && IsWord(part[i - 1]) && IsWord(part[i]))
                {
                    text.Append(' ');
                }

                text.Append(part[i].Text);
            }

            return text.ToString();
        })];

    private static bool IsWord(ShaderSyntaxToken token)
        => token.Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken;

    /// <summary>マクロの名前の引数 (<c>TEXT("X")</c> か <c>"X"</c>) から名前を取る。</summary>
    private static string? DefineName(ShaderSyntaxToken[] argument)
        => argument.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.StringLiteralToken) is { } literal ? Unquote(literal.Text) : null;

    /// <summary>マクロの名前の引数 (<c>TEXT("X")</c> か <c>"X"</c>) から名前を取る。</summary>
    private static string? DefineName(string argument)
    {
        int open = argument.IndexOf('"', StringComparison.Ordinal);
        int close = open >= 0 ? argument.IndexOf('"', open + 1) : -1;
        return close > open + 1 ? argument[(open + 1)..close] : null;
    }

    private static string LastSegment(string qualified)
    {
        int scope = qualified.LastIndexOf("::", StringComparison.Ordinal);
        return scope >= 0 ? qualified[(scope + 2)..] : qualified;
    }

    private static string Unquote(string literal)
    {
        int open = literal.IndexOf('"', StringComparison.Ordinal);
        int close = literal.LastIndexOf('"');
        return close > open ? literal[(open + 1)..close] : literal;
    }

    private static bool TryParseInteger(string text, out long value)
    {
        string trimmed = text.TrimEnd('u', 'U', 'l', 'L');

        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static readonly SourceText SyntheticSource = SourceText.From(string.Empty);

    /// <summary>短い C++ の断片 (テンプレートの実引数) をトークンに分ける。</summary>
    private static ShaderSyntaxToken[] Lex(string text)
        => [.. new ShaderLexer(SourceText.From(text)).Lex(out _).Where(t => t.Kind != ShaderSyntaxKind.EndOfFileToken)];

    private static ShaderSyntaxToken Synthetic(ShaderSyntaxKind kind, string text)
        => new(kind, SyntheticSource, default, text);

    /// <summary>行の継続 (<c>\</c> と改行) をつなげた行を返す。</summary>
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

    /// <summary>クラスを作るマクロの定義 (本体に <c>class</c> と <c>ModifyCompilationEnvironment</c> か次元がある)。</summary>
    [GeneratedRegex("""^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\((?=.*\bclass\b)(?=.*(?:ModifyCompilationEnvironment|SHADER_PERMUTATION_|PermutationDomain))""", RegexOptions.CultureInvariant)]
    private static partial Regex ClassMacro();

    [GeneratedRegex("""^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)\(""", RegexOptions.CultureInvariant)]
    private static partial Regex MacroDefinition();

    /// <summary>列挙の宣言 (<c>enum class E : uint8 { … }</c> と、修飾の無い <c>enum E { … }</c>)。<c>scoped</c> は <c>class</c> があること。</summary>
    [GeneratedRegex("""\benum\s+(?<scoped>(?:class|struct)\s+)?(?<name>\w+)\s*(?::\s*[\w:]+\s*)?\{(?<body>[^{}]*)\}""", RegexOptions.CultureInvariant)]
    private static partial Regex EnumClass();

    /// <summary>シェーダープラットフォームだけを受け取る、本体が 1 段の <c>inline bool</c> 関数。</summary>
    [GeneratedRegex("""\binline\s+bool\s+(?<name>[A-Za-z_]\w*)\s*\(\s*(?:const\s+)?(?:FStaticShaderPlatform|EShaderPlatform)\s+\w+\s*\)\s*\{(?<body>[^{}]*)\}""", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformHelper();

    /// <summary><see cref="PlatformHelper"/> の本体が、<c>return 式;</c> だけであること。</summary>
    [GeneratedRegex("""^\s*return\s+(?<expression>[^;]+);\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformHelperReturn();

    /// <summary>C++ のコメント (<c>// …</c> と <c>/* … */</c>)。</summary>
    [GeneratedRegex("""//[^\n]*|/\*.*?\*/""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    /// <summary>整数の定数 (<c>constexpr int32 X = 3;</c>、<c>static constexpr int32 X = 3;</c>、<c>const static uint32 X = 3;</c>)。</summary>
    [GeneratedRegex("""\b(?:static\s+)?(?:constexpr|const)\s+(?:static\s+)?(?:u?int(?:8|16|32|64)?|unsigned|int)\s+(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>\d+)\s*;""", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerConstant();

    /// <summary>整数に定義したマクロ (<c>#define NAME 0</c>、<c>#define NAME (0)</c>、<c>#define NAME 0u</c>)。</summary>
    [GeneratedRegex("""^[ \t]*#[ \t]*define[ \t]+(?<name>[A-Za-z_]\w*)[ \t]+\(?[ \t]*(?<value>\d+)[uU]?[ \t]*\)?[ \t]*(?://.*)?\r?$""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IntegerDefine();
}

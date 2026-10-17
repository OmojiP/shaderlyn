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
    ImmutableSortedDictionary<string, string?> CompilerFlags);

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

        /// <summary>列挙 (<c>enum class</c>) の名前から、メンバーの数 (値を書いたメンバーがあれば <see langword="null"/>)。</summary>
        private readonly ConcurrentDictionary<string, int?> _enums = new(StringComparer.Ordinal);

        /// <summary>整数の定数の名前から値 (同じ名前で値が違えば <see langword="null"/>)。</summary>
        private readonly ConcurrentDictionary<string, int?> _constants = new(StringComparer.Ordinal);

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
                string body = match.Groups["body"].Value;
                string[] members = [.. body.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0)];

                // SHADER_PERMUTATION_ENUM_CLASS は 0 から MAX の手前までを使う。最後のメンバーが数を表すなら除く。
                int? count = members.Any(m => m.Contains('=', StringComparison.Ordinal))
                    ? null
                    : members.Length > 0 && members[^1] is "MAX" or "Max" or "Num" or "NUM" or "Count" ? members.Length - 1 : members.Length;
                _enums.AddOrUpdate(match.Groups["name"].Value, count, (_, old) => old == count ? old : null);
            }

            foreach (Match match in IntegerConstant().Matches(text))
            {
                int? value = int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
                _constants.AddOrUpdate(match.Groups["name"].Value, value, (_, old) => old == value ? old : null);
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
            Model model = new(_enums, _constants);
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

            foreach (string name in classNames.Select(BareClassName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
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
    private sealed record ClassDeclaration(string Name, string File, ImmutableArray<string> Bases);

    /// <summary>パーミュテーションの次元 (<c>class FDim : SHADER_PERMUTATION_BOOL("X");</c>)。</summary>
    private sealed record Dimension(string Name, string? Owner, string File, string Macro, ImmutableArray<string> Arguments);

    /// <summary>パーミュテーションの組 (<c>using FPermutationDomain = TShaderPermutationDomain&lt;…&gt;;</c>)。</summary>
    private sealed record DomainAlias(string Name, string? Owner, string File, ImmutableArray<(string? Qualifier, string Name)> Members);

    /// <summary><c>ModifyCompilationEnvironment</c> の定義 1 つ。</summary>
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
    }

    /// <summary>集めた宣言と、クラスの評価。</summary>
    private sealed class Model(ConcurrentDictionary<string, int?> enums, ConcurrentDictionary<string, int?> constants)
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<ClassDeclaration>> _classes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentBag<Dimension>> _dimensions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentBag<DomainAlias>> _aliases = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentBag<ModifyFunction>> _functions = new(StringComparer.Ordinal);

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

            for (int i = 0; i < tokens.Length; i++)
            {
                while (enclosing.Count > 0 && i > enclosing[^1].End)
                {
                    enclosing.RemoveAt(enclosing.Count - 1);
                }

                string? owner = enclosing.Count > 0 ? enclosing[^1].Name : null;
                string word = tokens[i].Text;

                if ((word == "class" || word == "struct") && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    if (ReadClass(path, tokens, match, i, owner, out int bodyOpen, out string? name) && name is not null)
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
                    ReadFunction(path, tokens, match, i, owner);
                }
            }
        }

        /// <summary><c>class</c> の宣言を読む。次元なら次元として覚える。</summary>
        /// <returns>本体のあるクラスなら <see langword="true"/>。</returns>
        private bool ReadClass(string path, ShaderSyntaxToken[] tokens, int[] match, int start, string? owner, out int bodyOpen, out string? name)
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

            _classes.GetOrAdd(name, _ => []).Add(new ClassDeclaration(name, path, [.. bases]));
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

        /// <summary><c>ModifyCompilationEnvironment</c> の定義を読む。呼び出しや宣言だけなら何もしない。</summary>
        private void ReadFunction(string path, ShaderSyntaxToken[] tokens, int[] match, int at, string? enclosing)
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
            _functions.GetOrAdd(owner, _ => []).Add(new ModifyFunction(owner, path, names, tokens[(body + 1)..match[body]]));
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

        /// <summary>クラスのマクロとパーミュテーションを求める。</summary>
        /// <param name="name">クラスの名前。</param>
        /// <returns>求めたもの。クラスが見つからなければ <see langword="null"/>。</returns>
        public UnrealShaderClass? Evaluate(string name)
        {
            if (!_classes.TryGetValue(name, out ConcurrentBag<ClassDeclaration>? declarations))
            {
                return null;
            }

            ClassDeclaration declaration = declarations.OrderBy(d => d.File, StringComparer.Ordinal).First();
            Environment environment = new();

            if (FindEntryFunction(declaration, 0) is { } entry)
            {
                Execute(entry.Function, entry.Class, [], environment, certain: true, depth: 0);
            }

            SortedDictionary<string, ImmutableArray<int>?> permutations = new(StringComparer.Ordinal);

            if (FindDomain(declaration, "FPermutationDomain", 0) is { } domain)
            {
                CollectDimensions(domain, permutations, 0);
            }

            return new UnrealShaderClass(
                name,
                environment.Defines.ToImmutableSortedDictionary(StringComparer.Ordinal),
                permutations.ToImmutableSortedDictionary(StringComparer.Ordinal),
                environment.Flags.ToImmutableSortedDictionary(StringComparer.Ordinal));
        }

        /// <summary>クラスの宣言を探す。同じ名前が複数あれば、指定したファイルのものを選ぶ。</summary>
        private ClassDeclaration? FindClass(string name, string? preferredFile)
            => _classes.TryGetValue(name, out ConcurrentBag<ClassDeclaration>? found)
                ? found.OrderBy(d => d.File == preferredFile ? 0 : 1).ThenBy(d => d.File, StringComparer.Ordinal).First()
                : null;

        /// <summary>
        /// エンジンが呼ぶ <c>ModifyCompilationEnvironment</c> を探す。クラスに無ければ、基底のクラスのものを使う (C++ の名前の探し方)。
        /// </summary>
        private (ModifyFunction Function, ClassDeclaration Class)? FindEntryFunction(ClassDeclaration declaration, int depth)
        {
            if (depth > MaxCallDepth)
            {
                return null;
            }

            if (_functions.TryGetValue(declaration.Name, out ConcurrentBag<ModifyFunction>? functions))
            {
                // エンジンが呼ぶのは (Parameters, OutEnvironment) の 2 つを受け取るもの。
                ModifyFunction? entry = functions
                    .Where(f => f.Parameters.Length == 2)
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
                if (FindClass(baseName, declaration.File) is { } baseClass && FindEntryFunction(baseClass, depth + 1) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>関数を探す。クラスに無ければ基底のクラスから探す。</summary>
        private (ModifyFunction Function, ClassDeclaration Class)? FindFunction(string className, string? preferredFile, int argumentCount, ModifyFunction? exclude, int depth)
        {
            if (depth > MaxCallDepth || FindClass(className, preferredFile) is not { } declaration)
            {
                // クラスとして宣言していない名前空間の関数 (FForwardLightingParameters::ModifyCompilationEnvironment など) も探す。
                return _functions.TryGetValue(className, out ConcurrentBag<ModifyFunction>? loose)
                       && loose.FirstOrDefault(f => f != exclude && f.Parameters.Length == argumentCount) is { } function
                    ? (function, new ClassDeclaration(className, function.File, []))
                    : null;
            }

            if (_functions.TryGetValue(declaration.Name, out ConcurrentBag<ModifyFunction>? functions)
                && functions
                    .Where(f => f != exclude && f.Parameters.Length == argumentCount)
                    .OrderBy(f => f.File == declaration.File ? 0 : 1)
                    .FirstOrDefault() is { } own)
            {
                return (own, declaration);
            }

            foreach (string baseName in declaration.Bases)
            {
                if (FindFunction(baseName, declaration.File, argumentCount, exclude, depth + 1) is { } found)
                {
                    return found;
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
        private void Execute(
            ModifyFunction function,
            ClassDeclaration owner,
            Dictionary<string, ShaderSyntaxToken[]> bindings,
            Environment environment,
            bool certain,
            int depth)
        {
            if (depth > MaxCallDepth)
            {
                return;
            }

            ShaderSyntaxToken[] body = function.Body;
            int[] match = MatchBraces(body);
            Context context = new(function, owner, new(bindings, StringComparer.Ordinal), environment, depth);
            bool returned = false;
            ExecuteRange(body, match, 0, body.Length, context, certain, ref returned);
        }

        /// <summary>実行の文脈。</summary>
        private sealed record Context(
            ModifyFunction Function,
            ClassDeclaration Owner,
            Dictionary<string, ShaderSyntaxToken[]> Bindings,
            Environment Environment,
            int Depth);

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

        /// <summary>式の文を実行する (定義、フラグ、呼び出し、ローカル変数)。</summary>
        private void ExecuteStatement(ShaderSyntaxToken[] statement, Context context, bool certain)
        {
            for (int i = 0; i < statement.Length; i++)
            {
                string text = statement[i].Text;

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

            // ローカル変数: const bool bInline = DispatchType == E::Inline;
            int equals = Array.FindIndex(statement, t => t.Kind == ShaderSyntaxKind.EqualsToken);

            if (equals >= 2 && statement[equals - 1].Kind == ShaderSyntaxKind.IdentifierToken
                && statement[equals - 2].Kind == ShaderSyntaxKind.IdentifierToken
                && !statement[..equals].Any(t => t.Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken))
            {
                int end = statement.Length > 0 && statement[^1].Kind == ShaderSyntaxKind.SemicolonToken ? statement.Length - 1 : statement.Length;
                ShaderSyntaxToken[] initializer = Substitute(statement[(equals + 1)..end], context.Bindings, 0);

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
            (ModifyFunction Function, ClassDeclaration Class)? target;
            int count = arguments.Count == 1 && arguments[0].Length == 0 ? 0 : arguments.Count;

            if (at >= 2 && statement[at - 1].Kind == ShaderSyntaxKind.ColonColonToken)
            {
                string qualifier = statement[at - 2].Text;

                target = qualifier == "Super"
                    ? context.Owner.Bases.Select(b => FindFunction(b, context.Owner.File, count, null, context.Depth + 1)).FirstOrDefault(f => f is not null)
                    : FindFunction(qualifier, context.Owner.File, count, context.Function, context.Depth + 1);
            }
            else if (at >= 1 && statement[at - 1].Kind is ShaderSyntaxKind.DotToken)
            {
                return;
            }
            else
            {
                target = FindFunction(context.Owner.Name, context.Owner.File, count, context.Function, context.Depth + 1);
            }

            if (target is not { } found)
            {
                return;
            }

            Dictionary<string, ShaderSyntaxToken[]> bindings = new(StringComparer.Ordinal);

            for (int k = 0; k < count && k < found.Function.Parameters.Length; k++)
            {
                bindings[found.Function.Parameters[k]] = Substitute(arguments[k], context.Bindings, 0);
            }

            Execute(found.Function, found.Class, bindings, context.Environment, certain, context.Depth + 1);
        }

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

        /// <summary>式を評価する。</summary>
        private Value Evaluate(ShaderSyntaxToken[] tokens, Context context, int depth)
        {
            if (depth > MaxCallDepth)
            {
                return Value.Unknown;
            }

            ShaderSyntaxToken[] substituted = Substitute(tokens, context.Bindings, 0);
            ExpressionReader reader = new(substituted, constants);
            Value value = reader.ReadConditional();
            return reader.AtEnd ? value : Value.Unknown;
        }

        /// <summary>組の次元を集める。入れ子の組もたどる。</summary>
        private void CollectDimensions(DomainAlias domain, SortedDictionary<string, ImmutableArray<int>?> permutations, int depth)
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

                    if (macro.Length > 0)
                    {
                        permutations[macro] = values;
                    }
                }
                else if (FindAliasByName(qualifier, name, domain) is { } nested)
                {
                    CollectDimensions(nested, permutations, depth + 1);
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

                return constants.TryGetValue(text, out int? constant) ? constant : null;
            }

            ImmutableArray<int>? values = dimension.Macro switch
            {
                "SHADER_PERMUTATION_BOOL" => [0, 1],
                "SHADER_PERMUTATION_INT" when Integer(1) is { } count and > 0 and <= 256 => [.. Enumerable.Range(0, count)],
                "SHADER_PERMUTATION_RANGE_INT" when Integer(1) is { } first && Integer(2) is { } count and > 0 and <= 256 => [.. Enumerable.Range(first, count)],
                "SHADER_PERMUTATION_SPARSE_INT" when arguments.Length > 1 && Enumerable.Range(1, arguments.Length - 1).All(i => Integer(i) is not null)
                    => [.. Enumerable.Range(1, arguments.Length - 1).Select(i => Integer(i)!.Value)],
                "SHADER_PERMUTATION_ENUM_CLASS" when arguments.Length > 1
                                                    && enums.TryGetValue(BareClassName(arguments[1]), out int? members) && members is { } n and > 0
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
    private sealed class ExpressionReader(ShaderSyntaxToken[] tokens, ConcurrentDictionary<string, int?> constants)
    {
        private int _position;

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
                return TryParseInteger(token.Text, out long number) ? new Value(ValueKind.Number, number, null) : Value.Unknown;
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

            // 呼び出しやメンバーは評価しない。
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

            // 列挙の値 (E::A、または列挙の型の名前を含むもの) は、名前どうしで比べる。
            // 修飾の無い名前は変数かもしれないので、決まらないものにする。
            return full.Contains("::", StringComparison.Ordinal)
                ? new Value(ValueKind.Symbol, 0, full)
                : Value.Unknown;
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
        text.Append("# Shaderlyn が C++ のシェーダーのクラスから作った。クラス\t種類 (define / permutation / flag)\t名前\t値 (? は決まらない)\n");

        foreach (UnrealShaderClass shaderClass in classes.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            // 定義もパーミュテーションも無いクラスも書く (見つかったことを残す)。
            text.Append(CultureInfo.InvariantCulture, $"{shaderClass.Name}\tclass\t\t\n");

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

        Dictionary<string, (SortedDictionary<string, string?> Defines, SortedDictionary<string, ImmutableArray<int>?> Permutations, SortedDictionary<string, string?> Flags)> classes =
            new(StringComparer.Ordinal);

        foreach (string[] parts in lines.Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).Where(p => p.Length >= 4))
        {
            if (!classes.TryGetValue(parts[0], out var entry))
            {
                entry = (new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
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
            }
        }

        return classes.ToImmutableDictionary(
            p => p.Key,
            p => new UnrealShaderClass(
                p.Key,
                p.Value.Defines.ToImmutableSortedDictionary(StringComparer.Ordinal),
                p.Value.Permutations.ToImmutableSortedDictionary(StringComparer.Ordinal),
                p.Value.Flags.ToImmutableSortedDictionary(StringComparer.Ordinal)),
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

    [GeneratedRegex("""\benum\s+class\s+(?<name>\w+)\s*(?::\s*[\w:]+\s*)?\{(?<body>[^{}]*)\}""", RegexOptions.CultureInvariant)]
    private static partial Regex EnumClass();

    [GeneratedRegex("""\b(?:constexpr|const)\s+(?:static\s+)?(?:u?int(?:8|16|32|64)?|unsigned|int)\s+(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>\d+)\s*;""", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerConstant();
}

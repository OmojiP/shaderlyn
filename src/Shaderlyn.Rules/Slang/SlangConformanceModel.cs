using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の型が、どのインターフェイスに適合するか。利用者の宣言 (継承の並び・extension・インターフェイスの継承) と、標準ライブラリの表 (<see cref="SlangStandardConformance"/>) から決める。
/// </summary>
/// <remarks>
/// <para>
/// 適合するのは、次のどれかでインターフェイスに届くときである。
/// </para>
/// <list type="bullet">
/// <item>型の継承の並び (<c>struct S : IFoo</c>) と <c>extension S : IFoo</c>。基底の構造体の適合は受け継がない (slangc 2026.19 で確かめた)</item>
/// <item>インターフェイスの継承 (<c>interface IBar : IFoo</c>)</item>
/// <item>どの型にも当たる extension (<c>extension&lt;T : IBar&gt; T : IFoo</c>)。T の制約に適合する型は IFoo にも適合する。適合が増えなくなるまで繰り返す</item>
/// <item>標準ライブラリの表。ベクトルと行列は、要素の型の適合で決まる (<c>float3</c> は <c>IFloat</c> に適合する)</item>
/// </list>
/// <para>
/// 決められないときは <see langword="null"/> を返す。標準のモジュール (slang.numerics など) を取り込んだファイルでは、適合しないと言わない (モジュールが C++ の部分で組み込みの型の適合を足すため)。利用者のジェネリックの型に extension を書いたもの (<c>extension&lt;T&gt; Box&lt;T&gt; : IFoo</c>)、修飾した基底、2 度宣言した名前、
/// 表で閉じていない標準ライブラリの型 (数の型・ベクトル・行列でないもの。C++ の部分が作る適合があるかもしれない)、知らない名前は決めない。
/// </para>
/// </remarks>
internal sealed partial class SlangConformanceModel
{
    /// <summary>組み込みの数の型の名前と、別名 (float32_t は float)。</summary>
    [GeneratedRegex(@"^(?<base>bool|int|uint|float|half|double|int8_t|uint8_t|int16_t|uint16_t|int32_t|uint32_t|int64_t|uint64_t|float16_t|float32_t|float64_t|intptr_t|uintptr_t)(?:(?<rows>[1-4])(?:x(?<columns>[1-4]))?)?$")]
    private static partial Regex NumericShape();

    private readonly Dictionary<string, HashSet<string>?> _interfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>?> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _undecided = new(StringComparer.Ordinal);
    private readonly List<(ImmutableArray<string> Constraints, ImmutableArray<string> Interfaces)> _blankets = [];
    private readonly Func<string, bool> _isImported;
    private readonly Dictionary<string, (HashSet<string> Set, bool Complete)> _cache = new(StringComparer.Ordinal);

    private SlangConformanceModel(Func<string, bool> isImported) => _isImported = isImported;

    /// <summary>利用者のインターフェイスの名前 (ジェネリックでなく、1 度だけ宣言したもの)。</summary>
    public IEnumerable<string> UserInterfaces => _interfaces.Where(p => p.Value is not null).Select(p => p.Key);

    /// <summary>利用者のインターフェイスか (2 度宣言したものやジェネリックのものも含む)。</summary>
    public bool IsUserInterface(string name) => _interfaces.ContainsKey(name);

    /// <summary>トークンの並び (解析するファイルと取り込んだモジュール) から作る。</summary>
    /// <param name="tokens">トークンの並び。</param>
    /// <param name="typeSystem">ファイルの型システム (import した標準のモジュールを見る)。</param>
    public static SlangConformanceModel Build(ImmutableArray<ShaderSyntaxToken> tokens, ShaderTypeSystem typeSystem)
    {
        SlangConformanceModel model = new(module => module.Length == 0 || (typeSystem is SlangTypeSystem slang && slang.IsImported(module)));
        model.Collect(tokens);

        // 標準のモジュール (slang.numerics など) は、組み込みの型の適合を C++ の部分で足す (表に出てこない)。取り込んだファイルでは、適合しないと言わない。
        model._undecidedEverything |= typeSystem is SlangTypeSystem { Imports.IsEmpty: false };
        return model;
    }

    /// <summary>
    /// 型がインターフェイスに適合するか。決められなければ <see langword="null"/>。
    /// </summary>
    /// <param name="type">型の名前 (型引数を除いたもの。<c>float3</c>・<c>S</c>)。</param>
    /// <param name="requirement">インターフェイスの名前。</param>
    public bool? Conforms(string type, string requirement)
    {
        (HashSet<string> set, bool complete) = ConformanceOf(type, 0);
        return set.Contains(requirement) ? true : complete && !_undecidedEverything ? false : null;
    }

    /// <summary>
    /// 制約 <paramref name="constraints"/> を持つ型の仮引数が、インターフェイスに適合するか (制約から、インターフェイスの継承とどの型にも当たる extension で届くか)。
    /// 何に適合するか決められない extension があれば、適合するものとする。
    /// </summary>
    public bool ConformsThrough(IEnumerable<string> constraints, string requirement)
    {
        HashSet<string> set = new(constraints, StringComparer.Ordinal);
        Close(set);
        return set.Contains(requirement) || _undecidedEverything;
    }

    /// <summary>
    /// どの型にも当たる extension (<c>extension&lt;T : IBar&gt; T { … }</c>) が、型 <paramref name="type"/> にメソッドを足しうるか。
    /// 制約に適合するか、適合するか決められなければ足しうる。制約の無いもの (書けない) と、制約に適合しないと言えるものは足さない。
    /// </summary>
    public bool MethodExtensionsMayApply(string type)
        => _undecidedEverything || _undecided.Contains(type) || _blankets.Any(b => b.Constraints.Length > 0 && b.Constraints.All(c => Conforms(type, c) != false));

    /// <summary>型が適合するインターフェイスの全部と、それで全部と言えるか。</summary>
    private (HashSet<string> Set, bool Complete) ConformanceOf(string type, int depth)
    {
        if (_cache.TryGetValue(type, out (HashSet<string>, bool) known))
        {
            return known;
        }

        // 循環 (要素の型の条件をたどって自分に戻る) と深すぎるものは決めない。
        if (depth > 16)
        {
            return ([], false);
        }

        _cache[type] = ([], false);
        HashSet<string> set = new(StringComparer.Ordinal);
        bool complete = true;

        if (_types.TryGetValue(type, out HashSet<string>? bases))
        {
            if (bases is null)
            {
                complete = false;
            }
            else
            {
                set.UnionWith(bases);
            }

            complete &= !_undecided.Contains(type);
        }
        else if (NumericShape().Match(type) is { Success: true } shape)
        {
            string element = Canonical(shape.Groups["base"].Value);

            if (!shape.Groups["rows"].Success)
            {
                AddStandard(element, null, set, ref complete, depth);
            }
            else
            {
                // floatN は vector<float, N>、floatNxM は matrix<float, N, M>。
                string generic = shape.Groups["columns"].Success ? "matrix" : "vector";
                AddStandard(generic, element, set, ref complete, depth);
                complete &= !_undecided.Contains(generic);
            }
        }
        else if (SlangStandardConformance.Instance.Types.ContainsKey(type))
        {
            // 表で閉じていない標準ライブラリの型。表にある適合は確かだが、ほかにもあるかもしれない。
            AddStandard(type, null, set, ref complete, depth);
            complete = false;
        }
        else
        {
            complete = false;
        }

        Close(set);
        _cache[type] = (set, complete);
        return (set, complete);
    }

    /// <summary>標準ライブラリの表の、型 <paramref name="type"/> の適合を足す。<paramref name="element"/> は型の最初の型引数 (ベクトルの要素)。</summary>
    private void AddStandard(string type, string? element, HashSet<string> set, ref bool complete, int depth)
    {
        if (!SlangStandardConformance.Instance.Types.TryGetValue(type, out ImmutableArray<SlangStandardConformance.Row> rows))
        {
            return;
        }

        foreach (SlangStandardConformance.Row row in rows)
        {
            if (!_isImported(row.Module))
            {
                continue;
            }

            if (row.Condition is not { } condition)
            {
                set.Add(row.Interface);
                continue;
            }

            if (element is null)
            {
                complete = false;
                continue;
            }

            (HashSet<string> elementSet, bool elementComplete) = ConformanceOf(element, depth + 1);

            if (elementSet.Contains(condition))
            {
                set.Add(row.Interface);
            }
            else if (!elementComplete)
            {
                complete = false;
            }
        }
    }

    /// <summary>インターフェイスの継承と、どの型にも当たる extension で、適合が増えなくなるまで足す。</summary>
    private void Close(HashSet<string> set)
    {
        bool grew = true;

        while (grew)
        {
            grew = false;

            foreach (string name in set.ToArray())
            {
                if (_interfaces.TryGetValue(name, out HashSet<string>? userBases) && userBases is not null)
                {
                    foreach (string b in userBases)
                    {
                        grew |= set.Add(b);
                    }
                }

                if (SlangStandardConformance.Instance.Interfaces.TryGetValue(name, out var standardBases))
                {
                    foreach ((ImmutableArray<string> list, string module) in standardBases)
                    {
                        if (_isImported(module))
                        {
                            foreach (string b in list)
                            {
                                grew |= set.Add(b);
                            }
                        }
                    }
                }
            }

            foreach ((ImmutableArray<string> constraints, ImmutableArray<string> provided) in _blankets)
            {
                if (constraints.All(set.Contains))
                {
                    foreach (string p in provided)
                    {
                        grew |= set.Add(p);
                    }
                }
            }

            foreach ((string constraint, string provided, string module) in SlangStandardConformance.Instance.Blankets)
            {
                if (_isImported(module) && set.Contains(constraint))
                {
                    grew |= set.Add(provided);
                }
            }
        }
    }

    private static string Canonical(string name) => name switch
    {
        "float32_t" => "float",
        "float16_t" => "half",
        "float64_t" => "double",
        "int32_t" => "int",
        "uint32_t" => "uint",
        _ => name,
    };

    /// <summary>トークンの並びから、インターフェイス・型の継承の並び・extension を集める。</summary>
    private void Collect(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        HashSet<string> declared = new(StringComparer.Ordinal);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind != ShaderSyntaxKind.IdentifierToken || token.Text is not ("interface" or "struct" or "class" or "enum" or "extension" or "__extension"))
            {
                continue;
            }

            // enum class E (class は列挙型の書き方の一部。enum の側で読む)
            if (token.Text == "class" && i > 0 && tokens[i - 1].Text == "enum")
            {
                continue;
            }

            bool extension = token.Text is "extension" or "__extension";
            int j = token.Text == "enum" && tokens[i + 1].Text == "class" ? i + 2 : i + 1;

            // extension<T : IBar> T : IFoo の <T : IBar>。__generic<…> extension の形も、前の __generic から読む。
            List<(string Name, List<string> Constraints)> parameters = [];

            if (extension && tokens[j].Kind == ShaderSyntaxKind.LessThanToken)
            {
                j = ReadParameters(tokens, j, parameters);
            }
            else if (extension && i > 2 && tokens[i - 1].Kind == ShaderSyntaxKind.GreaterThanToken && FindGenericBefore(tokens, i - 1) is { } open)
            {
                ReadParameters(tokens, open, parameters);
            }

            if (j >= tokens.Length || tokens[j].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            string name = tokens[j].Text;
            j++;
            bool genericTarget = j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.LessThanToken;

            // 型の仮引数 (struct Box<T : I>) と、型引数を書いた extension の対象 (extension Box<T>) を飛ばす。
            if (genericTarget)
            {
                j = SkipAngles(tokens, j);
            }

            HashSet<string>? bases = new(StringComparer.Ordinal);

            if (j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.ColonToken)
            {
                int angle = 0;

                for (j++; j < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken) && tokens[j].Text != "where"; j++)
                {
                    angle += tokens[j].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

                    // 修飾した基底 (ns.IFoo) は、どのインターフェイスか決めない。
                    if (tokens[j].Kind is ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.DotToken)
                    {
                        bases = null;
                        break;
                    }

                    if (angle == 0 && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken
                        && tokens[j - 1].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.AmpersandToken)
                    {
                        bases.Add(tokens[j].Text);
                    }
                }
            }

            if (!extension)
            {
                if (token.Text == "enum")
                {
                    // 列挙型の継承の並びは基の型 (enum E : int)。適合は標準ライブラリが決める (__EnumType など)。
                    _types[name] = null;
                }
                else if (token.Text == "interface")
                {
                    _interfaces[name] = _interfaces.ContainsKey(name) || genericTarget ? null : bases;
                }
                else if (!declared.Add(name))
                {
                    // 同じ名前の型が 2 つ (名前空間が違うなど)。
                    _types[name] = null;
                }
                else if (_types.TryGetValue(name, out HashSet<string>? earlier))
                {
                    // 型より前に書いた extension の適合と合わせる。
                    if (earlier is not null && bases is not null)
                    {
                        earlier.UnionWith(bases);
                    }
                    else
                    {
                        _types[name] = null;
                    }
                }
                else
                {
                    _types[name] = bases;
                }

                continue;
            }

            // extension<T : IBar> T : IFoo (どの型にも当たる)
            if (parameters.FirstOrDefault(p => p.Name == name) is { Name: not null } target)
            {
                if (bases is null)
                {
                    // 何に適合するか決められないので、どの型の適合も決めない。
                    _blankets.Add(([], ["\u0001"]));
                    _undecidedEverything = true;
                }
                else if (target.Constraints.Count > 0)
                {
                    // 制約の無い extension<T> T は誤り (E30850)。適合を足さない。
                    _blankets.Add(([.. target.Constraints], [.. bases]));
                }

                continue;
            }

            // extension<T> Box<T> : IFoo (ジェネリックの型の、型引数によって決まる適合)・型引数のある extension の対象
            if (genericTarget || parameters.Count > 0)
            {
                _undecided.Add(name);
                continue;
            }

            // extension S : IFoo
            if (bases is null)
            {
                _types[name] = null;
            }
            else if (_types.TryGetValue(name, out HashSet<string>? existing))
            {
                existing?.UnionWith(bases);
            }
            else
            {
                _types[name] = bases;
            }
        }
    }

    /// <summary>何に適合するか決められない、どの型にも当たる extension があるか。あれば、どの型の適合も決めない。</summary>
    private bool _undecidedEverything;

    /// <summary>どの型の適合も決められないか。</summary>
    public bool IsUndecided => _undecidedEverything;

    /// <summary><c>&lt;T : I, let N : int&gt;</c> を読み、型の仮引数と制約を足す。閉じの後ろの位置を返す。</summary>
    private static int ReadParameters(ImmutableArray<ShaderSyntaxToken> tokens, int open, List<(string Name, List<string> Constraints)> parameters)
    {
        int depth = 0;
        int i = open;
        List<ShaderSyntaxToken> part = [];

        void Flush()
        {
            int k = 0;

            if (part.Count > 0 && part[0].Text == "let")
            {
                part.Clear();
                return;
            }

            while (k < part.Count && part[k].Text == "each")
            {
                k++;
            }

            if (k < part.Count && part[k].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                List<string> constraints = [.. part.Skip(k + 1).SkipWhile(t => t.Kind == ShaderSyntaxKind.ColonToken)
                    .TakeWhile(t => t.Kind != ShaderSyntaxKind.EqualsToken)
                    .Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken)
                    .Select(t => t.Text)];
                parameters.Add((part[k].Text, constraints));
            }

            part.Clear();
        }

        for (; i < tokens.Length; i++)
        {
            ShaderSyntaxKind kind = tokens[i].Kind;

            if (kind == ShaderSyntaxKind.LessThanToken && depth++ == 0)
            {
                continue;
            }

            if (kind is ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken)
            {
                depth -= kind == ShaderSyntaxKind.GreaterThanToken ? 1 : 2;

                if (depth <= 0)
                {
                    Flush();
                    return i + 1;
                }
            }

            if (kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken)
            {
                return i;
            }

            if (kind == ShaderSyntaxKind.CommaToken && depth == 1)
            {
                Flush();
                continue;
            }

            part.Add(tokens[i]);
        }

        return i;
    }

    /// <summary>位置 <paramref name="close"/> の &gt; に対応する、前の __generic&lt; の &lt; の位置。無ければ <see langword="null"/>。</summary>
    private static int? FindGenericBefore(ImmutableArray<ShaderSyntaxToken> tokens, int close)
    {
        int depth = 0;

        for (int i = close; i > 0; i--)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.GreaterThanToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.LessThanToken && --depth == 0)
            {
                return tokens[i - 1].Text == "__generic" ? i : null;
            }
            else if (tokens[i].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>&lt; から対応する &gt; の後ろまで飛ばす。</summary>
    private static int SkipAngles(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            depth += tokens[i].Kind switch
            {
                ShaderSyntaxKind.LessThanToken => 1,
                ShaderSyntaxKind.GreaterThanToken => -1,
                ShaderSyntaxKind.GreaterThanGreaterThanToken => -2,
                _ => 0,
            };

            if (depth <= 0 && tokens[i].Kind is ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken)
            {
                return i + 1;
            }

            if (tokens[i].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken)
            {
                return i;
            }
        }

        return tokens.Length;
    }
}

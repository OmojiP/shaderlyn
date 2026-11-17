using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// Slang の標準ライブラリの、型がどのインターフェイスに適合するかの表 (<c>SlangConformance.g.txt</c>。<c>tools/slang/stdlib.mjs</c> が作る)。
/// </summary>
/// <remarks>
/// <para>
/// 組み込みの数の型 (<c>float</c> など) は <c>__BuiltinFloatingPointType</c> などに適合し、そのインターフェイスが <c>IFloat</c> などを継承する。
/// ベクトルと行列は、要素の型によって適合が決まる (<c>__generic&lt;T : __BuiltinFloatingPointType, let N : int&gt; extension vector&lt;T,N&gt; : IFloat</c>)。
/// </para>
/// <para>
/// 標準のモジュール (slang/neural など) にだけある行は、そのモジュールを import したときだけ使う。
/// </para>
/// </remarks>
internal sealed class SlangStandardConformance
{
    private const string ResourceName = "Shaderlyn.Semantics.SlangConformance.txt";

    private static readonly Lazy<SlangStandardConformance> Shared = new(() => new SlangStandardConformance());

    private SlangStandardConformance()
    {
        Dictionary<string, List<Row>> types = new(StringComparer.Ordinal);
        Dictionary<string, List<(ImmutableArray<string> Bases, string Module)>> interfaces = new(StringComparer.Ordinal);
        List<(string Constraint, string Interface, string Module)> blankets = [];
        Dictionary<string, string> closed = new(StringComparer.Ordinal);
        HashSet<string> synthesized = new(StringComparer.Ordinal);
        Dictionary<string, List<(int Index, string Parameter, string Constraint, string Module)>> generics = new(StringComparer.Ordinal);

        using Stream stream = typeof(SlangStandardConformance).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException($"埋め込みのリソース {ResourceName} がありません。");
        using StreamReader reader = new(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            switch (line.Split('\t'))
            {
                case ["C", var type, var iface, var condition, var module]:
                    if (!types.TryGetValue(type, out List<Row>? rows))
                    {
                        types[type] = rows = [];
                    }

                    rows.Add(new Row(iface, condition.Length == 0 ? null : condition, module));
                    break;

                case ["I", var name, var bases, var module]:
                    if (!interfaces.TryGetValue(name, out List<(ImmutableArray<string>, string)>? list))
                    {
                        interfaces[name] = list = [];
                    }

                    list.Add(([.. bases.Split(',', StringSplitOptions.RemoveEmptyEntries)], module));
                    break;

                case ["X", var constraint, var iface, var module]:
                    blankets.Add((constraint, iface, module));
                    break;

                case ["K", var type, var module]:
                    closed[type] = module;
                    break;

                case ["U", var iface, _]:
                    synthesized.Add(iface);
                    break;

                case ["G", var type, var index, var parameter, var constraint, var module]:
                    if (!generics.TryGetValue(type, out var constraints))
                    {
                        generics[type] = constraints = [];
                    }

                    constraints.Add((int.Parse(index, System.Globalization.CultureInfo.InvariantCulture), parameter, constraint, module));
                    break;
            }
        }

        Types = types.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal);
        Interfaces = interfaces.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal);
        Blankets = [.. blankets];
        Closed = closed.ToFrozenDictionary(StringComparer.Ordinal);
        Synthesized = synthesized.ToFrozenSet(StringComparer.Ordinal);
        Generics = generics.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>表 (1 つだけ)。</summary>
    public static SlangStandardConformance Instance => Shared.Value;

    /// <summary>型ごとの適合 (インターフェイスと、要素の型の条件)。</summary>
    public FrozenDictionary<string, ImmutableArray<Row>> Types { get; }

    /// <summary>インターフェイスごとの、継承したインターフェイス。</summary>
    public FrozenDictionary<string, ImmutableArray<(ImmutableArray<string> Bases, string Module)>> Interfaces { get; }

    /// <summary>制約に適合する型はどれもインターフェイスに適合する、という extension (<c>extension&lt;T : 制約&gt; T : インターフェイス</c>)。</summary>
    public ImmutableArray<(string Constraint, string Interface, string Module)> Blankets { get; }

    /// <summary>
    /// 適合を全部 <see cref="Types"/> に書いた型と、そのモジュール (標準ライブラリなら空)。
    /// 表の行 (条件を問わない)・インターフェイスの継承・<see cref="Blankets"/> で届かないインターフェイスには、適合しない (<see cref="Synthesized"/> を除く)。
    /// </summary>
    public FrozenDictionary<string, string> Closed { get; }

    /// <summary>コンパイラが自分で適合を足すインターフェイス (<c>IDifferentiable</c>・<c>IDefaultInitializable</c> など)。表に無くても、適合しないとは言えない。</summary>
    public FrozenSet<string> Synthesized { get; }

    /// <summary>ジェネリックの型の、型の仮引数 (位置) の制約 (<c>struct DescriptorHandle&lt;T : IOpaqueDescriptor&gt;</c> なら 0・T・IOpaqueDescriptor)。</summary>
    public FrozenDictionary<string, ImmutableArray<(int Index, string Parameter, string Constraint, string Module)>> Generics { get; }

    /// <summary>型の適合 1 つ。</summary>
    /// <param name="Interface">適合するインターフェイス。</param>
    /// <param name="Condition">型の最初の型引数 (ベクトルの要素など) が適合しなければならないインターフェイス。無ければ <see langword="null"/>。</param>
    /// <param name="Module">標準のモジュールにだけある行なら、そのモジュール (slang/neural など)。標準ライブラリなら空。</param>
    internal readonly record struct Row(string Interface, string? Condition, string Module);
}

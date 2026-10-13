using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// コードが届くコンパイル先 (API と方言) のうち、その構文や型を使えないものがあれば報告する (HL0003)。
/// </summary>
/// <remarks>
/// <para>
/// <b>1 つのパスは、API ごとに別の方言でコンパイルされることがある。</b>
/// Unity で <c>#pragma use_dxc</c> を書いたパスは、DX12 は DXC、DX11 は FXC でコンパイルされうる
/// (<see cref="HlslCompileTarget"/>)。パーサーはどれかの方言で使える構文を読むので、
/// 片方でしか使えない構文 (<c>enum</c> など) は、ここで API を添えて報告する。
/// どの方言でも使えない構文は、パーサーが HL0003 として報告済みである。
/// </para>
/// <para>
/// <b>構成ごとに、そのノードに届くコンパイル先だけを見る。</b>
/// <c>#ifdef UNITY_DEVICE_SUPPORTS_NATIVE_16BIT</c> の中の <c>float16_t</c> は、
/// キーワードが有効な構成 (DXC) でしかコンパイルされない。
/// </para>
/// <para>使えないコンパイル先 (<see cref="HlslCompileTarget.IsUsable"/> が偽) は見ない。そこでのエラーは利用者に見えない。</para>
/// </remarks>
internal sealed class CompileTargetAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslDescriptors.UnsupportedDeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 位置ごとに、使えないコンパイル先を集めてから 1 度だけ報告する。同じコードが構成ごとに何度も渡る。
        Dictionary<(string File, int Start), (HlslSyntaxToken Token, string Construct, SortedSet<string> Subjects)> found = [];

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (Describe(node, program) is not { } construct || !compilation.IsReportable(node))
            {
                continue;
            }

            // 型 (int64_t など) はパーサーが見ないので、方言が 1 つだけでも見る。
            // コンパイル先が分からなければ (汎用の HLSL)、方言を 1 つのコンパイル先として見る。ただし見るのは interface の変数だけ
            // (型は、コンパイルの設定 (-enable-16bit-types など) が分からないと決められない)。
            IEnumerable<HlslCompileTarget> targets = !program.Tree.CompileTargets.IsDefaultOrEmpty
                ? CompileTargets.Reaching(compilation, node, program)
                : construct.CheckWithoutTargets
                    ? program.Tree.Dialects.Select(d => new HlslCompileTarget(string.Empty, d))
                    : [];

            foreach (HlslCompileTarget target in targets)
            {
                if (construct.IsAvailable(target.Dialect))
                {
                    continue;
                }

                (string File, int Start) position = (construct.Token.Source.FilePath, construct.Token.Span.Start);

                if (!found.TryGetValue(position, out var entry))
                {
                    entry = (construct.Token, construct.Name, new SortedSet<string>(StringComparer.Ordinal));
                    found[position] = entry;
                }

                entry.Subjects.Add(target.Subject);
            }
        }

        foreach ((HlslSyntaxToken token, string name, SortedSet<string> subjects) in found.Values)
        {
            if (token.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HlslDescriptors.UnsupportedDeclaration, location, name, string.Join("・", subjects)));
            }
        }
    }

    /// <summary>方言で使えるかが変わる構文と型を見分ける。</summary>
    /// <param name="node">ノード。</param>
    /// <param name="program">ノードが属する木。</param>
    /// <returns>見分けた構文。当てはまらなければ <see langword="null"/>。</returns>
    private static Construct? Describe(HlslSyntaxNode node, AnalyzedProgram program) => node switch
    {
        EnumDeclarationSyntax enumeration => new Construct(enumeration.NameToken ?? enumeration.Keyword, "enum", static d => d.Features.Enums),
        HlslTypeSyntax type => DescribeType(type),
        VariableDeclarationSyntax variable => DescribeInterfaceVariable(variable, program),
        InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: var name } target } when LegacyTextureIntrinsics.Contains(name)
            => new Construct(target.Identifier, $"DX9 の組み込み関数 {name}", static d => d.AcceptsLegacySamplerSyntax) { CheckWithoutTargets = true },
        ParameterSyntax parameter => DescribeUniformParameter(parameter, program),
        _ => null,
    };

    /// <summary>DX9 の書き方のテクスチャの組み込み関数。単体の DXC は知らない (<c>Unsupported intrinsic</c>)。</summary>
    private static readonly FrozenSet<string> LegacyTextureIntrinsics = new[]
    {
        "tex1D", "tex1Dbias", "tex1Dgrad", "tex1Dlod", "tex1Dproj",
        "tex2D", "tex2Dbias", "tex2Dgrad", "tex2Dlod", "tex2Dproj",
        "tex3D", "tex3Dbias", "tex3Dgrad", "tex3Dlod", "tex3Dproj",
        "texCUBE", "texCUBEbias", "texCUBEgrad", "texCUBElod", "texCUBEproj",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// エントリ関数の <c>uniform</c> 引数を見分ける。DXC はどの版でも拒む
    /// (<c>Semantic must be defined for all parameters of an entry function</c>)。FXC は通す。手元の DXC 1.8 と Unity で確かめた。
    /// </summary>
    /// <param name="parameter">仮引数。</param>
    /// <param name="program">仮引数が属する木。</param>
    /// <returns>見分けた構文。当てはまらなければ <see langword="null"/>。</returns>
    private static Construct? DescribeUniformParameter(ParameterSyntax parameter, AnalyzedProgram program)
    {
        if (!parameter.ModifierTokens.Any(t => t.Text == "uniform")
            || parameter.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } function
            || !program.EntryPoints.Any(e => e.Name == function.Name))
        {
            return null;
        }

        return new Construct(
            parameter.ModifierTokens.First(t => t.Text == "uniform"),
            $"エントリ関数 {function.Name} の uniform 引数 {parameter.Name}",
            static d => d.Compiler == HlslCompiler.Fxc)
        {
            CheckWithoutTargets = true,
        };
    }

    /// <summary>
    /// グローバルな interface 型の変数を見分ける。
    /// </summary>
    /// <param name="variable">変数の宣言。</param>
    /// <param name="program">宣言が属する木。</param>
    /// <returns>見分けた構文。当てはまらなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// interface の変数は、FXC のシェーダーモデル 5 の動的リンクのためのもので、実体はコンパイルの外から結び付ける。
    /// DXC はどの版でも拒み (<c>variable type 'ILight' is an abstract class</c>)、
    /// FXC もシェーダーモデル 5.0 より前では拒む (<c>abstract interfaces not supported on ps_4_0</c>)。手元の FXC / DXC 1.8 と Unity で確かめた。
    /// </para>
    /// <para>
    /// シェーダーモデルは <c>#pragma target</c> から読む (Unity。無ければ Unity の既定の 3.5)。
    /// 単位にターゲットがあれば (<c>units:</c> の <c>target</c>) それを使う。どちらも無ければ FXC では報告しない。
    /// </para>
    /// </remarks>
    private static Construct? DescribeInterfaceVariable(VariableDeclarationSyntax variable, AnalyzedProgram program)
    {
        if (variable.Parent is not (HlslCompilationUnitSyntax or NamespaceDeclarationSyntax or ConstantBufferDeclarationSyntax)
            || variable.HasModifier("static")
            || !InterfaceNames(program).Contains(variable.Type.NameToken.Text))
        {
            return null;
        }

        int? shaderModel = program.Unit.Target is { } target
            ? (target.Major * 10) + target.Minor
            : !program.Tree.CompileTargets.IsDefaultOrEmpty
                ? ReadPragmaTarget(program) ?? 35
                : null;

        return new Construct(
            variable.Type.NameToken,
            $"interface 型 {variable.Type.NameToken.Text} のグローバル変数",
            d => d.Compiler == HlslCompiler.Fxc && (shaderModel is null || shaderModel >= 50))
        {
            CheckWithoutTargets = true,
        };
    }

    /// <summary>木の中で宣言された interface の名前。</summary>
    /// <param name="program">木。</param>
    /// <returns>名前。</returns>
    private static HashSet<string> InterfaceNames(AnalyzedProgram program)
        => InterfaceCache.GetValue(program.Tree, static tree => [
            .. tree.Root.DescendantNodesAndSelf()
                .OfType<StructDeclarationSyntax>()
                .Where(s => s.Keyword.Text == "interface" && s.Name.Length > 0)
                .Select(s => s.Name),
        ]);

    /// <summary>木ごとの interface の名前。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HlslSyntaxTree, HashSet<string>> InterfaceCache = [];

    /// <summary><c>#pragma target</c> のシェーダーモデルを読む (<c>4.5</c> → 45)。複数あれば最も高いもの。</summary>
    /// <param name="program">木。</param>
    /// <returns>シェーダーモデル。無ければ <see langword="null"/>。</returns>
    private static int? ReadPragmaTarget(AnalyzedProgram program)
    {
        int? best = null;

        foreach (Hlsl.Preprocessing.PragmaDirective pragma in program.Tree.Pragmas)
        {
            if (pragma.Name == "target"
                && pragma.Arguments.FirstOrDefault()?.Text is { } text
                && text.Split('.') is [var major, var minor, ..]
                && int.TryParse(major, out int a)
                && int.TryParse(minor.AsSpan(0, Math.Min(1, minor.Length)), out int b))
            {
                best = Math.Max(best ?? 0, (a * 10) + b);
            }
        }

        return best;
    }

    /// <summary>型名を見分ける (<c>int64_t</c>、<c>float32_t</c>、<c>float16_t3</c> など)。</summary>
    /// <param name="type">型。</param>
    /// <returns>見分けた型。当てはまらなければ <see langword="null"/>。</returns>
    private static Construct? DescribeType(HlslTypeSyntax type)
    {
        // vector<uint32_t, 2> は要素の型を見る。
        HlslSyntaxToken token = type.NameToken.Text is "vector" or "matrix" && !type.TemplateArguments.IsDefaultOrEmpty
            ? type.TemplateArguments[0]
            : type.NameToken;
        string written = token.Text;
        string scalar = StripShape(written);

        // sampler1D / sampler2D / sampler3D / samplerCUBE は DX9 の書き方。単体の DXC は知らない (unknown type name)。
        if (written is "sampler1D" or "sampler2D" or "sampler3D" or "samplerCUBE")
        {
            return new Construct(token, $"DX9 の型 {written}", static d => d.AcceptsLegacySamplerSyntax) { CheckWithoutTargets = true };
        }

        return scalar switch
        {
            "int64_t" or "uint64_t" => new Construct(token, $"型 {written}", static d => d.Features.Int64Types),
            "float64_t" or "float32_t" or "int32_t" or "uint32_t" => new Construct(token, $"型 {written}", static d => d.Features.ExplicitWidthTypes),
            "float16_t" or "int16_t" or "uint16_t" => new Construct(token, $"型 {written}", static d => d.Features.SixteenBitTypes),
            _ => null,
        };
    }

    /// <summary>ベクトルと行列の形 (<c>3</c> / <c>4x4</c>) を外す。</summary>
    /// <param name="name">型名。</param>
    /// <returns>スカラーの型名。</returns>
    private static string StripShape(string name)
    {
        int end = name.Length;

        if (end >= 3 && name[end - 2] == 'x' && name[end - 1] is >= '1' and <= '4' && name[end - 3] is >= '1' and <= '4')
        {
            end -= 3;
        }
        else if (end >= 1 && name[end - 1] is >= '1' and <= '4' && end >= 2 && name[end - 2] == 't')
        {
            end -= 1;
        }

        return name[..end];
    }

    /// <summary>見分けた構文。</summary>
    /// <param name="Token">報告する位置。</param>
    /// <param name="Name">文に入れる名前。</param>
    /// <param name="IsAvailable">方言で使えるか。</param>
    private sealed record Construct(HlslSyntaxToken Token, string Name, Func<HlslDialect, bool> IsAvailable)
    {
        /// <summary>コンパイル先が分からないとき (汎用の HLSL) にも、方言で見るか。</summary>
        public bool CheckWithoutTargets { get; init; }
    }
}

/// <summary>
/// ノードに届くコンパイル先を求める。
/// </summary>
internal static class CompileTargets
{
    /// <summary>
    /// そのノードが、どの構成でどのコンパイル先に届くかを求める。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="node">ノード。</param>
    /// <param name="program">ノードが属する木。</param>
    /// <returns>届く、使えるコンパイル先。コンパイル先が分からなければ空。</returns>
    /// <remarks>
    /// ノードの出現条件と、コンパイル先の構成の条件が両立するなら届く。
    /// 出現条件が分からなければ、木を解析した構成で決める。
    /// </remarks>
    public static IEnumerable<HlslCompileTarget> Reaching(ShaderCompilation compilation, HlslSyntaxNode node, AnalyzedProgram program)
    {
        ImmutableArray<HlslCompileTarget> targets = program.Tree.CompileTargets;

        if (targets.IsDefaultOrEmpty)
        {
            yield break;
        }

        SymbolCondition condition = compilation.GetEffectiveCondition(node, program);
        Semantics.Conditional.ConditionMap? map = null;

        foreach (HlslCompileTarget target in targets)
        {
            if (!target.IsUsable)
            {
                continue;
            }

            if (condition.IsUnknown)
            {
                if (target.AppliesTo([.. program.EnabledSymbols]))
                {
                    yield return target;
                }

                continue;
            }

            map ??= compilation.GetConditionMap();

            if (map.IsPossible(condition.And(target.Condition)))
            {
                yield return target;
            }
        }
    }
}

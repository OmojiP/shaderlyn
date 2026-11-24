using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// エントリポイントから、そのステージで使用できない機能 (<c>discard</c>・<c>clip</c>・微分・LOD を指定しないサンプリング) に届くものを報告する
/// (HLSL は HL0356、Slang は SG0365)。
/// </summary>
/// <remarks>
/// <para>
/// 誤りになる条件は、FXC・DXC 1.8・slangc 2026.19 にステージごとにコンパイルさせて決めた (2026-11-24)。コンパイラで条件が違うので、規則を分けている。
/// </para>
/// <list type="bullet">
/// <item><c>discard</c> と <c>clip</c>: ピクセルシェーダー以外のどのステージでも誤り (FXC の X4532、DXC の「Opcode Discard not valid」、slangc の E36107・E36100)</item>
/// <item>微分 (<c>ddx</c>・<c>ddy</c>・<c>fwidth</c> など): 頂点・ジオメトリ・ハル・ドメインのシェーダーで誤り。HLSL はコンピュートシェーダーでも FXC が誤りにする
///   (DXC はシェーダーモデル 6.6 から使用できるので見ない)。slangc はレイトレーシングとメッシュのステージでは通す</item>
/// <item>LOD を指定しないサンプリング (<c>Sample</c>・<c>SampleBias</c>・<c>SampleCmp</c>・<c>CalculateLevelOfDetail</c>、DX9 の <c>tex2D</c> など):
///   HLSL だけ。条件は微分と同じ。slangc はどのステージでも通す (LOD を補う)</item>
/// </list>
/// <para>
/// エントリポイントのステージは、<c>[shader("…")]</c> の属性と環境が読んだエントリポイントで決める (<see cref="SystemValueSemanticAnalyzer.EntryFunctions"/>)。
/// エントリポイントと、そこから呼び出しをたどって届く関数の本体を見る。HLSL は使用した箇所に、Slang は slangc と同じくエントリポイントの名前に報告する。
/// </para>
/// </remarks>
internal sealed class StageRestrictedFeatureAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>微分を使う組み込み関数。</summary>
    private static readonly ImmutableHashSet<string> Derivatives = ["ddx", "ddy", "ddx_coarse", "ddx_fine", "ddy_coarse", "ddy_fine", "fwidth"];

    /// <summary>LOD を指定しないテクスチャのメソッド。</summary>
    private static readonly ImmutableHashSet<string> ImplicitLodMethods = ["Sample", "SampleBias", "SampleCmp", "CalculateLevelOfDetail", "CalculateLevelOfDetailUnclamped"];

    /// <summary>LOD を指定しない DX9 の組み込み関数 (2 つの実引数の形。4 つの形は勾配を渡すので通る)。</summary>
    private static readonly ImmutableHashSet<string> ImplicitLodIntrinsics =
        ["tex1D", "tex2D", "tex3D", "texCUBE", "tex1Dbias", "tex2Dbias", "tex3Dbias", "texCUBEbias", "tex1Dproj", "tex2Dproj", "tex3Dproj", "texCUBEproj"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.StageRestrictedFeature, Slang.SlangRuleDescriptors.StageRestrictedFeature];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じ関数を複数のエントリポイントやバリアントの木から呼ぶので、同じ位置には 1 度だけ報告する。
        HashSet<Location> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<FunctionDeclarationSyntax> functions = [.. EntryPointAnalyzer.EnumerateFunctions(program.Tree.Root.Declarations).Where(f => f.IsDefinition)];
            HashSet<string> userFunctions = [.. functions.Select(f => f.Name)];
            ExpressionTypeBinder? binder = null;

            foreach ((FunctionDeclarationSyntax entry, string stage) in Entries(program, functions))
            {
                ImmutableArray<SystemValueCompiler> compilers = SystemValueSemanticAnalyzer.CompilersOf(compilation, entry, program);

                if (compilers.IsEmpty)
                {
                    continue;
                }

                IEnumerable<FunctionDeclarationSyntax> reached = [entry, .. compilation.EnumerateCallees(entry, program, int.MaxValue).Select(c => c.Function)];

                foreach (FunctionDeclarationSyntax function in reached)
                {
                    // Slang の [require(fragment)] (ステージを 1 つだけ書いた要件) の関数に、別のステージから届くもの (E36107)。
                    // [require] を 2 つ書いた関数は、どちらかを満たせばよいので見ない。
                    if (compilation.IsSlang
                        && function.Attributes.Where(a => a.Name == "require").ToArray() is [{ ArgumentTokens: [{ Kind: ShaderSyntaxKind.IdentifierToken } required] }]
                        && RequiredStage(required.Text) is { } requiredStage
                        && requiredStage != stage
                        && entry.NameToken.GetLocation() is { } entryLocation
                        && compilation.IsReportable(entry.NameToken)
                        && reported.Add(entryLocation))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Slang.SlangRuleDescriptors.StageRestrictedFeature, entryLocation,
                            $"{StageName(stage)}のエントリポイント '{entry.Name}' から、[require({required.Text})] の関数 '{function.Name}' に届きます ({StageName(requiredStage)}でしか使用できません)。"));
                    }

                    if (function.Body is null)
                    {
                        continue;
                    }

                    foreach (ShaderSyntaxNode node in function.Body.DescendantNodesAndSelf().OfType<ShaderSyntaxNode>())
                    {
                        if (Classify(node, userFunctions, compilation.IsSlang, () => binder ??= compilation.GetExpressionTypeBinder(program)) is not { } feature
                            || !compilation.IsReportable(node))
                        {
                            continue;
                        }

                        if (compilation.IsSlang)
                        {
                            ReportSlang(context, entry, stage, function, node, feature, reported);
                        }
                        else
                        {
                            ReportHlsl(context, entry, stage, node, feature, compilers, reported);
                        }
                    }
                }
            }
        }
    }

    /// <summary><c>[require(…)]</c> に書いたステージの名前を、<see cref="Entries"/> のステージの名前にする。ステージでなければ <see langword="null"/>。</summary>
    private static string? RequiredStage(string atom) => atom switch
    {
        "fragment" or "pixel" => "pixel",
        "raygen" or "raygeneration" => "raygeneration",
        "vertex" or "compute" or "geometry" or "hull" or "domain" or "mesh" or "amplification" or "intersection" or "anyhit" or "closesthit" or "miss" or "callable" => atom,
        _ => null,
    };

    /// <summary>機能の種類。</summary>
    private enum Feature
    {
        /// <summary><c>discard</c> と <c>clip</c>。</summary>
        Discard,

        /// <summary>微分。</summary>
        Derivative,

        /// <summary>LOD を指定しないサンプリング。</summary>
        ImplicitLod,
    }

    /// <summary>ステージで制限される機能か。違えば <see langword="null"/>。</summary>
    private static (Feature Kind, string Name)? Classify(SyntaxNode node, HashSet<string> userFunctions, bool slang, Func<ExpressionTypeBinder> binder)
    {
        switch (node)
        {
            case JumpStatementSyntax { Keyword.Text: "discard" }:
                return (Feature.Discard, "discard");

            case InvocationExpressionSyntax { TargetName: { } name } call when !userFunctions.Contains(name):
                if (name == "clip")
                {
                    return (Feature.Discard, "clip");
                }

                if (Derivatives.Contains(name))
                {
                    return (Feature.Derivative, name);
                }

                return !slang && ImplicitLodIntrinsics.Contains(name) && call.ArgumentExpressions.Count() == 2 ? (Feature.ImplicitLod, name) : null;

            case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access }
                when !slang && ImplicitLodMethods.Contains(access.Name)
                     && binder().GetEvaluatorFor(access.Target).Evaluate(access.Target) is { } receiver
                     && receiver.StartsWith("Texture", StringComparison.Ordinal):
                return (Feature.ImplicitLod, access.Name);

            default:
                return null;
        }
    }

    /// <summary>ステージの分かるエントリポイントと、ステージの名前 (<c>[shader("…")]</c> に書く名前。ピクセルシェーダーは pixel)。</summary>
    private static IEnumerable<(FunctionDeclarationSyntax Function, string Stage)> Entries(AnalyzedProgram program, ImmutableArray<FunctionDeclarationSyntax> functions)
    {
        HashSet<FunctionDeclarationSyntax> seen = new(ReferenceEqualityComparer.Instance);

        // レイトレーシングのステージ ([shader("closesthit")] など) は EntryFunctions が返さないので、属性から読む。
        foreach (FunctionDeclarationSyntax function in functions)
        {
            if (function.Attributes.FirstOrDefault(a => a.Name == "shader") is { ArgumentTokens: [{ Kind: ShaderSyntaxKind.StringLiteralToken } literal] }
                && literal.Text.Trim('"') is var name
                && name is "raygeneration" or "intersection" or "anyhit" or "closesthit" or "miss" or "callable"
                && seen.Add(function))
            {
                yield return (function, name);
            }
        }

        foreach ((FunctionDeclarationSyntax function, HlslShaderStage stage) in SystemValueSemanticAnalyzer.EntryFunctions(program, functions))
        {
            if (seen.Add(function))
            {
                yield return (function, stage switch
                {
                    HlslShaderStage.Vertex => "vertex",
                    HlslShaderStage.Pixel => "pixel",
                    HlslShaderStage.Compute => "compute",
                    HlslShaderStage.Geometry => "geometry",
                    HlslShaderStage.Hull => "hull",
                    HlslShaderStage.Domain => "domain",
                    HlslShaderStage.Mesh => "mesh",
                    _ => "amplification",
                });
            }
        }
    }

    /// <summary>HLSL: 使用した箇所に報告する。</summary>
    private static void ReportHlsl(
        SyntaxTreeAnalysisContext context,
        FunctionDeclarationSyntax entry,
        string stage,
        SyntaxNode node,
        (Feature Kind, string Name) feature,
        ImmutableArray<SystemValueCompiler> compilers,
        HashSet<Location> reported)
    {
        // 微分と LOD を指定しないサンプリングは、頂点・ジオメトリ・ハル・ドメインのシェーダーではどのコンパイラも誤りにする。
        // コンピュートシェーダーは FXC だけ (DXC はシェーダーモデル 6.6 から使用できる)。
        string[] rejecting = feature.Kind switch
        {
            Feature.Discard when stage != "pixel" => [.. compilers.Select(NameOf)],
            not Feature.Discard when stage is "vertex" or "geometry" or "hull" or "domain" => [.. compilers.Select(NameOf)],
            not Feature.Discard when stage == "compute" => [.. compilers.Where(c => c == SystemValueCompiler.Fxc).Select(NameOf)],
            _ => [],
        };

        if (rejecting.Length == 0 || Location(node) is not { } location || !reported.Add(location))
        {
            return;
        }

        string what = feature.Kind switch
        {
            Feature.Discard => $"{feature.Name} はピクセルシェーダーでしか使用できません",
            Feature.Derivative => $"{feature.Name} (微分) は{StageName(stage)}では使用できません",
            _ => $"{feature.Name} (LOD を指定しないサンプリング) は{StageName(stage)}では使用できません",
        };

        string fix = feature.Kind switch
        {
            Feature.ImplicitLod when feature.Name.StartsWith("tex", StringComparison.Ordinal) => $"LOD を指定する {feature.Name.Replace("bias", string.Empty, StringComparison.Ordinal).Replace("proj", string.Empty, StringComparison.Ordinal)}lod を使用してください。",
            Feature.ImplicitLod when feature.Name.StartsWith("SampleCmp", StringComparison.Ordinal) => "SampleCmpLevelZero を使用してください。",
            Feature.ImplicitLod when feature.Name.StartsWith("Calculate", StringComparison.Ordinal) => string.Empty,
            Feature.ImplicitLod => "LOD を指定する SampleLevel を使用してください。",
            _ => string.Empty,
        };

        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.StageRestrictedFeature, location,
            $"{what}。{StageName(stage)}のエントリポイント '{entry.Name}' から届きます ({string.Join("・", rejecting)} がコンパイルの誤りにします)。{fix}".TrimEnd()));
    }

    /// <summary>Slang: slangc と同じく、エントリポイントの名前に報告する。</summary>
    private static void ReportSlang(
        SyntaxTreeAnalysisContext context,
        FunctionDeclarationSyntax entry,
        string stage,
        FunctionDeclarationSyntax function,
        SyntaxNode node,
        (Feature Kind, string Name) feature,
        HashSet<Location> reported)
    {
        // discard と clip はフラグメントシェーダーだけ。微分は頂点・ジオメトリ・ハル・ドメインのシェーダーで誤り
        // (レイトレーシングとメッシュは slangc が通し、コンピュートは [numthreads] の大きさで決まる)。
        bool rejected = feature.Kind switch
        {
            Feature.Discard => stage != "pixel",
            Feature.Derivative => stage is "vertex" or "geometry" or "hull" or "domain",
            _ => false,
        };

        if (!rejected || entry.NameToken.GetLocation() is not { } location || !reported.Add(location))
        {
            return;
        }

        string where = ReferenceEquals(function, entry)
            ? $"{Line(node)} 行目"
            : $"'{function.Name}' の {Line(node)} 行目";

        string what = feature.Kind == Feature.Discard ? "フラグメントシェーダーでしか使用できない" : $"{StageName(stage)}では使用できない微分の";

        context.ReportDiagnostic(Diagnostic.Create(Slang.SlangRuleDescriptors.StageRestrictedFeature, location,
            $"{StageName(stage)}のエントリポイント '{entry.Name}' から、{what} {feature.Name} ({where}) に届きます。"));
    }

    private static Location? Location(SyntaxNode node) => node switch
    {
        JumpStatementSyntax jump => jump.Keyword.GetLocation(),
        ShaderSyntaxNode shader => shader.GetLocation(),
        _ => null,
    };

    private static int Line(SyntaxNode node) => Location(node) is { } location ? location.LineSpan.Start.Line + 1 : 0;

    private static string NameOf(SystemValueCompiler compiler) => compiler switch
    {
        SystemValueCompiler.Fxc => "FXC",
        SystemValueCompiler.Dxc => "DXC",
        _ => "slangc",
    };

    private static string StageName(string stage) => stage switch
    {
        "vertex" => "頂点シェーダー",
        "pixel" => "ピクセルシェーダー",
        "compute" => "コンピュートシェーダー",
        "geometry" => "ジオメトリシェーダー",
        "hull" => "ハルシェーダー",
        "domain" => "ドメインシェーダー",
        "mesh" => "メッシュシェーダー",
        "amplification" => "増幅シェーダー",
        _ => $"{stage} シェーダー",
    };
}

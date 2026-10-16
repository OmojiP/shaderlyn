using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cookbook.Environment;

/// <summary>
/// 独自エンジンの環境の例。エンジン独自の <c>#pragma</c> で、構成の軸とエントリポイントを宣言する。
/// </summary>
/// <remarks>
/// <para>
/// このエンジン (架空の「MyEngine」) の決まり:
/// </para>
/// <list type="bullet">
///   <item><description><c>#pragma variant QUALITY 0 1 2</c>: 値を持つ構成の軸。先頭の値が既定</description></item>
///   <item><description><c>#pragma variant USE_FOG</c>: 定義されるか・されないかの構成の軸</description></item>
///   <item><description><c>#pragma entry MainPS ps_6_6</c>: エントリポイントとコンパイルのターゲット</description></item>
///   <item><description><c>USE_</c> で始まる名前は構成の軸の慣習。宣言し忘れを HL0330 で報告させる</description></item>
///   <item><description><c>Generated/</c> で始まる取り込みは、ビルドのときにエンジンが生成する</description></item>
///   <item><description>エンジンのシェーダーコンパイラは <c>MYENGINE=1</c> を定義し、DXC (HLSL 2021) でコンパイルする</description></item>
/// </list>
/// <para>
/// 字句解析・プリプロセッサ・型・ルールは組み込みのものがそのまま働く。環境が教えるのは、エンジンの決まりだけである。
/// </para>
/// </remarks>
public sealed class MyEngineEnvironment : IHlslEnvironment
{
    /// <summary>構成の軸を宣言する <c>#pragma</c>。</summary>
    private const string VariantPragma = "variant";

    /// <summary>エントリポイントを指定する <c>#pragma</c>。</summary>
    private const string EntryPragma = "entry";

    /// <inheritdoc/>
    public string Name => "myengine";

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        // DXC が定義するマクロと、エンジンのシェーダーコンパイラが定義するマクロ。設定で与えたものが勝つ。
        return options with
        {
            Language = new HlslLanguageOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 },
            PredefinedMacros = HlslShaderTarget.CreateCommonDxcMacros(HlslLanguageVersion.Hlsl2021)
                .SetItem("MYENGINE", "1")
                .SetItems(semantics.PredefinedMacros ?? ImmutableDictionary<string, string>.Empty),
        };
    }

    /// <inheritdoc/>
    public PreprocessorOptions PrepareUnit(
        HlslCompilationUnit unit,
        ImmutableArray<ShaderSyntaxToken> tokens,
        PreprocessorOptions options,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(options);

        // #pragma entry のターゲットで決まるマクロ (__SHADER_TARGET_STAGE など) を足す。
        foreach (PragmaDirective pragma in ReadPragmas(tokens))
        {
            if (pragma.Name == EntryPragma
                && pragma.Arguments.Skip(1).FirstOrDefault() is { } target
                && HlslShaderTarget.TryParse(target.Text, out HlslShaderTarget? parsed))
            {
                return options with
                {
                    PredefinedMacros = parsed.Value.CreateDxcMacros(HlslLanguageVersion.Hlsl2021).SetItems(options.PredefinedMacros),
                };
            }
        }

        return options;
    }

    /// <inheritdoc/>
    public VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens)
        => ReadVariantSpace(ReadPragmas(tokens));

    /// <inheritdoc/>
    public VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result)
        => ReadVariantSpace(result.Pragmas);

    /// <inheritdoc/>
    public ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result)
    {
        return
        [
            .. result.Pragmas
                .Where(pragma => pragma.Name == EntryPragma)
                .Select(pragma => pragma.Arguments.FirstOrDefault())
                .OfType<ShaderSyntaxToken>()
                .Where(name => name.Kind == ShaderSyntaxKind.IdentifierToken)
                .Select(name => new HlslEntryPoint(name.Text, "#pragma entry", name.GetLocation())),
        ];
    }

    /// <inheritdoc/>
    public bool LooksLikeVariantSymbol(string name)
        => name.StartsWith("USE_", StringComparison.Ordinal);

    /// <inheritdoc/>
    public bool IsGeneratedInclude(string path)
        => path.Replace('\\', '/').StartsWith("Generated/", StringComparison.Ordinal);

    /// <summary><c>#pragma variant</c> から構成の空間を作る。</summary>
    /// <param name="pragmas">読む <c>#pragma</c>。</param>
    /// <returns>構成の空間。</returns>
    private static VariantSpace ReadVariantSpace(IEnumerable<PragmaDirective> pragmas)
    {
        VariantSpace space = VariantSpace.Empty;

        foreach (PragmaDirective pragma in pragmas.Where(p => p.Name == VariantPragma))
        {
            ImmutableArray<ShaderSyntaxToken> arguments = [.. pragma.Arguments];

            if (arguments.IsEmpty || arguments[0].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            ShaderSyntaxToken name = arguments[0];
            List<int> values = [];

            foreach (ShaderSyntaxToken value in arguments.Skip(1))
            {
                if (int.TryParse(value.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                {
                    values.Add(number);
                }
            }

            // 宣言の位置を持たせると、使っていない宣言を HL0331 で報告できる。
            space = space.Union(values.Count >= 2
                ? VariantSpace.ForValues(name.Text, values, name.GetLocation())
                : new VariantSpace([new VariantDeclaration(name.Text, VariantSymbolKind.Compiled, name.GetLocation())], []));
        }

        return space;
    }

    /// <summary>字句解析しただけのトークンから <c>#pragma</c> の行を読む。</summary>
    /// <param name="tokens">トークン。</param>
    /// <returns><c>#pragma</c> の行。</returns>
    /// <remarks>展開の前に構成の軸を知るために使う。展開の後は <see cref="PreprocessResult.Pragmas"/> を読む。</remarks>
    private static IEnumerable<PragmaDirective> ReadPragmas(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "pragma"
                || tokens[i + 2].IsAtLineStart)
            {
                continue;
            }

            int end = i + 2;

            while (end < tokens.Length && !tokens[end].IsAtLineStart && tokens[end].Kind != ShaderSyntaxKind.EndOfFileToken)
            {
                end++;
            }

            ImmutableArray<ShaderSyntaxToken> line = tokens[(i + 2)..end];
            yield return new PragmaDirective(line, line[0]);
            i = end - 1;
        }
    }
}

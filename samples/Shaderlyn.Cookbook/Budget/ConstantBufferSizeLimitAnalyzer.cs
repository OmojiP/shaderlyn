using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 定数バッファの大きさを制限する。
/// </summary>
/// <remarks>
/// <para>
/// <b>これは見積もりである。</b>
/// HLSL の定数バッファは 16 バイトの区切りに詰められ、
/// 区切りをまたぐ要素は次の区切りへ送られる。配列と行列は要素ごとに区切りを占める。
/// その規則で数えるが、実際の大きさはコンパイラと対象環境で変わりうる。
/// </para>
/// <para>
/// <b>大きさの分からない型が 1 つでもあれば報告しない。</b>
/// 構造体のメンバーや、展開されなかったマクロがそれにあたる。
/// 一部を飛ばして合計を出すと、小さすぎる見積もりを正しい数として示すことになる。
/// </para>
/// <para>
/// <c>half</c> は 4 バイトとして数える。多くの環境で <c>float</c> に昇格されるためである。
/// </para>
/// <para>
/// <b>構成ごとに見積もる。</b>
/// <c>#ifdef _A</c> と <c>#else</c> で書き分けたメンバーは、両方の分岐を並べた木には並んでいるが、同時には存在しない。
/// 構成ごとに存在するメンバーだけで見積もり、その最大を上限と比べる
/// (<see cref="Semantics.ShaderCompilation.TryEnumerateConfigurations"/>)。
/// </para>
/// </remarks>
public sealed class ConstantBufferSizeLimitAnalyzer(int maxBytes, params string[] bufferNames) : HlslRuleAnalyzer
{
    /// <summary>スカラー型 1 成分の大きさ。</summary>
    private static readonly ImmutableDictionary<string, int> ScalarSizes =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["float"] = 4,
            ["half"] = 4,
            ["fixed"] = 4,
            ["int"] = 4,
            ["uint"] = 4,
            ["bool"] = 4,
            ["dword"] = 4,
            ["min16float"] = 4,
            ["min16int"] = 4,
            ["min16uint"] = 4,
            ["min10float"] = 4,
            ["double"] = 8,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private readonly ImmutableHashSet<string> _names = [.. bufferNames];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.ConstantBufferTooLarge];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<ConstantBufferDeclarationSyntax>(c =>
        {
            if (!_names.IsEmpty && !_names.Contains(c.Node.Name))
            {
                return;
            }

            if (!c.Compilation.IsReportable(c.Node.Keyword)
                || !c.Compilation.TryEnumerateConfigurations(c.Node.Fields, c.Program, out var configurations))
            {
                return;
            }

            int bytes = 0;

            foreach (var configuration in configurations)
            {
                if (!TryMeasure(c.Node.Fields.Where(configuration.Contains), out int size))
                {
                    return;
                }

                bytes = Math.Max(bytes, size);
            }

            if (bytes > maxBytes)
            {
                c.ReportDiagnostic(
                    CookbookRules.ConstantBufferTooLarge,
                    (c.Node.NameToken ?? c.Node.Keyword).Span,
                    c.Node.Name.Length == 0 ? "(無名)" : c.Node.Name,
                    bytes,
                    maxBytes);
            }
        });
    }

    /// <summary>定数バッファの大きさを見積もる。</summary>
    /// <param name="fields">1 つの構成で存在するメンバー。</param>
    /// <param name="bytes">見積もった大きさ。</param>
    /// <returns>すべてのメンバーの大きさが分かれば <see langword="true"/>。</returns>
    private static bool TryMeasure(IEnumerable<VariableDeclarationSyntax> fields, out int bytes)
    {
        bytes = 0;
        int offset = 0;

        foreach (VariableDeclarationSyntax field in fields)
        {
            if (!TryMeasureType(field.Type.Name, out int elementSize, out bool wholeRegisters))
            {
                return false;
            }

            foreach (VariableDeclaratorSyntax variable in field.Variables)
            {
                int size = elementSize;
                bool aligned = wholeRegisters;

                if (variable.IsArray)
                {
                    // 長さの読み取りは構文の側が持っている。多次元も数えてくれる。
                    if (!variable.TryGetArrayLength(out int length))
                    {
                        return false;
                    }

                    size = length * Align(elementSize);
                    aligned = true;
                }

                // 区切りをまたぐ要素は次の区切りへ送られる。
                if (aligned || (offset % 16) + size > 16)
                {
                    offset = Align(offset);
                }

                offset += size;
            }
        }

        bytes = Align(offset);
        return true;
    }

    /// <summary>型 1 つ分の大きさを求める。</summary>
    /// <param name="typeName">型名。</param>
    /// <param name="size">大きさ。</param>
    /// <param name="wholeRegisters">16 バイトの区切りを占めるかどうか。</param>
    /// <returns>大きさが分かれば <see langword="true"/>。</returns>
    private static bool TryMeasureType(string typeName, out int size, out bool wholeRegisters)
    {
        size = 0;
        wholeRegisters = false;

        // 型名の読み解きは分類器が持っている。成分数の切り出しを自分で書かない。
        if (!HlslTypeClassifier.TryDescribeNumeric(typeName, out HlslNumericShape shape))
        {
            return false;
        }

        if (!ScalarSizes.TryGetValue(shape.BaseName, out int scalar))
        {
            return false;
        }

        // 行列は列優先 (HLSL の既定) として、列ごとに 1 つの区切りを占めるものとして数える。
        if (shape.Kind == HlslNumericKind.Matrix)
        {
            size = shape.Columns * 16;
            wholeRegisters = true;
            return true;
        }

        size = shape.Columns * scalar;
        return true;
    }

    /// <summary>16 バイトの区切りへ切り上げる。</summary>
    /// <param name="value">切り上げる値。</param>
    /// <returns>切り上げた値。</returns>
    private static int Align(int value) => (value + 15) / 16 * 16;
}

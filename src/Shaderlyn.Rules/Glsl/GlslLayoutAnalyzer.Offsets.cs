using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// uniform と buffer のブロックのメンバーの <c>offset</c> を、std140 / std430 の規則で並べて検査する
/// (glslang 16.6.0 の ParseHelper.cpp の <c>fixBlockUniformOffsets</c> と linkValidate.cpp の <c>getBaseAlignment</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 書いた <c>offset</c> はメンバーの型の揃えの倍数でなければならず、SPIR-V を作らないときは前のメンバーの範囲に重なってはならない。
/// </para>
/// <para>
/// 型の大きさと揃えが分からないメンバー (大きさを式で書いた配列、行の順の行列、分からない型など) があれば、そこから後ろは見ない。
/// scalar のパッキングも見ない。
/// </para>
/// </remarks>
internal sealed partial class GlslLayoutAnalyzer
{
    private sealed partial class Checker
    {
        /// <summary>ブロックのメンバーの offset を並べて検査する。</summary>
        private void CheckBlockOffsets(ImmutableArray<DeclarationSyntax> members, string? packing, bool rowMajorBlock)
        {
            if (packing is not ("std140" or "std430") || rowMajorBlock)
            {
                return;
            }

            bool std140 = packing == "std140";
            long current = 0;

            foreach (VariableDeclarationSyntax member in members.OfType<VariableDeclarationSyntax>())
            {
                LayoutSet memberLayout = new(member.ModifierTokens);

                if (memberLayout.Has("row_major"))
                {
                    return;
                }

                foreach (VariableDeclaratorSyntax declarator in member.Variables)
                {
                    if (BaseLayout(member.Type.NameToken.Text, declarator, std140, 0) is not { } shape)
                    {
                        return;
                    }

                    int alignment = shape.Alignment;

                    if (memberLayout.Get("offset") is { } offsetEntry && ValueOf(offsetEntry) is { } offset)
                    {
                        if (offset % alignment != 0)
                        {
                            Invalid(GlslRuleDescriptors.InvalidLayoutValue, declarator.NameToken,
                                $"offset {offset.ToString(CultureInfo.InvariantCulture)} は、メンバーの揃え {alignment.ToString(CultureInfo.InvariantCulture)} の倍数でなければなりません。");
                        }

                        if (_r.SpirvVersion == 0)
                        {
                            if (offset < current)
                            {
                                Invalid(GlslRuleDescriptors.InvalidLayoutValue, declarator.NameToken,
                                    $"offset {offset.ToString(CultureInfo.InvariantCulture)} は、前のメンバーの範囲 ({current.ToString(CultureInfo.InvariantCulture)} より前) に重なります。");
                            }

                            current = Math.Max(current, offset);
                        }
                        else
                        {
                            current = offset;
                        }
                    }

                    if (memberLayout.Get("align") is { } alignEntry && ValueOf(alignEntry) is { } align && align > 0 && (align & (align - 1)) == 0)
                    {
                        alignment = (int)Math.Max(alignment, align);
                    }

                    current = RoundUp(current, alignment) + shape.Size;
                }
            }
        }

        /// <summary>std140 / std430 の型の揃えと大きさ (getBaseAlignment)。分からなければ <see langword="null"/>。</summary>
        private (int Alignment, long Size)? BaseLayout(string typeName, VariableDeclaratorSyntax? declarator, bool std140, int depth)
        {
            if (depth > 8)
            {
                return null;
            }

            if (declarator is { IsArray: true })
            {
                if (declarator.ArrayDimensions != 1 || !declarator.TryGetArrayLength(out int length)
                    || BaseLayout(typeName, null, std140, depth + 1) is not { } element)
                {
                    return null;
                }

                int alignment = std140 ? Math.Max(16, element.Alignment) : element.Alignment;
                long stride = RoundUp(element.Size, alignment);
                return (alignment, stride * length);
            }

            if (_types is not null && _types.TryDescribeNumeric(typeName, out ShaderNumericShape shape))
            {
                int scalar = shape.BaseName switch
                {
                    "double" or "int64_t" or "uint64_t" => 8,
                    "float16_t" or "int16_t" or "uint16_t" => 2,
                    "int8_t" or "uint8_t" => 1,
                    _ => 4,
                };

                return shape.Kind switch
                {
                    ShaderNumericKind.Scalar => (scalar, scalar),
                    ShaderNumericKind.Vector when shape.Rows * shape.Columns == 2 => (2 * scalar, 2 * scalar),
                    ShaderNumericKind.Vector => (4 * scalar, (long)scalar * shape.Rows * shape.Columns),
                    ShaderNumericKind.Matrix => MatrixLayout(scalar, shape, std140),
                    _ => null,
                };
            }

            _structs ??= ReadStructs();

            if (!_structs.TryGetValue(typeName, out ImmutableArray<(string Type, VariableDeclaratorSyntax Declarator)> fields))
            {
                return null;
            }

            long size = 0;
            int maxAlignment = std140 ? 16 : 0;

            foreach ((string fieldType, VariableDeclaratorSyntax field) in fields)
            {
                if (BaseLayout(fieldType, field, std140, depth + 1) is not { } member)
                {
                    return null;
                }

                maxAlignment = Math.Max(maxAlignment, member.Alignment);
                size = RoundUp(size, member.Alignment) + member.Size;
            }

            return maxAlignment == 0 ? null : (maxAlignment, RoundUp(size, maxAlignment));
        }

        /// <summary>列の順の行列 (列ベクトルの配列) の揃えと大きさ。</summary>
        private static (int Alignment, long Size)? MatrixLayout(int scalar, ShaderNumericShape shape, bool std140)
        {
            // 列ベクトルの成分の数は行の数。GLSL の matCxR は C 列 R 行。
            int rows = shape.Columns;
            int columns = shape.Rows;
            int vectorAlignment = rows == 2 ? 2 * scalar : 4 * scalar;
            long vectorSize = (long)scalar * rows;
            int alignment = std140 ? Math.Max(16, vectorAlignment) : vectorAlignment;
            return (alignment, RoundUp(vectorSize, alignment) * columns);
        }
    }
}

using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 変換フィードバック (<c>xfb_buffer</c>・<c>xfb_offset</c>・<c>xfb_stride</c>) のバッファごとの検査。
/// glslang 16.6.0 の linkValidate.cpp の <c>addXfbBufferOffset</c> と <c>computeTypeXfbSize</c>、
/// localintermediate.h の <c>setXfbBufferStride</c>、ParseHelper.cpp の <c>fixXfbOffsets</c> と型の無い宣言の既定値を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 宣言を書いた順に読む。<c>layout(xfb_buffer = N) out;</c> で既定のバッファが変わる (初めは 0)。
/// <c>xfb_offset</c> を書いた出力は、バッファの中の範囲 (オフセットから型の大きさの分) を使い、前の範囲と重なれば誤り。
/// 同じバッファの <c>xfb_stride</c> は、どれも同じ値でなければならない。
/// </para>
/// <para>
/// <c>xfb_offset</c> を書いたブロックは、オフセットを書いていないメンバーに、前のメンバーの後ろから順にオフセットを割り当てる。
/// 型の大きさが分からないもの (大きさの決まっていない配列、型の分からない構造体) は範囲を覚えない。
/// </para>
/// </remarks>
internal sealed partial class GlslLayoutAnalyzer
{
    private sealed partial class Checker
    {
        private readonly Dictionary<long, long> _xfbStrides = [];
        private readonly Dictionary<long, List<(long Start, long End)>> _xfbRanges = [];
        private long _xfbDefaultBuffer;

        /// <summary>型の無い出力の宣言 (<c>layout(xfb_buffer = 3, xfb_stride = 64) out;</c>)。既定のバッファを変え、その stride を決める。</summary>
        private void XfbDefaults(ShaderSyntaxToken at, LayoutSet layout)
        {
            if (layout.Get("xfb_buffer") is { } buffer && ValueOf(buffer) is { } value)
            {
                _xfbDefaultBuffer = value;
            }

            if (layout.Get("xfb_stride") is { } stride && ValueOf(stride) is { } strideValue)
            {
                SetXfbStride(at, _xfbDefaultBuffer, strideValue);
            }
        }

        /// <summary>出力の変数 1 つ。</summary>
        private void XfbVariable(ShaderSyntaxToken at, LayoutSet layout, string typeName, VariableDeclaratorSyntax declarator)
        {
            if (!layout.HasXfb)
            {
                return;
            }

            long buffer = layout.Get("xfb_buffer") is { } b && ValueOf(b) is { } value ? value : _xfbDefaultBuffer;

            if (layout.Get("xfb_offset") is { } offset && ValueOf(offset) is { } start && XfbSize(typeName, declarator) is { } size)
            {
                CheckXfbAlignment(at, start, size.Alignment);
                AddXfbRange(at, buffer, start, size.Size);
            }

            if (layout.Get("xfb_stride") is { } stride && ValueOf(stride) is { } strideValue)
            {
                SetXfbStride(at, buffer, strideValue);
            }
        }

        /// <summary>出力のブロック 1 つ。メンバーを先に見て、ブロックの stride を後で見る (glslang と同じ順)。</summary>
        private void XfbBlock(ShaderSyntaxToken at, LayoutSet layout, ImmutableArray<DeclarationSyntax> members, bool builtIn)
        {
            long buffer = layout.Get("xfb_buffer") is { } b && ValueOf(b) is { } value ? value : _xfbDefaultBuffer;

            // ブロックに xfb_offset があるときだけ、オフセットを書いていないメンバーに割り当てる。
            bool assigns = layout.Has("xfb_offset");
            long? next = layout.Get("xfb_offset") is { } blockOffset ? ValueOf(blockOffset) : null;

            foreach (VariableDeclarationSyntax member in members.OfType<VariableDeclarationSyntax>())
            {
                LayoutSet memberLayout = new(member.ModifierTokens);

                // メンバーが xfb_buffer を書いていれば、それを使う (ブロックと食い違う誤りは別に報告する)。
                long memberBuffer = memberLayout.Get("xfb_buffer") is { } own && ValueOf(own) is { } ownValue ? ownValue : buffer;

                foreach (VariableDeclaratorSyntax declarator in member.Variables)
                {
                    // 組み込みのブロック (gl_PerVertex) のメンバーの誤りは、glslang はブロックの位置で報告する。
                    ShaderSyntaxToken memberAt = builtIn ? at : declarator.NameToken;
                    (long Size, int Alignment)? size = XfbSize(member.Type.NameToken.Text, declarator);
                    long? offset = memberLayout.Get("xfb_offset") is { } ownOffset ? ValueOf(ownOffset) : null;

                    if (offset is { } written && size is { } writtenSize)
                    {
                        CheckXfbAlignment(memberAt, written, writtenSize.Alignment);
                    }
                    else if (offset is null && assigns && next is { } auto && size is { } known)
                    {
                        offset = RoundUp(auto, known.Alignment);
                    }

                    if (offset is { } start && size is { } used)
                    {
                        AddXfbRange(memberAt, memberBuffer, start, used.Size);
                        next = start + used.Size;
                    }
                    else
                    {
                        next = null;
                    }

                    if (memberLayout.Get("xfb_stride") is { } stride && ValueOf(stride) is { } strideValue)
                    {
                        SetXfbStride(memberAt, memberBuffer, strideValue);
                    }
                }
            }

            if (layout.Get("xfb_stride") is { } blockStride && ValueOf(blockStride) is { } blockStrideValue)
            {
                SetXfbStride(at, buffer, blockStrideValue);
            }
        }

        /// <summary>
        /// 書いた xfb_offset が、型の最初の成分の大きさの倍数か (layoutTypeCheck)。64 ビットの型を含めば 8、32 ビットの型を含めば 4 の倍数でなければならない。
        /// 16 ビットと 8 ビットの型だけのものは、glslang も見ない。
        /// </summary>
        private void CheckXfbAlignment(ShaderSyntaxToken at, long offset, int alignment)
        {
            if (alignment >= 4 && offset % alignment != 0)
            {
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at,
                    $"xfb_offset {offset.ToString(CultureInfo.InvariantCulture)} は {alignment.ToString(CultureInfo.InvariantCulture)} の倍数でなければなりません"
                    + (alignment == 8 ? " (double か 64 ビットの整数を含む型)。" : " (型の最初の成分の大きさ)。"));
            }
        }

        /// <summary>バッファの stride を決める。前に決めた値と違えば誤り (setXfbBufferStride)。</summary>
        private void SetXfbStride(ShaderSyntaxToken at, long buffer, long stride)
        {
            if (_xfbStrides.TryGetValue(buffer, out long previous))
            {
                if (previous != stride)
                {
                    Invalid(GlslRuleDescriptors.InvalidLayoutValue, at,
                        $"xfb_buffer {buffer.ToString(CultureInfo.InvariantCulture)} の xfb_stride は、前の宣言の {previous.ToString(CultureInfo.InvariantCulture)} と同じにしてください。");
                }

                return;
            }

            _xfbStrides[buffer] = stride;
        }

        /// <summary>バッファの範囲を使う。前の範囲と重なれば誤り (addXfbBufferOffset)。</summary>
        private void AddXfbRange(ShaderSyntaxToken at, long buffer, long start, long size)
        {
            if (size <= 0)
            {
                return;
            }

            long end = start + size - 1;

            if (!_xfbRanges.TryGetValue(buffer, out List<(long Start, long End)>? ranges))
            {
                ranges = _xfbRanges[buffer] = [];
            }

            foreach ((long otherStart, long otherEnd) in ranges)
            {
                if (start <= otherEnd && otherStart <= end)
                {
                    Invalid(GlslRuleDescriptors.InvalidLayoutValue, at,
                        $"xfb_buffer {buffer.ToString(CultureInfo.InvariantCulture)} の xfb_offset {Math.Max(start, otherStart).ToString(CultureInfo.InvariantCulture)} は、ほかの宣言の範囲と重なります。");
                    return;
                }
            }

            ranges.Add((start, end));
        }

        /// <summary>変換フィードバックのバッファの中での型の大きさと、そろえる単位 (computeTypeXfbSize)。分からなければ <see langword="null"/>。</summary>
        private (long Size, int Alignment)? XfbSize(string typeName, VariableDeclaratorSyntax? declarator, int depth = 0)
        {
            long count = 1;

            if (declarator is { IsArray: true })
            {
                if (!declarator.TryGetArrayLength(out int length))
                {
                    return null;
                }

                count = length;
            }

            return XfbTypeSize(typeName, depth) is { } element ? (element.Size * count, element.Alignment) : null;
        }

        /// <summary>配列でない型の大きさと、そろえる単位 (64 ビットを含めば 8、32 ビットなら 4、16 ビットなら 2)。</summary>
        private (long Size, int Alignment)? XfbTypeSize(string typeName, int depth)
        {
            if (depth > 8)
            {
                return null;
            }

            if (_types is not null && _types.TryDescribeNumeric(typeName, out ShaderNumericShape shape))
            {
                int component = shape.BaseName switch
                {
                    "double" or "int64_t" or "uint64_t" => 8,
                    "float16_t" or "int16_t" or "uint16_t" => 2,
                    "int8_t" or "uint8_t" => 1,
                    _ => 4,
                };

                return ((long)component * shape.Rows * shape.Columns, component);
            }

            _structs ??= ReadStructs();

            if (!_structs.TryGetValue(typeName, out ImmutableArray<(string Type, VariableDeclaratorSyntax Declarator)> members))
            {
                return null;
            }

            long size = 0;
            int alignment = 1;

            foreach ((string memberType, VariableDeclaratorSyntax member) in members)
            {
                if (XfbSize(memberType, member, depth + 1) is not { } memberSize)
                {
                    return null;
                }

                size = RoundUp(size, memberSize.Alignment) + memberSize.Size;
                alignment = Math.Max(alignment, memberSize.Alignment);
            }

            return (RoundUp(size, alignment), alignment);
        }

        /// <summary><paramref name="value"/> を <paramref name="alignment"/> の倍数に切り上げる。</summary>
        private static long RoundUp(long value, int alignment) => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
    }
}

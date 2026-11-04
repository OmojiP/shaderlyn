using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// <c>location</c> の重なりの検査。glslang 16.6.0 の linkValidate.cpp の <c>addUsedLocation</c> と <c>computeTypeLocationSize</c>、
/// ParseHelper.cpp の <c>fixBlockLocations</c> を移したもの。
/// </summary>
internal sealed partial class GlslLayoutAnalyzer
{
    /// <summary>使った location と成分の範囲 (glslang の <c>TIoRange</c>)。</summary>
    private sealed record UsedLocation(
        int Set,
        long First,
        long Last,
        long FirstComponent,
        long LastComponent,
        long Index,
        string BaseType,
        string Interpolation);

    private sealed partial class Checker
    {
        private readonly List<UsedLocation> _usedLocations = [];
        private Dictionary<string, ImmutableArray<(string Type, VariableDeclaratorSyntax Declarator)>>? _structs;

        /// <summary>
        /// 変数の location を覚え、前の宣言と重なれば報告する (glslang の addUsedLocation)。
        /// </summary>
        private void AddUsedLocation(
            ShaderSyntaxToken at,
            LayoutSet layout,
            Storage storage,
            string typeName,
            VariableDeclaratorSyntax? declarator,
            ImmutableArray<ShaderSyntaxToken> modifiers,
            long? location = null)
        {
            if ((location ?? (layout.Get("location") is { } entry ? ValueOf(entry) : null)) is not { } first)
            {
                return;
            }

            int set;

            switch (storage)
            {
                case Storage.In:
                    set = 0;
                    break;
                case Storage.Out:
                    set = 1;
                    break;
                case Storage.Uniform:
                    set = 2;
                    break;
                case Storage.Buffer:
                    set = 3;
                    break;
                case Storage.Other when modifiers.Any(m => m.Text == "tileImageEXT"):
                    set = 4;
                    break;
                default:
                    return;
            }

            bool patch = modifiers.Any(m => m.Text == "patch");
            TypeShape type = TypeShape.Of(_types, typeName);
            long? size;

            if (set == 4 || storage is Storage.Uniform or Storage.Buffer)
            {
                size = declarator is null || !declarator.IsArray ? 1 : declarator.TryGetArrayLength(out int length) ? length : 1;
            }
            else
            {
                bool arrayedIo = Stage switch
                {
                    GlslShaderStage.Geometry => storage == Storage.In,
                    GlslShaderStage.TessellationControl => !patch,
                    GlslShaderStage.TessellationEvaluation => !patch && storage == Storage.In,
                    GlslShaderStage.Fragment => storage == Storage.In && modifiers.Any(m => m.Text is "pervertexNV" or "pervertexEXT"),
                    GlslShaderStage.Mesh => storage == Storage.Out && !modifiers.Any(m => m.Text == "perTaskNV"),
                    _ => false,
                };
                size = LocationSize(typeName, declarator, arrayedIo, storage == Storage.In && Stage == GlslShaderStage.Vertex);
            }

            if (size is not { } count)
            {
                return;
            }

            long? component = layout.Get("component") is { } componentEntry ? ValueOf(componentEntry) : null;
            long index = layout.Get("index") is { } indexEntry ? ValueOf(indexEntry) ?? 0 : 0;
            string interpolation = string.Join(" ", modifiers.Where(m => m.Text is "centroid" or "smooth" or "flat" or "sample" or "patch").Select(m => m.Text).Order(StringComparer.Ordinal));
            // タイルのイメージ (attachmentEXT) の型は、読む値の型とする (iattachmentEXT は int)。
            string baseType = set == 4 ? typeName switch { "iattachmentEXT" => "int", "uattachmentEXT" => "uint", _ => "float" } : type.BaseType;

            // 頂点シェーダーの入力は、デスクトップの OpenGL 向けなら重ねてよい。
            bool check = !(!_r.IsEs && Stage == GlslShaderStage.Vertex && storage == Storage.In) || _r.Vulkan == true;

            List<UsedLocation> ranges = [];

            if (count == 2 && type.IsDouble && type.VectorSize == 3 && storage is Storage.In or Storage.Out)
            {
                // dvec3 は 2 つの location にまたがる。2 つ目は成分 0 と 1 だけを使う。
                ranges.Add(new UsedLocation(set, first, first, 0, 3, index, baseType, interpolation));
                ranges.Add(new UsedLocation(set, first + 1, first + 1, 0, 1, index, baseType, interpolation));
            }
            else
            {
                long firstComponent = 0;
                long lastComponent = 3;

                if (component is not null || type.VectorSize is not null)
                {
                    firstComponent = component ?? 0;
                    lastComponent = firstComponent + ((type.VectorSize ?? 0) * (type.IsDouble ? 2 : 1)) - 1;
                }

                ranges.Add(new UsedLocation(set, first, first + count - 1, firstComponent, lastComponent, index, baseType, interpolation));
            }

            foreach (UsedLocation range in ranges)
            {
                if (check && Collide(range) is { } collision)
                {
                    long repeated = Math.Max(range.First, collision.Location.First);
                    string number = repeated.ToString(CultureInfo.InvariantCulture);

                    if (!collision.TypeMismatch)
                    {
                        Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"location {number} は、ほかの宣言の location と重なります。");
                    }
                    else if (storage is Storage.In or Storage.Out || set == 4)
                    {
                        Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"location {number} を分け合う宣言は、型と補間の修飾子を同じにしなければなりません。");
                    }

                    return;
                }

                _usedLocations.Add(range);
            }
        }

        private (UsedLocation Location, bool TypeMismatch)? Collide(UsedLocation range)
        {
            // タイルのイメージ (tileImageEXT) と出力は location を分け合い、重なるなら型が同じでなければならない。
            if (range.Set is 1 or 4 && _usedLocations.FirstOrDefault(u => u.Set == (range.Set == 4 ? 1 : 4) && range.First <= u.Last && u.First <= range.Last && u.BaseType != range.BaseType) is { } aliased)
            {
                return (aliased, true);
            }

            foreach (UsedLocation used in _usedLocations.Where(u => u.Set == range.Set))
            {
                bool locations = range.First <= used.Last && used.First <= range.Last;

                if (locations && range.FirstComponent <= used.LastComponent && used.FirstComponent <= range.LastComponent && range.Index == used.Index)
                {
                    return (used, false);
                }

                if (locations && (!CompatibleTypes(range.BaseType, used.BaseType) || range.Interpolation != used.Interpolation))
                {
                    return (used, true);
                }
            }

            return null;
        }

        /// <summary>同じ location の別の成分に置ける型か。符号の有無だけが違う整数は置ける。</summary>
        private static bool CompatibleTypes(string a, string b)
            => a == b || Unsigned(a) == Unsigned(b) && a.Length > 0;

        private static string Unsigned(string type) => type switch
        {
            "int" => "uint",
            "int8_t" => "uint8_t",
            "int16_t" => "uint16_t",
            "int64_t" => "uint64_t",
            _ => type,
        };

        /// <summary>
        /// 入力と出力の型が使う location の数 (glslang の computeTypeLocationSize)。分からなければ <see langword="null"/>。
        /// </summary>
        /// <param name="typeName">型の名前。</param>
        /// <param name="declarator">配列の大きさを持つ宣言子。</param>
        /// <param name="arrayedIo">いちばん外の配列の次元を除くか (ジオメトリシェーダーの入力など)。</param>
        /// <param name="vertexInput">頂点シェーダーの入力か (dvec3 / dvec4 も 1 つ)。</param>
        private long? LocationSize(string typeName, VariableDeclaratorSyntax? declarator, bool arrayedIo, bool vertexInput)
        {
            long elements = 1;

            if (declarator is { IsArray: true })
            {
                if (arrayedIo)
                {
                    if (declarator.ArrayDimensions != 1)
                    {
                        return null;
                    }
                }
                else if (declarator.TryGetArrayLength(out int length))
                {
                    elements = length;
                }
            }

            return TypeLocationSize(typeName, vertexInput, 0) is { } size ? elements * size : null;
        }

        private long? TypeLocationSize(string typeName, bool vertexInput, int depth)
        {
            if (depth > 8)
            {
                return null;
            }

            TypeShape type = TypeShape.Of(_types, typeName);

            if (type.IsMatrix && _types is not null && _types.TryDescribeNumeric(typeName, out ShaderNumericShape shape))
            {
                // 列の数だけのベクトル。dmat3 / dmat4 の列は 2 つずつ。GLSL の形は、列の数を Rows、行の数を Columns に持つ。
                long column = !vertexInput && type.IsDouble && shape.Columns > 2 ? 2 : 1;
                return shape.Rows * column;
            }

            if (type.VectorSize is { } vector)
            {
                return !vertexInput && type.IsDouble && vector > 2 ? 2 : 1;
            }

            if (!type.IsStruct)
            {
                return null;
            }

            _structs ??= ReadStructs();

            if (!_structs.TryGetValue(typeName, out ImmutableArray<(string Type, VariableDeclaratorSyntax Declarator)> members))
            {
                return null;
            }

            long total = 0;

            foreach ((string memberType, VariableDeclaratorSyntax member) in members)
            {
                long count = 1;

                if (member.IsArray)
                {
                    if (!member.TryGetArrayLength(out int length))
                    {
                        return null;
                    }

                    count = length;
                }

                if (TypeLocationSize(memberType, vertexInput, depth + 1) is not { } size)
                {
                    return null;
                }

                total += count * size;
            }

            return total;
        }

        /// <summary>構造体の宣言 (<c>struct S { … };</c>) のメンバー。</summary>
        private Dictionary<string, ImmutableArray<(string Type, VariableDeclaratorSyntax Declarator)>> ReadStructs()
        {
            Dictionary<string, ImmutableArray<(string, VariableDeclaratorSyntax)>> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax structure in _root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (structure.Keyword.Text == "struct" && structure.NameToken is { } name)
                {
                    structs[name.Text] = [.. structure.Fields.SelectMany(f => f.Variables.Select(v => (f.Type.NameToken.Text, v)))];
                }
            }

            return structs;
        }

        /// <summary>
        /// in / out のブロックの location (glslang の fixBlockLocations と、メンバーの layoutTypeCheck)。
        /// </summary>
        private void AddBlockLocations(
            ShaderSyntaxToken at,
            LayoutSet layout,
            Storage storage,
            ImmutableArray<DeclarationSyntax> members,
            VariableDeclaratorSyntax? instance,
            ImmutableArray<ShaderSyntaxToken> modifiers)
        {
            if (storage is not (Storage.In or Storage.Out))
            {
                return;
            }

            List<(VariableDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator, LayoutSet Layout)> fields =
            [
                .. members.OfType<VariableDeclarationSyntax>().SelectMany(m => m.Variables.Select(v => (m, v, new LayoutSet(m.ModifierTokens)))),
            ];

            if (fields.Count == 0 || fields.Any(f => f.Declarator.Name.StartsWith("gl_", StringComparison.Ordinal)))
            {
                return;
            }

            bool withLocation = fields.Any(f => f.Layout.HasLocation);
            bool withoutLocation = fields.Any(f => !f.Layout.HasLocation);

            if (!layout.HasLocation && withLocation && withoutLocation)
            {
                Invalid(GlslRuleDescriptors.LayoutRequired, at,
                    "ブロックに location が無いなら、メンバーの location は、すべてに書くか、どれにも書かないかにしてください。");
                return;
            }

            if (withLocation)
            {
                // ブロックの location をメンバーへ配り、メンバーごとに覚える。
                long? next = layout.Get("location") is { } entry ? ValueOf(entry) : 0;

                foreach ((VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator, LayoutSet memberLayout) in fields)
                {
                    long? location = memberLayout.Get("location") is { } memberEntry ? ValueOf(memberEntry) : next;

                    if (location is null)
                    {
                        return;
                    }

                    AddUsedLocation(declarator.NameToken, memberLayout, storage, declaration.Type.NameToken.Text, declarator,
                        [.. modifiers, .. declaration.ModifierTokens], location);

                    next = LocationSize(declaration.Type.NameToken.Text, declarator, arrayedIo: false, storage == Storage.In && Stage == GlslShaderStage.Vertex) is { } size
                        ? location + size
                        : null;
                }

                return;
            }

            if (layout.Get("location") is not { } blockEntry || ValueOf(blockEntry) is not { } blockLocation)
            {
                return;
            }

            // ブロックの location だけなら、ブロック全体で 1 つの範囲を使う。
            long total = 0;

            foreach ((VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator, _) in fields)
            {
                if (LocationSize(declaration.Type.NameToken.Text, declarator, arrayedIo: false, vertexInput: false) is not { } size)
                {
                    return;
                }

                total += size;
            }

            bool patch = modifiers.Any(m => m.Text == "patch");
            bool arrayedIo = Stage switch
            {
                GlslShaderStage.Geometry => storage == Storage.In,
                GlslShaderStage.TessellationControl => !patch,
                GlslShaderStage.TessellationEvaluation => !patch && storage == Storage.In,
                GlslShaderStage.Mesh => storage == Storage.Out,
                _ => false,
            };

            if (instance is { IsArray: true } && !arrayedIo)
            {
                if (!instance.TryGetArrayLength(out int length))
                {
                    return;
                }

                total *= length;
            }

            int set = storage == Storage.In ? 0 : 1;
            UsedLocation range = new(set, blockLocation, blockLocation + total - 1, 0, 3, 0, "block", string.Empty);

            if (Collide(range) is { } collision)
            {
                long repeated = Math.Max(range.First, collision.Location.First);
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"location {repeated.ToString(CultureInfo.InvariantCulture)} は、ほかの宣言の location と重なります。");
                return;
            }

            _usedLocations.Add(range);
        }
    }
}

using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 宣言ごとの <c>layout(…)</c> の検査。glslang 16.6.0 の ParseHelper.cpp の
/// <c>checkNoShaderLayouts</c>、<c>updateStandaloneQualifierDefaults</c>、<c>layoutQualifierCheck</c>、
/// <c>layoutTypeCheck</c>、<c>layoutObjectCheck</c>、<c>declareBlock</c> のメンバーの検査を移したもの。
/// </summary>
internal sealed partial class GlslLayoutAnalyzer
{
    // glslang の既定の資源の上限 (glslang-default-resource-limits)。
    private const int MaxDrawBuffers = 32;
    private const int MaxCombinedTextureImageUnits = 80;
    private const int MaxAtomicCounterBindings = 1;

    private static readonly string[] AepShaderIoBlocks = ["GL_EXT_shader_io_blocks", "GL_OES_shader_io_blocks"];

    private static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal)
    {
        "points", "lines", "lines_adjacency", "line_strip", "triangles", "triangles_adjacency", "triangle_strip", "quads", "isolines",
    };

    private static readonly HashSet<string> NonDefaultQualifiers = new(StringComparer.Ordinal)
    {
        // 補助・メモリ・補間・精度の修飾子 (glslang の isAuxiliary / isMemory / isInterpolation / precision)。
        "centroid", "sample", "patch", "coherent", "devicecoherent", "queuefamilycoherent", "workgroupcoherent", "subgroupcoherent",
        "shadercallcoherent", "nonprivate", "volatile", "restrict", "readonly", "writeonly", "nontemporal",
        "flat", "smooth", "noperspective", "__explicitInterpAMD", "pervertexNV", "pervertexEXT", "perprimitiveNV", "perviewNV", "pertaskNV",
        "highp", "mediump", "lowp",
    };

    /// <summary>ブロック (<c>uniform UBO { … }</c>) か。<c>struct</c> でない <see cref="StructDeclarationSyntax"/> はブロックである。</summary>
    private static bool IsBlock(StructDeclarationSyntax structure) => structure.Keyword.Text != "struct";

    /// <summary>記憶域の修飾子 (glslang の <c>TStorageQualifier</c> のうち、layout の検査に要るもの)。</summary>
    private enum Storage
    {
        /// <summary>無い (関数の外なら EvqGlobal、中なら EvqTemporary)。</summary>
        None,

        /// <summary><c>const</c>。</summary>
        Const,

        /// <summary><c>in</c> (と <c>attribute</c>)。</summary>
        In,

        /// <summary><c>out</c>。</summary>
        Out,

        /// <summary><c>uniform</c>。</summary>
        Uniform,

        /// <summary><c>buffer</c>。</summary>
        Buffer,

        /// <summary><c>shared</c>。</summary>
        Shared,

        /// <summary>ディスクリプターヒープ (<c>resourceheap</c> / <c>samplerheap</c>)。</summary>
        Heap,

        /// <summary>そのほか (レイトレーシングの <c>rayPayloadEXT</c>、<c>taskNV</c>、<c>tileImageEXT</c> など)。検査しない。</summary>
        Other,
    }

    /// <summary>宣言の中の layout の一揃い。名前は小文字にしてある。後に書いたものが勝つ。</summary>
    private sealed class LayoutSet
    {
        private readonly Dictionary<string, GlslLayoutEntry> _entries = new(StringComparer.Ordinal);

        public LayoutSet(ImmutableArray<ShaderSyntaxToken> modifiers)
        {
            foreach (GlslLayoutEntry entry in GlslLayoutQualifier.Read(modifiers))
            {
                Entries.Add(entry);
                _entries[entry.Name.Text.ToLowerInvariant()] = entry;
            }
        }

        public List<GlslLayoutEntry> Entries { get; } = [];

        public bool IsEmpty => Entries.Count == 0;

        public bool Has(string id) => _entries.ContainsKey(id);

        public GlslLayoutEntry? Get(string id) => _entries.TryGetValue(id, out GlslLayoutEntry entry) ? entry : null;

        public bool HasLocation => Has("location");

        public bool HasAnyLocation => Has("location") || Has("component") || Has("index");

        public bool HasXfb => _entries.Keys.Any(k => k.StartsWith("xfb_", StringComparison.Ordinal));

        public bool HasMatrix => Has("row_major") || Has("column_major");

        public string? Packing => new[] { "std140", "std430", "scalar", "packed", "shared" }.LastOrDefault(Has);

        public string? Format => _entries.Keys.FirstOrDefault(k => EsFloatFormats.Contains(k) || FloatFormats.Contains(k)
            || EsIntFormats.Contains(k) || IntFormats.Contains(k) || EsUintFormats.Contains(k) || UintFormats.Contains(k));

        public bool IsShaderRecord => Has("shaderrecordnv") || Has("shaderrecordext");
    }

    /// <summary>修飾子の記憶域。括弧の中 (<c>layout(shared)</c>) は見ない。</summary>
    private static Storage StorageOf(ImmutableArray<ShaderSyntaxToken> modifiers, GlslShaderStage? stage)
    {
        int depth = 0;
        Storage storage = Storage.None;

        foreach (ShaderSyntaxToken token in modifiers)
        {
            if (token.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.CloseParenToken)
            {
                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            Storage? found = token.Text switch
            {
                "const" => Storage.Const,
                "in" or "attribute" => Storage.In,
                "out" => Storage.Out,
                "varying" => stage == GlslShaderStage.Fragment ? Storage.In : Storage.Out,
                "uniform" => Storage.Uniform,
                "buffer" => Storage.Buffer,
                "shared" => Storage.Shared,
                "resourceheap" or "samplerheap" => Storage.Heap,
                "inout" or "rayPayloadEXT" or "rayPayloadInEXT" or "hitAttributeEXT" or "callableDataEXT" or "callableDataInEXT"
                    or "shaderRecordEXT" or "rayPayloadNV" or "rayPayloadInNV" or "hitAttributeNV" or "callableDataNV" or "callableDataInNV"
                    or "shaderRecordNV" or "hitObjectAttributeEXT" or "hitObjectAttributeNV" or "taskPayloadSharedEXT" or "taskNV"
                    or "tileImageEXT" or "spirv_storage_class" => Storage.Other,
                _ => null,
            };

            if (found is { } value)
            {
                storage = value;
            }
        }

        return storage;
    }

    /// <summary>修飾子の位置 (glslang の type_qualifier の位置)。layout が先頭なら、その中の最初の名前。</summary>
    private static ShaderSyntaxToken QualifierLocation(ImmutableArray<ShaderSyntaxToken> modifiers, LayoutSet layout)
        => modifiers[0].Text == "layout" && layout.Entries.Count > 0 ? layout.Entries[0].Name : modifiers[0];

    private sealed partial class Checker
    {
        private readonly Dictionary<string, long> _shaderSettings = new(StringComparer.Ordinal);
        private string? _uniformPacking;
        private string? _bufferPacking;

        /// <summary>宣言を検査する。</summary>
        public void Visit(ShaderSyntaxNode node)
        {
            switch (node)
            {
                case IncompleteDeclarationSyntax { Tokens: [_, ..] tokens }:
                    VisitQualifierOnly(tokens);
                    break;

                // ブロックのメンバーは、ブロックで検査する。
                case VariableDeclarationSyntax variable when variable.Parent is InterfaceBlockDeclarationSyntax
                                                             || variable.Parent is StructDeclarationSyntax structure && IsBlock(structure):
                    break;

                // 構造体のメンバー (glslang の memberQualifierCheck)。
                case VariableDeclarationSyntax { Parent: StructDeclarationSyntax, ModifierTokens: [_, ..] } member:
                    CheckNoShaderLayouts(new LayoutSet(member.ModifierTokens));
                    break;

                case VariableDeclarationSyntax { ModifierTokens: [_, ..] } variable:
                    VisitVariable(variable);
                    break;

                case ParameterSyntax { ModifierTokens: [_, ..] } parameter:
                    CheckNoShaderLayouts(new LayoutSet(parameter.ModifierTokens));
                    break;

                case FunctionDeclarationSyntax { ModifierTokens: [_, ..] } function:
                    CheckNoShaderLayouts(new LayoutSet(function.ModifierTokens));
                    break;

                case StructDeclarationSyntax structure when IsBlock(structure):
                    VisitBlock([.. structure.Modifiers, structure.Keyword], structure.NameToken?.Text, structure.Members,
                        structure.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().FirstOrDefault());
                    break;

                case InterfaceBlockDeclarationSyntax block:
                    VisitBlock(block.Modifiers, block.NameToken.Text, block.Members, instance: null);
                    break;

                default:
                    break;
            }
        }

        /// <summary>型の無い宣言 (<c>layout(…) in;</c>) と、既にある名前に修飾子を足す宣言 (<c>invariant gl_Position;</c>)。</summary>
        private void VisitQualifierOnly(ImmutableArray<ShaderSyntaxToken> tokens)
        {
            ImmutableArray<ShaderSyntaxToken> qualifiers = tokens[..^1];

            if (qualifiers.IsEmpty || qualifiers[0].Text == "precision")
            {
                return;
            }

            LayoutSet layout = new(qualifiers);

            // 最後が名前なら、既にある名前に修飾子を足す宣言である。
            ShaderSyntaxToken last = qualifiers[^1];
            bool addsToExisting = last.Kind == ShaderSyntaxKind.IdentifierToken && !GlslKeywords.Qualifiers.Contains(last.Text);

            if (addsToExisting)
            {
                CheckNoShaderLayouts(layout);
                return;
            }

            if (layout.IsEmpty && !qualifiers.Any(t => NonDefaultQualifiers.Contains(t.Text)))
            {
                return;
            }

            CheckDefaults(qualifiers, layout);
        }

        /// <summary>型の無い宣言の検査 (glslang の updateStandaloneQualifierDefaults)。</summary>
        private void CheckDefaults(ImmutableArray<ShaderSyntaxToken> qualifiers, LayoutSet layout)
        {
            ShaderSyntaxToken at = QualifierLocation(qualifiers, layout);
            Storage storage = StorageOf(qualifiers, Stage);

            void Place(string message) => Invalid(GlslRuleDescriptors.QualifierNotAllowed, at, message);

            foreach (GlslLayoutEntry entry in layout.Entries)
            {
                string id = entry.Name.Text.ToLowerInvariant();
                string name = entry.Name.Text;

                if (IsVerticesName(id) || id == "max_primitives")
                {
                    if (storage != Storage.Out)
                    {
                        Place($"'{name}' は out の宣言 (layout({name} = …) out;) にだけ書けます。");
                    }

                    Setting(at, id == "max_primitives" ? "max_primitives" : "vertices", ValueOf(entry), name);
                }
                else if (id == "invocations" && Stage == GlslShaderStage.Geometry)
                {
                    if (storage != Storage.In)
                    {
                        Place("'invocations' は in の宣言にだけ書けます。");
                    }

                    Setting(at, "invocations", ValueOf(entry), name);
                }
                else if (IsPrimitiveName(id))
                {
                    // 1 つの layout(…) の中では、後に書いたプリミティブが前のものを置き換える (glslang の mergeQualifiers)。
                    if (entry == layout.Entries.Last(e => IsPrimitiveName(e.Name.Text.ToLowerInvariant())))
                    {
                        CheckPrimitive(at, id, name, storage);
                    }
                }
                else if (IsTessellationInputName(id) || IsFragmentInputName(id) || IsWorkGroupName(id, entry))
                {
                    if (storage != Storage.In)
                    {
                        Place($"'{name}' は in の宣言にだけ書けます。");
                    }
                    else if (id is "equal_spacing" or "fractional_even_spacing" or "fractional_odd_spacing")
                    {
                        Setting(at, "spacing", id, $"頂点の間隔 '{name}'");
                    }
                    else if (id is "cw" or "ccw")
                    {
                        Setting(at, "order", id, $"頂点の順序 '{name}'");
                    }
                    else if (id.StartsWith("local_size_", StringComparison.Ordinal))
                    {
                        CheckLocalSize(at, id, entry);
                    }
                    else if (id.StartsWith("pixel_interlock", StringComparison.Ordinal) || id.StartsWith("sample_interlock", StringComparison.Ordinal)
                             || id.StartsWith("shading_rate_interlock", StringComparison.Ordinal))
                    {
                        Setting(at, "interlock", id, $"インターロックの順序 '{name}'");
                    }
                }
                else if (Stage == GlslShaderStage.Fragment && BlendEquations.Contains(id) && storage != Storage.Out)
                {
                    Place($"'{name}' は out の宣言にだけ書けます。");
                }
                else if (id == "primitive_culling" && storage != Storage.None)
                {
                    Place("'primitive_culling' は記憶域の修飾子 (in / out など) と一緒に書けません。");
                    return;
                }
            }

            if (layout.Has("primitive_culling"))
            {
                return;
            }

            bool quads = layout.Has("derivative_group_quadsnv") || layout.Has("derivative_group_quadskhr");
            bool linear = layout.Has("derivative_group_linearnv") || layout.Has("derivative_group_linearkhr");

            if (quads && linear)
            {
                Place("derivative_group_quads* と derivative_group_linear* は一緒に書けません。");
            }
            else if ((quads || linear) && storage != Storage.In)
            {
                Place($"'{(quads ? "derivative_group_quads" : "derivative_group_linear")}' は in の宣言にだけ書けます。");
            }

            if (qualifiers.FirstOrDefault(t => NonDefaultQualifiers.Contains(t.Text)) is { } qualifier)
            {
                Place($"型の無い宣言には、補助・メモリ・補間・精度の修飾子 ('{qualifier.Text}') を書けません。");
            }

            if (layout.Has("offset") || layout.Has("align"))
            {
                Place("型の無い宣言には、offset と align を書けません。ブロックかメンバーに書いてください。");
            }

            CheckQualifier(at, layout, storage);

            switch (storage)
            {
                case Storage.Uniform:
                    _uniformPacking = layout.Packing ?? _uniformPacking;
                    break;

                case Storage.Buffer:
                    _bufferPacking = layout.Packing ?? _bufferPacking;
                    break;

                case Storage.Out:
                    XfbDefaults(at, layout);
                    break;

                case Storage.In or Storage.Shared:
                    break;

                default:
                    Place("型の無い宣言には、記憶域の修飾子 uniform / buffer / in / out / shared のどれかが要ります。");
                    return;
            }

            foreach ((string id, string message) in new[]
            {
                ("binding", "'binding' は型の無い宣言に書けません。型か変数まで書いた宣言に書いてください。"),
                ("location", "'location' は型の無い宣言に書けません。変数まで書いた宣言に書いてください。"),
                ("component", "'component' は型の無い宣言に書けません。変数まで書いた宣言に書いてください。"),
                ("index", "'index' は型の無い宣言に書けません。変数まで書いた宣言に書いてください。"),
                ("xfb_offset", "'xfb_offset' は型の無い宣言に書けません。変数まで書いた宣言に書いてください。"),
                ("push_constant", "'push_constant' は型の無い宣言に書けません。ブロックにだけ書けます。"),
                ("buffer_reference", "'buffer_reference' は型の無い宣言に書けません。ブロックにだけ書けます。"),
                ("buffer_type", "'buffer_type' は型の無い宣言に書けません。ブロックにだけ書けます。"),
                ("constant_id", "'constant_id' は型の無い宣言に書けません。スカラーの const の変数にだけ書けます。"),
                ("shaderrecordnv", "'shaderRecordNV' は型の無い宣言に書けません。ブロックにだけ書けます。"),
                ("shaderrecordext", "'shaderRecordEXT' は型の無い宣言に書けません。ブロックにだけ書けます。"),
            })
            {
                if (layout.Has(id))
                {
                    Place(message);
                }
            }
        }

        /// <summary>シェーダー全体の設定を覚え、前と違う値なら報告する (glslang の「cannot change previously set …」)。</summary>
        private void Setting(ShaderSyntaxToken at, string key, long? value, string name)
        {
            if (value is not { } v)
            {
                return;
            }

            if (_shaderSettings.TryGetValue(key, out long previous) && previous != v)
            {
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"'{name}' は前の宣言と違う値です。シェーダーの中で 1 つの値にしてください。");
                return;
            }

            _shaderSettings[key] = v;
        }

        private void Setting(ShaderSyntaxToken at, string key, string value, string name)
        {
            if (_settingNames.TryGetValue(key, out string? previous) && previous != value)
            {
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"{name} は前の宣言 ('{previous}') と違います。シェーダーの中で 1 つにしてください。");
                return;
            }

            _settingNames[key] = value;
        }

        private readonly Dictionary<string, string> _settingNames = new(StringComparer.Ordinal);

        /// <summary>プリミティブの名前 (glslang の updateStandaloneQualifierDefaults の geometry)。</summary>
        private void CheckPrimitive(ShaderSyntaxToken at, string id, string name, Storage storage)
        {
            void Place(string message) => Invalid(GlslRuleDescriptors.QualifierNotAllowed, at, message);

            if (storage == Storage.In)
            {
                if (id is "points" or "lines" or "lines_adjacency" or "triangles" or "triangles_adjacency" or "quads" or "isolines")
                {
                    if (Stage == GlslShaderStage.Mesh)
                    {
                        Place($"'{name}' は入力 (in) に書けません。");
                    }
                    else
                    {
                        Setting(at, "input primitive", id, $"入力のプリミティブ '{name}'");
                    }
                }
                else
                {
                    Place($"'{name}' は入力 (in) に書けません。");
                }
            }
            else if (storage == Storage.Out)
            {
                if (id is "lines" or "triangles" && Stage != GlslShaderStage.Mesh)
                {
                    Place($"'{name}' は出力 (out) に書けません。");
                }
                else if (id is "points" or "line_strip" or "triangle_strip" or "lines" or "triangles")
                {
                    Setting(at, "output primitive", id, $"出力のプリミティブ '{name}'");
                }
                else
                {
                    Place($"'{name}' は出力 (out) に書けません。");
                }
            }
            else
            {
                Place($"'{name}' は in か out の宣言にだけ書けます。");
            }
        }

        /// <summary>ワークグループの大きさ (glslang の updateStandaloneQualifierDefaults の localSize)。</summary>
        private void CheckLocalSize(ShaderSyntaxToken at, string id, GlslLayoutEntry entry)
        {
            if (id.EndsWith("_id", StringComparison.Ordinal))
            {
                Setting(at, id, ValueOf(entry), entry.Name.Text);
                return;
            }

            if (ValueOf(entry) is not { } size)
            {
                return;
            }

            Setting(at, id, size, "local_size");
            int axis = id[^1] - 'x';
            bool ext = _r.IsOn("GL_EXT_mesh_shader", at);
            (int max, string limit) = Stage switch
            {
                GlslShaderStage.Compute => (axis == 2 ? 64 : 1024, "gl_MaxComputeWorkGroupSize"),
                GlslShaderStage.Mesh => ext ? (128, "gl_MaxMeshWorkGroupSizeEXT") : (axis == 0 ? 32 : 1, "gl_MaxMeshWorkGroupSizeNV"),
                GlslShaderStage.Task => ext ? (128, "gl_MaxTaskWorkGroupSizeEXT") : (axis == 0 ? 32 : 1, "gl_MaxTaskWorkGroupSizeNV"),
                _ => (int.MaxValue, string.Empty),
            };

            if (size > max)
            {
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"'{entry.Name.Text}' の値が大きすぎます ({limit} は {max})。");
            }
        }

        private bool IsVerticesName(string id)
            => id == "vertices" && Stage == GlslShaderStage.TessellationControl
               || id == "max_vertices" && Stage is GlslShaderStage.Geometry or GlslShaderStage.Mesh;

        private bool IsPrimitiveName(string id)
            => PrimitiveNames.Contains(id) && Stage is GlslShaderStage.Geometry or GlslShaderStage.TessellationEvaluation or GlslShaderStage.Mesh;

        private bool IsTessellationInputName(string id)
            => Stage == GlslShaderStage.TessellationEvaluation
               && id is "equal_spacing" or "fractional_even_spacing" or "fractional_odd_spacing" or "cw" or "ccw" or "point_mode";

        private bool IsFragmentInputName(string id)
            => Stage == GlslShaderStage.Fragment
               && (id is "early_fragment_tests" or "early_and_late_fragment_tests_amd" or "post_depth_coverage"
                       or "non_coherent_color_attachment_readext" or "non_coherent_depth_attachment_readext"
                       or "non_coherent_stencil_attachment_readext" or "non_coherent_attachment_readqcom"
                   || id.EndsWith("_interlock_ordered", StringComparison.Ordinal) || id.EndsWith("_interlock_unordered", StringComparison.Ordinal));

        private bool IsWorkGroupName(string id, GlslLayoutEntry entry)
            => Stage is GlslShaderStage.Compute or GlslShaderStage.Task or GlslShaderStage.Mesh
               && entry.HasValue
               && (id is "local_size_x" or "local_size_y" or "local_size_z" or "shading_rate_xqcom" or "shading_rate_yqcom" or "shading_rate_zqcom"
                   || _r.SpirvVersion != 0 && id is "local_size_x_id" or "local_size_y_id" or "local_size_z_id");

        /// <summary>型の無い宣言にだけ書ける名前を、型のある宣言に書いていないか (glslang の checkNoShaderLayouts)。</summary>
        private void CheckNoShaderLayouts(LayoutSet layout)
        {
            foreach (GlslLayoutEntry entry in layout.Entries)
            {
                string id = entry.Name.Text.ToLowerInvariant();
                bool shaderLevel = IsPrimitiveName(id)
                                   || IsTessellationInputName(id) && id != "point_mode" || id == "point_mode" && Stage == GlslShaderStage.TessellationEvaluation
                                   || id == "invocations" && Stage == GlslShaderStage.Geometry && entry.HasValue
                                   || IsVerticesName(id) && entry.HasValue
                                   || id == "max_primitives" && Stage == GlslShaderStage.Mesh && entry.HasValue
                                   || IsFragmentInputName(id) && id != "early_and_late_fragment_tests_amd"
                                   || Stage == GlslShaderStage.Fragment && BlendEquations.Contains(id)
                                   || id == "num_views" && entry.HasValue
                                   || IsWorkGroupName(id, entry) && (id.EndsWith("_id", StringComparison.Ordinal) || ValueOf(entry) > 1 || id.Contains("qcom", StringComparison.Ordinal) && ValueOf(entry) >= 1);

                if (shaderLevel)
                {
                    Invalid(GlslRuleDescriptors.QualifierNotAllowed, entry.Name,
                        $"'{entry.Name.Text}' は、型の無い宣言 (layout({entry.Name.Text}{(entry.HasValue ? " = …" : string.Empty)}) in; など) にだけ書けます。");
                }
                else if (id == "primitive_culling")
                {
                    Invalid(GlslRuleDescriptors.QualifierNotAllowed, entry.Name, "'primitive_culling' は、型の無い宣言 (layout(primitive_culling);) にだけ書けます。");
                }
            }
        }

        /// <summary>layout の値。求められなければ <see langword="null"/>。</summary>
        private long? ValueOf(GlslLayoutEntry entry) => GlslLayoutQualifier.Evaluate(entry.Value, Resolve)?.Integer;

        /// <summary>修飾子どうしの検査 (glslang の layoutQualifierCheck)。</summary>
        private void CheckQualifier(ShaderSyntaxToken at, LayoutSet layout, Storage storage)
        {
            void Place(string message) => Invalid(GlslRuleDescriptors.QualifierNotAllowed, at, message);

            if (storage == Storage.Other)
            {
                return;
            }

            if (storage == Storage.Shared && !layout.IsEmpty)
            {
                _r.RequireSpirv(at, 104, "shared のブロック");
                _r.ProfileRequires(at, GlslProfileMask.All, 0, ["GL_EXT_shared_memory_block"], "shared のブロック");
            }

            if (layout.Has("component") && !layout.HasLocation)
            {
                Place("'component' を書くには 'location' も要ります。");
            }

            if (layout.HasAnyLocation)
            {
                CheckLocationVersion(at, storage);

                if (layout.Has("index"))
                {
                    if (storage != Storage.Out)
                    {
                        Place("'index' は出力 (out) にだけ書けます。");
                    }

                    if (!layout.HasLocation)
                    {
                        Place("'index' を書くには 'location' も要ります。");
                    }
                }
            }

            bool uniformOrBuffer = storage is Storage.Uniform or Storage.Buffer;

            if (layout.Has("binding") && !uniformOrBuffer && !layout.Has("tile_attachmentqcom") && storage != Storage.Heap)
            {
                Place("'binding' は uniform か buffer の宣言にだけ書けます。");
            }

            if (layout.Has("stream") && storage != Storage.Out)
            {
                Place("'stream' は出力 (out) にだけ書けます。");
            }

            if (layout.HasXfb && storage != Storage.Out)
            {
                Place("変換フィードバックの修飾子 (xfb_*) は出力 (out) にだけ書けます。");
            }

            if (storage is not (Storage.Uniform or Storage.Buffer or Storage.Shared or Storage.Heap))
            {
                if (layout.HasMatrix || layout.Packing is not null)
                {
                    Place("行列の並び (row_major など) とパッキング (std140 など) は、uniform か buffer にだけ書けます。");
                }

                if (layout.Has("offset") || layout.Has("align"))
                {
                    Place("'offset' と 'align' は、uniform か buffer にだけ書けます。");
                }
            }

            if (layout.Has("push_constant"))
            {
                if (storage != Storage.Uniform)
                {
                    Place("'push_constant' は uniform にだけ書けます。");
                }

                if (layout.Has("set"))
                {
                    Place("'set' は 'push_constant' と一緒に書けません。");
                }

                if (layout.Has("binding"))
                {
                    Place("'binding' は 'push_constant' と一緒に書けません。");
                }
            }

            if (layout.Has("buffer_reference") && storage != Storage.Buffer)
            {
                Place("'buffer_reference' は buffer にだけ書けます。");
            }

            if (layout.Has("buffer_type") && !uniformOrBuffer)
            {
                Place("'buffer_type' は uniform か buffer にだけ書けます。");
            }

            if (layout.IsShaderRecord)
            {
                if (storage != Storage.Buffer)
                {
                    Place("'shaderRecordEXT' は buffer にだけ書けます。");
                }

                if (layout.Has("binding"))
                {
                    Place("'binding' は 'shaderRecordEXT' と一緒に書けません。");
                }

                if (layout.Has("set"))
                {
                    Place("'set' は 'shaderRecordEXT' と一緒に書けません。");
                }
            }

            if (layout.Has("bank") && !layout.Has("push_constant"))
            {
                Place("'bank' は 'push_constant' と一緒にだけ書けます。");
            }

            if (layout.Has("member_offset") && !layout.Has("push_constant"))
            {
                Place("'member_offset' は 'push_constant' と一緒にだけ書けます。");
            }

            if (layout.Has("descriptor_stride") && !layout.Has("descriptor_heap"))
            {
                Place("'descriptor_stride' を書くには 'descriptor_heap' も要ります。");
            }

            if (layout.Get("heap_offset") is { } heapOffset && ValueOf(heapOffset) is not 0 && !layout.Has("descriptor_heap") && storage != Storage.Heap)
            {
                Place("'heap_offset' を書くには 'descriptor_heap' も要ります。");
            }
        }

        /// <summary>location を書ける版とステージ (glslang の layoutQualifierCheck の hasAnyLocation)。</summary>
        private void CheckLocationVersion(ShaderSyntaxToken at, Storage storage)
        {
            GlslShaderStage[] notCompute = [.. Enum.GetValues<GlslShaderStage>().Where(s => s != GlslShaderStage.Compute)];

            switch (storage)
            {
                case Storage.In:
                {
                    const string feature = "入力の location";
                    _r.RequireStage(at, _r.IsEs && _r.Version < 310 ? [GlslShaderStage.Vertex] : notCompute, feature);

                    if (Stage == GlslShaderStage.Vertex)
                    {
                        _r.ProfileRequires(at, GlslProfileMask.NotEs, 330, ExplicitLocation, feature);
                        _r.ProfileRequires(at, GlslProfileMask.Es, 300, [], feature);
                    }
                    else
                    {
                        _r.ProfileRequires(at, GlslProfileMask.NotEs, 410, ["GL_ARB_separate_shader_objects"], feature);
                        _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], feature);
                    }

                    break;
                }

                case Storage.Out:
                {
                    const string feature = "出力の location";
                    _r.RequireStage(at, _r.IsEs && _r.Version < 310 ? [GlslShaderStage.Fragment] : notCompute, feature);

                    if (Stage == GlslShaderStage.Fragment)
                    {
                        _r.ProfileRequires(at, GlslProfileMask.NotEs, 330, ExplicitLocation, feature);
                        _r.ProfileRequires(at, GlslProfileMask.Es, 300, [], feature);
                    }
                    else
                    {
                        _r.ProfileRequires(at, GlslProfileMask.NotEs, 410, ["GL_ARB_separate_shader_objects"], feature);
                        _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], feature);
                    }

                    break;
                }

                case Storage.Uniform or Storage.Buffer:
                {
                    const string feature = "uniform と buffer の location";
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 330, ["GL_ARB_explicit_attrib_location"], feature);
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 430, ["GL_ARB_explicit_uniform_location"], feature);
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], feature);
                    break;
                }

                default:
                    break;
            }
        }

        /// <summary>関数の外の変数の宣言 (glslang の layoutObjectCheck)。</summary>
        private void VisitVariable(VariableDeclarationSyntax variable)
        {
            LayoutSet layout = new(variable.ModifierTokens);
            bool global = !variable.Ancestors().Any(a => a is FunctionDeclarationSyntax);
            CheckNoShaderLayouts(layout);

            if (!global)
            {
                return;
            }

            Storage storage = StorageOf(variable.ModifierTokens, Stage);
            TypeShape type = TypeShape.Of(_types, variable.Type.NameToken.Text);

            foreach (VariableDeclaratorSyntax declarator in variable.Variables)
            {
                if (layout.HasLocation && !declarator.Name.StartsWith("gl_", StringComparison.Ordinal))
                {
                    AddUsedLocation(declarator.NameToken, layout, storage, variable.Type.NameToken.Text, declarator, variable.ModifierTokens);
                }

                CheckObject(declarator.NameToken, declarator.Name, layout, storage, type, declarator.IsArray,
                    declarator.TryGetArrayLength(out int length) ? length : null,
                    variable.ModifierTokens);

                if (storage == Storage.Out)
                {
                    XfbVariable(declarator.NameToken, layout, variable.Type.NameToken.Text, declarator);
                }
            }
        }

        /// <summary>構造体が bool のメンバーを持つか。入れ子の構造体も見る。</summary>
        private bool StructContainsBool(string name, int depth)
        {
            _structs ??= ReadStructs();

            return depth <= 8
                   && _structs.TryGetValue(name, out ImmutableArray<(string Type, VariableDeclaratorSyntax Declarator)> members)
                   && members.Any(m => TypeShape.Of(_types, m.Type).IsBool || StructContainsBool(m.Type, depth + 1));
        }

        /// <summary>変数とブロックの、型と組み合わせた検査 (glslang の layoutTypeCheck と layoutObjectCheck)。</summary>
        private void CheckObject(
            ShaderSyntaxToken at,
            string name,
            LayoutSet layout,
            Storage storage,
            TypeShape type,
            bool isArray,
            long? arraySize,
            ImmutableArray<ShaderSyntaxToken> modifiers)
        {
            void Place(string message) => Invalid(GlslRuleDescriptors.QualifierNotAllowed, at, message);
            void Value(string message) => Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, message);
            void Required(string message) => Invalid(GlslRuleDescriptors.LayoutRequired, at, message);

            bool builtIn = name.StartsWith("gl_", StringComparison.Ordinal);

            if (storage == Storage.Other)
            {
                return;
            }

            CheckQualifier(at, layout, storage);

            if (layout.HasAnyLocation)
            {
                if (layout.Get("location") is { } location && storage == Storage.Out && Stage == GlslShaderStage.Fragment && ValueOf(location) >= MaxDrawBuffers)
                {
                    Value($"'location' の値がフラグメントシェーダーの出力には大きすぎます (gl_MaxDrawBuffers は {MaxDrawBuffers})。");
                }

                if (layout.Get("component") is { } componentEntry && ValueOf(componentEntry) is { } component)
                {
                    if (type.VectorSize is { } size && component + size * (type.IsDouble ? 2 : 1) > 4)
                    {
                        Value("'component' から型の成分が 4 つを越えます。");
                    }

                    if (type.IsMatrix || type.IsBlock || type.IsStruct)
                    {
                        Place("'component' は行列・構造体・ブロックに書けません。");
                    }

                    if (type.IsDouble && (component & 1) != 0)
                    {
                        Value("double は奇数番目の成分 (component = 1 か 3) から始められません。");
                    }
                }

                switch (storage)
                {
                    case Storage.In or Storage.Out:
                        if (type.IsBlock)
                        {
                            _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 440, ["GL_ARB_enhanced_layouts"], "in / out のブロックの location");
                        }

                        break;

                    case Storage.Uniform or Storage.Buffer:
                        if (type.IsBlock)
                        {
                            Place("'location' は uniform と buffer のブロックに書けません。");
                        }

                        break;

                    case Storage.Heap:
                        break;

                    default:
                        Place("'location' は uniform / buffer / in / out の宣言にだけ書けます。");
                        break;
                }
            }

            if (layout.Get("binding") is { } bindingEntry)
            {
                long? binding = ValueOf(bindingEntry);

                if (!type.IsOpaque && !type.IsBlock && !type.IsUnknown)
                {
                    Place("'binding' はブロック・サンプラー・イメージ・アトミックカウンターにだけ書けます。");
                }

                if (type.IsSampler && _r.Vulkan == false && binding is { } first)
                {
                    long last = first + (isArray && arraySize is { } count ? count - 1 : 0);

                    if (last >= MaxCombinedTextureImageUnits)
                    {
                        Value($"サンプラーの 'binding' が大きすぎます (gl_MaxCombinedTextureImageUnits は {MaxCombinedTextureImageUnits})。");
                    }
                }

                if (type.IsAtomic && binding >= MaxAtomicCounterBindings)
                {
                    Value($"atomic_uint の 'binding' が大きすぎます (gl_MaxAtomicCounterBindings は {MaxAtomicCounterBindings})。");
                    return;
                }
            }
            else if (!_r.AutoMapBindings)
            {
                if (type.IsAtomic)
                {
                    Required("atomic_uint には layout(binding = …) が要ります。");
                }

                if (_r.SpirvVersion > 0 && uniformOrBuffer(storage) && !_r.IsOn("GL_EXT_descriptor_heap", at))
                {
                    if (type.IsBlock && !layout.Has("push_constant") && !layout.IsShaderRecord && !layout.Has("input_attachment_index") && !layout.Has("buffer_reference"))
                    {
                        Required("SPIR-V を作るとき、uniform と buffer のブロックには layout(binding = …) が要ります。");
                    }
                    else if (_r.Vulkan == true && type.IsSampler && !type.IsAttachment)
                    {
                        Required("Vulkan 向けでは、サンプラー・テクスチャ・イメージに layout(binding = …) が要ります。");
                    }
                }
            }

            if (layout.Has("offset") && type.IsBlock)
            {
                Place("'offset' はブロックのメンバーにだけ書けます。ブロックそのものには書けません。");
            }

            CheckFormat(at, layout, type, modifiers);

            if (layout.Has("push_constant"))
            {
                if (!type.IsBlock)
                {
                    Place("'push_constant' はブロックにだけ書けます。");
                }

                if (isArray)
                {
                    Place("'push_constant' のブロックは配列にできません。");
                }
            }

            if (layout.Has("buffer_reference") && !type.IsBlock)
            {
                Place("'buffer_reference' はブロックにだけ書けます。");
            }

            if (layout.IsShaderRecord && !type.IsBlock)
            {
                Place("'shaderRecordEXT' はブロックにだけ書けます。");
            }

            if (type.IsSubpass)
            {
                if (_r.IsOn("GL_EXT_shader_tile_image", at))
                {
                    Place($"'{type.Name}' は GL_EXT_shader_tile_image を有効にしていると使えません。");
                }

                if (!layout.Has("input_attachment_index") && (isArray || !_r.IsOn("GL_EXT_optional_input_attachment_index", at)))
                {
                    Required($"'{type.Name}' には layout(input_attachment_index = …) が要ります。");
                }
            }
            else if (layout.Has("input_attachment_index"))
            {
                Place("'input_attachment_index' はサブパスの入力 (subpassInput) にだけ書けます。");
            }

            if (layout.Has("constant_id"))
            {
                if (storage != Storage.Const)
                {
                    Place("'constant_id' は const のスカラーにだけ書けます。");
                }

                if (!type.IsScalar && !type.IsUnknown)
                {
                    Place("'constant_id' はスカラーにだけ書けます。");
                }
            }

            // 変数そのものの検査 (glslang の layoutObjectCheck の残り)。
            if (_r.SpirvVersion > 0 && !_r.AutoMapLocations && !builtIn && !layout.HasLocation && storage is Storage.In or Storage.Out && !type.IsBlock
                && !modifiers.Any(m => m.Text is "spirv_decorate" or "spirv_decorate_id" or "spirv_decorate_string"))
            {
                Required("SPIR-V を作るとき、利用者の入力と出力には layout(location = …) が要ります。");
            }

            if (uniformOrBuffer(storage) && !type.IsBlock)
            {
                if (layout.HasMatrix)
                {
                    Place("行列の並び (row_major など) は変数の宣言に書けません。ブロックかそのメンバーに書いてください。");
                }

                if (layout.Packing is not null)
                {
                    Place("パッキング (std140 など) は変数の宣言に書けません。ブロックに書いてください。");
                }

                if (layout.Has("offset") && !type.IsAtomic)
                {
                    Place("'offset' は変数の宣言に書けません。ブロックのメンバーに書いてください。");
                }

                if (layout.Has("align"))
                {
                    Place("'align' は変数の宣言に書けません。ブロックかそのメンバーに書いてください。");
                }

                if (layout.Has("push_constant"))
                {
                    Place("'push_constant' は uniform のブロックにだけ書けます。");
                }

                if (layout.HasLocation && type.IsAtomic)
                {
                    Place("'location' はアトミックカウンターに書けません。");
                }
            }

            if (!builtIn && storage is Storage.In or Storage.Out && (type.ContainsBool || StructContainsBool(type.Name, 0)))
            {
                Place(type.IsBool
                    ? $"{(storage == Storage.In ? "入力 (in)" : "出力 (out)")} は bool にできません。"
                    : $"{(storage == Storage.In ? "入力 (in)" : "出力 (out)")} は bool を含められません。");
            }

            static bool uniformOrBuffer(Storage s) => s is Storage.Uniform or Storage.Buffer;
        }

        /// <summary>イメージの形式 (glslang の layoutTypeCheck の hasFormat)。</summary>
        private void CheckFormat(ShaderSyntaxToken at, LayoutSet layout, TypeShape type, ImmutableArray<ShaderSyntaxToken> modifiers)
        {
            bool bindless = _r.IsOn("GL_ARB_bindless_texture", at);

            if (layout.Format is { } format)
            {
                if (!type.IsImage)
                {
                    if (!bindless && !type.IsUnknown)
                    {
                        Invalid(GlslRuleDescriptors.QualifierNotAllowed, at, $"イメージの形式 '{format}' はイメージにだけ書けます。");
                    }

                    return;
                }

                bool floatFormat = EsFloatFormats.Contains(format) || FloatFormats.Contains(format);
                bool intFormat = EsIntFormats.Contains(format) || IntFormats.Contains(format);

                string? mismatch = type.ImageKind switch
                {
                    'f' when !floatFormat => "浮動小数点数のイメージ",
                    'i' when !intFormat => "符号付きの整数のイメージ",
                    'u' when floatFormat || intFormat => "符号無しの整数のイメージ",
                    _ => null,
                };

                if (mismatch is not null)
                {
                    Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"イメージの形式 '{format}' は{mismatch}に使えません。");
                }

                if (_r.IsEs && format is not ("r32f" or "r32i" or "r32ui") && !modifiers.Any(m => m.Text is "readonly" or "writeonly"))
                {
                    Invalid(GlslRuleDescriptors.LayoutRequired, at, $"ES では、イメージの形式 '{format}' には readonly か writeonly が要ります。");
                }
            }
            else if (type.IsImage && !modifiers.Any(m => m.Text == "writeonly") && !bindless)
            {
                const string feature = "writeonly でも形式 (layout(rgba8) など) の指定も無いイメージ";
                _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, feature);
                _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 0, ["GL_EXT_shader_image_load_formatted"], feature);
            }
        }

        /// <summary>ブロック (glslang の declareBlock)。</summary>
        private void VisitBlock(
            ImmutableArray<ShaderSyntaxToken> modifiers,
            string? name,
            ImmutableArray<DeclarationSyntax> members,
            VariableDeclaratorSyntax? instance)
        {
            if (modifiers.IsEmpty)
            {
                return;
            }

            LayoutSet layout = new(modifiers);
            ShaderSyntaxToken at = QualifierLocation(modifiers, layout);
            Storage storage = StorageOf(modifiers, Stage);
            CheckNoShaderLayouts(layout);

            if (storage == Storage.Out && name is not "struct")
            {
                XfbBlock(at, layout, members, builtIn: name is not null && name.StartsWith("gl_", StringComparison.Ordinal));
            }

            // uniform struct { … } s; は、名前が struct のブロックとして読まれる。構造体の変数なので、ブロックとして検査しない。
            if (storage == Storage.Other || name is "struct" || name is not null && name.StartsWith("gl_", StringComparison.Ordinal))
            {
                return;
            }

            // ブロックのパッキング。分からなければ null。
            string? packing = layout.Packing
                ?? (layout.Has("push_constant") || layout.IsShaderRecord ? "std430" : null)
                ?? storage switch
                {
                    Storage.Uniform => _uniformPacking ?? (_r.SpirvVersion is { } spv ? (spv != 0 ? "std140" : "shared") : null),
                    Storage.Buffer => _bufferPacking ?? (_r.SpirvVersion is { } spv ? (spv != 0 ? "std430" : "shared") : null),
                    Storage.Shared => "std430",
                    _ => "none",
                };
            bool standardPacking = packing is "std140" or "std430" or "scalar";

            if (layout.Get("align") is { } align && packing is not null && !standardPacking)
            {
                Invalid(GlslRuleDescriptors.QualifierNotAllowed, align.Name, "'align' は、パッキングが std140 / std430 / scalar のブロックにだけ書けます。");
            }

            bool firstMemberHasLocation = false;
            bool first = true;
            bool containsBool = false;

            foreach (VariableDeclarationSyntax member in members.OfType<VariableDeclarationSyntax>())
            {
                LayoutSet memberLayout = new(member.ModifierTokens);
                ShaderSyntaxToken memberAt = member.Variables.FirstOrDefault()?.NameToken ?? member.Type.NameToken;
                TypeShape memberType = TypeShape.Of(_types, member.Type.NameToken.Text);
                containsBool |= memberType.ContainsBool;

                if (first)
                {
                    firstMemberHasLocation = memberLayout.HasLocation || member.Variables.Any(v => v.Name.StartsWith("gl_", StringComparison.Ordinal));
                    first = false;
                }

                CheckNoShaderLayouts(memberLayout);

                if (memberLayout.IsEmpty)
                {
                    continue;
                }

                if (memberLayout.Packing is { } memberPacking && storage != Storage.Heap)
                {
                    Invalid(GlslRuleDescriptors.QualifierNotAllowed, memberAt, $"ブロックのメンバーにはパッキング ('{memberPacking}') を書けません。");
                }

                if (memberLayout.HasLocation)
                {
                    if (storage is Storage.In or Storage.Out)
                    {
                        const string feature = "ブロックのメンバーの location";
                        _r.RequireProfile(memberAt, GlslProfileMask.Core | GlslProfileMask.Compatibility | GlslProfileMask.Es, feature);
                        _r.ProfileRequires(memberAt, GlslProfileMask.Core | GlslProfileMask.Compatibility, 440, ["GL_ARB_enhanced_layouts"], feature);
                        _r.ProfileRequires(memberAt, GlslProfileMask.Es, 320, AepShaderIoBlocks, feature);
                    }
                    else
                    {
                        Invalid(GlslRuleDescriptors.QualifierNotAllowed, memberAt, "ブロックのメンバーの 'location' は、in か out のブロックにだけ書けます。");
                    }
                }

                if (memberLayout.Has("offset") && _r.SpirvVersion == 0)
                {
                    _r.ProfileRequires(memberAt, GlslProfileMask.NotEs, 440, ["GL_ARB_enhanced_layouts"], "ブロックのメンバーの offset");
                    _r.ProfileRequires(memberAt, GlslProfileMask.Es, 300, ["GL_ARB_enhanced_layouts"], "ブロックのメンバーの offset");
                }

                if ((memberLayout.Has("align") || memberLayout.Has("offset")) && packing is not null && !standardPacking)
                {
                    Invalid(GlslRuleDescriptors.QualifierNotAllowed, memberAt, "'offset' と 'align' は、パッキングが std140 / std430 / scalar のブロックのメンバーにだけ書けます。");
                }

                CheckQualifier(memberAt, memberLayout, storage);

                if (memberLayout.Get("component") is { } componentEntry && ValueOf(componentEntry) is { } component
                    && memberType.VectorSize is { } size && component + size * (memberType.IsDouble ? 2 : 1) > 4)
                {
                    Invalid(GlslRuleDescriptors.InvalidLayoutValue, memberAt, "'component' から型の成分が 4 つを越えます。");
                }
            }

            CheckObject(at, name ?? string.Empty, layout, storage, TypeShape.Block, instance?.IsArray ?? false, null, modifiers);
            AddBlockLocations(at, layout, storage, members, instance, modifiers);

            if (_r.SpirvVersion > 0 && !_r.AutoMapLocations && storage is Storage.In or Storage.Out && !layout.HasLocation && !firstMemberHasLocation
                && members.OfType<VariableDeclarationSyntax>().Any()
                && !modifiers.Any(m => m.Text is "spirv_decorate" or "spirv_decorate_id" or "spirv_decorate_string"))
            {
                Invalid(GlslRuleDescriptors.LayoutRequired, at, "SPIR-V を作るとき、利用者の入力と出力のブロックには layout(location = …) が要ります。");
            }

            if (storage is Storage.In or Storage.Out && containsBool)
            {
                Invalid(GlslRuleDescriptors.QualifierNotAllowed, at, $"{(storage == Storage.In ? "入力 (in)" : "出力 (out)")} のブロックは bool を含められません。");
            }
        }
    }

    /// <summary>layout の検査に要る、型の形。</summary>
    private sealed record TypeShape(string Name)
    {
        public static TypeShape Block { get; } = new("block") { IsBlock = true };

        public bool IsBlock { get; private init; }

        public bool IsStruct { get; private init; }

        public bool IsUnknown { get; private init; }

        public bool IsScalar { get; private init; }

        public bool IsMatrix { get; private init; }

        public bool IsDouble { get; private init; }

        public bool IsBool { get; private init; }

        /// <summary>スカラーの型の名前 (<c>float</c>・<c>int</c> など)。数でなければ型の名前。</summary>
        public string BaseType { get; private init; } = Name;

        public bool ContainsBool => IsBool;

        public int? VectorSize { get; private init; }

        public bool IsOpaque { get; private init; }

        /// <summary>glslang の EbtSampler (サンプラー・テクスチャ・イメージ・サブパスの入力)。</summary>
        public bool IsSampler { get; private init; }

        public bool IsImage { get; private init; }

        public bool IsSubpass { get; private init; }

        public bool IsAttachment { get; private init; }

        public bool IsAtomic { get; private init; }

        /// <summary>イメージの値の型 (<c>f</c>・<c>i</c>・<c>u</c>)。</summary>
        public char ImageKind { get; private init; }

        public static TypeShape Of(GlslTypeSystem? types, string name)
        {
            if (types is not null && types.TryDescribeNumeric(name, out ShaderNumericShape shape))
            {
                return new TypeShape(name)
                {
                    IsScalar = shape.Kind == ShaderNumericKind.Scalar,
                    IsMatrix = shape.Kind == ShaderNumericKind.Matrix,
                    IsDouble = shape.BaseName == "double",
                    IsBool = shape.BaseName == "bool",
                    BaseType = shape.BaseName,
                    VectorSize = shape.Kind == ShaderNumericKind.Matrix ? null : shape.Rows * shape.Columns,
                };
            }

            if (name == "atomic_uint")
            {
                return new TypeShape(name) { IsOpaque = true, IsAtomic = true };
            }

            if (GlslKeywords.IsBuiltInType(name) && name != "void")
            {
                bool image = name.Contains("image", StringComparison.Ordinal) && !name.Contains("sampler", StringComparison.Ordinal);
                bool subpass = name.Contains("subpassInput", StringComparison.Ordinal);
                bool attachment = name.Contains("attachmentEXT", StringComparison.Ordinal);
                bool sampler = image || subpass || attachment || name.Contains("sampler", StringComparison.Ordinal) || name.Contains("texture", StringComparison.Ordinal);
                return new TypeShape(name)
                {
                    IsOpaque = true,
                    IsSampler = sampler,
                    IsImage = image,
                    IsSubpass = subpass,
                    IsAttachment = attachment,
                    ImageKind = name.Contains("image", StringComparison.Ordinal) ? name[0] switch { 'i' when name[1] != 'm' => 'i', 'u' => 'u', _ => 'f' } : 'f',
                };
            }

            return new TypeShape(name) { IsStruct = true, IsUnknown = true };
        }
    }
}

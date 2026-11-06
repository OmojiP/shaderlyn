using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Godot;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の、関数の外の宣言を検査する。
/// 修飾子 (GD0101)、描画のモードとステンシルのモード (GD0102)、uniform のヒントと既定値 (GD0103)、宣言に使えない型 (GD0403)。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>ShaderLanguage::_parse_shader</c> と <c>_parse_shader_mode</c> の検査を、同じ順に写した。
/// Godot は最初の誤りで止まるので、1 つの宣言では最初の誤りだけを報告する。
/// </para>
/// <para>
/// <c>render_mode</c> の名前は、表 (<see cref="GodotBuiltins.GetRenderModes"/>) の名前で始まるかを順に調べる。
/// 選択肢のあるモード (<c>blend</c>) は「名前_選択肢」でなければならず、同じモードの別の選択肢を 2 度書けない。
/// 選択肢の無いモードは、名前で始まれば通る (Godot と同じ)。<c>.gdshaderinc</c> (シェーダーの種類が無い) では、どの種類のモードも書ける。
/// </para>
/// <para>
/// <c>hint_normal_roughness_texture</c> は Forward+ のレンダラーでだけ使える (GD0001)。Godot と同じく、シェーダーの種類より先に調べる。
/// </para>
/// </remarks>
internal sealed class GodotDeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <summary><c>instance</c> の uniform の数の上限 (<c>MAX_INSTANCE_UNIFORM_INDICES</c>)。</summary>
    private const int MaxInstanceUniforms = 16;

    /// <summary>配列の uniform に書けるヒント。</summary>
    private static readonly ImmutableHashSet<string> ArrayHints =
    [
        "source_color", "color_conversion_disabled", "repeat_disable", "repeat_enable",
        "filter_linear", "filter_linear_mipmap", "filter_linear_mipmap_anisotropic",
        "filter_nearest", "filter_nearest_mipmap", "filter_nearest_mipmap_anisotropic",
    ];

    /// <summary>uniform の種類を決めるヒントのうち、サンプラーでない uniform にも書けるもの。</summary>
    private static readonly ImmutableHashSet<string> ValueHints = ["source_color", "color_conversion_disabled", "hint_range", "hint_enum"];

    /// <summary>ヒントでないもの (フィルター・繰り返し・<c>instance_index</c>)。</summary>
    private static readonly ImmutableHashSet<string> NonHints = ["instance_index"];

    /// <summary>uniform にも varying にも使える、行列までの型 (Godot の <c>DataType</c> の <c>TYPE_MAT4</c> まで)。</summary>
    private static readonly ImmutableHashSet<string> ValueTypes =
    [
        "bool", "bvec2", "bvec3", "bvec4", "int", "ivec2", "ivec3", "ivec4", "uint", "uvec2", "uvec3", "uvec4",
        "float", "vec2", "vec3", "vec4", "mat2", "mat3", "mat4",
    ];

    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        GodotRuleDescriptors.QualifierNotAllowed, GodotRuleDescriptors.InvalidMode,
        GodotRuleDescriptors.InvalidUniformHint, GodotRuleDescriptors.TypeNotAllowed, GodotRuleDescriptors.NotAvailableInRenderer,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (program.Tree.Language is not GodotLanguageOptions language)
            {
                continue;
            }

            string? shaderType = language.ShaderType;
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            HashSet<string> structs = new(StringComparer.Ordinal);
            HashSet<string> constants = shaderType is null
                ? new(StringComparer.Ordinal)
                : new(GodotBuiltins.Instance.GetVariables(shaderType).Where(v => v.Function == "constants").Select(v => v.Name), StringComparer.Ordinal);
            ModeState renderModes = new();
            ModeState stencilModes = new();
            int instanceUniforms = 0;

            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                switch (declaration)
                {
                    case IncompleteDeclarationSyntax { Tokens: [{ Text: "render_mode" }, ..] } modes:
                        CheckModes(modes.Tokens, isStencil: false, shaderType, renderModes, Report);
                        break;

                    case IncompleteDeclarationSyntax { Tokens: [{ Text: "stencil_mode" }, ..] } modes:
                        CheckModes(modes.Tokens, isStencil: true, shaderType, stencilModes, Report);
                        break;

                    case StructDeclarationSyntax structure:
                        CheckStruct(structure, structs, Report);
                        structs.Add(structure.Name);
                        break;

                    case VariableDeclarationSyntax variable when variable.HasModifier("uniform"):
                        CheckUniform(variable, shaderType, language.Renderer, structs, constants, binder, ref instanceUniforms, Report);
                        break;

                    case VariableDeclarationSyntax variable when variable.HasModifier("varying"):
                        CheckVarying(variable, shaderType, structs, Report);
                        break;

                    case VariableDeclarationSyntax variable when variable.HasModifier("const"):
                        constants.UnionWith(variable.Variables.Select(v => v.Name));
                        break;
                }
            }
        }
    }

    /// <summary><c>render_mode</c> / <c>stencil_mode</c> の名前を調べる (<c>_parse_shader_mode</c>)。</summary>
    private static void CheckModes(
        ImmutableArray<ShaderSyntaxToken> tokens,
        bool isStencil,
        string? shaderType,
        ModeState state,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        string kind = isStencil ? "ステンシルのモード" : "描画のモード";
        ImmutableArray<GodotMode> modes = shaderType is null
            ? [.. GodotBuiltins.Instance.ShaderTypes.SelectMany(t => isStencil ? GodotBuiltins.Instance.GetStencilModes(t) : GodotBuiltins.Instance.GetRenderModes(t))]
            : isStencil ? GodotBuiltins.Instance.GetStencilModes(shaderType) : GodotBuiltins.Instance.GetRenderModes(shaderType);

        // 先頭は render_mode / stencil_mode、最後はセミコロン。名前とカンマが交互に並ぶ (形の誤りは CF0001 が報告する)。
        for (int i = 1; i < tokens.Length; i += 2)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.SemicolonToken)
            {
                return;
            }

            // stencil_mode の参照値 (0 から 255 の整数)。
            if (isStencil && token.Kind == ShaderSyntaxKind.NumericLiteralToken && GodotLiterals.TryParseInteger(token.Text, out long reference))
            {
                if (state.Reference is { } previous)
                {
                    report(GodotRuleDescriptors.InvalidMode, token, $"ステンシルの参照値は 1 つだけ書けます (すでに {previous} を書いています)。");
                    return;
                }

                if (reference > 255)
                {
                    report(GodotRuleDescriptors.InvalidMode, token, $"ステンシルの参照値は 255 までです (今は {reference})。");
                    return;
                }

                state.Reference = reference;
                continue;
            }

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                if (i > 1)
                {
                    report(GodotRuleDescriptors.InvalidMode, token, $"{kind}の名前が要ります。");
                }

                return;
            }

            string name = token.Text;

            if (!state.Written.Add(name))
            {
                report(GodotRuleDescriptors.InvalidMode, token, $"{kind} '{name}' を 2 度書いています。");
                return;
            }

            bool found = false;

            foreach (GodotMode mode in modes)
            {
                if (!name.StartsWith(mode.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (mode.Options.IsEmpty)
                {
                    found = true;
                    break;
                }

                if (name.Length > mode.Name.Length && mode.Options.Contains(name[(mode.Name.Length + 1)..]))
                {
                    found = true;

                    if (state.Groups.TryGetValue(mode.Name, out string? defined))
                    {
                        report(GodotRuleDescriptors.InvalidMode, token, $"{kind} '{name}' は書けません。'{mode.Name}' はすでに '{defined}' にしています。");
                        return;
                    }

                    state.Groups[mode.Name] = name;
                    break;
                }
            }

            if (!found)
            {
                string where = shaderType is null ? string.Empty : $"{shaderType} のシェーダーの";
                report(GodotRuleDescriptors.InvalidMode, token, $"'{name}' は{where}{kind}ではありません。");
                return;
            }
        }
    }

    /// <summary>構造体のメンバーの型を調べる。</summary>
    private static void CheckStruct(StructDeclarationSyntax structure, HashSet<string> structs, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            TypeSyntax type = member.Type;

            if (!IsBuiltInType(type.Name) && !structs.Contains(type.Name))
            {
                report(GodotRuleDescriptors.TypeNotAllowed, type.NameToken, $"'{type.Name}' は型ではありません。");
                return;
            }

            if (type.Name == "void" || IsSampler(type.Name))
            {
                report(GodotRuleDescriptors.TypeNotAllowed, type.NameToken, $"構造体のメンバーに {type.Name} の型は使えません。");
                return;
            }
        }
    }

    /// <summary>uniform の修飾子・型・ヒント・既定値を調べる。</summary>
    private static void CheckUniform(
        VariableDeclarationSyntax variable,
        string? shaderType,
        GodotRenderer renderer,
        HashSet<string> structs,
        HashSet<string> constants,
        ExpressionTypeBinder binder,
        ref int instanceUniforms,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        bool instance = variable.HasModifier("instance");
        bool global = variable.HasModifier("global");
        TypeSyntax typeSyntax = variable.Type;
        string type = typeSyntax.Name;

        if (variable.Variables.FirstOrDefault() is not { } declarator)
        {
            return;
        }

        if (instance && shaderType is not (null or "spatial" or "canvas_item"))
        {
            report(GodotRuleDescriptors.QualifierNotAllowed, variable.ModifierTokens.First(t => t.TextIs("instance")),
                $"instance uniform は、{shaderType} のシェーダーでは使えません (spatial と canvas_item だけ)。");
            return;
        }

        if (structs.Contains(type))
        {
            report(GodotRuleDescriptors.TypeNotAllowed, typeSyntax.NameToken, "uniform に構造体の型は使えません。");
            return;
        }

        if (type == "void")
        {
            report(GodotRuleDescriptors.TypeNotAllowed, typeSyntax.NameToken, "uniform に void の型は使えません。");
            return;
        }

        if (!IsBuiltInType(type))
        {
            return;
        }

        bool sampler = IsSampler(type);
        bool array = declarator.IsArray || !typeSyntax.ArrayRanks.IsEmpty;

        if (instance && sampler)
        {
            report(GodotRuleDescriptors.QualifierNotAllowed, typeSyntax.NameToken, "instance uniform にサンプラーの型は使えません。");
            return;
        }

        if (instance && type is "mat2" or "mat3" or "mat4")
        {
            report(GodotRuleDescriptors.QualifierNotAllowed, typeSyntax.NameToken, "instance uniform に行列の型は使えません。");
            return;
        }

        if (array && (global || instance))
        {
            report(GodotRuleDescriptors.QualifierNotAllowed, declarator.NameToken, $"{(global ? "global" : "instance")} uniform は配列にできません。");
            return;
        }

        if (!CheckHints(declarator, type, sampler, array, shaderType, renderer, constants, out bool customIndex, report))
        {
            return;
        }

        if (instance && !customIndex && ++instanceUniforms > MaxInstanceUniforms)
        {
            report(GodotRuleDescriptors.InvalidUniformHint, declarator.NameToken, $"instance uniform は {MaxInstanceUniforms} 個までです。");
            return;
        }

        if (declarator.Initializer is { } initializer && initializer.FirstToken is { } first)
        {
            if (array)
            {
                report(GodotRuleDescriptors.InvalidUniformHint, first, "配列の uniform には既定値を書けません。");
                return;
            }

            if (binder.GetTypesByConfiguration(initializer) is [{ Type: { } valueType }] && !ConvertsConstant(valueType, type, initializer))
            {
                report(GodotRuleDescriptors.InvalidUniformHint, first, $"既定値 ({valueType}) を {type} に変換できません。");
            }
        }
    }

    /// <summary>uniform のヒントを、Godot と同じ順に調べる。誤りがあれば最初の 1 つを報告して <see langword="false"/> を返す。</summary>
    private static bool CheckHints(
        VariableDeclaratorSyntax declarator,
        string type,
        bool sampler,
        bool array,
        string? shaderType,
        GodotRenderer renderer,
        HashSet<string> constants,
        out bool customIndex,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        customIndex = false;
        string? hint = null;
        string? filter = null;
        string? repeat = null;
        bool useColor = false;

        bool Error(ShaderSyntaxToken token, string message)
        {
            report(GodotRuleDescriptors.InvalidUniformHint, token, message);
            return false;
        }

        foreach (SemanticSyntax semantic in declarator.Semantics)
        {
            string name = semantic.Name;
            ShaderSyntaxToken at = semantic.NameToken;
            string? newHint = null;
            string? newFilter = null;
            string? newRepeat = null;

            if (array && !ArrayHints.Contains(name))
            {
                return Error(at, $"配列の uniform にはヒント '{name}' を使えません。");
            }

            switch (name)
            {
                case "source_color":
                    if (type is not ("vec3" or "vec4") && !sampler)
                    {
                        return Error(at, "source_color は vec3・vec4・サンプラーの uniform にだけ使えます。");
                    }

                    if (sampler)
                    {
                        if (useColor)
                        {
                            return Error(at, "ヒント 'source_color' を 2 度書いています。");
                        }

                        useColor = true;
                    }
                    else
                    {
                        newHint = name;
                    }

                    break;

                case "color_conversion_disabled":
                    if (type is not ("vec3" or "vec4"))
                    {
                        return Error(at, "color_conversion_disabled は vec3 と vec4 の uniform にだけ使えます。");
                    }

                    if (hint != "source_color")
                    {
                        return Error(at, "color_conversion_disabled の前には source_color が要ります。");
                    }

                    hint = null;
                    newHint = name;
                    break;

                case "hint_range":
                    if (type is not ("float" or "int"))
                    {
                        return Error(at, "hint_range は float と int の uniform にだけ使えます。");
                    }

                    if (semantic.ArgumentTokens.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken && !constants.Contains(t.Text)) is { } unknown)
                    {
                        return Error(unknown, $"'{unknown.Text}' は定数ではありません。hint_range の引数には数か定数を書きます。");
                    }

                    newHint = name;
                    break;

                case "hint_enum":
                    if (type != "int")
                    {
                        return Error(at, "hint_enum は int の uniform にだけ使えます。");
                    }

                    if (semantic.ArgumentTokens.FirstOrDefault(t => t.Kind is not (ShaderSyntaxKind.StringLiteralToken or ShaderSyntaxKind.CommaToken)) is { } notString)
                    {
                        return Error(notString, "hint_enum の引数には文字列を書きます。");
                    }

                    newHint = name;
                    break;

                case "instance_index":
                    if (customIndex)
                    {
                        return Error(at, "instance_index は 1 度だけ書けます。");
                    }

                    if (semantic.ArgumentTokens.FirstOrDefault() is { } index)
                    {
                        if (index.Kind == ShaderSyntaxKind.MinusToken)
                        {
                            return Error(index, "instance_index は負の数にできません。");
                        }

                        if (GodotLiterals.TryParseInteger(index.Text, out long value) && value >= MaxInstanceUniforms)
                        {
                            return Error(index, $"instance_index は 0 から {MaxInstanceUniforms - 1} までです (今は {value})。");
                        }
                    }

                    customIndex = true;
                    break;

                case "hint_screen_texture" when shaderType is not (null or "spatial" or "canvas_item"):
                    return Error(at, $"hint_screen_texture は、{shaderType} のシェーダーでは使えません。");

                case "hint_normal_roughness_texture" when renderer != GodotRenderer.ForwardPlus:
                    report(GodotRuleDescriptors.NotAvailableInRenderer, at,
                        $"hint_normal_roughness_texture は Forward+ のレンダラーでだけ使えます (今は {renderer.DisplayName()})。");
                    return false;

                case "hint_normal_roughness_texture" or "hint_depth_texture" when shaderType is not (null or "spatial"):
                    return Error(at, $"{name} は、{shaderType} のシェーダーでは使えません。");

                case "hint_blit_source0" or "hint_blit_source1" or "hint_blit_source2" or "hint_blit_source3" when shaderType is not (null or "texture_blit"):
                    return Error(at, $"hint_blit_source は、{shaderType} のシェーダーでは使えません。");

                case var _ when name.StartsWith("filter_", StringComparison.Ordinal):
                    newFilter = name;
                    break;

                case var _ when name.StartsWith("repeat_", StringComparison.Ordinal):
                    newRepeat = name;
                    break;

                default:
                    if (!NonHints.Contains(name))
                    {
                        newHint = name;
                    }

                    break;
            }

            bool samplerHint = newHint is not null && !ValueHints.Contains(newHint);

            if ((newFilter is not null || newRepeat is not null || samplerHint) && !sampler)
            {
                return Error(at, $"ヒント '{name}' はサンプラーの uniform にだけ使えます。");
            }

            if (newHint is not null)
            {
                if (hint is not null)
                {
                    return Error(at, hint == newHint
                        ? $"ヒント '{newHint}' を 2 度書いています。"
                        : $"ヒント '{newHint}' は書けません。ヒントはすでに '{hint}' にしています。");
                }

                hint = newHint;
            }

            if (newFilter is not null)
            {
                if (filter is not null)
                {
                    return Error(at, filter == newFilter
                        ? $"フィルター '{newFilter}' を 2 度書いています。"
                        : $"フィルター '{newFilter}' は書けません。フィルターはすでに '{filter}' にしています。");
                }

                filter = newFilter;
            }

            if (newRepeat is not null)
            {
                if (repeat is not null)
                {
                    return Error(at, repeat == newRepeat
                        ? $"繰り返しの指定 '{newRepeat}' を 2 度書いています。"
                        : $"繰り返しの指定 '{newRepeat}' は書けません。すでに '{repeat}' にしています。");
                }

                repeat = newRepeat;
            }
        }

        return true;
    }

    /// <summary>varying の修飾子と型を調べる。</summary>
    private static void CheckVarying(
        VariableDeclarationSyntax variable,
        string? shaderType,
        HashSet<string> structs,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        if (shaderType is "particles" or "sky" or "fog")
        {
            report(GodotRuleDescriptors.QualifierNotAllowed, variable.ModifierTokens.First(t => t.TextIs("varying")),
                $"varying は、{shaderType} のシェーダーでは使えません。");
            return;
        }

        TypeSyntax type = variable.Type;

        if (structs.Contains(type.Name))
        {
            report(GodotRuleDescriptors.TypeNotAllowed, type.NameToken, "varying に構造体の型は使えません。");
        }
        else if (type.Name == "void" || (IsBuiltInType(type.Name) && !ValueTypes.Contains(type.Name)))
        {
            report(GodotRuleDescriptors.TypeNotAllowed, type.NameToken, $"varying に {type.Name} の型は使えません。");
        }
    }

    /// <summary>uniform の既定値の定数を、uniform の型へ変換できるか (<c>convert_constant</c>)。</summary>
    private static bool ConvertsConstant(string from, string to, ExpressionSyntax value)
        => from == to
           || (from is "int" or "uint" && to == "float")
           || (from == "int" && to == "uint" && value is not PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken })
           || (from == "uint" && to == "int");

    /// <summary>Godot の組み込みの型か。</summary>
    private static bool IsBuiltInType(string name) => name == "void" || ValueTypes.Contains(name) || IsSampler(name);

    /// <summary>サンプラーの型か。</summary>
    private static bool IsSampler(string name) => name.Contains("sampler", StringComparison.Ordinal);

    /// <summary>1 つのファイルの <c>render_mode</c> か <c>stencil_mode</c> に書いたもの。</summary>
    private sealed class ModeState
    {
        /// <summary>書いた名前。</summary>
        public HashSet<string> Written { get; } = new(StringComparer.Ordinal);

        /// <summary>選択肢のあるモードの、決めた選択肢 (<c>blend</c> → <c>blend_mix</c>)。</summary>
        public Dictionary<string, string> Groups { get; } = new(StringComparer.Ordinal);

        /// <summary>ステンシルの参照値。</summary>
        public long? Reference { get; set; }
    }
}

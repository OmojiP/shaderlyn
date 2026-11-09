using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の型の書き方 (WG0324) と、宣言ごとに使える型 (WG0403) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// WG0324: ベクトルの要素がスカラーでない (<c>vec2&lt;vec2f&gt;</c>)、行列の要素が浮動小数点数でない (<c>mat3x3&lt;i32&gt;</c>)、
/// 標本を取るテクスチャの要素の型が f32・i32・u32 でない、アトミックの型が整数でない、ストレージのテクスチャの形式・アクセスが無い名前、
/// <c>var</c> のアドレス空間・アクセスが無い名前、型引数を書かず実引数も無い値の作成 (<c>mat2x2()</c>)。
/// </para>
/// <para>
/// WG0403: 構造体のメンバー・配列の要素の資源の型、最後でないメンバーの大きさを決めない配列と、それを含む構造体のメンバー、
/// 局所の <c>var</c> のアトミック・ポインター・資源・大きさを決めない配列、大きさを決めない配列を含む仮引数、function・private 以外のポインターの仮引数、
/// アトミック・ポインター・資源を含む戻り値、アドレス空間を書かない数の変数、private・function・workgroup の binding_array、構造体でない binding_array の要素 (storage と uniform)、
/// var&lt;immediate&gt; の 16 ビットの型、var&lt;workgroup&gt; の初期値、作れない値 (大きさを決めない配列、override で大きさを決める配列、アトミック、それを含む構造体)。
/// </para>
/// naga 30 はどれもコンパイルの誤りにする。
/// </remarks>
internal sealed class WgslTypeAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] AddressSpaces = ["function", "private", "workgroup", "uniform", "storage", "immediate", "handle", "task_payload", "ray_payload", "incoming_ray_payload"];

    private static readonly string[] AccessModes = ["read", "write", "read_write", "atomic"];

    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [WgslRuleDescriptors.InvalidTypeArgument, WgslRuleDescriptors.InvalidTypeForDeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Id)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message)
        {
            if (token is { IsMissing: false }
                && compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start, descriptor.Id)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            WgslTypes types = new(root);
            HashSet<string> overrides = [.. root.Declarations.OfType<VariableDeclarationSyntax>()
                .Where(d => WgslConstants.Keyword(d) == "override").SelectMany(d => d.Variables).Select(v => v.Name)];

            foreach (DeclarationSyntax declaration in root.Declarations)
            {
                switch (declaration)
                {
                    case TypedefDeclarationSyntax { Keyword.Text: "alias", Tokens: [_, { Kind: ShaderSyntaxKind.EqualsToken }, .. var aliased] }:
                        CheckType(types, types.Parse([.. aliased]), Report);
                        break;

                    case StructDeclarationSyntax structure:
                        CheckStruct(types, structure, Report);
                        break;

                    case VariableDeclarationSyntax global:
                        CheckGlobal(types, global, Report);
                        break;

                    case FunctionDeclarationSyntax function:
                        CheckFunction(types, function, Report);
                        break;
                }
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case TypeSyntax { Parent: not null } type when type.Parent is not StructDeclarationSyntax:
                        CheckType(types, types.Of(type), Report);
                        break;

                    case VariableDeclarationSyntax local when local.Parent is LocalDeclarationStatementSyntax or ForStatementSyntax:
                        CheckLocal(types, overrides, local, Report);
                        break;

                    case InvocationExpressionSyntax invocation:
                        CheckConstruction(types, overrides, invocation, Report);
                        break;
                }
            }
        }
    }

    /// <summary>型引数の決まり (WG0324) と配列の要素 (WG0403)。</summary>
    private static void CheckType(WgslTypes types, WgslTypeRef type, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        ImmutableArray<WgslTypeRef> arguments = type.Arguments;

        switch (type.Name)
        {
            case "vec2" or "vec3" or "vec4" when arguments is [{ } element] && !WgslTypes.Scalars.Contains(element.Name):
                report(WgslRuleDescriptors.InvalidTypeArgument, element.First, $"ベクトルの要素はスカラーの型でなければなりませんが、{element} です。");
                break;

            case ['m', 'a', 't', _, 'x', _] when arguments is [{ } element] && element.Name is not ("f32" or "f16" or "f64"):
                report(WgslRuleDescriptors.InvalidTypeArgument, element.First, $"行列の要素は浮動小数点数の型 (f32・f16) でなければなりませんが、{element} です。");
                break;

            case "atomic" when arguments is [{ } element] && element.Name is not ("i32" or "u32" or "i64" or "u64" or "f32"):
                report(WgslRuleDescriptors.InvalidTypeArgument, element.First, $"アトミックの型は i32 か u32 でなければなりませんが、{element} です。");
                break;

            case var name when WgslTypes.SampledTextures.Contains(name) && arguments is [{ } element] && element.Name is not ("f32" or "i32" or "u32"):
                report(WgslRuleDescriptors.InvalidTypeArgument, element.First, $"{name} の要素の型は f32・i32・u32 のどれかでなければなりませんが、{element} です。");
                break;

            case var name when name.StartsWith("texture_storage_", StringComparison.Ordinal) && arguments.Length >= 1:
                if (arguments[0] is { Tokens: [{ Kind: ShaderSyntaxKind.IdentifierToken } format] } && !WgslTypes.StorageFormats.Contains(format.Text))
                {
                    report(WgslRuleDescriptors.InvalidTypeArgument, format, $"ストレージのテクスチャの形式 '{format.Text}' はありません (rgba8unorm・r32float など)。");
                }

                if (arguments.Length >= 2 && arguments[1] is { Tokens: [{ Kind: ShaderSyntaxKind.IdentifierToken } access] } && !AccessModes.Contains(access.Text))
                {
                    report(WgslRuleDescriptors.InvalidTypeArgument, access, $"アクセス '{access.Text}' はありません (read・write・read_write)。");
                }

                break;

            case "array" when arguments.Length >= 1:
            {
                WgslTypeRef element = arguments[0];

                if (!WgslTypes.IsDataType(element))
                {
                    report(WgslRuleDescriptors.InvalidTypeForDeclaration, element.First, $"配列の要素に {element} は使えません。");
                }
                else if (types.ContainsRuntimeArray(element))
                {
                    report(WgslRuleDescriptors.InvalidTypeForDeclaration, element.First, $"配列の要素に、大きさを決めない配列 (を含む型) {element} は使えません。");
                }

                break;
            }

            case "binding_array" when arguments.Length >= 1 && arguments[0].Name == "texture_external":
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, arguments[0].First, "binding_array の要素に texture_external は使えません。");
                break;
        }

        foreach (WgslTypeRef argument in arguments.Where(a => a.Name.Length > 0))
        {
            CheckType(types, argument, report);
        }
    }

    /// <summary>構造体のメンバーの型。</summary>
    private static void CheckStruct(WgslTypes types, StructDeclarationSyntax structure, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        VariableDeclarationSyntax[] members = [.. structure.Members.OfType<VariableDeclarationSyntax>()];

        for (int i = 0; i < members.Length; i++)
        {
            WgslTypeRef type = types.Of(members[i].Type);
            ShaderSyntaxToken at = members[i].Type.NameToken;
            string name = members[i].Variables.FirstOrDefault()?.Name ?? string.Empty;

            CheckType(types, type, report);

            if (!WgslTypes.IsDataType(type))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"構造体のメンバー '{name}' に {type} は使えません (データの型だけが使えます)。");
            }
            else if (type.IsRuntimeArray && i != members.Length - 1)
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"大きさを決めない配列は、構造体の最後のメンバーにだけ書けます ('{name}')。");
            }
            else if (!type.IsRuntimeArray && types.ContainsRuntimeArray(type))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"大きさを決めない配列を含む構造体 {type} は、構造体のメンバーにできません ('{name}')。");
            }
        }
    }

    /// <summary>関数の外の変数。</summary>
    private static void CheckGlobal(WgslTypes types, VariableDeclarationSyntax declaration, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        if (WgslConstants.Keyword(declaration) != "var")
        {
            return;
        }

        ImmutableArray<ShaderSyntaxToken> modifiers = declaration.ModifierTokens;
        int keyword = modifiers.IndexOf(modifiers.Last(t => t.Text == "var"));
        ShaderSyntaxToken[] template = [.. modifiers.Skip(keyword + 1).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken)];
        string? space = template.FirstOrDefault()?.Text;
        WgslTypeRef type = types.Of(declaration.Type);
        ShaderSyntaxToken at = declaration.Type.NameToken;

        if (template.Length > 0 && !AddressSpaces.Contains(template[0].Text))
        {
            report(WgslRuleDescriptors.InvalidTypeArgument, template[0], $"アドレス空間 '{template[0].Text}' はありません (private・workgroup・uniform・storage など)。");
            return;
        }

        if (template.Length > 1 && !AccessModes.Contains(template[1].Text))
        {
            report(WgslRuleDescriptors.InvalidTypeArgument, template[1], $"アクセス '{template[1].Text}' はありません (read・read_write)。");
        }

        if (space is null && declaration.Type.NameToken.Text is not ("var" or "let" or "const" or "override") && !WgslTypes.HandleTypes.Contains(type.Name))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, at,
                $"アドレス空間を書かない var には、テクスチャ・サンプラーなどの資源の型だけが使えます ({type})。var<private> などのアドレス空間を書いてください。");
        }

        if (space is "private" or "function" or "workgroup" && type.Name == "binding_array")
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"var<{space}> に binding_array は使えません。");
        }

        if (space is "storage" or "uniform" && type is { Name: "binding_array", Arguments: [{ } element, ..] } && types.MembersOf(element.Name) is null)
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, element.First, $"var<{space}> の binding_array の要素は構造体でなければなりませんが、{element} です。");
        }

        if (space == "immediate" && types.ContainsScalar(type, s => s is "f16" or "i16" or "u16"))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"var<immediate> に 16 ビットの型 (f16・i16・u16) を含む型 {type} は使えません。");
        }

        if (space is "workgroup" or "storage" or "uniform" or "immediate" && declaration.Variables.FirstOrDefault() is { Initializer: { } } declarator
            && declarator.EqualsToken is { } equals)
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, equals, $"var<{space}> の変数に初期値は書けません。");
        }

        // 大きさを決めない配列は storage にだけ置ける。
        if (space is not null and not "storage" && types.ContainsRuntimeArray(type))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"var<{space}> に大きさを決めない配列 (を含む型) {type} は使えません。");
        }
    }

    /// <summary>関数の仮引数と戻り値。</summary>
    private static void CheckFunction(WgslTypes types, FunctionDeclarationSyntax function, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            WgslTypeRef type = types.Of(parameter.Type);
            ShaderSyntaxToken at = parameter.Type.NameToken;

            if (type is { Name: "ptr", Arguments: [{ Tokens: [{ } space] }, ..] } && space.Text is not ("function" or "private"))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"関数の仮引数のポインターは function か private のアドレス空間でなければなりませんが、{space.Text} です。");
            }
            else if (types.ContainsRuntimeArray(type))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"関数の仮引数 '{parameter.Name}' に、大きさを決めない配列 (を含む型) {type} は使えません。");
            }
            else if (type is { Name: "ptr", Arguments: [_, { } pointee, ..] } && types.ContainsRuntimeArray(pointee))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"大きさを決めない配列は storage のアドレス空間にだけ置けます ({type})。");
            }
        }

        // -> の後ろの型 (書かなければ void)。
        if (function.ReturnType.NameToken.IsMissing || function.ReturnType.Name == "void")
        {
            return;
        }

        WgslTypeRef result = types.Of(function.ReturnType);

        if (result.Name == "ptr" || !WgslTypes.IsDataType(result) || types.ContainsAtomic(result) || types.ContainsRuntimeArray(result))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, function.ReturnType.NameToken, $"関数の戻り値に {result} は使えません (ポインター・資源・アトミック・大きさを決めない配列を含む型は返せません)。");
        }
    }

    /// <summary>局所の var と let。</summary>
    private static void CheckLocal(WgslTypes types, HashSet<string> overrides, VariableDeclarationSyntax declaration, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        string? keyword = WgslConstants.Keyword(declaration);

        if (declaration.Type.NameToken.Text is "var" or "let" or "const" or "override")
        {
            return;
        }

        WgslTypeRef type = types.Of(declaration.Type);
        ShaderSyntaxToken at = declaration.Type.NameToken;

        if (keyword == "var"
            && (type.Name is "ptr" or "binding_array" || WgslTypes.HandleTypes.Contains(type.Name) || types.ContainsAtomic(type) || types.ContainsRuntimeArray(type)))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"局所の var に {type} は使えません (ポインター・資源・アトミック・大きさを決めない配列を含む型は置けません)。");
        }

        if (keyword is "let" or "const" && OverrideSized(overrides, type))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, at, $"override で大きさを決める配列 {type} の値は作れません。");
        }
    }

    /// <summary>値の作成 (array&lt;u32&gt;(…)・atomic&lt;i32&gt;(0)・S()・mat2x2())。vec2() は要素が抽象的な整数の 0 になるので通す。</summary>
    private static void CheckConstruction(WgslTypes types, HashSet<string> overrides, InvocationExpressionSyntax invocation, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        if (invocation.Target is not IdentifierExpressionSyntax { Identifier: { } name })
        {
            return;
        }

        int count = invocation.ArgumentExpressions.Count();

        if (!invocation.TypeArgumentTokens.IsDefaultOrEmpty && invocation.OpenAngle is { } open && invocation.CloseAngle is { } close)
        {
            WgslTypeRef type = types.Parse([name, open, .. invocation.TypeArgumentTokens, close]);
            CheckType(types, type, report);

            if (type.Name == "atomic" || types.ContainsAtomic(type))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, name, $"アトミックの値 {type} は作れません。");
            }
            else if (type.IsRuntimeArray)
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, name, $"大きさを決めない配列 {type} の値は作れません。");
            }
            else if (OverrideSized(overrides, type))
            {
                report(WgslRuleDescriptors.InvalidTypeForDeclaration, name, $"override で大きさを決める配列 {type} の値は作れません。");
            }

            return;
        }

        if (types.MembersOf(name.Text) is { } && (types.ContainsRuntimeArray(new WgslTypeRef(name.Text, [], [name])) || types.ContainsAtomic(new WgslTypeRef(name.Text, [], [name]))))
        {
            report(WgslRuleDescriptors.InvalidTypeForDeclaration, name, $"大きさを決めない配列かアトミックを含む構造体 {name.Text} の値は作れません。");
        }
        else if (count == 0 && Semantics.Symbols.WgslTypeSystem.IsTypeGenerator(name.Text) && name.Text.StartsWith("mat", StringComparison.Ordinal))
        {
            report(WgslRuleDescriptors.InvalidTypeArgument, name, $"型引数も実引数も無い {name.Text}() は、要素の型が決まりません。{name.Text}<f32>() のように型引数を書いてください。");
        }
    }

    /// <summary>大きさを override で決める配列か。</summary>
    private static bool OverrideSized(HashSet<string> overrides, WgslTypeRef type)
        => type is { Name: "array", Arguments: [_, { Tokens: [{ Kind: ShaderSyntaxKind.IdentifierToken } size] }] } && overrides.Contains(size.Text);
}

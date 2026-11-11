using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang のポインター (<c>Ptr&lt;T, Access, AddressSpace&gt;</c>・<c>T*</c>) の暗黙の変換と、読むだけのポインターへの書き込みを検査する (CF0410・SG0309)。
/// </summary>
/// <remarks>
/// <para>
/// ポインターは、要素の型・アクセス (<c>Access::Read</c> / <c>Access::ReadWrite</c>。書かなければ ReadWrite)・アドレス空間 (書かなければ Device) の
/// どれかが違えば、暗黙に変換されない (slangc 2026.19 の E30019)。変換するなら <c>Ptr&lt;…&gt;(p)</c> と書く。<c>T*</c> は <c>Ptr&lt;T&gt;</c> と同じである。
/// </para>
/// <para>
/// <c>Access::Read</c> のポインターの指す先 (<c>p[i]</c>・<c>*p</c>) には書き込めない (E30011)。out / inout の仮引数にも渡せない (E30047)。
/// <c>typealias ReadPtr = Ptr&lt;int, Access::Read&gt;;</c> の別名も読む。
/// </para>
/// </remarks>
internal sealed class SlangPointerAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>ポインターの型の 3 つの部分。</summary>
    private readonly record struct PointerType(string Element, string Access, string Space)
    {
        public override string ToString() => $"Ptr<{Element}, Access::{Access}, AddressSpace::{Space}>";
    }

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.ValueNotConvertible, SlangRuleDescriptors.NotWritable, SlangRuleDescriptors.AddressNotTakable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

            if (!tokens.Any(t => t.Text is "Ptr" or "*" or "&"))
            {
                continue;
            }

            CompilationUnitSyntax root = program.Tree.Root;
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            Dictionary<string, PointerType?> aliases = new(StringComparer.Ordinal);

            // typealias ReadPtr = Ptr<int, Access::Read, AddressSpace::Device>;
            for (int i = 0; i + 4 < tokens.Length; i++)
            {
                if (tokens[i].Text == "typealias" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 2].Kind == ShaderSyntaxKind.EqualsToken
                    && tokens[i + 3].Text == "Ptr" && tokens[i + 4].Kind == ShaderSyntaxKind.LessThanToken)
                {
                    List<ShaderSyntaxToken> arguments = [];
                    int depth = 0;

                    for (int j = i + 4; j < tokens.Length && tokens[j].Kind != ShaderSyntaxKind.SemicolonToken; j++)
                    {
                        if (tokens[j].Kind == ShaderSyntaxKind.LessThanToken && depth++ == 0)
                        {
                            continue;
                        }

                        if (tokens[j].Kind == ShaderSyntaxKind.GreaterThanToken && --depth == 0)
                        {
                            break;
                        }

                        arguments.Add(tokens[j]);
                    }

                    string name = tokens[i + 1].Text;
                    aliases[name] = aliases.ContainsKey(name) ? null : Parse([.. arguments]);
                }
            }

            PointerType? OfType(TypeSyntax type)
            {
                if (type.IsPointer && type.PointerTokens.Length == 1 && type.OpenAngle is null && type.NestedTypeTokens.IsDefaultOrEmpty)
                {
                    return new PointerType(type.NameToken.Text, "ReadWrite", "Device");
                }

                if (type.IsPointer)
                {
                    return null;
                }

                if (type.NameToken.Text == "Ptr" && !type.TemplateArguments.IsDefaultOrEmpty)
                {
                    return Parse(type.TemplateArguments);
                }

                return type.OpenAngle is null && aliases.TryGetValue(type.NameToken.Text, out PointerType? alias) ? alias : null;
            }

            PointerType? OfValue(ExpressionSyntax expression) => expression switch
            {
                ParenthesizedExpressionSyntax parenthesized => OfValue(parenthesized.Expression),
                IdentifierExpressionSyntax identifier when binder.ResolveName(identifier).Candidates is [var candidate] && !candidate.Type.IsArray => candidate.Declarator switch
                {
                    VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => OfType(declaration.Type),
                    ParameterSyntax parameter => OfType(parameter.Type),
                    _ => null,
                },
                InvocationExpressionSyntax { Target: GenericNameExpressionSyntax { Name: "Ptr" } generic } => Parse(generic.TypeArguments),
                InvocationExpressionSyntax { TargetName: "Ptr", HasTypeArguments: true } call => Parse(call.TypeArgumentTokens),
                InvocationExpressionSyntax { TargetName: { } aliasName, HasTypeArguments: false } aliasCall when aliases.TryGetValue(aliasName, out PointerType? alias) => alias,
                BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken } offset => OfValue(offset.Left),
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandToken, Operand: IdentifierExpressionSyntax addressed }
                    when DeclaredType(addressed) is { IsPointer: false, OpenAngle: null } pointee && pointee.NestedTypeTokens.IsDefaultOrEmpty
                    => new PointerType(pointee.NameToken.Text, "ReadWrite", "Device"),
                _ => null,
            };

            TypeSyntax? DeclaredType(IdentifierExpressionSyntax identifier)
                => binder.ResolveName(identifier).Candidates is [var candidate] && !candidate.Type.IsArray
                    ? candidate.Declarator switch
                    {
                        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } => declaration.Type,
                        ParameterSyntax parameter => parameter.Type,
                        _ => null,
                    }
                    : null;

            HashSet<string> interfaceNames = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
                .Where(t => t.Keyword.Text == "interface" && t.NameToken is not null).Select(t => t.NameToken!.Text)];

            // 読むだけのポインターの指す先 (p[i]・*p・*(p + 1))。
            bool ReadOnlyTarget(ExpressionSyntax expression) => expression switch
            {
                ElementAccessExpressionSyntax element => OfValue(element.Target) is { Access: "Read" },
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AsteriskToken } dereference => OfValue(dereference.Operand) is { Access: "Read" },
                _ => false,
            };

            // 読むだけの資源の要素 (StructuredBuffer の s[i] など)。
            bool ReadOnlyElement(ExpressionSyntax expression)
                => expression is ElementAccessExpressionSyntax { Target: IdentifierExpressionSyntax buffer }
                   && DeclaredType(buffer)?.NameToken.Text is "StructuredBuffer" or "Buffer" or "ByteAddressBuffer" or "ConstantBuffer" || expression is ElementAccessExpressionSyntax { Target: IdentifierExpressionSyntax texture } && DeclaredType(texture)?.NameToken.Text is { } textureType && textureType.StartsWith("Texture", StringComparison.Ordinal);

            // 局所変数と仮引数のアドレスは、CPU と CUDA のコンパイル先では取れる。コンパイル先を設定ファイル (slang: target) に書いて GPU のものと決まるときだけ見る。
            bool gpu = compilation.Environment is Semantics.Environments.SlangEnvironment { Target: { } configured, TargetsHost: false } && configured.Length > 0;

            void Report(DiagnosticDescriptor descriptor, ExpressionSyntax at, params object?[] arguments)
            {
                if (compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
                }
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case VariableDeclarationSyntax declaration when OfType(declaration.Type) is { } target:
                        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                        {
                            // インターフェイスの型の値のアドレス (&ib の ib が IBase) は、ポインターにできない (存在型の値には決まった置き場所が無い)。
                            if (variable.Initializer is PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandToken, Operand: IdentifierExpressionSyntax existential }
                                && DeclaredType(existential) is { IsPointer: false } existentialType && interfaceNames.Contains(existentialType.NameToken.Text))
                            {
                                Report(HlslRuleDescriptors.ValueNotConvertible, variable.Initializer, target.ToString(), $"&{existential.Name}",
                                    $"インターフェイスの型 {existentialType.NameToken.Text} の値のアドレスは、ポインターにできません。");
                                continue;
                            }

                            if (variable.Initializer is { } initializer && !variable.IsArray && OfValue(initializer) is { } source && source != target)
                            {
                                Report(HlslRuleDescriptors.ValueNotConvertible, initializer, target.ToString(), source.ToString(),
                                    "要素の型・アクセス・アドレス空間の違うポインターは、暗黙に変換されません。Ptr<…>(p) と書いて変換してください。");
                            }
                        }

                        break;

                    // __getAddress(x)。GPU のコンパイル先 (設定ファイルに書いたもの) では、局所変数と仮引数のアドレスは取れない (E31160)。読むだけの資源の要素のアドレスも取れない (&s[i] と InterlockedAdd は SG0309 が見る)。
                    case InvocationExpressionSyntax { TargetName: "__getAddress" } address when address.ArgumentExpressions.ToArray() is [var addressed]:
                        if (ReadOnlyElement(addressed))
                        {
                            Report(SlangRuleDescriptors.AddressNotTakable, addressed, "読むだけの資源の要素のアドレスは取れません。");
                        }
                        else if (gpu && addressed is IdentifierExpressionSyntax local
                                 && binder.ResolveName(local).Kind == DeclaredNameKind.Local
                                 && DeclaredType(local) is { } localType
                                 && localType.NameToken.Text is not ("ConstantBuffer" or "ParameterBlock")
                                 && !(binder.ResolveName(local).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax shared } }] && shared.ModifierTokens.Any(t => t.Text == "groupshared")))
                        {
                            Report(SlangRuleDescriptors.AddressNotTakable, addressed, $"GPU のコンパイル先では、局所変数と仮引数 ('{local.Name}') のアドレスは取れません。");
                        }

                        break;

                    case AssignmentExpressionSyntax assignment when ReadOnlyTarget(assignment.Left):
                        Report(SlangRuleDescriptors.NotWritable, assignment.Left, "Access::Read のポインターの指す先には書き込めません。");
                        break;

                    case InvocationExpressionSyntax { TargetName: { } name } call
                        when compilation.GetFunctionDeclarations(name, program) is [var function]:
                        ParameterSyntax[] parameters = [.. function.ParameterList];
                        ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                        for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
                        {
                            if (parameters[i].ModifierTokens.Any(m => m.Text is "out" or "inout") && ReadOnlyTarget(arguments[i]))
                            {
                                Report(SlangRuleDescriptors.NotWritable, arguments[i], $"Access::Read のポインターの指す先は、out / inout の仮引数 ('{parameters[i].Name}') に渡せません。");
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>Ptr の型引数 (<c>int, Access::Read, AddressSpace::Device</c>) を読む。読めなければ <see langword="null"/>。</summary>
    private static PointerType? Parse(ImmutableArray<ShaderSyntaxToken> arguments)
    {
        List<List<ShaderSyntaxToken>> parts = [[]];
        int depth = 0;

        foreach (ShaderSyntaxToken token in arguments)
        {
            depth += token.Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

            if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                parts.Add([]);
                continue;
            }

            parts[^1].Add(token);
        }

        if (parts[0] is not [{ Kind: ShaderSyntaxKind.IdentifierToken } element] || parts.Count > 3)
        {
            return null;
        }

        string? access = parts.Count > 1 ? Member(parts[1], "Access") : "ReadWrite";
        string? space = parts.Count > 2 ? Member(parts[2], "AddressSpace") : "Device";
        return access is null || space is null ? null : new PointerType(element.Text, access, space);
    }

    /// <summary><c>Access::Read</c> の <c>Read</c>。その形でなければ <see langword="null"/>。</summary>
    private static string? Member(List<ShaderSyntaxToken> part, string owner)
        => part is [{ Text: var o }, { Kind: ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.DotToken }, { Kind: ShaderSyntaxKind.IdentifierToken } member] && o == owner
            ? member.Text
            : null;
}

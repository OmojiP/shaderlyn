using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の宣言の形を検査する。宣言に使えない型 (SG0407)、属性の値 (SG0304)、初期値の無い static const (SG0318)、修飾子 (SG0102)、名前の無い enum class (SG0101)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0407: void の変数 (E30009)、ビットフィールドの整数でない型と型の幅を超える幅 (E31301・E31300)、整数でない列挙型の基の型 (E32000)、
/// スカラーでも列挙型でもない特殊化定数 (E31218)</item>
/// <item>SG0304: <c>[numthreads]</c> の 1 より小さい値 (E31102)、<c>[WaveSize]</c> の最初の値の、4 から 128 までの 2 の累乗でないもの (E31103)、
/// <c>[COM]</c> の GUID (- を除いて 16 進数 32 桁) でないもの (E31123)、<c>[RemovedSince]</c> の -1 から 9999 の外の版 (E31209)</item>
/// <item>SG0318: 初期値の無い static const (E31225)。インターフェイスの要件と extern は除く</item>
/// <item>SG0102: 初期値を書いた groupshared (E30623)、ポインターの coherent / globallycoherent (E30078)、typedef の volatile (E20019)、
/// ポインターの constexpr / const (E20017)、static の特殊化定数 (E31219)、push constant でもある特殊化定数 (E31220)</item>
/// <item>SG0101: 名前の無い enum class (E32001)</item>
/// </list>
/// GLSL の書き方のファイル (#version のあるもの) は見ない。
/// </remarks>
internal sealed class SlangDeclarationFormAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        SlangRuleDescriptors.InvalidDeclarationType,
        SlangRuleDescriptors.InvalidAttributeValue,
        SlangRuleDescriptors.StaticConstWithoutInitializer,
        SlangRuleDescriptors.ModifierNotAllowed,
        SlangRuleDescriptors.DeclarationNotAllowed,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(program.Tree.Text.ToString(), @"^\s*#\s*version\b", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                continue;
            }

            CompilationUnitSyntax root = program.Tree.Root;
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
            ShaderTypeSystem types = compilation.GetExpressionTypeBinder(program).TypeSystem;
            Dictionary<ShaderSyntaxToken, int> positions = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                positions[tokens[i]] = i;
            }

            HashSet<string> enums = [.. root.DescendantNodesAndSelf().OfType<EnumDeclarationSyntax>().Select(e => e.Name)];
            HashSet<string> structs = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(s => s.NameToken is not null).Select(s => s.NameToken!.Text)];
            structs.ExceptWith(root.TypeAliases.Keys);

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? at, string message)
            {
                if (at is { IsMissing: false } && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            // 資源の型のフィールドを持つ構造体の名前。
            HashSet<string> resourceStructs = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
                .Where(t => t.NameToken is not null && t.Members.OfType<VariableDeclarationSyntax>().Any(f => IsResourceName(f.Type.NameToken.Text)))
                .Select(t => t.NameToken!.Text)];

            CheckAttributes(tokens, Report);

            // 関数の本体の範囲 (トークンの位置)。局所の宣言の属性を見分ける。
            List<(int Open, int Close)> bodies = [];

            foreach (FunctionDeclarationSyntax function in root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                if (function.Body is { } body && positions.TryGetValue(body.OpenBrace, out int open) && positions.TryGetValue(body.CloseBrace, out int close))
                {
                    bodies.Add((open, close));
                }
            }

            CheckPlacements(tokens, bodies, Report);

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case EnumDeclarationSyntax { ScopeKeyword: { } scope, NameToken: null }:
                        Report(SlangRuleDescriptors.DeclarationNotAllowed, scope, "名前が存在しない enum class は書けません。名前を付けるか、class を外してください。");
                        break;

                    case EnumDeclarationSyntax enumeration
                        when enumeration.BaseTypeTokens.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken) is { } tag
                             && types.TryDescribeNumeric(tag.Text, out ShaderNumericShape shape)
                             && !SlangConstants.IsIntegerType(tag.Text)
                             && (shape.Kind != ShaderNumericKind.Scalar || !SlangConstants.IsIntegerType(shape.BaseName)):
                        Report(SlangRuleDescriptors.InvalidDeclarationType, tag, $"列挙型の基の型は整数でなければなりません ({tag.Text} は使用できません)。");
                        break;

                    case TypedefDeclarationSyntax typedef when typedef.Tokens.FirstOrDefault(t => t.Text == "volatile") is { } volatileToken:
                        Report(SlangRuleDescriptors.ModifierNotAllowed, volatileToken, "typedef に volatile は書けません。");
                        break;

                    case VariableDeclarationSyntax declaration:
                        CheckVariable(declaration, tokens, positions, types, enums, structs, resourceStructs, Report);
                        break;
                }
            }
        }
    }

    private static void CheckVariable(
        VariableDeclarationSyntax declaration,
        ImmutableArray<ShaderSyntaxToken> tokens,
        Dictionary<ShaderSyntaxToken, int> positions,
        ShaderTypeSystem types,
        HashSet<string> enums,
        HashSet<string> structs,
        HashSet<string> resourceStructs,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        TypeSyntax type = declaration.Type;
        bool local = declaration.Parent is LocalDeclarationStatementSyntax;
        bool global = declaration.Parent is CompilationUnitSyntax or NamespaceDeclarationSyntax;
        bool field = declaration.Parent is StructDeclarationSyntax { Keyword.Text: "struct" or "class" };
        ImmutableArray<ShaderSyntaxToken> modifiers = [.. declaration.ModifierTokens, .. type.ModifierTokens];
        bool isStatic = modifiers.Any(t => t.Text == "static");
        bool isConst = modifiers.Any(t => t.Text == "const");

        if ((local || global || field) && type.Name == "void" && !type.IsPointer && type.NestedTypeTokens.IsDefaultOrEmpty)
        {
            report(SlangRuleDescriptors.InvalidDeclarationType, type.NameToken, "変数の型に void は使用できません。");
        }

        if (type.IsPointer || type.NameToken.Text == "Ptr")
        {
            if (modifiers.FirstOrDefault(t => t.Text is "coherent" or "globallycoherent") is { } coherent)
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, coherent, $"ポインターに {coherent.Text} は書けません。");
            }

            if (type.IsPointer && modifiers.FirstOrDefault(t => t.Text is "constexpr") is { } constant)
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, constant, $"C の書き方のポインターに {constant.Text} は書けません。");
            }
        }

        if (field && !isStatic && modifiers.FirstOrDefault(t => t.Text == "groupshared") is { } sharedField)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, sharedField, "構造体のフィールドに groupshared は書けません。関数の外の変数にしてください。");
        }

        // 関数の外の static の資源 (と資源を持つ構造体) の初期値 (E30076)。資源は定数のように作れない。
        if (global && isStatic && !isConst && declaration.Variables.Any(v => v.Initializer is not null)
            && (IsResourceName(type.NameToken.Text) || resourceStructs.Contains(type.NameToken.Text)))
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, declaration.Variables.First(v => v.Initializer is not null).NameToken,
                $"関数の外の static の変数 ({type.NameToken.Text}) に、資源を含む初期値は書けません。資源は関数の中で使用するか、static でない変数にしてください。");
        }

        if (modifiers.Any(t => t.Text == "groupshared"))
        {
            foreach (VariableDeclaratorSyntax variable in declaration.Variables.Where(v => v.Initializer is not null))
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, variable.NameToken, $"groupshared の '{variable.Name}' に初期値は書けません。値は関数の中で入れてください。");
            }
        }

        if ((global || field) && isStatic && isConst && !modifiers.Any(t => t.Text == "extern"))
        {
            foreach (VariableDeclaratorSyntax variable in declaration.Variables.Where(v => v.Initializer is null))
            {
                report(SlangRuleDescriptors.StaticConstWithoutInitializer, variable.NameToken, $"static const の '{variable.Name}' には初期値が必要です。");
            }
        }

        if (field)
        {
            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                CheckBitField(type, variable, tokens, positions, types, structs, report);
            }
        }

        if (global && AttributesBefore(tokens, positions, declaration) is { Count: > 0 } attributes
            && attributes.FirstOrDefault(a => a.Text is "constant_id" or "specialization_constant" or "SpecializationConstant") is { } specialization)
        {
            bool scalar = enums.Contains(type.Name) || (types.TryDescribeNumeric(type.Name, out ShaderNumericShape shape) && shape.Kind == ShaderNumericKind.Scalar);

            if (!scalar && !type.IsPointer)
            {
                report(SlangRuleDescriptors.InvalidDeclarationType, specialization, $"特殊化定数の型は、スカラーか列挙型でなければなりません ({type.Name} は使用できません)。");
            }

            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                if (isStatic)
                {
                    report(SlangRuleDescriptors.ModifierNotAllowed, variable.NameToken, $"特殊化定数の '{variable.Name}' は static にできません。");
                }
                else if (attributes.Any(a => a.Text == "push_constant"))
                {
                    report(SlangRuleDescriptors.ModifierNotAllowed, variable.NameToken, $"'{variable.Name}' を push constant と特殊化定数の両方にはできません。");
                }
            }
        }
    }

    /// <summary>資源の型の名前 (テクスチャ・バッファー・サンプラー)。</summary>
    private static bool IsResourceName(string name)
        => name.StartsWith("Texture", StringComparison.Ordinal) || name.StartsWith("RWTexture", StringComparison.Ordinal)
           || name.EndsWith("StructuredBuffer", StringComparison.Ordinal) || name.EndsWith("ByteAddressBuffer", StringComparison.Ordinal)
           || name is "Buffer" or "RWBuffer" or "SamplerState" or "SamplerComparisonState" or "RaytracingAccelerationStructure";

    /// <summary>ビットフィールド (<c>int a : 8;</c>) の型と幅。幅のトークンは構文木に残らないので、名前の後ろのトークンを読む。</summary>
    private static void CheckBitField(
        TypeSyntax type,
        VariableDeclaratorSyntax variable,
        ImmutableArray<ShaderSyntaxToken> tokens,
        Dictionary<ShaderSyntaxToken, int> positions,
        ShaderTypeSystem types,
        HashSet<string> structs,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        if (!positions.TryGetValue(variable.NameToken, out int index)
            || index + 2 >= tokens.Length
            || tokens[index + 1].Kind != ShaderSyntaxKind.ColonToken
            || tokens[index + 2] is not { Kind: ShaderSyntaxKind.NumericLiteralToken } width
            || !ShaderLiteral.TryGetInt64(width, out long bits))
        {
            return;
        }

        string name = SlangConstants.IsIntegerType(type.Name) ? type.Name
            : types.TryDescribeNumeric(type.Name, out ShaderNumericShape shape) && shape.Kind == ShaderNumericKind.Scalar ? shape.BaseName
            : type.Name;

        // 構造体と浮動小数は誤り。typedef の別名など、決められない名前は見ない。
        bool known = types.TryDescribeNumeric(type.Name, out _) || structs.Contains(type.Name);

        if (known && (!SlangConstants.IsIntegerType(name) || name == "bool"))
        {
            report(SlangRuleDescriptors.InvalidDeclarationType, variable.NameToken, $"ビットフィールドの型は整数でなければなりません ({type.Name} は使用できません)。");
            return;
        }

        int? size = name switch
        {
            "int8_t" or "uint8_t" => 8,
            "int16_t" or "uint16_t" => 16,
            "int" or "uint" or "int32_t" or "uint32_t" or "dword" => 32,
            "int64_t" or "uint64_t" => 64,
            _ => null,
        };

        if (size is { } limit && bits > limit)
        {
            report(SlangRuleDescriptors.InvalidDeclarationType, variable.NameToken, $"ビットフィールドの幅 ({bits}) が、型 {type.Name} の幅 ({limit}) を超えています。");
        }
    }

    /// <summary>宣言の前の属性の名前 (<c>[vk::constant_id(1)]</c> の <c>constant_id</c>)。</summary>
    private static List<ShaderSyntaxToken> AttributesBefore(ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<ShaderSyntaxToken, int> positions, VariableDeclarationSyntax declaration)
    {
        List<ShaderSyntaxToken> names = [];

        if (declaration.FirstToken is not { } first || !positions.TryGetValue(first, out int index))
        {
            return names;
        }

        for (int i = index - 1; i >= 0 && tokens[i].Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.OpenBraceToken); i--)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken
                && i > 0 && tokens[i - 1].Kind is ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.ColonColonToken)
            {
                names.Add(tokens[i]);
            }
        }

        return names;
    }

    /// <summary>属性の値 ([numthreads]・[WaveSize]・[COM]・[RemovedSince])。トークンの並びから読む (関数の外の宣言の属性は構文木に残らない)。</summary>
    private static void CheckAttributes(ImmutableArray<ShaderSyntaxToken> tokens, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            ShaderSyntaxToken name = tokens[i];

            if (name.Kind != ShaderSyntaxKind.IdentifierToken
                || name.Text is not ("numthreads" or "WaveSize" or "COM" or "RemovedSince")
                || tokens[i - 1].Kind != ShaderSyntaxKind.OpenBracketToken
                || tokens[i + 1].Kind != ShaderSyntaxKind.OpenParenToken)
            {
                continue;
            }

            List<List<ShaderSyntaxToken>> arguments = [[]];
            int depth = 0;
            int j = i + 2;

            for (; j < tokens.Length; j++)
            {
                ShaderSyntaxToken token = tokens[j];

                if (token.Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    depth++;
                }
                else if (token.Kind == ShaderSyntaxKind.CloseParenToken && depth-- == 0)
                {
                    break;
                }
                else if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
                {
                    arguments.Add([]);
                    continue;
                }

                arguments[^1].Add(token);
            }

            if (j + 1 >= tokens.Length || tokens[j + 1].Kind != ShaderSyntaxKind.CloseBracketToken)
            {
                continue;
            }

            long?[] values = [.. arguments.Select(Integer)];

            switch (name.Text)
            {
                case "numthreads" when values.Any(v => v is < 1):
                    report(SlangRuleDescriptors.InvalidAttributeValue, name, "[numthreads] の値は 1 以上でなければなりません。");
                    break;

                case "WaveSize" when values.Length > 0 && values[0] is { } size && (size is < 4 or > 128 || (size & (size - 1)) != 0):
                    report(SlangRuleDescriptors.InvalidAttributeValue, name, $"[WaveSize] の値は、4 から 128 までの 2 の累乗でなければなりません ({size} は使用できません)。");
                    break;

                case "COM" when arguments is [[{ Kind: ShaderSyntaxKind.StringLiteralToken } guid]] && !IsGuid(guid.Text):
                    report(SlangRuleDescriptors.InvalidAttributeValue, name, $"[COM] の GUID ({guid.Text}) の書き方が正しくありません。16 進数 32 桁を書いてください (- で区切れます)。");
                    break;

                case "RemovedSince" when values.Length > 0 && values[0] is { } version && version is < -1 or > 9999:
                    report(SlangRuleDescriptors.InvalidAttributeValue, name, $"[RemovedSince] のバージョン ({version}) が、使用できる範囲 (-1 から 9999) の外です。");
                    break;
            }
        }
    }

    /// <summary>
    /// 書く場所の決まった修飾子と属性。<c>[UnscopedEnum]</c> の <c>enum class</c> (E31202)、構造体の宣言と局所変数の <c>[[vk::offset]]</c> (E31002)、
    /// 構造体・列挙型・関数の <c>dyn</c> (E31201。インターフェイスと、インターフェイスの型の変数・仮引数・フィールドには書ける)。
    /// </summary>
    private static void CheckPlacements(ImmutableArray<ShaderSyntaxToken> tokens, List<(int Open, int Close)> bodies, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            if (token.Text == "UnscopedEnum" && i > 0 && tokens[i - 1].Kind == ShaderSyntaxKind.OpenBracketToken
                && NextDeclarationKeyword(tokens, i) is { } keyword && keyword + 1 < tokens.Length
                && tokens[keyword].Text == "enum" && tokens[keyword + 1].Text is "class" or "struct")
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, token, $"[UnscopedEnum] は enum {tokens[keyword + 1].Text} と重なります (列挙子を修飾しないで書く場合は、{tokens[keyword + 1].Text} を外してください)。");
            }
            else if (token.Text == "offset" && i > 1 && tokens[i - 1].Kind == ShaderSyntaxKind.ColonColonToken && tokens[i - 2].Text == "vk"
                     && NextDeclarationKeyword(tokens, i) is { } next
                     && (tokens[next].Text == "struct" || bodies.Any(b => i > b.Open && i < b.Close)))
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, token, "[[vk::offset]] は、構造体のフィールドにだけ書けます。");
            }
            else if (token.Text is "__magic_type" or "__magic_enum" or "__builtin_type" or "__builtin_requirement"
                     && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken)
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, token, $"{token.Text} は、slangc の標準のモジュールの中でだけ書けます。");
            }
            else if (token.Text == "dyn" && i + 1 < tokens.Length
                     && (tokens[i + 1].Text is "struct" or "enum" or "class"
                         || (i + 3 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 2].Kind == ShaderSyntaxKind.IdentifierToken
                             && tokens[i + 3].Kind == ShaderSyntaxKind.OpenParenToken
                             && (i == 0 || tokens[i - 1].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.CloseBracketToken)
                             && !bodies.Any(b => i > b.Open && i < b.Close))))
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, token, tokens[i + 1].Text is "struct" or "enum" or "class"
                    ? $"{tokens[i + 1].Text} に dyn は書けません (dyn はインターフェイスと、その型の変数に書きます)。"
                    : "関数に dyn は書けません (dyn はインターフェイスと、その型の変数に書きます)。");
            }
        }
    }

    /// <summary>属性 (index の名前を含む [...]) の後ろの、次の属性でない最初のトークンの位置。</summary>
    private static int? NextDeclarationKeyword(ImmutableArray<ShaderSyntaxToken> tokens, int index)
    {
        int i = index;

        while (i < tokens.Length)
        {
            // 今の属性の ] まで進む。
            int depth = 0;

            for (; i < tokens.Length; i++)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    depth++;
                }
                else if (tokens[i].Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    depth--;
                }
                else if (tokens[i].Kind == ShaderSyntaxKind.CloseBracketToken && depth == 0)
                {
                    break;
                }
            }

            while (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.CloseBracketToken)
            {
                i++;
            }

            if (i >= tokens.Length || tokens[i].Kind != ShaderSyntaxKind.OpenBracketToken)
            {
                return i < tokens.Length ? i : null;
            }

            i++;
        }

        return null;
    }

    /// <summary>-1・3 のような整数の実引数の値。</summary>
    private static long? Integer(List<ShaderSyntaxToken> argument) => argument switch
    {
        [{ Kind: ShaderSyntaxKind.NumericLiteralToken } literal] when ShaderLiteral.TryGetInt64(literal, out long value) => value,
        [{ Kind: ShaderSyntaxKind.MinusToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken } literal] when ShaderLiteral.TryGetInt64(literal, out long value) => -value,
        _ => null,
    };

    /// <summary>- を除いて 16 進数 32 桁か (slangc が GUID として読む形)。</summary>
    private static bool IsGuid(string literal)
    {
        string text = literal.Trim('"').Replace("-", string.Empty, StringComparison.Ordinal);
        return text.Length == 32 && text.All(char.IsAsciiHexDigit);
    }
}

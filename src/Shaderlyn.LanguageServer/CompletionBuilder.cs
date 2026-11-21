using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.Unity;

namespace Shaderlyn.LanguageServer;

/// <summary>補完の候補 1 つ。</summary>
/// <param name="Label">表示する名前。</param>
/// <param name="Kind">LSP の CompletionItemKind。</param>
/// <param name="Detail">名前の右に出す短い説明。</param>
/// <param name="Documentation">候補を選んだときに出す説明 (Markdown)。</param>
internal readonly record struct CompletionItem(string Label, int Kind, string? Detail, string? Documentation = null);

/// <summary>
/// カーソルの位置で書ける名前を挙げる。
/// </summary>
/// <remarks>
/// <para>
/// <b>この解析ツールが挙げる候補には条件が付く。</b>
/// <c>#ifdef</c> で守られた構造体のメンバーは、
/// その構成でだけ存在することを添えて出す。
/// 何も断らずに並べると、別の構成で壊れるコードを書かせることになる。
/// </para>
/// <para>
/// <b>絞り込みはエディタに任せる。</b>
/// 打ちかけの語で候補を削ると、書き直したときに候補が戻らない。
/// ここでは「その位置で書ける名前」をすべて返す。
/// </para>
/// </remarks>
internal static partial class CompletionBuilder
{
    /// <summary>LSP の CompletionItemKind。必要なものだけ。</summary>
    private const int KindField = 5;
    private const int KindVariable = 6;
    private const int KindFunction = 3;
    private const int KindStruct = 22;
    private const int KindKeyword = 14;

    /// <summary>
    /// 補完の候補を組み立てる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>候補。</returns>
    public static ImmutableArray<CompletionItem> Build(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        // 指令の行やコメントの中で、HLSL のコードの候補を出さない。
        switch (CompletionPlaces.Find(compilation, offset))
        {
            case CompletionPlace.None:
                return [];

            case CompletionPlace.DirectiveName:
                return BuildDirectives(compilation.IsGlsl);

            // #pragma の名前とエントリポイントの指定は Unity の書き方である。ほかの環境では #pragma を読まない。
            case CompletionPlace.PragmaName:
                return compilation.GetUnity() is null ? [] : BuildPragmas();

            case CompletionPlace.EntryPoint:
                return compilation.GetUnity() is null ? [] : BuildEntryPoints(compilation);

            case CompletionPlace.Condition:
                return BuildConditionNames(compilation, includeDefined: true);

            case CompletionPlace.DefinedName:
                return BuildConditionNames(compilation, includeDefined: false);

            case CompletionPlace.MacroName:
                return BuildMacroNames(compilation);

            case CompletionPlace.GodotShaderType:
                return [.. GodotBuiltins.Instance.ShaderTypes.Order(StringComparer.Ordinal).Select(t => new CompletionItem(t, KindKeyword, "シェーダーの種類"))];

            case CompletionPlace.GodotRenderMode:
                return BuildGodotModes(compilation, stencil: false);

            case CompletionPlace.GodotStencilMode:
                return BuildGodotModes(compilation, stencil: true);

            case CompletionPlace.GodotUniformHint:
                return [.. Godot.Parsing.GodotKeywords.Hints.Order(StringComparer.Ordinal).Select(h => new CompletionItem(h, KindKeyword,
                    h.StartsWith("filter_", StringComparison.Ordinal) ? "フィルター" : h.StartsWith("repeat_", StringComparison.Ordinal) ? "繰り返し" : "ヒント"))];

            default:
                break;
        }

        // "." の直後なら、その型が持つものだけを出す。
        if (FindMemberTarget(compilation, offset) is { } target)
        {
            return BuildMembers(compilation, target);
        }

        return BuildNames(compilation, offset);
    }

    /// <summary>
    /// カーソルの直前が <c>.</c> なら、その左側の式を返す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>左側の式。<c>.</c> の直後でなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 打ちかけの <c>IN.</c> は構文としては壊れているので、
    /// 構文木ではなく元のテキストを見る。
    /// </remarks>
    private static MemberAccessExpressionSyntax? FindMemberTarget(
        ShaderCompilation compilation,
        int offset)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is MemberAccessExpressionSyntax access
                    && compilation.IsWrittenHere(access)
                    && access.Span.IntersectsWith(offset))
                {
                    return access;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// メンバーの候補を挙げる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="access">対象のメンバー参照。</param>
    /// <returns>候補。</returns>
    /// <remarks>
    /// 構造体ならメンバーを、数値型ならスウィズルの成分を出す。
    /// 型が分からなければ何も出さない。誤った候補を出すより空のほうがよい。
    /// </remarks>
    private static ImmutableArray<CompletionItem> BuildMembers(
        ShaderCompilation compilation,
        MemberAccessExpressionSyntax access)
    {
        AnalyzedProgram? program = compilation.Programs.FirstOrDefault();

        if (program is null)
        {
            return [];
        }

        ExpressionTypeBinder binder = new(compilation, program);

        if (binder.GetEvaluatorFor(access).Evaluate(access.Target) is not { } typeName)
        {
            return [];
        }

        if (FindStruct(compilation, typeName) is { } structure)
        {
            return BuildStructMembers(compilation, structure);
        }

        return binder.TypeSystem.TryDescribeNumeric(typeName, out ShaderNumericShape shape)
               && shape.Kind is ShaderNumericKind.Scalar or ShaderNumericKind.Vector
            ? BuildSwizzles(binder.TypeSystem, shape.Columns)
            : [];
    }

    /// <summary>構造体のメンバーを候補にする。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="structure">対象の構造体。</param>
    /// <returns>候補。</returns>
    /// <remarks>
    /// <b>条件付きのメンバーには、その条件を添える。</b>
    /// 断らずに並べると、別の構成で壊れるコードを書かせることになる。
    /// </remarks>
    private static ImmutableArray<CompletionItem> BuildStructMembers(
        ShaderCompilation compilation,
        StructDeclarationSyntax structure)
    {
        ConditionMap conditions = compilation.GetConditionMap();
        ImmutableArray<CompletionItem>.Builder items = ImmutableArray.CreateBuilder<CompletionItem>();

        foreach (VariableDeclarationSyntax field in structure.Fields)
        {
            SymbolCondition condition = conditions.GetCondition(field);

            string detail = condition.IsAlways
                ? field.Type.Name
                : $"{field.Type.Name} — #if {condition} のときだけ";

            foreach (VariableDeclaratorSyntax variable in field.Variables)
            {
                items.Add(new CompletionItem(variable.Name, KindField, detail));
            }
        }

        return items.ToImmutable();
    }

    /// <summary>スウィズルの成分を候補にする。</summary>
    /// <param name="types">成分の名前の組を尋ねる先。</param>
    /// <param name="components">元の型の成分数。</param>
    /// <returns>候補。</returns>
    private static ImmutableArray<CompletionItem> BuildSwizzles(ShaderTypeSystem types, int components)
    {
        ImmutableArray<CompletionItem>.Builder items = ImmutableArray.CreateBuilder<CompletionItem>();

        foreach (string set in types.ComponentNameSets)
        {
            for (int i = 0; i < components && i < set.Length; i++)
            {
                items.Add(new CompletionItem(set[i].ToString(), KindField, "成分"));
            }
        }

        return items.ToImmutable();
    }

    /// <summary>
    /// その位置で書ける名前を挙げる。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>候補。</returns>
    private static ImmutableArray<CompletionItem> BuildNames(ShaderCompilation compilation, int offset)
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        void Add(string label, int kind, string? detail)
        {
            // 先に入れたほうを残す。近い範囲のものを先に入れる。
            _ = items.TryAdd(label, new CompletionItem(label, kind, detail));
        }

        // Slang の import したモジュールのファイル。その関数と型も、このファイルから名前で使える。
        HashSet<string> modules = new(
            compilation.Programs.SelectMany(p => p.Tree.PreprocessResult.Includes)
                .Where(i => i.Directive == "import" && i.ResolvedFilePath is not null)
                .Select(i => i.ResolvedFilePath!),
            StringComparer.OrdinalIgnoreCase);

        bool Visible(ShaderSyntaxNode node)
            => compilation.IsWrittenHere(node)
               || (modules.Count > 0 && node.FirstToken is { } first && modules.Contains(first.Source.FilePath));

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // カーソルを囲む関数の中の名前が最も近い。
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is FunctionDeclarationSyntax function
                    && compilation.IsWrittenHere(function)
                    && function.Span.IntersectsWith(offset))
                {
                    AddLocals(function, Add);
                }
            }

            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                switch (declaration)
                {
                    case FunctionDeclarationSyntax function when Visible(function):
                        Add(function.Name, KindFunction, DescribeSignature(function));
                        break;

                    case StructDeclarationSyntax structure when Visible(structure):
                        Add(structure.Name, KindStruct, "struct");

                        // 構造体に続けて宣言した変数 (struct Light { … } light;、GLSL の uniform UBO { … } ubo;)。
                        foreach (VariableDeclaratorSyntax declarator in structure.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>())
                        {
                            Add(declarator.Name, KindVariable, structure.Name);
                        }

                        break;

                    default:
                        break;
                }
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (UniformSymbol uniform in program.Uniforms)
            {
                Add(uniform.Name, KindVariable, uniform.TypeName);
            }
        }

        // 組み込みは、ファイルの言語のバージョンとステージで使えるものだけを出す (GLSL の gl_FragCoord はフラグメントシェーダーだけ)。
        ShaderTypeSystem types = compilation.Programs.IsEmpty
            ? compilation.TypeSystem
            : compilation.TypeSystem.ForTree(compilation.Programs[0].Tree);

        foreach (string name in types.FunctionNames)
        {
            // Godot の表は値の作成 (vec3(…)) も関数として持つ。型は型として出す (WGSL の vec3・array も同じ)。
            if (types is GodotTypeSystem or WgslTypeSystem && types.IsBuiltInTypeName(name))
            {
                continue;
            }

            Add(name, KindFunction, "組み込み関数");
        }

        if (types is GodotTypeSystem godot)
        {
            // 組み込みの変数は、カーソルを囲む関数で使えるものだけを出す (fragment の中で vertex だけの INSTANCE_ID を出さない)。
            string? function = compilation.Programs
                .SelectMany(p => p.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>())
                .FirstOrDefault(f => compilation.IsWrittenHere(f) && f.Span.IntersectsWith(offset))?.Name;

            foreach (GodotBuiltinVariable variable in godot.GetAvailableBuiltinVariables(function).OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                Add(variable.Name, KindVariable, $"{variable.Type} ({DescribeGodotBuiltin(variable)})");
            }

            foreach (string name in Godot.Parsing.GodotKeywords.BuiltInTypes)
            {
                Add(name, KindKeyword, "型");
            }
        }
        else if (types is WgslTypeSystem wgsl)
        {
            foreach ((string name, string type) in wgsl.BuiltinValues)
            {
                Add(name, KindVariable, $"{type} (組み込みの値)");
            }

            foreach (string name in wgsl.CompletionTypeNames)
            {
                Add(name, KindKeyword, "型");
            }
        }
        else if (types is GlslTypeSystem glsl)
        {
            foreach (string name in glsl.Builtins.VariableNames)
            {
                Add(name, KindVariable, $"{glsl.Builtins.GetVariable(name)?.Type} (組み込み変数)");
            }

            foreach (string name in GlslKeywords.BuiltInTypes)
            {
                Add(name, KindKeyword, "型");
            }
        }
        else
        {
            foreach (string name in HlslKeywords.BuiltInTypes)
            {
                Add(name, KindKeyword, "型");
            }

            foreach (string name in HlslKeywords.ObjectTypes)
            {
                Add(name, KindKeyword, "型");
            }

            // Slang の標準ライブラリの型 (CoopVec・Optional・Tuple など)。__ で始まる内部の型は出さない。
            if (types is SlangTypeSystem slang)
            {
                foreach (string name in slang.StandardLibraryTypeNames.Where(n => !n.StartsWith("__", StringComparison.Ordinal)))
                {
                    Add(name, KindKeyword, "型");
                }
            }
        }

        return [.. items.Values.OrderBy(i => i.Label, StringComparer.Ordinal)];
    }

    /// <summary>Godot の描画のモードかステンシルのモードを候補にする。選択肢のあるモードは「名前_選択肢」(<c>blend_mix</c>) で出す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="stencil">ステンシルのモードか。</param>
    /// <returns>候補。シェーダーの種類が分からなければ (<c>.gdshaderinc</c>)、どの種類のものも出す。</returns>
    private static ImmutableArray<CompletionItem> BuildGodotModes(ShaderCompilation compilation, bool stencil)
    {
        string? shaderType = compilation.Programs.IsEmpty ? null : (compilation.Programs[0].Tree.Language as Godot.GodotLanguageOptions)?.ShaderType;
        IEnumerable<string> types = shaderType is null ? GodotBuiltins.Instance.ShaderTypes : [shaderType];
        string detail = stencil ? "ステンシルのモード" : "描画のモード";

        return
        [
            .. types
                .SelectMany(t => stencil ? GodotBuiltins.Instance.GetStencilModes(t) : GodotBuiltins.Instance.GetRenderModes(t))
                .SelectMany(m => m.Options.IsEmpty ? [m.Name] : m.Options.Select(o => $"{m.Name}_{o}"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(name => new CompletionItem(name, KindKeyword, detail)),
        ];
    }

    /// <summary>Godot の組み込みの変数の種類を短く表す。</summary>
    /// <param name="variable">組み込みの変数。</param>
    /// <returns>「組み込みの定数」「組み込み変数、読み取り専用」など。</returns>
    internal static string DescribeGodotBuiltin(GodotBuiltinVariable variable)
        => variable.Function == "constants"
            ? "組み込みの定数"
            : variable.IsConstant ? "組み込み変数、読み取り専用" : "組み込み変数";

    /// <summary>関数の中で宣言された名前を候補に足す。</summary>
    /// <param name="function">対象の関数。</param>
    /// <param name="add">候補を足す手続き。</param>
    private static void AddLocals(
        FunctionDeclarationSyntax function,
        Action<string, int, string?> add)
    {
        foreach (SyntaxNode node in function.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParameterSyntax parameter:
                    add(parameter.Name, KindVariable, $"{parameter.Type.Name} (仮引数)");
                    break;

                case VariableDeclarationSyntax declaration:
                    foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                    {
                        add(variable.Name, KindVariable, declaration.Type.Name);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>関数の形を 1 行で表す。</summary>
    /// <param name="function">対象の関数。</param>
    /// <returns>画面に出す文字列。</returns>
    internal static string DescribeSignature(FunctionDeclarationSyntax function)
        => $"{function.ReturnType.Name} {function.Name}("
           + string.Join(", ", function.ParameterList.Select(p => $"{p.Type.Name} {p.Name}"))
           + ")";

    /// <summary>名前から、このシェーダーが書いた構造体を引く。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">構造体の名前。</param>
    /// <returns>見つかった構造体。無ければ <see langword="null"/>。</returns>
    private static StructDeclarationSyntax? FindStruct(ShaderCompilation compilation, string name)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (StructDeclarationSyntax structure in program.Structs)
            {
                if (structure.Name == name && compilation.IsWrittenHere(structure))
                {
                    return structure;
                }
            }
        }

        return null;
    }
}

using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics.Symbols;

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
                return BuildDirectives();

            case CompletionPlace.PragmaName:
                return BuildPragmas();

            case CompletionPlace.EntryPoint:
                return BuildEntryPoints(compilation);

            case CompletionPlace.Condition:
                return BuildConditionNames(compilation, includeDefined: true);

            case CompletionPlace.DefinedName:
                return BuildConditionNames(compilation, includeDefined: false);

            case CompletionPlace.MacroName:
                return BuildMacroNames(compilation);

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

        return HlslTypeClassifier.TryDescribeNumeric(typeName, out HlslNumericShape shape)
               && shape.Kind is HlslNumericKind.Scalar or HlslNumericKind.Vector
            ? BuildSwizzles(shape.Columns)
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
    /// <param name="components">元の型の成分数。</param>
    /// <returns>候補。</returns>
    private static ImmutableArray<CompletionItem> BuildSwizzles(int components)
    {
        ImmutableArray<CompletionItem>.Builder items = ImmutableArray.CreateBuilder<CompletionItem>();

        foreach (string set in (string[])["xyzw", "rgba"])
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

            foreach (HlslDeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                switch (declaration)
                {
                    case FunctionDeclarationSyntax function when compilation.IsWrittenHere(function):
                        Add(function.Name, KindFunction, DescribeSignature(function));
                        break;

                    case StructDeclarationSyntax structure when compilation.IsWrittenHere(structure):
                        Add(structure.Name, KindStruct, "struct");
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

        foreach (string name in HlslIntrinsics.FunctionNames)
        {
            Add(name, KindFunction, "組み込み関数");
        }

        foreach (string name in HlslKeywords.BuiltInTypes)
        {
            Add(name, KindKeyword, "型");
        }

        foreach (string name in HlslKeywords.ObjectTypes)
        {
            Add(name, KindKeyword, "型");
        }

        return [.. items.Values.OrderBy(i => i.Label, StringComparer.Ordinal)];
    }

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

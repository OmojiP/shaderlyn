using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 定義の探索。
/// </summary>
internal static partial class DefinitionBuilder
{
    /// <summary>
    /// <c>Properties</c> のプロパティ名から、HLSL 側の宣言を探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかった定義。プロパティ名の上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// プロパティと uniform は別の言語で別々に書かれており、
    /// 対応が取れているかを目で追うのが最も面倒な部分である。
    /// </remarks>
    private static DefinitionTarget? FindShaderLabProperty(ShaderCompilation compilation, int offset)
    {
        foreach (PropertySymbol property in compilation.Properties)
        {
            Core.Diagnostics.Location name = property.NameLocation;

            if (offset < name.Span.Start || offset > name.Span.End)
            {
                continue;
            }

            return compilation.TryGetUniform(property.Name, out UniformSymbol? uniform)
                   && uniform.GetLocation() is { } location
                ? ToTarget(location)
                : null;
        }

        return null;
    }

    /// <summary>
    /// 関数の中の宣言を探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">探す名前のトークン。</param>
    /// <returns>見つかった定義。無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>名前を使っている位置を囲む関数の中だけを見る。</b>
    /// 別の関数の同名の変数へ飛ばすと、読み手を誤った場所へ連れて行くことになる。
    /// </remarks>
    private static DefinitionTarget? FindLocal(ShaderCompilation compilation, HlslSyntaxToken name)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is not FunctionDeclarationSyntax function
                    || !compilation.IsWrittenHere(function)
                    || name.Span.Start < function.Span.Start
                    || name.Span.End > function.Span.End)
                {
                    continue;
                }

                if (FindDeclarationIn(compilation, function, name.Text) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>関数の中から、指定した名前の宣言を探す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="function">対象の関数。</param>
    /// <param name="name">探す名前。</param>
    /// <returns>見つかった定義。無い場合は <see langword="null"/>。</returns>
    private static DefinitionTarget? FindDeclarationIn(
        ShaderCompilation compilation,
        FunctionDeclarationSyntax function,
        string name)
    {
        foreach (SyntaxNode node in function.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParameterSyntax parameter
                    when parameter.Name == name && compilation.IsWrittenHere(parameter):
                    return ToTarget(parameter.NameToken);

                case VariableDeclarationSyntax declaration when compilation.IsWrittenHere(declaration):
                    foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                    {
                        if (variable.Name == name)
                        {
                            return ToTarget(variable.NameToken);
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>uniform の宣言を探す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">探す名前のトークン。</param>
    /// <returns>見つかった定義。無い場合は <see langword="null"/>。</returns>
    private static DefinitionTarget? FindUniform(ShaderCompilation compilation, HlslSyntaxToken name)
        => compilation.TryGetUniform(name.Text, out UniformSymbol? uniform)
           && uniform.GetLocation() is { } location
            ? ToTarget(location)
            : null;

    /// <summary>
    /// <c>#include</c> に書かれたパスから、取り込んだファイルを探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかったファイル。パスの上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>書かれたパスへは飛べない。</b>
    /// <c>Packages/com.unity.render-pipelines.universal/...</c> は実在するフォルダーではなく、
    /// 実体は <c>Library/PackageCache</c> の下にある。
    /// 解決後のパスを使わなければ、開けないタブが出るだけになる。
    /// </para>
    /// <para>
    /// このファイルに書かれた <c>#include</c> だけを見る。
    /// ヘッダの中の <c>#include</c> は、カーソルの下には無い。
    /// </para>
    /// </remarks>
    private static DefinitionTarget? FindInclude(ShaderCompilation compilation, int offset)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (IncludeReference include in program.Tree.PreprocessResult.Includes)
            {
                if (include.ResolvedFilePath is not { } resolved
                    || !string.Equals(
                        include.Location.FilePath, compilation.Text.FilePath, StringComparison.Ordinal)
                    || offset < include.Location.Span.Start
                    || offset > include.Location.Span.End)
                {
                    continue;
                }

                // 取り込み先の先頭を指す。どの行かは分からないので、ファイルを開くところまで。
                return new DefinitionTarget(resolved, default);
            }
        }

        return null;
    }

    /// <summary>
    /// 関数または構造体の宣言をすべて探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">探す名前。</param>
    /// <returns>見つかった定義。</returns>
    /// <remarks>
    /// <para>
    /// 取り込んだヘッダの中も対象にする。
    /// URP の関数へ飛べないのでは、飛べる先がほとんど無くなる。
    /// </para>
    /// <para>
    /// <b>候補が複数あるなら、すべて返す。</b>
    /// <c>#ifdef</c> で切り替わる同名の関数は、シンボルごとに別の定義である。
    /// 1 つに決め打つと、利用者が読みたいほうへ飛べないことがある。
    /// エディタは複数の候補を受け取ると、選ばせる画面を出す。
    /// シンボルバリアント (<see cref="ShaderCompilation.SymbolVariants"/>) も探すのはこのためである。
    /// </para>
    /// </remarks>
    private static List<DefinitionTarget> FindFunctionsAndStructs(ShaderCompilation compilation, string name)
    {
        List<DefinitionTarget> definitions = [];
        List<DefinitionTarget> prototypes = [];

        foreach (AnalyzedProgram program in
                 compilation.Programs.Concat(compilation.SymbolVariants))
        {
            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case FunctionDeclarationSyntax function when function.Name == name:
                        Add(function.Body is null ? prototypes : definitions, ToTarget(function.NameToken));
                        break;

                    case StructDeclarationSyntax { NameToken: { } structName } when structName.Text == name:
                        Add(definitions, ToTarget(structName));
                        break;

                    default:
                        break;
                }
            }
        }

        // 中身のある定義を優先する。プロトタイプ宣言へ飛ばしても中身が読めない。
        // 定義が 1 つも無いときだけ、宣言でも無いよりはましとして返す。
        return definitions.Count > 0 ? definitions : prototypes;
    }

    /// <summary>マクロの定義を探す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">探す名前。</param>
    /// <returns>見つかった定義。無い場合は <see langword="null"/>。</returns>
    private static DefinitionTarget? FindMacro(ShaderCompilation compilation, string name)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (program.Tree.PreprocessResult.Macros.TryGetValue(name, out MacroDefinition? macro))
            {
                return ToTarget(macro.NameToken);
            }
        }

        return null;
    }

    /// <summary>
    /// HLSL の名前から、対応する <c>Properties</c> の宣言を探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">探す名前。</param>
    /// <returns>見つかった定義。無い場合は <see langword="null"/>。</returns>
    private static DefinitionTarget? FindProperty(ShaderCompilation compilation, string name)
    {
        foreach (PropertySymbol property in compilation.Properties)
        {
            if (property.Name == name)
            {
                return ToTarget(property.NameLocation);
            }
        }

        return null;
    }

    /// <summary>
    /// 構造体のフィールドの宣言を探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>見つかった定義。フィールド名の上に無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>IN.uv</c> のような書き方はシェーダーの至るところに現れる。
    /// 名前だけで探すと別の場所の同名の宣言へ飛ばすことになるので、
    /// 左側の型を求めてから、その構造体の中を探す。
    /// </remarks>
    private static DefinitionTarget? FindStructField(ShaderCompilation compilation, int offset)
    {
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ExpressionTypeBinder binder = new(compilation, program);

            foreach (SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                if (node is not MemberAccessExpressionSyntax member
                    || member.NameToken.Span.Length == 0
                    || offset < member.NameToken.Span.Start
                    || offset > member.NameToken.Span.End
                    || !compilation.IsWrittenHere(member))
                {
                    continue;
                }

                if (binder.GetEvaluatorFor(member).Evaluate(member.Target) is not { } targetType)
                {
                    continue;
                }

                if (FindField(program, targetType, member.Name) is { } field)
                {
                    return field;
                }
            }
        }

        return null;
    }

    /// <summary>構造体の中からフィールドを探す。</summary>
    /// <param name="program">対象のコードブロック。</param>
    /// <param name="structName">構造体の名前。</param>
    /// <param name="fieldName">フィールドの名前。</param>
    /// <returns>見つかった定義。無い場合は <see langword="null"/>。</returns>
    private static DefinitionTarget? FindField(AnalyzedProgram program, string structName, string fieldName)
    {
        foreach (StructDeclarationSyntax structure in program.Structs)
        {
            if (structure.Name != structName)
            {
                continue;
            }

            foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
            {
                foreach (VariableDeclaratorSyntax variable in member.Variables)
                {
                    if (variable.Name == fieldName)
                    {
                        return ToTarget(variable.NameToken);
                    }
                }
            }
        }

        return null;
    }
}

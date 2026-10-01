using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Rules;

/// <summary>
/// 構造体の中でセマンティクスが重複していないかを検査する (HL0210)。
/// </summary>
/// <remarks>
/// <b>検査するのは、そのシェーダー自身が書いた構造体だけである。</b>
/// Unity のヘッダで定義された構造体を指摘しても直しようがない。
/// </remarks>
internal sealed class DuplicateSemanticAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>
    /// セマンティクスではない修飾。
    /// </summary>
    /// <remarks>
    /// <c>: register(t0)</c> や <c>: packoffset(c0)</c> は構文上セマンティクスと同じ形をしているが、
    /// 意味が違うので重複の検査から外す。
    /// これらは引数を伴うので、引数の有無で見分けられる。
    /// </remarks>
    private static readonly FrozenSet<string> NonSemanticAnnotations =
        new[] { "register", "packoffset" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.DuplicateSemantic];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じ構造体が複数の Pass で解析されるため、重ねて報告しないようにする。
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (StructDeclarationSyntax declaration in program.Structs)
            {
                AnalyzeStruct(context, compilation, declaration, reported);
            }
        }
    }

    /// <summary>構造体 1 つ分を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">検査する構造体。</param>
    /// <param name="reported">報告済みの組み合わせ。</param>
    private static void AnalyzeStruct(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        StructDeclarationSyntax declaration,
        HashSet<string> reported)
    {
        // 対象は利用者が書いた構造体 (このシェーダーと、利用者が書いて取り込んだヘッダ) に限る。
        // Unity のヘッダの構造体は数百に及ぶため、先に絞り込む。
        if (declaration.GetLocation() is not { } declarationLocation
            || !compilation.IsUserFile(declarationLocation.FilePath))
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (VariableDeclarationSyntax field in declaration.Fields)
        {
            foreach (VariableDeclaratorSyntax variable in field.Variables)
            {
                foreach (SemanticSyntax semantic in variable.Semantics)
                {
                    if (semantic.HasArguments
                        || NonSemanticAnnotations.Contains(semantic.Name)
                        || semantic.NameToken.IsMissing)
                    {
                        continue;
                    }

                    if (seen.Add(semantic.Name))
                    {
                        continue;
                    }

                    ReportDuplicate(context, compilation, declaration, semantic, reported);
                }
            }
        }
    }

    /// <summary>重複を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">対象の構造体。</param>
    /// <param name="semantic">重複しているセマンティクス。</param>
    /// <param name="reported">報告済みの組み合わせ。</param>
    private static void ReportDuplicate(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        StructDeclarationSyntax declaration,
        SemanticSyntax semantic,
        HashSet<string> reported)
    {
        if (semantic.GetLocation() is not { } location)
        {
            return;
        }

        // 同じ構造体が複数の Pass で解析されるので、同じ位置を重ねて報告しない。
        if (!reported.Add($"{location.Span.Start}:{semantic.Name}"))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.DuplicateSemantic, location, semantic.Name, declaration.Name));
    }
}

/// <summary>
/// <c>#pragma</c> が指すエントリポイントの存在を検査する (HL0301 / HL0302)。
/// </summary>
/// <remarks>
/// <para>
/// エントリポイント名のスペルミスは、そのシェーダーが丸ごとコンパイルできなくなる誤りである。
/// Unity はシェーダーを必要になった時点でコンパイルするため、
/// 該当のマテリアルを画面に出すまで気づけないことがある。
/// </para>
/// <para>
/// <b>依存関係が完全に解決できている場合にのみ検査する。</b>
/// 取り込めなかったヘッダにエントリポイントが定義されている可能性がある以上、
/// 「見つからない」ことを根拠にはできない。
/// </para>
/// </remarks>
internal sealed class EntryPointAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>
    /// 関数名を引数に取る <c>#pragma</c>。
    /// </summary>
    /// <remarks>
    /// <c>surface</c> はサーフェスシェーダーの記述関数、
    /// <c>kernel</c> はコンピュートシェーダーの入口を指す。
    /// いずれも第 1 引数が関数名である点は共通している。
    /// </remarks>
    private static readonly FrozenSet<string> EntryPointPragmas = new[]
    {
        "vertex", "fragment", "geometry", "hull", "domain", "surface", "kernel",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// これがあればシェーダーステージが指定されているとみなす <c>#pragma</c>。
    /// </summary>
    /// <remarks>
    /// <c>raytracing</c> や <c>require</c> のように、
    /// 頂点・フラグメント以外の形でステージが決まる書き方も受け入れる。
    /// 知らない書き方を「指定が無い」と報告すると誤検出になる。
    /// </remarks>
    private static readonly FrozenSet<string> StagePragmas = new[]
    {
        "vertex", "fragment", "surface", "kernel", "raytracing", "geometry", "hull", "domain",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        HlslRuleDescriptors.EntryPointNotFound,
        HlslRuleDescriptors.MissingShaderStage,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasCompleteDependencies)
        {
            return;
        }

        // 取り込まれる前提の断片 (.hlsl など) は、ステージの #pragma を持たないのが普通である。
        // ステージは取り込む側の .shader が決める。
        bool requireStage = !ShaderSourceKinds.IsIncludeFragment(compilation.Text.FilePath);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            AnalyzeProgram(context, compilation, program, requireStage);
        }
    }

    /// <summary>コードブロック 1 つ分を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">検査するコードブロック。</param>
    /// <param name="requireStage">ステージの指定が無いことを報告するかどうか。</param>
    private static void AnalyzeProgram(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        bool requireStage)
    {
        bool hasStage = false;

        foreach (PragmaDirective pragma in program.Tree.Pragmas)
        {
            if (StagePragmas.Contains(pragma.Name))
            {
                hasStage = true;
            }

            if (!EntryPointPragmas.Contains(pragma.Name))
            {
                continue;
            }

            if (pragma.Arguments.FirstOrDefault() is not { } entryPoint
                || entryPoint.Kind != HlslSyntaxKind.IdentifierToken)
            {
                continue;
            }

            // コンピュートシェーダーはカーネルごとに別々にコンパイルされ、
            // ブロックもカーネルごとに 1 つある。
            // このブロックはこのカーネルの構成でしか展開していないので、
            // 他のカーネルの入口がここに見えていなくても当たり前である。
            if (program.KernelName is { } kernel
                && string.Equals(pragma.Name, "kernel", StringComparison.Ordinal)
                && !string.Equals(entryPoint.Text, kernel, StringComparison.Ordinal))
            {
                continue;
            }

            CheckEntryPoint(context, compilation, program, pragma.Name, entryPoint);
        }

        if (requireStage && !hasStage)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                HlslRuleDescriptors.MissingShaderStage, program.GetBlockLocation()));
        }
    }

    /// <summary>エントリポイントの関数が存在するかを確かめる。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">検査するコードブロック。</param>
    /// <param name="pragmaName"><c>#pragma</c> の名前。</param>
    /// <param name="entryPoint">エントリポイントの名前を表すトークン。</param>
    private static void CheckEntryPoint(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        string pragmaName,
        HlslSyntaxToken entryPoint)
    {
        // 入口の関数が無い構成を求める。
        // 両方の分岐を並べた木では、#ifdef _B の中の関数も木にある。名前があるだけで「ある」とすると、
        // !_B の構成で入口が無いことを見落とす。
        if (MissingEntryPoint(compilation, program, entryPoint.Text, out bool nowhere) is not { } missing)
        {
            return;
        }

        // どの構成でも解析しなかった領域に名前があれば、そこで定義されているかもしれない。
        // 見るのはこのブロックの読み飛ばしだけである。別の Pass に同じ名前があっても、この Pass の入口ではない。
        if (compilation.AppearsOutsideAnalyzedCode(entryPoint.Text, program))
        {
            return;
        }

        string suffix = nowhere || missing.IsAlways
            ? string.Empty
            : $" ({missing} のとき)";

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.EntryPointNotFound,
            entryPoint.GetLocation(),
            pragmaName,
            entryPoint.Text,
            suffix));
    }

    /// <summary>入口の関数が無い構成を求める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">既定の構成の木。</param>
    /// <param name="name">入口の関数の名前。</param>
    /// <param name="nowhere">どの木にも無かったかどうか。</param>
    /// <returns>無い構成の条件。どの構成にもあるか、判断できなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 木ごとに、その木が表す構成 (<see cref="ShaderCompilation.GetTreeConfiguration"/>) の中で無い部分を求めて足し合わせる。
    /// 木が表す構成は木どうしで重ならないので、別の木にあることを差し引く必要は無い。
    /// どの木も解析していない組み合わせについては何も言わない。
    /// </para>
    /// <para>
    /// 既定の木では、関数の先頭 (戻り値の型) の出現条件でその木の中のどこにあるかを見る。
    /// 出現条件は #if の領域が内側の指令で切れた単位で付き、関数全体のように複数の切れ目にまたがるノードには付かない。
    /// バリアントの木では、木にあればその木の構成全体にあるとする。
    /// ノードの出現条件は既定の木を基準にしたもので、バリアントの木の中では合わないことがある。
    /// </para>
    /// </remarks>
    private static SymbolCondition? MissingEntryPoint(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        string name,
        out bool nowhere)
    {
        ConditionMap map = compilation.GetConditionMap();
        SymbolCondition missing = SymbolCondition.Never;
        nowhere = true;

        foreach (AnalyzedProgram tree in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            if (tree.BlockKey != program.BlockKey)
            {
                continue;
            }

            bool isDefault = tree.EnabledSymbols.IsDefaultOrEmpty;
            SymbolCondition present = SymbolCondition.Never;

            foreach (HlslDeclarationSyntax declaration in tree.Tree.Root.Declarations)
            {
                // プロトタイプ宣言だけでも「ある」とする。定義がどこにあるかはリンクの問題である。
                if (declaration is not FunctionDeclarationSyntax { IsMethodDefinition: false } function
                    || !string.Equals(function.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                nowhere = false;
                SymbolCondition condition = isDefault
                    ? compilation.GetConditionMap().GetCondition(function.ReturnType)
                    : SymbolCondition.Always;

                if (condition.IsUnknown)
                {
                    return null;
                }

                present = present.Or(condition);

                if (present.IsAlways)
                {
                    break;
                }
            }

            if (present.IsAlways)
            {
                continue;
            }

            SymbolCondition absent = compilation.GetTreeConfiguration(tree).And(present.Negate());

            if (!absent.IsUnknown && map.IsPossible(absent))
            {
                missing = missing.Or(absent);
            }
        }

        return missing.IsNever ? null : map.Simplify(missing);
    }
}

using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// シェーダーのシンボルの宣言と使用が噛み合っているかを検査する (HL0330 / HL0331 / HL0332)。
/// </summary>
/// <remarks>
/// <para>
/// <b>シンボルは宣言して初めて切り替えられる。</b>
/// <c>#ifdef _NORMALMAP</c> と書いても、
/// <c>#pragma shader_feature _NORMALMAP</c> がどこにも無ければ、
/// その分岐は決してコンパイルに含まれない。
/// Unity はこれをエラーにも警告にもしないため、
/// 「機能を書いたのに効かない」という形でしか現れない。
/// </para>
/// <para>
/// 宣言と使用が噛み合っているかは、
/// シンボルの組み合わせを展開しなくても調べられる。
/// </para>
/// </remarks>
internal sealed class ShaderSymbolAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslDescriptors.UndeclaredSymbol, HlslDescriptors.UnusedSymbol, HlslDescriptors.NeverTrueCondition];

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>宣言も使用もパスをまたぐ。</b>
    /// <c>HLSLINCLUDE</c> に書いた <c>#pragma</c> や <c>#ifdef</c> は
    /// SubShader のすべてのパスに入る。
    /// パスごとに判定すると、同じ 1 行をパスの数だけ報告してしまい、
    /// 「あるパスでだけ使われているシンボル」を未使用と誤って言う。
    /// </para>
    /// <para>
    /// そこで宣言と使用をシェーダー全体で集めてから報告する。
    /// シンボルバリアント (<see cref="ShaderCompilation.SymbolVariants"/>) も数える。
    /// 別のシンボルを有効にして初めて現れる条件も、書かれている条件である。
    /// </para>
    /// </remarks>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ImmutableArray<AnalyzedProgram> programs = [.. compilation.Programs, .. compilation.SymbolVariants];

        HashSet<string> declared = new(StringComparer.Ordinal);
        HashSet<string> referenced = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in programs)
        {
            declared.UnionWith(ShaderSymbols.CollectDeclared(program.Tree.PreprocessResult.Pragmas));

            foreach (HlslSyntaxToken token in program.Tree.PreprocessResult.ConditionalIdentifiers)
            {
                referenced.Add(token.Text);
            }
        }

        // #ifdef だけでは足りない。#pragma dynamic_branch で宣言したシンボルは
        // バリアントを作らず、if (_HDR_OVERLAY) のように実行時の分岐として書かれる。
        // 条件だけを数えると「宣言したのに使っていない」と誤って言うことになる。
        referenced.UnionWith(ShaderSymbols.CollectRuntimeReferences(compilation.CodeTokens));

        // 取り込まれる前提の断片 (.hlsl など) では、シンボルは取り込む側の .shader が宣言する。
        // 断片だけを見て「宣言されていない」とは言えない。
        bool canJudgeDeclarations = !ShaderSourceKinds.IsIncludeFragment(compilation.Text.FilePath);

        // 同じ 1 行が複数のパスに入る。報告は書かれた場所ごとに 1 回でよい。
        HashSet<TextSpan> reported = [];

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            if (canJudgeDeclarations)
            {
                ReportUndeclared(context, compilation, result, declared, reported);
            }

            ReportUnused(context, compilation, result, referenced, reported);
            ReportNeverTrue(context, compilation, result, reported);
        }
    }

    /// <summary>
    /// どの構成でも成り立たない条件を報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="result">プリプロセスの結果。</param>
    /// <param name="reported">すでに報告した場所。</param>
    /// <remarks>
    /// 判定はプリプロセッサが行い、このファイルに書かれた条件だけを記録している
    /// (<see cref="PreprocessResult.NeverTrueConditions"/>)。
    /// 同じ指令は既定の構成とバリアントの両方で読まれるので、位置ごとに 1 回だけ報告する。
    /// </remarks>
    private static void ReportNeverTrue(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        PreprocessResult result,
        HashSet<TextSpan> reported)
    {
        foreach (NeverTrueCondition condition in result.NeverTrueConditions)
        {
            if (!compilation.IsWrittenHere(condition.Directive) || !reported.Add(condition.Directive.Span))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HlslDescriptors.NeverTrueCondition, condition.Directive.GetLocation(), condition.Text));
        }
    }

    /// <summary>
    /// 宣言されていないシンボルを報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="result">プリプロセスの結果。</param>
    /// <param name="declared">シェーダー全体で宣言されているシンボル。</param>
    /// <param name="reported">すでに報告した場所。</param>
    /// <remarks>
    /// <para>
    /// <b>このファイルに書かれた条件だけを見る。</b>
    /// 取り込んだヘッダの条件は、そのヘッダの都合で書かれたものであり、
    /// 利用者に直しようがない。
    /// </para>
    /// <para>
    /// <b>マテリアルのシンボルの形をしたものだけを報告する。</b>
    /// 条件にはプラットフォームの判定 (<c>SHADER_API_D3D11</c>) や
    /// コンパイラが定義する名前も現れる。それらは宣言されていなくて当然である。
    /// シンボルはアンダースコアで始まる大文字の名前という慣習があり、
    /// この形に限れば取り違えはほとんど起きない。
    /// </para>
    /// </remarks>
    private static void ReportUndeclared(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        PreprocessResult result,
        HashSet<string> declared,
        HashSet<TextSpan> reported)
    {
        foreach (HlslSyntaxToken token in result.ConditionalIdentifiers)
        {
            if (declared.Contains(token.Text)
                || !IsMaterialSymbol(token.Text)
                || result.Macros.ContainsKey(token.Text)
                || !compilation.IsWrittenHere(token)
                || !reported.Add(token.Span))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HlslDescriptors.UndeclaredSymbol, token.GetLocation(), token.Text));
        }
    }

    /// <summary>
    /// 宣言したのに使われていないシンボルを報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="result">プリプロセスの結果。</param>
    /// <param name="referenced">シェーダー全体で参照されたシンボル。</param>
    /// <param name="reported">すでに報告した場所。</param>
    /// <remarks>
    /// 宣言そのものがこのファイルに書かれている場合だけを見る。
    /// ヘッダが宣言したシンボルを使うかどうかは、このシェーダーの都合ではない。
    /// </remarks>
    private static void ReportUnused(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        PreprocessResult result,
        HashSet<string> referenced,
        HashSet<TextSpan> reported)
    {
        foreach (HlslSyntaxToken declaration in ShaderSymbols.EnumerateDeclared(result.Pragmas))
        {
            if (referenced.Contains(declaration.Text)
                || !compilation.IsWrittenHere(declaration)
                || !reported.Add(declaration.Span))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                HlslDescriptors.UnusedSymbol, declaration.GetLocation(), declaration.Text));
        }
    }

    /// <summary>
    /// 名前がマテリアルのシンボルの形をしているかを判定する。
    /// </summary>
    /// <param name="name">調べる名前。</param>
    /// <returns>シンボルの形であれば <see langword="true"/>。</returns>
    /// <remarks>
    /// アンダースコアで始まり、小文字を含まない。
    /// Unity のシンボルはこの慣習に従っており、
    /// プラットフォーム判定や内部の名前と取り違えずに済む。
    /// </remarks>
    private static bool IsMaterialSymbol(string name)
        => name.Length > 1
           && name[0] == '_'
           && !name.Any(char.IsAsciiLetterLower);
}

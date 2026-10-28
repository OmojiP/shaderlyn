using System.Collections.Immutable;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// シェーダーのシンボルの宣言と使用が噛み合っているかを検査する (CF0201 / CF0202 / CF0203)。
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
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderDescriptors.UndeclaredSymbol, ShaderDescriptors.UnusedSymbol, ShaderDescriptors.NeverTrueCondition];

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
            declared.UnionWith(program.Variants.Symbols);

            // 値を持つシンボルは原子 (QUALITY==2) で宣言されるが、条件には名前 (QUALITY) で書く。
            declared.UnionWith(program.Variants.ValueSymbols.Keys);

            foreach (ShaderSyntaxToken token in program.Tree.PreprocessResult.ConditionalIdentifiers)
            {
                referenced.Add(token.Text);
            }
        }

        // #ifdef だけでは足りない。#pragma dynamic_branch で宣言したシンボルは
        // バリアントを作らず、if (_HDR_OVERLAY) のように実行時の分岐として書かれる。
        // 条件だけを数えると「宣言したのに使っていない」と誤って言うことになる。
        referenced.UnionWith(CollectRuntimeReferences(compilation.CodeTokens));

        // 取り込まれる前提の断片 (.hlsl など) では、シンボルは取り込む側の .shader が宣言する。
        // 断片だけを見て「宣言されていない」とは言えない。
        bool canJudgeDeclarations = !ShaderSourceKinds.IsIncludeFragment(compilation.Text.FilePath);

        // 同じ 1 行が複数のパスに入る。報告は書かれた場所ごとに 1 回でよい。
        HashSet<(string File, TextSpan Span)> reported = [];

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            if (canJudgeDeclarations)
            {
                ReportUndeclared(context, compilation, result, declared, reported);
            }

            ReportUnused(context, compilation, program.Variants, referenced, reported);
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
        HashSet<(string File, TextSpan Span)> reported)
    {
        foreach (NeverTrueCondition condition in result.NeverTrueConditions)
        {
            if (!compilation.IsWrittenHere(condition.Directive) || !reported.Add((condition.Directive.Source.FilePath, condition.Directive.Span)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ShaderDescriptors.NeverTrueCondition, condition.Directive.GetLocation(), condition.Text));
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
    /// <b>環境がシンボルらしいと言う名前だけを報告する (<see cref="Semantics.Environments.IShaderEnvironment.LooksLikeVariantSymbol"/>)。</b>
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
        HashSet<(string File, TextSpan Span)> reported)
    {
        foreach (ShaderSyntaxToken token in result.ConditionalIdentifiers)
        {
            if (declared.Contains(token.Text)
                || !compilation.Environment.LooksLikeVariantSymbol(token.Text)
                || result.Macros.ContainsKey(token.Text)
                || !compilation.IsWrittenHere(token)
                || !reported.Add((token.Source.FilePath, token.Span)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ShaderDescriptors.UndeclaredSymbol, token.GetLocation(), token.Text));
        }
    }

    /// <summary>
    /// 宣言したのに使われていないシンボルを報告する。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="variants">木から読んだ構成の空間。</param>
    /// <param name="referenced">シェーダー全体で参照されたシンボル。</param>
    /// <param name="reported">すでに報告した場所。</param>
    /// <remarks>
    /// 宣言そのものがこのファイルに書かれている場合だけを見る。
    /// ヘッダが宣言したシンボルを使うかどうかは、このシェーダーの都合ではない。
    /// </remarks>
    private static void ReportUnused(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        VariantSpace variants,
        HashSet<string> referenced,
        HashSet<(string File, TextSpan Span)> reported)
    {
        // 名前が書かれていない宣言 (組み込みの宣言が作るもの) は、使うかどうかを利用者が選んでいない。
        foreach (VariantDeclaration declaration in variants.Declarations)
        {
            // 値を持つシンボルは、値ごとの原子 (QUALITY==2) として宣言されている。条件に書くのは名前 (QUALITY) である。
            string name = VariantValues.TryParseAtom(declaration.Name, out string? valueName, out _) ? valueName : declaration.Name;

            if (declaration.IsImplicit
                || declaration.Location is not { } location
                || referenced.Contains(name)
                || SwitchesCompiler(compilation, name)
                || !compilation.IsWrittenHere(location)
                || !reported.Add((location.FilePath, location.Span)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ShaderDescriptors.UnusedSymbol, location, name));
        }
    }

    /// <summary>
    /// そのシンボルが、有効なときにコンパイラや HLSL のバージョンを切り替えるかを判定する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">シンボル。</param>
    /// <returns>切り替えるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// Unity の <c>UNITY_DEVICE_SUPPORTS_NATIVE_16BIT</c> などは、条件に書かなくても、有効な構成を DXC でコンパイルさせる。
    /// 宣言するだけで意味があるので、「条件のどこにも現れない」とは言わない。
    /// </remarks>
    private static bool SwitchesCompiler(ShaderCompilation compilation, string name)
        => compilation.Programs.Any(p => p.Tree.Hlsl().CompileTargets.Any(t => t.WhenAnySymbol.Contains(name)));

    /// <summary>
    /// コードの中で名前として現れた識別子を集める。
    /// </summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <returns>現れた識別子の名前。</returns>
    /// <remarks>
    /// <para>
    /// <b><c>#ifdef</c> だけを見ていては、使われているかを判定できない。</b>
    /// <c>#pragma dynamic_branch</c> で宣言したシンボルはバリアントを作らず、
    /// <c>if (_HDR_OVERLAY)</c> のように実行時の分岐として書かれる。
    /// 条件だけを数えると「宣言したのに使っていない」と誤って言うことになる。
    /// </para>
    /// <para>
    /// プリプロセッサの行は数えない。
    /// <c>#pragma</c> の行にはシンボルの名前そのものが並んでおり、
    /// それを使用と数えると、どのシンボルも使われていることになる。
    /// </para>
    /// </remarks>
    private static HashSet<string> CollectRuntimeReferences(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        HashSet<string> referenced = new(StringComparer.Ordinal);

        for (int i = 0; i < tokens.Length; i++)
        {
            // プリプロセッサの行は飛ばす。
            // #pragma の行にはシンボルの名前そのものが並んでおり、
            // それを使用と数えると、どのシンボルも「使われている」ことになる。
            if (tokens[i].Kind == ShaderSyntaxKind.HashToken && tokens[i].IsAtLineStart)
            {
                while (i + 1 < tokens.Length && !tokens[i + 1].IsAtLineStart) { i++; }
                continue;
            }

            if (tokens[i].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                referenced.Add(tokens[i].Text);
            }
        }

        return referenced;
    }
}

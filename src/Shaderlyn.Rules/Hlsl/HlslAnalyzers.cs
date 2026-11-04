using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Rules;

/// <summary>
/// 構造体の中でセマンティクスが重複していないかを検査する (HL0210)。
/// </summary>
/// <remarks>
/// <b>検査するのは、そのシェーダー自身が書いた構造体だけである。</b>
/// Unity のヘッダで定義された構造体を指摘しても直しようがない。
/// <para>
/// DXC でコンパイルするエントリポイントの入出力に届く構造体 (メンバーの構造体を含む) なら、エラーにする。
/// DXC は検証の誤り (「Semantic 'TEXCOORD' overlap」) でコンパイルを止める。FXC は警告 X4711 で通し、
/// どちらのコンパイラも、入出力に使わない構造体の重複は何も言わない (FXC / DXC 1.8 と Unity 6.3 で確かめた)。
/// </para>
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
        HashSet<string> dxcEntryStructs = CollectDxcEntryStructs(compilation);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (StructDeclarationSyntax declaration in program.Structs)
            {
                AnalyzeStruct(context, compilation, declaration, dxcEntryStructs.Contains(declaration.Name), reported);
            }
        }
    }

    /// <summary>DXC でコンパイルするエントリポイントの入出力に届く構造体の名前を集める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>構造体の名前。メンバーの構造体も含む。</returns>
    /// <remarks>
    /// コンパイル先が分からない環境 (汎用の HLSL) は、木のコンパイラで決める (HL0304 と同じ)。
    /// </remarks>
    private static HashSet<string> CollectDxcEntryStructs(ShaderCompilation compilation)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            Dictionary<string, StructDeclarationSyntax> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax declaration in program.Structs)
            {
                structs.TryAdd(declaration.Name, declaration);
            }

            foreach (ShaderEntryPoint entryPoint in program.EntryPoints)
            {
                foreach (FunctionDeclarationSyntax function in EntryPointAnalyzer.EnumerateFunctions(program.Tree.Root.Declarations))
                {
                    if (function.IsMethodDefinition || function.Name != entryPoint.Name || !CompiledByDxc(compilation, program, function))
                    {
                        continue;
                    }

                    Add(function.ReturnType.Name);

                    foreach (ParameterSyntax parameter in function.ParameterList)
                    {
                        Add(parameter.Type.Name);
                    }
                }
            }

            void Add(string name)
            {
                if (!structs.TryGetValue(name, out StructDeclarationSyntax? declaration) || !names.Add(name))
                {
                    return;
                }

                foreach (VariableDeclarationSyntax field in declaration.Fields)
                {
                    Add(field.Type.Name);
                }
            }
        }

        return names;
    }

    /// <summary>関数を DXC でコンパイルするかを決める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">関数が属する木。</param>
    /// <param name="function">エントリポイントの関数。</param>
    /// <returns>届くコンパイル先に DXC があれば true。</returns>
    private static bool CompiledByDxc(ShaderCompilation compilation, AnalyzedProgram program, FunctionDeclarationSyntax function)
    {
        ImmutableArray<HlslCompileTarget> targets = [.. CompileTargets.Reaching(compilation, function, program)];

        return targets.IsEmpty
            ? program.Tree.Hlsl().Dialects.Any(d => d.Compiler == HlslCompiler.Dxc)
            : targets.Any(t => t.Dialect.Compiler == HlslCompiler.Dxc);
    }

    /// <summary>構造体 1 つ分を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">検査する構造体。</param>
    /// <param name="error">エラーにするか (DXC でコンパイルするエントリポイントの入出力に届く)。</param>
    /// <param name="reported">報告済みの組み合わせ。</param>
    private static void AnalyzeStruct(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        StructDeclarationSyntax declaration,
        bool error,
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

                    ReportDuplicate(context, declaration, semantic, error, reported);
                }
            }
        }
    }

    /// <summary>重複を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="declaration">対象の構造体。</param>
    /// <param name="semantic">重複しているセマンティクス。</param>
    /// <param name="error">エラーにするか。</param>
    /// <param name="reported">報告済みの組み合わせ。</param>
    private static void ReportDuplicate(
        SyntaxTreeAnalysisContext context,
        StructDeclarationSyntax declaration,
        SemanticSyntax semantic,
        bool error,
        HashSet<string> reported)
    {
        if (semantic.GetLocation() is not { } location)
        {
            return;
        }

        // 同じ構造体が複数の Pass で解析されるので、同じ位置を重ねて報告しない。
        if (!reported.Add($"{location.FilePath}:{location.Span.Start}:{semantic.Name}"))
        {
            return;
        }

        // 重要度は既定として差し替える。設定ファイルで重要度を書けば、そちらが勝つ。
        Diagnostic diagnostic = Diagnostic.Create(HlslRuleDescriptors.DuplicateSemantic, location, semantic.Name, declaration.Name);
        context.ReportDiagnostic(error ? diagnostic.WithSeverity(DiagnosticSeverity.Error) : diagnostic);
    }
}

using System.Collections.Immutable;
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
/// 環境が指定したエントリポイントの関数があるかを検査する (CF0301)。
/// </summary>
/// <remarks>
/// <para>
/// エントリポイント名のスペルミスは、そのシェーダーが丸ごとコンパイルできなくなる誤りである。
/// Unity はシェーダーを必要になった時点でコンパイルするため、
/// 該当のマテリアルを画面に出すまで気づけないことがある。
/// </para>
/// <para>
/// エントリポイントは環境が読む (<see cref="IShaderEnvironment.ReadEntryPoints"/>)。
/// Unity なら <c>#pragma vertex</c> などに書いた名前、設定でコンパイル単位を与える環境なら設定に書いた名前である。
/// </para>
/// <para>
/// <b>依存関係が完全に解決できている場合にのみ検査する。</b>
/// 取り込めなかったヘッダにエントリポイントが定義されている可能性がある以上、
/// 「見つからない」ことを根拠にはできない。
/// </para>
/// </remarks>
internal sealed class EntryPointAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.EntryPointNotFound, HlslRuleDescriptors.EntryPointInConstantBuffer, HlslRuleDescriptors.EntryPointInConstantBufferWithDxc];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasCompleteDependencies)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (ShaderEntryPoint entryPoint in program.EntryPoints)
            {
                CheckEntryPoint(context, compilation, program, entryPoint);
                CheckConstantBufferEntryPoint(context, compilation, program, entryPoint);
            }
        }
    }

    /// <summary>
    /// エントリポイントの関数が cbuffer の中にあれば報告する (HL0303 / HL0304)。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">検査するコードブロック。</param>
    /// <param name="entryPoint">エントリポイント。</param>
    /// <remarks>
    /// <para>
    /// FXC はコンパイルできる。DXC はエントリポイントを見つけられない (Unity の DXC は落ちる)。Unity 6.6 と、スタンドアロンの FXC / DXC 1.8 で確かめた。
    /// cbuffer の中で呼ぶだけの関数は、FXC も DXC もコンパイルできるので報告しない。
    /// </para>
    /// <para>
    /// 関数に届くコンパイル先に DXC があればエラー (HL0304)、FXC だけなら警告 (HL0303) にする。
    /// コンパイル先が分からない環境 (汎用の HLSL) は、木のコンパイラと HLSL のバージョンで決める。
    /// </para>
    /// </remarks>
    private static void CheckConstantBufferEntryPoint(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        ShaderEntryPoint entryPoint)
    {
        foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
        {
            if (declaration is not ConstantBufferDeclarationSyntax buffer
                || EnumerateFunctions(buffer.Members).FirstOrDefault(f => !f.IsMethodDefinition && f.Name == entryPoint.Name) is not { } function)
            {
                continue;
            }

            ImmutableArray<HlslCompileTarget> targets = [.. CompileTargets.Reaching(compilation, function, program)];
            string bufferName = buffer.Name.Length > 0 ? buffer.Name : "(名前なし)";
            Location location = entryPoint.Location ?? program.GetBlockLocation();

            if (targets.IsEmpty)
            {
                bool dxc = program.Tree.Hlsl().Dialects.Any(d => d.Compiler == HlslCompiler.Dxc);

                context.ReportDiagnostic(Diagnostic.Create(
                    dxc ? HlslRuleDescriptors.EntryPointInConstantBufferWithDxc : HlslRuleDescriptors.EntryPointInConstantBuffer,
                    location,
                    entryPoint.Origin,
                    entryPoint.Name,
                    bufferName,
                    dxc ? "DXC" : "FXC"));
                return;
            }

            ImmutableArray<HlslCompileTarget> dxcTargets = [.. targets.Where(t => t.Dialect.Compiler == HlslCompiler.Dxc)];

            context.ReportDiagnostic(Diagnostic.Create(
                dxcTargets.IsEmpty ? HlslRuleDescriptors.EntryPointInConstantBuffer : HlslRuleDescriptors.EntryPointInConstantBufferWithDxc,
                location,
                entryPoint.Origin,
                entryPoint.Name,
                bufferName,
                string.Join("、", (dxcTargets.IsEmpty ? targets : dxcTargets).Select(t => t.Subject).Distinct(StringComparer.Ordinal))));
            return;
        }
    }

    /// <summary>関数の宣言を列挙する。cbuffer の中に書いた関数も含む。</summary>
    /// <param name="declarations">宣言の並び。</param>
    /// <returns>関数の宣言。</returns>
    private static IEnumerable<FunctionDeclarationSyntax> EnumerateFunctions(IEnumerable<DeclarationSyntax> declarations)
    {
        foreach (DeclarationSyntax declaration in declarations)
        {
            if (declaration is FunctionDeclarationSyntax function)
            {
                yield return function;
            }
            else if (declaration is ConstantBufferDeclarationSyntax buffer)
            {
                foreach (FunctionDeclarationSyntax member in EnumerateFunctions(buffer.Members))
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>エントリポイントの関数が存在するかを確かめる。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">検査するコードブロック。</param>
    /// <param name="entryPoint">エントリポイント。</param>
    private static void CheckEntryPoint(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        ShaderEntryPoint entryPoint)
    {
        // 入口の関数が無い構成を求める。
        // 両方の分岐を並べた木では、#ifdef _B の中の関数も木にある。名前があるだけで「ある」とすると、
        // !_B の構成で入口が無いことを見落とす。
        if (MissingEntryPoint(compilation, program, entryPoint.Name, out bool nowhere) is not { } missing)
        {
            return;
        }

        // どの構成でも解析しなかった領域に名前があれば、そこで定義されているかもしれない。
        // 見るのはこのブロックの読み飛ばしだけである。別の Pass に同じ名前があっても、この Pass の入口ではない。
        if (compilation.AppearsOutsideAnalyzedCode(entryPoint.Name, program))
        {
            return;
        }

        string suffix = nowhere || missing.IsAlways
            ? string.Empty
            : $" ({missing} のとき)";

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.EntryPointNotFound,
            entryPoint.Location ?? program.GetBlockLocation(),
            entryPoint.Origin,
            entryPoint.Name,
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

            foreach (FunctionDeclarationSyntax function in EnumerateFunctions(tree.Tree.Root.Declarations))
            {
                // プロトタイプ宣言だけでも「ある」とする。定義がどこにあるかはリンクの問題である。
                // cbuffer の中の関数も「ある」とする。FXC はエントリポイントにでき、DXC で使えないことは HL0304 で伝える。
                if (function.IsMethodDefinition
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

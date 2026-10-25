using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Semantics;

/// <summary>
/// 識別子が指すグローバル変数の宣言が、どの構成にあるかを求める。
/// </summary>
public sealed partial class ShaderCompilation
{
    /// <summary>グローバル変数の宣言の索引 (<see cref="TryGetIdentifierUndeclaredCondition"/>)。初めて使うときに作る。</summary>
    private GlobalVariableIndex? _globalVariables;

    /// <summary>
    /// 識別子が指すグローバル変数が宣言されていない構成があるかを調べ、あればその構成を返す。
    /// </summary>
    /// <param name="identifier">使っている識別子。</param>
    /// <param name="program">識別子のある木。</param>
    /// <param name="undeclared">識別子を使っていて、宣言が無い構成 (読める形に簡約したもの)。どの構成でもなら <see cref="SymbolCondition.Always"/>。</param>
    /// <returns>
    /// 宣言が無い構成があれば <see langword="true"/>。どの構成にも宣言があるか、判断できなければ <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>対象はグローバル変数 (定数バッファのメンバーを含む) である。</b>
    /// その位置で局所変数か仮引数を指す名前、そのコードブロックのどの木でもグローバル変数として宣言されていない名前
    /// (関数や構造体のメンバー、どこにも宣言の無い名前。後者は CF0310 の範囲) には <see langword="false"/> を返す。
    /// </para>
    /// <para>
    /// 次の順で求める。組み込みのルール (CF0315) と同じである。
    /// </para>
    /// <list type="number">
    ///   <item><description>使う側の条件 = その位置の条件 × その木が表す構成 (<see cref="GetTreeConfiguration"/>)</description></item>
    ///   <item><description>宣言がある条件 = その木の、同じ名前のグローバル変数の宣言の条件の和</description></item>
    ///   <item><description>宣言が無い構成 = 使う側の条件 ∧ ¬宣言がある条件</description></item>
    ///   <item><description>ありえない構成と、<c>#error</c> に到達する構成を除く (<see cref="IsPossibleIn"/>)</description></item>
    /// </list>
    /// <para>
    /// どこかで条件を追えなかったときと、どの構成でも解析しなかった領域に名前があるとき
    /// (そこで宣言されているかもしれない) は、<see langword="false"/> を返す。分からないことを根拠に報告しないためである。
    /// 依存関係が解決できていない (<see cref="HasCompleteDependencies"/> が偽) ときも判断しない。
    /// </para>
    /// </remarks>
    public bool TryGetIdentifierUndeclaredCondition(IdentifierExpressionSyntax identifier, AnalyzedProgram program, out SymbolCondition undeclared)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(program);

        undeclared = SymbolCondition.Unknown;

        if (!HasCompleteDependencies || UnanalyzedIdentifiers.Contains(identifier.Name))
        {
            return false;
        }

        ConditionMap condition = GetConditionMap();
        _globalVariables ??= GlobalVariableIndex.Collect(this, condition);

        if (!_globalVariables.IsDeclaredInBlock(program.BlockKey, identifier.Name))
        {
            return false;
        }

        // その位置で局所変数を指しているなら対象ではない。判断できないなら見送る。
        // 別の構成でしか宣言されていない名前は NotFound になるが、それこそが調べる対象である。
        if (GetExpressionTypeBinder(program).ResolveName(identifier).Kind is DeclaredNameKind.Local or DeclaredNameKind.Undecidable)
        {
            return false;
        }

        // その木が表す構成を掛け合わせる (バリアントの木なら、その木を解析した構成も)。
        // 宣言はその木のものと比べる。木が表す構成は木どうしで重ならないので、ほかの木の宣言を足す必要は無い。
        // 木をまたいで足した条件を否定すると、式が膨れ上がる。
        SymbolCondition use = GetEffectiveCondition(identifier, program).And(GetTreeConfiguration(program));
        SymbolCondition declaration = _globalVariables.ConditionIn(program, identifier.Name);

        if (use.IsUnknown || declaration.IsUnknown)
        {
            return false;
        }

        SymbolCondition missing = use.And(declaration.Negate());

        if (missing.IsUnknown || !IsPossibleIn(program, missing))
        {
            return false;
        }

        undeclared = condition.Simplify(missing);
        return true;
    }

    /// <summary>グローバル変数の宣言を、コードブロックごとの名前と、木ごとの「宣言がある条件」に分けて持つ。</summary>
    /// <remarks>
    /// <para>
    /// 木ごとに、その木の宣言 (取り込んだヘッダのものも含む) を数える。既定の木には、バリアントの木から足す宣言も数える。
    /// 同じ名前の宣言が複数の条件の下にあるなら、その和が宣言のある条件である。
    /// </para>
    /// <para>
    /// <b>使う側も宣言の側も、木ごとに比べる (使う側にはその木が表す構成を掛ける)。</b>
    /// 以前は既定の木の宣言とバリアントの木から足す宣言だけを数え、バリアントの木にしか無い宣言は条件を分からないとしていた。
    /// 既定の値で読んだ連なりの宣言 (<c>#if LIGHT_SOURCE_MODE == 0</c> の側) は木の中で条件を持たないので、
    /// ほかの値の構成にも宣言があることになるか、その構成の宣言を「無い」と取り違えていた
    /// (Unreal の CapsuleShadowShaders.usf)。
    /// </para>
    /// </remarks>
    private sealed class GlobalVariableIndex
    {
        private readonly Dictionary<(int Start, string? Kernel), HashSet<string>> _names = [];
        private readonly Dictionary<AnalyzedProgram, Dictionary<string, SymbolCondition>> _byTree = [];

        /// <summary>そのコードブロックのどれかの木で、グローバル変数として宣言されているか。</summary>
        public bool IsDeclaredInBlock((int Start, string? Kernel) block, string name)
            => _names.TryGetValue(block, out HashSet<string>? names) && names.Contains(name);

        /// <summary>その木で宣言がある条件。無ければ <see cref="SymbolCondition.Never"/>、分からなければ <see cref="SymbolCondition.Unknown"/>。</summary>
        public SymbolCondition ConditionIn(AnalyzedProgram program, string name)
            => _byTree.TryGetValue(program, out Dictionary<string, SymbolCondition>? declared)
               && declared.TryGetValue(name, out SymbolCondition present)
                ? present
                : SymbolCondition.Never;

        /// <summary>すべての木の宣言を集める。</summary>
        public static GlobalVariableIndex Collect(ShaderCompilation compilation, ConditionMap condition)
        {
            GlobalVariableIndex index = new();

            foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
            {
                foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables(program.Tree.Root.Declarations))
                {
                    index.Add(program, declaration, condition.GetCondition(declaration));
                }

                foreach (ConditionalNode inserted in condition.GetInsertedChildren(program.Tree.Root))
                {
                    if (inserted.Node is DeclarationSyntax root)
                    {
                        foreach (VariableDeclarationSyntax declaration in EnumerateGlobalVariables([root]))
                        {
                            index.Add(program, declaration, condition.GetCondition(declaration));
                        }
                    }
                }
            }

            return index;
        }

        private void Add(AnalyzedProgram program, VariableDeclarationSyntax declaration, SymbolCondition present)
        {
            if (!_names.TryGetValue(program.BlockKey, out HashSet<string>? blockNames))
            {
                blockNames = new HashSet<string>(StringComparer.Ordinal);
                _names[program.BlockKey] = blockNames;
            }

            if (!_byTree.TryGetValue(program, out Dictionary<string, SymbolCondition>? declared))
            {
                declared = new Dictionary<string, SymbolCondition>(StringComparer.Ordinal);
                _byTree[program] = declared;
            }

            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                blockNames.Add(variable.Name);
                declared[variable.Name] = declared.TryGetValue(variable.Name, out SymbolCondition existing)
                    ? existing.Or(present)
                    : present;
            }
        }

        /// <summary>グローバル変数の宣言を列挙する。定数バッファの中も見る。</summary>
        private static IEnumerable<VariableDeclarationSyntax> EnumerateGlobalVariables(IEnumerable<DeclarationSyntax> declarations)
        {
            foreach (DeclarationSyntax declaration in declarations)
            {
                switch (declaration)
                {
                    case VariableDeclarationSyntax variable:
                        yield return variable;
                        break;

                    case ConstantBufferDeclarationSyntax buffer:
                        foreach (VariableDeclarationSyntax member in EnumerateGlobalVariables(buffer.Members))
                        {
                            yield return member;
                        }

                        break;

                    default:
                        break;
                }
            }
        }
    }
}

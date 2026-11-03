using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 協調行列 (<c>coopmat</c>、<c>fcoopmatNV</c> など) の式を検査する (GL0325 / CF0410)。
/// glslang 16.6.0 の Intermediate.cpp の <c>promoteBinary</c> と <c>addConversion</c>、ParseHelper.cpp の協調行列の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 協調行列の型は、要素の型と型の引数 (スコープ・行の数・列の数・用途) で決まる。型の引数は、書いた式のまま比べる
/// (特殊化の定数の <c>Y</c> と <c>(Y+1)</c> は別の型)。整数のリテラルだけの式は値で比べる。
/// </para>
/// <para>
/// 型の違う協調行列どうしの代入と演算、協調行列でない値の代入 (値の作成を除く)、協調行列と協調行列でない値の <c>+</c>・<c>-</c>・<c>/</c>、
/// 要素の型と違う型のスカラーとの <c>*</c>、NV の協調行列どうしの <c>*</c>、<c>[ ][ ]</c> と成分の選択 (<c>.x</c>)、
/// 値の作成の 2 つ以上の実引数と、スカラーでも協調行列でもない実引数を報告する。
/// </para>
/// <para>
/// 式の型は、名前 (宣言の型)、値の作成、括弧、演算子、代入から求める。分からない式は見ない。
/// </para>
/// </remarks>
internal sealed class GlslCooperativeMatrixAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.CooperativeMatrixUse, HlslRuleDescriptors.ValueNotConvertible];

    /// <summary>協調行列の型。</summary>
    /// <param name="Nv">NV の協調行列か。</param>
    /// <param name="Element">要素の型。</param>
    /// <param name="Parameters">要素の型の後ろの型の引数。</param>
    private sealed record CoopType(bool Nv, string Element, string Parameters)
    {
        public override string ToString() => Nv ? $"協調行列<{Element}, {Parameters}> (NV)" : $"coopmat<{Element}, {Parameters}>";
    }

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Text is "coopmat" or "fcoopmatNV" or "icoopmatNV" or "ucoopmatNV"))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];
        Dictionary<AnalyzedProgram, HashSet<string>> specConstants = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (AssignmentExpressionSyntax or BinaryExpressionSyntax or ElementAccessExpressionSyntax or MemberAccessExpressionSyntax
                or InvocationExpressionSyntax or VariableDeclaratorSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            if (!specConstants.TryGetValue(program, out HashSet<string>? specs))
            {
                specs = specConstants[program] = CollectSpecConstants(program);
            }

            Checker checker = new(requirement, compilation.GetExpressionTypeBinder(program), name => compilation.GetFunctionDeclarations(name, program), specs);
            checker.Check(node);
        }
    }

    /// <summary>特殊化の定数 (layout(constant_id = …) const) の名前。</summary>
    private static HashSet<string> CollectSpecConstants(AnalyzedProgram program)
        => [.. program.Tree.Root.ChildNodes().OfType<VariableDeclarationSyntax>()
            .Where(d => GlslLayoutQualifier.Read(d.ModifierTokens).Any(e => e.Name.Text == "constant_id"))
            .SelectMany(d => d.Variables.Select(v => v.Name))];

    /// <summary>1 つの木の協調行列の式を検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, ExpressionTypeBinder binder, Func<string, ImmutableArray<FunctionDeclarationSyntax>> functions, HashSet<string> specConstants)
    {
        public void Check(ShaderSyntaxNode node)
        {
            switch (node)
            {
                case VariableDeclaratorSyntax { Initializer: { } initializer, Parent: VariableDeclarationSyntax declaration } declarator
                    when !declarator.IsArray && FromType(declaration.Type) is { } target:
                    CheckAssign(declarator.NameToken, target, initializer);
                    break;

                case AssignmentExpressionSyntax assignment when TypeOf(assignment.Left) is { } left:
                    CheckCompound(assignment, left);
                    break;

                case BinaryExpressionSyntax binary:
                    CheckBinary(binary.OperatorToken, binary.Left, binary.Right);
                    break;

                // [ ] は 1 つだけ書ける (glslang の「left of '[' is not of type array, matrix, or vector」)。
                case ElementAccessExpressionSyntax { Target: ElementAccessExpressionSyntax inner } element when TypeOf(inner.Target) is not null:
                    requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, element.OpenBracket, "協調行列の要素は [ ] を 1 つだけ書いて引きます。");
                    break;

                // 成分の選択は書けない (glslang の「cannot apply to a cooperative matrix type」)。
                case MemberAccessExpressionSyntax member when member.Parent is not InvocationExpressionSyntax && TypeOf(member.Target) is not null:
                    requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, member.NameToken, $"協調行列には成分の選択 ('.{member.Name}') を書けません。");
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "coopMatTransposeEXT" or "coopMatTransposeNV" or "coopMatReduceEXT" or "coopMatReduceNV" or "coopMatPerElementEXT" or "coopMatPerElementNV" or "coopMatLoadTensorNV" or "coopMatMulAdd" or "coopMatMulAddNV" } callee } call
                    when functions(callee.Name).IsEmpty:
                    CheckBuiltin(callee, [.. call.ArgumentExpressions]);
                    break;

                case InvocationExpressionSyntax invocation when ConstructedType(invocation) is not null:
                    CheckConstructor(invocation);
                    break;

                // 行列の組み込み関数に協調行列を渡したもの (transpose(m))。協調行列は GLSL の行列ではない。
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "transpose" or "inverse" or "determinant" or "matrixCompMult" or "outerProduct" } callee } call
                    when call.ArgumentExpressions.Any(a => TypeOf(a) is not null):
                    requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, callee.Identifier, $"組み込み関数 '{callee.Name}' には協調行列を渡せません。");
                    break;

                default:
                    break;
            }
        }

        /// <summary>代入と初期値。協調行列には、同じ型の協調行列しか代入できない。</summary>
        private void CheckAssign(ShaderSyntaxToken at, CoopType target, ExpressionSyntax value)
        {
            if (TypeOf(value) is { } source)
            {
                if (source != target)
                {
                    requirement.Report(HlslRuleDescriptors.ValueNotConvertible, at, target.ToString(), source.ToString(), "協調行列は、同じ型の協調行列にしか変換できません。");
                }

                return;
            }

            if (KnownNonCoopType(value) is { } other)
            {
                requirement.Report(HlslRuleDescriptors.ValueNotConvertible, at, target.ToString(), other, "協調行列でない値を、暗黙に協調行列へ変換できません。値の作成 (coopmat<…>(x)) を使ってください。");
            }
        }

        /// <summary>代入の演算子 (<c>=</c>、<c>+=</c> など)。</summary>
        private void CheckCompound(AssignmentExpressionSyntax assignment, CoopType left)
        {
            if (assignment.OperatorToken.Kind != ShaderSyntaxKind.EqualsToken && IsLimited(left))
            {
                ReportLimited(assignment.OperatorToken, left);
                return;
            }

            switch (assignment.OperatorToken.Kind)
            {
                case ShaderSyntaxKind.EqualsToken:
                    CheckAssign(assignment.OperatorToken, left, assignment.Right);
                    break;

                // 型の違う協調行列の += など (glslang は代入の変換の誤りにする)。
                case ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken or ShaderSyntaxKind.SlashEqualsToken or ShaderSyntaxKind.AsteriskEqualsToken
                    when TypeOf(assignment.Right) is { } right && right != left:
                    requirement.Report(HlslRuleDescriptors.ValueNotConvertible, assignment.OperatorToken, left.ToString(), right.ToString(), "協調行列は、同じ型の協調行列にしか変換できません。");
                    break;

                case ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken or ShaderSyntaxKind.SlashEqualsToken
                    when TypeOf(assignment.Right) is null && KnownNonCoopType(assignment.Right) is { } other:
                    requirement.Report(HlslRuleDescriptors.ValueNotConvertible, assignment.OperatorToken, left.ToString(), other, "協調行列の + - / の相手は、同じ型の協調行列でなければなりません。");
                    break;

                case ShaderSyntaxKind.AsteriskEqualsToken:
                    CheckBinary(assignment.OperatorToken, assignment.Left, assignment.Right);
                    break;

                default:
                    break;
            }
        }

        /// <summary>二項演算子。</summary>
        private void CheckBinary(ShaderSyntaxToken op, ExpressionSyntax leftExpression, ExpressionSyntax rightExpression)
        {
            CoopType? left = TypeOf(leftExpression);
            CoopType? right = TypeOf(rightExpression);

            if (left is null && right is null)
            {
                return;
            }

            if ((left ?? right) is { } limited && (IsLimited(limited) || right is not null && IsLimited(right)))
            {
                ReportLimited(op, IsLimited(limited) ? limited : right!);
                return;
            }

            string text = op.Text.TrimEnd('=');

            if (left is not null && right is not null)
            {
                if (left != right)
                {
                    Report($"型の違う協調行列 ({left} と {right}) に演算子 '{text}' は使えません。");
                }
                else if (text == "*" && left.Nv)
                {
                    Report("NV の協調行列どうしに演算子 '*' は使えません。");
                }
                else if (text is not ("+" or "-" or "*" or "/"))
                {
                    Report($"協調行列に演算子 '{text}' は使えません。");
                }

                return;
            }

            CoopType matrix = left ?? right!;
            ExpressionSyntax otherExpression = left is null ? leftExpression : rightExpression;

            if (KnownNonCoopType(otherExpression) is not { } other)
            {
                return;
            }

            if (text != "*")
            {
                Report($"協調行列と協調行列でない値 ({other}) に演算子 '{text}' は使えません。");
            }
            else if (other != matrix.Element)
            {
                Report($"協調行列に掛けるスカラーは、要素と同じ型 ({matrix.Element}) でなければなりません (今は {other})。");
            }

            void Report(string message) => requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, op, message);
        }

        /// <summary>bfloat16_t と 8 ビットの浮動小数の協調行列か。代入のほかの演算子を使えない (glslang の hasLimitedFloatCoopMat)。</summary>
        private static bool IsLimited(CoopType type)
            => type.Element is "bfloat16_t" or "floate5m2_t" or "floate4m3_t";

        private void ReportLimited(ShaderSyntaxToken op, CoopType type)
            => requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, op, $"要素が {type.Element} の協調行列には、代入のほかの演算子 ('{op.Text}') を使えません。");

        /// <summary>
        /// 協調行列の組み込み関数 (glslang の ParseHelper.cpp の coopMatTranspose・coopMatReduce・coopMatPerElement の検査)。
        /// 行と列の数は、どちらも整数のリテラルのときだけ比べる。
        /// </summary>
        private void CheckBuiltin(IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
        {
            ShaderSyntaxToken at = callee.Identifier;
            string name = callee.Name;

            void Report(string message) => requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, at, $"{name}: {message}");

            // coopMatPerElement は仮引数の無い宣言なので、どの実引数でも呼び出せてしまう。
            if (name.StartsWith("coopMatPerElement", StringComparison.Ordinal) && arguments.Length < 3)
            {
                Report("結果の協調行列、入力の協調行列、要素ごとの関数の 3 つの実引数が要ります。");
                return;
            }

            if (name.StartsWith("coopMatMulAdd", StringComparison.Ordinal))
            {
                CheckMulAdd(arguments, Report);
                return;
            }

            if (name == "coopMatLoadTensorNV")
            {
                CheckDecode(arguments, Report);
                return;
            }

            if (arguments.Length < 2 || TypeOf(arguments[0]) is not { } result || TypeOf(arguments[1]) is not { } input)
            {
                return;
            }

            string[] r = result.Parameters.Split(", ");
            string[] s = input.Parameters.Split(", ");

            if (r.Length != s.Length || r.Length < 3)
            {
                return;
            }

            bool hasUse = r.Length == 4;
            const string Accumulator = "gl_MatrixUseAccumulator";

            if (name.StartsWith("coopMatTranspose", StringComparison.Ordinal))
            {
                if (name.EndsWith("EXT", StringComparison.Ordinal) && hasUse)
                {
                    if (s[3] != Accumulator)
                    {
                        Report("変換元の協調行列の用途は Accumulator でなければなりません。");
                    }

                    if (r[3] == Accumulator)
                    {
                        Report("結果の協調行列の用途は Accumulator にできません。");
                    }
                }

                if (ScopesDiffer(r[0], s[0]))
                {
                    Report("結果と変換元の協調行列のスコープは同じでなければなりません。");
                }
                else if (Numbers(r, s) is var (rows, cols, inRows, inCols) && (rows != inCols || cols != inRows))
                {
                    Report("結果の協調行列は、変換元の行と列の数を入れ替えた形でなければなりません。");
                }

                return;
            }

            if (name.StartsWith("coopMatReduce", StringComparison.Ordinal))
            {
                if (arguments.Length >= 3)
                {
                    long? mask = arguments[2] switch
                    {
                        IdentifierExpressionSyntax { Name: var maskName } when maskName.StartsWith("gl_CooperativeMatrixReduce", StringComparison.Ordinal)
                            => maskName.Replace("gl_CooperativeMatrixReduce", string.Empty, StringComparison.Ordinal).TrimEnd('T', 'X', 'E', 'N', 'V') switch
                            {
                                "Row" => 1,
                                "Column" => 2,
                                "RowAndColumn" => 3,
                                "2x2" => 4,
                                _ => null,
                            },
                        _ => new GlslConstantEvaluator(binder).Evaluate(arguments[2])?.Integer,
                    };

                    if (mask is null && !IsConstantName(arguments[2]))
                    {
                        Report("reduceMask (3 つ目の実引数) は定数の式でなければなりません。");
                    }
                    else if (mask is { } value && value is < 1 or > 4)
                    {
                        Report("reduceMask (3 つ目の実引数) は gl_CooperativeMatrixReduce… の値でなければなりません。");
                    }
                    else if (Numbers(r, s) is var (rows, cols, inRows, inCols))
                    {
                        if (mask == 4 && (rows * 2 != inRows || cols * 2 != inCols))
                        {
                            Report("2x2 の縮約の結果の行と列の数は、入力の半分でなければなりません。");
                        }
                        else if (mask == 1 && rows != inRows)
                        {
                            Report("行の縮約の結果の行の数は、入力と同じでなければなりません。");
                        }
                        else if (mask == 2 && cols != inCols)
                        {
                            Report("列の縮約の結果の列の数は、入力と同じでなければなりません。");
                        }
                    }
                }

                if (Canonical(result.Element) != Canonical(input.Element))
                {
                    Report("結果と入力の協調行列の要素の型は同じでなければなりません。");
                }
                else if (ScopesDiffer(r[0], s[0]))
                {
                    Report("結果と入力の協調行列のスコープは同じでなければなりません。");
                }
                else if (hasUse && (r[3] != Accumulator || s[3] != Accumulator))
                {
                    Report("結果と入力の協調行列の用途は Accumulator でなければなりません。");
                }

                if (arguments.Length >= 4 && Callback(arguments[3]) is { } combine)
                {
                    ParameterSyntax[] parameters = [.. combine.ParameterList];
                    CheckConstIn(combine, Report);

                    if (parameters.Any(p => Canonical(p.Type.Name) != Canonical(input.Element)))
                    {
                        Report($"縮約の関数 '{combine.Name}' の仮引数は、協調行列の要素の型 ({input.Element}) でなければなりません。");
                    }
                    else if (Canonical(combine.ReturnType.Name) != Canonical(input.Element))
                    {
                        Report($"縮約の関数 '{combine.Name}' の戻り値は、協調行列の要素の型 ({input.Element}) でなければなりません。");
                    }
                }

                return;
            }

            // coopMatPerElement (result, input, func, 追加の実引数…)。
            if (result != input)
            {
                Report("結果と入力の協調行列の型は同じでなければなりません。");
            }

            if (arguments.Length < 3 || Callback(arguments[2]) is not { } function)
            {
                return;
            }

            ParameterSyntax[] elementParameters = [.. function.ParameterList];
            CheckConstIn(function, Report);

            if (elementParameters.Length != arguments.Length)
            {
                Report($"要素ごとの関数 '{function.Name}' の仮引数の数は、呼び出しの実引数の数 ({arguments.Length}) と同じでなければなりません。");
                return;
            }

            if (elementParameters.Take(2).Any(p => Canonical(p.Type.Name) != "uint"))
            {
                Report($"要素ごとの関数 '{function.Name}' の行と列の仮引数は uint32_t でなければなりません。");
            }

            if (Canonical(elementParameters[2].Type.Name) != Canonical(input.Element))
            {
                Report($"要素ごとの関数 '{function.Name}' の 3 つ目の仮引数は、協調行列の要素の型 ({input.Element}) でなければなりません。");
            }

            if (Canonical(function.ReturnType.Name) != Canonical(result.Element))
            {
                Report($"要素ごとの関数 '{function.Name}' の戻り値は、協調行列の要素の型 ({result.Element}) でなければなりません。");
            }

            for (int i = 3; i < arguments.Length; i++)
            {
                string parameter = Canonical(elementParameters[i].Type.Name);

                if (TypeOf(arguments[i]) is { } extra)
                {
                    if (extra != input)
                    {
                        Report("追加の協調行列は、入力の協調行列と同じ型でなければなりません。");
                    }
                    else if (parameter != Canonical(input.Element))
                    {
                        Report($"協調行列に当たる仮引数は、協調行列の要素の型 ({input.Element}) でなければなりません。");
                    }
                }
                else if (KnownNonCoopType(arguments[i]) is { } other && Canonical(other) != parameter)
                {
                    Report($"{i + 1} つ目の実引数の型 ({other}) は、要素ごとの関数の仮引数の型と同じでなければなりません。");
                }
            }
        }

        /// <summary>スコープが違うと分かるか。組み込みの定数 (gl_Scope…) か整数のリテラルどうしだけを比べる (特殊化の定数は値が分からない)。</summary>
        private static bool ScopesDiffer(string left, string right)
            => left != right && IsKnownScope(left) && IsKnownScope(right);

        private static bool IsKnownScope(string scope) => scope.StartsWith("gl_", StringComparison.Ordinal) || scope.All(char.IsAsciiDigit);

        /// <summary>組み込みの定数か、const の変数の名前か (値の分からない定数)。</summary>
        private bool IsConstantName(ExpressionSyntax expression)
            => expression is IdentifierExpressionSyntax identifier
               && (identifier.Name.StartsWith("gl_", StringComparison.Ordinal)
                   || binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }] && declaration.HasModifier("const"));

        /// <summary>
        /// coopMatLoadTensorNV (mat, buf, off, t [, view] [, decodeFunc [, decodeVectorFunc]]) の復号の関数。
        /// 仮引数は 3 つ (バッファの参照、uint32_t の配列 2 つ) で、decodeFunc は要素の型を、decodeVectorFunc は要素の型の 2・4・8 成分のベクトルを返す。
        /// </summary>
        private void CheckDecode(ExpressionSyntax[] arguments, Action<string> report)
        {
            if (arguments.Length < 5 || TypeOf(arguments[0]) is not { } matrix)
            {
                return;
            }

            FunctionDeclarationSyntax[] decoders = [.. arguments.Skip(4).Select(Callback).OfType<FunctionDeclarationSyntax>()];

            for (int i = 0; i < decoders.Length && i < 2; i++)
            {
                FunctionDeclarationSyntax decoder = decoders[i];
                ParameterSyntax[] parameters = [.. decoder.ParameterList];
                CheckConstIn(decoder, report);

                if (parameters.Length != 3)
                {
                    report($"復号の関数 '{decoder.Name}' の仮引数は 3 つでなければなりません。");
                    continue;
                }

                if (Shaderlyn.Glsl.Parsing.GlslKeywords.IsBuiltInType(parameters[0].Type.Name))
                {
                    report($"復号の関数 '{decoder.Name}' の 1 つ目の仮引数は、バッファの参照の型でなければなりません。");
                }

                if (parameters.Skip(1).Any(p => Canonical(p.Type.Name) != "uint" || !p.IsArray))
                {
                    report($"復号の関数 '{decoder.Name}' の座標の仮引数は、uint32_t の配列でなければなりません。");
                }

                string returnType = decoder.ReturnType.Name;

                if (i == 0)
                {
                    if (Canonical(returnType) != Canonical(matrix.Element))
                    {
                        report($"復号の関数 '{decoder.Name}' の戻り値は、協調行列の要素の型 ({matrix.Element}) のスカラーでなければなりません。");
                    }
                }
                else if (VectorOf(returnType) is not var (component, size))
                {
                    if (Shaderlyn.Glsl.Parsing.GlslKeywords.IsBuiltInType(returnType) && !returnType.Contains("vector", StringComparison.Ordinal))
                    {
                        report($"ベクトルの復号の関数 '{decoder.Name}' の戻り値は、協調行列の要素の型のベクトルでなければなりません。");
                    }
                }
                else if (Canonical(component) != Canonical(matrix.Element))
                {
                    report($"ベクトルの復号の関数 '{decoder.Name}' の戻り値の成分の型は、協調行列の要素の型 ({matrix.Element}) でなければなりません。");
                }
                else if (size is not (2 or 4 or 8))
                {
                    report($"ベクトルの復号の関数 '{decoder.Name}' の戻り値の成分の数は 2・4・8 のどれかでなければなりません。");
                }
            }
        }

        /// <summary>
        /// coopMatMulAdd (A, B, C) の形と用途 (glslang の ParseHelper.cpp の MulAdd の検査)。A の列と B の行、A の行と C の行、B の列と C の列が同じで、
        /// 用途は A・B・Accumulator の順でなければならない。
        /// </summary>
        private void CheckMulAdd(ExpressionSyntax[] arguments, Action<string> report)
        {
            if (arguments.Length != 3 || TypeOf(arguments[0]) is not { } a || TypeOf(arguments[1]) is not { } b || TypeOf(arguments[2]) is not { } c)
            {
                return;
            }

            string[] x = a.Parameters.Split(", ");
            string[] y = b.Parameters.Split(", ");
            string[] z = c.Parameters.Split(", ");

            // NV の協調行列は [スコープ, 行, 列]、KHR は [スコープ, 行, 列, 用途]。
            if (x.Length < 3 || y.Length != x.Length || z.Length != x.Length)
            {
                return;
            }

            if (SameSize(x[2], y[1]) == false)
            {
                report($"掛ける協調行列の形が合いません (A の列 {x[2]} と B の行 {y[1]})。");
            }
            else if (SameSize(x[1], z[1]) == false || SameSize(y[2], z[2]) == false)
            {
                report($"足す協調行列の形が合いません (積 {x[1]} x {y[2]} と C の {z[1]} x {z[2]})。");
            }
            else if (x.Length == 4)
            {
                (string Use, string Expected, string Name)[] uses = [(x[3], "gl_MatrixUseA", "A"), (y[3], "gl_MatrixUseB", "B"), (z[3], "gl_MatrixUseAccumulator", "C")];

                if (uses.FirstOrDefault(u => u.Use.StartsWith("gl_MatrixUse", StringComparison.Ordinal) && u.Use != u.Expected) is { Name: not null } wrong)
                {
                    report($"{wrong.Name} の協調行列の用途は {wrong.Expected} でなければなりません ({wrong.Use} です)。");
                }
            }
        }

        /// <summary>
        /// 型の引数の大きさが同じか。整数のリテラルどうしは値を、特殊化の定数は名前を比べる (glslang は特殊化の定数とリテラルを違う大きさとみなす)。
        /// 分からなければ <see langword="null"/>。
        /// </summary>
        private bool? SameSize(string left, string right)
        {
            if (left == right)
            {
                return true;
            }

            bool leftLiteral = long.TryParse(left, out long leftValue);
            bool rightLiteral = long.TryParse(right, out long rightValue);

            if (leftLiteral && rightLiteral)
            {
                return leftValue == rightValue;
            }

            return (leftLiteral || specConstants.Contains(left)) && (rightLiteral || specConstants.Contains(right)) ? false : null;
        }

        /// <summary>組み込みのベクトルの型 (vec2、f16vec3 など) の成分の型と数。</summary>
        private static (string Component, int Size)? VectorOf(string type)
        {
            int at = type.IndexOf("vec", StringComparison.Ordinal);

            if (at < 0 || at + 4 != type.Length || type[^1] is not (>= '2' and <= '4'))
            {
                return null;
            }

            string? component = type[..at] switch
            {
                "" or "f32" => "float",
                "f16" => "float16_t",
                "d" or "f64" => "double",
                "i" or "i32" => "int",
                "u" or "u32" => "uint",
                "i8" => "int8_t",
                "i16" => "int16_t",
                "i64" => "int64_t",
                "u8" => "uint8_t",
                "u16" => "uint16_t",
                "u64" => "uint64_t",
                "b" => "bool",
                _ => null,
            };

            return component is null ? null : (component, type[^1] - '0');
        }

        /// <summary>関数を渡す実引数 (利用者の関数の名前) の定義。</summary>
        private FunctionDeclarationSyntax? Callback(ExpressionSyntax argument)
            => argument is IdentifierExpressionSyntax identifier && functions(identifier.Name) is [var only, ..] ? only : null;

        /// <summary>渡す関数の仮引数は、どれも const in (const だけでもよい) でなければならない。</summary>
        private static void CheckConstIn(FunctionDeclarationSyntax function, Action<string> report)
        {
            if (function.ParameterList.Any(p => !p.ModifierTokens.Concat(p.Type.ModifierTokens).Any(t => t.Text == "const")
                                                || p.ModifierTokens.Concat(p.Type.ModifierTokens).Any(t => t.Text is "out" or "inout")))
            {
                report($"渡す関数 '{function.Name}' の仮引数には、どれも const in を書かなければなりません。");
            }
        }

        /// <summary>行と列の数 (結果の行・列、入力の行・列)。どれかが整数のリテラルでなければ <see langword="null"/>。</summary>
        private static (long Rows, long Cols, long InRows, long InCols)? Numbers(string[] result, string[] input)
            => long.TryParse(result[1], out long rows) && long.TryParse(result[2], out long cols)
               && long.TryParse(input[1], out long inRows) && long.TryParse(input[2], out long inCols)
                ? (rows, cols, inRows, inCols)
                : null;

        private static string Canonical(string type) => type switch
        {
            "float32_t" => "float",
            "float64_t" => "double",
            "int32_t" => "int",
            "uint32_t" => "uint",
            _ => type,
        };

        /// <summary>値の作成。実引数は 1 つで、スカラーか協調行列。</summary>
        private void CheckConstructor(InvocationExpressionSyntax invocation)
        {
            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
            ShaderSyntaxToken at = invocation.OpenParen;

            if (arguments.Length == 1 && TypeOf(arguments[0]) is { } source && ConstructedType(invocation) is { } constructed
                && source.Nv == constructed.Nv && ShapeDiffers(source, constructed, at))
            {
                requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, at, $"協調行列の値の作成では、要素の型だけを変えられます。スコープ・行の数・列の数・用途は同じでなければなりません ({source} から {constructed})。");
            }
            else if (arguments.Length > 1)
            {
                requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, at, "協調行列の値の作成には、実引数を 1 つだけ書けます。");
            }
            else if (arguments.Length == 1 && TypeOf(arguments[0]) is null && KnownNonCoopType(arguments[0]) is { } other
                     && (!binder.TypeSystem.TryDescribeNumeric(other, out ShaderNumericShape shape) || shape.Kind != ShaderNumericKind.Scalar))
            {
                requirement.Report(GlslRuleDescriptors.CooperativeMatrixUse, at, $"協調行列の値の作成の実引数は、スカラーか協調行列でなければなりません (今は {other})。");
            }
        }

        /// <summary>
        /// 値の作成で変えられない形が違うか。スコープと、値の分かる行と列の数を比べる。
        /// 用途は、GL_EXT_cooperative_matrix_maintenance1 なら Accumulator と A / B の間で、GL_NV_cooperative_matrix2 なら Accumulator から A / B へ変えられる。
        /// </summary>
        private bool ShapeDiffers(CoopType source, CoopType target, ShaderSyntaxToken at)
        {
            string[] from = source.Parameters.Split(", ");
            string[] to = target.Parameters.Split(", ");

            if (from.Length != to.Length || from[0] != to[0])
            {
                return from.Length == to.Length;
            }

            for (int i = 1; i <= 2 && i < from.Length; i++)
            {
                if (from[i].All(char.IsAsciiDigit) && to[i].All(char.IsAsciiDigit) && from[i] != to[i])
                {
                    return true;
                }
            }

            if (from.Length != 4 || from[3] == to[3])
            {
                return false;
            }

            bool fromAccumulator = from[3] == "gl_MatrixUseAccumulator";
            bool toAccumulator = to[3] == "gl_MatrixUseAccumulator";

            return requirement.IsOn("GL_EXT_cooperative_matrix_maintenance1", at) ? !(fromAccumulator ^ toAccumulator)
                : requirement.IsOn("GL_NV_cooperative_matrix2", at) ? !(fromAccumulator && !toAccumulator)
                : true;
        }

        /// <summary>式の協調行列の型。協調行列でないか分からなければ <see langword="null"/>。</summary>
        private CoopType? TypeOf(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => TypeOf(parenthesized.Expression),
            IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ } candidate] ? FromDeclarator(candidate.Declarator) : null,
            // coopMatMulAdd の結果は C の型。
            InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "coopMatMulAdd" or "coopMatMulAddNV" } callee } call
                when functions(callee.Name).IsEmpty && call.ArgumentExpressions.Count() == 3 => TypeOf(call.ArgumentExpressions.Last()),
            InvocationExpressionSyntax invocation => ConstructedType(invocation),
            AssignmentExpressionSyntax assignment => TypeOf(assignment.Left),
            BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken } binary
                => TypeOf(binary.Left) is { } left ? (TypeOf(binary.Right) is not { } right || right == left ? left : null) : TypeOf(binary.Right),
            _ => null,
        };

        /// <summary>協調行列でない式の型 (スカラー・ベクトル・行列など)。分からないか協調行列なら <see langword="null"/>。</summary>
        private string? KnownNonCoopType(ExpressionSyntax expression)
            => TypeOf(expression) is null && ContainsNoCoop(expression) && binder.GetTypesByConfiguration(expression) is [{ Type: { } name }]
               && binder.TypeSystem.TryDescribeNumeric(name, out _)
                ? name
                : null;

        /// <summary>式の中に、協調行列の名前と値の作成が無いか (型を求められない協調行列の式を、協調行列でないと取り違えないため)。</summary>
        private static bool ContainsNoCoop(ExpressionSyntax expression)
            => !expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => i.Target is IdentifierExpressionSyntax { Name: "coopmat" or "fcoopmatNV" or "icoopmatNV" or "ucoopmatNV" })
               && !expression.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>().Any(i => i.Name.StartsWith("coopMat", StringComparison.Ordinal));

        /// <summary>値の作成 (<c>coopmat&lt;…&gt;(x)</c>) の型。</summary>
        private static CoopType? ConstructedType(InvocationExpressionSyntax invocation)
            => invocation.Target is IdentifierExpressionSyntax { Name: var name } && invocation.OpenAngle is not null
                ? FromParts(name, invocation.TypeArgumentTokens)
                : null;

        /// <summary>宣言の型。</summary>
        private static CoopType? FromDeclarator(ShaderSyntaxNode declarator) => declarator switch
        {
            VariableDeclaratorSyntax { IsArray: false, Parent: VariableDeclarationSyntax declaration } => FromType(declaration.Type),
            ParameterSyntax { IsArray: false } parameter => FromType(parameter.Type),
            _ => null,
        };

        private static CoopType? FromType(TypeSyntax type) => type.OpenAngle is null ? null : FromParts(type.Name, type.TemplateArguments);

        /// <summary>型の名前と型の引数のトークンから、協調行列の型を作る。</summary>
        private static CoopType? FromParts(string name, ImmutableArray<ShaderSyntaxToken> arguments)
        {
            List<string> parts = Split(arguments);

            return name switch
            {
                "coopmat" when parts.Count == 5 => new CoopType(false, parts[0], string.Join(", ", parts.Skip(1))),
                "fcoopmatNV" when parts.Count == 4 => new CoopType(true, parts[0] switch { "16" => "float16_t", "64" => "double", _ => "float" }, string.Join(", ", parts.Skip(1))),
                "icoopmatNV" when parts.Count == 4 => new CoopType(true, parts[0] switch { "8" => "int8_t", "16" => "int16_t", _ => "int" }, string.Join(", ", parts.Skip(1))),
                "ucoopmatNV" when parts.Count == 4 => new CoopType(true, parts[0] switch { "8" => "uint8_t", "16" => "uint16_t", _ => "uint" }, string.Join(", ", parts.Skip(1))),
                _ => null,
            };
        }

        /// <summary>型の引数を括弧の外のカンマで分け、空白を詰めた文字列にする。整数のリテラルを括弧で囲んだだけのものは、括弧を外す。</summary>
        private static List<string> Split(ImmutableArray<ShaderSyntaxToken> tokens)
        {
            List<string> parts = [];
            List<string> current = [];
            int parens = 0;

            foreach (ShaderSyntaxToken token in tokens)
            {
                parens += token.Kind switch
                {
                    ShaderSyntaxKind.OpenParenToken => 1,
                    ShaderSyntaxKind.CloseParenToken => -1,
                    _ => 0,
                };

                if (parens == 0 && token.Kind == ShaderSyntaxKind.CommaToken)
                {
                    parts.Add(Normalize(current));
                    current.Clear();
                    continue;
                }

                current.Add(token.Text);
            }

            parts.Add(Normalize(current));
            return parts;

            static string Normalize(List<string> texts)
                => texts is ["(", var only, ")"] && only.All(char.IsAsciiDigit) ? only : string.Concat(texts);
        }
    }
}

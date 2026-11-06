using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の、利用者の関数のサンプラーの仮引数に渡したテクスチャの食い違いを検査する (GD0334)。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 は、サンプラーの仮引数ごとに、最初に渡した uniform のフィルター・繰り返し・ヒント (<c>hint_screen_texture</c>・
/// <c>hint_depth_texture</c>・<c>hint_normal_roughness_texture</c> だけ) か、組み込みのテクスチャの名前を覚える
/// (<c>_propagate_function_call_sampler_uniform_settings</c>)。同じ仮引数に設定の違う uniform を渡すか、uniform と組み込みを混ぜるか、
/// 違う組み込みを渡すと誤りである。関数の仮引数をそのまま別の関数に渡したものは、渡した先の仮引数にもたどる。
/// 呼び出しは、関数を書いた順、式の中では内側から左の順に調べる。
/// </para>
/// <para>
/// <c>RADIANCE</c> (<c>sky</c>) は利用者の関数に渡せない。
/// </para>
/// </remarks>
internal sealed class GodotSamplerAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>覚えるヒント (<c>_sanitize_hint</c>)。</summary>
    private static readonly ImmutableHashSet<string> KeptHints = ["hint_screen_texture", "hint_normal_roughness_texture", "hint_depth_texture"];

    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.SamplerArgumentMismatch];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        void Report(ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(GodotRuleDescriptors.SamplerArgumentMismatch, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            new Walker(program.Tree.Root, compilation.GetExpressionTypeBinder(program), Report).Run();
        }
    }

    /// <summary>サンプラーの仮引数 1 つが覚えたもの。</summary>
    private sealed class Argument
    {
        /// <summary>uniform の設定 (フィルター・繰り返し・ヒント)。</summary>
        public string? Settings { get; set; }

        /// <summary>組み込みのテクスチャの名前。</summary>
        public string? Builtin { get; set; }

        /// <summary>この仮引数をそのまま渡した先 (関数の名前と仮引数の位置)。</summary>
        public HashSet<(string Function, int Index)> Connections { get; } = [];
    }

    private sealed class Walker(CompilationUnitSyntax root, ExpressionTypeBinder binder, Action<ShaderSyntaxToken, string> report)
    {
        private readonly Dictionary<(string Function, int Index), Argument> _arguments = [];
        private readonly Dictionary<string, FunctionDeclarationSyntax> _functions = new(StringComparer.Ordinal);

        public void Run()
        {
            foreach (FunctionDeclarationSyntax function in root.Declarations.OfType<FunctionDeclarationSyntax>().Where(f => f.Body is not null))
            {
                _functions.TryAdd(function.Name, function);
                Walk(function.Body!, function);
            }
        }

        private void Walk(ShaderSyntaxNode node, FunctionDeclarationSyntax current)
        {
            foreach (Core.Syntax.SyntaxNode child in node.ChildNodes())
            {
                if (child is ShaderSyntaxNode shaderNode)
                {
                    Walk(shaderNode, current);
                }
            }

            if (node is InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation
                && callee.Name != current.Name
                && _functions.TryGetValue(callee.Name, out FunctionDeclarationSyntax? called))
            {
                Call(invocation, callee, called, current);
            }
        }

        private void Call(InvocationExpressionSyntax invocation, IdentifierExpressionSyntax callee, FunctionDeclarationSyntax called, FunctionDeclarationSyntax current)
        {
            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
            ParameterSyntax[] parameters = [.. called.ParameterList];

            for (int i = 0; i < Math.Min(arguments.Length, parameters.Length); i++)
            {
                if (!parameters[i].Type.Name.Contains("sampler", StringComparison.Ordinal))
                {
                    continue;
                }

                ExpressionSyntax argument = arguments[i] is ElementAccessExpressionSyntax element ? element.Target : arguments[i];

                if (argument is not IdentifierExpressionSyntax name)
                {
                    continue;
                }

                NameResolution resolution = binder.ResolveName(name);

                switch (resolution)
                {
                    case { Kind: DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator }] }
                        when declaration.HasModifier("uniform"):
                        string? error = PropagateSettings(callee.Name, i, SettingsOf(declarator));

                        if (error is not null)
                        {
                            report(callee.Identifier, error);
                        }

                        break;

                    case { Kind: DeclaredNameKind.Local, Candidates: [{ Declarator: ParameterSyntax parameter }] }:
                        int index = current.ParameterList.ToList().IndexOf(parameter);

                        if (index >= 0)
                        {
                            ArgumentOf(current.Name, index).Connections.Add((callee.Name, i));
                        }

                        break;

                    // 組み込みのテクスチャ (TEXTURE、SCREEN_TEXTURE など)。宣言されていない名前は CF0310 が報告する。
                    case { Kind: DeclaredNameKind.NotFound }
                        when binder.GetTypesByConfiguration(name) is [{ Type: { } type }] && type.Contains("sampler", StringComparison.Ordinal):
                        if (name.Name == "RADIANCE")
                        {
                            report(name.Identifier, "RADIANCE は利用者の関数に渡せません。関数の外でサンプリングして、結果を渡してください。");
                            break;
                        }

                        if (PropagateBuiltin(callee.Name, i, name.Name) is { } builtinError)
                        {
                            report(callee.Identifier, builtinError);
                        }

                        break;
                }
            }
        }

        private Argument ArgumentOf(string function, int index)
        {
            if (!_arguments.TryGetValue((function, index), out Argument? argument))
            {
                _arguments[(function, index)] = argument = new Argument();
            }

            return argument;
        }

        private string? PropagateSettings(string function, int index, string settings)
        {
            Argument argument = ArgumentOf(function, index);

            if (argument.Builtin is not null)
            {
                return $"関数 '{function}' の {index + 1} 番目のサンプラーの仮引数に、組み込みのテクスチャと uniform の両方を渡しています。";
            }

            if (argument.Settings is not null)
            {
                return argument.Settings == settings
                    ? null
                    : $"関数 '{function}' の {index + 1} 番目のサンプラーの仮引数に、フィルター・繰り返し・ヒントの違うテクスチャを渡しています。";
            }

            argument.Settings = settings;

            foreach ((string next, int nextIndex) in argument.Connections)
            {
                if (PropagateSettings(next, nextIndex, settings) is { } error)
                {
                    return error;
                }
            }

            return null;
        }

        private string? PropagateBuiltin(string function, int index, string builtin)
        {
            Argument argument = ArgumentOf(function, index);

            if (argument.Settings is not null)
            {
                return $"関数 '{function}' の {index + 1} 番目のサンプラーの仮引数に、組み込みのテクスチャと uniform の両方を渡しています。";
            }

            if (argument.Builtin is not null)
            {
                return argument.Builtin == builtin
                    ? null
                    : $"関数 '{function}' の {index + 1} 番目のサンプラーの仮引数に、違う組み込みのテクスチャを渡しています。";
            }

            argument.Builtin = builtin;

            foreach ((string next, int nextIndex) in argument.Connections)
            {
                if (PropagateBuiltin(next, nextIndex, builtin) is { } error)
                {
                    return error;
                }
            }

            return null;
        }

        /// <summary>uniform のフィルター・繰り返し・ヒント (Godot が比べるもの) を 1 つの文字列にする。</summary>
        private static string SettingsOf(VariableDeclaratorSyntax declarator)
        {
            string[] hints = [.. declarator.Semantics.Select(s => s.Name)];
            string filter = hints.LastOrDefault(h => h.StartsWith("filter_", StringComparison.Ordinal)) ?? "-";
            string repeat = hints.LastOrDefault(h => h.StartsWith("repeat_", StringComparison.Ordinal)) ?? "-";
            string hint = hints.LastOrDefault(KeptHints.Contains) ?? "-";
            return $"{filter}/{repeat}/{hint}";
        }
    }
}

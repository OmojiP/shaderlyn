# ルールの実例集

書きたいルールに近い例を探すためのページです。例はそのまま使用するか、写して書き換えて使用できます。
ルールの書き方の手順は [自作ルールの作り方](tutorial.md) にあります。

* **実装参照先**: `samples/Shaderlyn.Cookbook/`（1 クラス 1 ファイルです。プロジェクトと名前空間はどちらも `Shaderlyn.Cookbook` です）。
  公開 API だけで書いてあるので、`Shaderlyn.Semantics` パッケージを参照したプロジェクトにファイルをそのまま入れて使用できます。
  診断の定義は `CookbookRules.cs` にまとめてあります
* **テスト参照先**: `CookbookTests.cs`
* **このページのコード**: 読みやすさのために `using` と診断の定義 (`Rule` など) を省いています。
  診断の定義は [自作ルールの作り方「ルールを定義する」](tutorial.md#3-1-ルールを定義する) の形で用意してください

## 目次

- [外部ファイルによる設定の動的ロード](#外部ファイルによる設定の動的ロード)
- [HLSL / 構文解析ルール](#hlsl--構文解析ルール)
- [型評価とオーバーロードの解析](#型評価とオーバーロードの解析)
- [ShaderLab 構文解析ルール](#shaderlab-構文解析ルール)
- [独自エンジンの環境](#独自エンジンの環境)
- [Unity 以外の言語と環境](#unity-以外の言語と環境)
- [そのほかの実装例](#そのほかの実装例)
- [アナライザ実装時の共通ガイドライン](#アナライザ実装時の共通ガイドライン)

---

## 外部ファイルによる設定の動的ロード

コンストラクタ経由で設定値を引き渡すことで、外部ファイル（YAML / CSV / JSON）や CLI オプションに基づく動的なルール定義が可能です。

```csharp
// 外部ファイルから禁止リストをロード
string[] banned = File.ReadAllLines("banned-functions.txt")
    .Select(line => line.Trim())
    .Where(line => line.Length > 0 && !line.StartsWith('#'))
    .ToArray();

ImmutableArray<DiagnosticAnalyzer> analyzers =
[
    .. BuiltInAnalyzers.All,
    new BannedFunctionAnalyzer(banned),
];

return (int)await Program.RunAsync(args, analyzers, Console.Out, Console.Error);

```

> **Note**: アナライザのインスタンスは並列処理されるため、**可変状態（State）を保持することは禁止**されています。ただし、コンストラクタで受け取る読み取り専用データ（Immutable データ構造）の保持は問題ありません。

### 設定ファイルの `options` から閾値を受け取る

閾値のような数値は、コンストラクタの代わりに `.shaderlyn.yaml` から受け取れます。
どのオプション名を読み取るかはルールごとに自分で決めます。読み取る処理を書かないと、設定ファイルに書いても反映されません
（組み込みルールにはオプションを読み取るものがありません）。

実装は `samples/Shaderlyn.Cookbook/Budget/ParameterCountLimitAnalyzer.cs` にあります。

```csharp
public sealed class ParameterCountLimitAnalyzer(int defaultMaxParameters = 8) : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            // 設定が存在しない場合と、数値として解釈できない場合は既定値へ倒れる
            int maximum = c.Options.GetIntOption(
                Rule.Id,
                "maxParameters",
                defaultMaxParameters);

            if (!c.Compilation.IsReportable(c.Node.NameToken))
            {
                return;
            }

            int parameters = c.Node.ParameterList.Count();

            if (parameters > maximum)
            {
                c.ReportDiagnostic(
                    Rule,
                    c.Node.NameToken.Span,
                    c.Node.Name,
                    parameters,
                    maximum);
            }
        });
    }
}
```

利用者はこう書きます。

```yaml
rules:
  COOK0030:
    severity: warning
    options: { maxParameters: 4 }
```

- **値は文字列として渡されます。** 整数は `GetIntOption`、それ以外は `TryGetOption` で受け取って自分で解釈します
- **解釈できない値は既定値へ倒します。** 書き間違いは設定の読み込み側が `TOOL0003` で報告します
- **向くのは閾値です。** 禁止する名前の一覧のように項目が増えるものは、コンストラクタで渡したほうが型のまま扱えます
- **ルールの説明に、読み取るオプション名を書き残してください。** 利用者が設定ファイルに何を書けるかを知る手がかりがそこしかありません

---

## どこまでを報告の対象にするか

この例では `IsReportable(トークン)` で、**利用者が修正できるコードだけ**に絞っています。
Unity や外部パッケージのヘッダのコードを指摘しても、利用者には修正できないためです。
利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) のコードは、取り込むシェーダーの文脈で渡ってきて、報告はヘッダの位置に出ます。

選べるのは 2 つです。

| 書き方                   | 対象                                                         | 報告の位置                                      |
| --------------------- | ---------------------------------------------------------- | ------------------------------------------ |
| `IsReportable(node)`  | 利用者のファイル (このファイルと、利用者が書いて取り込んだヘッダ) に書かれたコードと、それらのファイルが書いたマクロの本体から来たコード | そのコードの位置。マクロの本体から来たコードは本体の位置（`node.GetLocation()` が自動で指す） |
| `IsWrittenHere(node)` | このファイルに直接書かれたコードだけ                                         | そのコードの位置                                   |
組み込みルールは `IsReportable` を使用しています。`IsWrittenHere` はエディタの操作のための判定で、
共通のヘッダに書かれたコードを見落とします。同じマクロを何度使用しても、報告は本体の 1 か所にまとまります。

```hlsl
#define HELPER_DECL half4 helper() { return undeclared; }
                                  // ^^^^^^^^^^ IsReportable の場合はここに報告される
HELPER_DECL
```

**Unity や外部パッケージのヘッダのマクロの本体は、どちらでも対象外です。** 実引数として渡したトークンは
呼び出し位置に書かれたものなので、そちらの位置で報告されます。

---

## HLSL / 構文解析ルール

### 1. 禁止マクロの検出

マクロはプリプロセス時に展開され構文木（AST）に含まれないため、展開前のトークン系列 `ShaderCompilation.CodeTokens` を参照します。
利用者が書いて取り込んだヘッダの分は `UserIncludeCodeTokens` にあります。こちらのトークンはヘッダを指すので、位置はトークンから作ります。

```csharp
internal sealed class BannedMacroAnalyzer(params string[] banned) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
        => context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation) return;

            foreach (ShaderSyntaxToken token in compilation.CodeTokens)
            {
                if (token.Kind == ShaderSyntaxKind.IdentifierToken && _banned.Contains(token.Text))
                {
                    c.ReportDiagnostic(Rule, token.Span, token.Text);
                }
            }

            foreach (ShaderSyntaxToken token in compilation.UserIncludeCodeTokens)
            {
                if (token.Kind == ShaderSyntaxKind.IdentifierToken && _banned.Contains(token.Text))
                {
                    c.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation(), token.Text));
                }
            }
        });
}
```

### 2. 禁止関数の呼び出し検出

構文木上の関数呼び出しノード (`InvocationExpressionSyntax`) から判定します。

```csharp
internal sealed class BannedFunctionAnalyzer(params string[] banned) : ShaderRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            // 関数名（Identifier）のみを指摘範囲とすることで、複数行にわたる波線ハイライトを防止する
            if (c.Node.Target is IdentifierExpressionSyntax name && _banned.Contains(name.Name))
            {
                c.ReportDiagnostic(Rule, name.Span, name.Name);
            }
        });
}
```

### 3. 分岐構文 (`if`) の禁止

HLSLでifを見つけたら報告する例

```csharp
internal sealed class NoBranchAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<IfStatementSyntax>(
            c => c.ReportDiagnostic(Rule, c.Node.IfKeyword.Span));
}
```

### 4. ネストされた `else if` 連鎖数の制限

`else if` は構文木上「`else` ブロック配下の `if`」として表現されます。多重判定を防ぐため、ネスト連鎖の最先頭ノードでのみ評価を行います。

```csharp
internal sealed class TooManyBranchesAnalyzer(int maxElseCount) : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<IfStatementSyntax>(c =>
        {
            // 連鎖の途中の場合は親ノード側の処理に委ねる
            if (c.Node.Parent is IfStatementSyntax parent && ReferenceEquals(parent.ElseStatement, c.Node))
            {
                return;
            }

            int elseCount = 0;
            for (IfStatementSyntax? current = c.Node; current?.ElseStatement is not null;)
            {
                elseCount++;
                current = current.ElseStatement as IfStatementSyntax;
            }

            if (elseCount > maxElseCount)
            {
                c.ReportDiagnostic(Rule, c.Node.IfKeyword.Span, elseCount, maxElseCount);
            }
        });
}
```

### 5. コンパイル条件分岐（シンボル）に応じた関数制限

「特定のシンボルが定義されている構成では、特定の関数呼び出しを禁じる」といった検査です。

```csharp
// Rule の書式: "{0}関数 '{1}' は使用できません。"  ({0} に「構成：○○ のとき、」が入る)
internal sealed class BannedUnderSymbolAnalyzer(string keyword, string function) : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(
            c =>
            {
                if (c.Node.Target is IdentifierExpressionSyntax name
                    && string.Equals(name.Name, function, StringComparison.Ordinal))
                {
                    c.ReportWhen(SymbolCondition.Always, Rule, name.Span, function);
                }
            },
            // その構成に存在しうる呼び出しだけを受け取る。ReportWhen にもこの条件が掛かる。
            new NodeActionOptions { When = $"defined({keyword})" });
}
```

```text
構成：_LOW_QUALITY のとき、関数 'ExpensiveCall' は使用できません。
```

- `#ifdef _LOW_QUALITY` の外の呼び出しも、その構成で実行されるので報告します。`#ifndef _LOW_QUALITY` の中の呼び出しは受け取りません
- 条件の掛け方 (ノードの条件、木の構成、ありえない構成と `#error` に到達する構成を除く、Pass と構成をまたいで 1 件にまとめる) は
  `ReportWhen` と `NodeActionOptions` が行います ([API リファレンス](api-reference.md#reportwhen))
- 条件は `"defined(_A) && QUALITY >= 2"` のように `#if` と同じ書き方でも、`SymbolCondition` でも書けます

---

## 型評価とオーバーロードの解析

### 6. 引数型の評価およびオーバーロード解析

#### 引数の評価 (`ExpressionTypeBinder`)

型バインダーは `c.Compilation.GetExpressionTypeBinder(c.Program)` から取得して再利用します（インスタンスの再生成はパフォーマンス劣化の原因となります）。

```csharp
internal sealed class ArgumentTypeAnalyzer(string function, int parameter, string expectedType)
    : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.Target is not IdentifierExpressionSyntax name
                || !string.Equals(name.Name, function, StringComparison.Ordinal))
            {
                return;
            }

            ImmutableArray<ExpressionSyntax> arguments =
                [.. c.Node.Arguments.Select(a => a.Node).OfType<ExpressionSyntax>()];

            if (parameter >= arguments.Length) return;

            ExpressionSyntax argument = arguments[parameter];
            string? actual = c.Compilation.GetExpressionTypeBinder(c.Program)
                .GetEvaluatorFor(argument)
                .Evaluate(argument);

            // 型評価結果が null（判定不能）の場合は報告を行わない
            if (actual is null || string.Equals(actual, expectedType, StringComparison.Ordinal))
            {
                return;
            }

            c.ReportDiagnostic(Rule, argument.Span, function, parameter + 1, expectedType, actual);
        });
}
```

#### オーバーロードの判定 (`ResolveCall`)

実引数の型ではなく、解決された宣言側の仮引数型に対して照合を行います（HLSLの暗黙的型変換に対応するため）。

```csharp
internal sealed class BannedOverloadAnalyzer(string function, params string?[] parameterTypes)
    : ShaderRuleAnalyzer
{
    private readonly ImmutableArray<string?> _parameters = [.. parameterTypes];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.TargetName is not { } name || !string.Equals(name, function, StringComparison.Ordinal))
            {
                return;
            }

            OverloadResolution resolution = c.Compilation.ResolveCall(c.Node, c.Program);

            // 呼び出し先が確定（IsResolved）していない場合は判定をスキップ
            if (!resolution.IsResolved || !Matches(resolution.ParameterTypes))
            {
                return;
            }

            c.ReportDiagnostic(Rule, c.Node.Target.Span, function, string.Join(", ", resolution.ParameterTypes));
        });

    private bool Matches(ImmutableArray<string> resolved)
    {
        if (resolved.Length != _parameters.Length) return false;

        for (int i = 0; i < _parameters.Length; i++)
        {
            if (_parameters[i] is { } expected && !string.Equals(resolved[i], expected, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
```

---

## ShaderLab 構文解析ルール

### 7. 必須タグ（`SubShader`）の検証

`SubShader` 内への必須タグ設定の有無を検証します。`Pass` 内で定義されたタグと混同しないよう構文ツリーの親関係を判定します。

```csharp
internal sealed class RequiredTagAnalyzer(string tag, params string[] allowedValues) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _allowed =
        allowedValues.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MissingRule, ValueRule];

    public override void Initialize(AnalysisContext context)
        => context.RegisterAnalysisStartAction(start =>
        {
            List<SubShaderSyntax> subShaders = [];
            HashSet<SyntaxNode> withTag = [];

            start.RegisterNodeAction<SubShaderSyntax>(c => subShaders.Add(c.Node));
            start.RegisterNodeAction<TagSyntax>(c =>
            {
                if (!string.Equals(c.Node.Key, tag, StringComparison.OrdinalIgnoreCase)) return;

                if (FindSubShader(c.Node) is { } subShader)
                {
                    withTag.Add(subShader);
                }

                if (!_allowed.IsEmpty && !_allowed.Contains(c.Node.Value))
                {
                    c.ReportDiagnostic(ValueRule, c.Node.ValueToken.Span, tag, c.Node.Value);
                }
            });

            start.RegisterAnalysisEndAction(end =>
            {
                foreach (SubShaderSyntax subShader in subShaders.Where(s => !withTag.Contains(s)))
                {
                    end.ReportDiagnostic(MissingRule, subShader.Keyword.Span, tag);
                }
            });
        });

    private static SubShaderSyntax? FindSubShader(TagSyntax tag)
    {
        for (SyntaxNode? node = tag.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case SubShaderSyntax subShader: return subShader;
                case PassSyntax: return null; // Pass 内のタグは除外
            }
        }
        return null;
    }
}
```

---

## 独自エンジンの環境

Unity・Unreal・汎用の HLSL (DXC) のどれにも当てはまらないエンジンは、環境を自分で書いて追加できます。
字句解析・プリプロセッサ・型・ルールは組み込みのものがそのまま働きます。書くのは、エンジンの決まりを解析に教える部分だけです。

* **実装参照先**: `samples/Shaderlyn.Cookbook/Environment/MyEngineEnvironment.cs`
* **テスト参照先**: `HostEnvironmentTests.cs`

### 8. エンジン独自の `#pragma` で構成とエントリポイントを宣言する

架空のエンジン「MyEngine」は、次のように書きます。

```hlsl
#pragma variant QUALITY 0 1 2     // 値を持つ構成の軸。先頭の値が既定
#pragma variant USE_FOG           // 定義されるか・されないかの構成の軸
#pragma entry MainPS ps_6_6       // エントリポイントとコンパイルのターゲット
```

環境は `IHlslEnvironment` (`Shaderlyn.Semantics.Environments`) を実装します。

| メンバー | 教えること |
| --- | --- |
| `ConfigurePreprocessor` | どの単位にも共通する設定。言語のバージョン、エンジンのコンパイラが定義するマクロ |
| `PrepareUnit` | 単位ごとの設定。ターゲットで決まるマクロ (`HlslShaderTarget.CreateDxcMacros`) |
| `ReadDeclaredVariants` / `ReadVariants` | 構成の軸。展開の前 (字句解析しただけのトークン) と後 (取り込んだヘッダの指令も含む) の 2 回読み取る |
| `ReadEntryPoints` | エントリポイント。この名前の関数が存在しなければ CF0301。ステージ (`Stage`) を渡すと、システム値のセマンティクスを CF0302 で検査する |
| `LooksLikeVariantSymbol` | 宣言されていない名前を、構成の軸の宣言し忘れとして報告するか (CF0201) |
| `IsGeneratedInclude` | ビルドのときに生成される取り込みか。解決できなくても CF0021 にしない |
| `GetFunctionsDeclaredIn` | (省略可) 解決できない取り込みが宣言しうる関数の名前。分かっていれば、ほかの名前の呼び出しを CF0401 / CF0402 で検査できる |

```csharp
public sealed class MyEngineEnvironment : IHlslEnvironment
{
    public string Name => "myengine";

    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
        => options with
        {
            Language = new HlslLanguageOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 },
            PredefinedMacros = HlslShaderTarget.CreateCommonDxcMacros(HlslLanguageVersion.Hlsl2021)
                .SetItem("MYENGINE", "1")
                .SetItems(semantics.PredefinedMacros ?? ImmutableDictionary<string, string>.Empty),
        };

    public PreprocessorOptions PrepareUnit(
        ShaderCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens, PreprocessorOptions options, string filePath)
    {
        foreach (PragmaDirective pragma in ReadPragmas(tokens))
        {
            if (pragma.Name == "entry"
                && pragma.Arguments.Skip(1).FirstOrDefault() is { } target
                && HlslShaderTarget.TryParse(target.Text, out HlslShaderTarget? parsed))
            {
                return options with
                {
                    PredefinedMacros = parsed.Value.CreateDxcMacros(HlslLanguageVersion.Hlsl2021).SetItems(options.PredefinedMacros),
                };
            }
        }

        return options;
    }

    public VariantSpace ReadDeclaredVariants(ShaderCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens)
        => ReadVariantSpace(ReadPragmas(tokens));

    public VariantSpace ReadVariants(ShaderCompilationUnit unit, PreprocessResult result)
        => ReadVariantSpace(result.Pragmas);

    public ImmutableArray<ShaderEntryPoint> ReadEntryPoints(ShaderCompilationUnit unit, PreprocessResult result)
    {
        List<ShaderEntryPoint> entryPoints = [];

        foreach (PragmaDirective pragma in result.Pragmas.Where(pragma => pragma.Name == "entry"))
        {
            ImmutableArray<ShaderSyntaxToken> arguments = [.. pragma.Arguments];

            if (arguments.IsEmpty)
            {
                continue;
            }

            // 2 つ目の引数 (ps_6_6) からステージを決める。ステージを渡すと CF0302 が働く。
            HlslShaderStage? stage = arguments.Length > 1
                                     && HlslShaderTarget.TryParse(arguments[1].Text, out HlslShaderTarget? target)
                                     && target.Value.Stage != HlslShaderStage.Library
                ? target.Value.Stage
                : null;

            entryPoints.Add(new ShaderEntryPoint(arguments[0].Text, "#pragma entry", arguments[0].GetLocation(), stage));
        }

        return [.. entryPoints];
    }

    public bool LooksLikeVariantSymbol(string name) => name.StartsWith("USE_", StringComparison.Ordinal);

    public bool IsGeneratedInclude(string path) => path.StartsWith("Generated/", StringComparison.Ordinal);

    private static VariantSpace ReadVariantSpace(IEnumerable<PragmaDirective> pragmas)
    {
        VariantSpace space = VariantSpace.Empty;

        foreach (PragmaDirective pragma in pragmas.Where(p => p.Name == "variant"))
        {
            ImmutableArray<ShaderSyntaxToken> arguments = [.. pragma.Arguments];

            if (arguments.IsEmpty)
            {
                continue;
            }

            ShaderSyntaxToken name = arguments[0];
            List<int> values = [.. arguments.Skip(1).Select(v => int.TryParse(v.Text, out int n) ? n : -1).Where(n => n >= 0)];

            // 宣言の位置を持たせると、使用していない宣言を CF0202 で報告できる。
            space = space.Union(values.Count >= 2
                ? VariantSpace.ForValues(name.Text, values, name.GetLocation())
                : new VariantSpace([new VariantDeclaration(name.Text, VariantSymbolKind.Compiled, name.GetLocation())], []));
        }

        return space;
    }

    // 字句解析しただけのトークンから #pragma の行を読み取る。展開の前に構成の軸を知るために使用する。
    private static IEnumerable<PragmaDirective> ReadPragmas(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.HashToken || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "pragma" || tokens[i + 2].IsAtLineStart)
            {
                continue;
            }

            int end = i + 2;

            while (end < tokens.Length && !tokens[end].IsAtLineStart && tokens[end].Kind != ShaderSyntaxKind.EndOfFileToken)
            {
                end++;
            }

            ImmutableArray<ShaderSyntaxToken> line = tokens[(i + 2)..end];
            yield return new PragmaDirective(line, line[0]);
            i = end - 1;
        }
    }
}
```

値を持つシンボル (`VariantSpace.ForValues`) は、`#if QUALITY == 2` / `#if QUALITY >= 1` / `#if QUALITY` を値ごとに区別して解析し、
指摘に `QUALITY == 2 のとき` を添えます。

### 9. 自前の CLI と言語サーバーに環境を追加する

環境は自作ルールと同じく、ホストを作るときに渡します (Native AOT ではプラグインを読み込めないため)。
設定ファイルの `environment` (または `--environment`) にこの名前を書くと選ばれます。

```csharp
HostEnvironment myEngine = new("myengine", "MyEngine", [".fx"], () => new MyEngineEnvironment());

return (int)await Program.RunAsync(args, BuiltInAnalyzers.All, [myEngine], Console.Out, Console.Error);
```

```yaml
# .shaderlyn.yaml
environment: myengine
```

言語サーバーは `new ShaderLanguageServer(connection, analyzers, [myEngine])` で作ります。

* コンパイル単位は、汎用の HLSL 環境と同じく設定ファイルの `units:` から作ります。当てはまらないファイルは、ファイル全体を 1 つの単位にします
* 設定ファイルの `symbols:` と `units:` の `entry` は、単位の `Variants` / `EntryPoints` に入ります。使用するかどうかは環境が決めます
  (上の例はソースの `#pragma` だけを解析します)
* 名前は組み込みの環境 (`unity` / `hlsl` / `unreal` / `glsl` / `godot` / `slang` / `wgsl`) と重ねられません

## Unity 以外の言語と環境

ルールは既定では HLSL のファイルだけを調べます。HLSL には Unity の `.shader`・`.hlsl`・`.compute` のほか、
汎用の HLSL (`environment: hlsl`)、Unreal Engine (`environment: unreal`)、[独自エンジンの環境](#独自エンジンの環境) が含まれます。
GLSL・Godot・Slang・WGSL のファイルも調べる場合は、`Languages` を上書きします。

| `ShaderLanguages` | 対象 |
| --- | --- |
| `Hlsl` (既定) | Unity・汎用の HLSL・Unreal Engine・独自エンジンの環境 |
| `Glsl` | `.vert`・`.frag`・`.comp`・`.glsl` など |
| `Godot` | `.gdshader`・`.gdshaderinc` |
| `Slang` | `.slang` |
| `Wgsl` | `.wgsl` |
| `All` | すべて |

構文木のノードの型 (`FunctionDeclarationSyntax`・`InvocationExpressionSyntax` など) はどの言語でも同じです。
ただし、型の名前・組み込み関数・宣言の書き方は言語ごとに違います。HLSL のつもりの判断を GLSL に当てると誤検出になるので、
言語を広げるときはその言語のコードでテストしてから広げてください。どの言語のファイルかは `c.Compilation.Language` で参照できます。

`samples/Shaderlyn.Cookbook/` には、言語ごとのフォルダーに例を置いています。

| フォルダー | 例 |
| --- | --- |
| `Glsl/` | [11. GLSL の古い組み込み関数を報告する](#11-glsl-の古い組み込み関数を報告する) |
| `Slang/` | [12. Slang のインターフェイスの名前を検査する](#12-slang-のインターフェイスの名前を検査する) |
| `Godot/` | [13. Godot の色のテクスチャに `source_color` を付ける](#13-godot-の色のテクスチャに-source_color-を付ける) |
| `Wgsl/` | [14. WGSL の関数の名前を検査する](#14-wgsl-の関数の名前を検査する) |
| `Unreal/` | [15. Unreal Engine で `Texture2DSample` を使用する](#15-unreal-engine-で-texture2dsample-を使用する) |

### 10. 言語に依らないルールをすべての言語に当てる

関数の仮引数の数のように、構文の形だけを見るルールは `ShaderLanguages.All` にできます
(`samples` の `ParameterCountLimitAnalyzer`・`FunctionLengthLimitAnalyzer`。下は前者を短くしたものです)。

```csharp
public sealed class AnyLanguageParameterCountAnalyzer(int defaultMaxParameters = 8) : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    // 関数の形だけを見るので、言語に依らない。どの言語のファイルにも当てる。
    protected override ShaderLanguages Languages => ShaderLanguages.All;

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            int maximum = c.Options.GetIntOption(Rule.Id, "maxParameters", defaultMaxParameters);
            int parameters = c.Node.ParameterList.Count();

            if (c.Compilation.IsReportable(c.Node.NameToken) && parameters > maximum)
            {
                c.ReportDiagnostic(Rule, c.Node.NameToken.Span, c.Node.Name, parameters, maximum);
            }
        });
}
```

WGSL の `fn f(a: f32, b: f32) -> f32` も、Godot の関数も、同じ `FunctionDeclarationSyntax` として渡ってきます。

### 11. GLSL の古い組み込み関数を報告する

`Languages` を `ShaderLanguages.Glsl` にすると、GLSL のファイルだけが渡ってきます (`DeprecatedGlslFunctionAnalyzer`)。
`texture2D` などは GLSL 1.30 で非推奨になり、core プロファイルと GLSL ES 3.00 では使用できません。

```csharp
public sealed class DeprecatedGlslFunctionAnalyzer : ShaderRuleAnalyzer
{
    private static readonly ImmutableDictionary<string, string> Replacements = new Dictionary<string, string>
    {
        ["texture2D"] = "texture",
        ["texture2DLod"] = "textureLod",
        ["textureCube"] = "texture",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override ShaderLanguages Languages => ShaderLanguages.Glsl;

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            // 利用者が同じ名前の関数を定義している場合は、組み込みの呼び出しではない。
            if (c.Node.Target is IdentifierExpressionSyntax name
                && Replacements.TryGetValue(name.Name, out string? replacement)
                && !c.Program.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == name.Name))
            {
                c.ReportDiagnostic(Rule, name.Span, name.Name, replacement);
            }
        });
}
```

### 12. Slang のインターフェイスの名前を検査する

Slang の `interface` は、`Keyword` が `interface` の `StructDeclarationSyntax` として渡ってきます (`SlangInterfaceNamingAnalyzer`)。
HLSL にも `interface` はありますが、名前の決まりは Slang のプロジェクトのものなので、Slang だけに当てます。

```csharp
public sealed class SlangInterfaceNamingAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override ShaderLanguages Languages => ShaderLanguages.Slang;

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<StructDeclarationSyntax>(c =>
        {
            // IFoo の形 (I の後ろが大文字) を求める。Image のような名前を通さない。
            if (c.Node.Keyword.Text == "interface"
                && c.Node.NameToken is { } name
                && (name.Text.Length < 2 || name.Text[0] != 'I' || !char.IsUpper(name.Text[1])))
            {
                c.ReportDiagnostic(Rule, name.Span, name.Text);
            }
        });
}
```

### 13. Godot の色のテクスチャに `source_color` を付ける

Godot 4 は、`source_color` の付いていないテクスチャを sRGB から線形に変換しません。色のテクスチャに付け忘れると、色が明るく褪せて見えます。
名前に `albedo`・`color` などを含む `uniform sampler2D` (`sampler2DArray` も) に `source_color` がなければ報告します (`GodotSourceColorAnalyzer`)。
`: source_color, filter_linear` のヒントは、宣言子の `Semantics` に 1 つずつ入ります。

```csharp
public sealed class GodotSourceColorAnalyzer : ShaderRuleAnalyzer
{
    private static readonly ImmutableArray<string> ColorWords = ["albedo", "color", "colour", "diffuse", "base_col"];

    // 画面・深度・法線のテクスチャは、名前に color を含んでいても対象にしない。
    private static readonly ImmutableHashSet<string> EngineTextureHints =
        ["hint_screen_texture", "hint_depth_texture", "hint_normal_roughness_texture"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override ShaderLanguages Languages => ShaderLanguages.Godot;

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<VariableDeclarationSyntax>(c =>
        {
            if (!c.Node.HasModifier("uniform") || c.Node.Type.Name is not ("sampler2D" or "sampler2DArray"))
            {
                return;
            }

            foreach (VariableDeclaratorSyntax variable in c.Node.Variables)
            {
                string[] hints = [.. variable.Semantics.Select(s => s.Name)];

                if (c.Compilation.IsReportable(variable.NameToken)
                    && ColorWords.Any(w => variable.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                    && !hints.Contains("source_color")
                    && !hints.Any(EngineTextureHints.Contains))
                {
                    c.ReportDiagnostic(Rule, variable.NameToken.Span, variable.Name);
                }
            }
        });
}
```

### 14. WGSL の関数の名前を検査する

WGSL の関数を小文字と `_` (`snake_case`) で書く決まりを検査します (`WgslFunctionNamingAnalyzer`)。
HLSL や GLSL では `camelCase` が普通なので、言語を WGSL に絞らないと誤検出になります。
`fn` は、ほかの言語の関数と同じ `FunctionDeclarationSyntax` として渡ってきます。

```csharp
public sealed class WgslFunctionNamingAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override ShaderLanguages Languages => ShaderLanguages.Wgsl;

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            string name = c.Node.Name;

            // メッセージは「関数 '{0}' の名前は小文字と '_' で書いてください ('{1}')。」で、{1} に直した名前を入れる。
            if (c.Compilation.IsReportable(c.Node.NameToken) && name.Any(char.IsUpper))
            {
                c.ReportDiagnostic(Rule, c.Node.NameToken.Span, name, ToSnakeCase(name));
            }
        });

    // shadeSurface を shade_surface にする。
    private static string ToSnakeCase(string name)
    {
        System.Text.StringBuilder builder = new();

        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && name[i - 1] != '_' && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
```

### 15. Unreal Engine で `Texture2DSample` を使用する

Unreal Engine の `.usf`・`.ush` は HLSL なので、`Languages` は既定のままにし、環境の名前 (`c.Compilation.Environment.Name`) で絞ります。
この例は、`Texture2D` の `Sample` メソッドを直接呼んでいる箇所を報告し、`/Engine/Private/Common.ush` の `Texture2DSample` を使用するよう促します
(`UnrealTextureSampleAnalyzer`)。`Common.ush` を取り込んでいないファイルでは置き換えられないので、関数の名前 (`c.Program.FunctionNames`) を確認します。

```csharp
public sealed class UnrealTextureSampleAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeShader(ShaderAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Compilation.Environment.Name != "unreal"
                || c.Node.Target is not MemberAccessExpressionSyntax { Name: "Sample" } member
                || !c.Program.FunctionNames.Contains("Texture2DSample")
                || c.Node.FirstAncestorOrDefault<FunctionDeclarationSyntax>()?.Name == "Texture2DSample"
                || !c.Compilation.IsReportable(member.NameToken))
            {
                return;
            }

            // 型が求まらない場合は報告しない。TextureCube などの Sample も報告しない。
            string? type = c.Compilation.GetExpressionTypeBinder(c.Program)
                .GetEvaluatorFor(member.Target)
                .Evaluate(member.Target);

            if (type is "Texture2D" or "Texture2D<float4>")
            {
                c.ReportDiagnostic(Rule, member.NameToken.Span, "Texture2DSample");
            }
        });
}
```

### Unity 以外のファイルでテストする

`ShaderRuleVerifier` は、`Analyze` に渡したパスの拡張子で言語と環境を決めます
(`.usf`・`.ush` は Unreal Engine、`.slang` は Slang、`.vert`・`.frag`・`.glsl` などは GLSL、`.gdshader` は Godot、`.wgsl` は WGSL、それ以外は Unity)。
設定ファイルを置かずに CLI でファイルを直接指定した場合と同じ環境です。
`.hlsl` は Unity の HLSL として扱うので、汎用の HLSL として確認する場合は `WithEnvironment("hlsl")` を指定します。
`.mesh` は Unity のメッシュのアセットと同じ拡張子なので Unity になります。GLSL として確認する場合は `WithEnvironment("glsl")` を指定します。
Unity 以外の環境では、ファイル全体を 1 つのコンパイル単位にし、定義済みのマクロは置きません (設定ファイルを置かずに CLI で解析した場合と同じです)。
Unreal Engine は C++ のシェーダーの登録を解析しないので、`Common.ush` などのヘッダは `AddInclude("/Engine/Private/Common.ush", …)` で与えます。

```csharp
public sealed class LanguageRuleTests
{
    [Fact]
    public void GLSLの古い組み込み関数を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new DeprecatedGlslFunctionAnalyzer()).Analyze("""
            #version 330
            uniform sampler2D tex;
            in vec2 uv;
            out vec4 color;
            void main() { color = texture2D(tex, uv); }
            """, "shaders/old.frag");

        Assert.Single(diagnostics);
    }

    [Fact]
    public void 汎用のHLSLでも仮引数の数を検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = new ShaderRuleVerifier(new AnyLanguageParameterCountAnalyzer(defaultMaxParameters: 2))
            .WithEnvironment("hlsl")
            .Analyze("float f(float a, float b, float c) { return a + b + c; }\n", "Shaders/Lit.hlsl");

        Assert.Single(diagnostics);
    }
}
```

## そのほかの実装例

このページに全文を載せていない例も、`samples/Shaderlyn.Cookbook/` に実装とテストがあります。上の 7 例と同じく、コンストラクタに値を渡すだけで使用できます。

| クラス | 何を見るか |
| --- | --- |
| `LiteralLimitAnalyzer` | 数値リテラルの上限 |
| `PropertyNamePrefixAnalyzer` | プロパティ名の接頭辞 |
| `PassCountLimitAnalyzer` | 1 つの `SubShader` に置ける `Pass` の数 |
| `CommandValueAnalyzer` | `Cull` などの命令に書ける値 |
| `ThreadGroupSizeAnalyzer` | `[numthreads]` が指定の倍数か |
| `KernelNamePrefixAnalyzer` | `#pragma kernel` のカーネル名の接頭辞 |
| `WritableBufferNamingAnalyzer` | 書き込めるバッファ・テクスチャ名の接頭辞 |
| `BannedTypeAnalyzer` | 禁じた型（`half` を禁じれば `half4` も報告） |
| `BannedOperatorAnalyzer` | 禁じた演算子（`%` など） |
| `BannedSemanticAnalyzer` | 禁じたセマンティクス |
| `RequiredPragmaAnalyzer` | 必須の `#pragma` |
| `ShaderModelMinimumAnalyzer` | `#pragma target` の下限 |
| `SymbolBudgetAnalyzer` | 宣言するシンボルの数の上限 |
| `BannedIncludeAnalyzer` | 取り込んではいけないヘッダ |
| `RequiredIncludeAnalyzer` | 必ず取り込むべきヘッダ |
| `SampleBudgetAnalyzer` | テクスチャサンプリングの回数 |
| `NestingDepthLimitAnalyzer` | 入れ子の深さ |
| `FunctionLengthLimitAnalyzer` | 関数の文の数 |
| `LoopBoundAnalyzer` | ループの回数 |
| `ConstantBufferSizeLimitAnalyzer` | 定数バッファの大きさ |
| `ParameterCountLimitAnalyzer` | 関数の仮引数の数（上限は設定ファイルの `options` から読み取る） |

---

## アナライザ実装時の共通ガイドライン

| 状態 / 条件 | 推奨する処理動作 | 理由 / 背景 |
| --- | --- | --- |
| **型評価不能** (`Evaluate` が `null`) | **処理をスキップ（無視）** | 判定不能な状態を構文エラーとして報告する誤検出を防ぐため。 |
| **シンボル条件不明** (`IsUnknown`) | **処理をスキップ（無視）** | コンパイル条件が特定できないコードの判定を行わないため。 |
| **未展開のマクロが含まれる** | **処理をスキップ（無視）** | 構文評価時点での確定値が存在しないため。 |
| **モデル非存在** (`GetModel` が `null`) | **処理をスキップ（無視）** | 解析対象外または解析不可能なセクションであるため。 |
| **Unity・外部パッケージのヘッダ** | **解析対象外** | 外部依存コードやライブラリコードの変更を強制させないため。利用者が書いて取り込んだヘッダは対象（`IsReportable`）。 |
| **不確実な構造体のサイズ見積もり** | **サイズ合計を出力しない** | 不完全な結果を正常値として報告するリスクを防ぐため。 |
| **指摘ハイライト（Span）の範囲** | **最小限のトークンに限定** | 波線が複数行に広がるのを防ぎ、修復箇所を特定しやすくするため。 |
| **欠損の報告（「〜が存在しない」）** | **`RegisterAnalysisStartAction` で収集後に評価** | 単一ノードの評価のみでは全域での非存在が証明できないため。 |

---

## 関連

- [自作ルールの作り方](tutorial.md)
- [自作ルールの書き方の手引き](guide.md)
- [ルール API リファレンス](api-reference.md)

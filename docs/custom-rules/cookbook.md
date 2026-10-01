# ルールの実例集

書きたいルールに近い例を探すためのページです。例はそのまま使うか、写して直して使えます。
ルールの書き方の手順は [自作ルールの作り方](tutorial.md) にあります。

* **実装参照先**: `samples/Shaderlyn.Cookbook/`（1 クラス 1 ファイルです。プロジェクトと名前空間はどちらも `Shaderlyn.Cookbook` です）。
  公開 API だけで書いてあるので、`Shaderlyn.Semantics` パッケージを参照したプロジェクトにファイルをそのまま入れて使えます。
  診断の定義は `CookbookRules.cs` にまとめてあります
* **テスト参照先**: `CookbookTests.cs`
* **このページのコード**: 読みやすさのために `using` と診断の定義 (`Rule` など) を省いています。
  診断の定義は [自作ルールの作り方「ルールを定義する」](tutorial.md#3-1-ルールを定義する) の形で用意してください

## 目次

- [外部ファイルによる設定の動的ロード](#外部ファイルによる設定の動的ロード)
- [HLSL / 構文解析ルール](#hlsl--構文解析ルール)
- [型評価とオーバーロードの解析](#型評価とオーバーロードの解析)
- [ShaderLab 構文解析ルール](#shaderlab-構文解析ルール)
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
どのオプション名を読むかはルールごとに自分で決めます。読む側を書かないと、設定ファイルに書いても効きません
（組み込みルールにはオプションを読むものがありません）。

実装は `samples/Shaderlyn.Cookbook/Budget/ParameterCountLimitAnalyzer.cs` にあります。

```csharp
public sealed class ParameterCountLimitAnalyzer(int defaultMaxParameters = 8) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            // 設定が無い場合と、数値として読めない場合は既定値へ倒れる
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
- **読めない値は既定値へ倒します。** 書き間違いは設定の読み込み側が `TOOL0003` で報告します
- **向くのは閾値です。** 禁止する名前の一覧のように項目が増えるものは、コンストラクタで渡したほうが型のまま扱えます
- **ルールの説明に、読むオプション名を書き残してください。** 利用者が設定ファイルに何を書けるかを知る手がかりがそこしかありません

---

## どこまでを報告の対象にするか

この例では `IsReportable(トークン)` で、**利用者が直せるコードだけ**に絞っています。
Unity や外部パッケージのヘッダのコードを指摘しても、利用者には直しようがないためです。
利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) のコードは、取り込むシェーダーの文脈で渡ってきて、報告はヘッダの位置に出ます。

選べるのは 2 つです。

| 書き方                   | 対象                                                         | 報告の位置                                      |
| --------------------- | ---------------------------------------------------------- | ------------------------------------------ |
| `IsReportable(node)`  | 利用者のファイル (このファイルと、利用者が書いて取り込んだヘッダ) に書かれたコードと、それらのファイルが書いたマクロの本体から来たコード | そのコードの位置。マクロの本体から来たコードは本体の位置（`node.GetLocation()` が自動で指す） |
| `IsWrittenHere(node)` | このファイルに直接書かれたコードだけ                                         | そのコードの位置                                   |
組み込みルールは `IsReportable` を使っています。`IsWrittenHere` はエディタの操作のための判定で、
共通のヘッダに書かれたコードを見落とします。同じマクロを何度使っても、報告は本体の 1 か所にまとまります。

```hlsl
#define HELPER_DECL half4 helper() { return undeclared; }
                                  // ^^^^^^^^^^ IsReportable ならここに報告される
HELPER_DECL
```

**Unity や外部パッケージのヘッダのマクロの本体は、どちらでも対象外です。** 実引数として渡したトークンは
呼び出し位置に書かれたものなので、そちらの位置で報告されます。

---

## HLSL / 構文解析ルール

### 1. 禁止マクロの検出

マクロはプリプロセス時に展開され構文木（AST）に残らないため、展開前のトークン系列 `ShaderCompilation.CodeTokens` を参照します。
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

            foreach (HlslSyntaxToken token in compilation.CodeTokens)
            {
                if (token.Kind == HlslSyntaxKind.IdentifierToken && _banned.Contains(token.Text))
                {
                    c.ReportDiagnostic(Rule, token.Span, token.Text);
                }
            }

            foreach (HlslSyntaxToken token in compilation.UserIncludeCodeTokens)
            {
                if (token.Kind == HlslSyntaxKind.IdentifierToken && _banned.Contains(token.Text))
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
internal sealed class BannedFunctionAnalyzer(params string[] banned) : HlslRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
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
internal sealed class NoBranchAnalyzer : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
        => context.RegisterNodeAction<IfStatementSyntax>(
            c => c.ReportDiagnostic(Rule, c.Node.IfKeyword.Span));
}
```

### 4. ネストされた `else if` 連鎖数の制限

`else if` は構文木上「`else` ブロック配下の `if`」として表現されます。多重判定を防ぐため、ネスト連鎖の最先頭ノードでのみ評価を行います。

```csharp
internal sealed class TooManyBranchesAnalyzer(int maxElseCount) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
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

「特定のシンボルが定義されている場合のみ特定の関数呼び出しを禁じる」といった検査です。

```csharp
internal sealed class BannedUnderSymbolAnalyzer(string keyword, string function) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.Target is not IdentifierExpressionSyntax name
                || !string.Equals(name.Name, function, StringComparison.Ordinal))
            {
                return;
            }

            ConditionMap map = c.Compilation.GetConditionMap();
            SymbolCondition here = map.GetCondition(c.Node);

            // 条件が未確定（Unknown）な場合は誤検出を防ぐためスキップ
            if (here.IsUnknown) return;

            // シンボルが無い構成では存在しない = そのシンボルのときだけ存在する
            if (!map.IsPossible(here.And(SymbolCondition.Symbol(keyword, isDefined: false))))
            {
                c.ReportDiagnostic(Rule, name.Span, function, keyword);
            }
        });
}
```

---

## 型評価とオーバーロードの解析

### 6. 引数型の評価およびオーバーロード解析

#### 引数の評価 (`ExpressionTypeBinder`)

型バインダーは `c.Compilation.GetExpressionTypeBinder(c.Program)` から取得して使い回します（インスタンスの再生成はパフォーマンス劣化の原因となります）。

```csharp
internal sealed class ArgumentTypeAnalyzer(string function, int parameter, string expectedType)
    : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
        => context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.Target is not IdentifierExpressionSyntax name
                || !string.Equals(name.Name, function, StringComparison.Ordinal))
            {
                return;
            }

            ImmutableArray<HlslExpressionSyntax> arguments =
                [.. c.Node.Arguments.Select(a => a.Node).OfType<HlslExpressionSyntax>()];

            if (parameter >= arguments.Length) return;

            HlslExpressionSyntax argument = arguments[parameter];
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
    : HlslRuleAnalyzer
{
    private readonly ImmutableArray<string?> _parameters = [.. parameterTypes];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    protected override void InitializeHlsl(HlslAnalysisContext context)
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

## そのほかの実装例

このページに全文を載せていない例も、`samples/Shaderlyn.Cookbook/` に実装とテストがあります。上の 7 例と同じく、コンストラクタに値を渡すだけで使えます。

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
| `ParameterCountLimitAnalyzer` | 関数の仮引数の数（上限は設定ファイルの `options` から読む） |

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

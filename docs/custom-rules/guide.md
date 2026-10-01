# 自作ルールの書き方の手引き

[自作ルールの作り方](tutorial.md) で最初のルールを動かしたあとに読むページです。
ShaderLab を見るルール、ファイル単位の集計、セマンティックモデルの使い方、外部データの受け渡しと、ルールを書くときの原則をまとめています。

## 目次

- [1. ShaderLab を見るルール](#1-shaderlab-を見るルール)
- [2. ファイル単位の集計](#2-ファイル単位の集計)
- [3. セマンティックモデルを使う](#3-セマンティックモデルを使う)
- [4. 外部データの受け渡し](#4-外部データの受け渡し)
  - [閾値ひとつなら設定ファイルのオプションで足ります](#閾値ひとつなら設定ファイルのオプションで足ります)
- [5. ルールを書くときの原則](#5-ルールを書くときの原則)
  - [言語の規則をルールの側に書き写さない](#言語の規則をルールの側に書き写さない)

---

## 1. ShaderLab を見るルール

`Properties` や `Tags`、`Pass` の構造を見るルールは、`DiagnosticAnalyzer` を直接継承します。

```csharp
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

public sealed class PassNeedsNameAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MyRules.PassNeedsName];

    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<PassSyntax>(c =>
        {
            if (!HasName(c.Node))
            {
                c.ReportDiagnostic(MyRules.PassNeedsName);
            }
        });
}
```

`RegisterNodeAction` が歩くのは ShaderLab の構文木だけです。
埋め込み HLSL はコードブロックごとに別の構文木になっており、マクロ展開と `#include` の解決を経て
初めて出来上がるので、ShaderLab の木からは辿れません。
HLSL を見たいときは [3 章](tutorial.md#3-ルールを書く)の `HlslRuleAnalyzer` を使ってください。

---

## 2. ファイル単位の集計

「宣言はあるが、どこからも参照されていない」のような、集めてから判定するルールの書き方です。

```csharp
public override void Initialize(AnalysisContext context)
    => context.RegisterAnalysisStartAction(start =>
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        HashSet<string> used = new(StringComparer.Ordinal);

        start.RegisterNodeAction<PropertySyntax>(c => declared.Add(c.Node.Name));
        start.RegisterNodeAction<IdentifierExpressionSyntax>(c => used.Add(c.Node.Name));

        start.RegisterAnalysisEndAction(end =>
        {
            foreach (string name in declared.Except(used))
            {
                end.ReportDiagnostic(MyRules.Unused, ..., name);
            }
        });
    });
```

状態をアナライザのフィールドに置いてはいけません。インスタンスは全ファイルで共有され、
複数ファイルが並列に解析されるため、前のファイルの情報が混ざります。
`RegisterAnalysisStartAction` で作った変数はファイル 1 つ分のスコープを持ちます。

Roslyn の `RegisterCompilationStartAction` / `RegisterSymbolStartAction` と同じ考え方ですが、
入れ子の段数が 1 つだけで、どのスコープにいるかが型で分かります。

---

## 3. セマンティックモデルを使う

`Properties` と uniform の対応のような、層をまたぐ情報が要るときはセマンティックモデルを引きます。
`HlslRuleAnalyzer` を使っていれば `c.Compilation` から直接引けます。

```csharp
context.RegisterSyntaxTreeAction(c =>
{
    if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation)
    {
        return;   // 意味解析が行われていない
    }

    ...
});
```

| `ShaderCompilation`                                 | 引けるもの                                      |
| --------------------------------------------------- | ------------------------------------------ |
| `Properties`                                        | `Properties` ブロックの宣言                       |
| `Programs`                                          | コードブロックごとの解析結果                             |
| `TryGetUniform(name, out _)`                        | uniform の宣言                                |
| `HasCompleteDependencies`                           | `#include` をすべて解決できたか                      |
| `GetConditionMap()`                                 | ノードがどのシンボル構成で存在するか                         |
| `EnumerateRuleNodes()`                              | 検査のために歩くノード。既定の構成と各バリアントの木のノードを、属する木とともに返す |
| `GetEffectiveCondition(node, program)`              | そのノードが存在する条件。その木を解析した構成も掛け合わせる             |
| `TryEnumerateConfigurations(nodes, program, out _)` | 構成ごとに存在するノード。数えるルールはこれで数える                 |

`HlslRuleAnalyzer` を使っていれば、この歩き方は基底クラスが行います。
ノードは既定の構成の木とバリアントの木の両方から、それぞれの文脈で渡ってきます (`c.Program`)。
同じ位置のコードは `Pass` と構成の数だけ渡りますが、同じ内容の報告は 1 度にまとまります。

**ノードを数えたり合計したりするルールは、構成ごとに数えてください。**
両方の分岐を並べた木には、`#ifdef _A` と `#else` のコードが並んでいます。そのまま数えると、どの構成でも起きない数を報告します
([API リファレンス](api-reference.md#数えるルールは構成ごとに数える))。

識別子がどの変数を指すかは、名前で宣言を探さずに
`c.Compilation.GetExpressionTypeBinder(c.Program).ResolveName(identifier)` で引いてください。
別の関数に同じ名前の局所変数があっても取り違えず、決められない場合は `Undecidable` を返します
([ルール API](api-reference.md#resolvename))。
使う側と宣言の側が別の構成の木に載ることがあるので、1 つの木で `NotFound` だったことだけを根拠に報告しないでください。

---

## 4. 外部データの受け渡し

禁止する名前の一覧のようなデータはコードの外に置けます。このツールは書式を決めません。
アナライザは普通のクラスなので、コンストラクタで受け取ります。

```csharp
public sealed class BannedFunctionAnalyzer(params string[] banned) : HlslRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];
    ...
}
```

自分の CLI の中で読み、渡します。

```csharp
string[] banned = File.ReadAllLines("banned-functions.txt")
    .Select(line => line.Trim())
    .Where(line => line.Length > 0 && !line.StartsWith('#'))
    .ToArray();

ImmutableArray<DiagnosticAnalyzer> analyzers =
[
    .. BuiltInAnalyzers.All,
    new BannedFunctionAnalyzer(banned),
];

return (int)await Shaderlyn.Cli.Program.RunAsync(args, analyzers, Console.Out, Console.Error);
```

YAML なら `YamlDotNet`、CSV なら `CsvHelper`、JSON なら `System.Text.Json` がそのまま使えます。
INI なら数行の自作パーサでも足ります。

可変の状態は持てませんが、読み取り専用のデータは持って構いません。
全ファイルで共有され、並列に読まれるだけだからです。

### 閾値ひとつなら設定ファイルのオプションで足ります

`.shaderlyn.yaml` の `rules:` に書いたオプションは、ルールから引けます。
一覧やテーブルは外部ファイルにしてください。

```yaml
rules:
  MY0001:
    options: { maxValue: 64 }
```

```csharp
int maximum = c.Options.GetIntOption("MY0001", "maxValue", defaultValue: 64);
```

---

## 5. ルールを書くときの原則

判断できないものを誤りとして報告してはいけません。これが最も重要な原則です。
誤検出を出すルールは、最終的にツールごと無効化されます。

- `IsMissing` が真のトークンには報告しないでください。構文エラーとして既に報告済みであり、
  重ねて指摘すると出力が読めなくなります
- 「無いこと」を根拠にする指摘は、知識が完全であることを確かめてから出してください。
  `HasCompleteDependencies` が偽なら解析ができていません。
- `GetModel<ShaderCompilation>()` が `null` を返したら報告しないでください。
- 未知の命令名・タグ名・属性名を誤りにしないでください。Unity のバージョンで増えます
- `Cull [_Cull]` のようなプロパティ参照は検証しないでください。実際の値はマテリアル側で決まります
- 波線はできるだけ狭くしてください。ノード全体ではなく問題のトークンだけを指してください
- 検査を見送ったなら、そのことを報告する
- `WithSuggestedReplacement` は、直し方が一意に決まるときだけ添えてください。
  エディタが 1 回の操作で適用できてしまうので、推測で添えると誤りが広がります
- `ResolveCall` の `Declaration` が `null` でも「呼ばれていない」とは限りません。
  `Ambiguous` と `Unknown` は「調べたが決められなかった」場合で、
  指摘の根拠にしてよいのは `Resolved` だけです

### 言語の規則をルールの側に書き写さない

文字列を切り刻んで求めたものは、書き方が増えたときに気づけないまま取りこぼします。
同じ判定をする仕組みが用意してあるので、そちらを使ってください。

- リテラルの値は `HlslLiteral` で読んでください。`int.TryParse(token.Text)` では `0x10` も `2u` も読めず、
  「数字で書かれていない」として検査が素通りします
- 配列の長さは `TryGetArrayLength` で読んでください。多次元の場合も積で返ります
- 型名の分解は `HlslTypeClassifier` で行ってください。`Texture2D` の `2D` を成分数と読み違えません
- 式の型は `GetExpressionTypeBinder(program)` が返すものから求めてください。
  `ExpressionTypeBinder` を自分で `new` すると、ノードごとに宣言と構造体のフィールドを
  集め直すため、走査が二乗になります

---

## 関連

- [自作ルールの作り方](tutorial.md) — プロジェクトの用意から、CLI とエディタで動かすまで
- [ルールの実例集](cookbook.md) — 写して直せば動く例
- [ルール API リファレンス](api-reference.md) — 型とメンバーの一覧
- [トラブルシューティング「自作ルール」](../guide/troubleshooting.md#自作ルール) — 指摘が出ない、TOOL0001 / 0005 / 0006 が出る

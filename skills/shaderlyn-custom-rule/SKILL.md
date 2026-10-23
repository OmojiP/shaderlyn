---
name: shaderlyn-custom-rule
description: Shaderlyn (シェーダーの静的解析ツール) の自作ルール (C# のアナライザ) を作る・直すときに使う。シェーダーのコードを検査するルールを足したい、Shaderlyn に指摘を増やしたい、DiagnosticAnalyzer / ShaderRuleAnalyzer を書きたい、と頼まれたら、コードを書く前に必ず読む。
---

# Shaderlyn の自作ルールを作る

Shaderlyn のルールは、構文木 (AST) とセマンティックモデルを見て判定する C# のクラスです。
この手順を飛ばすと、組み込みにあるルールを作り直したり、ソースを文字列で調べる壊れやすいルールを書いたりします。
次の順に進めてください。

## 1. 組み込みのルールで足りないかを先に確かめる

コードを書く前に、頼まれた検査がすでにあるかを調べます。

1. `shaderlyn --list-rules` を実行し、ID・既定の重要度・内容の一覧を読む
2. 近いものがあれば、そのルールの説明 (`https://github.com/OmojiP/shaderlyn/blob/main/docs/rules/<ID>.md`) を読む。
   何を報告し、何を報告しないかが書いてある
3. 判断した結果を、実装の前に利用者へ伝える
   - **すでにある**: 実装しない。重要度を変えたいだけなら、設定ファイル `.shaderlyn.yaml` の `rules:` で変えられる
     (`HL0310: warning`、`/SL.*/: error` のような正規表現も書ける)
   - **一部だけある**: 組み込みのルールが何をカバーしていて、自作ルールで何を足すのかを示し、確認を取る
   - **無い**: 次へ進む

組み込みの ID の接頭辞 (`HL` `SL` `UN` `URP` `UE` `TOOL`) は自作ルールに使わないでください。
プロジェクト固有の接頭辞 (`MY0001` のような英大文字 + 4 桁) を、利用者と決めてから使います。

## 2. 何を見て判定するかを決める (文字列で調べない)

**ソースのテキストを正規表現や `Contains` / `IndexOf` / `Split` で調べてはいけません。**
コメント・文字列・マクロ展開・`#include` の先・`#if` で外れたコードを区別できず、誤検出と見逃しを出します。
判定には必ず次のどれかを使います。

| 見たいもの | 使うもの |
| --- | --- |
| HLSL の文・式・宣言 | `ShaderRuleAnalyzer` の `RegisterNodeAction<TNode>` (`InvocationExpressionSyntax`、`TypeSyntax` など) |
| ShaderLab (`Properties`、`Tags`、`Pass`) | `DiagnosticAnalyzer` の `RegisterNodeAction<PassSyntax>` など |
| マクロ (展開されて AST に残らない) | `ShaderCompilation.CodeTokens` / `UserIncludeCodeTokens` のトークン (`Kind` と `Text`) |
| 名前が何を指すか | `compilation.GetExpressionTypeBinder(program).ResolveName(identifier)` |
| 呼んでいる関数・オーバーロード | `ResolveCall`。根拠にしてよいのは `Resolved` だけ |
| 式の型 | `GetExpressionTypeBinder(program)` の評価結果。自分で `new` しない |
| 型名の分解 (`float3`、`Texture2D`) | `compilation.TypeSystem` (`ShaderTypeSystem`)、`HlslTypeClassifier` |
| リテラルの値 (`0x10`、`2u`) | `ShaderLiteral`。`int.TryParse(token.Text)` は使わない |
| 配列の長さ | `TryGetArrayLength` |
| 組み込み関数か | `HlslIntrinsics` |
| どの構成 (`#pragma multi_compile` など) で存在するか | `GetEffectiveCondition(node, program)`、`ConditionMap`、`TryEnumerateConfigurations` |
| 閾値などの設定値 | `c.Options.GetIntOption("MY0001", "maxValue", defaultValue: 64)` |

どのノード型に登録すればよいか分からないときは、推測で書かずに
`shaderlyn <シェーダー> --inspect tree.html` で実際の構文木を見て決めます (利用者に頼んでもよい)。

## 3. 書く

最初に次のページを読みます。手元に Shaderlyn のリポジトリがあれば `docs/custom-rules/` の同じファイルを読んでください。

- 作り方の手順 (プロジェクト、定義、アナライザ、テスト、CLI への組み込み):
  https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/tutorial.md
- 書くときの原則と、セマンティックモデルの使い方:
  https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/guide.md
- 写して直せる実例 (禁止関数、禁止マクロ、構成ごとの制限、オーバーロード、ShaderLab のタグ):
  https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/cookbook.md
- 型とメンバーの一覧:
  https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/api-reference.md

実例集に近いものがあれば、それを元にしてください。

守ること:

- 報告するルールの `DiagnosticDescriptor` を `SupportedDiagnostics` に載せる (漏れると TOOL0005)
- メッセージの `{0}` の数と、渡す引数の数を合わせる (合わないと TOOL0006)
- アナライザのフィールドに可変の状態を置かない。インスタンスは全ファイルで共有され、並列に動く。
  ファイル単位の集計は `RegisterAnalysisStartAction` の中で変数を作る
- 判断できないものは報告しない。誤検出を出すルールは、ツールごと無効にされる
  - `GetModel<ShaderCompilation>()` が `null`、`HasCompleteDependencies` が偽なら、「無いこと」を根拠に報告しない
  - `IsMissing` のトークン (構文エラーで補ったもの) には報告しない
  - `ResolveName` の `Undecidable`、`ResolveCall` の `Ambiguous` / `Unknown` は報告の根拠にしない
- 数える・合計するルールは、構成ごとに数える (`TryEnumerateConfigurations`)。両方の分岐を並べた木をそのまま数えない
- 報告の位置は問題のトークンだけにする。ノード全体に波線を引かない

## 4. テストを書いて確かめる

`Shaderlyn.Testing` の `ShaderRuleVerifier` でテストを書きます (チュートリアルの「テストを書く」)。
次の 3 種類を必ず入れてください。

- 報告すべきコードで報告する
- 似ているが正しいコードで報告しない (コメントや文字列の中、別の名前の関数、`#if` で外れた分岐)
- `#pragma multi_compile` などの構成で、ある構成にだけ問題があるコード (条件付きで報告するか、報告しないか)

テストが通ったら、利用者の実際のシェーダーに CLI でかけて、誤検出が無いかを一緒に確かめます。

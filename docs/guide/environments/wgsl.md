# WGSL

WGSL のファイル (`.wgsl`) は、設定ファイルが無くても、ほかの環境の設定ファイルの下にあっても、WGSL として解析します。VS Code 拡張も同じです。

**C 系の言語に共通のルール (`CF` で始まるもの) と、WGSL にしか無いルール (`WG` で始まるもの。[一覧](../../rules/README.md#wgsl)) で検査します。**
WG のルールは、naga 30 (wgpu の WGSL のコンパイラ) がエラーにするものを、naga と同じ行で報告します。

- 構文は WGSL の仕様どおりに読みます。属性 (`@group(0)`・`@vertex`)、`var<storage, read_write>`・`let`・`const`・`override`、`alias`、構造体、
  `loop` と `continuing`・`break if`、`case` の値を並べた `switch`、`_ =`、入れ子のコメントを読みます。型引数を囲む `<` と比較演算子の `<` は、仕様と同じ手順で見分けます
- 型は WGSL の決まりで判断します。型を書かない数 (`1`・`1.0`) は抽象的な数で、具体的な型に変換できます。それ以外の暗黙の変換は無いので、
  `i32` を `f32` の変数に代入すると [CF0410](../../rules/CF0410.md) になります。型を書かない `let` / `var` / `override` は初期値の型 (抽象的な数は `i32` か `f32`)、
  `const` は初期値の型として検査します
- 関数の外の名前は、宣言より前でも使えます (WGSL の決まり)
- 組み込み関数と型は naga 30 のものを知っています。`enable` の必要な型と機能 (`f16`・`binding_array`・光線の照会など) は、`enable` が無ければ [WG0001](../../rules/WG0001.md) になります
- 定数式を求めて、型に収まらない数・シフトの量・配列の大きさ・範囲の外の添字・成り立たない `const_assert` を報告します
- エントリポイントの入出力 (`@location` / `@builtin`)、ステージで使えない操作 (頂点シェーダーの `discard` など)、構造体のメモリの配置 (`@align`・`@size`・uniform の配列の間隔) も検査します

naga の誤りの試験 (`wgsl_errors.rs` の 272 の断片) と、naga・wgpu・webgpu-samples・Bevy の WGSL で、naga と同じ誤りを同じ行で報告し、
naga が通すファイルに誤りと警告を報告しないことを確認しています。

VS Code 拡張の説明 (ホバー)・定義へ移動・補完・引数の案内も、WGSL の型と組み込み関数で答えます。

- WGSL にはプリプロセッサも取り込みもありません。1 つのファイルを 1 つのシェーダーモジュールとして解析します
- Bevy の `#import` / `#define_import_path` / `#ifdef` (naga_oil) は読みません。`#` で始まる行は構文の誤りになります
- 1 つのファイルに頂点・フラグメント・計算のエントリポイントを並べてかまいません。どれを使うかはパイプラインを作るときに決まるので、エントリポイントを選ぶ設定はありません

## 設定ファイルの例

設定ファイルは要りません。ルールの重要度を変えるときだけ書きます。

```yaml
version: 1
environment: wgsl
rules:
  WG0003: none   # naga より新しい WebGPU の実装の拡張を使うなら
```

テンプレートとサンプルは [samples/environments/wgsl](../../../samples/environments/wgsl/) にあります。

## naga と違う所

- naga は最初の誤りで止まりますが、Shaderlyn はファイルの誤りをすべて報告します
- 均一性 (uniformity) の解析はしません。均一でない制御の中の `textureSample` などは報告しません
- 演算のあふれは、naga と同じく `const` の初期値の中だけを報告します

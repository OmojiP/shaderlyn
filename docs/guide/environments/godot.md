# Godot

Godot シェーダー言語のファイル (`.gdshader` / `.gdshaderinc`) は、設定ファイルが無くても、ほかの環境の設定ファイルの下にあっても、Godot 4 のシェーダーとして解析します。VS Code 拡張も同じです。

**C 系の言語に共通のルール (`CF` で始まるもの) と、Godot シェーダー言語にしか無いルール (`GD` で始まるもの。[一覧](../../rules/README.md#godot)) で検査します。**
GD のルールは、Godot 4.7.2 のシェーダーのコンパイラがエラーにするものを、Godot と同じ行で報告します。

- `shader_type` / `render_mode` / `stencil_mode` / `group_uniforms`、uniform のヒント (`: source_color, hint_range(0.0, 1.0)`)、`instance uniform` / `global uniform`、
  `varying` と補間・精度の修飾子を読みます。Godot の文法で書けない形 (関数の外の `const` でない変数、配列の配列など) は構文の誤り ([CF0001](../../rules/CF0001.md)) になります
- 型は Godot の決まりで判断します。暗黙の変換は無い (`float a = 1;` は誤り) ので、Godot と同じく、関数の呼び出しの実引数に書いた整数のリテラル (`f(0)`、`vec2(1, 2)`) だけを仮引数の型へ変換します
- 組み込みの変数と定数 (`ALBEDO`、`TIME`、`PI` など) と組み込み関数は、シェーダーの種類 (`shader_type`) ごとに Godot 4.7.2 の表を参照します。
  ステージ関数 (`vertex` / `fragment` / `light` など) の中ではその関数のものが、利用者の関数の中ではどの関数でも使えるもの (`TIME` など) と定数が、関数の外では定数だけが使えます
- 演算子と組み込み関数の型の組、ステージで使えない組み込み (vertex の中の `dFdx` など)、varying の書き込み、サンプラーの仮引数などを検査します
- プリプロセッサは Godot の規則で読みます (マクロの定義し直しは誤り、`#include` は `res://` と相対パス)

Godot のエンジンの誤りの例 (Godot のソースの誤りごとに作った 514 のファイル)、godot-demo-projects・godot-shaders のシェーダーで、
Godot と同じ誤りを同じ行で報告し、Godot が通すファイルに誤りと警告を報告しないことを確かめています。

VS Code 拡張の説明 (ホバー)・定義へ移動・補完・引数の案内も、Godot の型と組み込みで答えます。`render_mode` のモードと uniform のヒントも補完します。

## プロジェクトとレンダラー

- 解析するファイルから上へ辿って最初に見つけた `project.godot` のフォルダーを `res://` の根にします。設定ファイルの [`godot: project`](../configuration.md#godot) でも指定できます
- レンダラー (Forward+ / Mobile / Compatibility) は `project.godot` の `rendering/renderer/rendering_method` から読みます。[`godot: renderer`](../configuration.md#godot) で変えられます。
  レンダラーで、`CURRENT_RENDERER` の値、Compatibility で使えない組み込み関数 ([GD0001](../../rules/GD0001.md))、varying の数の上限 ([GD0104](../../rules/GD0104.md)) が変わります

## 設定ファイルの例

```yaml
version: 1
environment: godot
godot:
  renderer: gl_compatibility   # Web 向けに Compatibility で検査する
```

キーの説明は [設定](../configuration.md#godot) に、テンプレートとサンプルは [samples/environments/godot](../../../samples/environments/godot/) にあります。

## Godot と違う所

- Godot は最初の誤りで止まりますが、Shaderlyn はファイルの誤りをすべて報告します
- `project.godot` の書き出し先ごとのレンダラー (`rendering_method.mobile` / `rendering_method.web`) は読みません

# Slang

Slang のファイル (`.slang`) は、設定ファイルが無くても、ほかの環境の設定ファイルの下にあっても、Slang として解析します。VS Code 拡張も同じです。

**Slang は開発中です。** 構文の誤りと、C 系の言語に共通のルール (`CF` で始まるもの) のうち Slang で確かめたもの (宣言されていない名前、関数の引数の数、型の変換、構造体のメンバーなど) を報告します。
slangc が通すファイルには誤りを出さないようにしてあります。slangc の誤りのうち、まだ見つけられないもの (ジェネリックとインターフェイスの制約、多重定義の選び方など) があります。

- 構文は HLSL の構文に、Slang の `import` / `module`、`func`・`let`・`var`、ジェネリック (`<T : IFoo>`、`where`)、`interface`・`extension`・`typealias`・`associatedtype`・`property`・`__init`・`__subscript`、
  ポインター (`T*`)、`is` / `as`、ラムダ式、`try` / `catch` / `throw` / `defer`、GLSL の書き方 (`layout(…)`) を足したものとして読みます
- 型は HLSL 2021 の型の決まりに、Slang の標準ライブラリの名前 (slang 2026.19 のソースから作った表) を足したもので判断します。型を書かない `let` / `var` は、初期値の型として検査します
- 変数・仮引数・戻り値・フィールドの型と構造体の基底に書いた、どこにも宣言されていない型の名前を [CF0310](../../rules/CF0310.md) にします。
  名前空間などで修飾した名前 (`ns::T`・`T.Differential`)、ジェネリックの制約 (`<T : IFoo>`)、`throws` の型はまだ見ません
- slangc に合わせて、変数の初期値の中では、宣言しているその変数はまだ見えません (`int e = e;` の右の `e` は外側の `e`)。`for` の初期化の変数は、ループの外では見えません
- `import a.b;` のモジュール (`a/b.slang`) を、ファイルのフォルダーと [`include-paths`](../configuration.md) から探し、その宣言を使います。
  見つからないモジュールは [CF0021](../../rules/CF0021.md) になります。slangc に入っている標準のモジュール (`slang.neural` など) は探しません
- slangc に合わせて、構造体の空の `{ }`、64 ビットに広がる整数のリテラル、局所変数で仮引数を隠すことは報告しません。
  値を返さない経路 ([CF0501](../../rules/CF0501.md)) と、ベクトルを含んで成分の足りない値の作成 (`float4(v.xy, 1)`) は警告にします
- 成分を減らす暗黙の変換 (`float3 t = v;` の `v` が `float4`、行列からベクトル) は、HLSL と違って slangc が誤りにするので、
  [CF0410](../../rules/CF0410.md)・[CF0401](../../rules/CF0401.md)・[CF0411](../../rules/CF0411.md) にします
- 関数の重複 ([CF0314](../../rules/CF0314.md)) と本体の無い関数 ([CF0311](../../rules/CF0311.md)) は、ジェネリックの制約とリンクの指定を読むまで検査しません

slang の例・SaschaWillems の Vulkan の例・Falcor の Slang で、slangc が通すファイルに誤りを出さないことを確かめています。

VS Code 拡張の説明 (ホバー)・定義へ移動・補完・引数の案内は、`import` したモジュールの関数と構造体、標準ライブラリの型 (`CoopVec` など) も使います。

## 設定ファイルの例

```yaml
version: 1
environment: slang
include-paths:
  - shaders/modules   # import するモジュールを探すフォルダー
symbols:
  - name: USE_FOG     # #ifdef USE_FOG の両方の分岐を読む
```

コンパイル先は [`slang: target`](../configuration.md#slang) に書きます (CPU と CUDA では、資源をバッファーに入れられるなど、決まりが違います)。
構成の軸は GLSL と同じく `symbols:` に書きます。エントリポイントはコンパイルのときに選ぶので、`units:` は読みません。
テンプレートとサンプルは [samples/environments/slang](../../../samples/environments/slang/) にあります。

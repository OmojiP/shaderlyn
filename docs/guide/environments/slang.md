# Slang

Slang のファイル (`.slang`) は、設定ファイルが無くても、ほかの環境の設定ファイルの下にあっても、Slang として解析します。VS Code 拡張も同じです。

**C 系の言語に共通のルール (`CF` で始まるもの) のうち Slang で確かめたものと、Slang にしか無いルール (`SG` で始まるもの。[一覧](../../rules/README.md#slang)) で検査します。**
SG のルールは、slangc 2026.19 がエラーにするものを、slangc と同じ行で報告します。

- 構文は HLSL の構文に、Slang の `import` / `module`、`func`・`let`・`var`、ジェネリック (`<T : IFoo>`、`where`)、`interface`・`extension`・`typealias`・`associatedtype`・`property`・`__init`・`__subscript`、
  ポインター (`T*`)、`is` / `as`、ラムダ式、`try` / `catch` / `throw` / `defer`、GLSL の書き方 (`layout(…)`) を追加したものとして読みます
- 型は HLSL 2021 の型の決まりに、Slang の標準ライブラリの名前 (slang 2026.19 のソースから作った表) を追加したもので判断します。型を書かない `let` / `var` は、初期値の型として検査します
- 変数・仮引数・戻り値・フィールドの型、構造体の基底、ジェネリックの制約 (`<T : IFoo>`・`where T : IFoo`)、`throws` の型に書いた、どこにも宣言されていない型の名前を [CF0310](../../rules/CF0310.md) にします。
  名前空間などで修飾した名前 (`ns::T`・`T.Differential`) はまだ見ません
- 列挙子は、`[UnscopedEnum]` を付けた列挙型のものだけを修飾せずに書けるものとします (`Fruit.Apple`)。slangc に `-unscoped-enum` を渡すなら、[`slang: unscoped-enums`](../configuration.md#slang) を `true` にしてください
- 実験中のモジュール (`slang.neural` など) の import は、slangc の既定と同じく誤り ([SG0362](../../rules/SG0362.md)) にします。slangc に `-experimental-feature` を渡すなら、[`slang: experimental-features`](../configuration.md#slang) を `true` にしてください
- ジェネリックのエントリポイントは、型引数を決めない slangc と同じく誤り ([SG0358](../../rules/SG0358.md)) にします。slangc の `-specialize` か `-entry main<Foo>`、Slang の API (`specialize`) で型引数を決めるなら、[`slang: generic-entry-points`](../configuration.md#slang) を `true` にしてください
- slangc に合わせて、変数の初期値の中では、宣言しているその変数はまだ見えません (`int e = e;` の右の `e` は外側の `e`)。`for` の初期化の変数は、ループの外では見えません。`if (let a = x)` の `a` は、then の側でだけ見えます
- `import a.b;` のモジュール (`a/b.slang`) を、ファイルのフォルダーと [`include-paths`](../configuration.md) から探し、その宣言を使います。
  見つからないモジュールは [CF0021](../../rules/CF0021.md) になります。slangc に入っている標準のモジュール (`slang.neural` など) は探しません
- slangc に合わせて、構造体の空の `{ }`、64 ビットに広がる整数のリテラル、局所変数で仮引数を隠すことは報告しません。
  値を返さない経路 ([CF0501](../../rules/CF0501.md)) と、ベクトルを含んで成分の足りない値の作成 (`float4(v.xy, 1)`) は警告にします
- 成分を減らす暗黙の変換 (`float3 t = v;` の `v` が `float4`、行列からベクトル) は、HLSL と違って slangc が誤りにするので、
  [CF0410](../../rules/CF0410.md)・[CF0401](../../rules/CF0401.md)・[CF0411](../../rules/CF0411.md) にします
- 関数の重複 ([CF0314](../../rules/CF0314.md)) と本体の無い関数 ([CF0311](../../rules/CF0311.md)) は、ジェネリックの制約とリンクの指定を読むまで検査しません

slang の試験 (`tests/` の slangc が誤りにするものと通すもの)・slang の例・SaschaWillems の Vulkan の例・Falcor の Slang で、slangc と同じ誤りを同じ行で報告し、
slangc が通すファイルに誤りを報告しないことを確かめています。

### まだ見つけられないもの

slangc の誤りのうち、次のものはまだ報告しません。

- ジェネリックとインターフェイスの型の検査のうち、型引数の推論、インターフェイスの要件、多重定義の選び方、存在型の型引数 (制約を満たさない型引数は SG0349 で報告します)
- 自動微分の多くの検査 (`fwd_diff`・`bwd_diff` の型、微分の関数の形の合わせ方など。微分する関数のループの回数 (SG0350) と微分の属性の名前は報告します)
- コンパイルのときに選ぶエントリポイント・ステージ・能力 (capability) に依る誤りと、動的なディスパッチ (`-conformance`) に依る誤り
- コード生成と最適化の段の誤り (コンパイル先が扱えない命令、ループの展開など)
- 解析するファイル自身を `implementing` / `__include` で分けたモジュールの可視性 (取り込んだモジュールを分けて書いたものは見ます)
- 構文の誤りを含む文から、その関数の終わりまでの誤り (ほかの関数は検査します。構文の誤りが関数の外にあるときは、宣言が無いことを根拠にする検査を止めます。[CF0101](../../rules/CF0101.md))
- そのほかの個別の意味の検査 (多重定義の選び方、型の食い違いの一部など)

slang の誤りの試験 (slangc が誤りにする 965 ファイル、2,011 行) のうち、今は 1,194 行を slangc と同じ行で報告します (記録した slangc の引数を設定ファイルにして比べると 1,206 行。`tools/oracles/compare.mjs --use-args`)。

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

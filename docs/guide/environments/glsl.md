# GLSL

GLSL のファイル (`.vert` / `.frag` / `.comp` / `.geom` / `.tesc` / `.tese` / `.task`、レイトレーシングの `.rgen` などと、`.glsl`) は、
設定ファイルが無くても、ほかの環境の設定ファイルの下にあっても、GLSL として解析します。VS Code 拡張も同じです。

**C 系の言語に共通のルール (`CF` で始まるもの) と、GLSL にしか無いルール (`GL` で始まるもの。[一覧](../../rules/README.md)) で検査します。** HLSL にしか無いルール (`HL` で始まるもの) は出しません。
GL のルールは、glslang 16.6.0 がエラーにするもの (その `#version` や拡張で使えない機能、layout の誤りなど) を、glslang と同じ行で報告します。

- glslang 16.6.0 が読む書き方は、拡張のもの (協調行列の `coopmat<…>`、長いベクトルの `vector<T, N>`、`GL_EXT_spirv_intrinsics` など) も含めてすべて読みます。
  語がキーワードかどうかは、glslang と同じく `#version` と有効な拡張で決まります。たとえば、4.00 より前の `sample` は名前に使えます。予約語 (`class` など) を名前に使うと [CF0001](../../rules/CF0001.md) になります。
- 型は GLSL の決まりで判断します。暗黙の変換が使えるかどうかは、glslang と同じく `#version` と有効な拡張で変わります。ES の 3.10 より前と GLSL 1.10 では暗黙の変換は使えません。GLSL 4.00 以降では、`int` → `uint` → `float` → `double` の順に変換できます。`GL_ARB_gpu_shader5` や `GL_EXT_shader_explicit_arithmetic_types` などの拡張でも、使える変換が増えます。
  成分数の異なる値は変換できません (`vec3 c = 1.0;` と `vec3 c = vec4(1.0);` はどちらも誤りです)。行列を含む `*` は行列の積です。
- glslang から作った一覧を使い、ファイルのステージと `#version` で使える組み込み関数と変数 (`texture`、`gl_FragCoord` など) を解析対象にします。
  頂点シェーダーで `gl_FragCoord` を使うと、宣言されていない名前 ([CF0310](../../rules/CF0310.md)) になります
- Vulkan 向けか OpenGL 向けかは、設定ファイルの [`glsl: target`](../configuration.md#glsl) で決めます。書かなければ、Vulkan と OpenGL の組み込みはどちらも使えるものとします
- ステージのファイルのエントリポイントは `main` です。無ければ [CF0301](../../rules/CF0301.md) になります
- 取り込まれる断片 (`.glsl`) を単体で解析すると、取り込む側で宣言した名前が分からないため、宣言が無いことを根拠にする検査を見送ります
  ([CF0101](../../rules/CF0101.md) で伝えます)。取り込む側のステージのファイルを解析すれば、断片の中も検査します。
  ステージの拡張子でないファイルはすべて断片として読むので、単体のシェーダーを `.glsl` で置いているなら、`shadow.vert.glsl` のようにステージの拡張子を付けると検査されます

GLSL の公開されたシェーダー集 (Vulkan の SaschaWillems・Vulkan-Samples、GLSL ES の ARM OpenGL ES SDK、約 790 ファイル) で、誤りと警告を出さないことを確かめています。

VS Code 拡張の説明 (ホバー)・定義へ移動・補完・引数の案内も、GLSL の型と組み込みで答えます (組み込み関数の説明と引数の案内には、glslang の表の多重定義を並べます)。

- `#include` (`GL_GOOGLE_include_directive`) は、取り込む側のファイルからの相対パスと、設定ファイルの `include-paths` で探します。
  HLSL と同じく、解析対象の外のファイルは読みません
- `.mesh` (メッシュシェーダー) は Unity のメッシュのアセットと同じ拡張子なので、`environment: glsl` を指定したときだけ読みます
- 取り込まれる側の `.glsl` も、フォルダーを指定すると単体で解析します
- ステージはファイルの拡張子で決めます (glslang と同じ)。`shadow.vert.glsl` のように `.glsl` の前にステージの拡張子があれば、そのステージです
- 言語のバージョンはファイルの `#version` で決め、glslang と同じマクロを定義します (`__VERSION__`、ES の `GL_ES`、1.50 以降の `GL_core_profile`、
  glslang が読める拡張の名前のマクロ `GL_EXT_ray_tracing` など、デスクトップのステージのマクロ `GL_FRAGMENT_SHADER` など)。
  `#version` の無いステージのファイルは、glslang と同じく ES の 100 として読みます。`#version` の無い `.glsl` は、取り込む側で決まるのでバージョンを決めません
- `VULKAN` と `GL_SPIRV` は、[`glsl: target`](../configuration.md#glsl) と `glsl: spirv` で決まります (glslang の `-V` / `-G`)。`defines:` に書いても同じです
- `#if` で切り替える分岐は、設定ファイルの `symbols:` に書くと両方を読みます (書き方は[汎用の HLSL](hlsl.md) と同じ)

## 設定ファイルの例

```yaml
version: 1
environment: glsl
include-paths:
  - shaders/include
glsl:
  target: vulkan1.3  # glslang -V --target-env vulkan1.3 でコンパイルするなら
symbols:
  - name: USE_FOG    # #ifdef USE_FOG の両方の分岐を読む
```

キーの説明は [設定](../configuration.md#glsl) に、テンプレートとサンプルは [samples/environments/glsl](../../../samples/environments/glsl/) にあります。

## glslang と違う所

検査は glslang 16.6.0 と同じ結果になるようにしていますが、次のものは意図して違えています。

- 値を返さない経路がある関数 ([CF0501](../../rules/CF0501.md)) を警告にします。glslang は何も言いません
- マクロを一段通した実引数の中で空白を挟んだ字句を、`##` で連結しません。glslang は
  `#define S e1 D` を `#define Q(a, b) P(a, b)` (`P` は `a##b`) に渡した `Q(imag, S)` を `image1D` にしますが、
  C の規則では `image1 D` です。OpenGL のドライバーのコンパイラは C の規則で読むので、Shaderlyn も C の規則に従います

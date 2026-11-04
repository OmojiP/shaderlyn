# 汎用の HLSL (DXC)

`environment: hlsl` で、DXC で直接コンパイルする HLSL (DirectX 12 のエンジンなど) を解析します。フォルダーを指定すると `.hlsl` を探します。
`.hlsli` は取り込まれる断片なので、明示的に指定した場合に限り解析します。

ほかの環境と共通の使い方は [CLI の使い方](../cli-usage.md)、設定ファイルのキーは [設定](../configuration.md#hlsl) にあります。

## 設定ファイル

コンパイル単位 (どのファイルを、どのエントリポイント・ターゲットでコンパイルするか) と、構成の軸 (`-D` の組み合わせ) を設定ファイルに書きます。

```yaml
version: 1
environment: hlsl

hlsl:
  version: 2021            # DXC の -HV。既定は 2021

symbols:
  - name: QUALITY          # -D QUALITY=0 / 1 / 2 でコンパイルし分ける
    values: [0, 1, 2]
  - name: USE_FOG          # 定義するか・しないか

units:
  - files: Shaders/**/*.hlsl
    entry: MainVS
    target: vs_6_6
  - files: Shaders/**/*.hlsl
    entry: MainPS
    target: ps_6_6
```

- 1 つのファイルに複数の単位を当てられます。上の例では、頂点シェーダーとピクセルシェーダーを 1 つのファイルに書き、2 回解析します
- `entry` の関数が無ければ CF0301 になります。報告はファイルの先頭に表示されます
- `target` から、DXC が定義するマクロ (`__SHADER_TARGET_STAGE` / `__SHADER_TARGET_MAJOR` / `__SHADER_TARGET_MINOR`) を決めます。
  `__HLSL_VERSION` と `__SHADER_STAGE_*` はどの単位でも定義します
- 値を持つシンボルは `#if QUALITY == 2` / `#if QUALITY >= 1` / `#if QUALITY` を値ごとに読み分け、指摘に `QUALITY == 2 のとき` を添えます
- シェーダーモデル 6.x の組み込み関数 (メッシュシェーダー、wave、`Barrier` など) と、ディスクリプタヒープ (`ResourceDescriptorHeap`) を知っています。
  `select` / `and` / `or` は DXC の組み込み関数です (DXC 1.8 ではすべての HLSL バージョンで使用できます。DXC 1.6 では HLSL 2021 に限り使用できます)

テンプレートとサンプルが [samples/environments/hlsl](../../../samples/environments/hlsl/) にあります。

## コンパイラと言語のバージョン

使える構文と型は、コードをコンパイルするコンパイラ (FXC / DXC) と、DXC の言語のバージョン (`-HV`) で決まります。
Shaderlyn はこの組ごとの表を持ち、使えない構文を [HL0003](../../rules/HL0003.md) などで報告します。
表はローカル環境の FXC (Windows SDK 10.0.26100) と DXC 1.6 / 1.8 で確認したものです。

| | FXC | DXC 2016 | 2017 | 2018 | 2021 |
| --- | --- | --- | --- | --- | --- |
| `template` / 演算子の多重定義 / ビットフィールド | × | × | × | × | ○ |
| `enum` / `enum class` | × | × | ○ | ○ | ○ |
| `int64_t` | × | ○ | ○ | ○ | ○ |
| `float32_t` などの明示のビット幅の型 | × | × | × | ○ | ○ |
| `float16_t` など (`-enable-16bit-types`) | × | × | × | ○ | ○ |
| ベクトルへの `&&` / `?:` | ○ | ○ | ○ | ○ | × ([HL0349](../../rules/HL0349.md)) |
| `for` の変数をループの外で使う | ○ | ○ | ○ | ○ | × |
| `and()` / `or()` / `select()` | × | ○ | ○ | ○ | ○ |
| 構造体の `++` | 何もしない ([HL0348](../../rules/HL0348.md)) | × | × | × | × |
| `sampler2D` / `tex2D` (DX9 の書き方) | ○ | × | × | × | × |

コンパイラは `hlsl: compiler`、言語のバージョンは `hlsl: version` で決めます (既定は DXC の 2021)。
Unity がパスごとにどちらを使うかは [Unity「シェーダーのコンパイラ」](unity.md#シェーダーのコンパイラ) にあります。

## 独自エンジン

独自の書き方を持たないエンジン (`-D` を変えて DXC でコンパイルするだけ) は、この環境で対応できます。
エンジン独自の `#pragma` で構成を宣言するなど、独自の書き方を持つエンジンは、[環境ごとの使い方「独自エンジン」](../environments.md#独自エンジン) を見てください。

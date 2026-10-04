# 環境ごとの設定ファイルのテンプレート

`.shaderlyn.yaml` を環境ごとに書いた例と、動作を確かめるためのシェーダーです。
シェーダーには意図的に誤りを入れてあり、下の表の指摘が出れば正しく動いています。
どれも Unity や Unreal Engine が無くても解析できます。

自分のプロジェクトで使うときは、フォルダーの `.shaderlyn.yaml` を写し、パスと構成の軸を書き換えてください。
キーの説明は [設定](../../docs/guide/configuration.md)、環境の説明は [Unity 以外の環境で使う](../../docs/guide/environments.md) にあります。

| フォルダー | 環境 | 中身 |
| --- | --- | --- |
| [unity](unity/) | `unity` | `.shader` 1 つ。Unity のヘッダは取り込まない |
| [hlsl](hlsl/) | `hlsl` (DXC で直接コンパイルする HLSL) | 頂点・ピクセルの 2 つの単位を持つ `.hlsl`、取り込まれる `.hlsli`、シェーダーモデル 6 の書き方を使う `.hlsl` |
| [unreal](unreal/) | `unreal` | `.usf` と、エンジンの代わりの小さなフォルダー (`Engine/`)、プラグインの仮想パス |

独自エンジンの環境は設定ファイルだけでは選べません (自前の CLI に環境を足す)。
[ルールの実例集「独自エンジンの環境」](../../docs/custom-rules/cookbook.md#独自エンジンの環境) を見てください。

## CLI で確かめる

リポジトリのルートで実行します。

```sh
shaderlyn samples/environments/unity
shaderlyn samples/environments/hlsl
shaderlyn samples/environments/unreal

# 解析はせずに、環境と設定 (構成の軸・コンパイル単位・仮想パス) を表示する
shaderlyn samples/environments/hlsl --env
```

ソースからなら `dotnet run --project src/Shaderlyn.Cli.App -- samples/environments/hlsl` です。

| フォルダー | 出るはずの指摘 |
| --- | --- |
| unity | `SL1003` (Color のプロパティを float で受けている)、`HL0330` (`_SHADOWS_SOFTT` の綴り)、`HL0312` (`_SHADOWS_SOFT` のとき float3 に `.w` は無い) |
| hlsl | `HL0301` (entry の `MainPS` が無い。関数は `MainPs`)、`HL0312` (`QUALITY == 2 のとき` float3 に `.a` は無い)。`Blur.hlsl` には何も出ない |
| unreal | `SL0002` (情報。エンジンが生成するファイル `/Engine/Generated/GeneratedUniformBuffers.ush` の中身が分からない)、`HL0312` (`PERMUTATION_HIGH_QUALITY == 1 のとき`)。`View.PreExposure` は報告されない |

## VS Code 拡張で確かめる

各フォルダーをワークスペースとして開くか、リポジトリのルートを開いて各フォルダーのファイルを開きます。
設定ファイルは、開いたファイルから上へ辿って一番近いものが使われます。

- シェーダーを開いて保存すると、上の表の指摘が出る
- エディタの右上の「条件のシンボルを選ぶ」で構成を選ぶと、効いていない `#if` の範囲が薄くなる。
  hlsl では `QUALITY = 0 / 1 / 2` のように値を選ぶ。unreal では `PERMUTATION_SAMPLES = 2` を選ぶと `#if PERMUTATION_SAMPLES > 1` の中が濃くなる
- hlsl / unreal では、`#pragma` の補完と説明は出ない (Unity の書き方なので)

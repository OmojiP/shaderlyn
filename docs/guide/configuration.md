# 設定

`.shaderlyn.yaml` に、プロジェクト全体の解析の設定（重要度の変更、探索パス、定義済みマクロなど）を書くためのページです。

設定ファイル以外の設定も後ろにまとめています。

# 設定ファイル `.shaderlyn.yaml`

解析対象のパスから上のフォルダーへ辿って設定ファイルを探します。

`--config <パス>` で明示的に指定することも、`--no-config` で探索を止めることもできます。

## 例

```yaml
version: 1
profile: urp

include-paths:
  - Assets/ShaderLibrary

defines:
  - _NORMALMAP
  - SHADER_API_D3D11=1

rules:
  SL1001: error      # 対応する宣言が無いプロパティはエラー扱いにする
  SL1004: none       # 未使用プロパティの指摘は使わない
  COOK0030:
    severity: warning
    options: { maxParameters: 4 }   # options を読む自作ルールへの設定
```

## キーの一覧

| キー            | 内容                                                            |
| --------------- | --------------------------------------------------------------- |
| `version`       | 設定ファイルのバージョン。現在は `1`                                    |
| `profile`       | レンダーパイプライン: `urp` / `brp` / `hdrp`                    |
| `include-paths` | `#include` の追加探索パス。設定ファイルからの相対で解決されます |
| `defines`       | 定義済みマクロ。`NAME` または `NAME=VALUE`                      |
| `rules`         | ルール ID ごとの重要度とオプション                              |

## rules

2 通りの書き方があります。重要度は `none` / `info` / `warning` / `error` のいずれかで、
`none` はそのルールを無効にします。

```yaml
rules:
  SL1003: error

  COOK0030:
    severity: warning
    options: { maxParameters: 4 }
```

### options

`options` はルールごとの設定値で、ルールの閾値変更などに等に利用できます。

オプションを自作ルールで使用する方法：[ルールの実例集](../custom-rules/cookbook.md#設定ファイルの-options-から閾値を受け取る)

> 現在、組み込みルールにはオプションを使用するものはありません。

どのオプション名を読むかはルールの説明に書かれています。

## 設定の誤りの報告

設定ファイルに書かれた認識できないキーは `TOOL0003` として報告されます。

```
.shaderlyn.yaml(8:1): warning TOOL0003: 設定に指定できないキー 'banned-symbol' があります。
```

次のものも報告されます。

- 字下げにタブを使っている
- 同じキーが 2 回書かれている
- 閉じられていない括弧や引用符
- 解釈できない重要度の綴り
- このツールより新しい `version`

問題があっても読み込みは失敗せず、読めた範囲は適用されます。

## コマンドラインとの優先順位

コマンドラインの指定は設定ファイルより優先されます。

# 設定ファイル以外の設定

## 抑制コメント

設定ファイルではなくコードに書く抑制もあります。意図してそう書いている箇所だけを抑制するときに使います。

| 書き方                                                 | 効果                         |
| ------------------------------------------------------ | ---------------------------- |
| `// shaderlyn-disable-next-line ID`                    | 次の 1 行だけ                |
| `// shaderlyn-disable-line ID`                         | その行だけ（行末に書く）     |
| `// shaderlyn-disable ID` … `// shaderlyn-enable ID`   | 挟んだ範囲                   |
| `// shaderlyn-disable-file ID`                         | ファイル全体（どこでもよい） |

```hlsl
// shaderlyn-disable-next-line SL1003 移行中のため (#1234)
half _Color;

// shaderlyn-disable-line SL1021
Cull Sideways

// shaderlyn-disable SL1020
...
// shaderlyn-enable SL1020

// shaderlyn-disable-file HL0302
```

ルール ID を省略するとすべてのルールが対象になります。ID はカンマまたは空白で区切って複数書けます。
ID の後ろのコメントは解析されません。

抑制は ShaderLab、埋め込み HLSL、`#include` したヘッダのいずれでも同じように書けます。

## ベースライン

既存プロジェクトへ導入するときに、今ある指摘を凍結して新しく混入したものだけを報告させる仕組みです。
設定ファイルではなくコマンドラインで指定します。手順は [CLI の使い方 6. ベースライン](cli-usage.md#6-ベースライン) にあります。

---

## 関連

- [CLI の使い方](cli-usage.md) — コマンドラインでの指定
- [ルール](../rules/README.md) — 重要度を変えるルールの ID
- [トラブルシューティング「設定ファイル」](troubleshooting.md#設定ファイル)

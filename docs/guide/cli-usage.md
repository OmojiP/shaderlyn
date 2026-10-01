# CLI の使い方

`shaderlyn` コマンドのインストール、実行、出力の読み方、オプションをまとめたページです。

設定：[configuration.md](configuration.md)

---

## 目次

1. [インストール](#1-インストール)
2. [基本的な実行](#2-基本的な実行)
3. [出力の読み方](#3-出力の読み方)
4. [終了コード](#4-終了コード)
5. [プロジェクトの解析設定](#5-プロジェクトの解析設定)
6. [ベースライン](#6-ベースライン)
7. [キャッシュ](#7-キャッシュ)
8. [解析の中身を見る](#8-解析の中身を見る)
9. [オプション一覧](#9-オプション一覧)

---

## 1. インストール

### 方法 A: 配布バイナリを使う

[リリースページ](https://github.com/OmojiP/shaderlyn/releases) から、使う環境のアーカイブを取得して展開します。

| 環境                  | アーカイブ                     |
| --------------------- | ------------------------------ |
| Windows (x64)         | `shaderlyn-win-x64.zip`        |
| macOS (Apple Silicon) | `shaderlyn-osx-arm64.tar.gz`   |
| macOS (Intel)         | `shaderlyn-osx-x64.tar.gz`     |
| Linux (x64)           | `shaderlyn-linux-x64.tar.gz`   |
| Linux (arm64)         | `shaderlyn-linux-arm64.tar.gz` |

配布バイナリは組み込みルールのみ動作します。

自作ルールを追加して使用したい場合は、NuGet パッケージ `Shaderlyn.Cli` を参照した自分用の CLI を作ります。
このリポジトリを clone する必要はありません（[自作ルールの作り方](../custom-rules/tutorial.md#5-自作ルール入りの-cli-を作る)）。

### 方法 B: ソースからビルドする

[.NET 10 SDK](https://dotnet.microsoft.com/download) が必要です。

リポジトリをクローン

```bash
git clone https://github.com/OmojiP/shaderlyn.git
cd shaderlyn
```

dotnet run で実行する

```
dotnet build --configuration Release
dotnet run --project src/Shaderlyn.Cli.App -- <引数>
```

単一の実行ファイルを作る場合

```bash
dotnet publish src/Shaderlyn.Cli.App/Shaderlyn.Cli.App.csproj \
  --configuration Release --runtime win-x64 --output ./bin
```

`--runtime` は `win-x64` / `osx-arm64` / `osx-x64` / `linux-x64` / `linux-arm64` から指定

### パスを通す

`shaderlyn` でツールを呼び出せるようにする場合、実行ファイルを置いたフォルダーを `PATH` に追加します。

### 確認

```bash
shaderlyn --version
```

---

## 2. 基本的な実行

ファイル、フォルダーを指定して解析します

```bash
# 単一ファイル
shaderlyn Assets/Shaders/MyShader.shader

# フォルダー配下の .shader , .compute を解析
shaderlyn Assets

# 複数パス指定
shaderlyn Assets/Shaders Packages/com.mycompany.shaders
```

`.hlsl` や `.cginc` は単体では成立しない断片であることがあるため、フォルダー指定では解析しません。
それらのファイルは、取り込んでいるシェーダーの解析の中で、取り込む側の文脈で検査されます。指摘はヘッダの位置に出て、
同じヘッダを取り込むシェーダーが複数あっても 1 件にまとまります。
Unity と外部パッケージのヘッダ (`Library/PackageCache` と Unity Editor に同梱のもの) は、直せないので報告しません。

### 解析対象は自分のシェーダーに絞る

Unityプロジェクト全体を解析する場合、Assets 以下を指定してください。

プロジェクトルートを指定すると Unity 同梱のシェーダーやパッケージを解析してしまいます。

```bash
# Good
shaderlyn Assets/Shaders --unity-project .

# Bad（Unity のパッケージまで解析してしまう）
shaderlyn . --unity-project .
```

---

## 3. 出力の読み方

```
Assets/Shaders/Lit.shader(24:18): warning SL1021: 'Cull' に指定された値 'Sideways' は不正です。指定できる値: Back, Front, Off
 24 |             Cull Sideways
    |                  ^^^^^^^^
```

| 部分                               | 意味                                   |
| ---------------------------------- | -------------------------------------- |
| `Assets/Shaders/Lit.shader(24:18)` | ファイル、行、桁（いずれも 1 始まり）  |
| `warning`                          | 重要度                                 |
| `SL1021`                           | ルール ID                              |
| 続く文                             | 何が問題か                             |
| 下の 2 行                          | 該当箇所のコードと、指摘範囲を示す下線 |

※ `text` 形式が出すのは解決後の絶対パスです（ここでは読みやすさのために短く書いています）。
多くのターミナルとエディタがこの書式をファイル位置として認識し、クリックで該当行へ飛べます。
相対パスが必要な場合は `--format json` 等と `--base-path` を使ってください。

### 重要度

| 重要度    | 意味                                                           |
| --------- | -------------------------------------------------------------- |
| `error`   | ほぼ確実に壊れている。シェーダーがコンパイルできない場合を含む |
| `warning` | 意図と違う動作になる可能性が高い                               |
| `info`    | 知らせるだけ。直すかどうかは設計次第                           |

### ルールの詳細

ルール ID がそのままドキュメントの名前です。

`docs/rules/XXX.md`

実装されている全ルールは `--list-rules` で一覧できます。

### 出力形式

`--format` で切り替えます。`-o` / `--output` を付けるとファイルへ書きます。

| 形式       | 用途                               |
| -------- | -------------------------------- |
| `text`   | 人が読む（既定）。該当行のコードと下線が付きます         |
| `github` | GitHub Actions 用。PR にインライン注釈が出ます |
| `sarif`  | GitHub Code Scanning へのアップロード用   |
| `json`   | 独自の集計や別ツールへの受け渡し                 |

#### `--format text`

```
D:\...\Shader.shader(68:17): error HL0310: 'a' は !_A && _C のとき宣言されていません。
 68 |                 a = 5;
    |                 ^

1 ファイルを解析: エラー 1 件, 警告 0 件, 情報 0 件
```

#### `--format github`

```                    
::error file=D%3A\...\Shader.shader,line=68,col=17,endLine=68,endColumn=18,title=HL0310%3A 宣言されていない識別子です::HL0310: 'a' は !_A && _C のとき宣言されていません。
1 ファイルを解析: エラー 1 件, 警告 0 件, 情報 0 件
```

#### `--format sarif`

```
{
  "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
  "version": "2.1.0",
  "runs": [
    {
      "tool": {
		...
      },
      "results": [
        {
          "ruleId": "HL0310",
          "level": "error",
          "message": {
            "text": "'a' は !_A && _C のとき宣言されていません。"
          },
          "locations": [
            {
              "physicalLocation": {
                "artifactLocation": {
                  "uri": "D:\...\Shader.shader"
                },
                "region": {
                  "startLine": 68,
                  "startColumn": 17,
                  "endLine": 68,
                  "endColumn": 18
                }
              }
            }
          ],
          "partialFingerprints": {
            "shaderlyn/v1": "..."
          }
        }
      ]
    }
  ]
}
```

#### `--format json`

```
{
  "summary": {
    "analyzedFiles": 1,
    "errors": 1,
    "warnings": 0,
    "infos": 0
  },
  "diagnostics": [
    {
      "id": "HL0310",
      "severity": "error",
      "category": "Correctness",
      "title": "宣言されていない識別子です",
      "message": "'a' は !_A && _C のとき宣言されていません。",
      "file": "D:\...\Shader.shader",
      "line": 68,
      "column": 17,
      "endLine": 68,
      "endColumn": 18,
      "fingerprint": "...",
      "helpUri": "https://github.com/OmojiP/shaderlyn/blob/main/docs/rules/HL0310.md"
    }
  ]
}
```


#### `--output` 

```bash
shaderlyn Assets --unity-project . --format json --output result.json
```

SARIF をリポジトリ外のフォルダーから作る場合は、`--base-path` にリポジトリのルートを渡してください。
パスが絶対パスのままだと、GitHub がファイルに対応づけられず、アップロードしても何も表示されません。

```bash
shaderlyn /path/to/repo/Assets --format sarif --output result.sarif \
  --base-path /path/to/repo
```

---

## 4. 終了コード

| コード | 意味                                                     |
| ------ | -------------------------------------------------------- |
| `0`    | 閾値以上の指摘なし                                       |
| `1`    | 閾値以上の指摘あり                                       |
| `2`    | ツールの実行に失敗 |

0と1の閾値は `--error-on` で決めます（既定 `warning`）。

```bash
# エラーだけで失敗させる（警告は表示するが終了コードは 0）
shaderlyn Assets --error-on error

# 情報レベルの指摘でも失敗させる
shaderlyn Assets --error-on info
```

---

## 5. プロジェクトの解析設定

### --unity-project

Unityで用意された関数等のヘッダ取得に利用します

```bash
shaderlyn Assets --unity-project .
```

指定しない場合は、そのマシンにインストール済みの Unity Editor からヘッダを探します。

見つからない場合、次のエラーが発生します。

```
Assets/Shaders/Lit.shader(9:9): error TOOL0004: 取り込むヘッダを 1 つも解決できません
    (最初に引けなかったのは 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl')。
    --unity-project にプロジェクトのルートを渡してください。
    CI では Library/PackageCache がキャッシュされているかを確かめてください。
```

### Unity Editor の場所

通常は指定不要です。

自動で見つからない場合だけ指定します。

```bash
# Windows
shaderlyn Assets --unity-project . --unity-editor "C:\Program Files\Unity\Hub\Editor\6000.0.30f1\Editor\Data"

# macOS
shaderlyn Assets --unity-project . --unity-editor "/Applications/Unity/Hub/Editor/6000.0.30f1/Unity.app/Contents"

# Linux
shaderlyn Assets --unity-project . --unity-editor "$HOME/Unity/Hub/Editor/6000.0.30f1/Editor/Data"
```

### 独自ヘッダの探索パス

多くの用途では `--unity-project` だけで解決できるので、このオプションは要りません。

Unity の解決規則（`#include` を書いたファイルからの相対パス、`Assets/...` 形式、Editor 同梱のヘッダ）では引けない場所にヘッダを置いている場合に渡します。

```bash
shaderlyn Assets --unity-project . --include-path ../SharedShaderLibrary
```

### `#include` が読む範囲

- コマンドラインで渡した解析対象
- `--unity-project` とその `Assets`
- `--unity-editor` の同梱ヘッダ
- `--include-path` で渡したパス

範囲の外を指した場合、[SL0002](../rules/SL0002.md) を報告します。
意図した取り込みであれば`--include-path` を追加してください。

### レンダーパイプライン

既定は `urp` で、`urp` / `brp` / `hdrp` から選べます。

```bash
shaderlyn Assets --unity-project . --profile brp
```

### シンボルの展開数

`#ifdef` で分かれるコードは、どちらの分岐も解析します。
1 本の構文木に並べられない書き方の分岐は、そのシンボルを有効にした構成を別に展開して解析します。
この件数には上限があり（既定 8）、超えたシンボルは調べられないまま `SL0003` として報告されます。

上限は `--max-symbol-variants` で変更できます。

```bash
shaderlyn Assets/Shaders --unity-project . --max-symbol-variants 16
```

解析時間は展開するシンボルの数に比例して伸びます。

解析の仕組み： [条件付きコンパイルの扱い](conditional-compilation.md)

### 設定ファイル

同じオプションを毎回書く代わりに、リポジトリのルートの `.shaderlyn.yaml` に書けます。
CLI は解析対象のパスから上のフォルダーへ辿ってこのファイルを探します。

- `--config <パス>` で明示的に指定できます
- `--no-config` で探さないようにできます

[設定ファイルの詳細](configuration.md) 

---

## 6. ベースライン

既存プロジェクトで、今ある指摘を記録しておき、新しく混入したものだけを報告させます。

```bash
# 現在の指摘を記録する
shaderlyn Assets --unity-project . --write-baseline .shaderlyn-baseline.json
143 件の指摘をベースラインへ記録しました: .shaderlyn-baseline.json

# 記録と照合して実行する
shaderlyn Assets --unity-project . --baseline .shaderlyn-baseline.json
```

- 指摘が出ている行の内容を正規化した fingerprint で照合
- ベースラインファイルを読めない場合は終了コード `2` 
- パスはベースラインファイルからの相対で記録される

---

## 7. キャッシュ

`--cache` を付けると、前回結果を記録します。
変更のないファイルを解析しないため、２回目以降が高速になります。

```bash
shaderlyn Assets --unity-project . --cache .shaderlyn-cache.json
```

- キャッシュファイルは絶対パスを参照するため、`.gitignore` へ入れてください


> 解析の内訳を知りたい場合は `--timings` を付けると、構文解析・展開・ルールにかかった時間を標準エラーへ出します。

---

## 8. 解析の中身を見る

### 解析の環境を確かめる（`--env`）

指摘が出ないときは、まずヘッダが読めているかを確かめます。
`--env` を付けると、解析はせずに、解析が使う環境を表示します。

```bash
shaderlyn Assets --unity-project . --env

Shaderlyn {バージョン} の解析環境 (解析はしていません)

■ 解析対象
  D:\...\Assets\ (フォルダー)
  見つかったシェーダー: 1 件 (.shader / .compute)

■ 設定ファイル (.shaderlyn.yaml)
  探し始めた場所: D:\...\Assets\ (ここから上のフォルダーへ辿ります)
  見つかりませんでした。既定の設定で解析します。
  プロファイル: urp (既定)

■ Unity プロジェクト
  --unity-project: F:\src\ShaderAnalyzerTest\
  Assets: あり
  Packages: あり
  Library\PackageCache: あり
  ProjectSettings\ProjectVersion.txt: あり

■ Unity Editor
  データフォルダー: C:\Program Files\Unity\Hub\Editor\6000.0.60f1\Editor\Data
  決め方: ...
  CGIncludes: あり
  探したインストール先: C:\Program Files\Unity\Hub\Editor
  インストール済み: 6000.0.60f1

■ #include を探す順序
  1. #include を書いているファイルからの相対
  2. /.../
  3. /.../

■ パッケージの解決先
  ...

■ 解析対象に書かれた #include (直接書かれたものだけ。#if の条件は見ていません)
  解決できた: 1 / 1

■ 定義済みマクロ (展開に使う値)
  A=1
  B=2
  C=3
```

| 項目                     | 内容                                                                                   |
| ------------------------ | -------------------------------------------------------------------------------------- |
| 解析対象                 | 指定したパスと、見つかったシェーダーの数                                               |
| 設定ファイル             | 探し始めた場所、見つかった `.shaderlyn.yaml`、その中の誤り、効いているプロファイル     |
| Unity プロジェクト       | `Library/PackageCache` などがあるか。未指定なら、解析対象の上にあるプロジェクトを示す |
| Unity Editor             | 使うデータフォルダーと、その決め方（`ProjectVersion.txt` と一致したか、代用したか）   |
| `#include` を探す順序    | 解析と同じ順序。無いフォルダーには ⚠ が付く                                           |
| パッケージの解決先       | 解析対象が `Packages/` で参照するパッケージと `manifest.json` の依存が、どこにあったか |
| 解析対象に書かれた `#include` | 解決できた数と、解決できなかった行                                                |
| 定義済みマクロ           | 展開に使う値                                                                           |

### 解析の中身を HTML に書き出す（`--inspect`）

指摘が出る理由、または出ない理由が分からないときは、`--inspect` で解析の中身を HTML に書き出せます。
外部ファイルを参照しない 1 枚の HTML なので、そのまま添付して共有できます。

```bash
shaderlyn Assets/Shaders/Lit.shader --unity-project . --inspect inspect.html
```

| タブ             | 内容                                                     |
| ---------------- | -------------------------------------------------------- |
| ShaderLab 構文木 | ShaderLab としてどう読まれたか                           |
| HLSL             | 埋め込みコードがどう読まれたか（このファイルの宣言のみ） |
| uniform          | 認識された宣言と型                                       |
| 式の型           | 式ごとに、型がどう判定されたか                           |
| マクロ           | 展開の時点で定義されていたマクロ                         |
| Properties       | 認識されたプロパティ                                     |
| トークン         | マクロ展開前のトークン列                                 |
| include          | 解決できた／できなかったヘッダ                           |
| 診断             | そのファイルへの指摘                                     |

---

## 9. オプション一覧

```
shaderlyn <パス...> [オプション]
```

### 基本

| オプション              | 説明                                                              |
| ----------------------- | ----------------------------------------------------------------- |
| `--format <形式>`       | 出力形式: `text`（既定）/ `json` / `sarif` / `github`             |
| `--error-on <重要度>`   | この重要度以上で終了コードを `1` にする: `info` / `warning`（既定）/ `error` |
| `-o`, `--output <パス>` | 出力先ファイル。省略時は標準出力                                  |
| `--base-path <パス>`    | 出力するパスを相対化する基点。省略時は現在のフォルダー          |
| `--annotate`            | 主たる出力とは別に、GitHub の注釈も標準出力へ書く                 |
| `--timings`             | 解析の内訳（構文解析・展開・ルール）を標準エラーへ出す            |
| `--env`                 | 解析はせずに、設定ファイル・Unity・パッケージの解決先を表示する（[8 章](#8-解析の中身を見る)） |
| `--inspect <パス>`      | 解析の中身を HTML に書き出す（[8 章](#8-解析の中身を見る)）       |
| `--list-rules`          | 実装されている全ルールを一覧表示                                  |
| `--version`             | バージョンを表示                                                  |
| `-h`, `--help`          | ヘルプを表示                                                      |

### 解析の設定

| オプション                        | 説明                                                               |
| --------------------------------- | ------------------------------------------------------------------ |
| `--unity-project <パス>`          | Unity プロジェクトのルート。対応検査にはこれが必要です             |
| `--unity-editor <パス>`           | Unity Editor のデータフォルダー。省略時は自動で推定              |
| `--include-path <パス>`           | `#include` の追加探索パス。複数回指定できます。ここで許した場所だけが読み込みの対象になります |
| `--profile <名前>`                | レンダーパイプライン: `urp`（既定）/ `brp` / `hdrp`            |
| `--define <名前[=値]>`            | 定義済みマクロ。複数回指定できます                                 |
| `--max-symbol-variants <数>`     | シンボルを 1 つずつ有効にして解析する上限（既定 8）。0 で無効    |

### 設定ファイルとベースライン

| オプション                | 説明                                                               |
| ------------------------- | ------------------------------------------------------------------ |
| `--config <パス>`         | 設定ファイル。省略時は上へ辿って `.shaderlyn.yaml` を探します |
| `--no-config`             | 設定ファイルを探しません                                           |
| `--baseline <パス>`       | ベースラインに記録済みの指摘を除外します                           |
| `--write-baseline <パス>` | 現在の指摘をベースラインとして書き出します                         |
| `--cache <パス>`          | 解析結果のキャッシュ。変わっていないファイルを飛ばします           |

---

## 関連

- [設定ファイルの詳細](configuration.md)
- [実装済みのルール](../rules/README.md) / [ルールごとの説明](../rules/)
- [自作ルールの作り方](../custom-rules/tutorial.md)
- [トラブルシューティング](troubleshooting.md)

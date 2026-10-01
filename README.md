<p align="center">
  <img src="docs/images/icon.png" alt="Shaderlyn" width="160">
</p>

# Shaderlyn

Unity の ShaderLab および HLSL（`.shader`, `.compute`）を静的解析する variability-aware で Roslyn-like なツール

CLI, VS Code 拡張, GitHub Actions 等で利用でき、自作の解析ルールも追加できます。

## 主な機能

### 1. シェーダーファイルを解析し、info/warning/error の３段階で報告

構文エラーのほか、コンパイルは通るが誤りの可能性があるコードも報告します。重要度は error / warning / info の 3 段階です。

次のコードは、 `float4` で受けることを想定した `Color` 型のプロパティ `_BaseColor` を `float` で受けていますが、暗黙の型変換が行われコンパイルが通ります。

```shaderlab
Shader "Custom/Lit"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Pass
        {
            HLSLPROGRAM

            // ...

            CBUFFER_START(UnityPerMaterial)
                float _BaseColor; // Color を float で受けている
            CBUFFER_END

            // ...

            ENDHLSL
        }
    }
}
```

本ツールでは以下のように報告します

```
Assets/Shaders/Lit.shader(5:21): warning SL1003: プロパティ '_BaseColor' は Color ですが、HLSL では 'float' として宣言されています。float4 / half4 などのベクトル型が必要です。
```

ほかにも、スペルミスの可能性があるタグの値や、使われていないシンボルなどを報告します。
報告する内容の一覧は [ルール](docs/rules/README.md) にあります。

### 2. variability-aware な設計で `#if` 分岐を考慮して解析

以下のようなコードは `_A かつ _C` が有効なときには動作しますが、 `_B` の時には `a` は存在しませんし、 `_A` の時には `b` は存在しません。


```hlsl
float ReturnFloat()
{
    #pragma multi_compile _A _B
    #pragma multi_compile _C _D

    #if defined(_A)
    float a = 1.0;
    #elif defined(_B)
    float b = 0;
    #endif

    #if defined(_C)
    a = 5;
    #elif defined(_D)
    b = 2;
    #endif

    return a;
}
```

shaderlyn は、宣言や変数を存在条件付きで解析し、一部の構成によって起きる誤りも報告します。

```
D:\...\Shader.shader(68:17): error HL0310: 'a' は !_A && _C のとき宣言されていません。                                                                                       
 68 |                 a = 5;                       
    |                 ^                                                                 
   
D:\...\Shader.shader(70:17): error HL0310: 'b' は (_A && !_C && _D) || (!_B && !_C && _D) のとき宣言されていません。
 70 |                 b = 2;                                                            
    |                 ^
                    
D:\...\Shader.shader(73:24): error HL0310: 'a' は !_A のとき宣言されていません。
 73 |                 return a;                                   
    |                        ^
                                                                      
1 ファイルを解析: エラー 3 件, 警告 0 件, 情報 0 件
```

条件付き解析が難しい書き方の分岐は、上限（既定 8）までシンボルを有効にした構成を別に解析します。

解析の仕組み： [条件付きコンパイルの扱い](docs/guide/conditional-compilation.md) 

### 3. Roslyn風に自作の解析ルールを追加する

プロジェクトのルールに合わせて自作の解析ルールを追加できます

- 自作ルールの書き方 : [docs/custom-rules/tutorial.md](docs/custom-rules/tutorial.md)
- 自作ルールの実例集 : [docs/custom-rules/cookbook.md](docs/custom-rules/cookbook.md)

`half` 型の使用を禁止する自作ルール例：

```
public sealed class NoHalfAnalyzer : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MyRules.NoHalf];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        context.RegisterNodeAction<HlslTypeSyntax>(c =>
        {
            if (c.Node.Name.StartsWith("half", StringComparison.Ordinal))
            {
                c.ReportDiagnostic(MyRules.NoHalf, c.Node.Name);
            }
        });
    }
}
```

### 4. LSP利用

vscode拡張として指摘を表示します。
自作ルールを追加した言語サーバーを指定することで自作ルールの指摘を出すこともできます。

VS Code 拡張の言語サーバー利用 : [editors/vscode/README.md](editors/vscode/README.md)

![VS Code で指摘が波線と「問題」パネルに出ている画面](./editors/vscode/images/error_hide.png)

### 5. CI利用

ツールの出力形式を `--format` で `json`, `sarif`, `github` に設定することで CI にそのまま接続することが出来ます。

GitHub Actions のセルフホストランナーでPRに使用する例 : [docs/guide/ci-setup.md](docs/guide/ci-setup.md)

![PR の差分に指摘が注釈として表示されている画面](./docs/images/format-github.png)

---

## 導入・使い方

### 1. CLI 利用

詳しい使い方は [docs/guide/cli-usage.md](docs/guide/cli-usage.md) 

[リリースページ](https://github.com/OmojiP/shaderlyn/releases) から使う環境のアーカイブを取得して展開し、`shaderlyn` を PATH の通った場所に置きます。
環境ごとのファイル名、パスの通し方、ソースからのビルドは [CLI の使い方](docs/guide/cli-usage.md#1-インストール) にあります。

```bash
shaderlyn --version

# 基本実行
shaderlyn /.../UnityProject/Assets --unity-project /.../UnityProject
```

#### 主なコマンドラインオプション

| オプション                  | 説明                                           |
| ---------------------- | -------------------------------------------- |
| `--unity-project <パス>` | Unity プロジェクトのルート。パッケージ配下のヘッダ解決に必須            |
| `--format <形式>`        | 出力形式: `text` (既定), `json`, `sarif`, `github` |
| `--error-on <重要度>`     | ジョブを失敗させる閾値: `info`, `warning` (既定), `error` |
| `--config <パス>`        | 設定ファイルの指定（省略時は `.shaderlyn.yaml` を検索）        |
| `--baseline <パス>`      | ベースラインファイル（既存の指摘を除外）を指定                      |
| `--cache <パス>`         | 変更のないファイルの解析をスキップするキャッシュファイルを指定              |
| `-o, --output <パス>`    | 診断結果の出力先ファイルパス                               |
| `--list-rules`         | 実装されている全ルールを一覧表示                             |

---

### 2. GitHub Actions で使う

手順は [CI に組み込む](docs/guide/ci-setup.md) にあります。

![PR の差分に指摘が注釈として表示されている画面](docs/images/format-github.png)

---

### 3. VS Code 拡張で使う

VS Code の拡張機能ビューで **Shaderlyn** を検索してインストールします（[Marketplace](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn)）。

導入とオプションの詳細： [editors/vscode/README.md](editors/vscode/README.md#インストール) 

![スペルミスをクイックフィックスで直す様子](./editors/vscode/images/typo.gif)

| 機能               | 内容                                                                                                                   |
| ------------------ | ---------------------------------------------------------------------------------------------------------------------- |
| 解析               | 入力のたびに ShaderLab 単体の検査、保存時に HLSL との対応検査                                                          |
| ホバー             | プロパティと HLSL の対応、式の型、マクロの中身、定数バッファの構成、`#pragma` の意味                                   |
| 移動・補完         | 定義へ移動 (`F12`)、参照検索 (`Shift+F12`)、リネーム (`F2`)、補完、シグネチャヘルプ                                    |
| クイックフィックス | スペルミスの修正、抑制コメントの挿入                                                                                   |
| HLSL 単体          | `.hlsl` / `.cginc` / `.hlslinc` / `.compute` も解析。`#ifdef` のシンボルを右上から定義済みにして、条件の中も検査できる |
| 効いていない分岐   | どの構成でも条件が外れる `#if` / `#ifdef` の中を薄く表示（シンボルのバリアントで通る分岐は薄くしない）                     |

---

## 運用のための機能

### 新規に混入したエラーのみを検知する

今ある指摘を凍結し、新しく混入したものだけを報告させられます（手順: [CLI の使い方 6. ベースライン](docs/guide/cli-usage.md#6-ベースライン)）。

```bash
shaderlyn Assets --write-baseline .shaderlyn-baseline.json   # 今ある指摘を記録する
shaderlyn Assets --baseline .shaderlyn-baseline.json         # 記録と照合して実行する
```

### コメントによる指摘の抑制

意図してそう書いている箇所は、コメントで抑制できます（書き方の一覧: [設定ファイル「抑制コメント」](docs/guide/configuration.md#抑制コメント)）。

```hlsl
// shaderlyn-disable-next-line SL1003 理由コメント
half _Color;
```

### 自作ルールの追加

プロジェクト固有の規約は、自作ルールとして C# で実装します。
手順は [自作ルールの作り方](docs/custom-rules/tutorial.md) にあります。

```csharp
ImmutableArray<DiagnosticAnalyzer> analyzers =
[
    .. BuiltInAnalyzers.All,      // 組み込みルール
    new NoHalfAnalyzer(),         // 自作ルール
];

return (int)await Shaderlyn.Cli.Program.RunAsync(args, analyzers, Console.Out, Console.Error);
```

---

## 組み込みの静的解析ルール

組み込みルールの一覧と、ルールごとの説明は [docs/rules/](docs/rules/) にあります。

---

## ドキュメント

[docs/README.md](docs/README.md) から、読む人ごとのドキュメントを案内します。

| 読む人         | 最初に読むドキュメント                                                                                                                                                                      |
| ----------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ツールを使う      | [CLI の使い方](docs/guide/cli-usage.md) / [設定ファイル](docs/guide/configuration.md)                                                         |
| CI やエディタで使う | [CI に組み込む](docs/guide/ci-setup.md) / [VS Code 拡張](editors/vscode/README.md)                                                                                                  |
| トラブルシューティング | [トラブルシューティング](docs/guide/troubleshooting.md)                                                                                                                                 |
| 自作ルールを書く    | [自作ルールの作り方](docs/custom-rules/tutorial.md) → [書き方の手引き](docs/custom-rules/guide.md) / [実例集](docs/custom-rules/cookbook.md) / [API リファレンス](docs/custom-rules/api-reference.md) |
| 開発に参加する     | [貢献の手引き](CONTRIBUTING.md) |
| 脆弱性を報告する    | [セキュリティ](SECURITY.md) |

---

## ライセンス

[MIT License](LICENSE)

組み込み関数の表は DirectXShaderCompiler のデータから作っています。その著作権表示とライセンスは [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) にあります。

# Shaderlyn for Visual Studio

Unity の ShaderLab と HLSL、Unreal Engine、GLSL、Godot、Slang、WGSL のシェーダーの誤りを、Visual Studio で指摘する拡張機能です。
解析は VS Code 拡張と同じ言語サーバーで行い、同じ指摘を出します。

対象のバージョンは Visual Studio 2022 (17.14 以降) と Visual Studio 2026 です。

## 主な機能

| 機能 | 内容 |
| --- | --- |
| 指摘 | 波線とエラー一覧に出します。入力のたびに構文・ShaderLab の値などを検査し、開いたときと保存したときに宣言・型・`#include` の解決を要する検査まで行います |
| クイックフィックス | 修正が 1 つに決まる指摘 (スペルミスなど) の修正と、抑制コメント (`// shaderlyn-disable-next-line <ID>`) の挿入 |
| ホバー | 型、マクロの中身、プロパティと HLSL の対応、セマンティクスや `#pragma` の意味 |
| 補完・引数の案内 | 条件付きコンパイルを考慮した候補と、関数の引数 |
| 定義へ移動・参照・名前の変更 | 変数・関数・構造体・マクロと、`Properties` と HLSL の uniform の対応 |

VS Code 拡張にだけある機能は次のとおりです。

- 条件が外れた `#if` / `#ifdef` の中を薄く表示する
- シンボルの構成を選んで表示する
- 解析の中身を見る (コマンド「Shaderlyn: 解析の中身を見る」)

対象のファイルの拡張子は次のとおりです。

| 言語 | 拡張子 |
| --- | --- |
| Unity の ShaderLab | `.shader` |
| HLSL | `.hlsl` `.hlsli` `.cginc` `.hlslinc` `.compute` `.usf` `.ush` |
| GLSL | `.vert` `.frag` `.comp` `.geom` `.tesc` `.tese` `.task` `.rgen` `.rint` `.rahit` `.rchit` `.rmiss` `.rcall` `.glsl` |
| Godot | `.gdshader` `.gdshaderinc` |
| Slang | `.slang` |
| WGSL | `.wgsl` |

## HLSL Tools との併用

「C++ によるゲーム開発」ワークロードに含まれる HLSL Tools が入っている場合は、`.hlsl` `.hlsli` `.cginc` `.compute` `.usf` `.ush` などを HLSL Tools が受け持ちます。
これらのファイルでは、Shaderlyn は指摘とクイックフィックスだけを出し、ホバー・補完・定義へ移動は HLSL Tools のものが働きます。
エラー一覧には、HLSL Tools の指摘 (`HLSL0033` など) と Shaderlyn の指摘 (`CF0310` など) が並びます。

HLSL Tools が入っていない場合は、これらのファイルでも Shaderlyn がすべての機能を提供します。

## インストール

「拡張機能」→「拡張機能の管理」で **Shaderlyn** を検索してインストールします ([Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn-visualstudio))。
インストールのあと、Visual Studio を再起動してください。

Marketplace を使用できない環境では、[リリースページ](https://github.com/OmojiP/shaderlyn/releases) から `shaderlyn-visualstudio.vsix` を取得し、ダブルクリックしてインストールします。

## 動作条件

- Unity プロジェクトでは、プロジェクトのルート (`Assets` と `ProjectSettings` が存在するフォルダー) を「フォルダーを開く」で開いてください。パッケージのヘッダを自動で解決します
- Unity 以外の環境 (汎用の HLSL など) では、`.shaderlyn.yaml` に `environment` を書いてください ([環境ごとの使用方法](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/environments.md))
- Unreal Engine の `.usf`・`.ush` は、`environment` を書かなくても Unreal Engine の環境で解析します。エンジンのヘッダ (`/Engine/...`) を解決するには、`.shaderlyn.yaml` にエンジンの場所 (`unreal: engine`) を書いてください

## 設定

ルールの重要度や無効化は `.shaderlyn.yaml` に書きます ([設定ファイル](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/configuration.md))。開いたファイルから上へたどって、一番近い `.shaderlyn.yaml` を使用します。

Visual Studio の設定は、「ツール」→「オプション」→「Shaderlyn」→「全般」にあります。
Visual Studio 2026 の設定画面では、「Shaderlyn」を選ぶと「これらの設定はまだ移行されていません」と表示されるので、その下のリンク (「全般」) から従来の [オプション] ダイアログを開きます。

| 項目 | 内容 | 既定値 |
| --- | --- | --- |
| 言語サーバー > 実行ファイル | 起動する言語サーバー (`shaderlyn-lsp.exe`) のパス。空の場合は、拡張機能に同梱したものを使用します。変更すると言語サーバーを起動し直します | 空 |

指定した実行ファイルが存在しない場合は、そのことをメッセージで伝え、同梱したものを使用します。

## 自作ルールを使用する

同梱の言語サーバーは、組み込みのルールだけを解析します。
自作ルールの指摘をエディタに出すには、自作ルールを含む言語サーバーをビルドし ([自作ルールの作り方 5-1](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/tutorial.md#5-1-エディタでも働かせる))、
設定の「実行ファイル」にそのパスを指定します。

## ライセンス

MIT License

組み込み関数の表は DirectXShaderCompiler のデータから作っています。その著作権表示とライセンスは、拡張機能に同梱した THIRD-PARTY-NOTICES.txt にあります。

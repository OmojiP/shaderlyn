# Shaderlyn for Rider

Unity の ShaderLab と HLSL、Unreal Engine、GLSL、Godot、Slang、WGSL のシェーダーの誤りを、JetBrains Rider で指摘するプラグインです。
解析は VS Code 拡張と同じ言語サーバーで行い、同じ指摘を出します。

対象のバージョンは Rider 2026.3 以降です。

## 主な機能

| 機能 | 内容 |
| --- | --- |
| 指摘 | エディタの波線と「Problems」ツールウィンドウに出します。入力のたびに構文・ShaderLab の値などを検査し、開いたときと保存したときに宣言・型・`#include` の解決を要する検査まで行います |
| クイックフィックス | 修正が 1 つに決まる指摘 (スペルミスなど) の修正と、抑制コメント (`// shaderlyn-disable-next-line <ID>`) の挿入 |

ホバー・補完・定義へ移動・参照は、Rider 自身のシェーダーの支援 (Unity の ShaderLab と HLSL など) が働きます。
同じ位置に 2 つずつ出ないよう、Shaderlyn はこれらを提供しません。

対象のファイルの拡張子は、[Visual Studio 拡張](../visualstudio/README.md#主な機能) と同じです。

## インストール

Rider の「Settings」→「Plugins」の「Marketplace」タブで **Shaderlyn** を検索してインストールします。インストールのあと、Rider を再起動してください。
JetBrains Marketplace は、使用している OS と CPU に合う言語サーバーを同梱したものを配ります。

Marketplace を使用できない環境では、[リリースページ](https://github.com/OmojiP/shaderlyn/releases) から使用する環境の zip を取得します。

| OS と CPU | ファイル |
| --- | --- |
| Windows (x64・arm64) | `shaderlyn-rider-<バージョン>-windows-x86_64.zip` / `-windows-arm64.zip` |
| macOS (Apple シリコン・Intel) | `shaderlyn-rider-<バージョン>-mac-arm64.zip` / `-mac-x86_64.zip` |
| Linux (x64・arm64) | `shaderlyn-rider-<バージョン>-linux-x86_64.zip` / `-linux-arm64.zip` |

Rider の「Settings」→「Plugins」の歯車のメニューから「Install Plugin from Disk...」を選び、その zip を指定します。

zip から入れたプラグインには、Rider が「Cannot update plugin. Choose where it was installed from」という警告を出すことがあります。
入手元を特定できないため更新を確認できない、という意味で、Shaderlyn の動作には影響しません。

## 動作条件

- Unity プロジェクトでは、プロジェクトのルート (`Assets` と `ProjectSettings` が存在するフォルダー) を開いてください。パッケージのヘッダを自動で解決します
- Unity 以外の環境 (汎用の HLSL・Unreal Engine など) では、`.shaderlyn.yaml` に `environment` を書いてください ([環境ごとの使用方法](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/environments.md))

## 設定

ルールの重要度や無効化は `.shaderlyn.yaml` に書きます ([設定ファイル](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/configuration.md))。開いたファイルから上へたどって、一番近い `.shaderlyn.yaml` を使用します。

Rider の設定は、「Settings」→「Tools」→「Shaderlyn」にあります。

| 項目 | 内容 | 既定値 |
| --- | --- | --- |
| 言語サーバー > 実行ファイル | 起動する言語サーバー (`shaderlyn-lsp`) のパス。空の場合は、プラグインに同梱したものを使用します。変更すると言語サーバーを起動し直します | 空 |

指定した実行ファイルが存在しない場合は、そのことを通知で伝え、同梱したものを使用します。

## 自作ルールを使用する

同梱の言語サーバーは、組み込みのルールだけを解析します。
自作ルールの指摘をエディタに出すには、自作ルールを含む言語サーバーをビルドし ([自作ルールの作り方 5-1](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/tutorial.md#5-1-エディタでも働かせる))、
設定の「実行ファイル」にそのパスを指定します。

## ライセンス

MIT License

組み込み関数の表は DirectXShaderCompiler のデータから作っています。その著作権表示とライセンスは、プラグインに同梱した THIRD-PARTY-NOTICES.txt にあります。

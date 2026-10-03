# 変更履歴

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に従い、
バージョンの付け方は [セマンティックバージョニング](https://semver.org/lang/ja/) に従う。
CLI、VS Code 拡張、NuGet パッケージは同じタグから同じバージョンで出す。
0.x の間は、マイナーバージョンを上げるときに公開 API や CLI のオプションを変えることがある。
そうした変更は「変更」「削除」の項目の先頭に **破壊的変更:** と書いて示す。

利用者から見て何が変わるかを書く。実装の変更は、それが挙動か API を変える場合にだけ載せる。

## [未リリース]

最初の公開バージョン (0.1.0)。

### 追加

- Unity の ShaderLab、埋め込み HLSL、HLSL 単体のファイル (`.compute` / `.hlsl` / `.cginc` / `.hlslinc`) の静的解析
- 52 のルール。ShaderLab、Properties と HLSL の対応、HLSL、URP 固有の検査がある ([ルール一覧](docs/rules/README.md))
- `#ifdef` で分かれるコードは、どちらの分岐も検査する ([条件付きコンパイルの扱い](docs/guide/conditional-compilation.md))
- 利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) も、取り込むシェーダーの文脈で検査する。
  Unity と外部パッケージのヘッダは対象にしない
- 出力形式 `text` / `json` / `sarif` / `github`
- GitHub Action。PR へのインライン注釈と Code Scanning へのアップロード ([CI に組み込む](docs/guide/ci-setup.md))
- 設定ファイル `.shaderlyn.yaml` による重要度の変更、無効化、ルールのオプション ([設定](docs/guide/configuration.md))
- 抑制コメント、ベースライン (`--baseline` / `--write-baseline`)、実行をまたぐキャッシュ (`--cache`)
- 調べるための出力: 解析の中身を HTML に出す `--inspect`、解析が使う環境を表示する `--env`、段ごとの所要時間を出す `--timings`
- VS Code 拡張。言語サーバーを同梱し、VS Code Marketplace で公開する (vsix もリリースに添付する)。
  指摘、ホバー、補完、定義へ移動、キーワードの構成を選んだ表示 ([VS Code 拡張](editors/vscode/README.md))
- C# で自作ルールを書くための NuGet パッケージ (`Shaderlyn.Semantics` / `Shaderlyn.Testing` / `Shaderlyn.Cli` / `Shaderlyn.LanguageServer`) と、
  写して使えるルールの実例集 ([自作ルールの作り方](docs/custom-rules/tutorial.md))
- Native AOT の単一バイナリ (5 プラットフォーム)

[未リリース]: https://github.com/OmojiP/shaderlyn/commits/main

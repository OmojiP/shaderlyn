# 各ドキュメントへのマッピング

shaderlyn のドキュメントを、読む人ごとにまとめたページです。自分に当てはまる順路から読み始めてください。

## ツールを使う人向け

1. [CLI の使い方](guide/cli-usage.md) — インストール、実行、出力の読み方、オプション
2. [設定ファイル](guide/configuration.md) — `.shaderlyn.yaml`、抑制コメント
3. [ルール](rules/README.md) — 組み込みルールの一覧と、ルールごとの説明

必要になったら読むもの:

- [CI に組み込む](guide/ci-setup.md) — GitHub Actions のセルフホストランナーで PR ごとに動かす
- [条件付きコンパイルの扱い](guide/conditional-compilation.md) — `#ifdef` の分岐をどう解析しているか。`SL0003` / `SL0004` が出たとき。付録に実装の細部
- [VS Code 拡張](../editors/vscode/README.md) — 導入、機能、設定
- [トラブルシューティング](guide/troubleshooting.md) — 症状から原因と対処を探す

## 自作ルールを書く人

プロジェクト固有の規約を C# のルールとして書き、CLI やエディタで動かす人向けです。

1. [自作ルールの作り方](custom-rules/tutorial.md) — プロジェクトの用意から、最初のルールを CLI とエディタで動かすまで
2. [自作ルールの書き方の手引き](custom-rules/guide.md) — ShaderLab を見るルール、集計、セマンティックモデル、ルールを書くときの原則
3. [ルールの実例集](custom-rules/cookbook.md) — そのまま使えるか、写して直せる例
4. [ルール API リファレンス](custom-rules/api-reference.md) — 型とメンバーの一覧

## 開発に参加する人

shaderlyn 自体のコードを直す人向けです。

1. [貢献の手引き](../CONTRIBUTING.md) — ビルド、テスト、ルールを足すときに揃えるもの
2. [解析の流れ](internals/architecture.md) — CLI と言語サーバーが診断を返すまでの処理
3. [設計判断](internals/design-decisions.md) — なぜそう作ったか、採らなかった案

## ドキュメントの置き場所

| 場所 | 内容 |
| --- | --- |
| `docs/guide/` | ツールを使う人向け |
| `docs/custom-rules/` | 自作ルールを書く人向け |
| `docs/rules/` | ルールごとの説明。診断のヘルプリンクがここを指すので、ファイル名はルール ID のまま動かさない |
| `docs/internals/` | 開発に参加する人向け |
| `docs/images/` | ドキュメントで使う画像 |

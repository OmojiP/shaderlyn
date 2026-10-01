# 貢献の手引き

Shaderlyn への不具合の報告・提案・プルリクエストを歓迎します。

## 報告する

- **誤検出（正しいシェーダーに指摘が出る）** は、このツールで最も重い不具合です。
  「誤検出の報告」のテンプレートから、指摘の全文と、再現できる最小のシェーダーを添えて報告してください
- **見逃し（誤りなのに指摘が出ない）** も不具合です。Unity（FXC / DXC）が出したエラーを添えてもらえると確実です
- 脆弱性は Issue に書かず、[SECURITY.md](SECURITY.md) の方法で報告してください

## 開発を始める

[.NET 10 SDK](https://dotnet.microsoft.com/download) が要ります。

```bash
dotnet build
dotnet test
```

- **警告はすべてエラーになります**（`TreatWarningsAsErrors`）。公開メンバーの説明コメント（`///`）が無いとビルドが通りません
- **Native AOT で配布しています。** リフレクションに頼るコードはビルドで弾かれます（`IsAotCompatible`）
- VS Code 拡張を動かすには `editors/vscode` で `npm install` し、VS Code の「実行とデバッグ」から「VS Code 拡張を起動」を選びます
- Unity がインストールされていなくてもテストは通ります。Unity の実物を使う検証は、見つからなければ飛ばされます

## ルールを足す・直す

組み込みルールを足すときは、次がすべて揃うまでテストが落ちます。

| 必要なもの | 確かめているテスト |
| --- | --- |
| `BuiltInAnalyzers.All` への登録 | `BuiltInAnalyzerRegistrationTests` |
| ルールを踏むシェーダー `tests/Shaderlyn.Tests/fixtures/rules/<ID>.shader` | `RuleFixtureTests` |
| 説明のドキュメント `docs/rules/<ID>.md`（見出しと既定の重要度を実装と一致させる） | `RuleDocumentationTests` |
| 一覧 [docs/rules/README.md](docs/rules/README.md) への記載 | `RuleDocumentationTests` |

あわせて守ってほしいこと:

- **検査に必要な情報が足りないときは、誤りと断定せず、その検査を見送って理由を診断として伝えてください。** たとえば include を解決できない場合、その中の識別子を「未定義」とはせず、依存ファイルを読み込めず検査できなかったことを報告します
- **推測で規則を決めないでください。** 可能な限り、実際のコンパイラ（Unity）の結果と突き合わせてください
- 利用者から見て挙動が変わる変更は、[CHANGELOG.md](CHANGELOG.md) の「未リリース」に書いてください
- **新しい型やメンバーは、まず internal で書いてください。** `src/` のライブラリは NuGet で配っており、public にしたものは自作ルールの利用者との約束になります。
  public にするのは、自作ルールを書くのに要るものだけです。公開 API は `tests/Shaderlyn.Tests/PublicApi.txt` に記録してあり、
  変わると `PublicApiTests` が落ちます。意図した変更なら、環境変数 `SHADERLYN_UPDATE_PUBLIC_API=1` を付けてそのテストを実行し、
  書き直された差分を PR に含めてください

## 設計を変える

設計上の判断と採らなかった案は [docs/internals/design-decisions.md](docs/internals/design-decisions.md) にまとめています。
条件付きコンパイル (シンボル) の扱いの実装仕様は [docs/guide/conditional-compilation.md の付録](docs/guide/conditional-compilation.md#付録-実装の詳細) にあります。
既存の判断を覆す変更や、新しい仕組みを足す変更は、まず Issue で相談してください。
判断が決まったら、そのページを今の実装に合わせて直します。

## プルリクエスト

- 1 つのプルリクエストでは 1 つのことを変えてください
- `dotnet build` と `dotnet test` が手元で通ることを確かめてください
- コードのコメントとドキュメントは日本語で、コミットメッセージは英語で書いています。どちらの言語でも受け付けます

## リリースする (メンテナー向け)

`v` で始まるタグを push すると、[release.yml](.github/workflows/release.yml) が次を行います。手動では実行できません。

1. CLI と言語サーバーを 5 プラットフォーム向けに Native AOT でビルドし、VS Code 拡張の vsix を作る
2. すべて組めたら、`src/` のライブラリの NuGet パッケージを nuget.org へ公開する
3. GitHub のリリースを作り、アーカイブ・vsix・チェックサムを添付し、vsix を VS Code Marketplace へ公開する

手順:

1. [CHANGELOG.md](CHANGELOG.md) の「未リリース」を、出すバージョンと日付の見出しに直してコミットする
2. `git tag v0.1.0` のようにタグを打ち、`git push origin v0.1.0` で push する

NuGet へは Trusted Publishing で公開します。API キーは保存しません。最初のリリースの前に次を用意してください。

1. nuget.org で、ユーザー名のメニューの **Trusted Publishing** からポリシーを足す。
   Repository Owner は `OmojiP`、Repository は `shaderlyn`、Workflow File は `release.yml` (パスを付けない)、Environment は空にする。
   最初の公開でパッケージ ID が新しく作られるので、スコープで新しいパッケージの公開も許可する (対象は `Shaderlyn.*`)
2. GitHub のリポジトリのシークレット `NUGET_USER` に、nuget.org のプロフィール名を登録する (メールアドレスではない)

非公開のリポジトリで作ったポリシーは、7 日間だけの仮の状態で始まることがあります。その間に 1 度公開すると恒久的に有効になります。
期限が切れたら nuget.org の画面から延ばせます。
nuget.org に出したパッケージは消せない (一覧から隠せるだけ) ので、タグを打つ前に CI が通っていることを確かめてください。

VS Code Marketplace への公開には、リポジトリのシークレット `VSCE_PAT` が要ります。
Azure DevOps の Personal Access Token (Organization は All accessible organizations、Scopes は Marketplace の Manage) を登録してください。
トークンには有効期限があるので、切れたら作り直して登録し直します。Marketplace に出したバージョンも上書きできません。

配布物には [LICENSE](LICENSE) と [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を入れています。
ほかのプロジェクトのデータやコードを取り込んだときは、THIRD-PARTY-NOTICES.txt に追記してください。
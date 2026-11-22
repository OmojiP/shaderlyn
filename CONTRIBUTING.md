# 貢献の手引き

Shaderlyn への不具合の報告・提案・プルリクエストを歓迎します。

## 報告する

- **誤検出（正しいシェーダーに指摘が出る）** は、このツールで最も重い不具合です。
  「誤検出の報告」のテンプレートから、指摘の全文と、再現できる最小のシェーダーを添えて報告してください
- **見逃し（誤りなのに指摘が出ない）** も不具合です。Unity（FXC / DXC）が出したエラーを添えてもらえると確実です
- 脆弱性は Issue に書かず、[SECURITY.md](SECURITY.md) の方法で報告してください

## 開発を始める

[.NET 10 SDK](https://dotnet.microsoft.com/download) が必要です。

```bash
dotnet build
dotnet test
```

- **警告はすべてエラーになります**（`TreatWarningsAsErrors`）。公開メンバーの説明コメント（`///`）が無いとビルドが通りません
- **Native AOT で配布しています。** リフレクションに頼るコードはビルドで弾かれます（`IsAotCompatible`）
- VS Code 拡張を動かすには `editors/vscode` で `npm install` し、VS Code の「実行とデバッグ」から「VS Code 拡張を起動」を選びます
- Visual Studio 拡張と Rider プラグインは、言語サーバーを同梱して動かします。先に言語サーバーを publish します
  (Windows では、Visual Studio Installer のフォルダーを `PATH` に入れておきます。Native AOT のリンクで `vswhere.exe` を使用するためです)

  ```sh
  dotnet publish src/Shaderlyn.LanguageServer.App -c Release -r win-x64 -o <言語サーバーのフォルダー>
  ```

- Visual Studio 拡張は、Visual Studio 2026 の「Visual Studio 拡張機能の開発」ワークロードの MSBuild でビルドし、実験用のインスタンス (Exp) に配置します。
  配置のあとに `/updateconfiguration` を実行しないと、拡張機能の管理に出ないことがあります。無効になっていたら、拡張機能の管理で有効にします

  ```sh
  msbuild editors/visualstudio/Shaderlyn.VisualStudio/Shaderlyn.VisualStudio.csproj -restore -p:DeployExtension=true -p:VSSDKTargetPlatformRegRootSuffix=Exp -p:ShaderlynServerDirectory=<言語サーバーのフォルダー>
  devenv /rootsuffix Exp /updateconfiguration
  devenv /rootsuffix Exp <開くフォルダー>
  ```

  Debug のビルドでは、環境変数 `SHADERLYN_LSP_LOG` にフォルダーを指定すると、言語サーバーとのやり取りをそこへ書き出します
- Rider プラグインは JDK 21 と Gradle 9.9 でビルドします (Gradle はリポジトリに入れません)。`runIde` で、プラグインを入れたサンドボックスの Rider が起動します

  ```sh
  cd editors/rider
  gradle runIde -PriderLocalPath=<Rider のフォルダー> -PserverDirectory=<言語サーバーのフォルダー> -PopenPath=<開くフォルダー>
  ```

  `gradle verifyPlugin` で、Plugin Verifier による互換性の確認を行います (JetBrains Marketplace の審査の条件)。
  内部 API の使用や、プラグインの ID の問題 (製品名を含むなど) があると失敗します

- Visual Studio 拡張と Rider プラグインの画面の動作は、自動テストで確認していません。言語サーバーやエディタの拡張を変えたときは、次を手で確認します
  - `.shader` を開くと指摘が出て、編集・保存で更新される。クイックフィックスが働く。Visual Studio ではホバーと補完も働く
  - Visual Studio 2026 で HLSL Tools が入っている場合、`.hlsl` で HLSL Tools の指摘と Shaderlyn の指摘が並ぶ
  - Visual Studio 2022 で、シェーダーのタブを開いたまま閉じ、起動し直しても指摘とホバーが出る (起動時のタブの復元)
  - 設定の「実行ファイル」に存在しないパスを指定すると、そのことが伝えられ、同梱の言語サーバーで動き続ける
- Unity がインストールされていなくてもテストは通ります。Unity の実物を使う検証は、見つからなければ飛ばされます
- DirectX 12 の公開サンプルを使う検証 (誤検出が無いか) は、コーパスを取ってきて環境変数で渡したときだけ動きます。コーパスはリポジトリに入れません (`.work/` は git の対象外)

  ```sh
  node tools/corpus/fetch.mjs
  SHADERLYN_HLSL_CORPUS=.work/corpus dotnet test --filter "FullyQualifiedName~ExternalCorpusTests"
  ```

  指摘の一覧は `.work/corpus/report-<名前>.txt` に書き出されます
- GLSL の組み込みの関数と変数の表 (`src/Shaderlyn.Semantics/Symbols/GlslBuiltins.g.txt`) は、glslang から作ります。
  glslang はリポジトリに入れないので、[リリース](https://github.com/KhronosGroup/glslang/releases)から取って、実行ファイルを環境変数で渡します。
  表の先頭に、作った glslang のバージョンが書いてあります

  ```sh
  # 表を作り直す
  GLSLANG=<glslang の実行ファイル> node tools/glsl/builtins.mjs

  # 作り直しても表が変わらないことと、GLSL のコーパスの関数の呼び出しがすべて解決することを確認する
  SHADERLYN_GLSLANG=<glslang の実行ファイル> SHADERLYN_GLSL_CORPUS=.work/corpus dotnet test --filter "FullyQualifiedName~GlslBuiltinsTests"
  ```
- システム値のセマンティクスの表 (`src/Shaderlyn.Semantics/Symbols/SystemValueSemantics.g.txt`。CF0302 が使用する) は、
  FXC・DXC・slangc に組み合わせをコンパイルさせて作ります。FXC と DXC は Windows SDK のもの、slangc は環境変数か PATH で渡します。
  数千回コンパイルするので時間がかかります。表の先頭に、作ったコンパイラのバージョンが書いてあります

  ```sh
  SHADERLYN_SLANGC=<slangc の実行ファイル> node tools/semantics/system-values.mjs .work/system-values
  # 結果を残した作業フォルダーから、表だけを作り直す
  node tools/semantics/system-values.mjs .work/system-values --reuse
  ```
- GLSL のコーパス (Vulkan と GLSL ES のシェーダー) で、誤りと警告が出ないこと (誤検出が無いこと) を確認する

  ```sh
  node tools/corpus/fetch.mjs --only=glsl
  SHADERLYN_GLSL_CORPUS=.work/corpus dotnet test --filter "FullyQualifiedName~GlslCorpusTests"
  ```
- Unity をインストールしてあれば、Unity 同梱のシェーダー (約 400 件) を使う検証も動きます。時間がかかるので、ふだんの確認では外せます

  ```sh
  # ふだんの確認 (20 秒ほど)
  dotnet test --filter "FullyQualifiedName!~CorpusTests&FullyQualifiedName!~AnalysisSnapshotTests"

  # Unity 同梱のシェーダーで誤検出と例外を確認する (10 分ほど)
  dotnet test --filter "FullyQualifiedName~CorpusTests&FullyQualifiedName!~ExternalCorpusTests&FullyQualifiedName!~UnityProbeCorpusTests"

  # 指摘の全件を記録と比較する (記録はリポジトリに入れない。違えば <パス>.received に今の結果を書き出す)
  SHADERLYN_SNAPSHOT=.work/snapshot/unity.txt dotnet test --filter "FullyQualifiedName~AnalysisSnapshotTests"

  # Unity の実測 (ShaderProbePack のレポート) と、コンパイラの判定を比較する
  SHADERLYN_UNITY_PROBE=<プローブのプロジェクトを置いたフォルダー> dotnet test --filter "FullyQualifiedName~UnityProbeCorpusTests"
  ```

  記録と違ったときは、指摘の ID と位置が変わっていないこと (文だけの変更なら) を確認してから、`.received` で記録を置き換えます

## ルールを追加する・修正する

組み込みルールを追加するときは、次がすべて揃うまでテストが落ちます。

| 必要なもの | 確認しているテスト |
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
  public にするのは、自作ルールを書くのに必要なものだけです。公開 API は `tests/Shaderlyn.Tests/PublicApi.txt` に記録してあり、
  変わると `PublicApiTests` が落ちます。意図した変更なら、環境変数 `SHADERLYN_UPDATE_PUBLIC_API=1` を付けてそのテストを実行し、
  書き直された差分を PR に含めてください

## 設計を変える

設計上の判断と採らなかった案は [docs/internals/design-decisions.md](docs/internals/design-decisions.md) にまとめています。
条件付きコンパイル (シンボル) の扱いの実装仕様は [docs/guide/conditional-compilation.md の付録](docs/guide/conditional-compilation.md#付録-実装の詳細) にあります。
既存の判断を覆す変更や、新しい仕組みを追加する変更は、まず Issue で相談してください。
判断が決まったら、そのページを今の実装に合わせて更新します。

## プルリクエスト

- 1 つのプルリクエストでは 1 つのことを変えてください
- `dotnet build` と `dotnet test` が手元で通ることを確認してください
- コードのコメント、ドキュメント、コミットメッセージは日本語で書いています。どちらの言語でも受け付けます

## リリースする (メンテナー向け)

`v` で始まるタグを push すると、[release.yml](.github/workflows/release.yml) が次を行います。手動では実行できません。

1. CLI と言語サーバーを 6 プラットフォーム向けに Native AOT でビルドし、言語サーバーの起動を確認して、VS Code 拡張の vsix を作る
2. Visual Studio 拡張の vsix (言語サーバーは win-x64 と win-arm64) と、Rider プラグインの OS と CPU の組ごとの zip (6 つ) を作る
3. すべて組めたら、`src/` のライブラリの NuGet パッケージを nuget.org へ公開する
4. GitHub のリリースを作り、アーカイブ・vsix・zip・チェックサムを添付し、VS Code 拡張を VS Code Marketplace へ公開する
5. Visual Studio 拡張を Visual Studio Marketplace へ、Rider プラグインを JetBrains Marketplace へ公開する

手順:

1. [CHANGELOG.md](CHANGELOG.md) の「未リリース」を、出すバージョンと日付の見出しに変える
2. リリースの概要を `docs/releases/v<バージョン>.md` に書く (下の「リリースの概要」)。1 と一緒にコミットする
3. `git tag v0.1.0` のようにタグを打ち、`git push origin v0.1.0` で push する

### リリースの概要

リリースの本文の先頭には、[docs/releases](docs/releases) のそのバージョンのファイルを置きます。その下に、GitHub が作る PR の一覧が付きます。
ファイルがない場合や、英語と日本語を分ける `---` の行がない場合は、NuGet へ公開する前にリリースのワークフローが止まります。

- 英語を先に書き、`---` の行のあとに同じ内容の日本語を書く
- 1 行の要約と、目玉の変更を 3〜7 個の箇条書きにする。主なものは太字にする
- 破壊的変更がある場合は、移行の手順へのリンクを付けた箇条を加える
- 最後に CHANGELOG のそのバージョンの見出しへのリンクを付ける。英語の側から日本語のページへのリンクには「(in Japanese)」を添える
- 日本語は体言止めにする (「〜に対応」「〜を追加」「〜が使用可能」)

書き方は、これまでのバージョンのファイルを参照してください。

NuGet へは Trusted Publishing で公開します。API キーは保存しません。最初のリリースの前に次を用意してください。

1. nuget.org で、ユーザー名のメニューの **Trusted Publishing** からポリシーを追加する。
   Repository Owner は `OmojiP`、Repository は `shaderlyn`、Workflow File は `release.yml` (パスを付けない)、Environment は空にする。
   最初の公開でパッケージ ID が新しく作られるので、スコープで新しいパッケージの公開も許可する (対象は `Shaderlyn.*`)
2. GitHub のリポジトリのシークレット `NUGET_USER` に、nuget.org のプロフィール名を登録する (メールアドレスではない)

非公開のリポジトリで作ったポリシーは、7 日間だけの仮の状態で始まることがあります。その間に 1 度公開すると恒久的に有効になります。
期限が切れたら nuget.org の画面から延ばせます。
nuget.org に出したパッケージは消せない (一覧から隠せるだけ) ので、タグを打つ前に CI が通っていることを確認してください。

VS Code Marketplace と Visual Studio Marketplace へも Trusted Publishing で公開します (Azure DevOps の PAT は使用しません)。
最初のリリースの前に、[Marketplace の発行者の管理画面](https://marketplace.visualstudio.com/manage/publishers) で発行者 `OmojiP` を開き、
**Trusted Publishing** からポリシーを作成してください。Repository Owner は `OmojiP`、Repository は `shaderlyn`、Workflow は `release.yml`、Environment は空にします。
1 つのポリシーで、VS Code 拡張 (`vsce publish --oidc`) と Visual Studio 拡張 (`VsixPublisher.exe publish -oidc`) の両方を公開できます。
VS Code Marketplace に出したバージョンは上書きできません。Visual Studio Marketplace は、同じバージョンを出し直すと上書きします。

JetBrains Marketplace へは、最初のバージョンだけ Web から手で上げる決まりです。
[JetBrains Marketplace](https://plugins.jetbrains.com/) で Developer Agreement に同意してベンダーのプロフィールを作り、
リリースに添付された `shaderlyn-rider-<バージョン>-<OS>-<CPU>.zip` を上げます (ライセンスは MIT、ソースのリンクはこのリポジトリ)。
審査が通ったら、プロフィールの **My Tokens** でトークンを作り、リポジトリのシークレット `JETBRAINS_MARKETPLACE_TOKEN` に登録します。
登録した後のリリースでは、6 つの zip を Marketplace へ送ります。シークレットが無い間は送りません。公開は Marketplace の審査の後になります。

Rider の zip には、リリースのワークフローが作者の署名を付けます ([tools/rider/sign-zips.sh](tools/rider/sign-zips.sh))。
署名が無いと、インストールのときに IDE が警告を出します。最初のリリースの前に、証明書と秘密鍵を作ってシークレットに登録してください。
秘密鍵とパスフレーズはリポジトリの外に保管します。

```sh
# 秘密鍵 (パスフレーズを聞かれる)
openssl genpkey -aes-256-cbc -algorithm RSA -out private_encrypted.pem -pkeyopt rsa_keygen_bits:4096
# 証明書 (有効期間 10 年)。Git Bash では、/CN=… がパスに書き換えられないよう MSYS_NO_PATHCONV=1 を付ける
MSYS_NO_PATHCONV=1 openssl req -key private_encrypted.pem -new -x509 -days 3650 -out chain.crt -subj "/CN=OmojiP"
```

| シークレット | 値 |
| --- | --- |
| `JETBRAINS_CERTIFICATE_CHAIN` | `chain.crt` の中身 (`-----BEGIN CERTIFICATE-----` から) |
| `JETBRAINS_PRIVATE_KEY` | `private_encrypted.pem` の中身 (`-----BEGIN ENCRYPTED PRIVATE KEY-----` から) |
| `JETBRAINS_PRIVATE_KEY_PASSWORD` | 秘密鍵のパスフレーズ |

シークレットが無い間は、署名せずに警告を出します。署名の処理は、CI でも使い捨ての鍵で毎回動かしています。

配布物には [LICENSE](LICENSE) と [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を入れています。
ほかのプロジェクトのデータやコードを取り込んだときは、THIRD-PARTY-NOTICES.txt に追記してください。
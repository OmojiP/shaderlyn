# Shaderlyn

Unity の ShaderLab / HLSL / コンピュートシェーダーを解析する [Shaderlyn](https://github.com/OmojiP/shaderlyn) の、自作ルール用ライブラリです。

プロジェクト固有の規約を C# のルールとして書き、組み込みのルールと一緒に動く CLI や言語サーバーを作れます。
解析ツールとして使うだけなら、このパッケージは要りません。[リリースページ](https://github.com/OmojiP/shaderlyn/releases) の実行ファイルを使ってください。

| パッケージ | 用途 |
| --- | --- |
| `Shaderlyn.Semantics` | ルールを書く。`HlslRuleAnalyzer` とセマンティックモデル |
| `Shaderlyn.Testing` | ルールをテストする。`ShaderRuleVerifier` |
| `Shaderlyn.Cli` | 自作ルール入りの CLI を作る。`Program.RunAsync` |
| `Shaderlyn.LanguageServer` | 自作ルール入りの言語サーバーを作る。`ShaderLanguageServer` |

ほかのパッケージ (`Shaderlyn.Core` など) は、上のパッケージから依存として入ります。

- [自作ルールの作り方](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/tutorial.md)
- [ルールの実例集](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/cookbook.md)
- [ルール API リファレンス](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/api-reference.md)

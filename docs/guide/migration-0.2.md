# 0.1 から 0.2 へ移行する

0.1.x から 0.2.0 へ上げるときに、何を書き換えるかをまとめたページです。
変更の一覧は [CHANGELOG](../../CHANGELOG.md) にあります。

## 目次

- [書き換えが要るか](#書き換えが要るか)
- [新しく出る指摘](#新しく出る指摘)
- [設定ファイル](#設定ファイル)
- [CLI と CI](#cli-と-ci)
- [VS Code 拡張](#vs-code-拡張)
- [自作ルール](#自作ルール)
- [自前の CLI と言語サーバー](#自前の-cli-と言語サーバー)
- [Unity 以外の環境を使い始める](#unity-以外の環境を使い始める)

## 書き換えが要るか

**0.1.1 で出ていた指摘は、ID・位置・文がそのままです。** ベースラインもそのまま使えます。
ただし、パスがどのコンパイラ (FXC / DXC) でコンパイルされるかを判定するようになったので、
新しく出る指摘と、誤検出として出なくなる指摘があります ([下記](#新しく出る指摘))。
Unity 同梱のシェーダーでは、解析結果は 0.1.1 と同じです。

| 使い方 | 書き換え |
| --- | --- |
| CLI・GitHub Action・VS Code 拡張で使っている | 設定ファイルに `profile` を書いていれば、`unity:` の節の下へ移す ([下記](#設定ファイル))。ほかは要らない |
| 自作ルールを書いている | Unity の情報 (`Properties`・`Pass`・ShaderLab の木・プロファイル) を見ているなら、引き方を変える ([下記](#自作ルール)) |
| 自作ルール入りの CLI・言語サーバーを作っている | 要らない。`Program.RunAsync` と `ShaderLanguageServer` の呼び方は 0.1 のまま使える |

## 新しく出る指摘

Unity の環境で、0.2 から出るようになった指摘です。どれも Unity 2022.3〜6.6 の実測に基づきます。

| ID | 内容 |
| --- | --- |
| [HL0003](../rules/HL0003.md) | FXC に届くコードの `int64_t` / `float32_t` / `float16_t` などの型、interface 型のグローバル変数、DXC に届くエントリ関数の `uniform` 引数。`#pragma use_dxc` のパスが DX11 の FXC にも届くときの `enum` |
| [HL0346](../rules/HL0346.md) | DXC に届く構造体の `++` / `--` |
| [HL0348](../rules/HL0348.md) | FXC だけに届く構造体の `++` / `--` (エラーにならず何もしない) |
| [SL1050](../rules/SL1050.md) | `#pragma use_dxc` / `require` が無視される (`use_dxc d3d12` など) |
| [SL1053](../rules/SL1053.md) | DX11 で使えなくなるコンパイラの指定 (キーワードの条件の無い、DXC が要る `require` など) |
| [SL1054](../rules/SL1054.md) | Unity のバージョンでコンパイラが変わる指定 (`never_use_dxc` と DXC が要る `require`) |
| [SL1051](../rules/SL1051.md) | コンパイラの指定についての注意 (Unity のバージョンが分かるときだけ) |
| [SL1052](../rules/SL1052.md) | 16 ビットの型を有効にしたパスの定数バッファの `half` |

出なくなる指摘 (0.1 の誤検出): `class` / `interface` の宣言 (どの方言でも使える)、`#pragma use_dxc` などで DXC になるパスの `enum`。

**対象の API は、既定で DX11 と DX12 です。** DX12 だけのプロジェクトで DX11 についての指摘が出るなら、
`--unity-project` でプロジェクトを渡すか (`ProjectSettings.asset` の Windows の API を読みます)、設定ファイルに書いてください。

```yaml
unity:
  version: 6.3              # 無ければ ProjectSettings/ProjectVersion.txt から読む
  graphics-apis: [dx12]
```

## 設定ファイル

`profile` は Unity の環境だけの設定になったので、`unity:` の節の下に書きます。

```yaml
# 0.1
version: 1
profile: urp

# 0.2
version: 1
unity:
  profile: urp
```

> **移さないと CI が落ちることがあります。**
> 節の外の `profile` も読みますが、移すよう伝える警告 (`TOOL0003`) が出ます。
> 既定の `--error-on warning` では、この警告でも終了コードが `1` になります。上げる前に移してください。

`--unity-project` と `--unity-editor` に当たる設定も、`unity:` の節の `project` / `editor` に書けます (0.2 で足したもの。移す必要はありません)。

## CLI と CI

- オプションはそのまま使えます。`--profile` / `--unity-project` / `--unity-editor` は Unity の環境だけのものになりましたが、Unity の環境 (既定) では今までどおりです
- `--cache` のキャッシュは、ツールが変わったことを見て自動で作り直されます。消す必要はありません
- GitHub Action の入力もそのままです。Unity 以外の環境のための入力 `environment` が増えました

## VS Code 拡張

拡張と言語サーバーは一緒に上がります (Marketplace の拡張に同梱)。
自作の言語サーバーを `shaderlyn.serverPath` で使っているなら、言語サーバーも 0.2 で作り直してください。

拡張とサーバーの版がずれても、Unity のシェーダーでは今までどおり動きます。
0.1 の拡張と 0.2 のサーバーの組では、Unity 以外の環境のために足したもの (`.usf` / `.ush` / `.hlsli` を開くこと、
値を持つシンボルを `QUALITY = 2` と表示すること) が働きません。

## 自作ルール

Unity 固有の部分を `Shaderlyn.Unity` パッケージへ分けました。
**Unity の情報を見るルールは、`Shaderlyn.Unity` パッケージを参照に足してください。** (`Shaderlyn.Cli` を参照しているプロジェクトには、もう入っています。)

```xml
<PackageReference Include="Shaderlyn.Semantics" Version="0.2.0" />
<PackageReference Include="Shaderlyn.Unity" Version="0.2.0" />
```

### 引き方の対応

| 0.1 | 0.2 |
| --- | --- |
| `compilation.Properties` | `compilation.GetUnity()?.Properties` |
| `compilation.ShaderLabTree` | `compilation.GetUnity()?.Tree` |
| `compilation.ShaderName` / `Profile` / `IsStandaloneHlsl` | `compilation.GetUnity()` の同じ名前のメンバー |
| `compilation.IsReferencedFromShaderLab(name)` / `IsDeclaredButNeverUsed(name)` / `CountIdentifierOccurrences(name)` / `GetMaterialConstantBuffers()` | `compilation.GetUnity()` の同じ名前のメソッド |
| `program.Pass` / `program.PassName` / `program.Block` / `program.IncludeBlocks` | `program.GetUnityBlock()` の同じ名前のメンバー |
| `ShaderCompilation.Create(text, tree, options)` | `UnityCompilation.Create(text, tree, options)` |
| `ShaderCompilation.CreateForHlsl(text, options)` | `UnityCompilation.CreateForHlsl(text, options)` |
| `SemanticsOptions.Profile` / `UseFallbackMacros` | `UnityOptions` の同じ名前のプロパティ。`UnityCompilation.Create` の最後の引数に渡す |
| `SemanticsOptions.DefaultPredefinedMacros` | `UnityOptions.DefaultPredefinedMacros` |

型の置き場所 (名前空間) も変わりました。`using` を書き換えてください。

| 型 | 0.1 | 0.2 |
| --- | --- | --- |
| `PropertySymbol` / `ShaderPropertyKind` | `Shaderlyn.Semantics.Symbols` | `Shaderlyn.Unity` |
| `IRenderPipelineProfile` | `Shaderlyn.Semantics.Profiles` | `Shaderlyn.Unity.Profiles` |
| `ShaderSymbols` | `Shaderlyn.Hlsl.Preprocessing` | `Shaderlyn.Unity` |
| `SymbolGroup` | `Shaderlyn.Hlsl.Preprocessing` | 無くなった。構成の空間 `program.Variants` (`VariantSpace` / `VariantGroup`、`Shaderlyn.Core.Syntax`) を使う |

### 書き換えの例

```csharp
// 0.1
foreach (PropertySymbol property in compilation.Properties)
{
    ...
}

// 0.2
// Unity 以外の環境 (汎用の HLSL・Unreal) で作ったモデルでは null。そのときは何もしない
if (compilation.GetUnity() is not { } unity)
{
    return;
}

foreach (PropertySymbol property in unity.Properties)
{
    ...
}
```

```csharp
// 0.1
string? pass = program.PassName;

// 0.2
string? pass = program.GetUnityBlock()?.PassName;
```

```csharp
// 0.1
ShaderCompilation compilation = ShaderCompilation.Create(text, tree, new SemanticsOptions { Profile = profile });

// 0.2
ShaderCompilation compilation = UnityCompilation.Create(text, tree, new SemanticsOptions(), new UnityOptions { Profile = profile });
```

**Unity の情報が無いときに何もしないようにしてください。** 0.2 では、同じルールが Unity 以外の環境でも動きます。
`GetUnity()` / `GetUnityBlock()` が `null` のときに例外を投げると、その環境のファイルで解析が止まります。

HLSL のコードだけを見るルール (`HlslRuleAnalyzer` で式や宣言を見るもの) は、書き換えなくても動きます。どの環境でも同じように働きます。

### テスト

`ShaderRuleVerifier` (`Shaderlyn.Testing`) の使い方は変わりません。今までどおり Unity の環境で解析します。
Unity 以外の環境でルールを確かめるときは、`ShaderCompilation.Create` に環境 (`HlslEnvironment` など) を渡してモデルを作り、
`AnalyzerDriver` で動かします ([API リファレンス「環境」](../custom-rules/api-reference.md#環境))。

## 自前の CLI と言語サーバー

`Program.RunAsync(args, analyzers, output, error)` と `new ShaderLanguageServer(connection, analyzers)` は 0.1 のまま使えます。
独自エンジンの環境を足すための多重定義が増えました
([ルールの実例集「独自エンジンの環境」](../custom-rules/cookbook.md#独自エンジンの環境))。

## Unity 以外の環境を使い始める

DXC で直接コンパイルする HLSL と Unreal Engine は、[Unity 以外の環境で使う](environments.md) を見てください。
環境ごとの設定ファイルのテンプレートが [samples/environments](../../samples/environments/) にあります。

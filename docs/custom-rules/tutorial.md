# 自作ルールの作り方

プロジェクト固有の規約を検査するルールを自分で書き、CLI とエディタで動かすまでの手順書です。
最初のルールが動いたら、[自作ルールの書き方の手引き](guide.md) に進んでください。

## 目次

- [自作ルールの作り方](#自作ルールの作り方)
  - [目次](#目次)
  - [1. 実例集を利用する](#1-実例集を利用する)
  - [2. プロジェクトを用意する](#2-プロジェクトを用意する)
  - [3. ルールを書く](#3-ルールを書く)
    - [3-1. ルールを定義する](#3-1-ルールを定義する)
    - [3-2. アナライザを作成](#3-2-アナライザを作成)
    - [3-3. 解析結果を確認する](#3-3-解析結果を確認する)
  - [4. テストを書く](#4-テストを書く)
  - [5. 自作ルール入りの CLI を作る](#5-自作ルール入りの-cli-を作る)
    - [5-1. エディタでも働かせる](#5-1-エディタでも働かせる)
      - [サーバーのプロジェクトを作る](#サーバーのプロジェクトを作る)
      - [Program.cs を書く](#programcs-を書く)
      - [publish する](#publish-する)
  - [6. 次に読むもの](#6-次に読むもの)
  - [関連](#関連)

---

## 1. 実例集を利用する

使いたいものに近い例が [ルールの実例集](cookbook.md) にあれば、そのまま使用するか、調整することを推奨します。

実例集の実物は [samples/Shaderlyn.Cookbook](../../samples/Shaderlyn.Cookbook) にあります。
[2 章](#2-プロジェクトを用意する)のプロジェクトにファイルをコピーしてください。
診断の定義は `CookbookRules.cs` にまとめてあるので、一緒に入れるか差し替えてください。
テスト (`tests/Shaderlyn.Tests/CookbookTests.cs`) も、[4 章](#4-テストを書く)のテストプロジェクトにコピーすることで利用できます。

---

## 2. プロジェクトを用意する

必要なライブラリは NuGet パッケージとして配っています。
ルールを書くクラスライブラリを 1 つ作り、`Shaderlyn.Semantics` を参照します。

```bash
dotnet new classlib -o MyShaderRules
dotnet add MyShaderRules package Shaderlyn.Semantics
```

`MyShaderRules.csproj` は次のようになります。

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Shaderlyn.Semantics" Version="(使うバージョン)" />
  </ItemGroup>

</Project>
```

| パッケージ                 | 必要な場面                                                          |
| -------------------------- | ------------------------------------------------------------------- |
| `Shaderlyn.Semantics`      | ルールを書くとき。`Shaderlyn.Core` と `ShaderLab` / `Hlsl` も入ります |
| `Shaderlyn.Testing`        | テストを書くとき (テストプロジェクト側で)                          |
| `Shaderlyn.Cli`            | 自作ルール入りの CLI を作るとき ([5 章](#5-自作ルール入りの-cli-を作る)) |
| `Shaderlyn.LanguageServer` | 自作ルール入りの言語サーバーを作るとき ([5-1](#5-1-エディタでも働かせる)) |

**パッケージのバージョンはすべて揃えてください。** Shaderlyn のパッケージは同じバージョンで一緒にリリースしており、
バージョンの違うものを混ぜた組み合わせは確かめていません。

---

## 3. ルールを書く

「`half` 系の型を使わない」というルールを書いてみます。

### 3-1. ルールを定義する

```csharp
using Shaderlyn.Core.Diagnostics;

internal static class MyRules
{
    public static DiagnosticDescriptor NoHalf { get; } = new(
        id: "MY0001",
        title: "half 系の型を使わない",
        messageFormat: "型 '{0}' は使わないでください。float を使ってください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "このプロジェクトは精度差による見た目の破綻を避けるため half を使いません。"
            + "モバイル向けの最適化が必要な箇所は、レビューで個別に判断します。");
}
```

- ID は「英大文字の接頭辞 + 4 桁」です。組み込みの `SL` / `HL` / `URP` / `TOOL` と
  ぶつからない接頭辞を選んでください
- 詳細ページがあるなら `helpLinkUri` も渡してください

### 3-2. アナライザを作成

HLSL のノードを見るので `HlslRuleAnalyzer` を継承します。

解析タイミングと発火条件を指定して処理を登録します。

型の一覧は [ShaderLab のノード型](api-reference.md#shaderlab-のノード型) と
[HLSL のノード型](api-reference.md#hlsl-のノード型) にあります。

基底型で登録すると派生型すべてで発火します。
「式すべて」を見たいなら `HlslExpressionSyntax`、「文すべて」なら `HlslStatementSyntax` です。


```csharp
using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

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

### 3-3. 解析結果を確認する

アナライザーが想定の動作をしない場合や、解析条件の書き方がわからないときは、

オプション `--inspect` で解析結果を確認することを推奨します。

想定と異なる構造をしていないか、発火に使用する型はどれが適切か等の判断に利用できます。


```bash
shaderlyn Assets/Shaders/Lit.shader --unity-project . --inspect tree.html
```

![解析の中身を表示して構文木や型判定の結果を確かめる様子](../../editors/vscode/images/inspect.gif)

---

## 4. テストを書く

テストプロジェクトを作り、`Shaderlyn.Testing` とルールのプロジェクトを参照します。
例は xUnit で書いていますが、`ShaderRuleVerifier` は指摘を返すだけなので、テストフレームワークは問いません。

```bash
dotnet new xunit -o MyShaderRules.Tests
dotnet add MyShaderRules.Tests package Shaderlyn.Testing
dotnet add MyShaderRules.Tests reference MyShaderRules
```

```csharp
using Shaderlyn.Testing;

public sealed class NoHalfAnalyzerTests
{
    private const string Shader = """
        Shader "Test/Sample"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                    half4 frag() : SV_Target { return 1; }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public void half4_を報告する()
    {
        var diagnostics = new ShaderRuleVerifier(new NoHalfAnalyzer()).Analyze(Shader);

        Assert.Equal("MY0001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void float_だけなら報告しない()
    {
        var diagnostics = new ShaderRuleVerifier(new NoHalfAnalyzer())
            .Analyze(Shader.Replace("half4", "float4", StringComparison.Ordinal));

        Assert.True(diagnostics.IsEmpty, ShaderRuleVerifier.Describe(diagnostics));
    }
}
```

| `ShaderRuleVerifier`        | できること                                                                    |
| --------------------------- | ----------------------------------------------------------------------------- |
| `AddInclude(path, content)` | `#include` で引けるファイルをその場で与える。ディスクを読まない               |
| `WithOptions(options)`      | 重要度の上書きなど、設定ファイル相当の設定を与える                            |
| `WithPredefinedMacros(...)` | 解析開始時に定義済みとするマクロを与える                                      |
| `Analyze(source, filePath)` | 解析して指摘を返す。`.compute` を渡せばコンピュートシェーダーとして解析される |
| `Describe(diagnostics)`     | 表明が落ちたときに何が出ていたかを見せる                                      |

---

## 5. 自作ルール入りの CLI を作る

自分用の CLI のプロジェクトを作り、`Shaderlyn.Cli` とルールのプロジェクトを参照します。

```bash
dotnet new console -o MyShaderRules.Cli
dotnet add MyShaderRules.Cli package Shaderlyn.Cli
dotnet add MyShaderRules.Cli reference MyShaderRules
```

`MyShaderRules.Cli.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>my-shaderlyn</AssemblyName>

    <!-- 組み込みの shaderlyn と同じ設定。単一バイナリにし、並列の解析で GC が詰まらないようにする -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Shaderlyn.Cli" Version="(使うバージョン)" />
    <ProjectReference Include="..\MyShaderRules\MyShaderRules.csproj" />
  </ItemGroup>

</Project>
```

**サーバー GC の設定は省かないでください。** 解析はファイルを並列に処理し、割り当て量が多いため、
既定のワークステーション GC では回収が律速になり、コアを増やしても速くなりません。
この設定は実行ファイルの側で決まるもので、パッケージからは引き継がれません。

`Program.cs` で、作成したアナライザを渡して `Program.RunAsync` を呼びます。

```csharp
using System.Collections.Immutable;
using System.Text;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;

// Windows の既定のコードページのままだと、指摘の日本語が文字化けする。
// コンソールの無い環境では設定に失敗するが、解析には関係ないのでそのまま続ける
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
{
}

ImmutableArray<DiagnosticAnalyzer> analyzers =
[
    .. BuiltInAnalyzers.All,      // 組み込みルール
    new NoHalfAnalyzer(),         // 自作ルール
];

return (int)await Shaderlyn.Cli.Program.RunAsync(args, analyzers, Console.Out, Console.Error);
```

`Program.RunAsync` は `Shaderlyn.Cli.Program` と書いてください。トップレベルのステートメントで書いた `Program.cs` は、
それ自体が `Program` という名前のクラスになるため、`Program.RunAsync` だけでは自分自身を指します。

publish すると、組み込みと同じ単一バイナリになります。

```bash
dotnet publish MyShaderRules.Cli -c Release -r win-x64 -o bin
```

`-r` は `win-x64` / `osx-arm64` / `osx-x64` / `linux-x64` / `linux-arm64` から選びます。
Native AOT はクロスコンパイルできないので、対象と同じ OS で publish します。
AOT にしない場合は `PublishAot` を外すか、`-p:PublishAot=false` を付けてください。

リリースと CI での実行方法は [CI に組み込む](../guide/ci-setup.md) にあります。

### 5-1. エディタでも働かせる

VS Code 拡張に同梱されている言語サーバーは組み込みルールのみを解析します。

自作ルール診断をエディタで行う場合は、自作ルールを含む言語サーバーをビルドします。

#### サーバーのプロジェクトを作る

`Shaderlyn.LanguageServer` とルールのプロジェクトを参照する言語サーバーのプロジェクトを作ります。

```bash
dotnet new console -o MyShaderRules.Lsp
dotnet add MyShaderRules.Lsp package Shaderlyn.LanguageServer
dotnet add MyShaderRules.Lsp reference MyShaderRules
```

`MyShaderRules.Lsp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>my-shader-rules-lsp</AssemblyName>

    <!-- 同梱の言語サーバーと同じ設定 -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Shaderlyn.LanguageServer" Version="(使うバージョン)" />
    <ProjectReference Include="..\MyShaderRules\MyShaderRules.csproj" />
  </ItemGroup>

</Project>
```

#### Program.cs を書く

同梱サーバーの [Program.cs](../../src/Shaderlyn.LanguageServer.App/Program.cs) と同じものに、自分のルールを足します。

```csharp
using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.LanguageServer;
using Shaderlyn.LanguageServer.Protocol;

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

ImmutableArray<DiagnosticAnalyzer> analyzers =
[
    .. BuiltInAnalyzers.All,      // 組み込みルール
    new NoHalfAnalyzer(),         // 自作ルール
];

ShaderLanguageServer server = new(new LspConnection(input, output), analyzers);

try
{
    return await server.RunAsync(cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 0;
}
```

#### publish する

```bash
dotnet publish MyShaderRules.Lsp -c Release -r win-x64 -o bin
```

`-r` は CLI と同じく `win-x64` / `osx-arm64` / `osx-x64` / `linux-x64` / `linux-arm64` から選びます。

**publish するプロジェクトを指定してください。** ソリューションのあるフォルダーで引数なしに実行すると、
テストを含む全プロジェクトが 1 つのフォルダーへ出力されます（`NETSDK1194` の警告が出ます）。

publish した実行ファイルを VS Code から使う手順は [VS Code 拡張の README](../../editors/vscode/README.md#自作ルールを使う) にあります。

---

## 6. 次に読むもの

- [自作ルールの書き方の手引き](guide.md) — ShaderLab を見るルール、ファイル単位の集計、セマンティックモデル、外部データの受け渡し、ルールを書くときの原則
- [ルールの実例集](cookbook.md) — 書きたいものに近い例
- [ルール API リファレンス](api-reference.md) — 型とメンバーの一覧
- [トラブルシューティング「自作ルール」](../guide/troubleshooting.md#自作ルール) — 指摘が出ない、TOOL0001 / 0005 / 0006 が出る

---

## 関連

- [ルールの実例集](cookbook.md) — 写して直せば動く例
- [CI に組み込む](../guide/ci-setup.md) — 自作ルール入りのツールをリリースし、CI で使う方法
- [ルール API リファレンス](api-reference.md) — 型とメンバーの一覧
- [設計判断「配布と拡張」](../internals/design-decisions.md#配布と拡張) — なぜ C# で書き、DLL を動的に読み込まないか

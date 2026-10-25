using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// コンパイラの指定 (<c>#pragma use_dxc</c> / <c>never_use_dxc</c> / <c>require</c>) を、対象の API と Unity のバージョンに照らして検査する
/// (UN0201 / UN0202 / UN0203 / UN0204)。
/// </summary>
/// <remarks>
/// <para>規則は Unity 2022.3〜6.6 の実測による (<see cref="UnityCompilerSelection"/>)。</para>
/// <para>
/// <b>文はどれも 3 文で書く。</b>何が起きているか、何でコンパイルされるか、どう直すか、の順である。
/// テキストの出力は「。」ごとに改行するので、1 文が 1 行になる。
/// </para>
/// <para>
/// <b>バージョンで変わるものは、バージョンが分かるときだけ報告する。</b>
/// 分からないまま報告すると、そのバージョンを使っていない利用者には誤りになる。
/// DX11 の扱いは、対象の API (<see cref="UnityOptions.GraphicsApis"/>) に DX11 があるときだけ見る。
/// </para>
/// </remarks>
internal sealed class CompilerPragmaAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            UnityRuleDescriptors.CompilerPragmaIgnored,
            UnityRuleDescriptors.CompilerPragmaNote,
            UnityRuleDescriptors.CompilerPragmaBreaksDx11,
            UnityRuleDescriptors.CompilerPragmaVersionDependent,
        ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.Environment is not UnityHlslEnvironment environment)
        {
            return;
        }

        UnityOptions options = environment.Options;
        UnityVersionBand? band = options.VersionBand;
        bool dx11 = (options.GraphicsApis.IsDefaultOrEmpty ? UnityGraphicsApis.Default : options.GraphicsApis).Contains(UnityGraphicsApi.Direct3D11);

        // HLSLINCLUDE の指定は Pass ごとに渡るので、同じ位置を重ねて報告しない。
        HashSet<(int, string)> reported = [];

        void Report(Location location, DiagnosticDescriptor descriptor, string message)
        {
            if (reported.Add((location.Span.Start, message)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // コンパイル先を決めた単位 (.shader のパスと .compute のカーネル) だけを見る。
            if (program.Tree.Hlsl().CompileTargets.IsDefaultOrEmpty)
            {
                continue;
            }

            ShaderSyntaxToken? neverUseDxc = null;
            ShaderSyntaxToken? dxcRequirement = null;

            foreach (PragmaDirective pragma in program.Tree.Pragmas)
            {
                // 利用者のヘッダに書いたものも「報告できる」が、コードブロックに直接書いたものとは効き方が違う (2023.2 以降は無視される)。
                if (!compilation.IsWrittenHere(pragma.NameToken))
                {
                    if (pragma.Name is "use_dxc" or "require"
                        && band >= UnityVersionBand.Unity2023_2To6_3
                        && pragma.NameToken.Source.FilePath is { } path
                        && compilation.IsUserFile(path))
                    {
                        Report(
                            program.GetBlockLocation(),
                            UnityRuleDescriptors.CompilerPragmaIgnored,
                            $"include ファイル '{Path.GetFileName(path)}' に書いた #pragma {pragma.Name} は、Unity 2023.2 以降では無視されます。"
                            + $"#pragma {pragma.Name} をコードブロックか HLSLINCLUDE に移してください。");
                    }

                    continue;
                }

                ShaderSyntaxToken[] arguments = [.. pragma.Arguments];

                switch (pragma.Name)
                {
                    case "use_dxc":
                        AnalyzeUseDxc(pragma.NameToken, arguments, band, dx11, Report);
                        break;

                    case "never_use_dxc":
                        neverUseDxc ??= pragma.NameToken;
                        break;

                    case "require":
                        dxcRequirement ??= AnalyzeRequire(arguments, band, dx11, Report);
                        break;
                }
            }

            if (neverUseDxc is not null && dxcRequirement is not null)
            {
                Report(
                    neverUseDxc.GetLocation(),
                    UnityRuleDescriptors.CompilerPragmaVersionDependent,
                    $"同じコードブロックに never_use_dxc と、DXC を要する require {dxcRequirement.Text} が書かれています。"
                    + "Unity 6.5 以前は FXC、6.6 からは DXC でコンパイルされます。");
            }
        }
    }

    /// <summary><c>#pragma use_dxc</c> を検査する。</summary>
    /// <param name="name"><c>use_dxc</c> のトークン。</param>
    /// <param name="arguments">API の名前。</param>
    /// <param name="band">Unity のバージョンのまとまり。分からなければ <see langword="null"/>。</param>
    /// <param name="dx11">対象の API に DX11 があるか。</param>
    /// <param name="report">報告する手続き。</param>
    private static void AnalyzeUseDxc(
        ShaderSyntaxToken name,
        ShaderSyntaxToken[] arguments,
        UnityVersionBand? band,
        bool dx11,
        Action<Location, DiagnosticDescriptor, string> report)
    {
        bool known = band is not null;
        bool latest = band == UnityVersionBand.Unity6_6OrLater;

        if (arguments.Length == 0)
        {
            if (known && !latest && dx11)
            {
                report(
                    name.GetLocation(),
                    UnityRuleDescriptors.CompilerPragmaBreaksDx11,
                    "Unity 6.5 以前は、API名を指定しない use_dxc は、Graphics API for Windows: DX11 / DX12 のどちらの環境でもDXCコンパイルを行います。"
                    + "DXCコンパイルされたシェーダーは Graphics API for Windows: DX11 では動作しません。"
                    + "Graphics API for Windows: DX11 でもこのシェーダーを使用する場合、 use_dxc を外してください。");
            }

            return;
        }

        foreach (ShaderSyntaxToken argument in arguments)
        {
            switch (argument.Text.ToLowerInvariant())
            {
                case "d3d12":
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaIgnored,
                        "use_dxc の API 名 d3d12 は、どのバージョンの Unity でも認識されず、DXCが使用されません。"
                        + "ほかに DXC の指定が無ければ FXC でコンパイルされます。"
                        + "Graphics API for Windows: DX12 で DXC を使用する場合、Unity 6.6 以降は dx12、6.5 以前は d3d11 を指定してください"
                        + " (d3d11 は Graphics API for Windows: DX11/DX12 どちらもDXCコンパイルするため、DX11 では動作しません)。");
                    break;

                case "dx12" when known && !latest:
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaIgnored,
                        "Unity 6.5 以前は、use_dxc の API 名 dx12 が認識されず、DXCが使用されません。"
                        + "ほかに DXC の指定が無ければ FXC でコンパイルされます。"
                        + "6.5 以前で Graphics API for Windows: DX12 を DXC にするには d3d11 を書いてください"
                        + " (DXCコンパイルされたシェーダーは Graphics API for Windows: DX11 では動作しません)。");
                    break;

                case "d3d11" when latest:
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaNote,
                        "Unity 6.6 では、use_dxc の API 名 d3d11 は非推奨です。"
                        + "dx12 と書いてください。");
                    break;

                case "d3d11" when known && dx11:
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaBreaksDx11,
                        "Unity 6.5 以前の use_dxc d3d11 は、Graphics API for Windows: DX11 / DX12 のどちらの環境でもDXCコンパイルを行います。"
                        + "DXCコンパイルされたシェーダーは Graphics API for Windows: DX11 では動作しません。"
                        + "Graphics API for Windows: DX11 でもこのシェーダーを使用する場合、 use_dxc を外してください。");
                    break;
            }
        }
    }

    /// <summary><c>#pragma require</c> を検査する。</summary>
    /// <param name="arguments">値 (<c>:</c> の後ろはキーワード)。</param>
    /// <param name="band">Unity のバージョンのまとまり。分からなければ <see langword="null"/>。</param>
    /// <param name="dx11">対象の API に DX11 があるか。</param>
    /// <param name="report">報告する手続き。</param>
    /// <returns>キーワードの条件が無い、DXC が要る値のトークン。無ければ <see langword="null"/>。</returns>
    private static ShaderSyntaxToken? AnalyzeRequire(
        ShaderSyntaxToken[] arguments,
        UnityVersionBand? band,
        bool dx11,
        Action<Location, DiagnosticDescriptor, string> report)
    {
        int colon = Array.FindIndex(arguments, a => a.Text == ":");
        bool conditional = colon >= 0;
        ShaderSyntaxToken? dxc = null;

        foreach (ShaderSyntaxToken value in conditional ? arguments[..colon] : arguments)
        {
            string text = value.Text.ToLowerInvariant();
            bool requiresDxc = UnityCompilerSelection.DxcRequirements.Contains(text)
                               || (text == UnityCompilerSelection.InlineRayTracingRequirement && band >= UnityVersionBand.Unity6_4To6_5);

            if (text == UnityCompilerSelection.InlineRayTracingRequirement)
            {
                if (band == UnityVersionBand.Unity2022_3)
                {
                    report(
                        value.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaIgnored,
                        "Unity 2022.3 は require の値 inlineraytracing を認識しません。"
                        + "inlineraytracing は Unity 6.4 以降で使用できます。");
                }
                else if (band == UnityVersionBand.Unity2023_2To6_3)
                {
                    report(
                        value.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaNote,
                        "Unity 2023.2〜6.3 では、inlineraytracing を満たす GPU が無いと判定され、コンパイルされません。"
                        + "inlineraytracing は Unity 6.4 以降で使用できます。"
                        + "6.3 以前でもこのシェーダーを使用する場合、multi_compile _ UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING で分け、"
                        + "キーワードが無効な構成にレイトレーシングを使わない処理を書いてください (6.3 以前はこのキーワードが有効になりません)。");
                }
            }

            if (!requiresDxc || conditional)
            {
                continue;
            }

            dxc ??= value;

            if (dx11)
            {
                report(
                    value.GetLocation(),
                    UnityRuleDescriptors.CompilerPragmaBreaksDx11,
                    $"require {value.Text} がキーワードの条件なしで書かれています。"
                    + "このパスは DXCコンパイルされるため、Graphics API for Windows: DX11 では動作しません。"
                    + $"Graphics API for Windows: DX11 でも使用する場合、require {value.Text} : キーワード のように条件を付け、DXC と FXC の構成を分けてください。");
            }
        }

        return dxc;
    }
}

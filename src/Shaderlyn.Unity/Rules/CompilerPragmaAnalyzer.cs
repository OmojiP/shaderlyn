using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// コンパイラの指定 (<c>#pragma use_dxc</c> / <c>never_use_dxc</c> / <c>require</c>) を、対象の API と Unity のバージョンに照らして検査する
/// (SL1050 / SL1051)。
/// </summary>
/// <remarks>
/// <para>規則は Unity 2022.3〜6.6 の実測による (<see cref="UnityCompilerSelection"/>)。</para>
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
        [UnityRuleDescriptors.CompilerPragmaMismatch, UnityRuleDescriptors.CompilerPragmaNote];

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
            if (program.Tree.CompileTargets.IsDefaultOrEmpty)
            {
                continue;
            }

            HlslSyntaxToken? neverUseDxc = null;
            HlslSyntaxToken? dxcRequirement = null;

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
                            UnityRuleDescriptors.CompilerPragmaMismatch,
                            $"include ファイル '{Path.GetFileName(path)}' の #pragma {pragma.Name} は、Unity 2023.2 以降では無視されます (FXC でコンパイルされます)。"
                            + "コードブロックか HLSLINCLUDE に書いてください。");
                    }

                    continue;
                }

                HlslSyntaxToken[] arguments = [.. pragma.Arguments];

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
                    UnityRuleDescriptors.CompilerPragmaMismatch,
                    $"never_use_dxc と DXC が要る require {dxcRequirement.Text} を両方書くと、Unity 6.5 以前は FXC、6.6 からは DXC でコンパイルされます。"
                    + "どちらか一方にしてください。");
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
        HlslSyntaxToken name,
        HlslSyntaxToken[] arguments,
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
                    UnityRuleDescriptors.CompilerPragmaMismatch,
                    "Unity 6.5 以前は、API を書かない use_dxc で DX11 も DXC でコンパイルされ、このシェーダーは DX11 では使えなくなります "
                    + "(6.6 からは DX11 だけ FXC)。DX11 で使うなら、DXC だけの書き方を避けて use_dxc を外してください。");
            }

            return;
        }

        foreach (HlslSyntaxToken argument in arguments)
        {
            switch (argument.Text.ToLowerInvariant())
            {
                case "d3d12":
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaMismatch,
                        "use_dxc の API 名 d3d12 は、どのバージョンの Unity でも無視されます。DX12 は Unity 6.6 から dx12、6.5 以前は d3d11 で指定します。");
                    break;

                case "dx12" when known && !latest:
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaMismatch,
                        "Unity 6.5 以前は use_dxc dx12 を無視します (FXC のまま)。6.5 以前で DX12 を DXC にするには d3d11 と書きます "
                        + "(DX11 も DXC になり、DX11 では使えなくなります)。");
                    break;

                case "d3d11" when latest:
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaNote,
                        "Unity 6.6 では use_dxc の d3d11 は非推奨です (DX12 を DXC にします)。dx12 と書いてください。");
                    break;

                case "d3d11" when known && dx11:
                    report(
                        argument.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaMismatch,
                        "Unity 6.5 以前の use_dxc d3d11 は、DX12 と一緒に DX11 も DXC でコンパイルするので、このシェーダーは DX11 では使えなくなります。");
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
    private static HlslSyntaxToken? AnalyzeRequire(
        HlslSyntaxToken[] arguments,
        UnityVersionBand? band,
        bool dx11,
        Action<Location, DiagnosticDescriptor, string> report)
    {
        int colon = Array.FindIndex(arguments, a => a.Text == ":");
        bool conditional = colon >= 0;
        HlslSyntaxToken? dxc = null;

        foreach (HlslSyntaxToken value in conditional ? arguments[..colon] : arguments)
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
                        UnityRuleDescriptors.CompilerPragmaMismatch,
                        "Unity 2022.3 は require の値 inlineraytracing を知らないので無視します (FXC でコンパイルされます)。");
                }
                else if (band == UnityVersionBand.Unity2023_2To6_3)
                {
                    report(
                        value.GetLocation(),
                        UnityRuleDescriptors.CompilerPragmaNote,
                        "Unity 2023.2〜6.3 は inlineraytracing を満たす GPU が無いと判定し、このシェーダーをコンパイルしません (6.4 から DXC でコンパイルされます)。");
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
                    UnityRuleDescriptors.CompilerPragmaMismatch,
                    $"require {value.Text} は DXC が要るので、このシェーダーは DX11 では使えません (コンパイルされないか、作られても使われません)。"
                    + $"DX11 でも使うなら、キーワードで分けてください (require {value.Text} : キーワード)。");
            }
        }

        return dxc;
    }
}

using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cli.Caching;

/// <summary>
/// キャッシュした結果を使ってよいかを決める鍵。
/// </summary>
/// <remarks>
/// <para>
/// <b>ファイルの内容以外で結果を変えうるものは、すべてここに入れる。</b>
/// 入れ忘れた項目の症状は「設定を変えたのに指摘が変わらない」であり、
/// 利用者からは原因が全く見えない。
/// 迷ったら入れる。余分に入れて困るのは、無駄に解析し直すことだけである。
/// </para>
/// <para>
/// <b>解析ツールそのものの同一性は、組み立てられたアセンブリの識別子で見る。</b>
/// バージョン番号は開発中に変わらないため、ルールを直しても古い結果が出続けることになる。
/// <see cref="Module.ModuleVersionId"/> はビルドのたびに変わるので、
/// 「直したのに指摘が消えない」を構造的に防げる。
/// </para>
/// </remarks>
internal static class AnalysisCacheKey
{
    /// <summary>
    /// 鍵を計算する。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <returns>16 進数の文字列。</returns>
    public static string Compute(
        CommandLineOptions options,
        EffectiveSettings settings,
        ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        StringBuilder material = new();

        AppendAnalyzerIdentity(material, analyzers);
        AppendConfiguration(material, settings);
        AppendOptions(material, options, settings);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    /// <summary>
    /// 解析ツールの同一性を書き出す。
    /// </summary>
    /// <param name="material">書き出し先。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <remarks>
    /// 自作のアナライザを渡して実行する経路 (<see cref="Program.RunAsync"/> の第 2 引数) があるため、
    /// 組み込みのアセンブリに加えて、渡されたアナライザのアセンブリと
    /// 報告しうるルール ID も混ぜる。
    /// </remarks>
    private static void AppendAnalyzerIdentity(
        StringBuilder material,
        ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        SortedSet<string> modules = new(StringComparer.Ordinal);

        // 組み込みの構成要素。どれか 1 つでも組み直されれば結果は変わりうる。
        AddModule(modules, typeof(Program).Assembly);
        AddModule(modules, typeof(Diagnostic).Assembly);
        AddModule(modules, typeof(ShaderCompilation).Assembly);

        SortedSet<string> ruleIds = new(StringComparer.Ordinal);

        foreach (DiagnosticAnalyzer analyzer in analyzers)
        {
            AddModule(modules, analyzer.GetType().Assembly);

            foreach (DiagnosticDescriptor descriptor in analyzer.SupportedDiagnostics)
            {
                ruleIds.Add(descriptor.Id);
            }
        }

        material.AppendLine("analyzer");

        foreach (string module in modules)
        {
            material.AppendLine(module);
        }

        foreach (string ruleId in ruleIds)
        {
            material.AppendLine(ruleId);
        }
    }

    /// <summary>アセンブリの識別子を加える。</summary>
    /// <param name="modules">加える先。</param>
    /// <param name="assembly">対象のアセンブリ。</param>
    /// <remarks>
    /// 識別子を取れない環境では、代わりに完全名を使う。
    /// 鍵が緩くなるのは避けたいが、取れないことを理由に
    /// キャッシュ全体を使えなくするほどではない。
    /// </remarks>
    private static void AddModule(SortedSet<string> modules, Assembly assembly)
    {
        try
        {
            modules.Add(assembly.ManifestModule.ModuleVersionId.ToString("N"));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            modules.Add(assembly.FullName ?? assembly.GetName().Name ?? "unknown");
        }
    }

    /// <summary>
    /// 設定ファイルの内容を書き出す。
    /// </summary>
    /// <param name="material">書き出し先。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <remarks>
    /// <b>設定ファイルは中身をそのまま混ぜる。</b>
    /// 重要度の上書き・ルールごとの設定・利用者定義のルールは、
    /// どれも指摘の内容を変える。項目ごとに拾うと、
    /// 設定の書式が増えたときに拾い忘れる。
    /// </remarks>
    private static void AppendConfiguration(StringBuilder material, EffectiveSettings settings)
    {
        material.AppendLine("configuration");

        if (settings.Configuration.FilePath is not { } filePath)
        {
            material.AppendLine("(none)");
            return;
        }

        material.AppendLine(filePath);

        try
        {
            material.AppendLine(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めない設定ファイルは、読めないという事実を鍵に混ぜる。
            material.AppendLine($"(unreadable: {ex.GetType().Name})");
        }
    }

    /// <summary>
    /// コマンドライン引数のうち、解析結果を変えるものを書き出す。
    /// </summary>
    /// <param name="material">書き出し先。</param>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <remarks>
    /// <para>
    /// 出力形式や終了コードの閾値は入れない。これらは解析そのものを変えないため、
    /// 入れると <c>--format text</c> と <c>--format json</c> で
    /// キャッシュが別々になるだけで、得るものが無い。
    /// </para>
    /// <para>
    /// <b>Unity Editor のパスは、推定した結果を入れる。</b>
    /// 指定が無い場合はインストール済みのものから選ぶため、
    /// Editor を入れ替えると、指定を変えていなくても読まれるヘッダが変わる。
    /// </para>
    /// </remarks>
    private static void AppendOptions(
        StringBuilder material,
        CommandLineOptions options,
        EffectiveSettings settings)
    {
        material.AppendLine("options");
        material.AppendLine(settings.Profile);
        material.AppendLine(options.UnityProjectPath ?? "(none)");
        material.AppendLine(SemanticsSetup.ResolveEditorDataPath(options) ?? "(none)");
        material.AppendLine(
            (options.MaxSymbolVariants ?? SemanticsOptions.DefaultMaxSymbolVariants).ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        material.AppendLine("include-paths");

        foreach (string includePath in settings.IncludePaths)
        {
            material.AppendLine(includePath);
        }

        material.AppendLine("defines");

        foreach (string define in settings.Defines)
        {
            material.AppendLine(define);
        }
    }
}

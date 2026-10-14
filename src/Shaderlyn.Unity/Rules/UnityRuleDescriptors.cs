using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// Unity の環境でだけ働くルールのうち、ShaderLab と HLSL を突き合わせるものと、<c>#pragma</c> のエントリポイントを見るものの定義。
/// </summary>
/// <remarks>
/// ID は v0.1 から変えていない。ベースライン・抑制コメント・設定の <c>rules:</c> がそのまま効く。
/// </remarks>
internal static class UnityRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";
    private const string UsageCategory = "Usage";

    /// <summary>プロパティに対応する uniform 宣言が無い。</summary>
    public static DiagnosticDescriptor MissingUniformDeclaration { get; } = new(
        id: "SL1001",
        title: "プロパティに対応する HLSL の宣言が存在しません",
        messageFormat: "プロパティ '{0}' は HLSL コードで未使用です。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Properties に宣言されているのに、HLSL 側に対応する uniform が無く、"
            + "コード中にその名前が一度も現れていません。"
            + "マテリアルのインスペクタには表示され、値も保存されますが、描画には一切影響しません。"
            + "プロパティ名のスペルミスか、使わなくなったプロパティの消し忘れです。",
        helpLinkUri: DocumentationLinks.For("SL1001"));

    /// <summary>マテリアル定数バッファの uniform が Properties に公開されていない。</summary>
    /// <remarks>
    /// 検査対象をマテリアル用の定数バッファに限っている。
    /// HLSL の大域変数一般を対象にすると、スクリプトから設定する大域 uniform が
    /// すべて指摘され、誤検出の山になる。
    /// </remarks>
    public static DiagnosticDescriptor UniformNotExposed { get; } = new(
        id: "SL1002",
        title: "定数バッファの uniform が Properties に公開されていません",
        messageFormat: "'{1}' の '{0}' に対応するプロパティが Properties にありません。マテリアルから値を設定できません。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "マテリアルごとの値を収める定数バッファに、Properties へ公開されていない uniform があります。"
            + "マテリアルのインスペクタからは設定できず、スクリプトから明示的に設定しない限り既定値のままになります。"
            + "スクリプトから設定する意図的な内部状態であれば、この指摘は抑制して構いません。",
        helpLinkUri: DocumentationLinks.For("SL1002"));

    /// <summary>プロパティの型と uniform の型が食い違っている。</summary>
    public static DiagnosticDescriptor PropertyTypeMismatch { get; } = new(
        id: "SL1003",
        title: "プロパティの型と HLSL の型が一致しません",
        messageFormat: "プロパティ '{0}' は {1} ですが、HLSL では '{2}' として宣言されています。{3}が必要です。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Properties に宣言された型と、HLSL 側の uniform の型が噛み合っていません。"
            + "Unity はこれをエラーにせず、値の一部だけを転送するか、まったく転送しません。"
            + "「マテリアルで設定した色が反映されない」「スライダーを動かしても何も起きない」"
            + "という形で現れます。"
            + "成分数の違い (Color を half3 で受けるなど) は正常な書き方なので報告しません。",
        helpLinkUri: DocumentationLinks.For("SL1003"));

    /// <summary>宣言されているが一度も使われていないプロパティ。</summary>
    public static DiagnosticDescriptor UnusedProperty { get; } = new(
        id: "SL1004",
        title: "プロパティはシェーダー側で宣言されていますが、使用されていません",
        messageFormat: "プロパティ '{0}' は uniform として宣言されていますが、シェーダーコードで未使用です。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "uniform の宣言はあるものの、その値をどこからも読んでいません。"
            + "機能を削除した際の消し残しであることが多く、"
            + "定数バッファの容量とマテリアルの表示を無駄に占有します。"
            + "スクリプトから読む用途や、将来使う予定がある場合は抑制して構いません。",
        helpLinkUri: DocumentationLinks.For("SL1004"));

    /// <summary>シェーダーステージの指定が無い。</summary>
    public static DiagnosticDescriptor MissingShaderStage { get; } = new(
        id: "HL0302",
        title: "シェーダーステージが指定されていません",
        messageFormat: "コードブロックに {0} が存在しません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "どの関数を頂点シェーダー・フラグメントシェーダーとして使うかが指定されていません。"
            + "この Pass はコンパイルできません。"
            + "共通コード片 (HLSLINCLUDE) に書いたつもりの #pragma が、"
            + "別の SubShader に属していて届いていない場合によく起きます。",
        helpLinkUri: DocumentationLinks.For("HL0302"));

    /// <summary>コンパイラの指定 (<c>#pragma use_dxc</c> / <c>require</c>) が無視され、DXC に切り替わらない。</summary>
    /// <remarks>
    /// <para>Unity 2022.3〜6.6 の実測 (ShaderProbePack) による。</para>
    /// <para>
    /// コンパイラの指定の誤りは、直し方の種類で ID を分けてある。
    /// 無視される指定 (SL1050)、DX11 で使えなくなる指定 (SL1053)、バージョンで結果が変わる組み合わせ (SL1054) である。
    /// DX12 だけを対象にするプロジェクトは、SL1053 だけを無効にできる。
    /// 文はどれも「何が起きているか。何でコンパイルされるか。どう直すか。」の 3 文で書く。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor CompilerPragmaIgnored { get; } = new(
        id: "SL1050",
        title: "コンパイラの指定が無視されます",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "#pragma use_dxc / require のうち、Unity が認識しない、または効かない場所に書いたために無視されるものです。"
            + "use_dxc の API 名 d3d12 (どのバージョンでも)、dx12 (6.5 以前)、include ファイルの中の指定 (2023.2 以降)、"
            + "require inlineraytracing (2022.3) が当たります。DXC に切り替わらず、ほかに DXC の指定が無ければ FXC でコンパイルされます。",
        helpLinkUri: DocumentationLinks.For("SL1050"));

    /// <summary>コンパイラの指定で、パスが DX11 でも DXC でコンパイルされ、DX11 では使えなくなる。</summary>
    /// <remarks>Unity 2022.3〜6.6 の実測 (ShaderProbePack) による。</remarks>
    public static DiagnosticDescriptor CompilerPragmaBreaksDx11 { get; } = new(
        id: "SL1053",
        title: "DX11 で使えなくなるコンパイラの指定です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "DXC でコンパイルしたシェーダーは DX11 では使えません。"
            + "キーワードの条件の無い、DXC が要る #pragma require と、Unity 6.5 以前の use_dxc (API なし / d3d11) は、"
            + "DX11 でもパスを DXC でコンパイルさせます。対象の API に DX11 があるときだけ報告します。",
        helpLinkUri: DocumentationLinks.For("SL1053"));

    /// <summary>Unity のバージョンで、コンパイラが FXC と DXC に分かれる組み合わせ。</summary>
    /// <remarks>Unity 2022.3〜6.6 の実測 (ShaderProbePack) による。</remarks>
    public static DiagnosticDescriptor CompilerPragmaVersionDependent { get; } = new(
        id: "SL1054",
        title: "Unity のバージョンでコンパイラが変わる指定です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "#pragma never_use_dxc と、DXC が要る #pragma require を同じコードブロックに書くと、"
            + "Unity 6.5 以前は never_use_dxc が勝って FXC、6.6 からは require が勝って DXC でコンパイルされます。",
        helpLinkUri: DocumentationLinks.For("SL1054"));

    /// <summary>コンパイラの指定について、知っておくとよいこと (非推奨の API 名など)。</summary>
    public static DiagnosticDescriptor CompilerPragmaNote { get; } = new(
        id: "SL1051",
        title: "コンパイラの指定についての注意",
        messageFormat: "{0}",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "Unity 6.6 で非推奨になった use_dxc d3d11、Unity 2023.2〜6.3 でコンパイルされない require inlineraytracing など、"
            + "エラーにはならないが意図と違う結果になりうる指定を伝えます。",
        helpLinkUri: DocumentationLinks.For("SL1051"));

    /// <summary>Unity では効かない <c>#pragma once</c>。</summary>
    /// <remarks>
    /// Unity 2022.3 / 6.3 / 6.6 の実測 (ShaderProbePack の INCLUDE/HLSL_Include_PragmaOnce) による。
    /// マニュアルには「Caching Shader Preprocessor が有効なときに使える」とあるが、
    /// <c>#include</c> でも <c>#include_with_pragmas</c> でも 2 度目の取り込みが再定義のエラーになった。
    /// </remarks>
    public static DiagnosticDescriptor PragmaOnceIgnored { get; } = new(
        id: "SL1055",
        title: "Unity では #pragma once が効きません",
        messageFormat: "Unity は #pragma once を効かせません。このファイルを 2 度取り込むと、中の宣言が再定義のエラーになります。#ifndef / #define / #endif のインクルードガードで囲んでください。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Unity のシェーダーのプリプロセッサは #pragma once を無視します (Unity 2022.3 / 6.3 / 6.6 で確かめました)。"
            + "同じファイルを 2 か所から取り込むと、関数や構造体が再定義のエラーになります。"
            + "Unity のヘッダと同じく、#ifndef MY_HEADER_INCLUDED / #define MY_HEADER_INCLUDED / #endif で囲んでください。",
        helpLinkUri: DocumentationLinks.For("SL1055"));

    /// <summary>Unity が読めない UTF-16 で保存したファイル。</summary>
    /// <remarks>
    /// Unity 2022.3 / 6.3 / 6.6 の実測 (ShaderProbePack の ENCODING) による。UTF-16 は BOM の有無によらず読めず、
    /// <c>.shader</c> は「Parse error … expecting TOK_SHADER」、取り込んだヘッダは「Unexpected directive」や構文エラーになった。
    /// </remarks>
    public static DiagnosticDescriptor Utf16File { get; } = new(
        id: "SL1056",
        title: "Unity は UTF-16 のファイルを読めません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "Unity は UTF-16 で保存したシェーダーのファイルを読めません (BOM があっても読めません。Unity 2022.3 / 6.3 / 6.6 で確かめました)。"
            + ".shader は構文エラーに、取り込んだ .hlsl / .cginc は Unexpected directive や構文エラーになります。"
            + "UTF-8 (BOM の有無はどちらでもよい) で保存し直してください。",
        helpLinkUri: DocumentationLinks.For("SL1056"));

    /// <summary>16 ビットの型を有効にした DXC でコンパイルされる定数バッファに、half などを置いている。</summary>
    /// <remarks>
    /// <c>-enable-16bit-types</c> では <c>half</c> が <c>float16_t</c> になり、定数バッファの中で 2 バイトに詰められる。
    /// Unity は値を 32 ビットの float で書き込むので、シェーダーは別の値を読む
    /// (白 (1, 1, 1, 1) が DX12 で (0, 1.875, 0, 1.875)、Vulkan で 0)。Unity 2022.3〜6.6 で確かめた。
    /// </remarks>
    public static DiagnosticDescriptor HalfInNative16BitConstantBuffer { get; } = new(
        id: "SL1052",
        title: "16 ビットの型を有効にしたパスの定数バッファに half があります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "#pragma require native16bit や UNITY_DEVICE_SUPPORTS_NATIVE_16BIT の構成では、DXC が 16 ビットの型を有効にしてコンパイルし、"
            + "half / min16float は 2 バイトの float16_t になります。定数バッファの配置が変わり、Unity が 32 ビットで書き込んだ値を正しく読めません。"
            + "定数バッファのメンバーは float で宣言し、使うところで half に変換してください。",
        helpLinkUri: DocumentationLinks.For("SL1052"));
}

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
        title: "プロパティに対応する HLSL の宣言がありません",
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
        messageFormat: "'{1}' の '{0}' に対応するプロパティが Properties にありません。マテリアルからは値を設定できません。",
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
        title: "プロパティが宣言されているだけで使われていません",
        messageFormat: "プロパティ '{0}' は uniform として宣言されていますが、シェーダーのコードから一度も読まれていません。",
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
        messageFormat: "このコードブロックに #pragma vertex / #pragma fragment がありません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "どの関数を頂点シェーダー・フラグメントシェーダーとして使うかが指定されていません。"
            + "この Pass はコンパイルできません。"
            + "共通コード片 (HLSLINCLUDE) に書いたつもりの #pragma が、"
            + "別の SubShader に属していて届いていない場合によく起きます。",
        helpLinkUri: DocumentationLinks.For("HL0302"));
}

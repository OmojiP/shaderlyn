using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// docs/custom-rules/cookbook.md に載せているルールの実物。
/// </summary>
/// <remarks>
/// <b>手引きに載せたコードが動かないのは、書いていないのより悪い。</b>
/// 載せた形をここに置き、<c>CookbookTests</c> (tests/Shaderlyn.Tests) で動くことを確かめる。
/// </remarks>
internal static class CookbookRules
{
    public static DiagnosticDescriptor BannedMacro { get; } = new(
        id: "COOK0001",
        title: "使ってはいけないマクロ",
        messageFormat: "マクロ '{0}' は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedFunction { get; } = new(
        id: "COOK0002",
        title: "使ってはいけない関数",
        messageFormat: "関数 '{0}' は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor NoBranch { get; } = new(
        id: "COOK0003",
        title: "if を書かない",
        messageFormat: "if は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor TooManyBranches { get; } = new(
        id: "COOK0004",
        title: "else が多すぎる",
        messageFormat: "else が {0} 個あります。{1} 個までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedUnderSymbol { get; } = new(
        id: "COOK0005",
        title: "この構成では使えない関数",
        messageFormat: "'{1}' が定義されている構成で '{0}' は使えません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor ArgumentType { get; } = new(
        id: "COOK0006",
        title: "引数の型が違う",
        messageFormat: "'{0}' の第 {1} 引数には {2} を渡してください ({3} が渡されています)。",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor LiteralTooLarge { get; } = new(
        id: "COOK0007",
        title: "リテラルが大きすぎる",
        messageFormat: "{0} は {1} を超えています。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor PropertyNamePrefix { get; } = new(
        id: "COOK0008",
        title: "プロパティ名の接頭辞",
        messageFormat: "プロパティ '{0}' は '{1}' で始めてください。",
        category: "Naming",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor RequiredTag { get; } = new(
        id: "COOK0009",
        title: "必須のタグが無い",
        messageFormat: "SubShader に '{0}' タグがありません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor TooManyPasses { get; } = new(
        id: "COOK0010",
        title: "Pass が多すぎる",
        messageFormat: "Pass が {0} 個あります。{1} 個までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor ThreadGroupSize { get; } = new(
        id: "COOK0011",
        title: "スレッドグループの大きさ",
        messageFormat: "カーネル '{0}' のスレッド数は {1} です。{2} の倍数にしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor KernelNamePrefix { get; } = new(
        id: "COOK0012",
        title: "カーネル名の接頭辞",
        messageFormat: "カーネル '{0}' は '{1}' で始めてください。",
        category: "Naming",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedOverload { get; } = new(
        id: "COOK0029",
        title: "使ってはいけないオーバーロード",
        messageFormat: "'{0}' を ({1}) の形で呼んでいます。この形は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor TagValueNotAllowed { get; } = new(
        id: "COOK0014",
        title: "タグの値が許されていない",
        messageFormat: "'{0}' タグに '{1}' は指定できません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor CommandValueNotAllowed { get; } = new(
        id: "COOK0015",
        title: "命令の値が許されていない",
        messageFormat: "命令 '{0}' に '{1}' は指定できません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedType { get; } = new(
        id: "COOK0016",
        title: "使ってはいけない型",
        messageFormat: "型 '{0}' は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedOperator { get; } = new(
        id: "COOK0017",
        title: "使ってはいけない演算子",
        messageFormat: "演算子 '{0}' は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedSemantic { get; } = new(
        id: "COOK0018",
        title: "使ってはいけないセマンティクス",
        messageFormat: "セマンティクス '{0}' は使わないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor MissingPragma { get; } = new(
        id: "COOK0019",
        title: "必須の #pragma が無い",
        messageFormat: "このコードブロックに '#pragma {0}' がありません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor ShaderModelTooLow { get; } = new(
        id: "COOK0020",
        title: "シェーダーモデルが低い",
        messageFormat: "シェーダーモデルが {0} です。{1} 以上を指定してください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor TooManySymbols { get; } = new(
        id: "COOK0021",
        title: "シンボルが多すぎる",
        messageFormat: "シンボルを {0} 個宣言しています。{1} 個までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor BannedInclude { get; } = new(
        id: "COOK0022",
        title: "取り込んではいけないヘッダ",
        messageFormat: "'{0}' は取り込まないでください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor MissingInclude { get; } = new(
        id: "COOK0023",
        title: "必須のヘッダが無い",
        messageFormat: "このコードブロックは '{0}' を取り込んでいません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor TooManySamples { get; } = new(
        id: "COOK0024",
        title: "サンプリングが多すぎる",
        messageFormat: "{0} でサンプリングを {1} 回行っています。{2} 回までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor NestingTooDeep { get; } = new(
        id: "COOK0025",
        title: "入れ子が深すぎる",
        messageFormat: "入れ子が {0} 段あります。{1} 段までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor FunctionTooLong { get; } = new(
        id: "COOK0026",
        title: "関数が長すぎる",
        messageFormat: "関数 '{0}' は {1} 文あります。{2} 文までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor LoopTooLong { get; } = new(
        id: "COOK0027",
        title: "ループの回数が多すぎる",
        messageFormat: "ループが {0} 回まわります。{1} 回までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor ConstantBufferTooLarge { get; } = new(
        id: "COOK0028",
        title: "定数バッファが大きすぎる",
        messageFormat: "定数バッファ '{0}' は約 {1} バイトです。{2} バイトまでにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    /// <summary>仮引数が多すぎる。上限を設定ファイルの <c>options</c> から読む例。</summary>
    /// <remarks>
    /// 読むオプション: <c>maxParameters</c> (整数)。
    /// 書かれていなければ <see cref="ParameterCountLimitAnalyzer"/> のコンストラクタで渡した値を使う。
    /// </remarks>
    public static DiagnosticDescriptor TooManyParameters { get; } = new(
        id: "COOK0030",
        title: "仮引数が多すぎる",
        messageFormat: "関数 '{0}' の仮引数は {1} 個です。{2} 個までにしてください。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");

    public static DiagnosticDescriptor WritableBufferNaming { get; } = new(
        id: "COOK0013",
        title: "書き込めるバッファの名前",
        messageFormat: "'{0}' は書き込めるバッファなので '{1}' で始めてください。",
        category: "Naming",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "テスト用。");
}

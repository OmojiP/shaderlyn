using System.Collections.Frozen;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>システム値のセマンティクスを検査するコンパイラ。</summary>
internal enum SystemValueCompiler
{
    /// <summary>FXC (シェーダーモデル 5.0)。</summary>
    Fxc,

    /// <summary>DXC (シェーダーモデル 6.8)。</summary>
    Dxc,

    /// <summary>slangc。</summary>
    Slang,
}

/// <summary>エントリポイントがシステム値を受け渡す形。</summary>
internal enum SystemValueShape
{
    /// <summary>入力の仮引数 (構造体の仮引数のフィールドを含む)。</summary>
    In,

    /// <summary>出力の仮引数と戻り値 (構造体のフィールドを含む)。</summary>
    Out,

    /// <summary>ジオメトリシェーダーの頂点の配列 (<c>point S input[1]</c>) のフィールド。</summary>
    InArray,

    /// <summary>ジオメトリシェーダーのストリーム (<c>inout PointStream&lt;S&gt;</c>) のフィールド。</summary>
    OutStream,

    /// <summary>ハルシェーダーの <c>InputPatch</c>、ドメインシェーダーの <c>OutputPatch</c> のフィールド。</summary>
    InPatch,

    /// <summary>ハルシェーダーの戻り値 (制御点) のフィールド。</summary>
    OutControlPoint,

    /// <summary>ハルシェーダーのパッチ定数の関数の戻り値のフィールド。</summary>
    OutPatchConstant,

    /// <summary>ドメインシェーダーのパッチ定数の構造体の仮引数のフィールド。</summary>
    InPatchConstant,

    /// <summary>メッシュシェーダーの <c>out vertices</c> のフィールド。</summary>
    OutVertices,

    /// <summary>メッシュシェーダーの <c>out primitives</c> のフィールド。</summary>
    OutPrimitives,

    /// <summary>
    /// メッシュシェーダーの <c>out indices</c> のフィールド。表には無い。
    /// プリミティブごとの値 (<c>out primitives</c> にだけ使用できるもの) を置いた場合だけ、誤りとする (slangc の E30703)。
    /// </summary>
    OutIndices,
}

/// <summary>
/// システム値のセマンティクスを、どのステージのどの受け渡しの形で使用できるかの表
/// (<c>SystemValueSemantics.g.txt</c>。<c>tools/semantics/system-values.mjs</c> が FXC・DXC・slangc に掛けて作る)。
/// </summary>
/// <remarks>
/// コンパイラが通したもの (使用できる) と、セマンティクスの誤りにしたもの (使用できない) だけが表にある。
/// 骨組みの形の誤りなどで判断できなかった組み合わせと、表に無いセマンティクスは <see langword="null"/> (判断しない) を返す。
/// </remarks>
internal static class SystemValueSemantics
{
    private const string ResourceName = "Shaderlyn.Semantics.SystemValueSemantics.txt";

    private static readonly Lazy<Table> Shared = new(Load);

    /// <summary>セマンティクスを使用できるかを返す。</summary>
    /// <param name="compiler">コンパイラ。</param>
    /// <param name="stage">ステージ。</param>
    /// <param name="shape">受け渡しの形。</param>
    /// <param name="semantic">セマンティクスの名前 (大文字・小文字と末尾の番号は問わない)。</param>
    /// <returns>使用できれば <see langword="true"/>、使用できなければ <see langword="false"/>、表に無ければ <see langword="null"/>。</returns>
    public static bool? IsAllowed(SystemValueCompiler compiler, HlslShaderStage stage, SystemValueShape shape, string semantic)
    {
        string name = Normalize(semantic);
        return Shared.Value.Cells.TryGetValue((compiler, stage, shape), out Cell? cell)
            ? cell.Allowed.Contains(name) ? true : cell.Rejected.Contains(name) ? false : null
            : null;
    }

    /// <summary>そのコンパイラが、どこかのステージでそのセマンティクスを使用できるかを返す。</summary>
    /// <param name="compiler">コンパイラ。</param>
    /// <param name="semantic">セマンティクスの名前。</param>
    /// <returns>どこかで使用できれば <see langword="true"/>。</returns>
    /// <remarks>FXC は、シェーダーモデル 6 で加わったもの (<c>SV_ViewID</c> など) をどこでも使用できない。</remarks>
    public static bool IsKnown(SystemValueCompiler compiler, string semantic)
    {
        string name = Normalize(semantic);
        return Shared.Value.Cells.Any(c => c.Key.Compiler == compiler && c.Value.Allowed.Contains(name));
    }

    /// <summary>大文字にし、末尾の番号 (<c>SV_Target1</c> の 1) を除く。</summary>
    private static string Normalize(string semantic) => semantic.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').ToUpperInvariant();

    private static Table Load()
    {
        Dictionary<(SystemValueCompiler, HlslShaderStage, SystemValueShape), Cell> cells = [];

        using Stream stream = typeof(SystemValueSemantics).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException($"埋め込みのリソース {ResourceName} がありません。");
        using StreamReader reader = new(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] columns = line.Split('\t');

            if (columns.Length != 5)
            {
                throw new InvalidOperationException($"{ResourceName} の行の列の数が 5 ではありません: {line}");
            }

            cells[(ParseCompiler(columns[0]), ParseStage(columns[1]), ParseShape(columns[2]))] = new Cell(Split(columns[3]), Split(columns[4]));
        }

        return new Table(cells.ToFrozenDictionary());
    }

    private static FrozenSet<string> Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(StringComparer.Ordinal);

    private static SystemValueCompiler ParseCompiler(string text) => text switch
    {
        "fxc" => SystemValueCompiler.Fxc,
        "dxc" => SystemValueCompiler.Dxc,
        "slang" => SystemValueCompiler.Slang,
        _ => throw new InvalidOperationException($"{ResourceName} のコンパイラ '{text}' が不明です。"),
    };

    private static HlslShaderStage ParseStage(string text) => text switch
    {
        "vertex" => HlslShaderStage.Vertex,
        "pixel" => HlslShaderStage.Pixel,
        "compute" => HlslShaderStage.Compute,
        "amplification" => HlslShaderStage.Amplification,
        "geometry" => HlslShaderStage.Geometry,
        "hull" => HlslShaderStage.Hull,
        "domain" => HlslShaderStage.Domain,
        "mesh" => HlslShaderStage.Mesh,
        _ => throw new InvalidOperationException($"{ResourceName} のステージ '{text}' が不明です。"),
    };

    private static SystemValueShape ParseShape(string text) => text switch
    {
        "in" => SystemValueShape.In,
        "out" => SystemValueShape.Out,
        "in-array" => SystemValueShape.InArray,
        "out-stream" => SystemValueShape.OutStream,
        "in-patch" => SystemValueShape.InPatch,
        "out-control-point" => SystemValueShape.OutControlPoint,
        "out-patch-constant" => SystemValueShape.OutPatchConstant,
        "in-patch-constant" => SystemValueShape.InPatchConstant,
        "out-vertices" => SystemValueShape.OutVertices,
        "out-primitives" => SystemValueShape.OutPrimitives,
        _ => throw new InvalidOperationException($"{ResourceName} の受け渡しの形 '{text}' が不明です。"),
    };

    private sealed record Cell(FrozenSet<string> Allowed, FrozenSet<string> Rejected);

    private sealed record Table(FrozenDictionary<(SystemValueCompiler Compiler, HlslShaderStage Stage, SystemValueShape Shape), Cell> Cells);
}

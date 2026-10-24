using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shaderlyn.Unreal;

/// <summary>
/// 頂点ファクトリ (<c>IMPLEMENT_VERTEX_FACTORY_TYPE</c>) を C++ から読み、<c>/Engine/Generated/VertexFactory.ush</c> の代わりを作る。
/// </summary>
/// <remarks>
/// <para>
/// <b>メッシュに描くマテリアルのシェーダー (<c>FMeshMaterialShader</c> の派生) は、頂点ファクトリごとにコンパイルされる。</b>
/// エンジンは <c>VertexFactory.ush</c> を、その頂点ファクトリの <c>.ush</c> (<c>LocalVertexFactory.ush</c> など) を取り込むものにする。
/// 頂点ファクトリによって、入力の構造体のメンバーや関数が違う。
/// </para>
/// <para>
/// どの頂点ファクトリとコンパイルするかは、シェーダーのクラスの <c>ShouldCompilePermutation</c> が
/// <c>Parameters.VertexFactoryType->GetFName()</c> や <c>->SupportsLumenMeshCards()</c> で決める。
/// 頂点ファクトリを、パーミュテーションと同じく構成の軸 (<see cref="Macro"/> の値) にし、組ごとにこれを実行する
/// (<see cref="UnrealShaderClasses"/>)。
/// </para>
/// </remarks>
public static partial class UnrealVertexFactories
{
    /// <summary>頂点ファクトリを選ぶマクロ。値は <see cref="Write"/> が書いた一覧の番号。</summary>
    public const string Macro = "SHADERLYN_VERTEX_FACTORY";

    /// <summary>
    /// <see cref="Macro"/> が決まらないとき (パーミュテーションを軸にしない解析) に使う頂点ファクトリの番号。
    /// シェーダーのクラスが、コンパイルされる頂点ファクトリから決めて定義する。
    /// </summary>
    public const string DefaultMacro = "SHADERLYN_DEFAULT_VERTEX_FACTORY";

    /// <summary>書き出すファイルの名前。生成物のフォルダーの直下に置く。</summary>
    public const string FileName = "VertexFactory.ush";

    /// <summary>頂点ファクトリの定義するマクロを書き出すファイルの名前。生成物のフォルダーの直下に置く (<see cref="CreatePrelude"/>)。</summary>
    public const string DefinesFileName = "ShaderlynVertexFactoryDefines.ush";

    /// <summary>軸の値が決まらないとき (パーミュテーションを軸にしない解析) に使う頂点ファクトリ。静的メッシュのもの。</summary>
    public const string DefaultName = "FLocalVertexFactory";

    /// <summary>C++ のコードから頂点ファクトリの登録を集める。</summary>
    /// <param name="text">ファイルの中身。フラグをマクロで書いた登録 (<c>NIAGARA_MESH_VF_FLAGS</c>) の定義を引く。</param>
    /// <param name="code">コメントとマクロの定義を除いたコード。</param>
    /// <param name="found">集める先。名前から頂点ファクトリ。</param>
    /// <remarks>
    /// スキンメッシュの頂点ファクトリ (<c>IMPLEMENT_GPUSKINNING_VERTEX_FACTORY_TYPE</c>) は、骨の影響数の 2 通りの型を作る。
    /// 名前は <c>TGPUSkinVertexFactoryDefault</c> と <c>…Unlimited</c> で (C++ が <c>FName</c> で比べる名前)、フラグに <c>SupportsPrimitiveIdStream</c> が付く。
    /// </remarks>
    internal static void Collect(string text, string code, ConcurrentDictionary<string, UnrealVertexFactory> found)
    {
        if (!code.Contains("VERTEX_FACTORY_TYPE", StringComparison.Ordinal))
        {
            return;
        }

        foreach (Match match in Registration().Matches(code))
        {
            string name = match.Groups["name"].Value.Trim();
            found.TryAdd(name, new UnrealVertexFactory(name, match.Groups["path"].Value, Flags(text, match.Groups["flags"].Value)));
        }

        foreach (Match match in SkinningRegistration().Matches(code))
        {
            ImmutableHashSet<string> flags = Flags(text, match.Groups["flags"].Value).Add("SupportsPrimitiveIdStream");
            string name = match.Groups["name"].Value.Trim();

            foreach (string influence in (string[])["Default", "Unlimited"])
            {
                found.TryAdd(name + influence, new UnrealVertexFactory(name + influence, match.Groups["path"].Value, flags));
            }
        }
    }

    /// <summary>登録の引数のフラグを読む。マクロの名前なら、そのファイルの定義 (最初のもの) を読む。</summary>
    private static ImmutableHashSet<string> Flags(string text, string argument)
    {
        if (!argument.Contains("EVertexFactoryFlags", StringComparison.Ordinal)
            && Regex.Match(argument.Trim(), @"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant) is { Success: true } macro
            && Regex.Match(text, $@"#\s*define\s+{Regex.Escape(macro.Value)}\b(?<body>(?:[^\n]*\\\r?\n)*[^\n]*)", RegexOptions.CultureInvariant) is { Success: true } definition)
        {
            argument = definition.Groups["body"].Value;
        }

        return [.. Flag().Matches(argument).Select(m => m.Groups["flag"].Value)];
    }

    /// <summary>マテリアルと使う頂点ファクトリを、名前の順に並べる。この順の番号が <see cref="Macro"/> の値である。</summary>
    /// <param name="found">集めた頂点ファクトリ。</param>
    /// <returns>並べたもの。</returns>
    internal static ImmutableArray<UnrealVertexFactory> Order(IEnumerable<UnrealVertexFactory> found)
        => [.. found.Where(f => f.Flags.Contains("UsedWithMaterials")).OrderBy(f => f.Name, StringComparer.Ordinal)];

    /// <summary><c>VertexFactory.ush</c> の代わりと、頂点ファクトリの定義するマクロのファイル (<see cref="DefinesFileName"/>) を書き出す。</summary>
    /// <param name="outputDirectory">生成物のフォルダー。</param>
    /// <param name="factories">並べた頂点ファクトリ (<see cref="Order"/>)。</param>
    /// <param name="defines">
    /// 頂点ファクトリの名前から、その <c>ModifyCompilationEnvironment</c> が定義するマクロ。
    /// シェーダーはこのマクロ (<c>PARTICLE_SPRITE_FACTORY</c> など) で頂点ファクトリごとのコードを書き分ける。
    /// </param>
    /// <returns>書き出したファイルのパス。頂点ファクトリが無ければ空。</returns>
    internal static ImmutableArray<string> Write(
        string outputDirectory,
        ImmutableArray<UnrealVertexFactory> factories,
        IReadOnlyDictionary<string, ImmutableSortedDictionary<string, string>> defines)
    {
        if (factories.IsEmpty)
        {
            return [];
        }

        int fallback = Math.Max(0, factories.IndexOf(factories.FirstOrDefault(f => f.Name == DefaultName)!));
        StringBuilder own = new();
        own.Append("// Shaderlyn が C++ の頂点ファクトリの登録から作った。").Append(Macro).Append(" の値の頂点ファクトリが定義するマクロを定義する。\n");
        own.Append("#ifndef ").Append(Macro).Append('\n');
        own.Append("#ifdef ").Append(DefaultMacro).Append('\n');
        own.Append(CultureInfo.InvariantCulture, $"#define {Macro} {DefaultMacro}\n");
        own.Append("#else\n");
        own.Append(CultureInfo.InvariantCulture, $"#define {Macro} {fallback} // {factories[fallback].Name}\n");
        own.Append("#endif\n");
        own.Append("#endif\n");

        for (int i = 0; i < factories.Length; i++)
        {
            own.Append(CultureInfo.InvariantCulture, $"#{(i == 0 ? "if" : "elif")} {Macro} == {i} // {factories[i].Name}\n");

            foreach ((string name, string value) in defines.GetValueOrDefault(factories[i].Name) ?? ImmutableSortedDictionary<string, string>.Empty)
            {
                own.Append(CultureInfo.InvariantCulture, $"#ifndef {name}\n#define {name} {value}\n#endif\n");
            }
        }

        own.Append("#endif\n");

        StringBuilder include = new();
        include.Append("// Shaderlyn が C++ の頂点ファクトリの登録から作った。").Append(Macro).Append(" の値の頂点ファクトリを取り込む。\n");
        include.Append("#include \"/Engine/Generated/").Append(DefinesFileName).Append("\"\n");

        for (int i = 0; i < factories.Length; i++)
        {
            include.Append(CultureInfo.InvariantCulture, $"#{(i == 0 ? "if" : "elif")} {Macro} == {i} // {factories[i].Name}\n");
            include.Append("#include \"").Append(factories[i].ShaderFile).Append("\"\n");
        }

        include.Append("#endif\n");

        string definesPath = Path.Combine(outputDirectory, DefinesFileName);
        string path = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(definesPath, own.ToString());
        File.WriteAllText(path, include.ToString());
        return [path, definesPath];
    }

    /// <summary>
    /// メッシュに描くマテリアルのシェーダーの単位で、先頭に差し込むコード。頂点ファクトリの定義するマクロを、どのファイルよりも先に定義する。
    /// </summary>
    /// <param name="generatedDirectory">生成物のフォルダー。</param>
    /// <returns>コード。頂点ファクトリのマクロのファイルが無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>エンジンは、頂点ファクトリの <c>ModifyCompilationEnvironment</c> のマクロをコマンドラインで定義する。</b>
    /// マテリアルのテンプレート (<c>MaterialTemplate.ush</c>) は、<c>VertexFactory.ush</c> を取り込む前にこれを使い、
    /// <c>Definitions.usf</c> は定義されていなければ 0 にする。<c>VertexFactory.ush</c> で定義しては遅い。
    /// </para>
    /// <para>
    /// 構成の軸 (パーミュテーションの組で値の変わる定義) にもしない。頂点ファクトリの値と独立した軸になり、
    /// 実在しない組み合わせ (髪のカードの頂点ファクトリで <c>HAIR_CARD_MESH_FACTORY</c> が 0) の木を読んでしまう。
    /// <c>#ifdef HAIR_STRAND_MESH_FACTORY</c> のように定義されているかで分けるコードがあるので、頂点ファクトリの番号の式にも定義できない。
    /// </para>
    /// </remarks>
    public static string? CreatePrelude(string generatedDirectory)
        => File.Exists(Path.Combine(generatedDirectory, DefinesFileName))
            ? $"#include \"/Engine/Generated/{DefinesFileName}\"\n"
            : null;

    [GeneratedRegex("""IMPLEMENT_(?:TEMPLATE_)?VERTEX_FACTORY_TYPE\s*\(\s*(?:[A-Za-z0-9_]+\s*,\s*)?(?<name>[A-Za-z0-9_<>:, ]+?)\s*,\s*"(?<path>[^"]+)"\s*,(?<flags>[^;]*?)\)\s*;""", RegexOptions.CultureInvariant)]
    private static partial Regex Registration();

    [GeneratedRegex("""IMPLEMENT_GPUSKINNING_VERTEX_FACTORY_TYPE\s*\(\s*(?<name>[A-Za-z0-9_]+)\s*,\s*"(?<path>[^"]+)"\s*,(?<flags>[^;]*?)\)\s*;""", RegexOptions.CultureInvariant)]
    private static partial Regex SkinningRegistration();

    [GeneratedRegex("""EVertexFactoryFlags::(?<flag>[A-Za-z0-9_]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Flag();
}

/// <summary>頂点ファクトリ 1 つ。</summary>
/// <param name="Name">C++ のクラスの名前 (<c>FLocalVertexFactory</c>)。</param>
/// <param name="ShaderFile">シェーダーのファイルの仮想パス (<c>/Engine/Private/LocalVertexFactory.ush</c>)。</param>
/// <param name="Flags">対応する機能 (<c>EVertexFactoryFlags</c> の名前。<c>SupportsLumenMeshCards</c> など)。</param>
public sealed record UnrealVertexFactory(string Name, string ShaderFile, ImmutableHashSet<string> Flags)
{
    /// <summary>
    /// 頂点ファクトリの型のメソッド (<c>VertexFactoryType->SupportsLumenMeshCards()</c>) に、登録のフラグで答える。
    /// </summary>
    /// <param name="method">メソッドの名前。</param>
    /// <returns>答え。フラグで決まらないメソッドなら <see langword="null"/>。</returns>
    public bool? Answer(string method) => method switch
    {
        "IsUsedWithMaterials" => Flags.Contains("UsedWithMaterials"),
        _ when method.StartsWith("Supports", StringComparison.Ordinal) => Flags.Contains(method),
        _ => null,
    };
}

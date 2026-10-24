using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Unreal;

/// <summary>
/// 単位ごとに、<c>/Engine/Generated/GeneratedUniformBuffers.ush</c> に入れる uniform buffer を、エンジンと同じ規則で選ぶ。
/// </summary>
/// <remarks>
/// <para>
/// <b>エンジンは、シェーダーが名前で参照している uniform buffer だけを取り込む。</b>
/// エディタの起動のときに、シェーダーのファイルごとに本文を大文字にして、<c>名前.</c> か <c>名前 .</c> を含む uniform buffer を覚える
/// (UE 5.5 の <c>ShaderCore.cpp</c> の <c>BuildShaderFileToUniformBufferMap</c>。部分一致なので <c>PRIMARYVIEW.</c> も <c>VIEW.</c> に当たる)。
/// シェーダーをコンパイルするときは、単位のファイルから取り込みを条件に関わらずすべてたどり、そのどれかが参照するものを集める
/// (<c>GenerateReferencedUniformBuffers</c>)。
/// </para>
/// <para>
/// すべての uniform buffer を取り込むと、シェーダーが自分で宣言した同じ名前 (<c>HairStrandsVF_CullingIndexBuffer</c>) と重なり、
/// 二重の宣言 (HL0314) と報告していた (Unreal の HairStrandsTangent.usf)。
/// </para>
/// <para>
/// シェーダーのクラスの引数の構造体が持つ uniform buffer (<c>SHADER_PARAMETER_STRUCT_REF</c>) もエンジンは取り込むが、ここでは選ばない。
/// その uniform buffer をシェーダーが使うなら、名前で参照しているはずである。
/// </para>
/// </remarks>
internal sealed partial class UnrealUniformBufferSelection
{
    /// <summary>uniform buffer を取り込む生成ファイルの仮想パス。</summary>
    public const string IncludePath = "/Engine/Generated/GeneratedUniformBuffers.ush";

    /// <summary>取り込みをたどる深さの上限。</summary>
    private const int MaxIncludeDepth = 64;

    private readonly string _generatedDirectory;
    private readonly ImmutableArray<(string Name, string Key, string KeyWithSpace)> _uniformBuffers;
    private readonly ConcurrentDictionary<string, FileReferences> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SourceText> _contents = new(StringComparer.Ordinal);

    /// <summary>生成物のフォルダーから、uniform buffer の名前を読む。</summary>
    /// <param name="generatedDirectory">生成物のフォルダー。</param>
    /// <param name="names">uniform buffer の名前 (<c>UniformBuffers/&lt;名前&gt;.ush</c>)。</param>
    private UnrealUniformBufferSelection(string generatedDirectory, IEnumerable<string> names)
    {
        _generatedDirectory = generatedDirectory;
        _uniformBuffers =
        [
            .. names.Order(StringComparer.Ordinal)
                .Select(name => (name, name.ToUpperInvariant() + ".", name.ToUpperInvariant() + " .")),
        ];
    }

    /// <summary>生成物のフォルダーに uniform buffer の宣言があれば、選ぶ仕組みを作る。</summary>
    /// <param name="generatedDirectory">生成物のフォルダー。</param>
    /// <returns>仕組み。宣言が無ければ <see langword="null"/>。</returns>
    public static UnrealUniformBufferSelection? Create(string? generatedDirectory)
    {
        string? folder = generatedDirectory is null ? null : Path.Combine(generatedDirectory, "UniformBuffers");

        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        return new UnrealUniformBufferSelection(
            generatedDirectory!,
            Directory.EnumerateFiles(folder, "*.ush").Select(Path.GetFileNameWithoutExtension).OfType<string>());
    }

    /// <summary>単位の取り込みを、この単位の uniform buffer だけを取り込むものに差し替える。</summary>
    /// <param name="filePath">単位のファイル。</param>
    /// <param name="resolver">取り込みを解決する手続き。</param>
    /// <returns>差し替えた手続き。</returns>
    public IIncludeResolver Wrap(string filePath, IIncludeResolver resolver)
        => new UnitResolver(resolver, Content(Select(filePath, resolver)));

    /// <summary>単位が参照する uniform buffer の名前を、名前の順に返す。</summary>
    /// <param name="filePath">単位のファイル。</param>
    /// <param name="resolver">取り込みを解決する手続き。</param>
    /// <returns>名前。</returns>
    public ImmutableArray<string> Select(string filePath, IIncludeResolver? resolver)
    {
        SortedSet<string> selected = new(StringComparer.Ordinal);

        if (File.Exists(filePath))
        {
            Visit(SourceText.Load(filePath), 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return [.. selected];

        void Visit(SourceText text, int depth, HashSet<string> visited)
        {
            if (depth > MaxIncludeDepth || !visited.Add(text.FilePath))
            {
                return;
            }

            FileReferences references = _files.GetOrAdd(text.FilePath, _ => Read(text));
            selected.UnionWith(references.UniformBuffers);

            foreach (string path in references.Includes)
            {
                // 生成されるファイルは、エンジンが探すときにはまだ無い。
                if (path.StartsWith("/Engine/Generated/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (resolver is not null && resolver.TryResolve(path, text.FilePath, out SourceText? included))
                {
                    Visit(included, depth + 1, visited);
                }
            }
        }
    }

    /// <summary>1 つのファイルが参照する uniform buffer と、書いている取り込みを読む。</summary>
    private FileReferences Read(SourceText text)
    {
        string upper = text.Content.ToUpperInvariant();

        return new FileReferences(
            [
                .. _uniformBuffers
                    .Where(ub => upper.Contains(ub.Key, StringComparison.Ordinal) || upper.Contains(ub.KeyWithSpace, StringComparison.Ordinal))
                    .Select(ub => ub.Name),
            ],
            [.. IncludeLine().Matches(text.Content).Select(m => m.Groups["path"].Value.Replace('\\', '/'))]);
    }

    /// <summary>選んだ uniform buffer を取り込む中身。同じ選び方は 1 つの中身を使い回す。</summary>
    /// <remarks>
    /// 中身ごとに別のパスにする。取り込みの展開結果はパスで覚える (<see cref="IncludeCache"/>) ので、
    /// 同じパスで中身を変えると、別の単位の展開結果を使い回してしまう。
    /// </remarks>
    private SourceText Content(ImmutableArray<string> names)
    {
        string key = string.Join(',', names);

        return _contents.GetOrAdd(key, _ =>
        {
            StringBuilder builder = new();
            builder.Append("// Shaderlyn が作った。この単位が名前で参照する uniform buffer だけを取り込む (エンジンと同じ)。\n");

            foreach (string name in names)
            {
                builder.Append("#include \"/Engine/Generated/UniformBuffers/").Append(name).Append(".ush\"\n");
            }

            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8);
            return SourceText.From(builder.ToString(), Path.Combine(_generatedDirectory, "GeneratedUniformBuffers", hash, "GeneratedUniformBuffers.ush"));
        });
    }

    [GeneratedRegex("""^[ \t]*#[ \t]*include[ \t]*["<](?<path>[^">]+)[">]""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IncludeLine();

    /// <summary>1 つのファイルが参照する uniform buffer と、書いている取り込み。</summary>
    private sealed record FileReferences(ImmutableArray<string> UniformBuffers, ImmutableArray<string> Includes);

    /// <summary><see cref="IncludePath"/> だけを、その単位の中身に差し替える。</summary>
    private sealed class UnitResolver(IIncludeResolver inner, SourceText content) : IIncludeResolver
    {
        public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
        {
            if (string.Equals(path.Replace('\\', '/'), IncludePath, StringComparison.OrdinalIgnoreCase))
            {
                resolved = content;
                return true;
            }

            return inner.TryResolve(path, includingFilePath, out resolved);
        }

        public string? FindOutsideBoundary(string path, string includingFilePath) => inner.FindOutsideBoundary(path, includingFilePath);

        // 選んだ uniform buffer が同じ単位どうしなら、取り込みの展開結果を使い回せる。
        public string? CacheFingerprint => content.FilePath;
    }
}

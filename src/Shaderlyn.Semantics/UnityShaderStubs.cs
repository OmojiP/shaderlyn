using Shaderlyn.Core.Text;

namespace Shaderlyn.Semantics;

/// <summary>
/// Unity のヘッダが取り込めなかった場合に備える、最小限のマクロ定義。
/// </summary>
/// <remarks>
/// <para>
/// <b>これが無いと Unity をインストールしていない環境で uniform を 1 つも取り出せない。</b>
/// URP のシェーダーは <c>TEXTURE2D(_BaseMap)</c> や
/// <c>CBUFFER_START(UnityPerMaterial)</c> の形で宣言を書く。
/// これらは関数形式マクロであり、展開しなければ宣言として成立しない。
/// GitHub Actions のランナーには Unity が無いのが普通であり、
/// そこで解析が空振りするようでは PR 検査の道具として使えない。
/// </para>
/// <para>
/// <b>実物のヘッダが取り込めた場合は、実物の定義が必ずこれを上書きする。</b>
/// この先頭に差し込むコードは対象ファイルより前に処理されるため、
/// 後から現れる <c>#define</c> が同じ名前を再定義する形になる。
/// つまりここでの定義は「実物が無かった場合にだけ効く代替」であり、
/// 実物の挙動を書き換えることはない。
/// </para>
/// <para>
/// 収録するのは<b>宣言の形を決めるマクロだけ</b>に限っている。
/// 計算内容を持つマクロまで用意すると、実物と食い違ったときに
/// 誤った解析結果を「それらしく」出してしまう。
/// 宣言の形だけであれば、実物と食い違っても
/// 「uniform の名前と型が取れる」以上のことは主張しない。
/// </para>
/// </remarks>
internal static class UnityShaderStubs
{
    /// <summary>先頭に差し込むコードとして使うソーステキスト。</summary>
    /// <remarks>
    /// パスは実在しない識別子である。このパスでファイルを開こうとしてはならない。
    /// </remarks>
    public static SourceText Prelude { get; } = SourceText.From(PreludeText, "<shaderlyn 既定マクロ>");

    /// <summary>
    /// 先頭に差し込むコードの内容。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 定義の形は Unity の <c>com.unity.render-pipelines.core</c> および
    /// <c>CGIncludes/HLSLSupport.cginc</c> における D3D11 向けの定義に合わせている。
    /// </para>
    /// <para>
    /// 引数を取る形にしているものは、実物も引数を取る。
    /// 引数の有無が食い違うと展開結果が壊れるため、勝手に単純化してはならない。
    /// </para>
    /// </remarks>
    private const string PreludeText = """
        // 定数バッファ。SRP Batcher の検査 (URP0001) はこの展開結果を見る。
        #define CBUFFER_START(name) cbuffer name {
        #define CBUFFER_END };

        // テクスチャとサンプラの宣言 (SRP 系)。
        #define TEXTURE2D(textureName) Texture2D textureName
        #define TEXTURE2D_ARRAY(textureName) Texture2DArray textureName
        #define TEXTURE3D(textureName) Texture3D textureName
        #define TEXTURECUBE(textureName) TextureCube textureName
        #define TEXTURECUBE_ARRAY(textureName) TextureCubeArray textureName
        #define TEXTURE2D_FLOAT(textureName) Texture2D textureName
        #define TEXTURE2D_HALF(textureName) Texture2D textureName
        #define TEXTURE2D_ARRAY_FLOAT(textureName) Texture2DArray textureName
        #define TEXTURE2D_ARRAY_HALF(textureName) Texture2DArray textureName
        #define TEXTURE3D_FLOAT(textureName) Texture3D textureName
        #define TEXTURE3D_HALF(textureName) Texture3D textureName
        #define TEXTURECUBE_FLOAT(textureName) TextureCube textureName
        #define TEXTURECUBE_HALF(textureName) TextureCube textureName
        #define TEXTURE2D_SHADOW(textureName) Texture2D textureName
        #define TEXTURE2D_ARRAY_SHADOW(textureName) Texture2DArray textureName
        #define TEXTURECUBE_SHADOW(textureName) TextureCube textureName
        #define TEXTURECUBE_ARRAY_SHADOW(textureName) TextureCubeArray textureName
        #define SAMPLER(samplerName) SamplerState samplerName
        #define SAMPLER_CMP(samplerName) SamplerComparisonState samplerName

        // テクスチャとサンプラの宣言 (Built-in 系)。
        #define UNITY_DECLARE_TEX2D(name) Texture2D name; SamplerState sampler##name
        #define UNITY_DECLARE_TEX2D_NOSAMPLER(name) Texture2D name
        #define UNITY_DECLARE_TEX2D_FLOAT(name) Texture2D name; SamplerState sampler##name
        #define UNITY_DECLARE_TEX2D_HALF(name) Texture2D name; SamplerState sampler##name
        #define UNITY_DECLARE_TEX2DARRAY(name) Texture2DArray name; SamplerState sampler##name
        #define UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(name) Texture2DArray name
        #define UNITY_DECLARE_TEX3D(name) Texture3D name; SamplerState sampler##name
        #define UNITY_DECLARE_TEX3D_FLOAT(name) Texture3D name; SamplerState sampler##name
        #define UNITY_DECLARE_TEX3D_HALF(name) Texture3D name; SamplerState sampler##name
        #define UNITY_DECLARE_TEXCUBE(name) TextureCube name; SamplerState sampler##name
        #define UNITY_DECLARE_TEXCUBE_NOSAMPLER(name) TextureCube name
        #define UNITY_DECLARE_TEXCUBEARRAY(name) TextureCubeArray name; SamplerState sampler##name
        #define UNITY_DECLARE_SCREENSPACE_TEXTURE(name) Texture2D name; SamplerState sampler##name
        #define UNITY_DECLARE_DEPTH_TEXTURE(name) Texture2D name; SamplerState sampler##name
        #define UNITY_DECLARE_DEPTH_TEXTURE_MS(name) Texture2D name; SamplerState sampler##name

        // 構造体の中に「宣言としては何も生まない」形で書かれるマクロ。
        // 展開できないと構造体の解析がそこで止まり、頂点入力の型が一切分からなくなる。
        #define UNITY_VERTEX_INPUT_INSTANCE_ID
        #define UNITY_VERTEX_OUTPUT_STEREO
        #define UNITY_INSTANCING_BUFFER_START(name) cbuffer name {
        #define UNITY_INSTANCING_BUFFER_END(name) };
        #define UNITY_DEFINE_INSTANCED_PROP(type, name) type name;

        // 文として書かれ、宣言を生まないマクロ。
        #define UNITY_SETUP_INSTANCE_ID(input)
        #define UNITY_TRANSFER_INSTANCE_ID(input, output)
        #define UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output)
        #define UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input, output)
        #define UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)
        """;
}

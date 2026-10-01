namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// ShaderLab のプロパティ型と HLSL の uniform の型が噛み合うかを判定する。
/// </summary>
/// <remarks>
/// <para>
/// <b>「合っている」ではなく「明らかに食い違っている」だけを判定する。</b>
/// 戻り値が <see langword="false"/> になるのは、その組み合わせで
/// マテリアルの値が正しく届かないことが確実な場合に限る。
/// 判断がつかない組み合わせはすべて「食い違っていない」として扱う。
/// </para>
/// <para>
/// この非対称性は意図したものである。型不一致の指摘は、
/// 本当に間違っているときにだけ出るからこそ信用される。
/// 疑わしいものまで報告すると、正しいコードを直させる方向に働いてしまう。
/// </para>
/// </remarks>
internal static class PropertyTypeCompatibility
{
    /// <summary>
    /// プロパティの型と HLSL の型が明らかに食い違っていないかを判定する。
    /// </summary>
    /// <param name="propertyKind">ShaderLab 側のプロパティ型。</param>
    /// <param name="typeClass">HLSL 側の型の分類。</param>
    /// <returns>食い違いを断定できない場合は <see langword="true"/>。</returns>
    public static bool IsCompatible(ShaderPropertyKind propertyKind, HlslTypeClass typeClass)
    {
        // 判別できない型に対しては何も断定しない。
        if (typeClass == HlslTypeClass.Unknown || propertyKind == ShaderPropertyKind.Unknown)
        {
            return true;
        }

        return propertyKind switch
        {
            // Any はどの型としても解釈されうる。
            ShaderPropertyKind.Any => true,

            // 数値プロパティは成分数を問わない。
            // Color を half3 で受けるのは Unity 同梱のシェーダーでも普通に行われている。
            ShaderPropertyKind.Color or ShaderPropertyKind.Vector => typeClass == HlslTypeClass.Vector,

            // スカラープロパティを float4 で受けると x 成分にしか値が入らない。
            // 行列で受けることも当然できない。
            ShaderPropertyKind.Float or ShaderPropertyKind.Range
                or ShaderPropertyKind.Int or ShaderPropertyKind.Integer => typeClass == HlslTypeClass.Scalar,

            // テクスチャは種類まで一致していなければならない。
            // 2D のプロパティを TextureCube で受けると、そもそもバインドされない。
            //
            // サンプラを許しているのは Cg 記法との整合のためではなく、
            // TEXTURE2D(_X) と SAMPLER(_X) を同名で書く実装があるためである。
            ShaderPropertyKind.Texture2D =>
                typeClass is HlslTypeClass.Texture2D or HlslTypeClass.Sampler,
            ShaderPropertyKind.Texture3D =>
                typeClass is HlslTypeClass.Texture3D or HlslTypeClass.Sampler,
            ShaderPropertyKind.TextureCube =>
                typeClass is HlslTypeClass.TextureCube or HlslTypeClass.Sampler,
            ShaderPropertyKind.Texture2DArray =>
                typeClass is HlslTypeClass.Texture2DArray or HlslTypeClass.Sampler,
            ShaderPropertyKind.TextureCubeArray =>
                typeClass is HlslTypeClass.TextureCubeArray or HlslTypeClass.Sampler,

            _ => true,
        };
    }

    /// <summary>
    /// プロパティの型に対して期待される HLSL の型を、診断メッセージ用の文字列で返す。
    /// </summary>
    /// <param name="propertyKind">ShaderLab 側のプロパティ型。</param>
    /// <returns>期待される型の例。</returns>
    /// <remarks>
    /// 「何が間違っているか」だけでなく「どう書けばよいか」を示すために使う。
    /// 直し方の書かれていない指摘は、抑制されるか誤った直し方をされる。
    /// </remarks>
    public static string DescribeExpectedType(ShaderPropertyKind propertyKind) => propertyKind switch
    {
        ShaderPropertyKind.Color or ShaderPropertyKind.Vector => "float4 / half4 などのベクトル型",
        ShaderPropertyKind.Float or ShaderPropertyKind.Range => "float / half などのスカラー型",
        ShaderPropertyKind.Int or ShaderPropertyKind.Integer => "int / float などのスカラー型",
        ShaderPropertyKind.Texture2D => "Texture2D (または Cg 記法の sampler2D)",
        ShaderPropertyKind.Texture3D => "Texture3D (または Cg 記法の sampler3D)",
        ShaderPropertyKind.TextureCube => "TextureCube (または Cg 記法の samplerCUBE)",
        ShaderPropertyKind.Texture2DArray => "Texture2DArray",
        ShaderPropertyKind.TextureCubeArray => "TextureCubeArray",
        _ => "対応する型",
    };
}

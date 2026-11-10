import lighting;

struct VertexOutput
{
    float4 position : SV_Position;
    float3 normal;
}

ConstantBuffer<DirectionalLight> gLight;

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    let lit = gLight.illuminate(normalize(input.normal));
#ifdef USE_FOG
    let fogged = lerp(lit, fogColor, 0.5);   // CF0310: fogColor は宣言されていない
    return float4(fogged, 1.0);
#else
    return float4(lit, 1.0);
#endif
}

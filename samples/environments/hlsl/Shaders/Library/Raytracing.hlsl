// 動作確認用。DXR のライブラリ (lib_6_6) の単位で、誤りは入れていない。何も報告されなければ正しく動いている。
// ライブラリでは、宣言だけの関数 (SkyColor) を誤りにしない。定義はリンクするほかのライブラリにありうる。

RaytracingAccelerationStructure Scene : register(t0);
RWTexture2D<float4> Output : register(u0);

// ペイロードの読み書きの指定。DXC はシェーダーモデル 6.6 では -enable-payload-qualifiers を付けたときだけ受け付ける (6.7 からは既定)。
struct [raypayload] Payload
{
    float4 color : read(caller) : write(caller, closesthit, miss);
};

float3 SkyColor(float3 direction);

export float Luminance(float3 color)
{
    return dot(color, float3(0.2126f, 0.7152f, 0.0722f));
}

[shader("raygeneration")]
void RayGen()
{
    uint2 index = DispatchRaysIndex().xy;

    RayDesc ray;
    ray.Origin = float3(index, 0);
    ray.Direction = float3(0, 0, 1);
    ray.TMin = 0.001f;
    ray.TMax = 1000.0f;

    Payload payload = (Payload)0;
    TraceRay(Scene, RAY_FLAG_NONE, 0xFF, 0, 1, 0, ray, payload);
    Output[index] = payload.color * Luminance(payload.color.rgb);
}

[shader("miss")]
void Miss(inout Payload payload)
{
    payload.color = float4(SkyColor(WorldRayDirection()), 1);
}

[shader("closesthit")]
void ClosestHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attributes)
{
    payload.color = float4(attributes.barycentrics, 0, 1);
}

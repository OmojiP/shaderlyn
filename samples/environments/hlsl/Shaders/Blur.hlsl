// 動作確認用。シェーダーモデル 6 の書き方 (ディスクリプタヒープ・wave) は誤りにならない。
#include "Common.hlsli"

VsOut MainVS(uint id : SV_VertexID)
{
    VsOut o;
    o.Uv = float2((id << 1) & 2, id & 2);
    o.Position = float4(o.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 MainPS(VsOut input, nointerpolation uint index : MATERIAL) : SV_Target
{
    Texture2D<float4> source = ResourceDescriptorHeap[index];
    float4 sum = source.Sample(LinearSampler, input.Uv);

#if LIGHT_DEFERRED
    sum *= 0.5;
#endif

    return WaveActiveSum(sum) / WaveGetLaneCount();
}

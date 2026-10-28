// 動作確認用のシェーダー。意図的に誤りを入れてある (samples/environments/README.md)。
#include "Common.hlsli"

VsOut MainVS(float3 position : POSITION, float2 uv : TEXCOORD0)
{
    VsOut o;
    o.Position = mul(ViewProj, float4(position, 1));
    o.Uv = uv;
    return o;
}

// 誤り 1 (CF0301): 設定ファイルの entry は MainPS だが、関数の名前が MainPs になっている
float4 MainPs(VsOut input) : SV_Target
{
#if QUALITY >= 2
    float3 color = Albedo.Sample(LinearSampler, input.Uv).rgb;
#else
    float4 color = Albedo.Sample(LinearSampler, input.Uv);
#endif

#ifdef USE_FOG
    color *= FogColor.a;
#endif

    // 誤り 2 (CF0312): QUALITY == 2 のとき color は float3 なので .a は無い
    return float4(color.rgb, color.a);
}

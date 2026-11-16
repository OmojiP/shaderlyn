// 誤りを含む関数の中でも、誤りを含む文より前の文は検査する。誤りを含む文から関数の終わりまでは見ない。
// 期待: 8 CF0310
// 期待: 9 CF0001

float Blend(float a, float b)
{
    float t = 0.5;
    float u = missingBefore * t;
    float w = a b;
    float z = missingAfter + w;
    return lerp(a, b, t) + z;
}

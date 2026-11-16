// hlsl-include-broken.hlsl が取り込むヘッダ。関数の本体の中に構文の誤りがある。
// 期待: 6 CF0001

float Helper(float v)
{
    return v +;
}

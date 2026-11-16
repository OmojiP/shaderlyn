// 関数の外の構文の誤り (構造体の ; の書き忘れ)。宣言が読めていないので、宣言の検査はしない。
// 期待: 7 CF0001

struct Light
{
    float3 color
};

float3 Shade(Light l)
{
    return l.color * undefinedGain;
}

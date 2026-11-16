// } が足りず、後ろの関数を本体に飲み込んでいる。宣言が読めていないので、宣言の検査はしない。
// 期待: 15 CF0001

float Scale(float x)
{
    float y = x * 2.0;
    return y;

float UseScale(float v)
{
    return v * undefinedGain;
}

void Tail()
{

// 関数の本体の中の構文の誤り。ほかの関数の宣言されていない名前は報告する。
// 期待: 9 CF0001
// 期待: 14 CF0310

float Scale(float x)
{
    float y = x * 2.0;
    return y +
}

float UseScale(float v)
{
    float s = Scale(v);
    return s * undefinedGain;
}

// 誤りを含む関数の外では、呼び出しの引数の数も検査する。
// 期待: 7 CF0001
// 期待: 12 CF0402

float Scale(float x, float k)
{
    return x * k +;
}

float Use(float v)
{
    return Scale(v);
}

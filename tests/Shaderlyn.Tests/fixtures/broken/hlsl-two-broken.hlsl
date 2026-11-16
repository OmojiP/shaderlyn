// 2 つの関数に構文の誤りがある。どちらでもない関数は検査する。
// 期待: 8 CF0001
// 期待: 13 CF0001
// 期待: 18 CF0310

float A(float x)
{
    return x +;
}

float B(float x)
{
    return (x * 2.0;
}

float C(float x)
{
    return A(x) + B(x) + missingInC;
}

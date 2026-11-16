// for の見出しの中の構文の誤り。ほかの関数は検査する。
// 期待: 8 CF0001
// 期待: 15 CF0310

float Sum(float v)
{
    float s = 0;
    for (int i = 0; i < ; i++)
        s += v;
    return s;
}

float Other(float v)
{
    return v * missingInOther;
}

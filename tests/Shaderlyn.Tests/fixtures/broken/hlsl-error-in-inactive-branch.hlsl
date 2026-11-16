// 有効でない #if の分岐の構文の誤りは、構文の誤りにならない。ファイル全体を検査する。
// 期待: 14 CF0310

float Pick(float v)
{
#if 0
    return v +;
#endif
    return v;
}

float Other(float v)
{
    return v * missingInOther;
}

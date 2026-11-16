// 入れ子のブロック (if の中) の構文の誤り。誤りを含む本体の直下の文 (if) から関数の終わりまでは見ない。
// 期待: 8 CF0310
// 期待: 11 CF0001
// 期待: 19 CF0310

float Pick(float a, float b)
{
    float k = missingFirst;
    if (a > b)
    {
        return a *;
    }
    float later = missingInIf;
    return b + later;
}

float Other(float v)
{
    return v + missingInOther;
}

// ジェネリックの関数の本体の中の構文の誤り。ほかの関数は検査する。
// 期待: 7 CF0001
// 期待: 12 CF0310

T twice<T : IArithmetic>(T x)
{
    return x + ;
}

float use(float v)
{
    return twice(v) * missingGain;
}

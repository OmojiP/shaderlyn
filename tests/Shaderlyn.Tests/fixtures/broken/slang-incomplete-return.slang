// Slang の関数の本体の中の構文の誤り。関数の外の名前は宣言より前でも使える (Use から後ろの Later)。
// 期待: 8 CF0001
// 期待: 12 CF0310

float scale(float x)
{
    return x +
}

float use(float v)
{
    return later(v) * undefinedGain;
}

float later(float w) { return w; }

// 名前空間の中の関数の本体の構文の誤り。名前空間の外の関数は検査する。
// 期待: 9 CF0001
// 期待: 15 CF0310

namespace Tools
{
    float scale(float x)
    {
        return x *;
    }
}

float use(float v)
{
    return Tools.scale(v) + missingName;
}

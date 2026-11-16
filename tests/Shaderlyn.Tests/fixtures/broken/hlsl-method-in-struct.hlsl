// HLSL 2021 の構造体のメソッドの本体の中の構文の誤り。同じ構造体のほかのメソッドと、関数の外の関数は検査する。
// 期待: 9 CF0001
// 期待: 14 CF0310
// 期待: 20 CF0310

struct Accumulator
{
    float total;
    void Add(float v) { total = total + ; }
    float Mean(float count)
    {
        if (count == 0)
            return 0;
        return total / count * missingInMethod;
    }
};

float Use(Accumulator acc)
{
    return acc.Mean(2.0) + missingInFunction;
}

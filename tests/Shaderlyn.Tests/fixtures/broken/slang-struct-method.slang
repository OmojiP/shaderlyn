// 構造体のメソッドの本体の中の構文の誤り。同じ構造体のほかのメソッドと関数は検査する。
// 期待: 9 CF0001
// 期待: 14 CF0310
// 期待: 20 CF0310

struct Counter
{
    int value;
    [mutating] void add(int v) { value = value + ; }
    int get()
    {
        if (value < 0)
            return 0;
        return value + missingInMethod;
    }
}

int use(Counter c)
{
    return c.get() + missingInFunction;
}

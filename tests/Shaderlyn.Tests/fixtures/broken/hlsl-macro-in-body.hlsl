// マクロを展開してできた構文の誤り (展開した位置は関数の本体の中)。
// 期待: 11 CF0001
// 期待: 11 CF0001 (式の無い ) を読んだあと、外の ( の閉じが無い)
// 期待: 16 CF0310

#define INCOMPLETE(x) (x +)

float Broken(float v)
{
    float a = v;
    return INCOMPLETE(a);
}

float Fine(float v)
{
    return v * missingInFine;
}

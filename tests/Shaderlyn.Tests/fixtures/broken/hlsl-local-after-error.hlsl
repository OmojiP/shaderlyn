// 誤りより後ろで宣言した局所変数は読めていないことがある。誤りを含む文から関数の終わりまでは見ないので、使う位置を報告しない。
// 期待: 7 CF0001

float F(float v)
{
    float a = v;
    float b = a +;
    float c = b * 2.0;
    return c + d;
}

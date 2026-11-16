// 取り込んだヘッダに構文の誤りがある。ヘッダの中は読めていないことがあるので、宣言の検査はしない (誤りは取り込んだヘッダの位置で 1 度伝える)。
// 期待: なし

#include "broken-helper.hlsl"

float Use(float v)
{
    return Helper(v) * missingAfterInclude;
}

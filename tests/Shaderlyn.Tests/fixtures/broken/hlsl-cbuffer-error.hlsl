// 定数バッファーの中の構文の誤り (関数の外)。宣言が読めていないので、宣言の検査はしない。
// 期待: 7 CF0001

cbuffer Params
{
    float gain
    float bias;
};

float Apply(float v)
{
    return v * gain + bias + missingName;
}

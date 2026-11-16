// 誤りを含む文は、誤りより前のトークンも読み違える (vArray[i] var c = 3; を宣言と読む)。その文からは見ない。
// 期待: 9 CF0001

void m()
{
    float3 vArray[3];
    let i = 1;
    vArray[i]
    var c = 3;
    let d = c;
}

// switch の case の中の構文の誤り。ほかの関数は検査する。
// 期待: 10 CF0001
// 期待: 18 CF0310

int Classify(int v)
{
    switch (v)
    {
    case 0:
        return 1 +;
    default:
        return 2;
    }
}

int Other(int v)
{
    return v + missingInOther;
}

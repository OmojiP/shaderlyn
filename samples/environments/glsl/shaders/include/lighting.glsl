// lit.frag が取り込む。include-paths の shaders/include から見つける
float lambert(vec3 normal, vec3 lightDir)
{
    return max(dot(normalize(normal), -lightDir), 0.0);
}

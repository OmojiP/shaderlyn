// lit.slang が import するモジュール。
module lighting;

public interface ILight
{
    float3 illuminate(float3 normal);
}

public struct DirectionalLight : ILight
{
    public float3 direction;
    public float3 color;

    public float3 illuminate(float3 normal)
    {
        return color * max(dot(normal, -direction), 0.0);
    }
}

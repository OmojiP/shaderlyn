#version 450
#extension GL_GOOGLE_include_directive : require

#include "lighting.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vNormal;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 1) uniform sampler2D albedo;
layout(set = 0, binding = 2) uniform Params
{
    vec3 lightDir;
    vec3 fogColor;
    float fogDensity;
} params;

void main()
{
    vec3 color = texture(albedo, vUv).rgb * lambert(vNormal, params.lightDir);
#ifdef USE_FOG
    color = mix(color, params.fogColor, params.fogDensity);
    outColor = vec4(color, params.fogColor.w);
#else
    outColor = vec4(color, 1.0);
#endif
}

#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(set = 0, binding = 0) uniform Camera
{
    mat4 viewProjection;
} camera;

layout(location = 0) out vec2 vUv;
out vec3 vNormal;   // Vulkan 向けでは location が要る

void main()
{
    vUv = inUv;
    vNormal = inNormal;
    gl_Position = camera.viewProjection * vec4(inPosition, 1.0);
}

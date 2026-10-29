using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 型と修飾子のキーワードの検査。glslang 16.6.0 の glslang.y の type_specifier_nonarray と修飾子の規則の動作と、
/// そこから呼ぶ Versions.cpp の検査 (doubleCheck、int16ScalarVectorCheck など) を移したもの。
/// </summary>
/// <remarks>
/// 機能の名前 (<c>'16-bit signed integer'</c> など) は glslang の文のまま使う。glslang の出力と照らしやすくするためである。
/// </remarks>
internal sealed partial class GlslRequirements
{
    private static readonly string[] AepGpuShader5 = ["GL_EXT_gpu_shader5", "GL_OES_gpu_shader5"];

    /// <summary>型のキーワードを、使える版と拡張で検査する (glslang の type_specifier_nonarray)。</summary>
    /// <param name="at">キーワードのトークン。</param>
    /// <remarks>case は glslang.y から機械的に写した。</remarks>
    public void CheckTypeKeyword(ShaderSyntaxToken at)
    {
        switch (at.Text)
        {
            case "uint":
                FullIntegerCheck(at, "unsigned integer");
                break;

            case "uvec2":
            case "uvec3":
            case "uvec4":
                FullIntegerCheck(at, "unsigned integer vector");
                break;

            case "double":
                RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "'double'");
                DoubleCheck(at, "double");
                break;

            case "bfloat16_t":
                Bfloat16ScalarVectorCheck(at, "bfloat16_t");
                break;

            case "floate5m2_t":
                Floate5m2ScalarVectorCheck(at, "floate5m2_t");
                break;

            case "floate4m3_t":
                Floate4m3ScalarVectorCheck(at, "floate4m3_t");
                break;

            case "floate2m1_t":
                Floate2m1ScalarVectorCheck(at, "floate2m1_t");
                break;

            case "floate3m2_t":
                Floate3m2ScalarVectorCheck(at, "floate3m2_t");
                break;

            case "floate2m3_t":
                Floate2m3ScalarVectorCheck(at, "floate2m3_t");
                break;

            case "floatue8m0_t":
                Floatue8m0ScalarVectorCheck(at, "floatue8m0_t");
                break;

            case "floatmxint8_t":
                Floatmxint8ScalarVectorCheck(at, "floatmxint8_t");
                break;

            case "float16_t":
                Float16ScalarVectorCheck(at, "float16_t");
                break;

            case "float32_t":
                ExplicitFloat32Check(at, "float32_t");
                break;

            case "float64_t":
                ExplicitFloat64Check(at, "float64_t");
                break;

            case "int8_t":
                Int8ScalarVectorCheck(at, "8-bit signed integer");
                break;

            case "uint8_t":
                Int8ScalarVectorCheck(at, "8-bit unsigned integer");
                break;

            case "int16_t":
                Int16ScalarVectorCheck(at, "16-bit signed integer");
                break;

            case "uint16_t":
                Int16ScalarVectorCheck(at, "16-bit unsigned integer");
                break;

            case "int32_t":
                ExplicitInt32Check(at, "32-bit signed integer");
                break;

            case "uint32_t":
                ExplicitInt32Check(at, "32-bit unsigned integer");
                break;

            case "int64_t":
                Int64Check(at, "64-bit integer");
                break;

            case "uint64_t":
                Int64Check(at, "64-bit unsigned integer");
                break;

            case "dvec2":
            case "dvec3":
            case "dvec4":
                RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "'double vector'");
                DoubleCheck(at, "double vector");
                break;

            case "bf16vec2":
            case "bf16vec3":
            case "bf16vec4":
                Bfloat16ScalarVectorCheck(at, "half float vector");
                break;

            case "fe5m2vec2":
            case "fe5m2vec3":
            case "fe5m2vec4":
                Floate5m2ScalarVectorCheck(at, "fe5m2 vector");
                break;

            case "fe4m3vec2":
            case "fe4m3vec3":
            case "fe4m3vec4":
                Floate4m3ScalarVectorCheck(at, "fe4m3 vector");
                break;

            case "fe2m1vec2":
            case "fe2m1vec3":
            case "fe2m1vec4":
                Floate2m1ScalarVectorCheck(at, "fe2m1 vector");
                break;

            case "fe3m2vec2":
            case "fe3m2vec3":
            case "fe3m2vec4":
                Floate3m2ScalarVectorCheck(at, "fe3m2 vector");
                break;

            case "fe2m3vec2":
            case "fe2m3vec3":
            case "fe2m3vec4":
                Floate2m3ScalarVectorCheck(at, "fe2m3 vector");
                break;

            case "fue8m0vec2":
            case "fue8m0vec3":
            case "fue8m0vec4":
                Floatue8m0ScalarVectorCheck(at, "fue8m0 vector");
                break;

            case "fmxint8vec2":
            case "fmxint8vec3":
            case "fmxint8vec4":
                Floatmxint8ScalarVectorCheck(at, "fmxint8 vector");
                break;

            case "f16vec2":
            case "f16vec3":
            case "f16vec4":
                Float16ScalarVectorCheck(at, "half float vector");
                break;

            case "f32vec2":
            case "f32vec3":
            case "f32vec4":
                ExplicitFloat32Check(at, "float32_t vector");
                break;

            case "f64vec2":
            case "f64vec3":
            case "f64vec4":
                ExplicitFloat64Check(at, "float64_t vector");
                break;

            case "i8vec2":
            case "i8vec3":
            case "i8vec4":
                Int8ScalarVectorCheck(at, "8-bit signed integer vector");
                break;

            case "i16vec2":
            case "i16vec3":
            case "i16vec4":
                Int16ScalarVectorCheck(at, "16-bit signed integer vector");
                break;

            case "i32vec2":
            case "i32vec3":
            case "i32vec4":
                ExplicitInt32Check(at, "32-bit signed integer vector");
                break;

            case "i64vec2":
            case "i64vec3":
            case "i64vec4":
                Int64Check(at, "64-bit integer vector");
                break;

            case "u8vec2":
            case "u8vec3":
            case "u8vec4":
                Int8ScalarVectorCheck(at, "8-bit unsigned integer vector");
                break;

            case "u16vec2":
            case "u16vec3":
            case "u16vec4":
                Int16ScalarVectorCheck(at, "16-bit unsigned integer vector");
                break;

            case "u32vec2":
            case "u32vec3":
            case "u32vec4":
                ExplicitInt32Check(at, "32-bit unsigned integer vector");
                break;

            case "u64vec2":
            case "u64vec3":
            case "u64vec4":
                Int64Check(at, "64-bit unsigned integer vector");
                break;

            case "dmat2":
            case "dmat3":
            case "dmat4":
            case "dmat2x2":
            case "dmat2x3":
            case "dmat2x4":
            case "dmat3x2":
            case "dmat3x3":
            case "dmat3x4":
            case "dmat4x2":
            case "dmat4x3":
            case "dmat4x4":
                RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "'double matrix'");
                DoubleCheck(at, "double matrix");
                break;

            case "f16mat2":
            case "f16mat3":
            case "f16mat4":
            case "f16mat2x2":
            case "f16mat2x3":
            case "f16mat2x4":
            case "f16mat3x2":
            case "f16mat3x3":
            case "f16mat3x4":
            case "f16mat4x2":
            case "f16mat4x3":
            case "f16mat4x4":
                Float16Check(at, "half float matrix");
                break;

            case "f32mat2":
            case "f32mat3":
            case "f32mat4":
            case "f32mat2x2":
            case "f32mat2x3":
            case "f32mat2x4":
            case "f32mat3x2":
            case "f32mat3x3":
            case "f32mat3x4":
            case "f32mat4x2":
            case "f32mat4x3":
            case "f32mat4x4":
                ExplicitFloat32Check(at, "float32_t matrix");
                break;

            case "f64mat2":
            case "f64mat3":
            case "f64mat4":
            case "f64mat2x2":
            case "f64mat2x3":
            case "f64mat2x4":
            case "f64mat3x2":
            case "f64mat3x3":
            case "f64mat3x4":
            case "f64mat4x2":
            case "f64mat4x3":
            case "f64mat4x4":
                ExplicitFloat64Check(at, "float64_t matrix");
                break;

            case "atomic_uint":
                VulkanRemoved(at, "atomic counter types");
                break;

            case "f16sampler1D":
            case "f16sampler2D":
            case "f16sampler3D":
            case "f16samplerCube":
            case "f16sampler1DShadow":
            case "f16sampler2DShadow":
            case "f16samplerCubeShadow":
            case "f16sampler1DArray":
            case "f16sampler2DArray":
            case "f16sampler1DArrayShadow":
            case "f16sampler2DArrayShadow":
            case "f16samplerCubeArray":
            case "f16samplerCubeArrayShadow":
            case "f16sampler2DRect":
            case "f16sampler2DRectShadow":
            case "f16samplerBuffer":
            case "f16sampler2DMS":
            case "f16sampler2DMSArray":
                Float16OpaqueCheck(at, "half float sampler");
                break;

            case "f16texture1D":
            case "f16texture2D":
            case "f16texture3D":
            case "f16textureCube":
            case "f16texture1DArray":
            case "f16texture2DArray":
            case "f16textureCubeArray":
            case "f16texture2DRect":
            case "f16textureBuffer":
            case "f16texture2DMS":
            case "f16texture2DMSArray":
                Float16OpaqueCheck(at, "half float texture");
                break;

            case "f16image1D":
            case "f16image2D":
            case "f16image3D":
            case "f16image2DRect":
            case "f16imageCube":
            case "f16imageBuffer":
            case "f16image1DArray":
            case "f16image2DArray":
            case "f16imageCubeArray":
            case "f16image2DMS":
            case "f16image2DMSArray":
                Float16OpaqueCheck(at, "half float image");
                break;

            case "attachmentEXT":
            case "iattachmentEXT":
            case "uattachmentEXT":
                RequireStage(at, [GlslShaderStage.Fragment], "'attachmentEXT input'");
                break;

            case "subpassInput":
            case "subpassInputMS":
            case "isubpassInput":
            case "isubpassInputMS":
            case "usubpassInput":
            case "usubpassInputMS":
                RequireStage(at, [GlslShaderStage.Fragment], "'subpass input'");
                break;

            case "f16subpassInput":
            case "f16subpassInputMS":
                Float16OpaqueCheck(at, "half float subpass input");
                RequireStage(at, [GlslShaderStage.Fragment], "'subpass input'");
                break;

            case "fcoopmatNV":
                FcoopmatCheckNV(at, "fcoopmatNV");
                break;

            case "icoopmatNV":
                IntcoopmatCheckNV(at, "icoopmatNV");
                break;

            case "ucoopmatNV":
                IntcoopmatCheckNV(at, "ucoopmatNV");
                break;

            case "coopmat":
                CoopmatCheck(at, "coopmat");
                break;

            case "tensorLayoutNV":
                TensorLayoutViewCheck(at, "tensorLayoutNV");
                break;

            case "tensorViewNV":
                TensorLayoutViewCheck(at, "tensorViewNV");
                break;

            case "coopvecNV":
                CoopvecCheck(at, "coopvecNV");
                break;

            case "tensorARM":
                TensorCheckARM(at, "tensorARM");
                break;

            case "vector":
                LongVectorCheck(at, "vector");
                break;

            default:
                break;
        }
    }

    /// <summary>修飾子のキーワードを検査する (glslang の storage_qualifier などの規則)。</summary>
    /// <param name="at">キーワードのトークン。</param>
    /// <param name="global">関数の外の宣言か。関数の中に書けない修飾子がある (glslang の globalCheck)。</param>
    /// <remarks>
    /// case は glslang.y から機械的に写した。<c>subroutine</c> は glslang が対応していない (unimplemented) が、それは報告しない
    /// (ドライバーの GLSL のコンパイラは受け付ける)。
    /// </remarks>
    public void CheckQualifierKeyword(ShaderSyntaxToken at, bool global)
    {
        switch (at.Text)
        {
            case "inout":
                GlobalCheck(at, global, "inout");
                break;

            case "in":
                GlobalCheck(at, global, "in");
                break;

            case "out":
                GlobalCheck(at, global, "out");
                break;

            case "centroid":
                ProfileRequires(at, GlslProfileMask.None, 120, [], "'centroid'");
                ProfileRequires(at, GlslProfileMask.Es, 300, [], "'centroid'");
                GlobalCheck(at, global, "centroid");
                break;

            case "uniform":
                GlobalCheck(at, global, "uniform");
                break;

            case "tileImageEXT":
                GlobalCheck(at, global, "tileImageEXT");
                break;

            case "shared":
                GlobalCheck(at, global, "shared");
                ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 430, ["GL_ARB_compute_shader"], "'shared'");
                ProfileRequires(at, GlslProfileMask.Es, 310, [], "'shared'");
                RequireStage(at, [GlslShaderStage.Compute, GlslShaderStage.Mesh, GlslShaderStage.Task], "'shared'");
                break;

            case "buffer":
                GlobalCheck(at, global, "buffer");
                break;

            case "attribute":
                RequireStage(at, [GlslShaderStage.Vertex], "'attribute'");
                RequireNotRemoved(at, GlslProfileMask.Core, 420, "attribute");
                RequireNotRemoved(at, GlslProfileMask.Es, 300, "attribute");
                GlobalCheck(at, global, "attribute");
                break;

            case "varying":
                RequireNotRemoved(at, GlslProfileMask.Core, 420, "varying");
                RequireNotRemoved(at, GlslProfileMask.Es, 300, "varying");
                GlobalCheck(at, global, "varying");
                break;

            case "patch":
                GlobalCheck(at, global, "patch");
                RequireStage(at, [GlslShaderStage.TessellationControl, GlslShaderStage.TessellationEvaluation], "'patch'");
                break;

            case "sample":
                GlobalCheck(at, global, "sample");
                break;

            case "resourceheap":
                GlobalCheck(at, global, "resourceHeap");
                break;

            case "samplerheap":
                GlobalCheck(at, global, "samplerHeap");
                break;

            case "hitAttributeNV":
                GlobalCheck(at, global, "hitAttributeNV");
                RequireStage(at, [GlslShaderStage.Intersection, GlslShaderStage.ClosestHit, GlslShaderStage.AnyHit], "'hitAttributeNV'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_NV_ray_tracing"], "'hitAttributeNV'");
                break;

            case "hitObjectAttributeNV":
                GlobalCheck(at, global, "hitAttributeNV");
                RequireStage(at, [GlslShaderStage.RayGeneration, GlslShaderStage.ClosestHit, GlslShaderStage.Miss], "'hitObjectAttributeNV'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_NV_shader_invocation_reorder"], "'hitObjectAttributeNV'");
                break;

            case "hitObjectAttributeEXT":
                GlobalCheck(at, global, "hitAttributeEXT");
                RequireStage(at, [GlslShaderStage.RayGeneration, GlslShaderStage.ClosestHit, GlslShaderStage.Miss], "'hitObjectAttributeEXT'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_EXT_shader_invocation_reorder"], "'hitObjectAttributeEXT'");
                break;

            case "hitAttributeEXT":
                GlobalCheck(at, global, "hitAttributeEXT");
                RequireStage(at, [GlslShaderStage.Intersection, GlslShaderStage.ClosestHit, GlslShaderStage.AnyHit], "'hitAttributeEXT'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_EXT_ray_tracing"], "'hitAttributeNV'");
                break;

            case "rayPayloadNV":
                GlobalCheck(at, global, "rayPayloadNV");
                RequireStage(at, [GlslShaderStage.RayGeneration, GlslShaderStage.ClosestHit, GlslShaderStage.AnyHit, GlslShaderStage.Miss], "'rayPayloadNV'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_NV_ray_tracing"], "'rayPayloadNV'");
                break;

            case "rayPayloadEXT":
                GlobalCheck(at, global, "rayPayloadEXT");
                RequireStage(at, [GlslShaderStage.RayGeneration, GlslShaderStage.ClosestHit, GlslShaderStage.AnyHit, GlslShaderStage.Miss], "'rayPayloadEXT'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_EXT_ray_tracing"], "'rayPayloadEXT'");
                break;

            case "rayPayloadInNV":
                GlobalCheck(at, global, "rayPayloadInNV");
                RequireStage(at, [GlslShaderStage.ClosestHit, GlslShaderStage.AnyHit, GlslShaderStage.Miss], "'rayPayloadInNV'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_NV_ray_tracing"], "'rayPayloadInNV'");
                break;

            case "rayPayloadInEXT":
                GlobalCheck(at, global, "rayPayloadInEXT");
                RequireStage(at, [GlslShaderStage.ClosestHit, GlslShaderStage.AnyHit, GlslShaderStage.Miss], "'rayPayloadInEXT'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_EXT_ray_tracing"], "'rayPayloadInEXT'");
                break;

            case "callableDataNV":
                GlobalCheck(at, global, "callableDataNV");
                RequireStage(at, [GlslShaderStage.RayGeneration, GlslShaderStage.ClosestHit, GlslShaderStage.Miss, GlslShaderStage.Callable], "'callableDataNV'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_NV_ray_tracing"], "'callableDataNV'");
                break;

            case "callableDataEXT":
                GlobalCheck(at, global, "callableDataEXT");
                RequireStage(at, [GlslShaderStage.RayGeneration, GlslShaderStage.ClosestHit, GlslShaderStage.Miss, GlslShaderStage.Callable], "'callableDataEXT'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_EXT_ray_tracing"], "'callableDataEXT'");
                break;

            case "callableDataInNV":
                GlobalCheck(at, global, "callableDataInNV");
                RequireStage(at, [GlslShaderStage.Callable], "'callableDataInNV'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_NV_ray_tracing"], "'callableDataInNV'");
                break;

            case "callableDataInEXT":
                GlobalCheck(at, global, "callableDataInEXT");
                RequireStage(at, [GlslShaderStage.Callable], "'callableDataInEXT'");
                ProfileRequires(at, GlslProfileMask.Core, 460, ["GL_EXT_ray_tracing"], "'callableDataInEXT'");
                break;

            case "devicecoherent":
                RequireExtensions(at, ["GL_KHR_memory_scope_semantics"], "'devicecoherent'");
                break;

            case "queuefamilycoherent":
                RequireExtensions(at, ["GL_KHR_memory_scope_semantics"], "'queuefamilycoherent'");
                break;

            case "workgroupcoherent":
                RequireExtensions(at, ["GL_KHR_memory_scope_semantics"], "'workgroupcoherent'");
                break;

            case "subgroupcoherent":
                RequireExtensions(at, ["GL_KHR_memory_scope_semantics"], "'subgroupcoherent'");
                break;

            case "nonprivate":
                RequireExtensions(at, ["GL_KHR_memory_scope_semantics"], "'nonprivate'");
                break;

            case "shadercallcoherent":
                RequireExtensions(at, ["GL_EXT_ray_tracing"], "'shadercallcoherent'");
                break;

            case "subroutine":
                SpvRemoved(at, "subroutine");
                GlobalCheck(at, global, "subroutine");
                break;

            case "taskPayloadSharedEXT":
                GlobalCheck(at, global, "taskPayloadSharedEXT");
                RequireStage(at, [GlslShaderStage.Task, GlslShaderStage.Mesh], "'taskPayloadSharedEXT'");
                break;

            case "smooth":
                GlobalCheck(at, global, "smooth");
                ProfileRequires(at, GlslProfileMask.None, 130, [], "'smooth'");
                ProfileRequires(at, GlslProfileMask.Es, 300, [], "'smooth'");
                break;

            case "flat":
                GlobalCheck(at, global, "flat");
                ProfileRequires(at, GlslProfileMask.None, 130, [], "'flat'");
                ProfileRequires(at, GlslProfileMask.Es, 300, [], "'flat'");
                break;

            case "noperspective":
                GlobalCheck(at, global, "noperspective");
                ProfileRequires(at, GlslProfileMask.Es, 0, ["GL_NV_shader_noperspective_interpolation"], "'noperspective'");
                ProfileRequires(at, GlslProfileMask.None, 130, [], "'noperspective'");
                break;

            case "__explicitInterpAMD":
                GlobalCheck(at, global, "__explicitInterpAMD");
                ProfileRequires(at, GlslProfileMask.Core, 450, ["GL_AMD_shader_explicit_vertex_parameter"], "'explicit interpolation'");
                ProfileRequires(at, GlslProfileMask.Compatibility, 450, ["GL_AMD_shader_explicit_vertex_parameter"], "'explicit interpolation'");
                break;

            case "pervertexNV":
                GlobalCheck(at, global, "pervertexNV");
                ProfileRequires(at, GlslProfileMask.Core, 0, ["GL_NV_fragment_shader_barycentric"], "'fragment shader barycentric'");
                ProfileRequires(at, GlslProfileMask.Compatibility, 0, ["GL_NV_fragment_shader_barycentric"], "'fragment shader barycentric'");
                ProfileRequires(at, GlslProfileMask.Es, 0, ["GL_NV_fragment_shader_barycentric"], "'fragment shader barycentric'");
                break;

            case "pervertexEXT":
                GlobalCheck(at, global, "pervertexEXT");
                ProfileRequires(at, GlslProfileMask.Core, 0, ["GL_EXT_fragment_shader_barycentric"], "'fragment shader barycentric'");
                ProfileRequires(at, GlslProfileMask.Compatibility, 0, ["GL_EXT_fragment_shader_barycentric"], "'fragment shader barycentric'");
                ProfileRequires(at, GlslProfileMask.Es, 0, ["GL_EXT_fragment_shader_barycentric"], "'fragment shader barycentric'");
                break;

            case "perprimitiveNV":
                GlobalCheck(at, global, "perprimitiveNV");
                RequireStage(at, [GlslShaderStage.Fragment, GlslShaderStage.Mesh], "'perprimitiveNV'");
                if (Stage == GlslShaderStage.Fragment) { RequireExtensions(at, ["GL_NV_mesh_shader"], "'perprimitiveNV'"); }
                break;

            case "perprimitiveEXT":
                GlobalCheck(at, global, "perprimitiveEXT");
                RequireStage(at, [GlslShaderStage.Fragment, GlslShaderStage.Mesh], "'perprimitiveEXT'");
                if (Stage == GlslShaderStage.Fragment) { RequireExtensions(at, ["GL_EXT_mesh_shader"], "'perprimitiveEXT'"); }
                break;

            case "perviewNV":
                GlobalCheck(at, global, "perviewNV");
                RequireStage(at, [GlslShaderStage.Mesh], "'perviewNV'");
                break;

            case "taskNV":
                GlobalCheck(at, global, "taskNV");
                RequireStage(at, [GlslShaderStage.Task, GlslShaderStage.Mesh], "'taskNV'");
                break;

            case "invariant":
                GlobalCheck(at, global, "invariant");
                ProfileRequires(at, GlslProfileMask.None, 120, [], "'invariant'");
                break;

            case "precise":
                ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 400, ["GL_ARB_gpu_shader5"], "'precise'");
                ProfileRequires(at, GlslProfileMask.Es, 320, AepGpuShader5, "'precise'");
                break;

            case "highp":
                ProfileRequires(at, GlslProfileMask.None, 130, [], "'highp precision qualifier'");
                break;

            case "mediump":
                ProfileRequires(at, GlslProfileMask.None, 130, [], "'mediump precision qualifier'");
                break;

            case "lowp":
                ProfileRequires(at, GlslProfileMask.None, 130, [], "'lowp precision qualifier'");
                break;

            default:
                break;
        }
    }

    /// <summary>整数の演算に要る版 (glslang の fullIntegerCheck)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void FullIntegerCheck(ShaderSyntaxToken at, string feature)
    {
        ProfileRequires(at, GlslProfileMask.None, 130, [], $"'{feature}'");
        ProfileRequires(at, GlslProfileMask.Es, 300, [], $"'{feature}'");
    }

    /// <summary>double に要る版と拡張 (glslang の doubleCheck)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void DoubleCheck(ShaderSyntaxToken at, string feature)
        => ProfileRequires(
            at,
            GlslProfileMask.Core | GlslProfileMask.Compatibility,
            400,
            Stage == GlslShaderStage.Vertex ? ["GL_ARB_gpu_shader_fp64", "GL_ARB_vertex_attrib_64bit"] : ["GL_ARB_gpu_shader_fp64"],
            $"'{feature}'");

    /// <summary>関数の中に書けない (glslang の globalCheck。「not allowed in nested scope」)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="global">関数の外の宣言か。</param>
    /// <param name="feature">機能の名前。</param>
    public void GlobalCheck(ShaderSyntaxToken at, bool global, string feature)
    {
        if (!global)
        {
            Report(GlslRuleDescriptors.QualifierNotAllowed, at, $"'{feature}' は関数の中に書けません。関数の外の宣言にだけ付けられます。");
        }
    }

    /// <summary>そのプロファイルでは、そのバージョンで取り除かれた (glslang の requireNotRemoved)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="profiles">取り除いたプロファイル。</param>
    /// <param name="removedVersion">取り除いたバージョン。</param>
    /// <param name="feature">機能の名前。</param>
    public void RequireNotRemoved(ShaderSyntaxToken at, GlslProfileMask profiles, int removedVersion, string feature)
    {
        if ((Profile & profiles) != 0 && Version >= removedVersion)
        {
            Report(GlslRuleDescriptors.NotAvailable, at, $"'{feature}' は {DescribeProfile()} の #version {removedVersion} で取り除かれました。");
        }
    }

    /// <summary>SPIR-V を作るときは使えない (glslang の spvRemoved)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void SpvRemoved(ShaderSyntaxToken at, string feature)
    {
        if (SpirvVersion > 0)
        {
            Report(GlslRuleDescriptors.NotAvailable, at, $"'{feature}' は SPIR-V を作るときには使えません。");
        }
    }

    /// <summary>Vulkan 向けでは使えない (glslang の vulkanRemoved)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void VulkanRemoved(ShaderSyntaxToken at, string feature)
    {
        if (Vulkan == true)
        {
            Report(GlslRuleDescriptors.NotAvailable, at, $"'{feature}' は Vulkan 向けの GLSL では使えません。");
        }
    }

    /// <summary>Vulkan 向けでなければ使えない (glslang の requireVulkan)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void RequireVulkan(ShaderSyntaxToken at, string feature)
    {
        if (Vulkan == false)
        {
            Report(GlslRuleDescriptors.NotAvailable, at, $"'{feature}' は Vulkan 向けの GLSL でだけ使えます。設定ファイルの glsl: target が Vulkan か確かめてください。");
        }
    }

    /// <summary>SPIR-V を作るときでなければ使えない (glslang の requireSpv)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void RequireSpv(ShaderSyntaxToken at, string feature)
    {
        if (SpirvVersion == 0)
        {
            Report(GlslRuleDescriptors.NotAvailable, at, $"'{feature}' は SPIR-V を作るときにだけ使えます。");
        }
    }

    public void Float16Check(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_AMD_gpu_shader_half_float", "GL_EXT_shader_explicit_arithmetic_types", "GL_EXT_shader_explicit_arithmetic_types_float16"], $"'{feature}'");

    private void Float16ScalarVectorCheck(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_AMD_gpu_shader_half_float", "GL_EXT_shader_16bit_storage", "GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_float16"], $"'{feature}'");

    private void Bfloat16ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_bfloat16"], $"'{feature}'");

    private void Floate5m2ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_e5m2"], $"'{feature}'");

    private void Floate4m3ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_e4m3"], $"'{feature}'");

    private void Floate2m1ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_e2m1"], $"'{feature}'");

    private void Floate3m2ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_e3m2"], $"'{feature}'");

    private void Floate2m3ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_e2m3"], $"'{feature}'");

    private void Floatue8m0ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_ue8m0"], $"'{feature}'");

    private void Floatmxint8ScalarVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_float_mxint8"], $"'{feature}'");

    private void ExplicitFloat32Check(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_float32"], $"'{feature}'");

    private void ExplicitFloat64Check(ShaderSyntaxToken at, string feature)
    {
        RequireExtensions(at, ["GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_float64"], $"'{feature}'");
        RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, $"'{feature}'");
        ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, IsOn("GL_ARB_gpu_shader_fp64", at) && IsOn("GL_NV_gpu_shader5", at) ? 150 : 400, [], $"'{feature}'");
    }

    private void Float16OpaqueCheck(ShaderSyntaxToken at, string feature)
    {
        RequireExtensions(at, ["GL_AMD_gpu_shader_half_float_fetch"], $"'{feature}'");
        RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, $"'{feature}'");
        ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 400, [], $"'{feature}'");
    }

    private void Int16ScalarVectorCheck(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_AMD_gpu_shader_int16", "GL_EXT_shader_16bit_storage", "GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_int16"], $"'{feature}'");

    private void Int8ScalarVectorCheck(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_EXT_shader_8bit_storage", "GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_int8"], $"'{feature}'");

    private void ExplicitInt32Check(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_int32"], $"'{feature}'");

    public void Int64Check(ShaderSyntaxToken at, string feature)
    {
        RequireExtensions(at, ["GL_ARB_gpu_shader_int64", "GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_int64"], $"'{feature}'");
        RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, $"'{feature}'");
        ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, IsOn("GL_NV_gpu_shader5", at) ? 150 : 400, [], $"'{feature}'");
    }

    private void FcoopmatCheckNV(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_NV_cooperative_matrix"], $"'{feature}'");

    private void IntcoopmatCheckNV(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_NV_integer_cooperative_matrix"], $"'{feature}'");

    private void CoopmatCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_KHR_cooperative_matrix"], $"'{feature}'");

    private void TensorLayoutViewCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_NV_cooperative_matrix2"], $"'{feature}'");

    private void CoopvecCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_NV_cooperative_vector"], $"'{feature}'");

    private void TensorCheckARM(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_ARM_tensors"], $"'{feature}'");

    private void LongVectorCheck(ShaderSyntaxToken at, string feature) => RequireExtensions(at, ["GL_EXT_long_vector"], $"'{feature}'");

    /// <summary>16 ビットの整数に要る拡張 (glslang の explicitInt16Check)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void ExplicitInt16Check(ShaderSyntaxToken at, string feature)
        => RequireExtensions(at, ["GL_AMD_gpu_shader_int16", "GL_EXT_shader_explicit_arithmetic_types", "GL_EXT_shader_explicit_arithmetic_types_int16"], $"'{feature}'");

    /// <summary>配列 (と配列を含む構造体) の比較と代入に要る版 (glslang の arrayObjectCheck)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="feature">機能の名前。</param>
    public void ArrayObjectCheck(ShaderSyntaxToken at, string feature)
    {
        ProfileRequires(at, GlslProfileMask.None, 120, ["GL_3DL_array_objects"], $"'{feature}'");
        ProfileRequires(at, GlslProfileMask.Es, 300, [], $"'{feature}'");
    }

    /// <summary>配列の配列に要る版 (glslang の arrayOfArrayVersionCheck)。</summary>
    /// <param name="at">位置のトークン。</param>
    /// <param name="dimensions">配列の次元の数。</param>
    public void ArrayOfArrayVersionCheck(ShaderSyntaxToken at, int dimensions)
    {
        if (dimensions <= 1)
        {
            return;
        }

        const string feature = "配列の配列";
        RequireProfile(at, GlslProfileMask.Es | GlslProfileMask.Core | GlslProfileMask.Compatibility, feature);
        ProfileRequires(at, GlslProfileMask.Es, 310, [], feature);
        ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 430, [], feature);
    }
}

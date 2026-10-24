namespace VfxStudio.Renderer.Shaders;

public static class ShaderSources
{
    // =========================================================================
    // 1. GPU 512 Bone Hardware Skinning & PBR/Basic Mesh Shader
    // =========================================================================

    public const string SkinningVertexShader = @"#version 460 core
layout (location = 0) in vec3 a_Position;
layout (location = 1) in vec3 a_Normal;
layout (location = 2) in vec2 a_TexCoords;
layout (location = 3) in uvec4 a_BoneIndices;
layout (location = 4) in vec4 a_BoneWeights;

layout (std430, binding = 0) readonly buffer BoneBuffer {
    mat4 u_BoneMatrices[512];
};

uniform mat4 u_Model;
uniform mat4 u_View;
uniform mat4 u_Projection;
uniform int u_HasSkinning;

out vec3 v_FragPos;
out vec3 v_Normal;
out vec2 v_TexCoords;

void main() {
    vec4 localPos;
    vec3 localNormal;

    float totalWeight = a_BoneWeights.x + a_BoneWeights.y + a_BoneWeights.z + a_BoneWeights.w;
    if (u_HasSkinning != 0 && totalWeight > 0.001) {
        // Normalize weights if needed
        vec4 w = a_BoneWeights / totalWeight;
        mat4 skinMat = w.x * u_BoneMatrices[a_BoneIndices.x] +
                       w.y * u_BoneMatrices[a_BoneIndices.y] +
                       w.z * u_BoneMatrices[a_BoneIndices.z] +
                       w.w * u_BoneMatrices[a_BoneIndices.w];
        localPos = skinMat * vec4(a_Position, 1.0);
        localNormal = mat3(skinMat) * a_Normal;
    } else {
        localPos = vec4(a_Position, 1.0);
        localNormal = a_Normal;
    }

    vec4 worldPos = u_Model * localPos;
    v_FragPos = worldPos.xyz;
    v_Normal = normalize(mat3(u_Model) * localNormal);
    v_TexCoords = a_TexCoords;

    gl_Position = u_Projection * u_View * worldPos;
}";

    public const string MeshFragmentShader = @"#version 460 core
layout (location = 0) out vec4 FragColor;
layout (location = 1) out vec4 BrightColor;

in vec3 v_FragPos;
in vec3 v_Normal;
in vec2 v_TexCoords;

uniform vec4 u_BaseColor;
uniform vec3 u_EmissionColor;
uniform float u_EmissionIntensity;
uniform int u_HasTexture;
uniform sampler2D u_AlbedoMap;

const vec3 LIGHT_DIR = normalize(vec3(0.5, 1.0, 0.7));
const vec3 LIGHT_COLOR = vec3(1.0, 0.98, 0.95);
const vec3 AMBIENT_COLOR = vec3(0.25, 0.28, 0.35);

void main() {
    vec4 texColor = (u_HasTexture != 0) ? texture(u_AlbedoMap, v_TexCoords) : vec4(1.0);
    if (u_EmissionIntensity > 0.01) {
        // VFX Emissive Mesh mode (ports sim_preview.py _mesh_shader: mask = max(r,g,b) * a)
        float mask = (u_HasTexture != 0) ? (max(max(texColor.r, texColor.g), texColor.b) * texColor.a) : 0.85;
        float finalAlpha = mask * u_BaseColor.a;
        if (finalAlpha < 0.02) discard;

        vec3 emission = u_EmissionColor * u_EmissionIntensity * (0.55 + 0.65 * mask);
        FragColor = vec4(emission, finalAlpha);
        float brightness = dot(emission, vec3(0.2126, 0.7152, 0.0722));
        BrightColor = (brightness > 1.0) ? vec4(emission * finalAlpha, 1.0) : vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    vec4 albedo = texColor * u_BaseColor;
    if (albedo.a < 0.1) discard;

    vec3 norm = normalize(v_Normal);
    float diff = max(dot(norm, LIGHT_DIR), 0.0);
    vec3 diffuse = diff * LIGHT_COLOR * albedo.rgb;
    vec3 ambient = AMBIENT_COLOR * albedo.rgb;

    vec3 finalRgb = ambient + diffuse;
    FragColor = vec4(finalRgb, albedo.a);
    BrightColor = vec4(0.0, 0.0, 0.0, 1.0);
}";

    // =========================================================================
    // 2. VFX Particle, Trail Ribbon, and Wireframe Shapes Shader
    // =========================================================================

    public const string VfxVertexShader = @"#version 460 core
layout (location = 0) in vec3 a_Position;
layout (location = 1) in vec2 a_TexCoords;
layout (location = 2) in vec4 a_Color;

uniform mat4 u_Model;
uniform mat4 u_View;
uniform mat4 u_Projection;

out vec2 v_TexCoords;
out vec4 v_Color;

void main() {
    v_TexCoords = a_TexCoords;
    v_Color = a_Color;
    gl_Position = u_Projection * u_View * u_Model * vec4(a_Position, 1.0);
}";

    public const string VfxFragmentShader = @"#version 460 core
layout (location = 0) out vec4 FragColor;
layout (location = 1) out vec4 BrightColor;

in vec2 v_TexCoords;
in vec4 v_Color;

uniform vec4 u_TintColor;
uniform float u_Intensity;
uniform int u_HasTexture;
uniform int u_IsRibbon;
uniform sampler2D u_ParticleTex;

void main() {
    float effAlpha = 1.0;
    vec3 texRgb = vec3(1.0);

    if (u_HasTexture != 0) {
        vec4 texColor = texture(u_ParticleTex, v_TexCoords);
        effAlpha = texColor.a;
        texRgb = texColor.rgb;
    } else if (u_IsRibbon != 0) {
        // Smooth luminous profile across ribbon width so untextured ribbons look like glowing blade trails
        float across = sin(clamp(v_TexCoords.y, 0.0, 1.0) * 3.14159265);
        effAlpha = pow(max(across, 0.0), 1.35);
    } else {
        // Smooth radial falloff from quad center so untextured particles look like glowing orbs/sparks, never solid squares
        vec2 c = v_TexCoords * 2.0 - 1.0;
        float dist = length(c);
        effAlpha = pow(clamp(1.0 - dist, 0.0, 1.0), 2.2);
    }

    vec4 color = vec4(texRgb * v_Color.rgb * u_TintColor.rgb, effAlpha * v_Color.a * u_TintColor.a);
    if (color.a < 0.015) discard;

    vec3 hdrColor = color.rgb * u_Intensity;
    FragColor = vec4(hdrColor, color.a);

    // Emit into bloom buffer if glowing
    float brightness = dot(hdrColor, vec3(0.2126, 0.7152, 0.0722));
    if (brightness > 1.0) {
        BrightColor = vec4(hdrColor * color.a, 1.0);
    } else {
        BrightColor = vec4(0.0, 0.0, 0.0, 1.0);
    }
}";

    public const string WireframeVertexShader = @"#version 460 core
layout (location = 0) in vec3 a_Position;

uniform mat4 u_Model;
uniform mat4 u_View;
uniform mat4 u_Projection;

void main() {
    gl_Position = u_Projection * u_View * u_Model * vec4(a_Position, 1.0);
}";

    public const string WireframeFragmentShader = @"#version 460 core
layout (location = 0) out vec4 FragColor;
layout (location = 1) out vec4 BrightColor;

uniform vec4 u_Color;

void main() {
    FragColor = u_Color;
    BrightColor = vec4(0.0, 0.0, 0.0, 1.0);
}";

    // =========================================================================
    // 3. HDR Bloom Blur & Composite Tone Mapping Shaders
    // =========================================================================

    public const string FullscreenQuadVertexShader = @"#version 460 core
layout (location = 0) in vec2 a_Position;
layout (location = 1) in vec2 a_TexCoords;

out vec2 v_TexCoords;

void main() {
    v_TexCoords = a_TexCoords;
    gl_Position = vec4(a_Position, 0.0, 1.0);
}";

    public const string GaussianBlurFragmentShader = @"#version 460 core
out vec4 FragColor;

in vec2 v_TexCoords;

uniform sampler2D u_Image;
uniform int u_Horizontal;

const float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);

void main() {
    vec2 tex_offset = 1.0 / textureSize(u_Image, 0);
    vec3 result = texture(u_Image, v_TexCoords).rgb * weights[0];

    if (u_Horizontal != 0) {
        for (int i = 1; i < 5; ++i) {
            result += texture(u_Image, v_TexCoords + vec2(tex_offset.x * i, 0.0)).rgb * weights[i];
            result += texture(u_Image, v_TexCoords - vec2(tex_offset.x * i, 0.0)).rgb * weights[i];
        }
    } else {
        for (int i = 1; i < 5; ++i) {
            result += texture(u_Image, v_TexCoords + vec2(0.0, tex_offset.y * i)).rgb * weights[i];
            result += texture(u_Image, v_TexCoords - vec2(0.0, tex_offset.y * i)).rgb * weights[i];
        }
    }

    FragColor = vec4(result, 1.0);
}";

    public const string CompositeToneMapFragmentShader = @"#version 460 core
out vec4 FragColor;

in vec2 v_TexCoords;

uniform sampler2D u_Scene;
uniform sampler2D u_BloomBlur;
uniform float u_BloomIntensity;
uniform float u_Exposure;

// ACES Film Tone Mapping curve
vec3 ACESFilm(vec3 x) {
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main() {
    vec3 hdrColor = texture(u_Scene, v_TexCoords).rgb;
    vec3 bloomColor = texture(u_BloomBlur, v_TexCoords).rgb;

    // Additive Bloom blend with intensity control
    vec3 combined = hdrColor + bloomColor * u_BloomIntensity;
    combined *= u_Exposure;

    // ACES Tone mapping
    vec3 mapped = ACESFilm(combined);

    // Gamma correction (sRGB curve: 1.0 / 2.2)
    mapped = pow(mapped, vec3(1.0 / 2.2));

    FragColor = vec4(mapped, 1.0);
}";
}

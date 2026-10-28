using System;
using System.Collections.Generic;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering {
    /// <summary>Small helpers around raw GL objects. All math uses System.Numerics (row-vector convention).</summary>
    public static class GlUtil {
        private static readonly float[] matrixBuffer = new float[16];

        public static void SetMatrix(int location, Matrix4x4 matrix) {
            if (location < 0) return;
            var buffer = matrixBuffer;
            buffer[0] = matrix.M11; buffer[1] = matrix.M12; buffer[2] = matrix.M13; buffer[3] = matrix.M14;
            buffer[4] = matrix.M21; buffer[5] = matrix.M22; buffer[6] = matrix.M23; buffer[7] = matrix.M24;
            buffer[8] = matrix.M31; buffer[9] = matrix.M32; buffer[10] = matrix.M33; buffer[11] = matrix.M34;
            buffer[12] = matrix.M41; buffer[13] = matrix.M42; buffer[14] = matrix.M43; buffer[15] = matrix.M44;
            GL.UniformMatrix4(location, 1, false, buffer);
        }
        public static Matrix4x4 Perspective(float fovY, float aspect, float near, float far) {
            float focal = 1f / MathF.Tan(fovY / 2f);
            return new Matrix4x4(
                focal / aspect, 0, 0, 0,
                0, focal, 0, 0,
                0, 0, (far + near) / (near - far), -1,
                0, 0, 2 * far * near / (near - far), 0);
        }
    }

    public sealed class ShaderProgram : IDisposable {
        public int Handle { get; private set; }
        private readonly Dictionary<string, int> locations = new Dictionary<string, int>();

        public ShaderProgram(string vertexSource, string fragmentSource) {
            int vertexShader = Compile(ShaderType.VertexShader, vertexSource);
            int fragmentShader = Compile(ShaderType.FragmentShader, fragmentSource);
            Handle = GL.CreateProgram();
            GL.AttachShader(Handle, vertexShader);
            GL.AttachShader(Handle, fragmentShader);
            GL.LinkProgram(Handle);
            GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("Shader link failed: " + GL.GetProgramInfoLog(Handle));
            GL.DetachShader(Handle, vertexShader);
            GL.DetachShader(Handle, fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
        }

        private static int Compile(ShaderType type, string source) {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception(type + " compile failed: " + GL.GetShaderInfoLog(shader));
            return shader;
        }

        public void Use() => GL.UseProgram(Handle);

        public int Loc(string name) {
            if (!locations.TryGetValue(name, out int location)) {
                location = GL.GetUniformLocation(Handle, name);
                locations[name] = location;
            }
            return location;
        }

        // Last value uploaded per uniform location; skips redundant GL calls (big win with thousands of draws).
        private readonly Dictionary<int, Vector4> cache = new Dictionary<int, Vector4>();
        private readonly Dictionary<int, Matrix4x4> matrixCache = new Dictionary<int, Matrix4x4>();

        private bool Changed(int location, Vector4 value) {
            if (cache.TryGetValue(location, out var old) && old == value) return false;
            cache[location] = value;
            return true;
        }

        /// <summary>Forget cached values (call after the program is re-linked or another program changed state).</summary>
        public void ResetCache() { cache.Clear(); matrixCache.Clear(); }

        public void Set(string name, Matrix4x4 matrix) {
            int location = Loc(name);
            if (location < 0) return;
            if (matrixCache.TryGetValue(location, out var old) && old == matrix) return;
            matrixCache[location] = matrix;
            GlUtil.SetMatrix(location, matrix);
        }
        public void Set(string name, float value) { int location = Loc(name); if (location >= 0 && Changed(location, new Vector4(value, 0, 0, 0))) GL.Uniform1(location, value); }
        public void Set(string name, int value) { int location = Loc(name); if (location >= 0 && Changed(location, new Vector4(value, 0, 0, 1))) GL.Uniform1(location, value); }
        public void Set(string name, bool value) => Set(name, value ? 1 : 0);
        public void Set(string name, Vector2 value) { int location = Loc(name); if (location >= 0 && Changed(location, new Vector4(value.X, value.Y, 0, 0))) GL.Uniform2(location, value.X, value.Y); }
        public void Set(string name, Vector3 value) { int location = Loc(name); if (location >= 0 && Changed(location, new Vector4(value, 0))) GL.Uniform3(location, value.X, value.Y, value.Z); }
        public void Set(string name, Vector4 value) { int location = Loc(name); if (location >= 0 && Changed(location, value)) GL.Uniform4(location, value.X, value.Y, value.Z, value.W); }

        public void Dispose() {
            if (Handle != 0) GL.DeleteProgram(Handle);
            Handle = 0;
        }
    }

    /// <summary>Indexed triangle mesh with interleaved pos(3) nrm(3) uv(2) col(4).</summary>
    public sealed class GpuMesh : IDisposable {
        public const int Stride = 12;
        public int Vao, Vbo, Ebo, IndexCount;

        public static GpuMesh Create(float[] interleaved, uint[] indices, bool dynamic = false) {
            var mesh = new GpuMesh { IndexCount = indices.Length };
            mesh.Vao = GL.GenVertexArray();
            GL.BindVertexArray(mesh.Vao);
            mesh.Vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved,
                          dynamic ? BufferUsageHint.DynamicDraw : BufferUsageHint.StaticDraw);
            mesh.Ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, mesh.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
            int stride = Stride * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
            GL.BindVertexArray(0);
            return mesh;
        }

        public void Draw() {
            if (IndexCount == 0) return;
            GL.BindVertexArray(Vao);
            GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, 0);
        }

        /// <summary>Replaces the interleaved vertex data (same vertex count as at creation).</summary>
        public void UpdateVertices(float[] interleaved) {
            if (Vbo == 0) return;
            GL.BindBuffer(BufferTarget.ArrayBuffer, Vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, interleaved.Length * sizeof(float), interleaved);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        public void Dispose() {
            if (Vao != 0) GL.DeleteVertexArray(Vao);
            if (Vbo != 0) GL.DeleteBuffer(Vbo);
            if (Ebo != 0) GL.DeleteBuffer(Ebo);
            Vao = Vbo = Ebo = 0;
        }
    }

    /// <summary>Line list with pos(3) + rgba(4) per vertex.</summary>
    public sealed class GpuLines : IDisposable {
        public int Vao, Vbo, VertexCount;

        public static GpuLines Create(List<float> data, bool dynamic = false) {
            var lines = new GpuLines { VertexCount = data.Count / 7 };
            lines.Vao = GL.GenVertexArray();
            GL.BindVertexArray(lines.Vao);
            lines.Vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, lines.Vbo);
            var values = data.ToArray();
            GL.BufferData(BufferTarget.ArrayBuffer, values.Length * sizeof(float), values,
                          dynamic ? BufferUsageHint.DynamicDraw : BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
            GL.BindVertexArray(0);
            return lines;
        }

        public void Draw() {
            if (VertexCount == 0) return;
            GL.BindVertexArray(Vao);
            GL.DrawArrays(PrimitiveType.Lines, 0, VertexCount);
        }

        /// <summary>Replaces the line data (any length).</summary>
        public void Update(List<float> data) {
            if (Vbo == 0) return;
            var values = data.ToArray();
            GL.BindBuffer(BufferTarget.ArrayBuffer, Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, values.Length * sizeof(float), values, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            VertexCount = values.Length / 7;
        }

        public void Dispose() {
            if (Vao != 0) GL.DeleteVertexArray(Vao);
            if (Vbo != 0) GL.DeleteBuffer(Vbo);
            Vao = Vbo = 0;
        }
    }

    public static class Shaders {
        public const string MeshVS = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec2 aUV;
layout(location=3) in vec4 aCol;
uniform mat4 uModel;
uniform mat4 uViewProj;
uniform vec2 uUVOffset;
uniform mat4 uView;
uniform int uEnv;
out vec3 vNrm;
out vec2 vUV;
out vec4 vCol;
out vec3 vWorld;
void main() {
    vec4 w = uModel * vec4(aPos, 1.0);
    vWorld = w.xyz;
    gl_Position = uViewProj * w;
    vNrm = mat3(uModel) * aNrm;
    vUV = aUV + uUVOffset;
    if (uEnv == 1) {
        // Sphere-map: UV from the view-space normal (stored UVs on these materials are placeholders)
        vec3 en = normalize(mat3(uView) * mat3(uModel) * aNrm);
        vUV = vec2(en.x * 0.5 + 0.5, 0.5 - en.y * 0.5);
    }
    vCol = aCol;
}";

        public const string MeshFS = @"#version 330 core
in vec3 vNrm;
in vec2 vUV;
in vec4 vCol;
in vec3 vWorld;
uniform sampler2D uTex;
uniform int uUseTex;
uniform int uUseVCol;
uniform int uLighting;
uniform float uAlphaRef;
uniform float uBrightness;
uniform vec4 uTint;
uniform vec3 uLightDir;
uniform int uFog;
uniform vec3 uFogColor;
uniform vec2 uFogRange;
uniform float uFogMax;
uniform vec3 uEye;
out vec4 frag;
void main() {
    vec4 c = uTint;
    if (uUseTex == 1) c *= texture(uTex, vUV);
    if (uUseVCol == 1) c *= vec4(vCol.rgb * uBrightness, vCol.a);
    if (c.a < uAlphaRef) discard;
    if (uLighting == 1) {
        vec3 n = normalize(vNrm);
        if (!gl_FrontFacing) n = -n;
        float facing = dot(n, normalize(uLightDir)) * 0.5 + 0.5;
        c.rgb *= mix(0.6, 1.1, facing);
    }
    if (uFog == 1) {
        float dist = length(vWorld - uEye);
        float amount = clamp((dist - uFogRange.x) / max(uFogRange.y - uFogRange.x, 1.0), 0.0, 1.0);
        c.rgb = mix(c.rgb, uFogColor, amount * uFogMax);
    }
    frag = c;
}";

        public const string LineVS = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec4 aCol;
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec4 vCol;
void main() {
    gl_Position = uViewProj * (uModel * vec4(aPos, 1.0));
    vCol = aCol;
}";

        public const string LineFS = @"#version 330 core
in vec4 vCol;
uniform vec4 uTint;
out vec4 frag;
void main() { frag = vCol * uTint; }";
    }
}
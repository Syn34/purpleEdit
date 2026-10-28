using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Mk2.Formats;
using Mk2.Game;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering {
    public sealed class GpuPart {
        public GpuMesh Mesh;
        public Material Material;
        public int Texture;            // GL texture id, 0 = none
        public bool TextureHasAlpha;
        public bool IsCollisionComponent;
        public bool IsFragment;
        public string ComponentName;
        public int Triangles;
        public bool Transparent => Material.Blend != BlendMode.Opaque || VertexAlpha
                                   || (TextureHasAlpha && !Material.AlphaTest && !TextureIsCutout);
        /// <summary>Blended normally (not added or subtracted), so its visible pixels should hide what is behind them.</summary>
        public bool WritesDepthWhenBlended => Material.Blend == BlendMode.Opaque;
        /// <summary>Some vertices fade out (overlay edges blending into the ground below).</summary>
        public bool VertexAlpha;
        public bool TextureIsCutout;
        public SkinSource Skin;
    }

    /// <summary>Per-vertex bind pose and bone data for one part, and the interleaved buffer it is skinned into.</summary>
    public sealed class SkinSource {
        public Vector3[] Positions, Normals;
        public bool[] Skinned;
        public byte[] Bone0, Bone1;
        public float[] Weight0;
        public int[] FallbackBone;
        public float[] Data;
    }

    /// <summary>GPU copy of a model: one part per (component, material).</summary>
    public sealed class GpuModel : IDisposable {
        public LoadedModel Source;
        public List<GpuPart> Parts = new List<GpuPart>();
        public Vector3 Min, Max;           // local bounds of visible geometry
        public bool HasBounds;

        public bool Animatable;

        /// <summary>Uploads a model. Passing an animation keeps the skin data needed by <see cref="ApplySkin"/>.</summary>
        public static GpuModel Build(LoadedModel loadedModel, TextureCache textures, Animation animation = null) {
            bool animatable = animation != null;
            var gpuModel = new GpuModel { Source = loadedModel, Animatable = animatable };
            var model = loadedModel.Model;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var groups = model.Meshes.GroupBy(mesh => (mesh.ComponentIndex, mesh.MaterialIndex));
            foreach (var group in groups) {
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                foreach (var mesh in group) mesh.Triangulate(vertices, indices);
                if (indices.Count == 0) continue;
                var comp = model.Components[group.Key.ComponentIndex];
                var material = group.Key.MaterialIndex < loadedModel.Materials.Count ? loadedModel.Materials[group.Key.MaterialIndex]
                        : Material.Default(model.MaterialNames.ElementAtOrDefault(group.Key.MaterialIndex) ?? "?");
                var data = new float[vertices.Count * GpuMesh.Stride];
                for (int index = 0; index < vertices.Count; index++) {
                    var vertex = vertices[index];
                    int offset = index * GpuMesh.Stride;
                    data[offset] = vertex.Position.X; data[offset + 1] = vertex.Position.Y; data[offset + 2] = vertex.Position.Z;
                    var normal = vertex.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(vertex.Normal) : Vector3.UnitY;
                    data[offset + 3] = normal.X; data[offset + 4] = normal.Y; data[offset + 5] = normal.Z;
                    data[offset + 6] = vertex.UV.X; data[offset + 7] = vertex.UV.Y;
                    data[offset + 8] = vertex.Color.X; data[offset + 9] = vertex.Color.Y; data[offset + 10] = vertex.Color.Z; data[offset + 11] = vertex.Color.W;
                    if (!comp.IsCollision) {
                        min = Vector3.Min(min, vertex.Position);
                        max = Vector3.Max(max, vertex.Position);
                    }
                }
                SkinSource skin = null;
                if (animatable) skin = BuildSkin(model, comp, group, data, animation);
                var textureInfo = textures.Get(material.TextureName);
                var part = new GpuPart {
                    Mesh = GpuMesh.Create(data, indices.Select(index => (uint)index).ToArray(), dynamic: animatable),
                    Skin = skin,
                    Material = material,
                    Texture = textureInfo.Id,
                    TextureHasAlpha = textureInfo.HasAlpha,
                    TextureIsCutout = textureInfo.Cutout,
                    VertexAlpha = vertices.Any(vertex => vertex.Color.W < 0.95f),
                    IsCollisionComponent = comp.IsCollision,
                    IsFragment = comp.IsFragment,
                    ComponentName = comp.Name,
                    Triangles = indices.Count / 3,
                };
                gpuModel.Parts.Add(part);
            }
            if (min.X <= max.X) { gpuModel.Min = min; gpuModel.Max = max; gpuModel.HasBounds = true; }
            else { gpuModel.Min = model.BoundsMin; gpuModel.Max = model.BoundsMax; gpuModel.HasBounds = true; }
            return gpuModel;
        }

        private static SkinSource BuildSkin(Model model, ModelComponent component, IEnumerable<ModelMesh> meshes, float[] data, Animation animation) {
            int componentBone = Skinning.ComponentBone(model, component, animation);
            var positions = new List<Vector3>();
            var skinned = new List<bool>();
            var bone0 = new List<byte>();
            var bone1 = new List<byte>();
            var weight0 = new List<float>();
            var fallbackBone = new List<int>();
            foreach (var mesh in meshes) {
                var listedBones = model.NodeListBones(mesh);
                int meshFallback = componentBone >= 0 ? componentBone : listedBones.Count > 0 ? listedBones[0] : -1;
                foreach (var strip in mesh.Strips)
                    foreach (var vertex in strip.Vertices) {
                        positions.Add(vertex.Position);
                        skinned.Add(strip.Skinned);
                        bone0.Add(vertex.Bone0);
                        bone1.Add(vertex.Bone1);
                        weight0.Add(vertex.Weight0);
                        fallbackBone.Add(meshFallback);
                    }
            }
            var normals = new Vector3[positions.Count];
            for (int index = 0; index < normals.Length; index++) {
                int offset = index * GpuMesh.Stride;
                normals[index] = new Vector3(data[offset + 3], data[offset + 4], data[offset + 5]);
            }
            return new SkinSource {
                Positions = positions.ToArray(),
                Normals = normals,
                Skinned = skinned.ToArray(),
                Bone0 = bone0.ToArray(),
                Bone1 = bone1.ToArray(),
                Weight0 = weight0.ToArray(),
                FallbackBone = fallbackBone.ToArray(),
                Data = data,
            };
        }

        public void ApplySkin(Matrix4x4[] skinMatrices) {
            if (!Animatable || skinMatrices == null) return;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var part in Parts) {
                var skin = part.Skin;
                if (skin == null) continue;
                var data = skin.Data;
                for (int index = 0; index < skin.Positions.Length; index++) {
                    Skinning.PoseVertex(skin.Positions[index], skin.Normals[index], skin.Skinned[index], skin.Bone0[index], skin.Bone1[index],
                                        skin.Weight0[index], skin.FallbackBone[index], skinMatrices, out var position, out var normal);
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) {
                        position = skin.Positions[index];
                        normal = skin.Normals[index];
                    }
                    float length = normal.Length();
                    normal = length > 1e-6f ? normal / length : Vector3.UnitY;
                    int offset = index * GpuMesh.Stride;
                    data[offset] = position.X; data[offset + 1] = position.Y; data[offset + 2] = position.Z;
                    data[offset + 3] = normal.X; data[offset + 4] = normal.Y; data[offset + 5] = normal.Z;
                    if (!part.IsCollisionComponent) {
                        min = Vector3.Min(min, position);
                        max = Vector3.Max(max, position);
                    }
                }
                part.Mesh.UpdateVertices(data);
            }
            if (min.X <= max.X) { Min = min; Max = max; }
        }

        public void Dispose() {
            foreach (var part in Parts) part.Mesh.Dispose();
            Parts.Clear();
        }
    }

    public struct TexInfo {
        public int Id;
        public bool HasAlpha;
        public bool Cutout;
    }

    /// <summary>GL textures keyed by texture name, decoded through the scene's AssetCache.</summary>
    public sealed class TextureCache : IDisposable {
        private readonly Dictionary<string, TexInfo> textures = new Dictionary<string, TexInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly AssetCache assetCache;
        private TexInfo missing;

        public TextureCache(AssetCache assets) { assetCache = assets; }

        public int Count => textures.Count;

        public TexInfo Get(string name) {
            if (string.IsNullOrEmpty(name)) return Missing();
            if (textures.TryGetValue(name, out var textureInfo)) return textureInfo;
            var texture = assetCache.GetTexture(name);
            textureInfo = texture == null ? Missing() : Upload(texture);
            textures[name] = textureInfo;
            return textureInfo;
        }

        private TexInfo Missing() {
            if (missing.Id == 0) missing = Upload(Texture.Missing());
            return missing;
        }

        public static TexInfo Upload(Texture texture) {
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texture.Width, texture.Height, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, texture.Rgba);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            return new TexInfo { Id = id, HasAlpha = texture.HasAlpha, Cutout = texture.HasCutout };
        }

        public void Dispose() {
            foreach (var textureId in textures.Values.Select(textureInfo => textureInfo.Id).Distinct()) if (textureId != 0) GL.DeleteTexture(textureId);
            if (missing.Id != 0) GL.DeleteTexture(missing.Id);
            textures.Clear();
            missing = default;
        }
    }
}
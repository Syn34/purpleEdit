using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Mk2.Formats;
using Mk2.Game;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering {
    public enum ItemKind { Overlay, Object, EnvDome }

    public sealed class DrawItem {
        public GpuModel Model;
        public Matrix4x4 World;
        public LevelObject Object;
        public SceneChunk Chunk;
        public ItemKind Kind;
        public string Name;
        public Vector3 WorldMin, WorldMax;
        public Vector4 Tint = Vector4.One;
    }

    public sealed class CollisionItem {
        public GpuMesh Mesh;
        public Matrix4x4 World;
        public SceneChunk Chunk;
        public LevelObject Object;
        public string Name;
        public int Triangles;
        public Vector3 WorldMin, WorldMax;
    }

    public sealed class PickResult {
        public DrawItem Item;
        public LevelObject Object;
        public float Distance;
        public Vector3 Point;
        public string Description;
    }

    /// <summary>Owns all GPU resources for one loaded scene and draws it.</summary>
    public sealed partial class SceneRenderer : IDisposable {
        public LevelScene Scene { get; private set; }
        public readonly List<DrawItem> Items = new List<DrawItem>();
        public readonly List<CollisionItem> Collisions = new List<CollisionItem>();
        public Vector3 SceneMin, SceneMax;
        public int LastDrawnTriangles;
        public LevelObject Selected;
        public DrawItem SelectedItem;

        private readonly Dictionary<LoadedModel, GpuModel> models = new Dictionary<LoadedModel, GpuModel>();
        private readonly Dictionary<GpuModel, Vector3[]> pickTriangles = new Dictionary<GpuModel, Vector3[]>();
        private readonly Dictionary<(string chunk, ObjectCategory cat), GpuLines> shapeLines = new Dictionary<(string, ObjectCategory), GpuLines>();
        private readonly Dictionary<(string chunk, ObjectCategory cat), GpuLines> markerLines = new Dictionary<(string, ObjectCategory), GpuLines>();
        private GpuLines selectionLines;
        private GpuMesh selectionFill;
        private GpuLines gridLines;
        private TextureCache textures;
        private ShaderProgram meshShader, lineShader;
        private readonly DateTime startTime = DateTime.Now;

        public static readonly Dictionary<ObjectCategory, Vector4> CategoryColors = new Dictionary<ObjectCategory, Vector4>
        {
            { ObjectCategory.Prop, new Vector4(0.75f, 0.75f, 0.75f, 1) },
            { ObjectCategory.Trigger, new Vector4(1.0f, 0.55f, 0.1f, 1) },
            { ObjectCategory.Path, new Vector4(0.2f, 0.9f, 1.0f, 1) },
            { ObjectCategory.Volume, new Vector4(0.3f, 1.0f, 0.3f, 1) },
            { ObjectCategory.Spawn, new Vector4(1.0f, 0.25f, 0.25f, 1) },
            { ObjectCategory.Camera, new Vector4(1.0f, 0.3f, 1.0f, 1) },
            { ObjectCategory.Sound, new Vector4(0.35f, 0.5f, 1.0f, 1) },
            { ObjectCategory.Checkpoint, new Vector4(1.0f, 1.0f, 0.2f, 1) },
            { ObjectCategory.Logic, new Vector4(1.0f, 1.0f, 1.0f, 1) },
            { ObjectCategory.Critter, new Vector4(0.6f, 1.0f, 0.6f, 1) },
        };

        public void InitGl() {
            meshShader ??= new ShaderProgram(Shaders.MeshVS, Shaders.MeshFS);
            lineShader ??= new ShaderProgram(Shaders.LineVS, Shaders.LineFS);
            if (gridLines == null) {
                var gridData = new List<float>();
                var color = new Vector4(0.35f, 0.35f, 0.4f, 1);
                for (int line = -20; line <= 20; line++) {
                    Line(gridData, new Vector3(line * 1000, 0, -20000), new Vector3(line * 1000, 0, 20000), color);
                    Line(gridData, new Vector3(-20000, 0, line * 1000), new Vector3(20000, 0, line * 1000), color);
                }
                gridLines = GpuLines.Create(gridData);
            }
        }

        public void Load(LevelScene scene) {
            DisposeSceneResources();
            Scene = scene;
            textures = new TextureCache(scene.Assets);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var chunk in scene.Chunks) {
                foreach (var (overlay, loadedModel) in chunk.Overlays) {
                    if (loadedModel == null) continue;
                    AddItem(new DrawItem { Model = Gpu(loadedModel), World = Matrix4x4.Identity, Chunk = chunk, Kind = ItemKind.Overlay, Name = overlay.Name });
                }
                if (chunk.EnvDome != null)
                    AddItem(new DrawItem { Model = Gpu(chunk.EnvDome), World = Matrix4x4.Identity, Chunk = chunk, Kind = ItemKind.EnvDome, Name = chunk.EnvDome.Name });
                foreach (var levelObject in chunk.Objects) {
                    if (chunk.ObjectModels.TryGetValue(levelObject, out var loadedModel)) {
                        var world = levelObject.ModelMatrix;
                        AddItem(new DrawItem { Model = Gpu(loadedModel), World = world, Chunk = chunk, Kind = ItemKind.Object, Object = levelObject, Name = loadedModel.Name, Tint = ObjectTint(levelObject) });
                        foreach (var collision in loadedModel.Collisions)
                            AddCollision(collision, world, chunk, levelObject);
                    }
                }
                foreach (var (name, collision) in chunk.Grounds) AddCollision(collision, Matrix4x4.Identity, chunk, null, name);
                BuildLines(chunk);
            }
            foreach (var item in Items.Where(candidate => candidate.Kind == ItemKind.Overlay || candidate.Kind == ItemKind.Object)) {
                min = Vector3.Min(min, item.WorldMin);
                max = Vector3.Max(max, item.WorldMax);
            }
            if (min.X > max.X) {
                foreach (var collision in Collisions) { /* fall back to collision extents */ }
                min = new Vector3(-1000); max = new Vector3(1000);
            }
            SceneMin = min; SceneMax = max;
        }

        /// <summary>The layout's "color r g b" tint. Values above 2 are taken as PS2 style, where 128 is 1.0.</summary>
        private static Vector4 ObjectTint(LevelObject levelObject) {
            if (!levelObject.Color.HasValue) return Vector4.One;
            var color = levelObject.Color.Value;
            float largest = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
            if (largest <= 0) return Vector4.One;
            if (largest > 2) color /= 128f;
            color = Vector3.Clamp(color, Vector3.Zero, new Vector3(2));
            return new Vector4(color, 1);
        }

        private GpuModel Gpu(LoadedModel loadedModel) {
            if (models.TryGetValue(loadedModel, out var gpuModel)) return gpuModel;
            gpuModel = GpuModel.Build(loadedModel, textures);
            models[loadedModel] = gpuModel;
            return gpuModel;
        }

        private void AddItem(DrawItem item) {
            TransformBounds(item.Model.Min, item.Model.Max, item.World, out item.WorldMin, out item.WorldMax);
            Items.Add(item);
        }

        private static void TransformBounds(Vector3 min, Vector3 max, Matrix4x4 matrix, out Vector3 worldMin, out Vector3 worldMax) {
            worldMin = new Vector3(float.MaxValue); worldMax = new Vector3(float.MinValue);
            for (int corner = 0; corner < 8; corner++) {
                var localCorner = new Vector3((corner & 1) != 0 ? max.X : min.X, (corner & 2) != 0 ? max.Y : min.Y, (corner & 4) != 0 ? max.Z : min.Z);
                var worldCorner = Vector3.Transform(localCorner, matrix);
                worldMin = Vector3.Min(worldMin, worldCorner); worldMax = Vector3.Max(worldMax, worldCorner);
            }
        }

        private void AddCollision(Collision collision, Matrix4x4 world, SceneChunk chunk, LevelObject levelObject, string name = null) {
            if (collision == null || collision.Triangles.Count == 0) return;
            // Flat-shaded: unshared vertices so each face gets its own normal and material colour.
            var data = new float[collision.Triangles.Count * 3 * GpuMesh.Stride];
            var indices = new uint[collision.Triangles.Count * 3];
            int vertexIndex = 0;
            foreach (var triangle in collision.Triangles) {
                var first = collision.Vertices[triangle.First]; var second = collision.Vertices[triangle.Second]; var third = collision.Vertices[triangle.Third];
                var normal = Vector3.Cross(second - first, third - first);
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
                var color = MaterialColor(collision.MaterialName(triangle), triangle.Flags);
                foreach (var corner in new[] { first, second, third }) {
                    int offset = vertexIndex * GpuMesh.Stride;
                    data[offset] = corner.X; data[offset + 1] = corner.Y; data[offset + 2] = corner.Z;
                    data[offset + 3] = normal.X; data[offset + 4] = normal.Y; data[offset + 5] = normal.Z;
                    data[offset + 8] = color.X; data[offset + 9] = color.Y; data[offset + 10] = color.Z; data[offset + 11] = 1;
                    indices[vertexIndex] = (uint)vertexIndex;
                    vertexIndex++;
                }
            }
            var localMin = new Vector3(float.MaxValue); var localMax = new Vector3(float.MinValue);
            foreach (var vertex in collision.Vertices) { localMin = Vector3.Min(localMin, vertex); localMax = Vector3.Max(localMax, vertex); }
            TransformBounds(localMin, localMax, world, out var worldMin, out var worldMax);
            Collisions.Add(new CollisionItem {
                Mesh = GpuMesh.Create(data, indices),
                World = world,
                Chunk = chunk,
                Object = levelObject,
                Name = name ?? collision.Name,
                Triangles = collision.Triangles.Count,
                WorldMin = worldMin,
                WorldMax = worldMax,
            });
        }

        /// <summary>Stable pastel colour per collision material name.</summary>
        public static Vector3 MaterialColor(string name, ushort flags) {
            uint hash = 2166136261;
            foreach (char character in name ?? "") hash = (hash ^ char.ToLowerInvariant(character)) * 16777619;
            float red = 0.35f + ((hash >> 0) & 0xFF) / 255f * 0.6f;
            float green = 0.35f + ((hash >> 8) & 0xFF) / 255f * 0.6f;
            float blue = 0.35f + ((hash >> 16) & 0xFF) / 255f * 0.6f;
            return new Vector3(red, green, blue);
        }

        // ---------------------------------------------------------------- debug lines

        private static void Line(List<float> lineData, Vector3 start, Vector3 end, Vector4 color) {
            lineData.Add(start.X); lineData.Add(start.Y); lineData.Add(start.Z); lineData.Add(color.X); lineData.Add(color.Y); lineData.Add(color.Z); lineData.Add(color.W);
            lineData.Add(end.X); lineData.Add(end.Y); lineData.Add(end.Z); lineData.Add(color.X); lineData.Add(color.Y); lineData.Add(color.Z); lineData.Add(color.W);
        }

        public static void Box(List<float> lineData, Vector3 center, Vector3 angles, Vector3 size, Vector4 color) {
            var rotation = LevelObject.RotationPYR(angles);
            var half = size * 0.5f;
            var corners = new Vector3[8];
            for (int corner = 0; corner < 8; corner++) {
                var local = new Vector3((corner & 1) != 0 ? half.X : -half.X, (corner & 2) != 0 ? half.Y : -half.Y, (corner & 4) != 0 ? half.Z : -half.Z);
                corners[corner] = center + Vector3.TransformNormal(local, rotation);
            }
            int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            for (int edge = 0; edge < edges.Length; edge += 2) Line(lineData, corners[edges[edge]], corners[edges[edge + 1]], color);
        }

        public static void AabbLines(List<float> lineData, Vector3 min, Vector3 max, Vector4 color) =>
            Box(lineData, (min + max) * 0.5f, Vector3.Zero, max - min, color);

        public static void Sphere(List<float> lineData, Vector3 center, float radius, Vector4 color) {
            const int Segments = 32;
            for (int axis = 0; axis < 3; axis++)
                for (int segment = 0; segment < Segments; segment++) {
                    float startAngle = segment * MathF.PI * 2 / Segments, endAngle = (segment + 1) * MathF.PI * 2 / Segments;
                    Vector3 CirclePoint(float angle) => axis == 0 ? new Vector3(0, MathF.Cos(angle), MathF.Sin(angle)) * radius
                                       : axis == 1 ? new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * radius
                                       : new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * radius;
                    Line(lineData, center + CirclePoint(startAngle), center + CirclePoint(endAngle), color);
                }
        }

        public static void Cross(List<float> lineData, Vector3 point, float halfSize, Vector4 color) {
            Line(lineData, point - Vector3.UnitX * halfSize, point + Vector3.UnitX * halfSize, color);
            Line(lineData, point - Vector3.UnitY * halfSize, point + Vector3.UnitY * halfSize, color);
            Line(lineData, point - Vector3.UnitZ * halfSize, point + Vector3.UnitZ * halfSize, color);
        }

        public static void ShapeLines(List<float> lineData, LevelObject levelObject, Vector4 color) {
            foreach (var shape in levelObject.Shapes) {
                switch (shape.Kind) {
                    case ShapeKind.Box: Box(lineData, shape.Center, shape.Rotation, shape.Size, color); break;
                    case ShapeKind.Sphere: Sphere(lineData, shape.Center, shape.Radius, color); break;
                    case ShapeKind.Polyline:
                    case ShapeKind.Polygon: {
                            var dimColor = color * new Vector4(0.6f, 0.6f, 0.6f, 1);
                            for (int index = 0; index + 1 < shape.Points.Count; index++) Line(lineData, shape.Points[index], shape.Points[index + 1], color);
                            if (shape.Kind == ShapeKind.Polygon && shape.Points.Count > 2) Line(lineData, shape.Points[^1], shape.Points[0], color);
                            foreach (var point in shape.Points) Line(lineData, point, point + Vector3.UnitY * 150, dimColor);
                            if (shape.Points.Count > 0) Line(lineData, levelObject.Position, shape.Points[0], dimColor);
                            break;
                        }
                    case ShapeKind.CameraLine:
                        if (shape.Points.Count == 2) {
                            Line(lineData, shape.Points[0], shape.Points[1], color);
                            Box(lineData, shape.Points[0], Vector3.Zero, new Vector3(60), color);
                        }
                        break;
                }
            }
        }

        /// <summary>Re-create the debug line batches (after individual objects were hidden/shown).</summary>
        public void RebuildLines(ViewSettings settings) {
            foreach (var lines in shapeLines.Values) lines.Dispose();
            foreach (var lines in markerLines.Values) lines.Dispose();
            shapeLines.Clear();
            markerLines.Clear();
            if (Scene == null) return;
            foreach (var chunk in Scene.Chunks) BuildLines(chunk, settings);
        }

        private void BuildLines(SceneChunk chunk, ViewSettings settings = null) {
            foreach (var group in chunk.Objects.Where(candidate => settings == null || !settings.HiddenObjects.Contains(candidate)).GroupBy(candidate => candidate.Category)) {
                var color = CategoryColors[group.Key];
                var shapes = new List<float>();
                var markers = new List<float>();
                foreach (var levelObject in group) {
                    ShapeLines(shapes, levelObject, color);
                    if (!chunk.ObjectModels.ContainsKey(levelObject)) {
                        Cross(markers, levelObject.Position, 60, color);
                        Box(markers, levelObject.Position, levelObject.Rotation, new Vector3(40), color);
                        // forward tick to show facing
                        var forward = Vector3.TransformNormal(new Vector3(0, 0, 90), LevelObject.RotationPYR(levelObject.Rotation));
                        Line(markers, levelObject.Position, levelObject.Position + forward, color);
                    }
                }
                if (shapes.Count > 0) shapeLines[(chunk.Def.Name, group.Key)] = GpuLines.Create(shapes);
                if (markers.Count > 0) markerLines[(chunk.Def.Name, group.Key)] = GpuLines.Create(markers);
            }
        }

        public void SetSelection(LevelObject levelObject, DrawItem item = null) {
            Selected = levelObject;
            SelectedItem = item ?? (levelObject == null ? null : Items.FirstOrDefault(candidate => candidate.Object == levelObject));
            selectionLines?.Dispose();
            selectionLines = null;
            var lineData = new List<float>();
            var yellow = new Vector4(1, 1, 0, 1);
            if (SelectedItem != null) AabbLines(lineData, SelectedItem.WorldMin, SelectedItem.WorldMax, yellow);
            if (levelObject != null) {
                ShapeLines(lineData, levelObject, yellow);
                Cross(lineData, levelObject.Position, 120, yellow);
            }
            if (lineData.Count > 0) selectionLines = GpuLines.Create(lineData);

            selectionFill?.Dispose();
            selectionFill = null;
            if (levelObject != null && SelectedItem == null) {
                var vertexData = new List<float>(); var indices = new List<uint>();
                foreach (var shape in levelObject.Shapes) {
                    if (shape.Kind == ShapeKind.Box) FillBox(vertexData, indices, shape.Center, shape.Rotation, shape.Size);
                    else if (shape.Kind == ShapeKind.Sphere) FillSphere(vertexData, indices, shape.Center, shape.Radius);
                    else if (shape.Kind == ShapeKind.Polygon && shape.Points.Count >= 3) FillPolygon(vertexData, indices, shape.Points);
                }
                if (levelObject.Shapes.Count == 0 || !levelObject.Shapes.Any(candidate => candidate.Kind == ShapeKind.Box || candidate.Kind == ShapeKind.Sphere))
                    FillBox(vertexData, indices, levelObject.Position, levelObject.Rotation, new Vector3(90));
                if (indices.Count > 0) selectionFill = GpuMesh.Create(vertexData.ToArray(), indices.ToArray());
            }
        }

        private static void Vert(List<float> vertexData, Vector3 position, Vector3 normal) {
            vertexData.Add(position.X); vertexData.Add(position.Y); vertexData.Add(position.Z); vertexData.Add(normal.X); vertexData.Add(normal.Y); vertexData.Add(normal.Z);
            vertexData.Add(0); vertexData.Add(0); vertexData.Add(1); vertexData.Add(1); vertexData.Add(1); vertexData.Add(1);
        }

        private static void FillBox(List<float> vertexData, List<uint> indices, Vector3 center, Vector3 angles, Vector3 size) {
            var rotation = LevelObject.RotationPYR(angles);
            var half = size * 0.5f;
            uint firstVertex = (uint)(vertexData.Count / GpuMesh.Stride);
            for (int corner = 0; corner < 8; corner++) {
                var local = new Vector3((corner & 1) != 0 ? half.X : -half.X, (corner & 2) != 0 ? half.Y : -half.Y, (corner & 4) != 0 ? half.Z : -half.Z);
                Vert(vertexData, center + Vector3.TransformNormal(local, rotation), Vector3.UnitY);
            }
            int[] faces = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
            foreach (var corner in faces) indices.Add(firstVertex + (uint)corner);
        }

        private static void FillSphere(List<float> vertexData, List<uint> indices, Vector3 center, float radius) {
            const int Slices = 16, Rings = 10;
            uint firstVertex = (uint)(vertexData.Count / GpuMesh.Stride);
            for (int ring = 0; ring <= Rings; ring++)
                for (int slice = 0; slice <= Slices; slice++) {
                    float polar = ring * MathF.PI / Rings, azimuth = slice * 2 * MathF.PI / Slices;
                    var normal = new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(azimuth));
                    Vert(vertexData, center + normal * radius, normal);
                }
            for (int ring = 0; ring < Rings; ring++)
                for (int slice = 0; slice < Slices; slice++) {
                    uint current = firstVertex + (uint)(ring * (Slices + 1) + slice), below = current + Slices + 1;
                    indices.Add(current); indices.Add(below); indices.Add(current + 1); indices.Add(current + 1); indices.Add(below); indices.Add(below + 1);
                }
        }

        private static void FillPolygon(List<float> vertexData, List<uint> indices, List<Vector3> points) {
            uint firstVertex = (uint)(vertexData.Count / GpuMesh.Stride);
            foreach (var point in points) Vert(vertexData, point, Vector3.UnitY);
            for (int index = 1; index + 1 < points.Count; index++) { indices.Add(firstVertex); indices.Add(firstVertex + (uint)index); indices.Add(firstVertex + (uint)index + 1); }
        }

        public bool Visible(DrawItem item, ViewSettings settings) {
            if (Anim != null && Anim.Item == item && !settings.AnimatedMesh) return false;
            string chunk = item.Chunk.Def.Name;
            switch (item.Kind) {
                case ItemKind.Overlay: return settings.OverlayVisible(chunk, item.Name);
                case ItemKind.EnvDome: return settings.DomeVisible(chunk);
                case ItemKind.Object: return settings.ObjectVisible(item.Object);
            }
            return !settings.HiddenChunks.Contains(chunk);
        }

        // ---- frustum culling (planes extracted from the row-vector view-projection matrix)
        private readonly Vector4[] planes = new Vector4[6];
        private readonly List<DrawItem> visible = new List<DrawItem>();
        private readonly List<DrawItem> blended = new List<DrawItem>();

        private static float DistanceSquared(Vector3 eye, DrawItem item) => Vector3.DistanceSquared(eye, (item.WorldMin + item.WorldMax) * 0.5f);

        private void ExtractFrustum(Matrix4x4 viewProjection) {
            var column1 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
            var column2 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
            var column3 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
            var column4 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
            planes[0] = column4 + column1; planes[1] = column4 - column1;
            planes[2] = column4 + column2; planes[3] = column4 - column2;
            planes[4] = column4 + column3; planes[5] = column4 - column3;
        }

        private bool InFrustum(Vector3 min, Vector3 max) {
            for (int index = 0; index < 6; index++) {
                var plane = planes[index];
                // farthest corner along the plane normal
                float farX = plane.X >= 0 ? max.X : min.X, farY = plane.Y >= 0 ? max.Y : min.Y, farZ = plane.Z >= 0 ? max.Z : min.Z;
                if (plane.X * farX + plane.Y * farY + plane.Z * farZ + plane.W < 0) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>Set non-null to capture which labelled stage of Render() first raised a GL error (debugging the model-viewer FBO path).</summary>
        public Action<string> DebugStageError;

        private void CheckGl(string stage) {
            if (DebugStageError == null) return;
            var error = GL.GetError();
            if (error != ErrorCode.NoError) DebugStageError(stage + ": " + error);
        }

        public void Render(Camera camera, int width, int height, ViewSettings settings, Vector3 clearColor, Vector3 fogColor, Vector2 fogRange) {
            if (DebugStageError != null) { while (GL.GetError() != ErrorCode.NoError) { } } // drain any stale/pre-existing error so later checks aren't misattributed
            InitGl();
            CheckGl("InitGl");
            GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1));
            GL.ColorMask(true, true, true, true);
            GL.ClearColor(clearColor.X, clearColor.Y, clearColor.Z, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            // The frame is shown through ImGui with alpha blending, so blended and subtracted geometry must not
            // change the frame's alpha or those pixels turn see-through (black squares, thin holes at faded edges).
            GL.ColorMask(true, true, true, false);
            LastDrawnTriangles = 0;

            var mirror = settings.MirrorX ? Matrix4x4.CreateScale(-1, 1, 1) : Matrix4x4.Identity;
            var view = camera.View;
            var proj = camera.Projection(width / (float)Math.Max(height, 1));
            var viewProj = mirror * view * proj;
            float time = (float)(DateTime.Now - startTime).TotalSeconds;

            GL.FrontFace(settings.MirrorX ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Disable(EnableCap.StencilTest);
            GL.PolygonMode(TriangleFace.FrontAndBack, settings.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
            CheckGl("State setup");

            meshShader.Use();
            meshShader.Set("uViewProj", viewProj);
            meshShader.Set("uView", mirror * view);
            meshShader.Set("uEnv", false);
            meshShader.Set("uTex", 0);
            meshShader.Set("uLightDir", Vector3.Normalize(new Vector3(0.4f, 1f, 0.3f)));
            meshShader.Set("uFog", settings.Fog);
            meshShader.Set("uFogColor", fogColor);
            meshShader.Set("uFogRange", fogRange);
            meshShader.Set("uFogMax", 0.7f);
            var eye = settings.MirrorX ? camera.Position * new Vector3(-1, 1, 1) : camera.Position;
            meshShader.Set("uEye", eye);
            meshShader.Set("uBrightness", settings.Brightness);
            GL.ActiveTexture(TextureUnit.Texture0);

            // 1. Environment dome, centred on the camera, behind everything.
            if (settings.EnvDome && Scene != null) {
                GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(false);
                GL.Disable(EnableCap.CullFace);
                foreach (var item in Items) {
                    if (item.Kind != ItemKind.EnvDome || !Visible(item, settings)) continue;
                    var center = (item.Model.Min + item.Model.Max) * 0.5f;
                    var domeWorld = Matrix4x4.CreateTranslation(eye - new Vector3(center.X, 0, center.Z));
                    DrawModel(item, domeWorld, settings, time, pass: 0, forceNoFog: true);
                    GL.Enable(EnableCap.Blend);
                    DrawModel(item, domeWorld, settings, time, pass: 1, forceNoFog: true);
                    DrawModel(item, domeWorld, settings, time, pass: 2, forceNoFog: true);
                    GL.Disable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
                GL.Enable(EnableCap.DepthTest);
                GL.DepthMask(true);
            }
            CheckGl("EnvDome pass");

            if (settings.CullBackfaces) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); }
            else GL.Disable(EnableCap.CullFace);

            // 2. Opaque + alpha-tested geometry (visibility + frustum culled once per frame).
            ExtractFrustum(viewProj);
            visible.Clear();
            foreach (var item in Items)
                if (item.Kind != ItemKind.EnvDome && Visible(item, settings) && InFrustum(item.WorldMin, item.WorldMax))
                    visible.Add(item);
            GL.Disable(EnableCap.Blend);
            int opaqueIndex = 0;
            foreach (var item in visible) {
                DrawModel(item, item.World, settings, time, pass: 0);
                if (DebugStageError != null) CheckGl($"Opaque pass, item {opaqueIndex} ({item.Name})");
                opaqueIndex++;
            }

            // 3. Collision.
            if (settings.GroundCollision || settings.ObjectCollision) {
                GL.Enable(EnableCap.PolygonOffsetFill);
                GL.PolygonOffset(-1f, -2f);
                meshShader.Set("uUseTex", 0);
                meshShader.Set("uUseVCol", 1);
                meshShader.Set("uLighting", 1);
                meshShader.Set("uAlphaRef", 0f);
                meshShader.Set("uUVOffset", Vector2.Zero);
                meshShader.Set("uTint", Vector4.One);
                meshShader.Set("uBrightness", 1f);
                foreach (var collision in Collisions) {
                    bool shown = collision.Object == null
                        ? settings.GroundVisible(collision.Chunk.Def.Name, collision.Name)
                        : settings.ObjectCollisionVisible(collision.Object);
                    if (!shown || !InFrustum(collision.WorldMin, collision.WorldMax)) continue;
                    meshShader.Set("uModel", collision.World);
                    collision.Mesh.Draw();
                    LastDrawnTriangles += collision.Triangles;
                }
                meshShader.Set("uBrightness", settings.Brightness);
                GL.Disable(EnableCap.PolygonOffsetFill);
            }
            CheckGl("Collision pass");

            // 4. Blended geometry, farthest first. Normally blended surfaces write depth so nothing behind them shows
            //    through; added and subtracted ones don't.
            if (settings.Transparency) {
                blended.Clear();
                blended.AddRange(visible);
                blended.Sort((first, second) => DistanceSquared(eye, second).CompareTo(DistanceSquared(eye, first)));
                GL.Enable(EnableCap.Blend);
                foreach (var item in blended) DrawModel(item, item.World, settings, time, pass: 1);
                GL.DepthMask(false);
                foreach (var item in blended) DrawModel(item, item.World, settings, time, pass: 2);
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }
            CheckGl("Transparent pass");

            // 4b. Selection highlight on the selected model itself.
            if (SelectedItem != null && Visible(SelectedItem, settings))
                DrawSelectionHighlight(SelectedItem, settings);
            CheckGl("Selection highlight");

            // 5. Lines.
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            lineShader.Use();
            lineShader.Set("uViewProj", viewProj);
            lineShader.Set("uModel", Matrix4x4.Identity);
            lineShader.Set("uTint", Vector4.One);
            if (Scene == null || Items.Count == 0) gridLines?.Draw();
            foreach (var entry in shapeLines)
                if (settings.CategoryBatchVisible(entry.Key.chunk, entry.Key.cat) && HelpersVisible(entry.Key.cat, settings)) entry.Value.Draw();
            foreach (var entry in markerLines)
                if (settings.CategoryBatchVisible(entry.Key.chunk, entry.Key.cat) && HelpersVisible(entry.Key.cat, settings)) entry.Value.Draw();
            if (selectionFill != null) {
                float pulse = 0.5f + 0.5f * MathF.Sin((float)(DateTime.Now - startTime).TotalSeconds * 4f);
                meshShader.Use();
                meshShader.Set("uModel", Matrix4x4.Identity);
                meshShader.Set("uUseTex", false); meshShader.Set("uUseVCol", false); meshShader.Set("uLighting", false);
                meshShader.Set("uAlphaRef", 0f); meshShader.Set("uFog", false); meshShader.Set("uUVOffset", Vector2.Zero);
                meshShader.Set("uTint", new Vector4(1f, 0.85f, 0.1f, 0.12f + 0.12f * pulse));
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.DepthMask(false);
                GL.Disable(EnableCap.CullFace);
                selectionFill.Draw();
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
                meshShader.Set("uFog", settings.Fog);
                lineShader.Use();
            }
            CheckGl("Shapes/markers/selectionFill");
            DrawSkeleton(settings);
            CheckGl("Skeleton");
            if (selectionLines != null) {
                float pulse = 0.6f + 0.4f * MathF.Sin((float)(DateTime.Now - startTime).TotalSeconds * 4f);
                lineShader.Set("uTint", new Vector4(1, 1, pulse, 1));
                selectionLines.Draw();
            }
            CheckGl("Selection lines");
            GL.ColorMask(true, true, true, true);
            GL.BindVertexArray(0);
        }

        private void DrawSelectionHighlight(DrawItem item, ViewSettings settings) {
            float pulse = 0.5f + 0.5f * MathF.Sin((float)(DateTime.Now - startTime).TotalSeconds * 4f);
            meshShader.Use();
            meshShader.Set("uModel", item.World);
            meshShader.Set("uUseTex", false);
            meshShader.Set("uUseVCol", false);
            meshShader.Set("uLighting", false);
            meshShader.Set("uAlphaRef", 0f);
            meshShader.Set("uFog", false);
            meshShader.Set("uUVOffset", Vector2.Zero);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);

            void Parts() {
                foreach (var part in item.Model.Parts) {
                    if (part.IsCollisionComponent && !settings.ModelCollisionComponents) continue;
                    if (part.IsFragment && !settings.Fragments) continue;
                    part.Mesh.Draw();
                }
            }

            // a) translucent yellow fill where visible
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(-1f, -4f);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            meshShader.Set("uTint", new Vector4(1f, 0.85f, 0.1f, 0.25f + 0.15f * pulse));
            Parts();
            GL.Disable(EnableCap.PolygonOffsetFill);

            // b) faint x-ray wireframe so it can be found behind walls
            GL.Disable(EnableCap.DepthTest);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            meshShader.Set("uTint", new Vector4(1f, 0.9f, 0.2f, 0.18f));
            Parts();

            // c) bright wireframe where visible
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.PolygonOffsetLine);
            GL.PolygonOffset(-1f, -8f);
            meshShader.Set("uTint", new Vector4(1f, 1f, 0.25f, 0.9f));
            Parts();
            GL.Disable(EnableCap.PolygonOffsetLine);

            GL.PolygonMode(TriangleFace.FrontAndBack, settings.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            meshShader.Set("uFog", settings.Fog);
            if (settings.CullBackfaces) GL.Enable(EnableCap.CullFace);
        }

        /// <summary>pass 0 = opaque and alpha-tested parts, 1 = normally blended parts, 2 = added and subtracted parts.</summary>
        private void DrawModel(DrawItem item, Matrix4x4 world, ViewSettings settings, float time, int pass, bool forceNoFog = false) {
            var gpuModel = item.Model;
            bool modelSet = false;
            foreach (var part in gpuModel.Parts) {
                if (part.IsCollisionComponent && !settings.ModelCollisionComponents) continue;
                if (part.IsFragment && !settings.Fragments) continue;
                bool transparent = settings.Transparency && part.Transparent;
                int partPass = !transparent ? 0 : part.WritesDepthWhenBlended ? 1 : 2;
                if (partPass != pass) continue;
                if (!modelSet) {
                    meshShader.Set("uModel", world);
                    modelSet = true;
                }
                var material = part.Material;
                bool useTex = settings.Textures && part.Texture != 0;
                meshShader.Set("uUseTex", useTex);
                meshShader.Set("uUseVCol", settings.VertexColors);
                // R_Elec crystal textures (e.g. R_Elec_0004.tex) already have the facet highlight
                // baked into the pixel data -- confirmed by decoding the texture directly. Adding
                // the normal-based mix(0.6,1.1,facing) term on top double-applies shading and washes
                // out the painted highlight instead of sharpening it, which is the opposite of the
                // reference look. Left off for regular geometry pending a real per-material signal
                // for which materials want it (most likely none of the texture-authored ones do).
                meshShader.Set("uLighting", part.IsCollisionComponent);
                // Materials with flag bit 0x4 (the R_Elec crystals) have placeholder stored UVs; the game sphere-maps them from the normal.
                meshShader.Set("uEnv", settings.EnvMapGlow && material.EnvMapped && !part.IsCollisionComponent);
                float alphaReference = 0f;
                if (useTex && (material.AlphaTest || part.TextureIsCutout || (part.TextureHasAlpha && !transparent)))
                    alphaReference = material.AlphaRef > 0 ? material.AlphaRef : 0.5f;
                if (pass == 1) alphaReference = 0.05f;
                if (pass == 2) alphaReference = 0.01f;
                meshShader.Set("uAlphaRef", alphaReference);
                var tint = part.IsCollisionComponent ? new Vector4(0.4f, 1f, 0.4f, 1f) : settings.VertexColors ? item.Tint : Vector4.One;
                meshShader.Set("uTint", tint);
                var uvOffset = material.Scrolls ? new Vector2(Frac(material.ScrollU * time), Frac(material.ScrollV * time)) : Vector2.Zero;
                meshShader.Set("uUVOffset", uvOffset);
                if (forceNoFog) meshShader.Set("uFog", false);
                if (pass != 0) {
                    if (material.Blend == BlendMode.Additive) GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    else if (material.Blend == BlendMode.Subtractive) {
                        GL.BlendEquation(BlendEquationMode.FuncReverseSubtract);
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    }
                    else GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
                GL.BindTexture(TextureTarget.Texture2D, useTex ? part.Texture : 0);
                if (DebugStageError != null) CheckGl($"  BindTexture (tex={part.Texture}, useTex={useTex})");
                part.Mesh.Draw();
                if (DebugStageError != null) CheckGl($"  Mesh.Draw (vao={part.Mesh.Vao}, idx={part.Mesh.IndexCount})");
                LastDrawnTriangles += part.Triangles;
                if (pass != 0 && material.Blend == BlendMode.Subtractive) GL.BlendEquation(BlendEquationMode.FuncAdd);
                if (forceNoFog) meshShader.Set("uFog", settings.Fog);
            }
        }

        private static float Frac(float value) => value - MathF.Floor(value);

        // ---------------------------------------------------------------- picking

        private Vector3[] PickTriangles(GpuModel gpuModel) {
            if (pickTriangles.TryGetValue(gpuModel, out var triangles)) return triangles;
            var list = new List<Vector3>();
            var model = gpuModel.Source.Model;
            foreach (var mesh in model.Meshes) {
                var comp = model.Components[mesh.ComponentIndex];
                if (comp.IsCollision || comp.IsFragment) continue;
                foreach (var strip in mesh.Strips)
                    for (int index = 0; index + 2 < strip.Vertices.Count; index++) {
                        list.Add(strip.Vertices[index].Position);
                        list.Add(strip.Vertices[index + 1].Position);
                        list.Add(strip.Vertices[index + 2].Position);
                    }
            }
            triangles = list.ToArray();
            pickTriangles[gpuModel] = triangles;
            return triangles;
        }

        public PickResult Pick(Vector3 origin, Vector3 direction, ViewSettings settings) {
            if (settings.MirrorX) {
                origin *= new Vector3(-1, 1, 1);
                direction *= new Vector3(-1, 1, 1);
            }
            PickResult best = null;
            void Consider(float distance, DrawItem item, LevelObject levelObject, string desc) {
                if (distance < 0 || (best != null && distance >= best.Distance)) return;
                best = new PickResult { Distance = distance, Item = item, Object = levelObject, Point = origin + direction * distance, Description = desc };
            }

            foreach (var item in Items) {
                if (item.Kind == ItemKind.EnvDome || !Visible(item, settings)) continue;
                if (!RayAabb(origin, direction, item.WorldMin, item.WorldMax, out float boxDistance) || (best != null && boxDistance > best.Distance)) continue;
                if (!Matrix4x4.Invert(item.World, out var inverse)) continue;
                var localOrigin = Vector3.Transform(origin, inverse);
                var localDirection = Vector3.TransformNormal(direction, inverse);
                var triangles = PickTriangles(item.Model);
                float bestLocal = float.MaxValue;
                for (int index = 0; index < triangles.Length; index += 3)
                    if (RayTri(localOrigin, localDirection, triangles[index], triangles[index + 1], triangles[index + 2], out float localDistance) && localDistance < bestLocal) bestLocal = localDistance;
                if (bestLocal < float.MaxValue) {
                    var hit = Vector3.Transform(localOrigin + localDirection * bestLocal, item.World);
                    Consider(Vector3.Distance(origin, hit), item, item.Object, item.Kind == ItemKind.Overlay ? "overlay " + item.Name : item.Name);
                }
            }

            if (Scene != null) {
                foreach (var chunk in Scene.Chunks) {
                    if (settings.HiddenChunks.Contains(chunk.Def.Name)) continue;
                    foreach (var levelObject in chunk.Objects) {
                        if (!settings.ObjectVisible(levelObject) || !HelpersVisible(levelObject.Category, settings)) continue;
                        if (!chunk.ObjectModels.ContainsKey(levelObject) && RaySphere(origin, direction, levelObject.Position, 60, out float markerDistance))
                            Consider(Math.Max(0f, markerDistance - HelperPickBias), null, levelObject, levelObject.DisplayName);
                        var segments = PickSegments(levelObject);
                        for (int index = 0; index + 1 < segments.Length; index += 2) {
                            if (RaySegmentHit(origin, direction, segments[index], segments[index + 1], out float lineDistance))
                                Consider(Math.Max(0f, lineDistance - HelperPickBias), null, levelObject, levelObject.DisplayName);
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>Lines and markers win over a surface they sit on, as long as they are within this distance of it.</summary>
        private const float HelperPickBias = 100f;

        private readonly Dictionary<LevelObject, Vector3[]> pickSegments = new Dictionary<LevelObject, Vector3[]>();

        /// <summary>Endpoint pairs of the lines drawn for an object's shapes.</summary>
        private Vector3[] PickSegments(LevelObject levelObject) {
            if (pickSegments.TryGetValue(levelObject, out var segments)) return segments;
            var lineData = new List<float>();
            ShapeLines(lineData, levelObject, Vector4.One);
            segments = new Vector3[lineData.Count / 7];
            for (int index = 0; index < segments.Length; index++)
                segments[index] = new Vector3(lineData[index * 7], lineData[index * 7 + 1], lineData[index * 7 + 2]);
            pickSegments[levelObject] = segments;
            return segments;
        }

        /// <summary>True when the ray passes within a few pixels' worth of a line segment; distance is along the ray.</summary>
        private static bool RaySegmentHit(Vector3 origin, Vector3 direction, Vector3 start, Vector3 end, out float distance) {
            distance = 0;
            var segment = end - start;
            var offset = origin - start;
            float segmentLengthSquared = Vector3.Dot(segment, segment);
            float alignment = Vector3.Dot(direction, segment);
            float rayOffset = Vector3.Dot(direction, offset);
            float segmentOffset = Vector3.Dot(segment, offset);
            float denominator = segmentLengthSquared - alignment * alignment;
            float along = denominator > 1e-6f ? Math.Clamp((segmentOffset - alignment * rayOffset) / denominator, 0f, 1f) : 0f;
            var closestOnSegment = start + segment * along;
            distance = Vector3.Dot(closestOnSegment - origin, direction);
            if (distance <= 0) return false;
            var closestOnRay = origin + direction * distance;
            float tolerance = Math.Max(6f, distance * 0.008f);
            return Vector3.DistanceSquared(closestOnRay, closestOnSegment) <= tolerance * tolerance;
        }

        /// <summary>Prop markers and shapes are optional; every other category always shows its helpers.</summary>
        private static bool HelpersVisible(ObjectCategory category, ViewSettings settings) => category != ObjectCategory.Prop || settings.PropMarkers;

        private static bool RayAabb(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance) {
            float tmin = 0, tmax = float.MaxValue;
            distance = 0;
            for (int axis = 0; axis < 3; axis++) {
                float originAxis = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float directionAxis = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
                float minAxis = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                float maxAxis = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (MathF.Abs(directionAxis) < 1e-9f) {
                    if (originAxis < minAxis || originAxis > maxAxis) return false;
                }
                else {
                    float enter = (minAxis - originAxis) / directionAxis, exit = (maxAxis - originAxis) / directionAxis;
                    if (enter > exit) (enter, exit) = (exit, enter);
                    tmin = Math.Max(tmin, enter);
                    tmax = Math.Min(tmax, exit);
                    if (tmin > tmax) return false;
                }
            }
            distance = tmin;
            return true;
        }

        private static bool RayTri(Vector3 origin, Vector3 direction, Vector3 first, Vector3 second, Vector3 third, out float distance) {
            distance = 0;
            var edge1 = second - first; var edge2 = third - first;
            var perpendicular = Vector3.Cross(direction, edge2);
            float determinant = Vector3.Dot(edge1, perpendicular);
            if (MathF.Abs(determinant) < 1e-9f) return false;
            float inverseDeterminant = 1f / determinant;
            var fromFirst = origin - first;
            float barycentricU = Vector3.Dot(fromFirst, perpendicular) * inverseDeterminant;
            if (barycentricU < 0 || barycentricU > 1) return false;
            var crossEdge = Vector3.Cross(fromFirst, edge1);
            float barycentricV = Vector3.Dot(direction, crossEdge) * inverseDeterminant;
            if (barycentricV < 0 || barycentricU + barycentricV > 1) return false;
            distance = Vector3.Dot(edge2, crossEdge) * inverseDeterminant;
            return distance > 0;
        }

        private static bool RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radius, out float distance) {
            distance = 0;
            var fromCenter = origin - center;
            float projection = Vector3.Dot(fromCenter, direction);
            float offsetSquared = fromCenter.LengthSquared() - radius * radius;
            float discriminant = projection * projection - offsetSquared;
            if (discriminant < 0) return false;
            float root = MathF.Sqrt(discriminant);
            distance = -projection - root;
            if (distance < 0) distance = -projection + root;
            return distance >= 0;
        }

        // ---------------------------------------------------------------- screenshots

        public static byte[] ReadPixels(int width, int height) {
            var pixels = new byte[width * height * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            // flip rows (GL origin is bottom-left)
            var row = new byte[width * 4];
            for (int topRow = 0; topRow < height / 2; topRow++) {
                System.Buffer.BlockCopy(pixels, topRow * width * 4, row, 0, width * 4);
                System.Buffer.BlockCopy(pixels, (height - 1 - topRow) * width * 4, pixels, topRow * width * 4, width * 4);
                System.Buffer.BlockCopy(row, 0, pixels, (height - 1 - topRow) * width * 4, width * 4);
            }
            for (int alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
            return pixels;
        }
        private void DisposeSceneResources() {
            ClearAnimation();
            foreach (var gpuModel in models.Values) gpuModel.Dispose();
            models.Clear();
            pickTriangles.Clear();
            pickSegments.Clear();
            foreach (var collision in Collisions) collision.Mesh.Dispose();
            Collisions.Clear();
            foreach (var lines in shapeLines.Values) lines.Dispose();
            foreach (var lines in markerLines.Values) lines.Dispose();
            shapeLines.Clear();
            markerLines.Clear();
            selectionLines?.Dispose();
            selectionLines = null;
            selectionFill?.Dispose();
            selectionFill = null;
            textures?.Dispose();
            textures = null;
            Items.Clear();
            Selected = null;
            SelectedItem = null;
            Scene = null;
        }

        public void Dispose() {
            DisposeSceneResources();
            skeletonLines?.Dispose();
            skeletonLines = null;
            gridLines?.Dispose();
            meshShader?.Dispose();
            lineShader?.Dispose();
        }
    }
}
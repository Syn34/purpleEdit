using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Mk2.Export;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer {
    public sealed class Window : GameWindow {
        private readonly string[] arguments;

        public GameData Game { get; private set; }
        public ModelDb ModelDb { get; private set; }
        public List<LevelDef> Levels { get; private set; } = new List<LevelDef>();
        public List<string> Models { get; private set; } = new List<string>();
        public LevelScene Scene { get; private set; }
        public AssetCache Assets { get; private set; }

        public readonly SceneRenderer Renderer = new SceneRenderer();
        public readonly Camera Camera = new Camera();
        public readonly ViewSettings ViewSettings = new ViewSettings();
        public Vec3 ClearColor = new Vec3(0.18f, 0.2f, 0.26f);
        public Vec3 FogColor = new Vec3(0.5f, 0.5f, 0.6f);
        public Vec2 FogRange = new Vec2(10000, 100000);

        public PropertiesView Properties { get; private set; } = PropertiesView.Empty;
        public string Status { get; private set; } = "";
        public string LogText { get { lock (log) return log.ToString(); } }
        public bool Busy { get; private set; }

        private readonly StringBuilder log = new StringBuilder();
        private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private MainFrame ui;
        private bool glReady;
        private string pendingScreenshot;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "purpleEdit", "settings.txt");

        public Window(string[] arguments) : base(GameWindowSettings.Default,
            new NativeWindowSettings {
                ClientSize = new Vector2i(1600, 950),
                APIVersion = new Version(3, 3),
                Flags = ContextFlags.ForwardCompatible,
                Profile = ContextProfile.Core,
                Vsync = VSyncMode.On,
                Title = "purpleEdit",
            }) {
            this.arguments = arguments;
        }

        protected override void OnLoad() {
            base.OnLoad();
            Renderer.InitGl();
            glReady = true;
            ui = new MainFrame(this);

            string source = arguments.Length > 0 ? arguments[0] : LoadSetting("root");
            if (!string.IsNullOrEmpty(source) && (Directory.Exists(source) || File.Exists(source)))
                _ = OpenGameSource(source);
        }

        protected override void OnResize(ResizeEventArgs eventArgs) {
            base.OnResize(eventArgs);
            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        }

        protected override void OnRenderFrame(FrameEventArgs eventArgs) {
            base.OnRenderFrame(eventArgs);
            if (!glReady) return;
            while (mainThreadQueue.TryDequeue(out var action)) {
                try { action(); }
                catch (Exception exception) { Log("Main-thread task failed: " + exception); }
            }
            GL.ClearColor(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            ui?.RenderUi();
            SwapBuffers();
        }

        protected override void OnUnload() {
            ui?.Dispose();
            Renderer.Dispose();
            base.OnUnload();
        }

        public void RunOnMainThread(Action action) => mainThreadQueue.Enqueue(action);

        // ---------------------------------------------------------------- loading

        /// <summary>Indexes a game folder or a single .RKV/.BPK archive.</summary>
        public async Task OpenGameSource(string source) {
            SetStatus("Indexing " + source);
            try {
                var game = await Task.Run(() => new GameData(source, SetStatus));
                Game = game;

                foreach (var line in game.Log) 
                    Log(line);

                ModelDb = new ModelDb(game.ReadBni("global.model.bni"));

                if (!ModelDb.Has("default")) 
                    Log("global.model.bni not found");

                Levels = LevelLoader.LoadLevelList(game);

                if (Levels.Count == 0) 
                    Log("levels.ini.bni not found");

                Assets = new AssetCache(game);
                SaveSetting("root", source);

                Models = game.AllNames(".mdl").Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

                SetStatus($"{source}: {Levels.Count} levels, {game.ChunkPackNames.Count()} chunk packs, {Models.Count} models");
            }
            catch (Exception exception) {
                Log("Failed to open " + source + ": " + exception);
                SetStatus("Failed to open " + source);
            }
        }

        public async Task ExtractArchive(string archivePath, string outputFolder) {
            Busy = true;
            SetStatus("Extracting " + Path.GetFileName(archivePath));
            try {
                int count = await Task.Run(() => {
                    var archive = RkvArchive.Open(archivePath, loadIntoMemory: false);
                    archive.ExtractAll(outputFolder);
                    return archive.Entries.Count;
                });
                SetStatus($"Extracted {count} files to {outputFolder}");
            }
            catch (Exception exception) {
                Log("Extract failed: " + exception);
                SetStatus("Extract failed");
            }
            finally { Busy = false; }
        }

        public async Task LoadLevel(LevelDef level) {
            if (Game == null) return;
            SetStatus("Loading " + level.Name);
            var stopwatch = Stopwatch.StartNew();
            try {
                var cache = new AssetCache(Game);
                Assets = cache;
                var scene = await Task.Run(() => LevelLoader.Load(Game, level, ModelDb, cache, SetStatus));
                RunOnMainThread(() => {
                    ShowScene(scene, stopwatch);
                    ApplyFog(level);
                    PlaceCameraAtStart(scene);
                });
            }
            catch (Exception exception) { Log("Load failed: " + exception); }
        }

        public async Task LoadPack(string chunkName) {
            if (Game == null) return;
            var stopwatch = Stopwatch.StartNew();
            try {
                var cache = new AssetCache(Game);
                Assets = cache;
                var scene = await Task.Run(() => {
                    var packScene = LevelLoader.LoadPack(Game, chunkName, cache);
                    var layout = Game.ReadBni(chunkName + ".lc3.bni", new[] { Game.ChunkPack(chunkName) });
                    var chunk = packScene.Chunks[0];
                    if (layout != null) {
                        chunk.Layout = layout;
                        int index = 0;
                        foreach (var section in layout.Sections) chunk.Objects.Add(LevelObject.FromSection(section, chunkName, index++, ModelDb));
                    }
                    ArrangeLooseModels(chunk);
                    return packScene;
                });
                RunOnMainThread(() => {
                    ShowScene(scene, stopwatch);
                    FrameWorld(Renderer.SceneMin, Renderer.SceneMax);
                });
            }
            catch (Exception exception) { Log("Pack load failed: " + exception); }
        }

        /// <summary>Chunk packs mix world-space level geometry with props modelled around the origin; lay the props out on a grid.</summary>
        private static void ArrangeLooseModels(SceneChunk chunk) {
            var placed = new List<(OverlayRef overlay, LoadedModel model)>();
            var loose = new List<LoadedModel>();
            foreach (var (overlay, model) in chunk.Overlays) {
                var center = model.Model.BoundsMin + model.Model.BoundsSize * 0.5f;
                float radius = Math.Max(model.Model.BoundsSize.Length() * 0.5f, 1f);
                if (center.Length() > 3 * radius && center.Length() > 3000) placed.Add((overlay, model)); else loose.Add(model);
            }
            chunk.Overlays = placed;
            var placedMin = placed.Count > 0 ? placed.Select(entry => entry.model.Model.BoundsMin).Aggregate(Vec3.Min) : Vec3.Zero;
            var placedMax = placed.Count > 0 ? placed.Select(entry => entry.model.Model.BoundsMax).Aggregate(Vec3.Max) : Vec3.Zero;
            float rowStart = placedMin.X;
            float cursorX = rowStart;
            float cursorZ = placedMax.Z + 2000;
            float rowDepth = 0;
            float rowWidth = Math.Max(20000, placedMax.X - placedMin.X);
            int index = 0;
            foreach (var model in loose.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)) {
                var size = model.Model.BoundsSize;
                float width = Math.Max(size.X, 200) + 300;
                if (cursorX - rowStart + width > rowWidth) { cursorX = rowStart; cursorZ += rowDepth + 300; rowDepth = 0; }
                var levelObject = new LevelObject {
                    ClassName = model.Name,
                    ChunkName = chunk.Def.Name,
                    Index = 100000 + index++,
                    Section = new BniSection { Name = model.Name },
                    Position = new Vec3(cursorX - model.Model.BoundsMin.X, placedMin.Y, cursorZ - model.Model.BoundsMin.Z),
                    Category = ObjectCategory.Prop,
                };
                chunk.Objects.Add(levelObject);
                chunk.ObjectModels[levelObject] = model;
                cursorX += width;
                rowDepth = Math.Max(rowDepth, Math.Max(size.Z, 200));
            }
        }

        private void ShowScene(LevelScene scene, Stopwatch stopwatch) {
            Scene = scene;
            Renderer.Load(scene);
            ViewSettings.ClearSceneVisibility();
            int triangles = Renderer.Items.Sum(item => item.Model.Parts.Sum(part => part.Triangles));
            SetStatus($"{scene.Def.Name}: {scene.Chunks.Count} chunks, {scene.AllObjects.Count()} objects, {triangles:N0} triangles — {stopwatch.ElapsedMilliseconds} ms");
            Log($"Loaded {scene.Def.Name} in {stopwatch.ElapsedMilliseconds} ms");
            foreach (var warning in scene.Warnings.Distinct()) Log("  warning: " + warning);
            Select(null, null);
        }

        /// <summary>levels.ini fog colours are either 0–1 floats or 0–255 bytes.</summary>
        private void ApplyFog(LevelDef level) {
            if (level.FogColor != null && level.FogColor.Length >= 3) {
                var color = new Vec3(level.FogColor[0], level.FogColor[1], level.FogColor[2]);
                if (Math.Max(color.X, Math.Max(color.Y, color.Z)) > 1.5f) color /= 255f;
                FogColor = Vec3.Clamp(color, Vec3.Zero, Vec3.One);
            }
            if (level.FogFar > 0) FogRange = new Vec2(level.FogNear, level.FogFar);
            Log($"Fog: colour {string.Join(" ", level.FogColor ?? Array.Empty<float>())}, planes {level.FogNear} .. {level.FogFar}");
        }

        /// <summary>
        /// Looks at the first checkpoint of the start chunk from above and behind it, facing the way its respawn
        /// camera faces when it has one. Without a checkpoint, frames the start chunk.
        /// </summary>
        private void PlaceCameraAtStart(LevelScene scene) {
            var level = scene.Def;
            var startChunk = scene.Chunks.FirstOrDefault(chunk => string.Equals(chunk.Def.Name, level.StartChunk, StringComparison.OrdinalIgnoreCase))
                             ?? scene.Chunks.FirstOrDefault(chunk => !chunk.Def.IsEnvironment);
            if (startChunk == null) { FrameWorld(Renderer.SceneMin, Renderer.SceneMax); return; }

            var checkpoint = startChunk.Objects.Where(levelObject => levelObject.Category == ObjectCategory.Checkpoint)
                                               .OrderBy(levelObject => levelObject.Index).FirstOrDefault();
            if (checkpoint != null) {
                var facing = new Vec3(0, 0, -1);
                var respawnCamera = checkpoint.Shapes.FirstOrDefault(shape => shape.Kind == ShapeKind.CameraLine && shape.Points.Count == 2);
                if (respawnCamera != null) {
                    var look = respawnCamera.Points[1] - respawnCamera.Points[0];
                    look.Y = 0;
                    if (look.LengthSquared() > 1f) facing = Vec3.Normalize(look);
                }
                var target = checkpoint.Position + new Vec3(0, 150, 0);
                LookAtWorld(target - facing * 900 + new Vec3(0, 450, 0), target);
                Camera.Speed = 2000f;
                return;
            }

            var overlays = Renderer.Items.Where(item => item.Kind == ItemKind.Overlay && item.Chunk == startChunk).ToList();
            if (overlays.Count > 0)
                FrameWorld(overlays.Select(item => item.WorldMin).Aggregate(Vec3.Min), overlays.Select(item => item.WorldMax).Aggregate(Vec3.Max));
            else
                FrameWorld(Renderer.SceneMin, Renderer.SceneMax);
        }

        // ---------------------------------------------------------------- selection

        public void Select(LevelObject levelObject, DrawItem item) {
            Renderer.SetSelection(levelObject, item);
            if (levelObject == null && item == null)
                Properties = PropertiesView.ForLevel(Scene, Game, Renderer);
            else
                Properties = PropertiesView.ForSelection(levelObject, item ?? Renderer.SelectedItem, Scene, ModelDb, Assets);
        }

        public void SelectAndFocus(LevelObject levelObject, DrawItem item) {
            Select(levelObject, item);
            FocusSelection();
        }

        public void ShowCollision(Collision collision) {
            Renderer.SetSelection(null);
            Properties = PropertiesView.ForCollision(collision);
            FrameWorld(collision.BoundsMin, collision.BoundsMax);
        }

        public void ShowProperties(PropertiesView view) => Properties = view;

        public void FollowLink(LevelObject target) {
            if (ViewSettings.HiddenObjects.Remove(target)) Renderer.RebuildLines(ViewSettings);
            SelectAndFocus(target, null);
        }

        public void PickRay(Vec3 origin, Vec3 direction) {
            if (Scene == null) return;
            var hit = Renderer.Pick(origin, direction, ViewSettings);
            if (hit == null) { Select(null, null); return; }
            Select(hit.Object, hit.Item);
            SetStatus($"Picked {hit.Description} at {PropertiesView.Format(hit.Point)}");
        }

        public void VisibilityChanged() {
            if (glReady) Renderer.RebuildLines(ViewSettings);
        }

        public void FocusSelection() {
            Vec3 min, max;
            if (Renderer.SelectedItem != null) {
                min = Renderer.SelectedItem.WorldMin;
                max = Renderer.SelectedItem.WorldMax;
            }
            else if (Renderer.Selected != null) {
                var levelObject = Renderer.Selected;
                min = levelObject.Position - new Vec3(300);
                max = levelObject.Position + new Vec3(300);
                foreach (var shape in levelObject.Shapes) {
                    foreach (var point in shape.Points) { min = Vec3.Min(min, point); max = Vec3.Max(max, point); }
                    if (shape.Kind == ShapeKind.Sphere) {
                        min = Vec3.Min(min, shape.Center - new Vec3(shape.Radius));
                        max = Vec3.Max(max, shape.Center + new Vec3(shape.Radius));
                    }
                    if (shape.Kind == ShapeKind.Box) {
                        var half = Vec3.Abs(shape.Size) * 0.6f;
                        min = Vec3.Min(min, shape.Center - half);
                        max = Vec3.Max(max, shape.Center + half);
                    }
                }
            }
            else return;
            float speed = Camera.Speed;
            FrameWorld(min, max);
            Camera.Speed = Math.Min(speed, Camera.Speed * 2);
        }

        public Vec3 CameraWorldPosition => ToDisplay(Camera.Position);

        /// <summary>Camera yaw and pitch in degrees, in world (unmirrored) space.</summary>
        public Vec2 CameraWorldAngles {
            get {
                float yaw = ViewSettings.MirrorX ? -Camera.Yaw : Camera.Yaw;
                float degrees = 180f / MathF.PI;
                float wrappedYaw = (yaw * degrees % 360f + 540f) % 360f - 180f;
                return new Vec2(wrappedYaw, Camera.Pitch * degrees);
            }
        }

        public void FrameScene() => FrameWorld(Renderer.SceneMin, Renderer.SceneMax);

        /// <summary>The camera lives in display space, which is X-mirrored when MirrorX is on.</summary>
        private Vec3 ToDisplay(Vec3 world) => ViewSettings.MirrorX ? world * new Vec3(-1, 1, 1) : world;

        private void FrameWorld(Vec3 min, Vec3 max) {
            var corner1 = ToDisplay(min);
            var corner2 = ToDisplay(max);
            Camera.Frame(Vec3.Min(corner1, corner2), Vec3.Max(corner1, corner2));
        }

        private void LookAtWorld(Vec3 eye, Vec3 target) => Camera.LookAt(ToDisplay(eye), ToDisplay(target));

        // ---------------------------------------------------------------- export

        public void ExportScene(string path) {
            if (Scene == null) return;
            var exporter = new GlbExporter(Scene.Assets) {
                IncludeGroundCollision = ViewSettings.GroundCollision,
                IncludeCollisionComponents = ViewSettings.ModelCollisionComponents,
                IncludeFragments = ViewSettings.Fragments,
            };
            var visible = new LevelScene { Def = Scene.Def, Assets = Scene.Assets };
            visible.Chunks.AddRange(Scene.Chunks.Where(chunk => !ViewSettings.HiddenChunks.Contains(chunk.Def.Name)));
            exporter.AddScene(visible, ViewSettings.Objects);
            exporter.Save(path);
            SetStatus("Exported " + path);
        }

        public void ExportSelectedModel(string path) {
            var item = Renderer.SelectedItem;
            if (item == null || Scene == null) return;
            var exporter = new GlbExporter(Scene.Assets) {
                IncludeCollisionComponents = ViewSettings.ModelCollisionComponents,
                IncludeFragments = ViewSettings.Fragments,
            };
            exporter.AddModel(item.Model.Source, System.Numerics.Matrix4x4.Identity, item.Model.Source.Name);
            exporter.Save(path);
            SetStatus("Exported " + path);
        }

        public void ExportTextures(string folder) {
            if (Scene == null) return;
            int count = 0;
            var names = Renderer.Items.SelectMany(item => item.Model.Source.Materials).Select(material => material.TextureName).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names) {
                var texture = Scene.Assets.GetTexture(name);
                if (texture == null) continue;
                Png.Save(Path.Combine(folder, name + ".png"), texture.Rgba, texture.Width, texture.Height);
                count++;
            }
            SetStatus($"Wrote {count} textures to {folder}");
        }

        public void ExportLayouts(string folder) {
            if (Scene == null) return;
            int count = 0;
            foreach (var chunk in Scene.Chunks.Where(candidate => candidate.Layout != null)) {
                File.WriteAllText(Path.Combine(folder, chunk.Def.Name + ".lc3.txt"), chunk.Layout.ToText());
                count++;
            }
            SetStatus($"Wrote {count} layout files to {folder}");
        }

        public void RequestScreenshot(string path) => pendingScreenshot = path;

        /// <summary>Called by the main view right after it renders, with its framebuffer still bound.</summary>
        public void SavePendingScreenshot(int width, int height) {
            if (pendingScreenshot == null) return;
            string path = pendingScreenshot;
            pendingScreenshot = null;
            try {
                Png.Save(path, SceneRenderer.ReadPixels(width, height), width, height);
                SetStatus("Saved " + path);
            }
            catch (Exception exception) { Log("Screenshot failed: " + exception); }
        }

        // ---------------------------------------------------------------- misc

        public void SetStatus(string status) {
           // Status = status;
        }
        public void Log(string line) {
            lock (log) log.Append(line.Replace("\n", "\r\n")).Append("\r\n");
        }

        private static string LoadSetting(string key) {
            try {
                if (!File.Exists(SettingsPath)) return null;
                foreach (var line in File.ReadAllLines(SettingsPath)) {
                    int separator = line.IndexOf('=');
                    if (separator > 0 && line.Substring(0, separator) == key) return line.Substring(separator + 1);
                }
            }
            catch (IOException) { }
            return null;
        }

        private static void SaveSetting(string key, string value) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var lines = File.Exists(SettingsPath) ? File.ReadAllLines(SettingsPath).Where(line => !line.StartsWith(key + "=")).ToList() : new List<string>();
                lines.Add(key + "=" + value);
                File.WriteAllLines(SettingsPath, lines);
            }
            catch (IOException) { }
        }
    }
}

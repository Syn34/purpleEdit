using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hexa.NET.ImGui;
using Mk2.Viewer.ImGuiUi;
using Mk2.Viewer.ImGuiUi.Frames;
using Mk2.Viewer.Rendering;
using Mk2.Viewer.Utils;
using Vec2 = System.Numerics.Vector2;

namespace Mk2.Viewer.Frames {
    public sealed class MainFrame : IDisposable {
        public readonly Window Viewer;
        private readonly ImGuiController controller;
        private readonly List<Frame> frames = new List<Frame>();

        private readonly RenderModesFrame renderModes;
        private readonly BrowserFrame browser;
        private readonly PropertiesFrame properties;
        private readonly LogFrame log;
        private readonly MainViewFrame mainView;
        private readonly ModelViewerFrame modelViewer;
        private readonly TextureViewerFrame textureViewer;

        private bool dockLayoutBuilt;
        private string levelFilter = "";
        private string packFilter = "";

        public ViewSettings ViewSettings => Viewer.ViewSettings;

        public void ShowTexture(string name) => textureViewer.Show(name);

        public MainFrame(Window viewer) {
            Viewer = viewer;
            controller = new ImGuiController(viewer.ClientSize.X, viewer.ClientSize.Y);

            renderModes = new RenderModesFrame(this);
            browser = new BrowserFrame(this);
            properties = new PropertiesFrame(this);
            log = new LogFrame(this) { isOpen = false };
            modelViewer = new ModelViewerFrame(this) { isOpen = false };
            textureViewer = new TextureViewerFrame(this) { isOpen = false };
            mainView = new MainViewFrame(this);
            frames.AddRange(new Frame[] { renderModes, browser, properties, log, modelViewer, textureViewer, mainView });

            viewer.Resize += eventArgs => controller.WindowResized(viewer.ClientSize.X, viewer.ClientSize.Y);
            viewer.MouseWheel += eventArgs => controller.MouseScroll(new OpenTK.Mathematics.Vector2(eventArgs.OffsetX, eventArgs.OffsetY));
            viewer.TextInput += eventArgs => controller.PressChar((char)eventArgs.Unicode);
        }

        public void RenderUi() {
            controller.Update(Viewer, 1f / 60f);
            RenderMenuBar();
            RenderDockSpace();
            foreach (var frame in frames.Where(candidate => candidate.isOpen))
                frame.RenderAsWindow(1f / 60f);
            controller.Render();
        }

        private void RenderDockSpace() {
            var hostFlags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus |
                            ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground;

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.SetNextWindowSize(viewport.WorkSize);
            ImGui.SetNextWindowViewport(viewport.ID);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vec2.Zero);
            ImGui.Begin("##DockSpaceHost", hostFlags);
            ImGui.PopStyleVar(3);

            uint dockspaceId = ImGui.GetID("MainDockSpace");
            ImGui.DockSpace(dockspaceId, Vec2.Zero, ImGuiDockNodeFlags.None, null);
            if (!dockLayoutBuilt) {
                BuildDefaultLayout(dockspaceId);
                dockLayoutBuilt = true;
            }
            ImGui.End();
        }

        private unsafe void BuildDefaultLayout(uint dockspaceId) {
            ImGuiP.DockBuilderSetNodeSize(dockspaceId, ImGui.GetMainViewport().WorkSize);
            uint center = dockspaceId;
            uint left, right, bottom, leftBottom;
            ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.20f, &left, &center);
            ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.22f, &right, &center);
            ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.15f, &bottom, &center);
            ImGuiP.DockBuilderSplitNode(left, ImGuiDir.Down, 0.5f, &leftBottom, &left);
            ImGuiP.DockBuilderDockWindow(textureViewer.WindowTitle, center);
            ImGuiP.DockBuilderDockWindow(modelViewer.WindowTitle, center);
            ImGuiP.DockBuilderDockWindow(mainView.WindowTitle, center);
            ImGuiP.DockBuilderDockWindow(browser.WindowTitle, left);
            ImGuiP.DockBuilderDockWindow(renderModes.WindowTitle, leftBottom);
            ImGuiP.DockBuilderDockWindow(properties.WindowTitle, right);
            ImGuiP.DockBuilderDockWindow(log.WindowTitle, bottom);
            ImGuiP.DockBuilderFinish(dockspaceId);
        }

        private void RenderMenuBar() {
            if (!ImGui.BeginMainMenuBar()) return;
            RenderFileMenu();
            RenderListMenu("Levels", ref levelFilter, Viewer.Levels.Select(level => level.Name), name => {
                var level = Viewer.Levels.FirstOrDefault(candidate => candidate.Name == name);
                if (level != null) _ = Viewer.LoadLevel(level);
            });
            /*
            RenderListMenu("Packs", ref packFilter,
                Viewer.Game?.ChunkPackNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>(),
                name => _ = Viewer.LoadPack(name));
            */
            /*
            if (ImGui.BeginMenu("View")) {
                if (ImGui.MenuItem("Frame scene")) Viewer.FrameScene();
                if (ImGui.MenuItem("Focus selection", "F")) Viewer.FocusSelection();
                ImGui.EndMenu();
            }
            */

            if (ImGui.BeginMenu("Windows")) {
                MenuToggle("Main View", mainView);
                MenuToggle("Render", renderModes);
                MenuToggle("Browser", browser);
                MenuToggle("Model Viewer", modelViewer);
                // MenuToggle("Texture Viewer", textureViewer);
                MenuToggle("Properties", properties);
                // MenuToggle("Log", log);
                if (ImGui.MenuItem("Reset layout")) dockLayoutBuilt = false;
                ImGui.EndMenu();
            }

            //ImGui.SameLine(ImGui.GetWindowWidth() - 420);
            //ImGui.TextDisabled(Viewer.Status);
            ImGui.EndMainMenuBar();
        }

        private void RenderFileMenu() {
            if (!ImGui.BeginMenu("File")) return;
            /*if (ImGui.MenuItem("Open game folder")) {
                string folder = CrossFileDialog.OpenFolder("Game folder");
                if (folder.Length > 0) _ = Viewer.OpenGameSource(folder);
            }*/
            if (ImGui.MenuItem("Open RKV")) {
                string archive = CrossFileDialog.OpenFile("RKV archive", "*.rkv *.bpk");
                if (archive.Length > 0) _ = Viewer.OpenGameSource(archive);
            }
            /*
            if (ImGui.MenuItem("Extract RKV", "", false, !Viewer.Busy)) {
                string archive = CrossFileDialog.OpenFile("RKV archive", "*.rkv *.bpk");
                string folder = archive.Length > 0 ? CrossFileDialog.OpenFolder("Extract to") : "";
                if (folder.Length > 0) _ = Viewer.ExtractArchive(archive, folder);
            }
            */

            // ImGui.Separator();
           // bool hasScene = Viewer.Scene != null;
            /*
            if (ImGui.MenuItem("Export scene to .glb…", "", false, hasScene)) {
                string path = SavePath("Export scene", ".glb");
                if (path != null) Viewer.ExportScene(path);
            }
            if (ImGui.MenuItem("Export selected model to .glb…", "", false, Viewer.Renderer.SelectedItem != null)) {
                string path = SavePath("Export model", ".glb");
                if (path != null) Viewer.ExportSelectedModel(path);
            }
            if (ImGui.MenuItem("Export scene textures to PNG…", "", false, hasScene)) {
                string folder = CrossFileDialog.OpenFolder("Texture folder");
                if (folder.Length > 0) Viewer.ExportTextures(folder);
            }
            if (ImGui.MenuItem("Export layouts as text…", "", false, hasScene)) {
                string folder = CrossFileDialog.OpenFolder("Layout folder");
                if (folder.Length > 0) Viewer.ExportLayouts(folder);
            }
            if (ImGui.MenuItem("Save screenshot…")) {
                string path = SavePath("Screenshot", ".png");
                if (path != null) Viewer.RequestScreenshot(path);
            }
            */

            //ImGui.Separator();
            // if (ImGui.MenuItem("Exit")) Viewer.Close();
            ImGui.EndMenu();
        }

        private static string SavePath(string title, string extension) {
            string path = CrossFileDialog.SaveFile(title, "*" + extension);
            if (path.Length == 0) return null;
            return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
        }

        private static void RenderListMenu(string title, ref string filter, IEnumerable<string> names, Action<string> onPick) {
            if (!ImGui.BeginMenu(title)) return;
            ImGui.SetNextItemWidth(240);
            // ImGui.InputTextWithHint("##filter", "Filter", ref filter, 128);
            ImGui.Separator();
            string activeFilter = filter;
            if (ImGui.BeginChild("##list", new Vec2(260, 360))) {
                foreach (var name in names.Where(candidate => activeFilter.Length == 0 || candidate.Contains(activeFilter, StringComparison.OrdinalIgnoreCase)))
                    if (ImGui.MenuItem(name)) onPick(name);
            }
            ImGui.EndChild();
            ImGui.EndMenu();
        }

        private static void MenuToggle(string label, Frame frame) {
            bool open = frame.isOpen;
            if (ImGui.MenuItem(label, "", open)) frame.isOpen = !open;
        }

        public void Dispose() {
            foreach (var frame in frames) frame.Dispose();
            frames.Clear();
            controller.Dispose();
        }
    }
}

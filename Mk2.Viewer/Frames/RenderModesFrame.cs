using Hexa.NET.ImGui;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class RenderModesFrame : Frame {
        protected override string frameName { get; set; }

        public RenderModesFrame(MainFrame owner) : base(owner, "Render") { }

        public override void Render(float deltaTime) {
            var settings = owner.ViewSettings;
            if (settings == null) return;

            ImGui.SeparatorText("Geometry");
            ImGui.Checkbox("Overlays", ref settings.Overlays);
            ImGui.Checkbox("Objects", ref settings.Objects);
            ImGui.Checkbox("Prop markers", ref settings.PropMarkers);
            ImGui.Checkbox("Sky dome", ref settings.EnvDome);
            ImGui.Checkbox("Ground collision", ref settings.GroundCollision);
            ImGui.Checkbox("Object collision", ref settings.ObjectCollision);

            ImGui.SeparatorText("Volumes");
            foreach (var category in ViewSettings.VolumeCategories) {
                bool shown = settings.ShownVolumes.Contains(category);
                if (ImGui.Checkbox(VolumeLabel(category), ref shown)) {
                    if (shown) settings.ShownVolumes.Add(category); else settings.ShownVolumes.Remove(category);
                }
            }

            ImGui.SeparatorText("Display");
            ImGui.Checkbox("Camera info", ref settings.CameraInfo);

            ImGui.SeparatorText("Lighting");
            ImGui.Checkbox("Textures", ref settings.Textures);
            ImGui.Checkbox("Vertex colors", ref settings.VertexColors);
            ImGui.Checkbox("Transparency", ref settings.Transparency);
            ImGui.Checkbox("Fog", ref settings.Fog);
            // ImGui.Checkbox("Env-map crystals", ref settings.EnvMapGlow);
            // ImGui.SliderFloat("Brightness", ref settings.Brightness, 1.0f, 2.0f);
        }

        private static string VolumeLabel(ObjectCategory category) => category switch {
            ObjectCategory.Trigger => "Triggers",
            ObjectCategory.Path => "Paths",
            _ => category.ToString(),
        };
    }
}
using System.Collections.Generic;
using Mk2.Game;

namespace Mk2.Viewer.Rendering {
    public sealed class ViewSettings {
        public bool Overlays = true;
        public bool Objects = true;
        public bool PropMarkers = false;
        public bool EnvDome = true;
        public bool GroundCollision = false;
        public bool ObjectCollision = false;

        public bool Textures = true;
        public bool VertexColors = true;
        public bool Transparency = true;
        public bool Fog = true;
        public bool EnvMapGlow = true;
        public float Brightness = 1.2f;

        public bool CameraInfo = true;

        public bool Wireframe = false;
        public bool CullBackfaces = false;
        public bool ModelCollisionComponents = false;
        public bool Fragments = false;
        public bool MirrorX = true;

        public bool Skeleton = false;
        public bool AnimatedMesh = true;

        public static readonly ObjectCategory[] VolumeCategories =
        {
            ObjectCategory.Trigger, ObjectCategory.Spawn, ObjectCategory.Checkpoint, ObjectCategory.Path, ObjectCategory.Volume,
            ObjectCategory.Camera, ObjectCategory.Sound, ObjectCategory.Logic, ObjectCategory.Critter,
        };

        public readonly HashSet<ObjectCategory> ShownVolumes = new HashSet<ObjectCategory>();
        public readonly HashSet<string> HiddenChunks = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<(string chunk, string group)> HiddenGroups = new HashSet<(string, string)>();
        public readonly HashSet<(string chunk, ObjectCategory category)> HiddenChunkCategories = new HashSet<(string, ObjectCategory)>();
        public readonly HashSet<(string chunk, string name)> HiddenItems = new HashSet<(string, string)>();
        public readonly HashSet<LevelObject> HiddenObjects = new HashSet<LevelObject>();

        public bool CategoryVisible(ObjectCategory category) =>
            category == ObjectCategory.Prop ? Objects : ShownVolumes.Contains(category);

        public bool GroupHidden(string chunk, string group) => HiddenGroups.Contains((chunk, group));

        public bool OverlayVisible(string chunk, string name) =>
            Overlays && !HiddenChunks.Contains(chunk) && !GroupHidden(chunk, "overlays") && !HiddenItems.Contains((chunk, name));

        public bool DomeVisible(string chunk) => EnvDome && !HiddenChunks.Contains(chunk) && !GroupHidden(chunk, "dome");

        public bool GroundVisible(string chunk, string name) =>
            GroundCollision && !HiddenChunks.Contains(chunk) && !GroupHidden(chunk, "grounds") && !HiddenItems.Contains((chunk, name));

        public bool CategoryBatchVisible(string chunk, ObjectCategory category) =>
            CategoryVisible(category) && !HiddenChunks.Contains(chunk) && !GroupHidden(chunk, "objects")
            && !HiddenChunkCategories.Contains((chunk, category));

        public bool ObjectVisible(LevelObject levelObject) =>
            CategoryBatchVisible(levelObject.ChunkName, levelObject.Category) && !HiddenObjects.Contains(levelObject);

        public bool ObjectCollisionVisible(LevelObject levelObject) =>
            ObjectCollision && !HiddenChunks.Contains(levelObject.ChunkName) && !HiddenObjects.Contains(levelObject);

        public void ClearSceneVisibility() {
            HiddenChunks.Clear();
            HiddenGroups.Clear();
            HiddenChunkCategories.Clear();
            HiddenItems.Clear();
            HiddenObjects.Clear();
        }
    }
}
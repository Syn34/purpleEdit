using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Mk2.Viewer.Rendering;

namespace Mk2.Viewer.ImGuiUi {
    public sealed class ImGuiController : IDisposable {
        private bool frameBegun;

        private int vertexArray, vertexBuffer, indexBuffer;
        private int vertexBufferSize = 10000, indexBufferSize = 2000;

        private readonly Dictionary<int, int> textures = new Dictionary<int, int>();
        private ShaderProgram shader;

        private int windowWidth, windowHeight;

        public ImGuiController(int width, int height) {
            windowWidth = width;
            windowHeight = height;

            ImGuiContextPtr context = Hexa.NET.ImGui.ImGui.CreateContext();
            Hexa.NET.ImGui.ImGui.SetCurrentContext(context);
            var io = Hexa.NET.ImGui.ImGui.GetIO();
            if (System.IO.File.Exists(@"C:\Windows\Fonts\arial.ttf"))
                unsafe { io.Fonts.AddFontFromFileTTF(@"C:\Windows\Fonts\arial.ttf", 18f); }
            else
                unsafe { io.Fonts.AddFontDefault(); }

            ImGui.StyleColorsClassic();

            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            unsafe { io.IniFilename = null; } // no imgui.ini

            CreateDeviceResources();

            SetPerFrameData(1f / 60f);

            Hexa.NET.ImGui.ImGui.NewFrame();
            frameBegun = true;
        }

        public void WindowResized(int width, int height) {
            windowWidth = width;
            windowHeight = height;
        }

        private void CreateDeviceResources() {
            vertexArray = GL.GenVertexArray();
            GL.BindVertexArray(vertexArray);

            vertexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            indexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            const string vertexSource = @"#version 330 core
uniform mat4 uProjection;
layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;
out vec4 color;
out vec2 texCoord;
void main() {
    gl_Position = uProjection * vec4(in_position, 0, 1);
    color = in_color;
    texCoord = in_texCoord;
}";
            const string fragmentSource = @"#version 330 core
uniform sampler2D uFontTexture;
in vec4 color;
in vec2 texCoord;
out vec4 outputColor;
void main() { outputColor = color * texture(uFontTexture, texCoord); }";
            shader = new ShaderProgram(vertexSource, fragmentSource);

            SetVertexLayout();
        }

        private unsafe void SetVertexLayout() {
            GL.BindVertexArray(vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);

            int stride = sizeof(ImDrawVert);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
        }

        private void UpdateTextures(ImDrawDataPtr drawData) {
            var list = drawData.Textures;
            for (int index = 0; index < list.Size; index++) {
                ImTextureDataPtr texture = list[index];
                if (texture.Status == ImTextureStatus.WantCreate) CreateTexture(texture);
                else if (texture.Status == ImTextureStatus.WantUpdates) { DestroyTexture(texture); CreateTexture(texture); }
                else if (texture.Status == ImTextureStatus.WantDestroy) DestroyTexture(texture);
            }
        }

        private unsafe void CreateTexture(ImTextureDataPtr texture) {
            int handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texture.Width, texture.Height, 0,
                           OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, (IntPtr)texture.GetPixels());

            textures[texture.UniqueID] = handle;
            texture.SetTexID((ulong)(uint)handle);
            texture.SetStatus(ImTextureStatus.Ok);
        }

        private void DestroyTexture(ImTextureDataPtr texture) {
            if (textures.TryGetValue(texture.UniqueID, out int handle)) {
                GL.DeleteTexture(handle);
                textures.Remove(texture.UniqueID);
            }
            texture.SetTexID((nint)0);
            texture.SetStatus(ImTextureStatus.Destroyed);
        }
        public void Render() {
            if (!frameBegun) return;
            frameBegun = false;

            GL.BindVertexArray(vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
            SetVertexLayout();

            Hexa.NET.ImGui.ImGui.Render();
            RenderDrawData(Hexa.NET.ImGui.ImGui.GetDrawData());
        }

        public void Update(GameWindow window, float deltaSeconds) {
            if (frameBegun) Hexa.NET.ImGui.ImGui.Render();

            SetPerFrameData(deltaSeconds);
            UpdateInput(window);

            frameBegun = true;
            Hexa.NET.ImGui.ImGui.NewFrame();
        }

        private void SetPerFrameData(float deltaSeconds) {
            var io = Hexa.NET.ImGui.ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(windowWidth, windowHeight);
            io.DisplayFramebufferScale = System.Numerics.Vector2.One;
            io.DeltaTime = deltaSeconds;
        }

        private readonly List<char> pressedChars = new List<char>();

        internal void MouseScroll(OpenTK.Mathematics.Vector2 offset) {
            var io = Hexa.NET.ImGui.ImGui.GetIO();
            io.MouseWheel += offset.Y;
            io.MouseWheelH += offset.X;
        }

        public void PressChar(char character) => pressedChars.Add(character);

        private static readonly (MouseButton Button, ImGuiMouseButton ImGui)[] MouseButtonMap = {
            (MouseButton.Left, ImGuiMouseButton.Left),
            (MouseButton.Right, ImGuiMouseButton.Right),
            (MouseButton.Middle, ImGuiMouseButton.Middle),
        };

        private void UpdateInput(GameWindow window) {
            var io = Hexa.NET.ImGui.ImGui.GetIO();

            var mouse = window.MouseState;
            io.MousePos = new System.Numerics.Vector2(mouse.X, mouse.Y);
            foreach (var (button, imguiBtn) in MouseButtonMap)
                io.MouseDown[(int)imguiBtn] = window.IsFocused && mouse.IsButtonDown(button);

            var keyboard = window.KeyboardState;
            if (window.IsFocused) {
                io.KeyCtrl = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
                io.KeyAlt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
                io.KeyShift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
                io.KeySuper = keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper);

                foreach (var entry in KeyMap)
                    io.AddKeyEvent(entry.Value, keyboard.IsKeyDown(entry.Key));
            }

            foreach (var character in pressedChars) io.AddInputCharacter(character);
            pressedChars.Clear();
        }

        private static readonly Dictionary<Keys, ImGuiKey> KeyMap = new Dictionary<Keys, ImGuiKey> {
            { Keys.Tab, ImGuiKey.Tab },
            { Keys.Left, ImGuiKey.LeftArrow },
            { Keys.Right, ImGuiKey.RightArrow },
            { Keys.Up, ImGuiKey.UpArrow },
            { Keys.Down, ImGuiKey.DownArrow },
            { Keys.PageUp, ImGuiKey.PageUp },
            { Keys.PageDown, ImGuiKey.PageDown },
            { Keys.Home, ImGuiKey.Home },
            { Keys.End, ImGuiKey.End },
            { Keys.Delete, ImGuiKey.Delete },
            { Keys.Backspace, ImGuiKey.Backspace },
            { Keys.Enter, ImGuiKey.Enter },
            { Keys.Escape, ImGuiKey.Escape },
            { Keys.Space, ImGuiKey.Space },
            { Keys.LeftControl, ImGuiKey.LeftCtrl },
            { Keys.RightControl, ImGuiKey.RightCtrl },
            { Keys.LeftShift, ImGuiKey.LeftShift },
            { Keys.RightShift, ImGuiKey.RightShift },
            { Keys.LeftAlt, ImGuiKey.LeftAlt },
            { Keys.RightAlt, ImGuiKey.RightAlt },
            { Keys.C, ImGuiKey.C },
            { Keys.V, ImGuiKey.V },
            { Keys.X, ImGuiKey.X },
            { Keys.Y, ImGuiKey.Y },
            { Keys.Z, ImGuiKey.Z },
            { Keys.A, ImGuiKey.A },
            { Keys.W, ImGuiKey.W },
            { Keys.S, ImGuiKey.S },
            { Keys.D, ImGuiKey.D },
            { Keys.Q, ImGuiKey.Q },
            { Keys.E, ImGuiKey.E },
            { Keys.F, ImGuiKey.F },
        };

        private unsafe void RenderDrawData(ImDrawDataPtr drawData) {
            if (shader == null || drawData.CmdListsCount == 0) return;

            UpdateTextures(drawData);

            for (int index = 0; index < drawData.CmdListsCount; index++) {
                ImDrawListPtr cmdList = drawData.CmdLists[index];

                int vertexSize = cmdList.VtxBuffer.Size * sizeof(ImDrawVert);
                if (vertexSize > vertexBufferSize) {
                    int newSize = (int)Math.Max(vertexBufferSize * 1.5f, vertexSize);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
                    GL.BufferData(BufferTarget.ArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    vertexBufferSize = newSize;
                }

                int indexSize = cmdList.IdxBuffer.Size * sizeof(ushort);
                if (indexSize > indexBufferSize) {
                    int newSize = (int)Math.Max(indexBufferSize * 1.5f, indexSize);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    indexBufferSize = newSize;
                }
            }

            var io = Hexa.NET.ImGui.ImGui.GetIO();
            var proj = System.Numerics.Matrix4x4.CreateOrthographicOffCenter(0, io.DisplaySize.X, io.DisplaySize.Y, 0, -1f, 1f);

            shader.Use();
            shader.Set("uProjection", proj);
            shader.Set("uFontTexture", 0);

            GL.BindVertexArray(vertexArray);

            drawData.ScaleClipRects(io.DisplayFramebufferScale);

            GL.Enable(EnableCap.Blend);
            GL.Enable(EnableCap.ScissorTest);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);

            for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++) {
                ImDrawListPtr cmdList = drawData.CmdLists[listIndex];

                GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, cmdList.VtxBuffer.Size * sizeof(ImDrawVert), (IntPtr)cmdList.VtxBuffer.Data);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
                GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, cmdList.IdxBuffer.Size * sizeof(ushort), (IntPtr)cmdList.IdxBuffer.Data);

                int vtxOffset = 0, idxOffset = 0;
                for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++) {
                    ImDrawCmd command = cmdList.CmdBuffer[cmdI];
                    if (command.UserCallback != null) continue; // no user callbacks used here

                    ImTextureID texId = command.GetTexID();
                    int glHandle = (int)(ulong)texId;

                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, glHandle);

                    var clip = command.ClipRect;
                    GL.Scissor((int)clip.X, windowHeight - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));

                    if ((io.BackendFlags & ImGuiBackendFlags.RendererHasVtxOffset) != 0)
                        GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)command.ElemCount, DrawElementsType.UnsignedShort, (IntPtr)(idxOffset * sizeof(ushort)), vtxOffset);
                    else
                        GL.DrawElements(BeginMode.Triangles, (int)command.ElemCount, DrawElementsType.UnsignedShort, (int)command.IdxOffset * sizeof(ushort));

                    idxOffset += (int)command.ElemCount;
                }
                vtxOffset += cmdList.VtxBuffer.Size;
            }

            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
        }

        public void Dispose() {
            foreach (var handle in textures.Values) GL.DeleteTexture(handle);
            textures.Clear();
            shader?.Dispose();
            if (vertexArray != 0) GL.DeleteVertexArray(vertexArray);
            if (vertexBuffer != 0) GL.DeleteBuffer(vertexBuffer);
            if (indexBuffer != 0) GL.DeleteBuffer(indexBuffer);
        }
    }
}
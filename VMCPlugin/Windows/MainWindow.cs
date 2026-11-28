/*
 * Copyright(c) 2024 GiR-Zippo, Meowchestra
 * Licensed under the GPL v3 license. See https://github.com/GiR-Zippo/LightAmp/blob/main/LICENSE for full license information.
 */

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.IO;
using System.Numerics;
using System.Text;
using VMCPlugin.BoneManipulation;
using VMCPlugin.Config;

namespace VMCPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private VmcHeadReceiver _vmcHead { get; init; }
    private readonly IDalamudPluginInterface _pi;
    private Configuration _config { get; init; }
    private readonly FileDialogManager _fileDialog = new();

    // this extra bool exists for ImGui, since you can't ref a property
    private bool visible;
    public bool Visible
    {
        get => visible;
        set => visible = value;
    }

    public MainWindow(Configuration configuration, IDalamudPluginInterface pi, VmcHeadReceiver vmcHead) : base(
        "VMCPlugin", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this._config = configuration;
        Visible = false;
        _pi = pi;
        _vmcHead = vmcHead;
    }

    public void Dispose()
    {
    }

    public override void Update()
    {
    }

    public override void Draw()
    {
        if (ImGui.Begin("VMCPlugin", ref visible, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetNextWindowSize(new Vector2(300, 110), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(300, 110), new Vector2(float.MaxValue, float.MaxValue));

            DrawRecIndicator();
            _fileDialog.Draw();

            if (ImGui.Button("Start Recording"))
                _vmcHead.Recorder.Start(DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond);
            if (ImGui.Button("Stop Recording"))
            {
                _vmcHead.Recorder.Stop();
                _fileDialog.SaveFileDialog("Animation speichern", ".gltf", "recorded", ".gltf", (ok, path) =>
                {
                    if (ok)
                        _vmcHead.Recorder.ExportGltf(path);
                });
            }
        }
        ImGui.End();
    }

    void DrawRecIndicator()
    {
        if (!_vmcHead.Recorder.Recording) return;

        var on = ((int)(ImGui.GetTime() * 2) & 1) == 0;       // blinkt 1x pro Sekunde
        var col = ImGui.GetColorU32(new Vector4(1f, 0.15f, 0.15f, on ? 1f : 0.35f));

        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(p.X + h * 0.5f, p.Y + h * 0.5f), h * 0.4f, col);
        ImGui.Dummy(new Vector2(h, h));
        ImGui.SameLine();
        ImGui.Text($"REC {_vmcHead.Recorder.FrameCount / _vmcHead.Recorder.Fps:F1}s  ({_vmcHead.Recorder.FrameCount} Frames)");
    }
}
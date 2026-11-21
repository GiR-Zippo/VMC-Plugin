/*
 * Copyright(c) 2024 GiR-Zippo, Meowchestra
 * Licensed under the GPL v3 license. See https://github.com/GiR-Zippo/LightAmp/blob/main/LICENSE for full license information.
 */

using VMCPlugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VMCPlugin;
using System;
using System.Numerics;

namespace VMCPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private Configuration configuration { get; init; }

    // this extra bool exists for ImGui, since you can't ref a property
    private bool visible;
    public bool Visible
    {
        get => visible;
        set => visible = value;
    }

    public bool ManuallyDisconnected { get; set; }


    public MainWindow(Plugin plugin, Configuration configuration) : base(
        "VMCPlugin", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        Visible = false;
    }

    public void Dispose()
    {
    }

    public override void Update()
    {
    }

    public override void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(300, 110), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(300, 110), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.Begin("VMCPlugin", ref visible, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
        }
        ImGui.End();
    }
}
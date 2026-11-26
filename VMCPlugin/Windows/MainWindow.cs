/*
 * Copyright(c) 2024 GiR-Zippo, Meowchestra
 * Licensed under the GPL v3 license. See https://github.com/GiR-Zippo/LightAmp/blob/main/LICENSE for full license information.
 */

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.Numerics;
using VMCPlugin.Config;

namespace VMCPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private Configuration _config { get; init; }

    // this extra bool exists for ImGui, since you can't ref a property
    private bool visible;
    public bool Visible
    {
        get => visible;
        set => visible = value;
    }

    public MainWindow(Configuration configuration, IDalamudPluginInterface pi) : base(
        "VMCPlugin", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this._config = configuration;
        Visible = false;
        _pi = pi;
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

/*            var changed = false;


            if (changed)
                _pi.SavePluginConfig(_config);*/
        }
        ImGui.End();
    }
}
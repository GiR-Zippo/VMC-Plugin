using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
using VMCPlugin.BoneManipulation;
using VMCPlugin.Config;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentTradeMultiple.Delegates;

namespace VMCPlugin.Windows.Config;

internal class ConfigUi : IDisposable
{
    private readonly Configuration _config;
    private bool _showConfig;
    private readonly IDalamudPluginInterface _pi;
    private VmcHeadReceiver _vmcRecv;

    private int _selectedTab = 0;

    private static readonly (string Name, Func<string, bool> Has)[] _Groups =
    {
        ("Körper", b => b is "Hips" or "Spine" or "Chest" or "UpperChest" or "Neck" or "Head"),
        ("Arme",   b => b.EndsWith("Shoulder") || b.EndsWith("Arm") || b.EndsWith("Hand")),
        ("Finger", b => b.Contains("Proximal") || b.Contains("Intermediate")),
        ("Beine",  b => b.Contains("Leg") || b.EndsWith("Foot") || b.EndsWith("Toes")),
    };

    private static bool BoneChk(BonePreset p, string b)
    {
        var on = p.Bones.Contains(b);
        var label = b.StartsWith("Left") ? b[4..] : b.StartsWith("Right") ? b[5..] : b;
        if (!ImGui.Checkbox($"{label}##{b}", ref on)) return false;
        if (on) p.Bones.Add(b); else p.Bones.Remove(b);
        return true;
    }

    private static readonly string[] _Tabs =
    {
        "General",
        "VRM",
        "Presets",
        "FPView",
        "Debug"
    };

    public bool IsOpen
    {
        get => _showConfig;
        set => _showConfig = value;
    }

    public ConfigUi(Configuration config, bool showConfig, IDalamudPluginInterface pi, VmcHeadReceiver vmcRecv)
    {
        _config = config;
        _showConfig = showConfig;
        _pi = pi;
        _vmcRecv = vmcRecv;
    }

    public void OnDraw()
    {

        ImGui.SetNextWindowSize(new Vector2(720f, 480f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("VMC Config", ref _showConfig))
        {
            ImGui.End();
            return;
        }

        var changed = false;
        var rebuild = false;

        // Linkes Side-Tab-Panel
        if (ImGui.BeginChild("##SideTabs", new Vector2(165f, 0f), true))
        {
            for (var i = 0; i < _Tabs.Length; i++)
                if (ImGui.Selectable(_Tabs[i], _selectedTab == i, ImGuiSelectableFlags.None, new Vector2(0f, 26f)))
                    _selectedTab = i;
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Rechter Inhaltsbereich
        if (ImGui.BeginChild("##TabContent", new Vector2(0f, 0f), true))
        {
            ImGui.TextUnformatted(_Tabs[_selectedTab]);
            ImGui.Separator();
            ImGui.Spacing();

            switch (_selectedTab)
            {
                case 0:
                    DrawGeneral(_config);
                    break;
                case 1: // Allgemein
                    DrawVmcChains(_config);
                    break;
                case 2:
                    DrawPresets();
                    break;
                case 3:
                    DrawFPV();
                    break;
                case 4: // Debug
                    changed = DrawDebug();
                    break;
            }
        }
        ImGui.EndChild();

        if (rebuild)
        {
            changed = true;
        }

        ImGui.End();

        if (changed)
            _pi.SavePluginConfig(_config);
    }

    private void DrawGeneral(Configuration config)
    {
        Toggle("Enable Plugin", config.Enabled, v => config.Enabled = v);
        _vmcRecv.Enabled = config.Enabled;

        Toggle("First Person View", config.FirstPersonLook, v=> config.FirstPersonLook =v);
    }

    private bool DrawPresets()
    {
        var ch = false;
        var ps = _config.Presets;
        if (ps.Count == 0) { ps.Add(new BonePreset { Name = "Full", Bones = new(VmcHeadReceiver.AllBones) }); ch = true; }
        _config.ActivePreset = Math.Clamp(_config.ActivePreset, 0, ps.Count - 1);
        var p = ps[_config.ActivePreset];

        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("##preset", p.Name))
        {
            for (var i = 0; i < ps.Count; i++)
                if (ImGui.Selectable(ps[i].Name, i == _config.ActivePreset)) { _config.ActivePreset = i; ch = true; }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("+ Neu")) { ps.Add(new BonePreset { Name = $"Preset {ps.Count + 1}", Bones = new(p.Bones, StringComparer.OrdinalIgnoreCase) }); _config.ActivePreset = ps.Count - 1; ch = true; }
        ImGui.SameLine();
        if (ps.Count > 1 && ImGui.Button("Löschen")) { ps.RemoveAt(_config.ActivePreset); _config.ActivePreset = 0; return true; }

        var name = p.Name;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputText("Name", ref name, 64)) { p.Name = name; ch = true; }

        if (ImGui.Button("Alle")) { p.Bones = new(VmcHeadReceiver.AllBones, StringComparer.OrdinalIgnoreCase); ch = true; }
        ImGui.Separator();
        foreach (var (gName, has) in _Groups)
        {
            var bones = VmcHeadReceiver.AllBones.Where(has).ToArray();
            var cnt = bones.Count(b => p.Bones.Contains(b));
            ImGui.PushID(gName);
            if (ImGui.CollapsingHeader($"{gName}  ({cnt}/{bones.Length})###h"))
            {
                if (ImGui.SmallButton("Alle")) { foreach (var b in bones) p.Bones.Add(b); ch = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("Keine")) { foreach (var b in bones) p.Bones.Remove(b); ch = true; }

                var l = bones.Where(b => b.StartsWith("Left")).ToArray();
                var r = bones.Where(b => b.StartsWith("Right")).ToArray();
                if (l.Length == 0)
                    foreach (var b in bones) ch |= BoneChk(p, b);
                else if (ImGui.BeginTable("##t", 2))
                {
                    ImGui.TableSetupColumn("Links");
                    ImGui.TableSetupColumn("Rechts");
                    ImGui.TableHeadersRow();
                    for (var i = 0; i < l.Length; i++)
                    {
                        ImGui.TableNextColumn(); ch |= BoneChk(p, l[i]);
                        ImGui.TableNextColumn(); ch |= BoneChk(p, r[i]);
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.PopID();
        }
        return ch;
    }

    private static readonly string[] AxisNames = { "X", "Y", "Z" };
    private static bool DrawVmcChains(Configuration config)
    {
        var changed = false;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260f);

        changed |= Toggle("VRM1", config.Vrm1, v => config.Vrm1 = v);

        var bl = config.RetargetBlend;
        if (ImGui.SliderFloat("Mocap-Anteil", ref bl, 0f, 1f)) { config.RetargetBlend = bl; changed = true; }
        var fd = config.RetargetFadeSec;
        if (ImGui.SliderFloat("Fade (s)", ref fd, 0.05f, 2f)) { config.RetargetFadeSec = fd; changed = true; }
        return changed;
    }

    private bool DrawFPV()
    {
        bool changed = false;
        changed |= Toggle("Kamera folgt Kopf", _config.FirstPersonLook, v => _config.FirstPersonLook = v);
        changed |= Toggle("Position an Kopf binden", _config.CamBindPosition, v => _config.CamBindPosition = v);
        ImGui.SameLine();
        changed |= Toggle("Pitch invertieren", _config.CamInvertPitch, v => _config.CamInvertPitch = v);

        changed |= Drag("Auge hoch (m)", _config.CamEyeUp, 0.005f, -0.5f, 0.5f, v => _config.CamEyeUp = v, "%.3f");
        changed |= Drag("Auge vor (m)", _config.CamEyeFwd, 0.005f, -0.5f, 0.5f, v => _config.CamEyeFwd = v, "%.3f");
        changed |= Drag("Kamera hoch (m)", _config.CamUp, 0.01f, -3f, 3f, v => _config.CamUp = v);
        changed |= Drag("Kamera vor/zurück (m)", _config.CamBack, 0.01f, -3f, 3f, v => _config.CamBack = v);
        changed |= Drag("Yaw-Offset (°)", _config.CamYawOffsetDeg, 0.5f, -180f, 180f, v => _config.CamYawOffsetDeg = v, "%.1f");

        changed |= Drag("Kopf-Achse X (°)", _config.CamFixXDeg, 0.5f, -180f, 180f, v => _config.CamFixXDeg = v, "%.1f");
        changed |= Drag("Kopf-Achse Y (°)", _config.CamFixYDeg, 0.5f, -180f, 180f, v => _config.CamFixYDeg = v, "%.1f");
        changed |= Drag("Kopf-Achse Z (°)", _config.CamFixZDeg, 0.5f, -180f, 180f, v => _config.CamFixZDeg = v, "%.1f");
        return changed;
    }

    private bool DrawDebug()
    {
        bool changed = false;

        ImGui.Spacing();
        var dist = _config.DebugMaxDistance;
        if (ImGui.SliderFloat("Max. Entfernung (m)", ref dist, 2f, 30f))
        {
            _config.DebugMaxDistance = dist;
            changed = true;
        }
        return changed;
    }

    private static bool Toggle(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        return true;
    }

    private static bool Drag(string label, float v, float speed, float min, float max, Action<float> set, string fmt = "%.2f")
    {
        ImGui.SetNextItemWidth(160f);
        if (!ImGui.DragFloat(label, ref v, speed, min, max, fmt)) return false;
        set(Math.Clamp(v, min, max)); return true;
    }

    bool Axis(string label, int v, Action<int> set)
    {
        ImGui.SetNextItemWidth(80f);
        if (!ImGui.Combo(label, ref v, AxisNames, AxisNames.Length)) return false;
        set(v); return true;
    }
    public void Dispose()
    {
    }
}
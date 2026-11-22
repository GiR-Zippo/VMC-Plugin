using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using System;
using System.Linq;
using System.Numerics;
using VMCPlugin.BoneManipulation;
using VMCPlugin.Config;

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
        ("Body", b => b is "Hips" or "Spine" or "Chest" or "UpperChest" or "Neck" or "Head"),
        ("Arms",   b => b.EndsWith("Shoulder") || b.EndsWith("Arm") || b.EndsWith("Hand")),
        ("Finger", b => b.Contains("Proximal") || b.Contains("Intermediate")),
        ("Legs",  b => b.Contains("Leg") || b.EndsWith("Foot") || b.EndsWith("Toes")),
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
        "Hair",
        "Presets",
        "Emotes",
        "Face Tracking",
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
                    changed = DrawHair(_config);
                    break;
                case 3:
                    DrawPresets();
                    break;
                case 4:
                    DrawEmotes();
                    break;
                case 5:
                    DrawFace();
                    break;
                case 6:
                    DrawFPV();
                    break;
                case 7: // Debug
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
        if (ImGui.Button("+ New")) { ps.Add(new BonePreset { Name = $"Preset {ps.Count + 1}", Bones = new(p.Bones, StringComparer.OrdinalIgnoreCase) }); _config.ActivePreset = ps.Count - 1; ch = true; }
        ImGui.SameLine();
        if (ps.Count > 1 && ImGui.Button("Delete")) { ps.RemoveAt(_config.ActivePreset); _config.ActivePreset = 0; return true; }

        var name = p.Name;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputText("Name", ref name, 64)) { p.Name = name; ch = true; }

        if (ImGui.Button("All")) { p.Bones = new(VmcHeadReceiver.AllBones, StringComparer.OrdinalIgnoreCase); ch = true; }
        ImGui.Separator();
        foreach (var (gName, has) in _Groups)
        {
            var bones = VmcHeadReceiver.AllBones.Where(has).ToArray();
            var cnt = bones.Count(b => p.Bones.Contains(b));
            ImGui.PushID(gName);
            if (ImGui.CollapsingHeader($"{gName}  ({cnt}/{bones.Length})###h"))
            {
                if (ImGui.SmallButton("All")) { foreach (var b in bones) p.Bones.Add(b); ch = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("None")) { foreach (var b in bones) p.Bones.Remove(b); ch = true; }

                var l = bones.Where(b => b.StartsWith("Left")).ToArray();
                var r = bones.Where(b => b.StartsWith("Right")).ToArray();
                if (l.Length == 0)
                    foreach (var b in bones) ch |= BoneChk(p, b);
                else if (ImGui.BeginTable("##t", 2))
                {
                    ImGui.TableSetupColumn("Left");
                    ImGui.TableSetupColumn("Right");
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

    private static bool DrawHair(Configuration config)
    { 
        bool ch = false;
        var hp = config.HairPrefix;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputText("Haar-Bone-Prefixes", ref hp, 128))
        {
            config.HairPrefix = hp;
            ch = true;
        }
        ch |= Toggle("Hair-Springy", config.HairSpring, v => config.HairSpring = v);
        ch |= SliderF("Stiffnes", config.HairStiff, 10f, 200f, v => config.HairStiff = v, "%.0f");
        ch |= SliderF("Dampen", config.HairDamp, 1f, 30f, v => config.HairDamp = v, "%.1f");
        ch |= SliderF("Strongnes", config.HairStrength, 0f, 1f, v => config.HairStrength = v);
        ch |= Toggle("Haar-Springs only when idle", config.HairOnlyIdle, v => config.HairOnlyIdle = v);
        ch |= SliderF("Idle-Threshold (m/s)", config.HairIdleSpeed, 0.02f, 1f, v => config.HairIdleSpeed = v, "%.2f");
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
        if (ImGui.SliderFloat("Mocap-Portion", ref bl, 0f, 1f)) { config.RetargetBlend = bl; changed = true; }
        var fd = config.RetargetFadeSec;
        if (ImGui.SliderFloat("Fade (s)", ref fd, 0.05f, 2f)) { config.RetargetFadeSec = fd; changed = true; }
        var jd = config.JitterDeg;
        if (ImGui.SliderFloat("Jitter-Threshold (°)", ref jd, 0f, 2f, "%.2f")) { config.JitterDeg = jd; changed = true; }
        return changed;
    }

    private bool DrawEmotes()
    {
        var ch = false;
        var rules = _config.EmoteRules;
        var rm = -1;

        if (ImGui.BeginTable("##emotes", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Blendshape", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Live", ImGuiTableColumnFlags.WidthFixed, 45f);
            ImGui.TableSetupColumn("Threshold", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Hold/CD (s)", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                ImGui.PushID(5000 + i);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var on = r.Enabled;
                if (ImGui.Checkbox("##on", ref on)) { r.Enabled = on; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var bn = r.Blend;
                if (ImGui.InputText("##bn", ref bn, 64)) { r.Blend = bn; ch = true; }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(_vmcRecv.GetBlend(r.Blend).ToString("0.00"));

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var th = r.Threshold;
                if (ImGui.SliderFloat("##th", ref th, 0.05f, 1f, "%.2f")) { r.Threshold = th; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(60f);
                var hs = r.HoldSec;
                if (ImGui.DragFloat("##hs", ref hs, 0.05f, 0f, 5f, "%.2f")) { r.HoldSec = hs; ch = true; }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60f);
                var cd = r.CooldownSec;
                if (ImGui.DragFloat("##cd", ref cd, 0.1f, 0f, 60f, "%.1f")) { r.CooldownSec = cd; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var cmd = r.Command;
                if (ImGui.InputText("##cmd", ref cmd, 64)) { r.Command = cmd; ch = true; }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("x")) rm = i;

                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (rm >= 0) { rules.RemoveAt(rm); ch = true; }
        if (ImGui.Button("+ Emote")) { rules.Add(new EmoteRule()); ch = true; }
        return ch;
    }

    private bool DrawFace()
    {
        var ch = Toggle("Face-Tracking", _config.FaceTracking, v => _config.FaceTracking = v);
        
        if (ImGui.Button("Log Face-Bones")) _vmcRecv.DumpFaceBones();
        ImGui.SameLine();
        if (ImGui.Button("Load default (replaces)")) 
        {
            _config.FaceRules.Clear();
            _config.FaceRules.AddRange(FaceRule.Defaults());
            ch = true;
        }

        var rules = _config.FaceRules;
        var rm = -1;
        if (ImGui.BeginTable("##face", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Blendshape", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Live", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Bone", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Pos (m)", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("Rot (°)", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("T", ImGuiTableColumnFlags.WidthFixed, 26f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                ImGui.PushID(6000 + i);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var on = r.Enabled;
                if (ImGui.Checkbox("##on", ref on)) { r.Enabled = on; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var bn = r.Blend;
                if (ImGui.InputText("##bn", ref bn, 64)) { r.Blend = bn; ch = true; }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(_vmcRecv.GetBlend(r.Blend).ToString("0.00"));

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var bo = r.Bone;
                if (ImGui.InputText("##bo", ref bo, 64)) { r.Bone = bo; ch = true; }

                ImGui.TableNextColumn();
                var px = r.PosX; var py = r.PosY; var pz = r.PosZ;
                ImGui.SetNextItemWidth(55f); if (ImGui.DragFloat("##px", ref px, 0.0005f, -0.2f, 0.2f, "%.4f")) { r.PosX = px; ch = true; }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(55f); if (ImGui.DragFloat("##py", ref py, 0.0005f, -0.2f, 0.2f, "%.4f")) { r.PosY = py; ch = true; }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(55f); if (ImGui.DragFloat("##pz", ref pz, 0.0005f, -0.2f, 0.2f, "%.4f")) { r.PosZ = pz; ch = true; }

                ImGui.TableNextColumn();
                var rx = r.RotX; var ry = r.RotY; var rz = r.RotZ;
                ImGui.SetNextItemWidth(55f); if (ImGui.DragFloat("##rx", ref rx, 0.2f, -90f, 90f, "%.1f")) { r.RotX = rx; ch = true; }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(55f); if (ImGui.DragFloat("##ry", ref ry, 0.2f, -90f, 90f, "%.1f")) { r.RotY = ry; ch = true; }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(55f); if (ImGui.DragFloat("##rz", ref rz, 0.2f, -90f, 90f, "%.1f")) { r.RotZ = rz; ch = true; }

                ImGui.TableNextColumn();
                ImGui.Button("T");
                r.Force = ImGui.IsItemActive();   // gedrückt halten = Wert 1.0

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("x")) rm = i;

                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (rm >= 0) { rules.RemoveAt(rm); ch = true; }
        if (ImGui.Button("+ Regel")) { rules.Add(new FaceRule()); ch = true; }
        return ch;
    }

    private bool DrawFPV()
    {
        bool ch = false;
        ch |= Toggle("Enabled", _config.FirstPersonLook, v => _config.FirstPersonLook = v);

        ImGui.TextDisabled("Motion");
        ch |= Toggle("Reduce motion", _config.ReducedMotion, v => _config.ReducedMotion = v);

        ch |= Toggle("Reduce motion in combat and instanced content", _config.ReducedMotionInCombat, v => _config.ReducedMotionInCombat = v);
        ch |= Toggle("Allow full motion in emotes", _config.FullMotionInEmotes, v => _config.FullMotionInEmotes = v);
        ch |= Toggle("Remove camera roll in first person", _config.RemoveRollInFirstPerson, v => _config.RemoveRollInFirstPerson = v);

        ImGui.Spacing();
        ImGui.TextDisabled("First Person Adjustments");
        ch |= SliderF("Camera offset forward", _config.FirstPersonHeadOffsetForward, -0.5f, 0.5f, v => _config.FirstPersonHeadOffsetForward = v, "%.3f");
        ch |= SliderF("Camera offset up", _config.FirstPersonHeadOffsetUpward, -0.5f, 0.5f, v => _config.FirstPersonHeadOffsetUpward = v, "%.3f");
        ch |= SliderF("Camera offset sideways", _config.FirstPersonHeadOffsetSideward, -0.5f, 0.5f, v => _config.FirstPersonHeadOffsetSideward = v, "%.3f");

        ImGui.Spacing();
        ImGui.TextDisabled("Limits");
        ch |= SliderI("Head rotation pitch adjustment", _config.FirstPersonHeadRotationPitch, -90, 90, v => _config.FirstPersonHeadRotationPitch = v);
        ch |= SliderI("Maximum vertical angle", _config.FirstPersonHeadPitchLimit, 0, 180, v => _config.FirstPersonHeadPitchLimit = v);
        ch |= SliderI("Maximum horizontal angle", _config.FirstPersonHeadYawLimit, 0, 180, v => _config.FirstPersonHeadYawLimit = v);
        ch |= SliderI("Field of view in first person", _config.FirstPersonFieldOfView, CamController.MinFoV, CamController.MaxFoV, v => _config.FirstPersonFieldOfView = v, "%d");
        ch |= SliderF("Camera culling distance (m)", _config.FirstPersonCameraCullingDistance, 0f, 10f, v => _config.FirstPersonCameraCullingDistance = v, "%.1f");

        ImGui.Spacing();
        ImGui.TextDisabled("Smoothing");
        ch |= SliderF("yaw-Filter minCut", _config.yawFilterminCut, 0, 20, v => _config.yawFilterminCut = v);
        ch |= SliderF("yaw-Filter Beta", _config.yawFilterBeta, 0, 50, v => _config.yawFilterBeta = v);
        ch |= SliderF("pitch-Filter minCut", _config.pitchFilterminCut, 0, 20, v => _config.pitchFilterminCut = v);
        ch |= SliderF("pitch-Filter Beta", _config.pitchFilterBeta, 0, 50, v => _config.pitchFilterBeta = v);


        ImGui.Spacing();
        ImGui.TextDisabled("Misc");
        ch |= Toggle("Allow first person tracking in Gpose", _config.AllowInGpose, v => _config.AllowInGpose = v);
        ch |= Toggle("Enable third person camera control", _config.ThirdPersonControl, v => _config.ThirdPersonControl = v);
        ch |= Toggle("Use further camera for larger mounts", _config.UseFurtherCameraForLargerMounts, v => _config.UseFurtherCameraForLargerMounts = v);

        return ch;
    }

    private bool DrawDebug()
    {
        bool changed = false;
        changed |= Toggle("Emote-Log", _config.DebugEmotes, v => _config.DebugEmotes = v);
        return changed;
    }

    private static bool Toggle(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        return true;
    }

    private static bool SliderF(string label, float v, float min, float max, Action<float> set, string fmt = "%.2f")
    {
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.SliderFloat(label, ref v, min, max, fmt)) return false;
        set(v); return true;
    }

    private static bool SliderI(string label, int v, int min, int max, Action<int> set, string fmt = "%d°")
    {
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.SliderInt(label, ref v, min, max, fmt)) return false;
        set(v); return true;
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
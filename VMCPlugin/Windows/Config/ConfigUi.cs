using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VMCPlugin.BoneManipulation;
using VMCPlugin.Config;
using VMCPlugin.Offsets;

namespace VMCPlugin.Windows.Config;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private VmcHeadReceiver _vmcRecv;

    private string _selectedTab = "General";
    private readonly Dictionary<string, Func<bool>> _tabDrawers;

    private List<(uint Id, string Name)>? _emotes;
    private string _emoteFilter = "";

    private static class EnumCache<T> where T : struct, Enum
    {
        public static readonly T[] Vals = Enum.GetValues<T>();
        public static readonly string[] Names = Enum.GetNames<T>();   // gleiche Reihenfolge wie Vals
    }

    private static bool EnumCombo<T>(string id, ref int value) where T : struct, Enum
    {
        var target = value;   // Kopie für die Lambda
        var cur = Array.FindIndex(EnumCache<T>.Vals, v => Convert.ToInt32(v) == target);
        ImGui.SetNextItemWidth(130f);
        if (!ImGui.Combo(id, ref cur, EnumCache<T>.Names, EnumCache<T>.Names.Length) || cur < 0) return false;
        value = Convert.ToInt32(EnumCache<T>.Vals[cur]);
        return true;
    }

    private List<(uint Id, string Name)> Emotes(bool sortById = false)
    {
        if (_emotes != null) return _emotes;
        _emotes = new();
        if (Api.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>() is { } sheet)
            foreach (var e in sheet)
            {
                var n = e.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(n) || sortById) 
                    _emotes.Add((e.RowId, n));
            }
        if (sortById)
            _emotes.Sort((a, b) => a.Id.CompareTo(b.Id));
        else
            _emotes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return _emotes;
    }

    private bool EmoteCombo(PresetRule r, bool showIds = false)
    {
        var ch = false;
        var found = Emotes(showIds).Find(x => x.Id == r.Value);
        var cur = r.Value == 0 ? "(Emote wählen)"
            : found.Name == null ? $"#{r.Value}"
            : showIds ? $"{found.Name} ({r.Value})" : found.Name;

        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("##emote", cur))
        {
            ImGui.SetNextItemWidth(-60f);
            ImGui.InputTextWithHint("##flt", "Name oder ID…", ref _emoteFilter, 64);
            ImGui.SameLine();
            ImGui.Checkbox("IDs", ref showIds);

            foreach (var (id, name) in Emotes())
            {
                if (_emoteFilter.Length > 0
                    && !name.Contains(_emoteFilter, StringComparison.OrdinalIgnoreCase)
                    && !id.ToString().Contains(_emoteFilter)) continue;

                var label = showIds ? $"{id,4}  {name}##{id}" : $"{name}##{id}";
                if (ImGui.Selectable(label, id == r.Value)) { r.Value = (int)id; ch = true; }
            }
            ImGui.EndCombo();
        }
        return ch;
    }

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

    public ConfigWindow(Configuration config, IDalamudPluginInterface pi, VmcHeadReceiver vmcRecv) : base(
        $"VMC Configuration###VMCConfig")
    {
        Size = new Vector2(720f, 480f);
        SizeCondition = ImGuiCond.FirstUseEver;
        _config = config;
        _pi = pi;
        _vmcRecv = vmcRecv;

        _tabDrawers = new Dictionary<string, Func<bool>>
        {
            ["General"] = DrawGeneral,
            ["Preset Rules"] = DrawPresetRules,
            ["Presets"] = DrawPresets,
            ["VRM"] = DrawVmcChains,
            ["Remote"] = DrawRemoteSettings,
            ["Hair"] = DrawHair,
            ["Emotes"] = DrawEmotes,
            ["Face Tracking"] = DrawFace,
            ["Rotation Limits"] = DrawRotLimits,
            ["FPView"] = DrawFPV,
            ["Debug"] = DrawDebug,
        };
    }

    public override void PreDraw()
    {
        Flags &= ~ImGuiWindowFlags.NoMove;
    }


    public override void Draw()
    {
        var changed = false;

        // Linkes Side-Tab-Panel
        if (ImGui.BeginChild("##SideTabs", new Vector2(165f, 0f), true))
        {
            foreach (var name in _tabDrawers.Keys)
            {
                if (ImGui.Selectable(name, _selectedTab == name, ImGuiSelectableFlags.None, new Vector2(0f, 26f)))
                    _selectedTab = name;
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Rechter Inhaltsbereich
        if (ImGui.BeginChild("##TabContent", new Vector2(0f, 0f), true))
        {
            if (_tabDrawers.TryGetValue(_selectedTab, out var draw))
            {
                ImGui.TextUnformatted(_selectedTab);
                ImGui.Separator();
                ImGui.Spacing();

                changed = draw();
            }
        }
        ImGui.EndChild();

        if (changed)
            _pi.SavePluginConfig(_config);
    }

    private bool DrawGeneral()
    {
        bool ch = false;
        if (Toggle("Enable VMC", _config.VMCEnabled, v => _config.VMCEnabled = v))
        {
            _vmcRecv.VMCEnabled = _config.VMCEnabled;
            ch = true;
        }
        if (Toggle("Enable Remote", _config.RemoteEnabled, v => _config.RemoteEnabled = v))
        {
            _vmcRecv.RemoteEnabled = _config.RemoteEnabled;
            ch = true;
        }

        ch |= Toggle("Enable First Person View", _config.FirstPersonLook, v=> _config.FirstPersonLook =v);
        return ch;
    }

    private bool DrawPresetRules()
    {
        bool ch = false;
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Set Emote/Pose on top");
        //ImGui.TextUnformatted(_vmcRecv.StateText);   // zeigt die aktuellen Werte zum Eintragen

        var rules = _config.PresetRules;
        var rm = -1;
        var moveFrom = -1;
        var moveTo = -1;

        if (ImGui.BeginTable("##preset_rules", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Axis Lock", ImGuiTableColumnFlags.WidthFixed, 38f);
            ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("Order / Del", ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                ImGui.PushID(7000 + i);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ch |= Toggle("##on", r.Enabled, v => r.Enabled = v);

                ImGui.TableNextColumn();
                ch |= Toggle("##laxis", r.LimitAxis, v => r.LimitAxis = v);

                ImGui.TableNextColumn();
                var k = (int)r.Kind;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.Combo("##k", ref k, EnumCache<TriggerKind>.Names, EnumCache<TriggerKind>.Names.Length))
                {
                    r.Kind = (TriggerKind)k;
                    ch = true;
                }

                ImGui.TableNextColumn();
                var v = r.Value;
                ImGui.SetNextItemWidth(-1f);
                switch (r.Kind)
                {
                    case TriggerKind.Emote:
                        ch |= EmoteCombo(r);
                        break;
                    case TriggerKind.EmoteStart:
                        ch |= EmoteCombo(r, true);
                        if (r.ValueB != 255)
                        {
                            r.ValueB = 255;
                            ch = true;
                        }
                        break;
                    case TriggerKind.EmoteEnd:
                        ch |= EmoteCombo(r, true);
                        if (r.ValueB != 255)
                        {
                            r.ValueB = 255;
                            ch = true;
                        }
                        break;
                    case TriggerKind.Mode:
                        if (EnumCombo<FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes>("##v", ref v))
                        {
                            r.Value = v;
                            ch = true;
                        }
                        break;
                    case TriggerKind.Pose:
                        if (EnumCombo<FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.PoseType>("##v", ref v))
                        {
                            r.Value = v;
                            ch = true;
                        }
                        break;
                    default:
                        ImGui.TextDisabled("-");
                        break;
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##p", string.IsNullOrEmpty(r.Preset) ? "(Preset)" : r.Preset))
                {
                    foreach (var p in _config.Presets)
                        if (ImGui.Selectable(p.Name, p.Name == r.Preset)) { r.Preset = p.Name; ch = true; }
                    ImGui.EndCombo();
                }

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(i == 0);
                if (ImGui.SmallButton("^")) { moveFrom = i; moveTo = i - 1; }
                ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.BeginDisabled(i == rules.Count - 1);
                if (ImGui.SmallButton("v")) { moveFrom = i; moveTo = i + 1; }
                ImGui.EndDisabled();

                ImGui.SameLine();
                if (ImGui.SmallButton("x")) rm = i;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (moveFrom >= 0 && moveTo >= 0 && moveTo < rules.Count)
        {
            (rules[moveFrom], rules[moveTo]) = (rules[moveTo], rules[moveFrom]);
            ch = true;
        }
        if (rm >= 0) { rules.RemoveAt(rm); ch = true; }
        if (ImGui.Button("+ Regel##auto")) { rules.Add(new PresetRule()); ch = true; }

        return ch;
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

    private bool DrawRemoteSettings()
    {
        bool ch = false;
        var host = _config.RemoteIp ?? "127.0.0.1";
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputText("Host / Adresse", ref host, 128))
        {
            _config.RemoteIp = host.Trim();
            ch = true;
        }

        var port = _config.RemotePort;
        if (port <= 0) port = 40000;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Port", ref port))
        {
            _config.RemotePort = Math.Clamp(port, 1, 65535);
            ch = true;
        }
        if (ImGui.Button("Reconnect") && _config.RemoteEnabled)
        {
            _vmcRecv.RemoteEnabled = false;
            _vmcRecv.RemoteEnabled = true;
        }
        ch |= SliderF("Distance", _config.RemoteApplyMaxDistance, 10f, 100f, v => _config.RemoteApplyMaxDistance = v, "%.0f");

        return ch;
    }

    private bool DrawHair()
    { 
        bool ch = false;
        var hp = _config.HairPrefix;
        ImGui.SetNextItemWidth(220f);
        ch |= Toggle("Hair-Springy", _config.HairSpring, v => _config.HairSpring = v);
        if (ImGui.InputText("Haar-Bone-Prefixes", ref hp, 128))
        {
            _config.HairPrefix = hp;
            ch = true;
        }
        ch |= SliderF("Stiffnes", _config.HairStiff, 10f, 200f, v => _config.HairStiff = v, "%.0f");
        ch |= SliderF("Dampen", _config.HairDamp, 1f, 30f, v => _config.HairDamp = v, "%.1f");
        ch |= SliderF("Strongnes", _config.HairStrength, 0f, 1f, v => _config.HairStrength = v);
        ch |= Toggle("Haar-Springs only when idle", _config.HairOnlyIdle, v => _config.HairOnlyIdle = v);
        ch |= SliderF("Idle-Threshold (m/s)", _config.HairIdleSpeed, 0.02f, 1f, v => _config.HairIdleSpeed = v, "%.2f");
        return ch;
    }

    private static readonly string[] AxisNames = { "X", "Y", "Z" };
    private bool DrawVmcChains()
    {
        var changed = false;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260f);

        changed |= Toggle("VRM1", _config.Vrm1, v => _config.Vrm1 = v);

        var bl = _config.RetargetBlend;
        if (ImGui.SliderFloat("Mocap-Portion", ref bl, 0f, 1f)) { _config.RetargetBlend = bl; changed = true; }
        var fd = _config.RetargetFadeSec;
        if (ImGui.SliderFloat("Fade (s)", ref fd, 0.05f, 2f)) { _config.RetargetFadeSec = fd; changed = true; }
        var jd = _config.JitterDeg;
        if (ImGui.SliderFloat("Jitter-Threshold (°)", ref jd, 0f, 2f, "%.2f")) { _config.JitterDeg = jd; changed = true; }
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
        if (ImGui.Button("+ Rule")) { rules.Add(new FaceRule()); ch = true; }
        return ch;
    }

    private bool DrawRotLimits()
    {
        var ch = Toggle("Rotation-Limiter", _config.RotLimit, v => _config.RotLimit = v);

        var ts = _config.TwistShare;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat("Twist Hand→Lower Arm", ref ts, 0f, 1f, "%.2f")) { _config.TwistShare = ts; ch = true; }

        if (ImGui.Button("Load default (replaces)"))
        {
            _config.RotLimits.Clear();
            _config.RotLimits.AddRange(RotLimitRule.Defaults());
            ch = true;
        }

        var rules = _config.RotLimits;
        var rm = -1;
        if (ImGui.BeginTable("##rotlim", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Bone", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Swing (°)", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Twist (°)", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                ImGui.PushID(7000 + i);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var on = r.Enabled;
                if (ImGui.Checkbox("##on", ref on)) { r.Enabled = on; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var bn = r.Bone;
                if (ImGui.InputText("##bn", ref bn, 64)) { r.Bone = bn; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var sw = r.Swing;
                if (ImGui.DragFloat("##sw", ref sw, 0.5f, 1f, 180f, "%.0f")) { r.Swing = sw; ch = true; }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                var tw = r.Twist;
                if (ImGui.DragFloat("##tw", ref tw, 0.5f, 1f, 180f, "%.0f")) { r.Twist = tw; ch = true; }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("x")) rm = i;

                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (rm >= 0) { rules.RemoveAt(rm); ch = true; }
        if (ImGui.Button("+ Rule")) { rules.Add(new RotLimitRule()); ch = true; }

        if (ch) _vmcRecv.RebuildLimits();
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

        ch |= SliderF("Max pitch Up", _config.MaxPitchUp, 0f, 3f, v => _config.MaxPitchUp = v, "%.1f");
        ch |= SliderF("Max pitch Down", _config.MaxPitchDown, -3f, 0f, v => _config.MaxPitchDown = v, "%.1f");

        ImGui.Spacing();
        ImGui.TextDisabled("Smoothing");
        ch |= SliderF("yaw-Filter minCut", _config.yawFilterminCut, 0, 20, v => _config.yawFilterminCut = v);
        ch |= SliderF("yaw-Filter Beta", _config.yawFilterBeta, 0, 50, v => _config.yawFilterBeta = v);
        ch |= SliderF("pitch-Filter minCut", _config.pitchFilterminCut, 0, 20, v => _config.pitchFilterminCut = v);
        ch |= SliderF("pitch-Filter Beta", _config.pitchFilterBeta, 0, 50, v => _config.pitchFilterBeta = v);

        ImGui.Spacing();
        ImGui.TextDisabled("Multiplier");
        ch |= SliderF("pitch-Multiplier", _config.pitchMultiply, 0, 10, v => _config.pitchMultiply = v);
        ch |= SliderF("yaw-Multiplier", _config.yawMultiply, 0, 2, v => _config.yawMultiply = v);

        ImGui.Spacing();
        ImGui.TextDisabled("Misc");
        ch |= Toggle("Allow first person tracking in Gpose", _config.AllowInGpose, v => _config.AllowInGpose = v);
        ch |= Toggle("Enable third person camera control", _config.ThirdPersonControl, v => _config.ThirdPersonControl = v);
        ch |= Toggle("Use further camera for larger mounts", _config.UseFurtherCameraForLargerMounts, v => _config.UseFurtherCameraForLargerMounts = v);

        ImGui.Spacing();
        ImGui.TextDisabled("Turn character");
        ch |= Toggle("Turn character on extreme head turn (Standing)", _config.TurnBodyStanding, v => _config.TurnBodyStanding = v);
        ch |= Toggle("Turn character on extreme head turn (Walking)", _config.TurnBodyWalking, v => _config.TurnBodyWalking = v);
        ch |= SliderF("Start angle (°)", _config.TurnStartDeg, 20f, 100f, v => _config.TurnStartDeg = v, "%.0f");
        ch |= SliderF("Max turn speed (°/s)", _config.TurnSpeedDeg, 10f, 180f, v => _config.TurnSpeedDeg = v, "%.0f");
        ch |= SliderF("Center offset (°)", _config.TurnCenterDeg, -45f, 45f, v => _config.TurnCenterDeg = v, "%.1f");
        return ch;
    }

    private bool DrawDebug()
    {
        bool changed = false;
        changed |= Toggle("Emote-Log", _config.DebugEmotes, v => _config.DebugEmotes = v);
        return changed;
    }

    public static bool Toggle(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        return true;
    }

    public static bool SliderF(string label, float v, float min, float max, Action<float> set, string fmt = "%.2f")
    {
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.SliderFloat(label, ref v, min, max, fmt)) return false;
        set(v); return true;
    }

    public static bool SliderI(string label, int v, int min, int max, Action<int> set, string fmt = "%d°")
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
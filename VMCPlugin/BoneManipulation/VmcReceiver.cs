using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using VMCPlugin.Config;
using ObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace VMCPlugin.BoneManipulation;

public sealed unsafe class VmcHeadReceiver : IDisposable
{
    private CamController cam;

    private readonly record struct RNode(string Vmc, string Xiv, string? Parent, bool Lift, float Sx);

    private static readonly RNode[] RetargetNodes = BuildNodes();
    public static readonly string[] AllBones = RetargetNodes.Select(n => n.Vmc).ToArray(); // using System.Linq

    private HashSet<string>? ActiveSet => config.Presets.Count == 0 ? null
        : config.Presets[Math.Clamp(config.ActivePreset, 0, config.Presets.Count - 1)].Bones;

    private static readonly Dictionary<string, string> RetargetKeys = BuildKeyMap();

    private static RNode[] BuildNodes()
    {
        var l = new List<RNode>
    {
        new("Hips", "n_hara", null, false, 0),
        new("Spine", "j_sebo_a", "Hips", false, 0),
        new("Chest", "j_sebo_b", "Spine", false, 0),
        new("UpperChest", "j_sebo_c", "Chest", false, 0),
        new("Neck", "j_kubi", "UpperChest", false, 0),
        new("Head", "j_kao", "Neck", false, 0),
    };
        foreach (var (side, s, sx) in new[] { ("Left", "l", 1f), ("Right", "r", -1f) })
        {
            l.Add(new($"{side}Shoulder", $"j_sako_{s}", "UpperChest", false, sx));
            l.Add(new($"{side}UpperArm", $"j_ude_a_{s}", $"{side}Shoulder", true, sx));
            l.Add(new($"{side}LowerArm", $"j_ude_b_{s}", $"{side}UpperArm", true, sx));
            l.Add(new($"{side}Hand", $"j_te_{s}", $"{side}LowerArm", true, sx));
            foreach (var (v, x) in new[] { ("Thumb", "oya"), ("Index", "hito"), ("Middle", "naka"), ("Ring", "kusu"), ("Little", "ko") })
            {
                l.Add(new($"{side}{v}Proximal", $"j_{x}_a_{s}", $"{side}Hand", true, sx));
                l.Add(new($"{side}{v}Intermediate", $"j_{x}_b_{s}", $"{side}{v}Proximal", true, sx));
            }
            l.Add(new($"{side}UpperLeg", $"j_asi_a_{s}", "Hips", false, sx));
            l.Add(new($"{side}LowerLeg", $"j_asi_b_{s}", $"{side}UpperLeg", false, sx));
            l.Add(new($"{side}Foot", $"j_asi_c_{s}", $"{side}LowerLeg", false, sx));
            l.Add(new($"{side}Toes", $"j_asi_d_{s}", $"{side}Foot", false, sx));
        }
        return l.ToArray();
    }

    private static Dictionary<string, string> BuildKeyMap()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in BuildNodes()) d[n.Vmc] = n.Vmc;
        return d;
    }

    // Unity (links-händig) → XIV (rechts-händig): Achse spiegeln
    private Quaternion ToXiv(Quaternion q) => config.Vrm1
        ? new Quaternion(q.X, -q.Y, -q.Z, q.W)    // VRM 1.0: schaut +Z, links = -X → X spiegeln
        : new Quaternion(-q.X, -q.Y, q.Z, q.W);   // VRM 0.x: schaut -Z → Z spiegeln

    private static Quaternion RemoveYaw(Quaternion q)
    {
        var twist = new Quaternion(0, q.Y, 0, q.W);
        if (twist.LengthSquared() < 1e-8f) return q;
        return Quaternion.Normalize(q * Quaternion.Conjugate(Quaternion.Normalize(twist)));
    }
    private const double BoneTimeoutSeconds = 1.0;//0.3;   // ab hier gilt ein Bone als "ohne Update"
    private readonly Dictionary<string, double> latestBoneTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> retargetW = new(StringComparer.OrdinalIgnoreCase);
    private double lastRetargetT;

    private readonly Dictionary<string, Quaternion> latestBoneRots = new(StringComparer.OrdinalIgnoreCase);

    private const double PacketTimeoutSeconds = 0.5;

    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly object sync = new();
    private UdpClient? udp;
    private CancellationTokenSource? cts;
    private Thread? recvThread;

    private bool enabled;
    private int port = 39539;

    private double lastPacketUtcSec;
    private bool hasPacket;
    private bool disposed;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            if (enabled) StartListener(port);
            else StopListener();
        }
    }

    public int Port
    {
        get => port;
        set
        {
            var clamped = Math.Clamp(value, 1024, 65535);
            if (port == clamped) return;
            port = clamped;
            if (enabled)
            {
                StopListener();
                StartListener(port);
            }
        }
    }

    public VmcHeadReceiver(IObjectTable objects, IPluginLog log, Configuration conf, CamController cam)
    {
        this.objects = objects;
        this.log = log;
        this.config = conf;
        this.cam = cam;
    }

    public void StartListener(int listenPort)
    {
        StopListener();
        if (disposed) return;

        try
        {
            cts = new CancellationTokenSource();
            udp = new UdpClient(listenPort);
            udp.Client.ReceiveTimeout = 250;

            var token = cts.Token;
            recvThread = new Thread(() => ReceiveLoop(token))
            {
                IsBackground = true,
                Name = "VmcHeadReceiver"
            };
            recvThread.Start();

            log.Information($"VMC Receiver gestartet auf UDP Port {listenPort}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"VMC Receiver konnte Port {listenPort} nicht öffnen.");
        }
    }

    public void StopListener()
    {
        try { cts?.Cancel(); } catch { }
        try { udp?.Close(); udp?.Dispose(); } catch { }
        udp = null;
        cts?.Dispose();
        cts = null;
        recvThread = null;
    }

    private void ReceiveLoop(CancellationToken token)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested && udp != null)
        {
            try
            {
                var data = udp.Receive(ref remote);
                if (data == null || data.Length == 0) continue;
                ParseOscPacket(data);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.Interrupted)
            {
                // Normales Polling-Timeout zum sauberen Beenden
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    log.Warning(ex, "VMC: Fehler beim Empfangen eines OSC-Pakets.");
            }
        }
    }

    // Unterstützt einzelne OSC-Messages sowie OSC-Bundles (#bundle)
    private void ParseOscPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return;

        if (data.Length >= 16 &&
            data[0] == '#' && data[1] == 'b' && data[2] == 'u' && data[3] == 'n' &&
            data[4] == 'd' && data[5] == 'l' && data[6] == 'e')
        {
            // OSC-Bundle: "#bundle\0" (8 Bytes) + Timetag (8 Bytes), danach 4-Byte Big-Endian-Länge + Element
            var pos = 16;
            while (pos + 4 <= data.Length)
            {
                var elemLen = ReadInt32BE(data[pos..]);
                pos += 4;
                if (elemLen <= 0 || pos + elemLen > data.Length) break;
                ParseOscMessage(data.Slice(pos, elemLen));
                pos += elemLen;
            }
            return;
        }

        ParseOscMessage(data);
    }

    private void ParseOscMessage(ReadOnlySpan<byte> msg)
    {
        if (!TryReadOscString(msg, 0, out var address, out var idx)) return;
        if (!address.Equals("/VMC/Ext/Bone/Pos", StringComparison.OrdinalIgnoreCase)) return;
        if (!TryReadOscString(msg, idx, out var tags, out idx)) return;
        if (!tags.StartsWith(",", StringComparison.Ordinal)) return;

        string? boneName = null;
        float qx = 0, qy = 0, qz = 0, qw = 1f;
        var floatIndex = 0;

        for (var t = 1; t < tags.Length; t++)
        {
            var tag = tags[t];
            if (tag == 's')
            {
                if (!TryReadOscString(msg, idx, out var sVal, out idx)) return;
                if (boneName == null) boneName = sVal;
            }
            else if (tag == 'f')
            {
                if (idx + 4 > msg.Length) return;
                var fVal = ReadSingleBE(msg[idx..]);
                idx += 4;

                // Float 0..2 = px, py, pz | Float 3..6 = qx, qy, qz, qw
                switch (floatIndex++)
                {
                    case 3: qx = fVal; break;
                    case 4: qy = fVal; break;
                    case 5: qz = fVal; break;
                    case 6: qw = fVal; break;
                }
            }
            else if (tag == 'i' || tag == 'd')
            {
                var step = tag == 'd' ? 8 : 4;
                if (idx + step > msg.Length) return;
                idx += step;
            }
            else
            {
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(boneName)) return;
        StoreBoneRotation(boneName.Trim(), qx, qy, qz, qw);
    }

    private void StoreBoneRotation(string boneName, float qx, float qy, float qz, float qw)
    {
        if (!TryGetConfiguredSourceKey(boneName, out var canonicalKey))
            return;

        var rot = new Quaternion(qx, qy, qz, qw);
        if (rot.LengthSquared() < 1e-6f) return;
        rot = Quaternion.Normalize(rot);

        var now = GetNowSeconds();

        lock (sync)
        {
            hasPacket = true;
            lastPacketUtcSec = now;   // Stream bleibt "frisch", auch wenn das Paket verworfen wird

            if (latestBoneRots.TryGetValue(canonicalKey, out var prev)
                && MathF.Abs(Quaternion.Dot(prev, rot)) > MathF.Cos(config.JitterDeg * MathF.PI / 360f))
                return;   // Mikroruckler verwerfen

            latestBoneRots[canonicalKey] = rot;
            latestBoneTimes[canonicalKey] = now;
        }
    }

    private bool TryGetConfiguredSourceKey(string incomingBoneName, out string canonicalKey)
    {
        return RetargetKeys.TryGetValue(incomingBoneName, out canonicalKey);
    }

    private bool ApplyFullRetarget(nint charAddress, Dictionary<string, Quaternion> snap, Dictionary<string, double> times)
    {
        var chara = (Character*)charAddress;
        var draw = chara->GameObject.DrawObject;
        if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return false;
        var skel = ((CharacterBase*)draw)->Skeleton;
        if (skel == null) return false;

        var basePose = skel->PartialSkeletons[0].GetHavokPose(0);
        if (basePose == null || basePose->Skeleton == null) return false;
        var hk = basePose->Skeleton;
        var refRot = BuildRefModelRots(hk);

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < hk->Bones.Length; i++)
            index[hk->Bones[i].Name.String ?? string.Empty] = i;

        // A→T-Lift pro Seite aus der Referenzpose (Bone-Richtung = lokale X-Achse)
        Quaternion LiftFor(string upperArm, float sx)
        {
            if (!index.TryGetValue(upperArm, out var u)) return Quaternion.Identity;
            var dir = Vector3.Transform(Vector3.UnitX, refRot[u]);
            return FromTo(dir, new Vector3(sx, 0, 0));
        }
        var liftL = LiftFor("j_ude_a_l", 1f);
        var liftR = LiftFor("j_ude_a_r", -1f);

        var model = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);

        var now = GetNowSeconds();
        var dt = (float)Math.Clamp(now - lastRetargetT, 0.0, 0.1);
        lastRetargetT = now;
        var anyActive = false;
        var active = ActiveSet;
        foreach (var n in RetargetNodes)
        {
            var hasData = snap.TryGetValue(n.Vmc, out var raw);
            var local = hasData ? ToXiv(raw) : Quaternion.Identity;
            if (n.Vmc == "Hips") local = RemoveYaw(local);   // Zeile löschen, um das Körper-Yaw zu behalten

            var parentRot = n.Parent != null && model.TryGetValue(n.Parent, out var pr) ? pr : Quaternion.Identity;
            var m = Quaternion.Normalize(parentRot * local);
            model[n.Vmc] = m;

            var fresh = times.TryGetValue(n.Vmc, out var ts) && now - ts <= BoneTimeoutSeconds;
            var off = active != null && !active.Contains(n.Vmc);
            var goal = hasData && fresh && !off ? Math.Clamp(config.RetargetBlend, 0f, 1f) : 0f;
            retargetW.TryGetValue(n.Vmc, out var w);
            var step = dt / MathF.Max(0.01f, config.RetargetFadeSec);
            w = w < goal ? MathF.Min(goal, w + step) : MathF.Max(goal, w - step);
            retargetW[n.Vmc] = w;
            if (w > 0.001f) anyActive = true;
            if (w <= 0.001f || !index.TryGetValue(n.Xiv, out var b)) continue;

            var v = Quaternion.Normalize(m);
            var rest = n.Lift ? (n.Sx > 0 ? liftL : liftR) * refRot[b] : refRot[b];
            var target = Quaternion.Normalize(v * rest);

            var r = basePose->ModelPose[b].Rotation;
            var cur = new Quaternion(r.X, r.Y, r.Z, r.W);
            var delta = Quaternion.Normalize(target * Quaternion.Conjugate(cur));
            if (w < 0.999f) delta = Quaternion.Slerp(Quaternion.Identity, delta, w);

            var t = basePose->ModelPose[b].Translation;
            RotateByName(skel, n.Xiv, new Vector3(t.X, t.Y, t.Z), delta);
        }

        if (config.FirstPersonLook)
        {
            if (skel->PartialSkeletonCount > 1)
            {
                var hp = skel->PartialSkeletons[1].GetHavokPose(0);
                if (hp != null && hp->Skeleton != null && hp->Skeleton->Bones.Length > 0)
                {
                    var tr = &hp->ModelPose.Data[0];   // Bone 0 = j_kao im Kopf-Skeleton
                    cam.SetVmcHead(
                        new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W),
                        new Vector3(tr->Translation.X, tr->Translation.Y, tr->Translation.Z));
                }
            }
        }

        return anyActive;
    }

    private static Quaternion[] BuildRefModelRots(hkaSkeleton* hk)
    {
        var r = new Quaternion[hk->Bones.Length];
        for (var i = 0; i < r.Length; i++)
        {
            var q = hk->ReferencePose[i].Rotation;
            var local = new Quaternion(q.X, q.Y, q.Z, q.W);
            var p = hk->ParentIndices[i];
            r[i] = p >= 0 ? Quaternion.Normalize(r[p] * local) : Quaternion.Normalize(local);
        }
        return r;
    }

    private static Quaternion FromTo(Vector3 d, Vector3 t)
    {
        var axis = Vector3.Cross(d, t);
        var len = axis.Length();
        if (len < 1e-5f) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(axis / len, MathF.Atan2(len, Vector3.Dot(d, t)));
    }

    // Subtree über ALLE Partials (Haare, Gesicht, Gear), wie beim Kopf-Fix
    private static void RotateByName(FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* skel,
        string boneName, Vector3 pivot, Quaternion q)
    {
        var subtree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var pose = skel->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;
            var hk = pose->Skeleton;
            for (var b = 0; b < hk->Bones.Length; b++)
            {
                if (hk->Bones[b].Name.String != boneName) continue;
                for (var i = 0; i < hk->Bones.Length; i++)
                    if (IsSelfOrDescendant(hk, i, b))
                        subtree.Add(hk->Bones[i].Name.String ?? string.Empty);
            }
        }

        for (var p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var pose = skel->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;
            var hk = pose->Skeleton;
            for (var i = 0; i < hk->Bones.Length; i++)
            {
                if (!InSubtree(hk, i, subtree)) continue;
                var tr = &pose->ModelPose.Data[i];
                var nt = pivot + Vector3.Transform(
                    new Vector3(tr->Translation.X, tr->Translation.Y, tr->Translation.Z) - pivot, q);
                tr->Translation.X = nt.X; tr->Translation.Y = nt.Y; tr->Translation.Z = nt.Z;
                var nr = Quaternion.Normalize(
                    q * new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W));
                tr->Rotation.X = nr.X; tr->Rotation.Y = nr.Y; tr->Rotation.Z = nr.Z; tr->Rotation.W = nr.W;
            }
        }
    }

    // Im Render-Hook aufrufen (z. B. vor oder nach DeformCharacter)
    public void Apply()
    {
        if (!enabled) return;

        bool isFresh;
        Dictionary<string, Quaternion> boneSnapshot;
        Dictionary<string, double> timeSnapshot;
        lock (sync)
        {
            isFresh = hasPacket && (GetNowSeconds() - lastPacketUtcSec <= PacketTimeoutSeconds);
            boneSnapshot = new Dictionary<string, Quaternion>(latestBoneRots, StringComparer.OrdinalIgnoreCase);
            timeSnapshot = new Dictionary<string, double>(latestBoneTimes, StringComparer.OrdinalIgnoreCase);
        }
        if (!isFresh) return;

        var lp = objects.LocalPlayer;
        if (lp == null) return;

        ApplyFullRetarget(lp.Address, boneSnapshot, timeSnapshot);
        if (!isFresh) return;   // Chain-Regeln (Jaw/Augen) nur mit frischem Stream
    }

    private static Quaternion AdjustAxes(Quaternion q, bool invPitch, bool invYaw, bool invRoll)
    {
        // Einfache Achsen-Spiegelung (X = Pitch, Y = Yaw, Z = Roll)
        var x = invPitch ? -q.X : q.X;
        var y = invYaw ? -q.Y : q.Y;
        var z = invRoll ? -q.Z : q.Z;
        return Quaternion.Normalize(new Quaternion(x, y, z, q.W));
    }

    private static void ApplyRotationToChain(nint charAddress, string[] chainBones, Quaternion deltaRot, float scale)
    {
        if (charAddress == 0 || chainBones.Length == 0) return;

        var chara = (Character*)charAddress;
        var draw = chara->GameObject.DrawObject;
        if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var cb = (CharacterBase*)draw;
        var skel = cb->Skeleton;
        if (skel == null) return;

        var w = Quaternion.Slerp(Quaternion.Identity, deltaRot, 1f / chainBones.Length);
        var sinHalf = MathF.Sqrt(MathF.Max(0f, 1f - w.W * w.W));
        if (sinHalf < 1e-4f) return;
        var axis = Vector3.Normalize(new Vector3(w.X, w.Y, w.Z) / sinHalf);
        var q = Quaternion.CreateFromAxisAngle(axis, 2f * MathF.Acos(Math.Clamp(w.W, -1f, 1f)) * scale);

        for (var c = 0; c < chainBones.Length; c++)
        {
            var boneName = chainBones[c];
            if (string.IsNullOrWhiteSpace(boneName)) continue;
            var isLastInChain = c == chainBones.Length - 1;

            // 1) Pivot + Namen des Subtrees über ALLE Partials sammeln (vor der Änderung)
            var subtree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Vector3? pivot = null;
            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null) continue;
                var hk = pose->Skeleton;
                for (var b = 0; b < hk->Bones.Length; b++)
                {
                    if (hk->Bones[b].Name.String != boneName) continue;
                    if (pivot == null)
                    {
                        var pt = pose->ModelPose[b].Translation;
                        pivot = new Vector3(pt.X, pt.Y, pt.Z);
                    }
                    for (var i = 0; i < hk->Bones.Length; i++)
                        if (IsSelfOrDescendant(hk, i, b))
                            subtree.Add(hk->Bones[i].Name.String ?? string.Empty);
                }
            }
            if (pivot == null) continue;
            var pv = pivot.Value;

            // 2) Auf alle Partials anwenden, deren Bones (oder Vorfahren) im Subtree liegen
            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null) continue;
                var hk = pose->Skeleton;

                for (var i = 0; i < hk->Bones.Length; i++)
                {
                    if (!InSubtree(hk, i, subtree)) continue;
                    var tr = &pose->ModelPose.Data[i];
                    var nt = pv + Vector3.Transform(
                        new Vector3(tr->Translation.X, tr->Translation.Y, tr->Translation.Z) - pv, q);
                    tr->Translation.X = nt.X; tr->Translation.Y = nt.Y; tr->Translation.Z = nt.Z;

                    var nr = Quaternion.Normalize(
                        q * new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W));
                    tr->Rotation.X = nr.X; tr->Rotation.Y = nr.Y; tr->Rotation.Z = nr.Z; tr->Rotation.W = nr.W;
                }
            }
        }
    }

    private static bool InSubtree(hkaSkeleton* hk, int i, HashSet<string> set)
    {
        for (var n = 0; i >= 0 && n < 256; n++)
        {
            if (set.Contains(hk->Bones[i].Name.String ?? string.Empty)) return true;
            i = hk->ParentIndices[i];
        }
        return false;
    }

    private static bool IsSelfOrDescendant(hkaSkeleton* hk, int i, int root)
    {
        for (var n = 0; i >= 0 && n < 256; n++)
        {
            if (i == root) return true;
            i = hk->ParentIndices[i];
        }
        return false;
    }

    private static bool TryReadOscString(ReadOnlySpan<byte> buf, int offset, out string value, out int nextOffset)
    {
        value = string.Empty;
        nextOffset = offset;
        if (offset < 0 || offset >= buf.Length) return false;

        var end = -1;
        for (var i = offset; i < buf.Length; i++)
        {
            if (buf[i] == 0)
            {
                end = i;
                break;
            }
        }
        if (end < 0) return false;

        value = Encoding.UTF8.GetString(buf[offset..end]);
        // OSC-Strings sind auf 4-Byte-Grenzen gepadded (inkl. Null-Byte)
        nextOffset = (end + 4) & ~3;
        return true;
    }

    private static int ReadInt32BE(ReadOnlySpan<byte> s)
        => (s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3];

    private static float ReadSingleBE(ReadOnlySpan<byte> s)
    {
        var raw = ReadInt32BE(s);
        return BitConverter.Int32BitsToSingle(raw);
    }

    private static double GetNowSeconds()
        => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopListener();
    }
}
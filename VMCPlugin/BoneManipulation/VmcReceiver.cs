using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using System;
using System.Collections.Concurrent;
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

public unsafe partial class VmcHeadReceiver : IDisposable
{
    private CamController cam;

    //--- Face Stuff
    private readonly Dictionary<string, List<(int p, int b)>> faceIdx = new(StringComparer.OrdinalIgnoreCase);
    private nint faceSkel;
    private int faceSig;
    //---


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

    private readonly Dictionary<string, float> blends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EmoteRule, (double since, double fired)> emoteState = new();
    public readonly ConcurrentQueue<string> Commands = new();
    private readonly Dictionary<string, float> loggedBlends = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the latest received blend shape value for the specified name.
    /// </summary>
    /// <param name="n">The blend shape name.</param>
    /// <returns>The current blend value, or <c>0f</c> if not found.</returns>
    public float GetBlend(string n) { lock (sync) return blends.TryGetValue(n, out var v) ? v : 0f; }

    private readonly Dictionary<FaceRule, float> faceSm = new();
    private double lastFaceT;

    /// <summary>
    /// Converts a Unity quaternion into Final Fantasy XIV's coordinate space based on the configured VRM version.
    /// </summary>
    /// <param name="q">The source rotation quaternion in Unity coordinates.</param>
    /// <returns>The transformed rotation quaternion in FFXIV coordinates.</returns>
    private Quaternion ToXiv(Quaternion q) => config.Vrm1
        ? new Quaternion(q.X, -q.Y, -q.Z, q.W)    // VRM 1.0: schaut +Z, links = -X → X spiegeln
        : new Quaternion(-q.X, -q.Y, q.Z, q.W);   // VRM 0.x: schaut -Z → Z spiegeln

    /// <summary>
    /// Removes the yaw (Y-axis twist) component from a quaternion while preserving its remaining orientation.
    /// </summary>
    /// <param name="q">The input quaternion.</param>
    /// <returns>The normalized quaternion with yaw removed, or the original quaternion if the twist is near zero.</returns>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="VmcHeadReceiver"/> class.
    /// </summary>
    /// <param name="objects">The Dalamud object table service used to access the local player.</param>
    /// <param name="log">The Dalamud plugin log service.</param>
    /// <param name="conf">The active plugin configuration.</param>
    /// <param name="cam">The camera controller used for first-person head tracking updates.</param>
    public VmcHeadReceiver(IObjectTable objects, IPluginLog log, Configuration conf, CamController cam)
    {
        this.objects = objects;
        this.log = log;
        this.config = conf;
        this.cam = cam;
    }

    #region Network
    /// <summary>
    /// Starts or restarts the background UDP listener on the specified port to receive VMC OSC packets.
    /// </summary>
    /// <param name="listenPort">The UDP port number to listen on.</param>
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

    /// <summary>
    /// Stops the background receive thread and closes/disposes the UDP socket and cancellation token source.
    /// </summary>
    public void StopListener()
    {
        try { cts?.Cancel(); } catch { }
        try { udp?.Close(); udp?.Dispose(); } catch { }
        udp = null;
        cts?.Dispose();
        cts = null;
        recvThread = null;
    }

    /// <summary>
    /// Continuously polls the UDP socket for incoming OSC data and dispatches it for parsing until cancelled or disposed.
    /// </summary>
    /// <param name="token">Cancellation token used to stop the receive loop cleanly.</param>
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

    /// <summary>
    /// Parses a raw OSC packet, handling both standalone OSC messages and OSC bundles (<c>#bundle</c>).
    /// </summary>
    /// <param name="data">The raw byte span of the incoming OSC packet.</param>
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

    /// <summary>
    /// Parses a single OSC message and updates blend values or bone rotations for supported VMC addresses.
    /// </summary>
    /// <param name="msg">The raw byte span of a single OSC message.</param>
    private void ParseOscMessage(ReadOnlySpan<byte> msg)
    {
        if (!TryReadOscString(msg, 0, out var address, out var idx)) return;

        if (!TryReadOscString(msg, idx, out var tags, out idx)) return;
        if (!tags.StartsWith(",", StringComparison.Ordinal)) return;

        if (address.Equals("/VMC/Ext/Blend/Val", StringComparison.OrdinalIgnoreCase))
        {
            if (tags.StartsWith(",sf")
                && TryReadOscString(msg, idx, out var bn, out idx) && idx + 4 <= msg.Length)
            {
                var name = bn.Trim();
                var val = ReadSingleBE(msg[idx..]);
                lock (sync) blends[name] = val;

                if (config.DebugEmotes && (!loggedBlends.TryGetValue(name, out var lv) || MathF.Abs(val - lv) >= 0.1f))
                {
                    loggedBlends[name] = val;
                    log.Debug($"VMC Blend: {name} = {val:0.00}");
                }
            }
            return;
        }

        if (address.Equals("/VMC/Ext/Bone/Pos", StringComparison.OrdinalIgnoreCase))
        {
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
    }
    #endregion

    #region NetworkFunction
    /// <summary>
    /// Validates, normalizes, jitter-filters, and stores a received bone rotation along with its timestamp.
    /// </summary>
    /// <param name="boneName">The incoming VMC bone name.</param>
    /// <param name="qx">The X component of the rotation quaternion.</param>
    /// <param name="qy">The Y component of the rotation quaternion.</param>
    /// <param name="qz">The Z component of the rotation quaternion.</param>
    /// <param name="qw">The W component of the rotation quaternion.</param>
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

    /// <summary>
    /// Resolves an incoming bone name to its canonical retargeting key.
    /// </summary>
    /// <param name="incomingBoneName">The bone name received from VMC.</param>
    /// <param name="canonicalKey">When found, outputs the canonical retarget key.</param>
    /// <returns><c>true</c> if the bone name is mapped for retargeting; otherwise, <c>false</c>.</returns>
    private bool TryGetConfiguredSourceKey(string incomingBoneName, out string canonicalKey)
    {
        return RetargetKeys.TryGetValue(incomingBoneName, out canonicalKey);
    }
    #endregion

    #region Bonemanipulation
    /// <summary>
    /// Applies active VMC bone retargeting and optional facial tracking to the local player during render updates.
    /// </summary>
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

        if (config.FaceTracking)
            ApplyFace(lp.Address, isFresh);

        if (config.HairSpring)
        {
            UpdateIdle(lp.Position);
            ApplyHair(lp.Address);
        }

        if (!isFresh) return;   // Chain-Regeln (Jaw/Augen) nur mit frischem Stream
    }

    /// <summary>
    /// Applies full-body VMC bone retargeting (including A-to-T lift compensation, blending, and optional first-person head camera updates) to the character's skeleton.
    /// </summary>
    /// <param name="charAddress">Native pointer address of the target <see cref="Character"/>.</param>
    /// <param name="snap">Snapshot of the latest received bone rotations keyed by VMC bone name.</param>
    /// <param name="times">Snapshot of the timestamps when each bone last received an update.</param>
    /// <returns><c>true</c> if any bone has an active retarget weight above the threshold; otherwise, <c>false</c>.</returns>
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

        //Process when FPV is enabled
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

    /// <summary>
    /// Computes model-space reference pose rotations for every bone in a Havok skeleton.
    /// </summary>
    /// <param name="hk">Pointer to the Havok skeleton.</param>
    /// <returns>An array of model-space reference rotations indexed by bone index.</returns>
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

    /// <summary>
    /// Rotates a named bone and its descendants across all partial skeletons in the character's render skeleton.
    /// </summary>
    /// <param name="skel">Pointer to the FFXIV render skeleton.</param>
    /// <param name="boneName">The name of the root bone to rotate.</param>
    /// <param name="pivot">The pivot point around which translations are rotated.</param>
    /// <param name="q">The rotation quaternion to apply.</param>
    /// <param name="t">Optional translation offset added to affected bones.</param>
    private static void RotateByName(FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* skel, string boneName, Vector3 pivot, Quaternion q, Vector3 t = default)
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
                var nt = pivot + Vector3.Transform(new Vector3(tr->Translation.X, tr->Translation.Y, tr->Translation.Z) - pivot, q) +t;
                tr->Translation.X = nt.X; tr->Translation.Y = nt.Y; tr->Translation.Z = nt.Z;
                var nr = Quaternion.Normalize(
                    q * new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W));
                tr->Rotation.X = nr.X; tr->Rotation.Y = nr.Y; tr->Rotation.Z = nr.Z; tr->Rotation.W = nr.W;
            }
        }
    }
    #endregion

    #region Emote
    /// <summary>
    /// Evaluates configured emote rules against current blend values and enqueues chat commands when hold and cooldown conditions are met.
    /// </summary>
    public void UpdateEmotes()
    {
        var now = GetNowSeconds();
        foreach (var r in config.EmoteRules)
        {
            if (!r.Enabled || !r.Command.StartsWith('/')) continue;
            emoteState.TryGetValue(r, out var st);
            if (GetBlend(r.Blend) < r.Threshold) { emoteState[r] = (0, st.fired); continue; }

            var since = st.since == 0 ? now : st.since;
            var fired = st.fired;
            if (now - since >= r.HoldSec && now - fired >= r.CooldownSec)
            {
                Commands.Enqueue(r.Command);
                if (config.DebugEmotes) log.Debug($"VMC Emote ausgelöst: {r.Blend} ({GetBlend(r.Blend):0.00}) -> {r.Command}");
                fired = now;
                since = double.MaxValue;   // erst nach Abfall unter Schwelle wieder scharf
            }
            emoteState[r] = (since, fired);
        }
    }
    #endregion

    #region Face
    /// <summary>
    /// Rebuilds the cached facial bone index when needed and applies smoothed facial blend rules to the character's skeleton.
    /// </summary>
    /// <param name="addr">Native pointer address of the target <see cref="Character"/>.</param>
    /// <param name="fresh">Indicates whether the incoming VMC stream is currently fresh.</param>
    private void ApplyFace(nint addr, bool fresh)
    {
        var draw = ((Character*)addr)->GameObject.DrawObject;
        if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return;
        var skel = ((CharacterBase*)draw)->Skeleton;
        if (skel == null) return;

        // Index neu bauen, wenn sich Skeleton/Partials ändern
        var sig = 0;
        for (var p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var ps = skel->PartialSkeletons[p].GetHavokPose(0);
            if (ps != null && ps->Skeleton != null) sig = sig * 31 + ps->Skeleton->Bones.Length + p;
        }
        if ((nint)skel != faceSkel || sig != faceSig)
        {
            faceSkel = (nint)skel; faceSig = sig;
            faceIdx.Clear();
            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var ps = skel->PartialSkeletons[p].GetHavokPose(0);
                if (ps == null || ps->Skeleton == null) continue;
                for (var b = 0; b < ps->Skeleton->Bones.Length; b++)
                {
                    var n = ps->Skeleton->Bones[b].Name.String ?? string.Empty;
                    if (!faceIdx.TryGetValue(n, out var l)) faceIdx[n] = l = new();
                    l.Add((p, b));
                }
            }
            ///debug only
            /*
            log.Debug($"Face: {faceIdx.Count} Bones indiziert; j_f_mab_l in Partial "
                + (faceIdx.TryGetValue("j_f_mab_l", out var f) ? string.Join("/", f.Select(x => x.p)) : "- (nicht gefunden)")
                + ", j_ago in Partial "
                + (faceIdx.TryGetValue("j_ago", out var g) ? string.Join("/", g.Select(x => x.p)) : "- (nicht gefunden)"));
            var fk = faceIdx.Keys.Where(k => k.StartsWith("j_f")).ToList();
            log.Information($"Face-Namen ({fk.Count}): " + string.Join(", ", fk.Count > 0 ? fk : faceIdx.Keys.ToList()));
            */
        }

        var now = GetNowSeconds();
        var dt = (float)Math.Clamp(now - lastFaceT, 0.0, 0.1);
        lastFaceT = now;
        const float Deg = MathF.PI / 180f;

        foreach (var r in config.FaceRules)
        {
            if (!r.Enabled || !faceIdx.TryGetValue(r.Bone, out var occ)) continue;

            var goal = r.Force ? 1f : fresh ? Math.Clamp(GetBlend(r.Blend), 0f, 1f) : 0f;
            faceSm.TryGetValue(r, out var s);
            s += (goal - s) * MathF.Min(1f, dt * 30f);
            faceSm[r] = s;
            if (s < 0.002f) continue;

            foreach (var (p, bi) in occ)
            {
                var pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null) continue;
                var hk = pose->Skeleton;

                var t0 = pose->ModelPose[bi].Translation;
                var r0 = pose->ModelPose[bi].Rotation;
                var pivot = new Vector3(t0.X, t0.Y, t0.Z);
                var rot = new Quaternion(r0.X, r0.Y, r0.Z, r0.W);

                var lq = Quaternion.CreateFromYawPitchRoll(r.RotY * Deg * s, r.RotX * Deg * s, r.RotZ * Deg * s);
                var q = Quaternion.Normalize(rot * lq * Quaternion.Conjugate(rot));
                var dp = Vector3.Transform(new Vector3(r.PosX, r.PosY, r.PosZ) * s, rot);

                for (var i = 0; i < hk->Bones.Length; i++)
                {
                    if (!IsSelfOrDescendant(hk, i, bi)) continue;
                    var tr = &pose->ModelPose.Data[i];
                    var nt = pivot + Vector3.Transform(new Vector3(tr->Translation.X, tr->Translation.Y, tr->Translation.Z) - pivot, q) + dp;
                    tr->Translation.X = nt.X; tr->Translation.Y = nt.Y; tr->Translation.Z = nt.Z;
                    var nr = Quaternion.Normalize(q * new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W));
                    tr->Rotation.X = nr.X; tr->Rotation.Y = nr.Y; tr->Rotation.Z = nr.Z; tr->Rotation.W = nr.W;
                }
            }
        }
    }

    /// <summary>
    /// Logs reference pose hierarchy and transform details for facial and head bones of the local player.
    /// </summary>
    public void DumpFaceBones()
    {
        var lp = objects.LocalPlayer;
        if (lp == null) return;
        var draw = ((Character*)lp.Address)->GameObject.DrawObject;
        if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return;
        var skel = ((CharacterBase*)draw)->Skeleton;
        if (skel == null) return;
        for (var p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var pose = skel->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;
            var hk = pose->Skeleton;
            for (var b = 0; b < hk->Bones.Length; b++)
            {
                var n = hk->Bones[b].Name.String ?? "?";
                if (!n.StartsWith("j_f_") && n != "j_ago" && n != "j_kao") continue;
                var pi = hk->ParentIndices[b];
                var pn = pi >= 0 ? hk->Bones[pi].Name.String : "-";
                var t = hk->ReferencePose[b].Translation;
                var r = hk->ReferencePose[b].Rotation;
                log.Information($"P{p} {n} <- {pn} T({t.X:0.0000},{t.Y:0.0000},{t.Z:0.0000}) R({r.X:0.000},{r.Y:0.000},{r.Z:0.000},{r.W:0.000})");
            }
        }
    }

    /// <summary>
    /// Determines whether a bone or any of its ancestors up to a bounded depth belongs to the specified bone name set.
    /// </summary>
    /// <param name="hk">Pointer to the Havok skeleton.</param>
    /// <param name="i">The starting bone index.</param>
    /// <param name="set">The set of bone names representing the target subtree.</param>
    /// <returns><c>true</c> if a matching bone is found in the ancestor chain; otherwise, <c>false</c>.</returns>
    private static bool InSubtree(hkaSkeleton* hk, int i, HashSet<string> set)
    {
        for (var n = 0; i >= 0 && n < 256; n++)
        {
            if (set.Contains(hk->Bones[i].Name.String ?? string.Empty)) return true;
            i = hk->ParentIndices[i];
        }
        return false;
    }
    /// <summary>
    /// Determines whether bone <paramref name="i"/> is equal to or a descendant of <paramref name="root"/> within a bounded parent traversal.
    /// </summary>
    /// <param name="hk">Pointer to the Havok skeleton.</param>
    /// <param name="i">The candidate bone index.</param>
    /// <param name="root">The root bone index.</param>
    /// <returns><c>true</c> if <paramref name="i"/> is <paramref name="root"/> or a descendant of it; otherwise, <c>false</c>.</returns>
    private static bool IsSelfOrDescendant(hkaSkeleton* hk, int i, int root)
    {
        for (var n = 0; i >= 0 && n < 256; n++)
        {
            if (i == root) return true;
            i = hk->ParentIndices[i];
        }
        return false;
    }
    #endregion

    #region Helper

    /// <summary>
    /// Reads a null-terminated, 4-byte-aligned UTF-8 string from an OSC buffer starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="buf">The source OSC byte buffer.</param>
    /// <param name="offset">The starting byte index within <paramref name="buf"/>.</param>
    /// <param name="value">When successful, outputs the decoded string.</param>
    /// <param name="nextOffset">When successful, outputs the next 4-byte-aligned index after the string.</param>
    /// <returns><c>true</c> if a valid null-terminated string was read; otherwise, <c>false</c>.</returns>
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

    /// <summary>
    /// Reads a big-endian 32-bit signed integer from the start of the given byte span.
    /// </summary>
    /// <param name="s">The byte span containing at least 4 bytes.</param>
    /// <returns>The parsed 32-bit integer.</returns>
    private static int ReadInt32BE(ReadOnlySpan<byte> s)=> (s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3];

    /// <summary>
    /// Reads a big-endian 32-bit IEEE 754 single-precision floating-point number from the start of the given byte span.
    /// </summary>
    /// <param name="s">The byte span containing at least 4 bytes.</param>
    /// <returns>The parsed <see cref="float"/> value.</returns>
    private static float ReadSingleBE(ReadOnlySpan<byte> s)
    {
        var raw = ReadInt32BE(s);
        return BitConverter.Int32BitsToSingle(raw);
    }

    /// <summary>
    /// Gets the current UTC time expressed in seconds since tick zero.
    /// </summary>
    /// <returns>Current UTC timestamp in seconds.</returns>
    private static double GetNowSeconds() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>
    /// Computes the shortest-arc rotation quaternion that aligns direction vector <paramref name="d"/> with target vector <paramref name="t"/>.
    /// </summary>
    /// <param name="d">The initial direction vector.</param>
    /// <param name="t">The target direction vector.</param>
    /// <returns>The rotation quaternion from <paramref name="d"/> to <paramref name="t"/>.</returns>
    private static Quaternion FromTo(Vector3 d, Vector3 t)
    {
        var axis = Vector3.Cross(d, t);
        var len = axis.Length();
        if (len < 1e-5f) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(axis / len, MathF.Atan2(len, Vector3.Dot(d, t)));
    }

    #endregion

    /// <summary>
    /// Marks the receiver as disposed and stops the active UDP listener.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopListener();
    }
}
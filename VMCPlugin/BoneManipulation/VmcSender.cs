using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VMCPlugin.Offsets;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        // --- Remote Bone State & Outgoing Queue ---
        private sealed class RemoteBoneFrame
        {
            public ulong ClientId;
            public Vector3 WorldPos;
            public double ReceivedAtSec;
            public readonly Dictionary<string, (Quaternion Rot, Vector3 Pos)> Bones = new(StringComparer.OrdinalIgnoreCase);
            public string[] Names = Array.Empty<string>();
        }

        private readonly record struct OutgoingBoneSample(string Name, Quaternion Rot, Vector3 Pos);

        private readonly ConcurrentDictionary<ulong, RemoteBoneFrame> _latestRemoteFrames = new();
        private readonly BlockingCollection<byte[]> _sendQueue = new(boundedCapacity: 32);

        private sealed record LatestPositionState(
            ulong ClientId,
            uint MapId,
            uint CurrentWorld,
            Vector3 Position);

        private LatestPositionState? _latestPosition;
        private volatile bool _hasLatestPoseState;
        private double _lastHeartbeatSentSec;


        // --- TCP Client ---
        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private CancellationTokenSource? _tcpCts;
        private Thread? _tcpSendThread;
        private Thread? _tcpRecvThread;
        private string _tcpHost = "127.0.0.1";
        private int _tcpPort = 40000;
        private volatile bool _tcpRunning;
        private readonly object _connLock = new();
        private int _reconnectDelayMs = 500;

        // --- Paket ---
        private readonly Dictionary<string, (Quaternion Rot, Vector3 Pos)> _lastSent = new(StringComparer.OrdinalIgnoreCase);
        private int _keyCount;
        private double _lastKeySec = -999, _lastDeltaSec;
        private const double KeyInterval = 5.0;

        // --- Options ---
        private bool _remoteEnabled;
        public bool RemoteEnabled
        {
            get => _remoteEnabled;
            set
            {
                if (_remoteEnabled == value) return;
                _remoteEnabled = value;
                if (_remoteEnabled)
                    ConfigureServerTarget(_config.RemoteIp, _config.RemotePort);
                else
                    StopRemote();
            }
        }

        public void ConfigureServerTarget(string host, int port)
        {
            StopRemote();
            if (_disposed) return;

            _reconnectDelayMs = 500;
            _tcpHost = host;
            _tcpPort = Math.Clamp(port, 1024, 65535);
            _tcpRunning = true;
            _tcpCts = new CancellationTokenSource();

            _tcpSendThread = new Thread(() => TcpSendLoop(_tcpCts.Token))
            {
                IsBackground = true,
                Name = "VmcTcpSender"
            };
            _tcpRecvThread = new Thread(() => TcpReceiveLoop(_tcpCts.Token))
            {
                IsBackground = true,
                Name = "VmcTcpReceiver"
            };

            _tcpSendThread.Start();
            _tcpRecvThread.Start();
            _log.Information($"VMC TCP Client gestartet für {_tcpHost}:{_tcpPort}.");
        }

        public void StopRemote()
        {
            var cts = _tcpCts;
            var sendT = _tcpSendThread;
            var recvT = _tcpRecvThread;

            _tcpRunning = false;
            try { cts?.Cancel(); } catch { }
            try { _tcpStream?.Close(); _tcpStream?.Dispose(); } catch { }
            try { _tcpClient?.Close(); _tcpClient?.Dispose(); } catch { }

            sendT?.Join(500);
            recvT?.Join(500);

            _tcpStream = null;
            _tcpClient = null;
            cts?.Dispose();
            _tcpCts = null;
            _tcpSendThread = null;
            _tcpRecvThread = null;

            while (_sendQueue.TryTake(out _)) { }
        }

        private void EnsureConnected(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _tcpRunning)
            {
                if (_tcpClient is { Connected: true } && _tcpStream != null)
                    return;

                var client = new TcpClient { NoDelay = true };
                Task? connectTask = null;
                try
                {
                    connectTask = client.ConnectAsync(_tcpHost, _tcpPort);
                    if (!connectTask.Wait(2000, token))
                        throw new TimeoutException();

                    // try-Block, nach erfolgreichem Wait:
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

                    lock (_connLock)
                    {
                        _tcpClient = client;
                        _tcpStream = client.GetStream();
                    }
                    while (_sendQueue.TryTake(out _)) { }   // alte Frames nach Reconnect verwerfen
                    _reconnectDelayMs = 500;
                    _log.Debug($"Verbunden mit TCP-Server {_tcpHost}:{_tcpPort}");
                    return;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
                {
                    Observe(connectTask);
                    try { client.Dispose(); } catch { }
                    return;
                }
                catch
                {
                    Observe(connectTask);
                    try { client.Dispose(); } catch { }
                    Disconnect();
                    _log.Debug($"Verbindung fehlgeschlagen, neuer Versuch in {_reconnectDelayMs} ms.");
                    WaitBackoff();
                }
            }
        }

        private static void Observe(Task? t)
        {
            t?.ContinueWith(static x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void Disconnect()
        {
            lock (_connLock)
            {
                try { _tcpStream?.Close(); } catch { }
                try { _tcpClient?.Close(); } catch { }
                _tcpStream = null;
                _tcpClient = null;
            }
        }

        private void WaitBackoff()
        {
            var end = Environment.TickCount64 + _reconnectDelayMs;
            while (_tcpRunning && Environment.TickCount64 < end)
                Thread.Sleep(50);
            _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 5000);
        }

        private void TcpSendLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _tcpRunning)
            {
                EnsureConnected(token);
                var stream = _tcpStream;
                if (stream == null) continue;

                try
                {
                    if (!_sendQueue.TryTake(out var packet, 100, token))
                    {
                        var now = GetNowSeconds();
                        if (!_hasLatestPoseState || now - _lastHeartbeatSentSec < _config.HeartbeatIntervalSec)
                            continue;

                        _lastHeartbeatSentSec = now;
                        packet = BuildHeartbeatPacket();
                    }

                    stream.Write(packet, 0, packet.Length);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || !_tcpRunning)
                        break;

                    _log.Warning(ex, "VMC: TCP-Senden fehlgeschlagen, verbinde neu.");
                    Disconnect();
                }
            }
        }

        private void TcpReceiveLoop(CancellationToken token)
        {
            var lenBuf = new byte[4];

            while (!token.IsCancellationRequested && _tcpRunning)
            {
                var stream = _tcpStream;
                if (stream == null || _tcpClient is not { Connected: true })
                {
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    if (!ReadExact(stream, lenBuf, 0, 4, token))
                        throw new IOException("TCP-Stream geschlossen.");

                    var frameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                    if (frameLen <= 0 || frameLen > 1_048_576)
                        throw new InvalidDataException($"Ungültige TCP-Framelänge: {frameLen}");

                    var payload = new byte[frameLen];
                    if (!ReadExact(stream, payload, 0, frameLen, token))
                        throw new IOException("TCP-Frame unvollständig.");

                    ParseRemotePacket(payload);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        _log.Warning(ex, "VMC: TCP-Empfang unterbrochen.");
                    Disconnect();
                    Thread.Sleep(250);
                }
            }
        }

        public void UpdateLocalPresence()
        {
            if (Api.GetLocalPlayer() == null)
                return;

            var lp = Api.GetLocalPlayer();
            var pos = lp?.Position ?? Vector3.Zero;

            var snapshot = new LatestPositionState(
                Api.PlayerState.ContentId,
                Api.ClientState.MapId,
                Api.GetLocalPlayer().CurrentWorld.RowId,
                pos);

            Volatile.Write(ref _latestPosition, snapshot);
            _hasLatestPoseState = true;
        }

        private byte[] BuildHeartbeatPacket()
        {
            var snap = Volatile.Read(ref _latestPosition);
            var clientId = snap?.ClientId ?? Api.PlayerState.ContentId;
            var mapId = snap?.MapId ?? Api.ClientState.MapId;
            var currentWorld = snap?.CurrentWorld ?? Api.GetLocalPlayer().CurrentWorld.RowId;
            var pos = snap?.Position ?? Vector3.Zero;

            var payload = new byte[12];
            var pSpan = payload.AsSpan();
            BinaryPrimitives.WriteSingleLittleEndian(pSpan[0..4], pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(pSpan[4..8], pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(pSpan[8..12], pos.Z);

            return WireHeader.WrapPayloadFramed(
                clientId,
                mapId,
                currentWorld,
                WirePacketType.Heartbeat,
                payload,
                WirePacketFlags.HasWorldPos);
        }

        public void EnqueueFinalXivBones(nint charAddress, Vector3 worldPos)
        {
            var chara = (Character*)charAddress;
            if (!HasVisibleDrawObject(chara))
                return;

            if (!_tcpRunning)
                return;

            var skel = ((CharacterBase*)chara->GameObject.DrawObject)->Skeleton;
            if (skel == null)
                return;

            var samples = new List<OutgoingBoneSample>(64);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null)
                    continue;

                var hk = pose->Skeleton;
                var boneCount = Math.Min(hk->Bones.Length, pose->ModelPose.Length);

                for (var b = 0; b < boneCount; b++)
                {
                    var name = hk->Bones[b].Name.String;
                    if (string.IsNullOrEmpty(name) || !seenNames.Add(name))
                        continue;

                    var tr = pose->ModelPose.Data[b];
                    var q = Quaternion.Normalize(new Quaternion(tr.Rotation.X, tr.Rotation.Y, tr.Rotation.Z, tr.Rotation.W));
                    var pos = new Vector3(tr.Translation.X, tr.Translation.Y, tr.Translation.Z);
                    samples.Add(new OutgoingBoneSample(name, q, pos));
                }
            }

            if (samples.Count == 0)
                return;

            var nowSec = GetNowSeconds();
            var isKey = nowSec - _lastKeySec > KeyInterval || samples.Count != _keyCount;

            // ---------- Delta-Paket ----------
            if (!isKey)
            {
                var entries = new List<(ushort Idx, uint Rot, Vector3? Pos)>(samples.Count);
                for (var i = 0; i < samples.Count; i++)
                {
                    var s = samples[i];
                    if (!_lastSent.TryGetValue(s.Name, out var last))
                        continue;

                    var rotCh = MathF.Abs(Quaternion.Dot(s.Rot, last.Rot)) < 0.99999f;   // ~0,5°
                    var posCh = Vector3.DistanceSquared(s.Pos, last.Pos) > 1e-6f;        // ~1 mm
                    if (!rotCh && !posCh)
                        continue;

                    entries.Add(((ushort)i, PackQuat(s.Rot), posCh ? s.Pos : null));
                    _lastSent[s.Name] = (s.Rot, posCh ? s.Pos : last.Pos);
                }

                if (entries.Count == 0 && nowSec - _lastDeltaSec < 0.5)
                    return;                                   // Keepalive alle 0,5 s
                _lastDeltaSec = nowSec;

                var buf = new byte[14 + entries.Count * 12];
                var sp = buf.AsSpan();
                BinaryPrimitives.WriteSingleLittleEndian(sp[0..4], worldPos.X);
                BinaryPrimitives.WriteSingleLittleEndian(sp[4..8], worldPos.Y);
                BinaryPrimitives.WriteSingleLittleEndian(sp[8..12], worldPos.Z);
                BinaryPrimitives.WriteUInt16LittleEndian(sp[12..14], (ushort)entries.Count);

                var o = 14;
                foreach (var (idx, rot, pos) in entries)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(sp[o..], (ushort)(idx | (pos.HasValue ? 0x8000 : 0)));
                    o += 2;
                    BinaryPrimitives.WriteUInt32LittleEndian(sp[o..], rot);
                    o += 4;
                    if (pos is { } p)
                    {
                        BinaryPrimitives.WriteHalfLittleEndian(sp[o..], (Half)p.X);
                        BinaryPrimitives.WriteHalfLittleEndian(sp[(o + 2)..], (Half)p.Y);
                        BinaryPrimitives.WriteHalfLittleEndian(sp[(o + 4)..], (Half)p.Z);
                        o += 6;
                    }
                }

                _sendQueue.TryAdd(WireHeader.WrapPayloadFramed(
                    Api.PlayerState.ContentId,
                    Api.ClientState.MapId,
                    chara->CurrentWorld,
                    WirePacketType.FinalBonesDelta,
                    buf.AsSpan(0, o),
                    WirePacketFlags.HasWorldPos));
                return;
            }

            // ---------- Keyframe (volle Namen + Floats, wie bisher) ----------
            _lastKeySec = nowSec;
            _keyCount = samples.Count;
            _lastSent.Clear();
            foreach (var s in samples)
                _lastSent[s.Name] = (s.Rot, s.Pos);

            var encodedEntries = new List<(byte[] NameBytes, Quaternion Rot, Vector3 Pos)>(samples.Count);
            var entriesByteLen = 0;

            foreach (var s in samples)
            {
                var nameBytes = Encoding.UTF8.GetBytes(s.Name);
                if (nameBytes.Length == 0 || nameBytes.Length > byte.MaxValue)
                    continue;

                encodedEntries.Add((nameBytes, s.Rot, s.Pos));
                entriesByteLen += 1 + nameBytes.Length + 16 + 12;
            }

            if (encodedEntries.Count == 0)
                return;

            var payload = new byte[12 + 2 + entriesByteLen];
            var pSpan = payload.AsSpan();

            BinaryPrimitives.WriteSingleLittleEndian(pSpan[0..4], worldPos.X);
            BinaryPrimitives.WriteSingleLittleEndian(pSpan[4..8], worldPos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(pSpan[8..12], worldPos.Z);
            BinaryPrimitives.WriteUInt16LittleEndian(pSpan[12..14], (ushort)encodedEntries.Count);

            var offset = 14;
            foreach (var (nameBytes, rot, pos) in encodedEntries)
            {
                pSpan[offset++] = (byte)nameBytes.Length;
                nameBytes.CopyTo(pSpan.Slice(offset, nameBytes.Length));
                offset += nameBytes.Length;

                BinaryPrimitives.WriteSingleLittleEndian(pSpan[offset..(offset + 4)], rot.X);
                BinaryPrimitives.WriteSingleLittleEndian(pSpan[(offset + 4)..(offset + 8)], rot.Y);
                BinaryPrimitives.WriteSingleLittleEndian(pSpan[(offset + 8)..(offset + 12)], rot.Z);
                BinaryPrimitives.WriteSingleLittleEndian(pSpan[(offset + 12)..(offset + 16)], rot.W);
                offset += 16;

                BinaryPrimitives.WriteSingleLittleEndian(pSpan[offset..(offset + 4)], pos.X);
                BinaryPrimitives.WriteSingleLittleEndian(pSpan[(offset + 4)..(offset + 8)], pos.Y);
                BinaryPrimitives.WriteSingleLittleEndian(pSpan[(offset + 8)..(offset + 12)], pos.Z);
                offset += 12;
            }

            _sendQueue.TryAdd(WireHeader.WrapPayloadFramed(
                Api.PlayerState.ContentId,
                Api.ClientState.MapId,
                chara->CurrentWorld,
                WirePacketType.FinalBones,
                payload,
                WirePacketFlags.HasWorldPos));
        }

        private static bool MatchesAnyPrefix(string name, string[] prefixes)
        {
            for (var i = 0; i < prefixes.Length; i++)
            {
                if (name.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                    return false;
                totalRead += read;
            }
            return true;
        }

        private void ParseRemotePacket(ReadOnlySpan<byte> data)
        {
            if (!WireHeader.TryReadFrom(data, out var hdr))
                return;

            var availPayload = Math.Min(data.Length - WireHeader.Size, (int)hdr.PayloadLength);
            if (availPayload < 0)
                return;

            var payload = data.Slice(WireHeader.Size, availPayload);

            switch (hdr.PacketType)
            {
                case WirePacketType.Heartbeat:

                    return;

                case WirePacketType.FinalBones:
                    break;
                case WirePacketType.FinalBonesDelta:
                    {
                        if (payload.Length < 14 || !_latestRemoteFrames.TryGetValue(hdr.ClientId, out var old) || old.Names.Length == 0)
                            return;                                // auf Keyframe warten

                        var nf = new RemoteBoneFrame
                        {
                            ClientId = old.ClientId,
                            Names = old.Names,
                            ReceivedAtSec = GetNowSeconds(),
                            WorldPos = new Vector3(
                                BinaryPrimitives.ReadSingleLittleEndian(payload[0..4]),
                                BinaryPrimitives.ReadSingleLittleEndian(payload[4..8]),
                                BinaryPrimitives.ReadSingleLittleEndian(payload[8..12]))
                        };
                        foreach (var kv in old.Bones) nf.Bones[kv.Key] = kv.Value;   // Kopie, vermeidet Race mit dem Main-Thread

                        var cnt = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..14]);
                        var o = 14;
                        for (var i = 0; i < cnt; i++)
                        {
                            if (o + 6 > payload.Length) break;
                            var raw = BinaryPrimitives.ReadUInt16LittleEndian(payload[o..]); o += 2;
                            var rot = UnpackQuat(BinaryPrimitives.ReadUInt32LittleEndian(payload[o..])); o += 4;
                            var idx = raw & 0x7FFF;
                            if (idx >= nf.Names.Length) break;
                            var name = nf.Names[idx];
                            var dpos = nf.Bones.TryGetValue(name, out var cur) ? cur.Pos : default;
                            if ((raw & 0x8000) != 0)
                            {
                                if (o + 6 > payload.Length) break;
                                dpos = new Vector3(
                                    (float)BinaryPrimitives.ReadHalfLittleEndian(payload[o..]),
                                    (float)BinaryPrimitives.ReadHalfLittleEndian(payload[(o + 2)..]),
                                    (float)BinaryPrimitives.ReadHalfLittleEndian(payload[(o + 4)..]));
                                o += 6;
                            }
                            nf.Bones[name] = (Quaternion.Normalize(rot), dpos);
                        }
                        _latestRemoteFrames[hdr.ClientId] = nf;
                        return;
                    }
                default:
                    return;
            }

            // Payload (14B Basis): [12B WorldPos x,y,z] + [2B boneCount(u16)] + entries
            if (payload.Length < 14)
                return;

            var x = BinaryPrimitives.ReadSingleLittleEndian(payload[0..4]);
            var y = BinaryPrimitives.ReadSingleLittleEndian(payload[4..8]);
            var z = BinaryPrimitives.ReadSingleLittleEndian(payload[8..12]);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..14]);

            var frame = new RemoteBoneFrame
            {
                ClientId = hdr.ClientId,
                WorldPos = new Vector3(x, y, z),
                ReceivedAtSec = GetNowSeconds(),
            };

            var names = new List<string>(count);
            var pos = 14;
            for (var i = 0; i < count; i++)
            {
                if (pos + 1 > payload.Length) break;
                int nameLen = payload[pos++];
                if (pos + nameLen + 28 > payload.Length) break;

                var boneName = Encoding.UTF8.GetString(payload.Slice(pos, nameLen));
                names.Add(boneName);
                pos += nameLen;

                var rx = BinaryPrimitives.ReadSingleLittleEndian(payload[pos..(pos + 4)]);
                var ry = BinaryPrimitives.ReadSingleLittleEndian(payload[(pos + 4)..(pos + 8)]);
                var rz = BinaryPrimitives.ReadSingleLittleEndian(payload[(pos + 8)..(pos + 12)]);
                var rw = BinaryPrimitives.ReadSingleLittleEndian(payload[(pos + 12)..(pos + 16)]);
                pos += 16;

                var px = BinaryPrimitives.ReadSingleLittleEndian(payload[(pos + 0)..(pos + 4)]);
                var py = BinaryPrimitives.ReadSingleLittleEndian(payload[(pos + 4)..(pos + 8)]);
                var pz = BinaryPrimitives.ReadSingleLittleEndian(payload[(pos + 8)..(pos + 12)]);
                pos += 12;

                var rot = Quaternion.Normalize(new Quaternion(rx, ry, rz, rw));
                frame.Bones[boneName] = (rot, new Vector3(px, py, pz));
            }
            frame.Names = names.ToArray();
            _latestRemoteFrames[hdr.ClientId] = frame;
        }

        public void ApplyRemoteFrames()
        {
            if (!_remoteEnabled || _latestRemoteFrames.IsEmpty)
                return;

            var lp = _objects.LocalPlayer;
            if (lp == null)
                return;

            var maxDist = _config.RemoteApplyMaxDistance;
            var maxDistSq = maxDist * maxDist;
            var now = GetNowSeconds();

            foreach (var obj in _objects)
            {
                if (obj.EntityId == lp.EntityId)
                    continue;
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
                    continue;

                var chara = (Character*)obj.Address;
                if (chara == null || !HasVisibleDrawObject(chara))
                    continue;

                if (!_latestRemoteFrames.TryGetValue(chara->ContentId, out var frame))
                    continue;

                if (now - frame.ReceivedAtSec > 2.0)
                {
                    _latestRemoteFrames.TryRemove(frame.ClientId, out _);
                    continue;
                }

                if (frame.Bones.Count == 0)
                    continue;

                var dx = frame.WorldPos.X - obj.Position.X;
                var dy = frame.WorldPos.Y - obj.Position.Y;
                var dz = frame.WorldPos.Z - obj.Position.Z;
                if ((dx * dx + dy * dy + dz * dz) > maxDistSq)
                    continue;

                ApplyTransformsToCharacter(chara, frame);
            }
        }

        private void ApplyTransformsToCharacter(Character* chara, RemoteBoneFrame frame)
        {
            if (!HasVisibleDrawObject(chara))
                return;

            var skel = ((CharacterBase*)chara->GameObject.DrawObject)->Skeleton;
            if (skel == null)
                return;

            var blend = Math.Clamp(_config.RetargetBlend, 0f, 1f);
            if (blend <= 0.001f)
                return;

            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null)
                    continue;

                var hk = pose->Skeleton;
                var boneCount = Math.Min(hk->Bones.Length, pose->ModelPose.Length);

                for (var b = 0; b < boneCount; b++)
                {
                    var boneName = hk->Bones[b].Name.String;
                    if (string.IsNullOrEmpty(boneName))
                        continue;
                    if (!frame.Bones.TryGetValue(boneName, out var sample))
                        continue;

                    var tr = &pose->ModelPose.Data[b];

                    // Rotation auf Ziel-Bone anwenden
                    var curRot = Quaternion.Normalize(
                        new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W));
                    var targetRot = blend < 0.999f
                        ? Quaternion.Slerp(curRot, sample.Rot, blend)
                        : sample.Rot;
                    targetRot = Quaternion.Normalize(targetRot);

                    tr->Rotation.X = targetRot.X;
                    tr->Rotation.Y = targetRot.Y;
                    tr->Rotation.Z = targetRot.Z;
                    tr->Rotation.W = targetRot.W;

                    // Translation (z. B. Gesicht/Kiefer/Deformer) auf Ziel-Bone anwenden
                    if (blend < 0.999f)
                    {
                        tr->Translation.X += (sample.Pos.X - tr->Translation.X) * blend;
                        tr->Translation.Y += (sample.Pos.Y - tr->Translation.Y) * blend;
                        tr->Translation.Z += (sample.Pos.Z - tr->Translation.Z) * blend;
                    }
                    else
                    {
                        tr->Translation.X = sample.Pos.X;
                        tr->Translation.Y = sample.Pos.Y;
                        tr->Translation.Z = sample.Pos.Z;
                    }
                }
            }
        }

        private static bool HasVisibleDrawObject(Character* chara)
        {
            if (chara == null) return false;
            var draw = chara->GameObject.DrawObject;
            if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return false;
            return ((CharacterBase*)draw)->Skeleton != null;
        }

        private static uint PackQuat(Quaternion q)
        {
            Span<float> a = stackalloc float[4] { q.X, q.Y, q.Z, q.W };
            var big = 0;
            for (var i = 1; i < 4; i++) if (MathF.Abs(a[i]) > MathF.Abs(a[big])) big = i;
            var sign = a[big] < 0 ? -1f : 1f;
            var v = (uint)big; var k = 0;
            for (var i = 0; i < 4; i++)
            {
                if (i == big) continue;
                var c = Math.Clamp(a[i] * sign * 1.41421356f, -1f, 1f);
                v |= (uint)MathF.Round((c * 0.5f + 0.5f) * 1023f) << (2 + 10 * k++);
            }
            return v;
        }

        private static Quaternion UnpackQuat(uint v)
        {
            Span<float> a = stackalloc float[4];
            var big = (int)(v & 3); var k = 0; var sum = 0f;
            for (var i = 0; i < 4; i++)
            {
                if (i == big) continue;
                var q10 = (v >> (2 + 10 * k++)) & 1023;
                a[i] = ((q10 / 1023f) * 2f - 1f) / 1.41421356f;
                sum += a[i] * a[i];
            }
            a[big] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
            return new Quaternion(a[0], a[1], a[2], a[3]);
        }

        public enum WirePacketType : uint { None = 0, FinalBones = 1, Heartbeat = 2, FinalBonesDelta = 3 }

        [Flags]
        public enum WirePacketFlags : ushort
        {
            None = 0,
            HasWorldPos = 1 << 0
        }

        public readonly struct WireHeader
        {
            public const int Size = 32;
            public const uint MagicValue = 0x58495642; // "XIVB"
            public const ushort CurrentVersion = 1;

            public uint Magic { get; init; }
            public ushort Version { get; init; }
            public WirePacketFlags Flags { get; init; }
            public ulong ClientId { get; init; }
            public uint Map { get; init; }
            public uint CurrentWorld { get; init; }
            public WirePacketType PacketType { get; init; }
            public uint PayloadLength { get; init; }

            public static WireHeader Create(
                ulong clientId,
                uint map,
                uint currentWorld,
                WirePacketType packetType,
                uint payloadLength,
                WirePacketFlags flags = WirePacketFlags.None) => new()
                {
                    Magic = MagicValue,
                    Version = CurrentVersion,
                    Flags = flags,
                    ClientId = clientId,
                    Map = map,
                    CurrentWorld = currentWorld,
                    PacketType = packetType,
                    PayloadLength = payloadLength
                };

            public void WriteTo(Span<byte> dst)
            {
                if (dst.Length < Size)
                    throw new ArgumentException($"Buffer too small for WireHeader ({dst.Length} < {Size}).", nameof(dst));

                BinaryPrimitives.WriteUInt32LittleEndian(dst[0..4], Magic);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[4..6], Version);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[6..8], (ushort)Flags);
                BinaryPrimitives.WriteUInt64LittleEndian(dst[8..16], ClientId);
                BinaryPrimitives.WriteUInt32LittleEndian(dst[16..20], Map);
                BinaryPrimitives.WriteUInt32LittleEndian(dst[20..24], CurrentWorld);
                BinaryPrimitives.WriteUInt32LittleEndian(dst[24..28], (uint)PacketType);
                BinaryPrimitives.WriteUInt32LittleEndian(dst[28..32], PayloadLength);
            }

            public static bool TryReadFrom(ReadOnlySpan<byte> src, out WireHeader header)
            {
                header = default;
                if (src.Length < Size)
                    return false;

                var magic = BinaryPrimitives.ReadUInt32LittleEndian(src[0..4]);
                var version = BinaryPrimitives.ReadUInt16LittleEndian(src[4..6]);
                if (magic != MagicValue || version != CurrentVersion)
                    return false;

                header = new WireHeader
                {
                    Magic = magic,
                    Version = version,
                    Flags = (WirePacketFlags)BinaryPrimitives.ReadUInt16LittleEndian(src[6..8]),
                    ClientId = BinaryPrimitives.ReadUInt64LittleEndian(src[8..16]),
                    Map = BinaryPrimitives.ReadUInt32LittleEndian(src[16..20]),
                    CurrentWorld = BinaryPrimitives.ReadUInt32LittleEndian(src[20..24]),
                    PacketType = (WirePacketType)BinaryPrimitives.ReadUInt32LittleEndian(src[24..28]),
                    PayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(src[28..32])
                };
                return true;
            }

            public static byte[] WrapPayloadFramed(
                ulong clientId, uint map, uint currentWorld,
                WirePacketType packetType, ReadOnlySpan<byte> payload,
                WirePacketFlags flags = WirePacketFlags.None)
            {
                var total = Size + payload.Length;
                var packet = new byte[4 + total];
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), (uint)total);
                Create(clientId, map, currentWorld, packetType, (uint)payload.Length, flags)
                    .WriteTo(packet.AsSpan(4, Size));
                payload.CopyTo(packet.AsSpan(4 + Size));
                return packet;
            }
        }
    }
}

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SharpGLTF.Scenes;
using System;
using System.Collections.Generic;
using System.Numerics;
using VMCPlugin.Offsets;

namespace VMCPlugin.BoneManipulation
{
    public unsafe sealed class PapRecorder
    {
        public sealed class Track
        {
            public string Name = "";
            public int Parent = -1;                       // Index in Tracks, -1 = Root
            public List<Quaternion> Rot = new();
            public List<Vector3> Pos = new();
        }

        public bool Recording { get; private set; }
        public float Fps { get; set; } = 30f;
        public int FrameCount { get; private set; }
        public readonly List<Track> Tracks = new();

        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private double _startSec, _nextSec;

        public void Start(double nowSec)
        {
            Tracks.Clear(); _index.Clear();
            FrameCount = 0; _startSec = nowSec; _nextSec = nowSec;
            Recording = true;
        }

        public void Stop()
        {
            Recording = false;
            Api.PluginLog.Debug($"Recorder: {FrameCount} Frames, {Tracks.Count} Tracks, {FrameCount / Fps:F2}s");

        }

        public void Capture(Character* chara, double nowSec)
        {
            if (!Recording || nowSec < _nextSec) return;
            _nextSec += 1.0 / Fps;                         // feste Framerate -> sauberes Resampling

            var skel = ((CharacterBase*)chara->GameObject.DrawObject)->Skeleton;
            if (skel == null) return;

            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null) continue;
                var hk = pose->Skeleton;
                var n = Math.Min(hk->Bones.Length, pose->ModelPose.Length);

                for (var b = 0; b < n; b++)
                {
                    var name = hk->Bones[b].Name.String;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (!_index.TryGetValue(name, out var ti))
                    {
                        // neuer Track: vorherige Frames mit Identität auffüllen
                        ti = Tracks.Count;
                        var t = new Track { Name = name };
                        for (var i = 0; i < FrameCount; i++) { t.Rot.Add(Quaternion.Identity); t.Pos.Add(Vector3.Zero); }
                        Tracks.Add(t); _index[name] = ti;
                    }
                    var tr = Tracks[ti];
                    if (tr.Rot.Count > FrameCount) continue;   // Bone-Name doppelt in Partials

                    var m = pose->ModelPose.Data[b];
                    var mr = Quaternion.Normalize(new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W));
                    var mp = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);

                    var pi = hk->ParentIndices[b];
                    if (pi >= 0)
                    {
                        var pm = pose->ModelPose.Data[pi];
                        var pr = Quaternion.Normalize(new Quaternion(pm.Rotation.X, pm.Rotation.Y, pm.Rotation.Z, pm.Rotation.W));
                        var pp = new Vector3(pm.Translation.X, pm.Translation.Y, pm.Translation.Z);
                        var inv = Quaternion.Inverse(pr);
                        mp = Vector3.Transform(mp - pp, inv);
                        mr = Quaternion.Normalize(inv * mr);
                        var pn = hk->Bones[pi].Name.String;
                        if (_index.TryGetValue(pn, out var pIdx)) tr.Parent = pIdx;
                    }
                    tr.Rot.Add(mr); tr.Pos.Add(mp);
                }
            }
            FrameCount++;
        }

        public void ExportGltf(string path, string animName = "recorded")
        {
            var nodes = new NodeBuilder[Tracks.Count];
            for (var i = 0; i < Tracks.Count; i++)
                nodes[i] = new NodeBuilder(Tracks[i].Name);

            var scene = new SceneBuilder();
            var armature = new NodeBuilder("Armature");

            for (var i = 0; i < Tracks.Count; i++)
            {
                var t = Tracks[i];
                if (t.Parent >= 0) nodes[t.Parent].AddNode(nodes[i]);
                else armature.AddNode(nodes[i]);

                var rot = nodes[i].UseRotation(animName);
                var pos = nodes[i].UseTranslation(animName);
                for (var f = 0; f < FrameCount; f++)
                {
                    var time = f / Fps;                       // Fps = 30 lassen
                    var r = t.Rot[f]; var p = t.Pos[f];
                    rot.SetPoint(time, new Quaternion(r.X, r.Y, r.Z, r.W));
                    pos.SetPoint(time, new Vector3(p.X, p.Y, p.Z));
                }
            }

            scene.AddNode(armature);
            scene.ToGltf2().SaveGLTF(path);
        }
    }
}
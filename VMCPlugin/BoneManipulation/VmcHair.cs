using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;

using ObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        private sealed class Spr { public Quaternion S; public Vector3 W; public bool Init; }
        private readonly Dictionary<(int p, int b), Spr> _hairSpr = new();
        private readonly List<(int p, int b)> _hairBones = new();
        private nint hairSkel; private int hairSig; private string _hairPrefixUsed = "";
        private double _lastHairT;
        private string[] _hairPfx = Array.Empty<string>();

        private Vector3 _lastPos;
        private double _lastPosT;
        private bool _idle { get; set; } = true;
        private float _hairW;

        /// <summary>
        /// Updates whether the character is considered idle based on movement speed between position samples, ignoring pauses or teleports.
        /// </summary>
        /// <param name="pos">The current world or character position.</param>
        private void UpdateIdle(Vector3 pos)
        {
            var now = GetNowSeconds();
            var dt = now - _lastPosT;
            _lastPosT = now;
            var prev = _lastPos;
            _lastPos = pos;
            if (dt <= 0.0001 || dt > 0.25) 
                return;   // Pause/Teleport: Zustand behalten
            _idle = Vector3.Distance(pos, prev) / (float)dt < _config.HairIdleSpeed;
        }

        /// <summary>
        /// Updates cached matching hair bones when the skeleton or prefix configuration changes and applies spring-damper secondary motion to those bones and their descendants.
        /// </summary>
        /// <param name="addr">Native pointer address of the target <see cref="Character"/>.</param>
        private void ApplyHair(nint addr)
        {
            var draw = ((Character*)addr)->GameObject.DrawObject;
            if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return;
            var skel = ((CharacterBase*)draw)->Skeleton;
            if (skel == null) return;

            var sig = 0;
            for (var p = 0; p < skel->PartialSkeletonCount; p++)
            {
                var ps = skel->PartialSkeletons[p].GetHavokPose(0);
                if (ps != null && ps->Skeleton != null) sig = sig * 31 + ps->Skeleton->Bones.Length + p;
            }
            if ((nint)skel != hairSkel || sig != hairSig || _config.HairPrefix != _hairPrefixUsed)
            {
                hairSkel = (nint)skel; hairSig = sig; _hairPrefixUsed = _config.HairPrefix;
                _hairSpr.Clear(); _hairBones.Clear();
                _hairPfx = _config.HairPrefix.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var p = 0; p < skel->PartialSkeletonCount; p++)
                {
                    var ps = skel->PartialSkeletons[p].GetHavokPose(0);
                    if (ps == null || ps->Skeleton == null) continue;
                    for (var b = 0; b < ps->Skeleton->Bones.Length; b++)   // Eltern vor Kindern
                    {
                        var n = ps->Skeleton->Bones[b].Name.String ?? "";
                        foreach (var pf in _hairPfx)
                            if (n.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) { _hairBones.Add((p, b)); break; }
                    }
                }
                //log.Debug($"Haar-Bones: {hairBones.Count}");
            }

            var now = GetNowSeconds();
            var dt = (float)Math.Clamp(now - _lastHairT, 0.001, 0.05);
            _lastHairT = now;

            var goal = !_config.HairOnlyIdle || _idle ? 1f : 0f;
            _hairW = _hairW < goal ? MathF.Min(goal, _hairW + dt / 0.3f) : MathF.Max(goal, _hairW - dt / 0.3f);
            if (_hairW <= 0.001f) { _hairSpr.Clear(); return; }   // aus, beim Wiedereinschalten neu synchronisieren

            foreach (var key in _hairBones)
            {
                var pose = skel->PartialSkeletons[key.p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null) continue;
                var hk = pose->Skeleton;
                var b = key.b;

                var tr0 = &pose->ModelPose.Data[b];
                var R = Quaternion.Normalize(new Quaternion(tr0->Rotation.X, tr0->Rotation.Y, tr0->Rotation.Z, tr0->Rotation.W));
                var pivot = new Vector3(tr0->Translation.X, tr0->Translation.Y, tr0->Translation.Z);

                if (!_hairSpr.TryGetValue(key, out var st)) _hairSpr[key] = st = new Spr();
                if (!st.Init) { st.S = R; st.W = Vector3.Zero; st.Init = true; continue; }

                // Fehler S → R als Rotationsvektor
                var e = Quaternion.Normalize(R * Quaternion.Conjugate(st.S));
                if (e.W < 0) e = new Quaternion(-e.X, -e.Y, -e.Z, -e.W);
                var sinH = MathF.Sqrt(MathF.Max(0f, 1f - e.W * e.W));
                var err = sinH > 1e-5f
                    ? new Vector3(e.X, e.Y, e.Z) / sinH * (2f * MathF.Acos(Math.Clamp(e.W, -1f, 1f)))
                    : Vector3.Zero;

                st.W += (err * _config.HairStiff - st.W * _config.HairDamp) * dt;
                var wl = st.W.Length();
                if (wl * dt > 1e-6f)
                    st.S = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(st.W / wl, wl * dt) * st.S);

                var O = Quaternion.Slerp(R, st.S, Math.Clamp(_config.HairStrength, 0f, 1f) * _hairW);
                var q = Quaternion.Normalize(O * Quaternion.Conjugate(R));

                for (var i = b; i < hk->Bones.Length; i++)
                {
                    if (!IsSelfOrDescendant(hk, i, b)) continue;
                    var tr = &pose->ModelPose.Data[i];
                    var nt = pivot + Vector3.Transform(new Vector3(tr->Translation.X, tr->Translation.Y, tr->Translation.Z) - pivot, q);
                    tr->Translation.X = nt.X; tr->Translation.Y = nt.Y; tr->Translation.Z = nt.Z;
                    var nr = Quaternion.Normalize(q * new Quaternion(tr->Rotation.X, tr->Rotation.Y, tr->Rotation.Z, tr->Rotation.W));
                    tr->Rotation.X = nr.X; tr->Rotation.Y = nr.Y; tr->Rotation.Z = nr.Z; tr->Rotation.W = nr.W;
                }
            }
        }
    }
}

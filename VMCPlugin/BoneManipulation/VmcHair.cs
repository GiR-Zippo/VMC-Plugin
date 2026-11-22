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
        private readonly Dictionary<(int p, int b), Spr> hairSpr = new();
        private readonly List<(int p, int b)> hairBones = new();
        private nint hairSkel; private int hairSig; private string hairPrefixUsed = "";
        private double lastHairT;
        private string[] hairPfx = Array.Empty<string>();

        private Vector3 lastPos;
        private double lastPosT;
        private bool idle = true;
        private float hairW;

        private void UpdateIdle(Vector3 pos)
        {
            var now = GetNowSeconds();
            var dt = now - lastPosT;
            lastPosT = now;
            var prev = lastPos;
            lastPos = pos;
            if (dt <= 0.0001 || dt > 0.25) return;   // Pause/Teleport: Zustand behalten
            idle = Vector3.Distance(pos, prev) / (float)dt < config.HairIdleSpeed;
        }

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
            if ((nint)skel != hairSkel || sig != hairSig || config.HairPrefix != hairPrefixUsed)
            {
                hairSkel = (nint)skel; hairSig = sig; hairPrefixUsed = config.HairPrefix;
                hairSpr.Clear(); hairBones.Clear();
                hairPfx = config.HairPrefix.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var p = 0; p < skel->PartialSkeletonCount; p++)
                {
                    var ps = skel->PartialSkeletons[p].GetHavokPose(0);
                    if (ps == null || ps->Skeleton == null) continue;
                    for (var b = 0; b < ps->Skeleton->Bones.Length; b++)   // Eltern vor Kindern
                    {
                        var n = ps->Skeleton->Bones[b].Name.String ?? "";
                        foreach (var pf in hairPfx)
                            if (n.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) { hairBones.Add((p, b)); break; }
                    }
                }
                log.Debug($"Haar-Bones: {hairBones.Count}");
            }

            var now = GetNowSeconds();
            var dt = (float)Math.Clamp(now - lastHairT, 0.001, 0.05);
            lastHairT = now;

            var goal = !config.HairOnlyIdle || idle ? 1f : 0f;
            hairW = hairW < goal ? MathF.Min(goal, hairW + dt / 0.3f) : MathF.Max(goal, hairW - dt / 0.3f);
            if (hairW <= 0.001f) { hairSpr.Clear(); return; }   // aus, beim Wiedereinschalten neu synchronisieren

            foreach (var key in hairBones)
            {
                var pose = skel->PartialSkeletons[key.p].GetHavokPose(0);
                if (pose == null || pose->Skeleton == null) continue;
                var hk = pose->Skeleton;
                var b = key.b;

                var tr0 = &pose->ModelPose.Data[b];
                var R = Quaternion.Normalize(new Quaternion(tr0->Rotation.X, tr0->Rotation.Y, tr0->Rotation.Z, tr0->Rotation.W));
                var pivot = new Vector3(tr0->Translation.X, tr0->Translation.Y, tr0->Translation.Z);

                if (!hairSpr.TryGetValue(key, out var st)) hairSpr[key] = st = new Spr();
                if (!st.Init) { st.S = R; st.W = Vector3.Zero; st.Init = true; continue; }

                // Fehler S → R als Rotationsvektor
                var e = Quaternion.Normalize(R * Quaternion.Conjugate(st.S));
                if (e.W < 0) e = new Quaternion(-e.X, -e.Y, -e.Z, -e.W);
                var sinH = MathF.Sqrt(MathF.Max(0f, 1f - e.W * e.W));
                var err = sinH > 1e-5f
                    ? new Vector3(e.X, e.Y, e.Z) / sinH * (2f * MathF.Acos(Math.Clamp(e.W, -1f, 1f)))
                    : Vector3.Zero;

                st.W += (err * config.HairStiff - st.W * config.HairDamp) * dt;
                var wl = st.W.Length();
                if (wl * dt > 1e-6f)
                    st.S = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(st.W / wl, wl * dt) * st.S);

                var O = Quaternion.Slerp(R, st.S, Math.Clamp(config.HairStrength, 0f, 1f) * hairW);
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

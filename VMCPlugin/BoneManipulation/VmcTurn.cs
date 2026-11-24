using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Numerics;
using ObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        private double lastTurnT;
        private double lastTurnLog;


        private static unsafe bool PlayerMoving()
        {
            var map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            return map != null && map->IsPlayerMoving;
        }

        private static unsafe bool PlayerStands()
        {
            var chara = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.GetLocalPlayer();
            if (chara == null || chara->Mode != CharacterModes.Normal)
                return false;
            var map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            return map != null && !map->IsPlayerMoving;
        }

        private void ApplyBodyTurn(nint addr)
        {
            var now = GetNowSeconds();
            var dt = (float)Math.Clamp(now - lastTurnT, 0.0, 0.1);
            lastTurnT = now;

            if (!((config.TurnBodyStanding && idle && PlayerStands()) ||  // Stand: Option an und wirklich frei
                (config.TurnBodyWalking && !idle && PlayerMoving())))    // Bewegung: Option an und läuft
                return;


            var chara = (Character*)addr;
            var draw = chara->GameObject.DrawObject;
            if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase) return;
            var skel = ((CharacterBase*)draw)->Skeleton;
            if (skel == null || skel->PartialSkeletonCount < 2) return;
            var pose = skel->PartialSkeletons[1].GetHavokPose(0);
            if (pose == null) return;

            // j_kao: lokale X-Achse zeigt nach vorn → Kopf-Yaw relativ zum Körper
            var r = pose->ModelPose[0].Rotation;
            var fwd = Vector3.Transform(Vector3.UnitX, new Quaternion(r.X, r.Y, r.Z, r.W));
            const float Deg = MathF.PI / 180f;
            var yaw = MathF.Atan2(fwd.X, fwd.Z) - config.TurnCenterDeg * Deg;
            if (config.DebugEmotes && now - lastTurnLog > 0.5) { lastTurnLog = now; log.Debug($"Kopf-Yaw rel. Körper: {yaw / Deg:0.0}°"); }

            var k = Math.Clamp((MathF.Abs(yaw) - config.TurnStartDeg * Deg) / (30f * Deg), 0f, 1f);   // weich einblenden
            if (k <= 0f) return;

            var nr = chara->GameObject.Rotation + MathF.Sign(yaw) * k * config.TurnSpeedDeg * Deg * dt;
            if (nr > MathF.PI) nr -= 2f * MathF.PI; else if (nr < -MathF.PI) nr += 2f * MathF.PI;
            chara->GameObject.SetRotation(nr);
        }
    }
}

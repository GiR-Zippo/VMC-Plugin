using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Numerics;
using ObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        private double _lastTurnT;
        private double _lastTurnLog;


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
            var dt = (float)Math.Clamp(now - _lastTurnT, 0.0, 0.1);
            _lastTurnT = now;

            if (!((_config.TurnBodyStanding && _idle && PlayerStands()) ||  // Stand: Option an und wirklich frei
                (_config.TurnBodyWalking && !_idle && PlayerMoving())))    // Bewegung: Option an und läuft
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
            var yaw = MathF.Atan2(fwd.X, fwd.Z) - _config.TurnCenterDeg * Deg;
            if (_config.DebugEmotes && now - _lastTurnLog > 0.5) { _lastTurnLog = now; _log.Debug($"Kopf-Yaw rel. Körper: {yaw / Deg:0.0}°"); }

            var k = Math.Clamp((MathF.Abs(yaw) - _config.TurnStartDeg * Deg) / (30f * Deg), 0f, 1f);   // weich einblenden
            if (k <= 0f) return;

            var nr = chara->GameObject.Rotation + MathF.Sign(yaw) * k * _config.TurnSpeedDeg * Deg * dt;
            if (nr > MathF.PI) nr -= 2f * MathF.PI; else if (nr < -MathF.PI) nr += 2f * MathF.PI;
            chara->GameObject.SetRotation(nr);
        }
    }
}

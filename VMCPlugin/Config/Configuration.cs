/*
 * Copyright(c) 2025 GiR-Zippo 
 * Licensed under the GPL v3 license. See https://github.com/GiR-Zippo/LightAmp/blob/main/LICENSE for full license information.
 */

using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace VMCPlugin.Config;


public enum TriggerKind { Mode, Pose, Emote, EmoteBlend, Combat }
[Serializable]
public sealed class PresetRule
{
    public bool Enabled { get; set; } = true;
    public TriggerKind Kind { get; set; } = TriggerKind.Combat;
    public int Value { get; set; }          // Mode-/Pose-/Emote-ID, bei Combat ohne Bedeutung
    public string Preset { get; set; } = "";   // Name des Presets
    public bool LimitAxis { get; set; } = false;

}

[Serializable]
public sealed class BonePreset
{
    public string Name { get; set; } = "Neu";
    public HashSet<string> Bones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[Serializable]
public sealed class EmoteRule
{
    public bool Enabled { get; set; } = true;
    public string Blend { get; set; } = "Joy";
    public float Threshold { get; set; } = 0.8f;
    public float HoldSec { get; set; } = 0.3f;
    public float CooldownSec { get; set; } = 5f;
    public string Command { get; set; } = "/joy";
}

[Serializable]
public sealed class FaceRule
{
    public bool Enabled { get; set; } = true;
    public string Blend { get; set; } = "Blink";
    public string Bone { get; set; } = "j_f_mab_l";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float RotX { get; set; }   // Grad bei Wert 1.0
    public float RotY { get; set; }
    public float RotZ { get; set; }
    [Newtonsoft.Json.JsonIgnore] public bool Force { get; set; }

    public static List<FaceRule> Defaults()
    {
        var l = new List<FaceRule>();
        void R(string blend, string bone, float px = 0, float py = 0, float pz = 0, float rx = 0, float ry = 0, float rz = 0)
            => l.Add(new FaceRule { Blend = blend, Bone = bone, PosX = px, PosY = py, PosZ = pz, RotX = rx, RotY = ry, RotZ = rz });
        void LR(string blend, string bone, float px = 0, float py = 0, float rz = 0)   // beide Seiten, nur spiegelsichere Achsen
        { R(blend, bone + "_l", px, py, 0, 0, 0, rz); R(blend, bone + "_r", px, py, 0, 0, 0, rz); }

        // Lider
        R("Blink_L", "j_f_mabup_01_l", rz: -30); R("Blink_L", "j_f_mabdn_01_l", rz: 15);
        R("Blink_R", "j_f_mabup_01_r", rz: -30); R("Blink_R", "j_f_mabdn_01_r", rz: 15);

        // Kiefer: j_ago (+) und j_f_ago (−) öffnen gemeinsam
        foreach (var (b, a) in new[] { ("A", 14f), ("O", 9f), ("E", 6f), ("I", 4f), ("U", 5f) })
        { R(b, "j_ago", rz: a); R(b, "j_f_ago", rz: -a); }

        // Mundwinkel
        LR("I", "j_f_uslip", px: .004f); LR("I", "j_f_dslip", px: .004f);
        LR("E", "j_f_uslip", px: .003f); LR("E", "j_f_dslip", px: .003f);
        LR("U", "j_f_uslip", px: -.004f); LR("U", "j_f_dslip", px: -.004f);
        LR("O", "j_f_uslip", px: -.003f); LR("O", "j_f_dslip", px: -.003f);

        // Emotionen
        LR("Joy", "j_f_uslip", px: .003f, py: .005f); LR("Joy", "j_f_dslip", px: .003f, py: .005f);
        LR("Joy", "j_f_hoho", py: .004f); LR("Joy", "j_f_mabdn_01", rz: 8);
        LR("Angry", "j_f_mayu", py: -.004f); LR("Angry", "j_f_mmayu", py: -.004f);
        LR("Sorrow", "j_f_mmayu", py: .004f);
        LR("Sorrow", "j_f_uslip", py: -.004f); LR("Sorrow", "j_f_dslip", py: -.004f);

        // Zähne zeigen beim Lachen
        LR("Joy", "j_f_ulip_01", py: .004f); LR("Joy", "j_f_umlip_01", py: .004f);
        LR("Joy", "j_f_dlip_01", py: -.003f); LR("Joy", "j_f_dmlip_01", py: -.003f);
        R("Joy", "j_ago", rz: 5); R("Joy", "j_f_ago", rz: -5);
        return l;
    }
}



[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool SelfInitialized { get; set; }

    #region General
    public bool Enabled { get; set; } = true;
    public bool FirstPersonLook { get; set; }
    #endregion

    #region VRM Settings
    public bool Vrm1 { get; set; }   // Modell in XR Animator ist VRM 1.0
    public float RetargetBlend { get; set; } = 1f;
    public float RetargetFadeSec { get; set; } = 0.4f;
    public float JitterDeg { get; set; } = 0.2f;
    #endregion

    #region Hair
    public bool HairSpring { get; set; }
    public string HairPrefix { get; set; } = "j_kami, j_ex";
    public float HairStiff { get; set; } = 60f;
    public float HairDamp { get; set; } = 7f;
    public float HairStrength { get; set; } = 1f;
    public bool HairOnlyIdle { get; set; } = true;
    public float HairIdleSpeed { get; set; } = 0.15f;   // m/s
    #endregion

    #region Deform Presets
    public List<PresetRule> PresetRules { get; set; } = new();

    public List<BonePreset> Presets { get; set; } = new();
    public int ActivePreset { get; set; }
    #endregion

    #region Face Tracking
    public bool FaceTracking { get; set; }
    public List<FaceRule> FaceRules { get; set; } = new();
    #endregion

    #region BodyTurn
    public bool TurnBodyStanding { get; set; } = false;
    public bool TurnBodyWalking { get; set; } = false;
    public float TurnStartDeg { get; set; } = 55f;
    public float TurnSpeedDeg { get; set; } = 60f;   // max °/s
    public float TurnCenterDeg { get; set; }   // Ruhe-Yaw des Kopfes (Abgleich)
    #endregion

    #region FPV Settings
    public float MaxPitchDown { get; set; } = -1.9f;
    public float MaxPitchUp { get; set; } = 1.3f;

    public float FirstPersonCameraCullingDistance { get; set; } = 4f;
    public bool AllowInGpose { get; set; } = false;
    public bool ReducedMotionInCombat { get; set; } = true;
    public bool ReducedMotion { get; set; } = true;
    public bool FullMotionInEmotes { get; set; } = true;
    public float FirstPersonHeadOffsetForward { get; set; } = 0.03f;
    public float FirstPersonHeadOffsetUpward { get; set; } = 0.0f;
    public float FirstPersonHeadOffsetSideward { get; set; } = 0.0f;
    public int FirstPersonHeadRotationPitch { get; set; } = 0;
    public int FirstPersonHeadPitchLimit { get; set; } = 100;
    public int FirstPersonHeadYawLimit { get; set; } = 120;
    public int FirstPersonFieldOfView { get; set; } = 78;
    public bool RemoveRollInFirstPerson { get; set; } = false;
    public bool ThirdPersonControl { get; set; } = false;
    public bool UseFurtherCameraForLargerMounts { get; set; } = true;

    public float yawFilterminCut { get; set; } = 0.3f;
    public float yawFilterBeta { get; set; } = 5f;

    public float pitchFilterminCut { get; set; } = 0.3f;
    public float pitchFilterBeta { get; set; } = 5f;

    public float pitchMultiply { get; set; } = 1.0f;
    public float yawMultiply { get; set; } = 1.0f;
    #endregion

    public List<EmoteRule> EmoteRules { get; set; } = new();
    public bool DebugEmotes { get; set; }


    [NonSerialized]
    private IDalamudPluginInterface pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface!.SavePluginConfig(this);
    }
}
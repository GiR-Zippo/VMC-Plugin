/*
 * Copyright(c) 2025 GiR-Zippo 
 * Licensed under the GPL v3 license. See https://github.com/GiR-Zippo/LightAmp/blob/main/LICENSE for full license information.
 */

using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace VMCPlugin.Config;

[Serializable]
public sealed class BonePreset
{
    public string Name { get; set; } = "Neu";
    public HashSet<string> Bones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}



[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public float DebugMaxDistance { get; set; } = 10f;
    public bool SelfInitialized { get; set; }
    public List<BonePreset> Presets { get; set; } = new();
    public int ActivePreset { get; set; }
    public bool Vrm1 { get; set; }   // Modell in XR Animator ist VRM 1.0
    public float RetargetBlend { get; set; } = 1f;
    public float RetargetFadeSec { get; set; } = 0.4f;
    public float JitterDeg { get; set; } = 0.2f;

    public bool FirstPersonLook { get; set; }

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
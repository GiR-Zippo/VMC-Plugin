using System;
using System.Collections.Generic;
using VMCPlugin.Config;
using VMCPlugin.Offsets;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.AozNoteModule;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        private byte stMode, stPose;
        private ushort stEmote;
        private bool stCombat;
        private string activeName = "";

        public string StateText => $"Mode {stMode} | Pose {stPose} | Emote {stEmote} | Kampf {stCombat} | Preset: {activeName}";

        private HashSet<string>? ActiveSet => activeSet;
        private bool ActiveSetLimitAxis => limitAxis;

        private HashSet<string>? activeSet;
        private bool limitAxis;

        private unsafe void ReadState()
        {
            var c = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.GetLocalPlayer();
            if (c == null) return;
            stMode = (byte)c->Mode;
            stPose = (byte)c->EmoteController.CurrentPoseType;
            stEmote = c->EmoteController.EmoteId;
            stCombat = Api.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        }

        private bool Matches(PresetRule r) => r.Kind switch
        {
            TriggerKind.Mode => stMode == r.Value,
            TriggerKind.Pose => stPose == r.Value,
            TriggerKind.Emote => stEmote == r.Value,
            TriggerKind.EmoteBlend => stPose == r.Value,
            TriggerKind.Combat => stCombat,
            _ => false
        };

        private void ResolvePreset()   // einmal pro Frame
        {
            activeSet = null; limitAxis = false;
            if (config.Presets.Count == 0) return;

            var p = config.Presets[Math.Clamp(config.ActivePreset, 0, config.Presets.Count - 1)];
            foreach (var r in config.PresetRules)
            {
                if (!r.Enabled || !Matches(r)) continue;
                BonePreset? hit = null;
                foreach (var x in config.Presets)
                    if (x.Name == r.Preset) { hit = x; break; }   // ohne Lambda, ohne Allokation
                if (hit == null) continue;
                p = hit; limitAxis = r.LimitAxis;
                break;
            }
            activeSet = p.Bones;
            activeName = p.Name;
        }
    }
}

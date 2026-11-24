using System;
using System.Collections.Generic;
using VMCPlugin.Config;
using VMCPlugin.Offsets;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        private byte stMode, stPose;
        private ushort stEmote;
        private bool stCombat;
        private string activeName = "";

        public string StateText => $"Mode {stMode} | Pose {stPose} | Emote {stEmote} | Kampf {stCombat} | Preset: {activeName}";

        private HashSet<string>? ActiveSet => activePreset?.Bones;
        private bool ActiveSetLimitAxis => activeRule?.LimitAxis ?? false;

        private BonePreset? activePreset;
        private PresetRule? activeRule;

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

        private void ResolvePreset()   // einmal pro Frame, direkt nach ReadState()
        {
            activePreset = null; activeRule = null;
            if (config.Presets.Count == 0) return;

            activePreset = config.Presets[Math.Clamp(config.ActivePreset, 0, config.Presets.Count - 1)];
            foreach (var r in config.PresetRules)
            {
                if (!r.Enabled || !Matches(r)) continue;
                var hit = config.Presets.Find(x => x.Name == r.Preset);
                if (hit == null) continue;                 // gleiche Regel wie bei den Bones
                activePreset = hit; activeRule = r;
                break;
            }
            activeName = activePreset.Name;
        }
    }
}

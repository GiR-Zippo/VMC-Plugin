using System;
using System.Collections.Generic;
using VMCPlugin.Config;
using VMCPlugin.Offsets;

namespace VMCPlugin.BoneManipulation
{
    public unsafe partial class VmcHeadReceiver
    {
        private byte _stMode, _stPose;
        private ushort _stEmote;
        private bool _stCombat, _animPose, _enterPose;
        private string _activeName = "";

        private PresetRule? _lastRule;
        private int _poseSig;

        /// <summary>
        /// Gets a formatted diagnostic string showing the current character mode, pose, emote, combat state, and active preset name.
        /// </summary>
        public string StateText => $"Mode {_stMode} | Pose {_stPose} | Emote {_stEmote} | Kampf {_stCombat} | Preset: {_activeName}";

        /// <summary>
        /// Gets the bone set of the currently resolved active preset, or <see langword="null"/> if none is active.
        /// </summary>
        private HashSet<string>? ActiveSet => activeSet;

        /// <summary>
        /// Gets a value indicating whether axis limiting is active for the currently resolved preset rule.
        /// </summary>
        private bool ActiveSetLimitAxis => limitAxis;

        private HashSet<string>? activeSet;
        private bool limitAxis;

        /// <summary>
        /// Reads the local player's current character mode, pose type, emote ID, and combat status from the game state.
        /// </summary>
        private unsafe void ReadState()
        {
            var c = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.GetLocalPlayer();
            if (c == null) return;
            _stMode = (byte)c->Mode;
            _stPose = (byte)c->EmoteController.CurrentPoseType;
            _stEmote = c->EmoteController.EmoteId;
            _stCombat = Api.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];

        }

        /// <summary>
        /// Evaluates whether a preset rule's trigger condition matches the current character state.
        /// </summary>
        /// <param name="r">The preset rule to evaluate.</param>
        /// <returns><see langword="true"/> if the rule's trigger matches the current state; otherwise, <see langword="false"/>.</returns>
        private bool Matches(PresetRule r) => r.Kind switch
        {
            TriggerKind.Mode => _stMode == r.Value,
            TriggerKind.Pose => _stPose == r.Value,
            TriggerKind.Emote => _stEmote == r.Value,
            TriggerKind.EmoteStart => _stPose == r.ValueB && _stEmote == r.Value,
            TriggerKind.EmoteEnd => _stMode == 1 && _stPose == r.ValueB && _stEmote == r.Value,
            TriggerKind.Combat => _stCombat,
            _ => false
        };

        /// <summary>
        /// Resolves the active bone preset using the first matching enabled rule (falling back to the configured preset) and updates pose-entry tracking.
        /// </summary>
        private void ResolvePreset()
        {
            activeSet = null;
            limitAxis = false;
            PresetRule? rule = null;

            var presets = _config.Presets;
            if (presets.Count == 0)
            {
                _animPose = false;
                _lastRule = null;
                return;
            }

            //one day I've been to party
            var p = presets[Math.Clamp(_config.ActivePreset, 0, presets.Count - 1)];
            foreach (var r in _config.PresetRules)
            {
                if (!r.Enabled || !Matches(r))
                    continue;

                var hit = FindPresetByName(presets, r.Preset);
                if (hit == null)
                    continue;

                //to the end
                p = hit;
                rule = r;
                limitAxis = r.LimitAxis;
                break;
            }

            //looking for the after party
            if (rule == null)
            {
                foreach (var r in _config.PresetRules)
                {
                    if (r.Kind != TriggerKind.Default || !r.Enabled)
                        continue;

                    //to begin
                    var defPreset = FindPresetByName(presets, r.Preset);
                    if (defPreset != null)
                    {
                        p = defPreset;
                        break;
                    }
                }
            }

            //I'm going down to
            activeSet = p.Bones;
            _activeName = p.Name;

            //Lala land
            var sig = (_stMode << 24) | (_stPose << 16) | _stEmote;
            if (limitAxis && (!_animPose || !ReferenceEquals(rule, _lastRule) || sig != _poseSig))
            {
                Api.PluginLog.Debug($"Pose-Eintritt sig={sig} (Mode {_stMode}, Pose {_stPose}, Emote {_stEmote})");
                _enterPose = true;
            }

            _animPose = limitAxis;
            _lastRule = rule;
            _poseSig = sig;
        }

        /// <summary>
        /// Searches the given preset list for a preset matching the specified name using an ordinal comparison.
        /// </summary>
        /// <param name="presets">The list of available bone presets.</param>
        /// <param name="name">The preset name to search for.</param>
        /// <returns>The matching <see cref="BonePreset"/> if found; otherwise, <see langword="null"/>.</returns>
        private static BonePreset? FindPresetByName(List<BonePreset> presets, string? name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            for (var i = 0; i < presets.Count; i++)
            {
                if (string.Equals(presets[i].Name, name, StringComparison.Ordinal))
                    return presets[i];
            }

            return null;
        }
    }
}
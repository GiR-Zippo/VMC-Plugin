using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace VMCPlugin.BoneManipulation;

public partial class VmcHeadReceiver : IDisposable
{
    /// <summary>
    /// Share of hand twist distributed to the forearm.
    /// </summary>
    public float TwistShare { get; set; } = 0.6f;   // Anteil Hand-Twist -> Unterarm

    private Dictionary<string, (float swing, float twist)> _limits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuilds the swing and twist angle limit lookup table from active configuration rules.
    /// </summary>
    public void RebuildLimits()
    {
        var d = new Dictionary<string, (float, float)>(StringComparer.OrdinalIgnoreCase);
        var rules = _config.RotLimits;
        foreach (var v in AllBones)
        {
            var n = v.StartsWith("Left") ? v[4..] : v.StartsWith("Right") ? v[5..] : v;
            var r = rules.FirstOrDefault(x => x.Enabled && x.Bone.Length > 0 && n.Equals(x.Bone, StringComparison.OrdinalIgnoreCase))
                 ?? rules.FirstOrDefault(x => x.Enabled && x.Bone.Length > 0 && n.EndsWith(x.Bone, StringComparison.OrdinalIgnoreCase));
            if (r != null)
                d[v] = (r.Swing, r.Twist);
        }
        _limits = d;
    }

    /// <summary>
    /// Resolves the local twist axis for a VMC bone by name.
    /// </summary>
    /// <param name="vmc">Name of the VMC bone.</param>
    /// <returns>Unit vector of the twist axis in local space.</returns>
    private static Vector3 TwistAxis(string vmc)
    {
        var n = vmc.StartsWith("Left") ? vmc[4..] : vmc.StartsWith("Right") ? vmc[5..] : vmc;
        if (n is "Foot" or "Toes") 
            return Vector3.UnitZ;
        if (n.Contains("Arm") || n is "Shoulder" or "Hand" || n.StartsWith("Thumb") || n.StartsWith("Index")
            || n.StartsWith("Middle") || n.StartsWith("Ring") || n.StartsWith("Little"))
            return Vector3.UnitX;
        return Vector3.UnitY;
    }

    /// <summary>
    /// Decomposes a quaternion rotation along a given axis into swing and twist components.
    /// </summary>
    /// <param name="q">Input quaternion.</param>
    /// <param name="axis">Normalized twist axis.</param>
    /// <param name="swing">Rotation component perpendicular to the axis.</param>
    /// <param name="twist">Rotation component around the axis.</param>
    private static void SwingTwist(Quaternion q, Vector3 axis, out Quaternion swing, out Quaternion twist)
    {
        var p = Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis) * axis;
        twist = new Quaternion(p, q.W);
        twist = twist.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(twist);
        swing = Quaternion.Normalize(q * Quaternion.Conjugate(twist));
    }

    /// <summary>
    /// Transfers a fraction of a child bone's twist onto its parent bone and compensates the child.
    /// </summary>
    /// <param name="loc">Dictionary of local bone rotations.</param>
    /// <param name="child">Name of the child bone whose twist is shared.</param>
    /// <param name="parent">Name of the parent bone receiving the twist.</param>
    /// <param name="share">Distribution factor in the range of 0 to 1.</param>
    private static void ShareTwist(Dictionary<string, Quaternion> loc, string child, string parent, float share)
    {
        if (!loc.TryGetValue(child, out var c) || !loc.TryGetValue(parent, out var p))
            return;
        SwingTwist(c, TwistAxis(child), out _, out var tw);
        var part = Quaternion.Slerp(Quaternion.Identity, tw, share);
        loc[parent] = Quaternion.Normalize(p * part);
        loc[child] = Quaternion.Normalize(Quaternion.Conjugate(part) * c);   // Welt-Rotation des Childs bleibt gleich
    }

    /// <summary>
    /// Distributes arm and hand twist to parent bones and applies configured swing and twist limits to local rotations.
    /// </summary>
    /// <param name="loc">Dictionary of local bone rotations updated in place.</param>
    private void LimitLocals(Dictionary<string, Quaternion> loc)
    {
        foreach (var side in new[] { "Left", "Right" })
        {
            ShareTwist(loc, side + "Hand", side + "LowerArm", TwistShare);
            ShareTwist(loc, side + "LowerArm", side + "UpperArm", TwistShare * 0.5f);
        }

        const float d2r = MathF.PI / 180f;
        foreach (var k in loc.Keys.ToList())
        {
            if (!_limits.TryGetValue(k, out var lim))
                continue;
            var (sw, tw) = lim;
            if (sw <= 0)
                continue;
            SwingTwist(loc[k], TwistAxis(k), out var s, out var t);
            loc[k] = Quaternion.Normalize(SoftLimit(s, sw * d2r) * SoftLimit(t, tw * d2r));
        }
    }
}

using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// A charge-and-release power's look on a power that isn't one (Kurt, 2026-11-04: Punisher's Ol' Painless with Human Torch's
/// Extreme Combustion "doesn't do much"). A charge-up power runs its charge (a looping animation, glows) from
/// power_on_variable_powerup, the moment the button goes down, until power_on_start, the release, where its burst plays. Other
/// powers never reach power_on_variable_powerup: the charge never played and the burst came once at the start. Here the
/// borrowed components are moved onto the target's own moments: the charge from its start; the release at a channel's end
/// (power_on_loop_end: the minigun's whole hold charges, letting go bursts) or at a single attack's hit.
/// </summary>
static partial class PowerBorrow
{
    const string ChargePoint = "power_on_variable_powerup";

    /// <summary>A power package's class default has a component on the charge-up moment (power_on_variable_powerup).</summary>
    static bool HasChargeUp(Package pk, int def)
    {
        foreach (int r in Components(pk, def, out _, out _))
        {
            if (r <= 0) continue;
            byte[] b = pk.ReadExportBytes(pk.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(pk, b, 16) is { } tags && tags.Any(t => t.Name.EndsWith("ActivationPoint", StringComparison.OrdinalIgnoreCase) && t.Size == 8
                && TagWalker.NameAt(pk, b, t.ValueAt).Equals(ChargePoint, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>The power's own looping animation component (not a borrowed one): an export under its class default, still in the
    /// package after the borrow (Ol' Painless' anim1), else null.</summary>
    static int? OwnLooping(Package p, int def)
    {
        for (int i = 0; i < p.Exports.Length; i++)
            if (p.Exports[i].OuterIndex == def + 1 && p.ClassOf(p.Exports[i]).Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase)
                && !p.Exports[i].ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>
    /// The borrowed components (borrowed_…) of <paramref name="targetDefault"/> moved off the charge-up moments: activation or
    /// deactivation at power_on_variable_powerup → the charge: power_on_loop_start on a channel (its hold), power_on_start
    /// otherwise; at power_on_start (set, or by the class's default) → the release: power_on_loop_end on a channel,
    /// power_at_contact_time otherwise. The package unchanged when nothing moves.
    /// </summary>
    static byte[] RemapChargeUp(byte[] package, string targetDefault, bool channel, Fx.FxGame game, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, targetDefault);
        if (td < 0) return package;
        string release = channel ? "power_on_loop_end" : "power_at_contact_time";
        // a channel's hold starts at power_on_loop_start: the stock minigun's muzzle flashes and hits run from there, and a part on
        // power_on_start never even came up (telemetry ride66, Kurt 2026-11-05: the charge "wasn't doing much"; only the release played)
        string charge = channel ? "power_on_loop_start" : "power_on_start";
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var moved = new List<string>();
        // On a channel, the release's own animation component (a plain PowerFxAnimation: Torch's startanim, his burst) goes into
        // the looping animation's end slot instead, and off the list. With a plain animation component in the power, the looping
        // one never started at the press (rides 66 to 70); the Storm build that worked has none, and its looping animation ends
        // with its own end animation. Controlled test (ride71, Kurt 2026-11-05): that Storm build playing Torch's charge
        // animations started the charge, so the animations weren't the gate.
        var dropped = new List<int>();
        string? endAnim = null;
        if (channel && Components(p, td, out _, out _).Any(r => r > 0 && p.ClassOf(p.Exports[r - 1]).Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase)
                && p.Exports[r - 1].ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase)))
            foreach (int r in Components(p, td, out _, out _).Distinct())
            {
                if (r <= 0 || !p.Exports[r - 1].ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase)
                    || !p.ClassOf(p.Exports[r - 1]).Equals("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] ab = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
                if (TagWalker.Walk(p, ab, 16)?.FirstOrDefault(t => t.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } an)
                    endAnim ??= TagWalker.NameAt(p, ab, an.ValueAt);
                dropped.Add(r);
            }
        // the looping animation's end slot (PowerAnims: start, loop, end) set to the release animation
        byte[] WithEnd(byte[] data)
        {
            if (endAnim == null || TagWalker.Walk(p, data, 16)?.FirstOrDefault(t => t.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase)) is not { } pa
                || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pa.ValueAt)) < 3) return data;
            byte[] o = (byte[])data.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(pa.ValueAt + 4 + 16), NameIdx(endAnim));
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(pa.ValueAt + 8 + 16), 0);
            return o;
        }
        foreach (int r in Components(p, td, out _, out _).Distinct())
        {
            if (r <= 0 || !p.Exports[r - 1].ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase) || dropped.Contains(r)) continue;
            string cls = p.ClassOf(p.Exports[r - 1]);
            if (!cls.StartsWith("PowerFx", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] b = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(p, b, 16) is not { } tags) continue;
            var changes = new List<string>();
            // On a channel the looping animation keeps its class's moments (start to end, as Storm's Lightning Conduit has them,
            // which worked on Ol' Painless: ride55): its timing points are taken off. Moved to power_on_start (ride66) or
            // power_on_loop_start (ride67), it never came up, and neither did any part on the channel's hold.
            if (channel && cls.Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase))
            {
                // The target's own looping animation, byte for byte, playing the borrowed animations (only PowerAnims swapped):
                // every other property as on a component known to start this power's loop at the press (Ol' Painless' anim1:
                // blend 0.2 / 0.2, include attachments). With the borrowed one's own properties (none of those) it was never even
                // considered at the press, rides 66 to 69, though Storm's (blend 0.2 / 0.2) was in ride55.
                if (OwnLooping(p, td) is int own && tags.FirstOrDefault(t => t.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase)) is { } bpa)
                {
                    byte[] ob = p.ReadExportBytes(p.Exports[own]).ToArray();
                    if (TagWalker.Walk(p, ob, 16)?.FirstOrDefault(t => t.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase)) is { } opa && opa.Size == bpa.Size)
                    {
                        byte[] cb0 = (byte[])ob.Clone();
                        b.AsSpan(bpa.ValueAt, bpa.Size).CopyTo(cb0.AsSpan(opa.ValueAt));
                        byte[] cb = WithEnd(cb0);
                        rep[r - 1] = _ => cb;
                        moved.Add($"{p.Exports[r - 1].ObjectName} (as {p.Exports[own].ObjectName}, this power's own looping animation, playing the borrowed animations)");
                        continue;
                    }
                }
                // ... and LoopIndefinitely off (a charge-up's charge loops until released; a channel's loop ends with the channel:
                // neither Storm's working Lightning Conduit nor Ol' Painless' own animation has it, and with it the borrowed
                // animation was never even considered at the press, ride68)
                var pts = tags.Where(t => (t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase)) && t.Size == 8
                        || t.Name.Equals("LoopIndefinitely", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Start).ToList();
                if (pts.Count == 0) continue;
                byte[] lb0 = b;
                foreach (var t in pts) lb0 = [.. lb0.AsSpan(0, t.Start), .. lb0.AsSpan(t.End)];
                byte[] lb = WithEnd(lb0);
                rep[r - 1] = _ => lb;
                moved.Add($"{p.Exports[r - 1].ObjectName} (its class's start and end, not looping on its own)");
                continue;
            }
            string? Now(string prop) => tags.FirstOrDefault(t => t.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } t ? TagWalker.NameAt(p, b, t.ValueAt) : null;
            string Next(string at) => at.Equals(ChargePoint, StringComparison.OrdinalIgnoreCase) ? charge
                : at.Equals("power_on_start", StringComparison.OrdinalIgnoreCase) ? release : at;
            // (both moments read before either changes: the charge's end, power_on_start, becomes the release too)
            string act = Now("ActivationPoint") ?? game.DefaultActivationPoint(cls);
            string? deact = Now("DeactivationPoint");
            // (a part that stops at the donor's hit, after its release: on a channel, at the channel's end; the charge sound)
            string newAct = Next(act), newDeact = deact == null ? "" : channel && deact.Equals("power_at_contact_time", StringComparison.OrdinalIgnoreCase) ? release : Next(deact);
            byte[] nb = b;
            if (!newAct.Equals(act, StringComparison.OrdinalIgnoreCase))
            {
                if (tags.FirstOrDefault(t => t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } at)
                    BinaryPrimitives.WriteInt32LittleEndian(nb.AsSpan(at.ValueAt), NameIdx(newAct));
                else
                {
                    // the class's default start (power_on_start): an ActivationPoint tag added before None
                    // (ByteProperty, size 8, enum EActivationPoint, then the value's name)
                    var tag = new byte[40];
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("ActivationPoint"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ByteProperty"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 8);
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(24), NameIdx("EActivationPoint"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(32), NameIdx(newAct));
                    nb = [.. nb.AsSpan(0, tags.NoneAt), .. tag, .. nb.AsSpan(tags.NoneAt)];
                }
                changes.Add($"on {act} → {newAct}");
            }
            if (deact != null && !newDeact.Equals(deact, StringComparison.OrdinalIgnoreCase)
                && tags.FirstOrDefault(t => t.Name.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } dt)
            {
                BinaryPrimitives.WriteInt32LittleEndian(nb.AsSpan(dt.ValueAt), NameIdx(newDeact));   // (before the added tag: offsets unchanged)
                changes.Add($"off {deact} → {newDeact}");
            }
            if (changes.Count == 0) continue;
            byte[] fin = nb;
            rep[r - 1] = _ => fin;
            moved.Add($"{p.Exports[r - 1].ObjectName} ({string.Join(", ", changes)})");
        }
        // the dropped animation components off the class default's list
        if (dropped.Count > 0)
        {
            var list = Components(p, td, out var ctag, out byte[] cdata);
            var keep = list.Where(x => !dropped.Contains(x)).ToList();
            var arr = new byte[4 + 4 * keep.Count];
            BinaryPrimitives.WriteInt32LittleEndian(arr, keep.Count);
            for (int k = 0; k < keep.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(arr.AsSpan(4 + 4 * k), keep[k]);
            byte[] nd = [.. cdata.AsSpan(0, ctag.ValueAt), .. arr, .. cdata.AsSpan(ctag.ValueAt + ctag.Size)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(ctag.Start + 16), arr.Length);
            rep[td] = _ => nd;
            moved.Add($"{string.Join(", ", dropped.Select(x => p.Exports[x - 1].ObjectName))} off the list, its animation {endAnim} the looping animation's end");
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the charge-up remap: " + string.Join("; ", problems.Take(3)));
        log.Add($"  charge-up → {(channel ? "channel" : "attack")}: the charge from {(channel ? "the hold" : "the start")}, the release at {(channel ? "the channel's end" : "the hit")}: {string.Join("; ", moved)}");
        return output;
    }
}

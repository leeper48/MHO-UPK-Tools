using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Its Triggered Powers' Look Too (a user, 2026-11-09: Magik's Sorcerous Eruption borrowing Venom's Symbiotic Spikes showed
/// Venom's tendrils but no ground spikes, only Magik's own AoE). A power's look can come partly from the powers it sets off:
/// Venom's ground spikes are SigFreakoutComboImpale's (vfx_freakout_spikes / vfx_freakout_aoe at socket_ground), and Magik's
/// AoE is SorcerousEruptionCombo's. The server sets off the target's own powers, so the donor's never play; here each of the
/// target's triggered powers with a look of its own takes the components of the donor's triggered power that draws the most
/// away from the hit (effects and sounds only: no animation, no props). What they do in the game stays the target's.
/// </summary>
static partial class PowerBorrow
{
    /// <summary>A target triggered power (its class, package) and the donor's whose look it takes.</summary>
    public sealed record TriggeredPair(string TargetClass, string TargetFile, string TargetName, string DonorClass, string DonorFile, string DonorName);

    static readonly System.Text.RegularExpressions.Regex ExtraPowers = new(@"Ultimate|Talent|Signature|Specialization", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The pairs for <paramref name="targetPower"/> borrowing <paramref name="donorPower"/>: the powers each sets off directly
    /// (depth 1, not through an ultimate, talent, signature or specialization, not a projectile) that draw something away from
    /// the one hit (particles on the ground or at the target, decals). Every target one takes the donor's best (most such
    /// effects). Empty when either side has none.
    /// </summary>
    public static List<TriggeredPair> TriggeredPairs(Fx.GameData db, Fx.FxGame game, string targetPower, string donorPower, List<string>? notes = null)
    {
        var res = new List<TriggeredPair>();
        List<(Fx.PowerClosure.Art Art, string File, int Score)> Side(string power)
        {
            var list = new List<(Fx.PowerClosure.Art, string, int)>();
            Fx.PowerEffects fx;
            try { fx = Fx.PowerEffects.For(game, db, power, HeroOf(power)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException) { return list; }
            foreach (var a in Fx.PowerClosure.Of(db, power))
            {
                if (!a.IsPower || a.Depth != 1 || ExtraPowers.IsMatch(a.Prototype) || a.Class.StartsWith("MarvelProjectile", StringComparison.OrdinalIgnoreCase)) continue;
                string by = Path.GetFileNameWithoutExtension(a.Prototype);
                int score = fx.Effects.Count(e => by.Equals(e.TriggeredBy, StringComparison.OrdinalIgnoreCase) && !e.OnHit && e.Kind == "power" && !e.Sockets.All(s => s.Equals("socket_hit", StringComparison.OrdinalIgnoreCase)))
                    + fx.Decals.Count(d => by.Equals(d.TriggeredBy, StringComparison.OrdinalIgnoreCase));
                if (score == 0) continue;
                string def = $"marvelgamecontent.default__{a.Class}".ToLowerInvariant();
                string? file = game.PackagesOf(a.Class).Select(game.FileFor).OfType<string>().FirstOrDefault(f => Find(Package.Open(f), def) >= 0);
                if (file == null) { notes?.Add($"{a.Class}: its package isn't found"); continue; }
                file = Path.GetDirectoryName(file) is string dir && Directory.Exists(dir) ? Directory.EnumerateFiles(dir, Path.GetFileName(file)).FirstOrDefault() ?? file : file;
                list.Add((a, file, score));
            }
            return list;
        }
        var t = Side(targetPower);
        if (t.Count == 0) return res;
        var d = Side(donorPower);
        if (d.Count == 0) return res;
        var best = d.OrderByDescending(x => x.Score).ThenBy(x => x.Art.Class, StringComparer.OrdinalIgnoreCase).First();
        foreach (var x in t)
            res.Add(new TriggeredPair(x.Art.Class, x.File, Pretty(Path.GetFileNameWithoutExtension(x.Art.Prototype)),
                best.Art.Class, best.File, Pretty(Path.GetFileNameWithoutExtension(best.Art.Prototype))));
        return res;
        static string Pretty(string n) => System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// <summary>The built triggered packages (by file name), each target triggered power's components the donor's (effects
    /// and sounds; its own animation and prop parts kept).</summary>
    public static Dictionary<string, byte[]> BuildTriggered(IReadOnlyList<TriggeredPair> pairs, Func<string, string?> start, string cooked, List<string> log)
    {
        var done = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pairs)
        {
            string tf = Path.GetFileName(p.TargetFile);
            byte[]? cur = done.TryGetValue(tf, out var b) ? b : start(tf) is string sf ? File.ReadAllBytes(sf) : null;
            if (cur == null) { log.Add($"triggered look: {tf} has no clean copy to start from"); continue; }
            log.Add($"triggered look: {p.TargetClass} ({tf}) ← {p.DonorClass} ({Path.GetFileName(p.DonorFile)})");
            done[tf] = Graft(cur, $"marvelgamecontent.default__{p.TargetClass}".ToLowerInvariant(), p.DonorFile, $"marvelgamecontent.default__{p.DonorClass}".ToLowerInvariant(),
                log, cooked: cooked, take: cls => PartOf(cls) == "effect");
        }
        return done;
    }
}

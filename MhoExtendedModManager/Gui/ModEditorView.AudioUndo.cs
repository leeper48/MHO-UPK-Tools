using System.Text.Json;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Audio tab's Undo / Redo / History (Kurt, 2026-10-31: "revert importing audio you don't like"): every change to the voice
/// and sound packs is a step, with what it was before and after. A step holds paths, not copies: each build writes to a folder
/// of its own, kept until the editor closes, so going back is instant. Only what the step changed is put back; a package
/// another tab rebuilt since is left alone (with a message).
/// </summary>
sealed partial class ModEditorView
{
    sealed record AudioState(Dictionary<string, string> Packages, List<string> SoundPacks, string Shifts, List<VoiceOffEntry> Off,
        Dictionary<(string, int), bool> Wanted, Dictionary<string, string> Before, VoiceShiftEntry Sliders, string Sounds);

    sealed record AudioStep(string Label, AudioState Was, AudioState Is);

    readonly Stack<AudioStep> audioUndo = new(), audioRedo = new();
    readonly List<Button> audioUndoButtons = [], audioRedoButtons = [];
    /// <summary>A build under way: the steps inside it are its own.</summary>
    bool audioBusy;

    AudioState AudioNow() => new(draft.Packages.ToDictionary(p => p.File, p => p.Source, StringComparer.OrdinalIgnoreCase), [.. draft.SoundPacks], JsonSerializer.Serialize(draft.VoiceShifts),
        [.. draft.VoiceOff], new(voiceWanted), new(voiceBefore, StringComparer.OrdinalIgnoreCase), Sliders, JsonSerializer.Serialize(draft.PowerSounds));

    static bool SameAudio(AudioState a, AudioState b) => a.Shifts == b.Shifts && a.Sounds == b.Sounds && a.SoundPacks.SequenceEqual(b.SoundPacks) && a.Off.SequenceEqual(b.Off)
        && a.Packages.Count == b.Packages.Count && a.Packages.All(kv => b.Packages.GetValueOrDefault(kv.Key) == kv.Value)
        && a.Wanted.Count == b.Wanted.Count && a.Wanted.All(kv => b.Wanted.TryGetValue(kv.Key, out bool v) && v == kv.Value);

    /// <summary>A change made since <paramref name="was"/> becomes a step (nothing changed: none).</summary>
    void AudioRecord(string label, AudioState was)
    {
        var now = AudioNow();
        if (SameAudio(was, now)) return;
        audioUndo.Push(new AudioStep(label, was, now));
        audioRedo.Clear();
        UpdateAudioButtons();
    }

    void UpdateAudioButtons()
    {
        foreach (var b in audioUndoButtons)
        {
            b.Enabled = audioUndo.Count > 0 && !shifting;
            Ui.SetTipText(b, Ui.Titled("Undo", audioUndo.Count > 0 ? $"{audioUndo.Peek().Label}  (Ctrl+Z)" : "Nothing to undo. Replacing lines and sounds, shifting the voice, another voice, ticking lines and sound packs can each be taken back (Ctrl+Z)."));
        }
        foreach (var b in audioRedoButtons)
        {
            b.Enabled = audioRedo.Count > 0 && !shifting;
            Ui.SetTipText(b, Ui.Titled("Redo", audioRedo.Count > 0 ? $"{audioRedo.Peek().Label}  (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)."));
        }
    }

    /// <summary>The audio as <paramref name="to"/> has it, coming from <paramref name="from"/>; false (with a message) when another
    /// tab rebuilt a package this step changed.</summary>
    bool AudioGo(AudioState from, AudioState to, string label)
    {
        var changed = from.Packages.Keys.Union(to.Packages.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(f => from.Packages.GetValueOrDefault(f) != to.Packages.GetValueOrDefault(f)).ToList();
        var mine = draft.Packages.ToDictionary(p => p.File, p => p.Source, StringComparer.OrdinalIgnoreCase);
        var moved = changed.Where(f => mine.GetValueOrDefault(f) != from.Packages.GetValueOrDefault(f)).ToList();
        if (moved.Count > 0)
        {
            Dialog.Show(this, $"\"{label}\" can't be taken back: {string.Join(", ", moved)} changed on another tab after it, and going back would lose that.", "Not Undone", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        foreach (string f in changed)
        {
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (to.Packages.GetValueOrDefault(f) is not string src) { if (k >= 0) draft.Packages.RemoveAt(k); }
            else if (k >= 0) draft.Packages[k] = (f, src);
            else draft.Packages.Add((f, src));
        }
        draft.SoundPacks.Clear(); draft.SoundPacks.AddRange(to.SoundPacks);
        draft.VoiceShifts.Clear(); draft.VoiceShifts.AddRange(JsonSerializer.Deserialize<List<VoiceShiftEntry>>(to.Shifts) ?? []);
        draft.VoiceOff.Clear(); draft.VoiceOff.AddRange(to.Off);
        draft.PowerSounds.Clear(); draft.PowerSounds.AddRange(JsonSerializer.Deserialize<List<PowerSoundEntry>>(to.Sounds) ?? []);
        voiceWanted.Clear(); foreach (var (k, v) in to.Wanted) voiceWanted[k] = v;
        voiceBefore.Clear(); foreach (var (k, v) in to.Before) voiceBefore[k] = v;
        var s = to.Sliders;
        foreach (var (sl, v) in new[] { (voicePitch, s.Pitch), (voiceFormant, s.Formant), (voiceWarmth, s.Warmth), (voiceReverb, s.Reverb), (voiceEcho, s.Echo), (voiceDistortion, s.Distortion), (voiceLoFi, s.LoFi), (voiceChorus, s.Chorus), (voiceRobot, s.Robot) })
            if (sl != null) sl.Value = v;
        VoiceAudio.Stop();
        voiceWavs.Clear();
        RefreshPackages(); RefreshSounds(); RefreshVoice(); ShowShiftNote(); FillSounds();
        return true;
    }

    void AudioUndo()
    {
        if (shifting || audioUndo.Count == 0) return;
        var step = audioUndo.Peek();
        if (!AudioGo(step.Is, step.Was, step.Label)) return;
        audioRedo.Push(audioUndo.Pop());
        voiceStatus.ForeColor = Ui.Subtle; voiceStatus.Text = "Undone: " + step.Label;
        UpdateAudioButtons();
    }

    void AudioRedo()
    {
        if (shifting || audioRedo.Count == 0) return;
        var step = audioRedo.Peek();
        if (!AudioGo(step.Was, step.Is, step.Label)) return;
        audioUndo.Push(audioRedo.Pop());
        voiceStatus.ForeColor = Ui.Subtle; voiceStatus.Text = "Redone: " + step.Label;
        UpdateAudioButtons();
    }

    /// <summary>History: every step (the oldest first, the one now marked); a pick undoes or redoes to it.</summary>
    void ShowAudioHistory(Button at)
    {
        var names = new List<string> { "Start" };
        names.AddRange(audioUndo.Reverse().Select(s => s.Label));
        int now = names.Count - 1;
        names.AddRange(audioRedo.Select(s => s.Label));
        DropList.Show(at, at.RectangleToScreen(at.ClientRectangle),
            [.. names.Select((t, i) => (i == now ? "▶ " : "    ") + t + (i > now ? " (undone)" : ""))], now, AudioJump, i => i > now ? Ui.Subtle : i == now ? Ui.Accent : null);
    }

    void AudioJump(int pick)
    {
        for (int k = audioUndo.Count; k > pick && audioUndo.Count > 0; k--) { int was = audioUndo.Count; AudioUndo(); if (audioUndo.Count == was) return; }
        for (int k = audioUndo.Count; k < pick && audioRedo.Count > 0; k++) { int was = audioRedo.Count; AudioRedo(); if (audioRedo.Count == was) return; }
    }

    /// <summary>A set of Undo, Redo and History buttons for a tab's bar (the Voice and Power Sounds tabs; all sets share the steps;
    /// Ctrl+Z / Ctrl+Y work on Sound Packs too).</summary>
    Control[] AudioButtons()
    {
        var undoB = Ui.FlatButton("Undo", AudioUndo, "Nothing to undo.");
        var redoB = Ui.FlatButton("Redo", AudioRedo, "Nothing to redo.");
        Button? historyB = null;
        historyB = Ui.FlatButton("History", () => ShowAudioHistory(historyB!), "Every Audio step by name, from when this mod was opened. Click one to go back or forward to it.");
        Icons.Make(undoB, "Undo", Icons.Undo, S); Icons.Make(redoB, "Redo", Icons.Redo, S); Icons.Make(historyB, "History", Icons.History, S);
        audioUndoButtons.Add(undoB); audioRedoButtons.Add(redoB);
        UpdateAudioButtons();
        return [undoB, redoB, historyB];
    }

    /// <summary>Test (--voice-undo-test): two lines replaced one at a time, a shift, undo and redo, History, a line off.</summary>
    internal async Task<int> TestAudioUndo(string tone, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        for (int i = 0; i < 100 && voiceLines.All(l => !l.Sound); i++) await Task.Delay(100);
        var lines = voiceLines.Where(l => l.Sound && !l.Missing && !l.Off).DistinctBy(l => l.Event).ToList();
        if (lines.Count < 2) { Check(false, "the mod has a voice"); return fails; }
        string a = VoiceLines.Leaf(lines[0].Event), b = VoiceLines.Leaf(lines[1].Event);
        string src0 = draft.Packages.First(p => p.File.Equals(lines[0].Package, StringComparison.OrdinalIgnoreCase)).Source;
        int packs0 = draft.SoundPacks.Count;
        Check(audioUndoButtons.All(b => !b.Enabled), "nothing to undo at the start");
        await PutLines([lines[0]], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [a] = tone }, "Replace Line: " + lines[0].Situation);
        string src1 = draft.Packages.First(p => p.File.Equals(lines[0].Package, StringComparison.OrdinalIgnoreCase)).Source;
        Check(src1 != src0 && draft.VoiceShifts.Any(x => x.IsReplaced(a)) && audioUndo.Count == 1, $"replaced: a step ({audioUndo.Peek().Label})");
        await PutLines([lines[1]], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [b] = tone }, "Replace Line: " + lines[1].Situation);
        Check(audioUndo.Count == 2 && draft.VoiceShifts.Any(x => x.IsReplaced(a) && x.IsReplaced(b)), "the second too");
        AudioUndo();
        Check(audioRedo.Count == 1 && draft.VoiceShifts.Any(x => x.IsReplaced(a) && !x.IsReplaced(b)) && draft.Packages.First(p => p.File.Equals(lines[0].Package, StringComparison.OrdinalIgnoreCase)).Source == src1, "undo: the second line back, the first stays");
        AudioRedo();
        Check(draft.VoiceShifts.Any(x => x.IsReplaced(b)) && audioUndo.Count == 2, "redo: it's replaced again");
        voiceWanted[(lines[0].Package, lines[0].Offset)] = false;
        var was = AudioNow(); was = was with { Wanted = [] };
        AudioRecord("Line Off", was);
        AudioUndo();
        Check(!voiceWanted.ContainsKey((lines[0].Package, lines[0].Offset)), "a line ticked off is a step");
        audioRedo.Clear();
        AudioJump(0);
        Check(audioUndo.Count == 0 && draft.VoiceShifts.Count == 0 && draft.SoundPacks.Count == packs0 && draft.Packages.First(p => p.File.Equals(lines[0].Package, StringComparison.OrdinalIgnoreCase)).Source == src0, "History to the start: as it was opened");
        AudioJump(2);
        Check(audioRedo.Count == 0 && draft.VoiceShifts.Any(x => x.IsReplaced(a) && x.IsReplaced(b)), "History to the end");
        var moved = draft.Packages.FindIndex(p => p.File.Equals(lines[0].Package, StringComparison.OrdinalIgnoreCase));
        var keep = draft.Packages[moved];
        draft.Packages[moved] = (keep.File, keep.Source + ".othertab");
        var d0 = audioUndo.Count;
        AudioUndo();
        Check(audioUndo.Count == d0, "a package another tab rebuilt isn't overwritten");
        draft.Packages[moved] = keep;
        return fails;
    }
}

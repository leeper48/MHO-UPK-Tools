using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// From the Game's lists in three tabs (Kurt, 2026-10-26: Source and Target): Heroes (heroes' costumes, with their summons and
/// power models under each hero), Team-Ups, and NPCs (NPCs, enemies and bosses). The filter searches the tab shown; each
/// tab's title has its count (under the filter). A pick made elsewhere (a saved source, a right-click, Edit in Blender's own
/// model) shows its tab.
/// </summary>
sealed partial class ModelPage
{
    static readonly string[] KindTitles = ["Heroes", "Team-Ups", "NPCs"];
    static readonly string[] KindTips =
    [
        "Heroes' costume packages, with each hero's summons and power models under the hero.",
        "Team-up packages.",
        "NPCs, enemies and bosses (the ones with the heroes' skeleton).",
    ];

    int sourceKindTab, targetKindTab;
    readonly List<Label> sourceKindTabs = [], targetKindTabs = [];
    Control sourceKindStrip = null!, targetKindStrip = null!;

    /// <summary>A From the Game package's tab: 0 heroes (and their summons), 1 team-ups, 2 NPCs, enemies and bosses.</summary>
    static int KindOf(string file) =>
        IsSummon(file) || (file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) ? 0
        : file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    Control KindStrip(List<Label> labels, Func<int> current, Action<int> pick)
    {
        var strip = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) };
        for (int k = 0; k < KindTitles.Length; k++)
        {
            int index = k;
            var tab = new Label { Text = KindTitles[k], AutoSize = true, Padding = new Padding(6, 3, 6, 5), Cursor = Cursors.Hand, Font = Ui.Regular(9f), ForeColor = k == 0 ? Ui.Text : Ui.Subtle, BackColor = Color.Transparent, Margin = new Padding(0, 0, 4, 0) };
            tab.Click += (_, _) => pick(index);
            tab.Paint += (_, e) =>
            {
                if (current() != index) return;
                using var b = new SolidBrush(Ui.Accent);
                float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
                e.Graphics.FillRectangle(b, 5 * s, tab.Height - 3 * s, tab.Width - 10 * s, 2.5f * s);
            };
            Ui.Tip(tab, KindTips[k]);
            labels.Add(tab);
            strip.Controls.Add(tab);
        }
        return strip;
    }

    void InitKindTabs()
    {
        sourceKindStrip = KindStrip(sourceKindTabs, () => sourceKindTab, k => { if (k == sourceKindTab) return; sourceKindTab = k; FillCharacters(); });
        targetKindStrip = KindStrip(targetKindTabs, () => targetKindTab, k => { if (k == targetKindTab) return; targetKindTab = k; FillPackages(); });
    }

    /// <summary>The tabs' titles with their counts (the packages the filter lets through) and the shown one lit.</summary>
    static void ShowKindTabs(List<Label> labels, int current, IEnumerable<(string File, string Title, string Detail)> shown)
    {
        var counts = new int[KindTitles.Length];
        foreach (var p in shown) counts[KindOf(p.File)]++;
        for (int k = 0; k < labels.Count; k++)
        {
            labels[k].Text = $"{KindTitles[k]} ({counts[k]:N0})";
            labels[k].ForeColor = k == current ? Ui.Text : Ui.Subtle;
            labels[k].Invalidate();
        }
    }

    /// <summary>The source tab that holds this game package (a pick made from elsewhere shows there).</summary>
    void SourceTabFor(string file) => sourceKindTab = KindOf(Path.GetFileName(file));
}

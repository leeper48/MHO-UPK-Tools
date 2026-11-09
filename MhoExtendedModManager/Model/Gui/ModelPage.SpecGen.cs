using System.Drawing;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Generate Spec Map and Generate Spec Color Map (the Materials tab's right-click menu, Kurt 2026-11-08), beside Generate Normal
/// Map. Spec: the shine made from the color map by one of SpecMapGen's recipes (Soft, Strong, Dark Is Shiny, Detail, Flat). A
/// source's material takes the recipe (as Next Recipe steps through them); a package's own packed spec texture gets its shine
/// channel (where the texture's parameter name puts it: R in every layout that has one) replaced and keeps its other channels
/// (power, skin mask, reflectivity, glow). Spec color: the highlight's tint, from the color map (as it is, or with half its
/// tint), white (no tint) or one color; a source's material gets it as its Spec Color file, a package's own spec color texture
/// is replaced. Tag Colors and From Channels (the other spec map makers) are in the menu too when the row allows them.
/// </summary>
sealed partial class ModelPage
{
    static readonly (string Label, string Recipe)[] SpecRecipeItems =
        [("Soft", "soft"), ("Strong", "strong"), ("Dark Is Shiny", "dark"), ("Detail", "detail"), ("Flat", "flat")];

    static readonly (string Label, string Kind, string Tip)[] SpecColorItems =
    [
        ("From the Color Map", "color", "The highlight takes the color map's colors as they are (gold shines gold)."),
        ("Color Map, Half Tint", "half", "The color map's colors with half their tint taken out (each toward its own gray): a softer colored highlight."),
        ("White (No Tint)", "white", "Highlights in the light's own color: no tint."),
        ("One Color", "one", "One color for the whole material's highlights, picked with the color picker (wheel, hex and eyedropper)."),
    ];

    /// <summary>The spec map makers for the selected row (Generate Spec Map, Tag Colors, From Channels), or none.</summary>
    IEnumerable<ToolStripItem> SpecMenuItems()
    {
        string? why = null;
        bool show;
        if (SelectedPackageMap() is { } pm)
        {
            show = pm.Kind is "Spec (Packed)" or "Color" && PackageSpecPair(pm).Spec != null;
            if (show && PackageSpecPair(pm) is var (color, spec) && (color == null || ShineChannel(spec!) < 0))
                why = color == null ? "This material has no color map to make it from." : $"{spec!.Texture}'s layout ({spec.Param}) has no shine channel.";
        }
        else
        {
            var sel = SelectedMap();
            show = sel is { Kind: "Spec" or "MHO Spec" or "Color" } && OverridesPath() != null;
            var tx = sel is { } s ? shownMaterials.FirstOrDefault(m => m.Material == s.Material).Tex : null;
            if (show && tx?.Diffuse == null) why = "This material has no color map to make it from.";
            else if (show && tx?.SpecMho != null) why = "The material uses an MHO spec map (its MHO Spec row): Back to Automatic on that row first.";
            else if (show && tx?.ColorTags is { Count: > 0 }) why = "The material's spec map is made from your color tags (Tag Colors): clear them first.";
        }
        if (!show) yield break;
        var item = new ToolStripMenuItem("Generate Spec Map")
        {
            Enabled = why == null,
            ToolTipText = why ?? (SelectedPackageMap() != null
                ? "Makes the shine of the material's packed spec map from its color map and puts it into the texture; its other channels (power, skin mask, reflectivity, glow) stay as they are. Undo (Ctrl+Z) takes it back."
                : "Makes the material's spec (shine) map from its color map in the way you pick (the same recipes Next Recipe steps through). Preview Shows → Spec Map shows it. Undo (Ctrl+Z) takes it back."),
        };
        foreach (var (label, recipe) in SpecRecipeItems)
            item.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => GenerateSpec(recipe)) { ToolTipText = SpecMapGen.Label(recipe) + "." });
        yield return item;
        if (SelectedPackageMap() is { } tp)
        {
            var (tc, ts) = PackageSpecPair(tp);
            bool ok = tc != null && ts != null;
            yield return new ToolStripMenuItem("Tag Colors", null, (_, _) => PackageTagColors()) { Enabled = ok, ToolTipText = ok
                ? "Tell it what each color group of the color texture is made of (Metal, Skin, Leather, Cloth, Glow): the packed spec texture is made from your tags, in its own layout. Untagged groups, and channels the tags don't set (rim mask, ambient, height), keep what they have. Undo (Ctrl+Z) takes it back."
                : "This material has no color map to tag." };
        }
        else
        {
            if (matTags.Enabled) yield return new ToolStripMenuItem("Tag Colors", null, (_, _) => TagColors()) { ToolTipText = "Tell it what each color group is made of (Metal, Skin, Leather, Cloth): an MHO spec map is made from your tags." };
            if (matChannels.Enabled) yield return new ToolStripMenuItem("From Channels", null, (_, _) => FromChannels()) { ToolTipText = "Makes the MHO spec map from separate gray images, one per channel (Shine, Power, Skin Mask, Reflectivity)." };
        }
    }

    /// <summary>Generate Spec Color Map for the selected row, or null when it has no spec color map to make.</summary>
    ToolStripMenuItem? SpecColorMenuItem()
    {
        bool show; string? why = null;
        if (SelectedPackageMap() is { } pm)
        {
            var (color, sc) = PackageSpecColorPair(pm);
            show = pm.Kind is "Spec Color" or "Color" && sc != null;
            if (show && color == null) why = "This material has no color map to make it from (White and One Color still work).";
        }
        else
        {
            var sel = SelectedMap();
            show = sel is { Kind: "Spec Color" or "Color" } && OverridesPath() != null;
            if (show && shownMaterials.FirstOrDefault(m => m.Material == sel!.Value.Material).Tex?.Diffuse == null) why = "This material has no color map to make it from (White and One Color still work).";
        }
        if (!show) return null;
        var item = new ToolStripMenuItem("Generate Spec Color Map") { ToolTipText = "Makes the map that tints the material's highlights (Spec Color) and uses it in place of the one it has. Undo (Ctrl+Z) takes it back."
            + (SelectedPackageMap() == null ? " A source model's spec color map is used when the material is built with the Metal game material (This Material ▾); the others tint by the color map." : "") };
        foreach (var (label, kind, tip) in SpecColorItems)
            item.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => GenerateSpecColor(kind))
            {
                Enabled = why == null || kind is "white" or "one",
                ToolTipText = why != null && kind is "color" or "half" ? why : tip,
            });
        return item;
    }

    (PackageMap? Color, PackageMap? Spec) PackageSpecPair(PackageMap m)
    {
        var same = (packageMaps ?? []).Where(x => x.Material == m.Material && x.InFile == m.InFile).ToList();
        return (same.FirstOrDefault(x => x.Kind == "Color"), m.Kind == "Spec (Packed)" ? m : same.FirstOrDefault(x => x.Kind == "Spec (Packed)"));
    }

    (PackageMap? Color, PackageMap? SpecColor) PackageSpecColorPair(PackageMap m)
    {
        var same = (packageMaps ?? []).Where(x => x.Material == m.Material && x.InFile == m.InFile).ToList();
        return (same.FirstOrDefault(x => x.Kind == "Color"), m.Kind == "Spec Color" ? m : same.FirstOrDefault(x => x.Kind == "Spec Color"));
    }

    /// <summary>The channel (0-3 = R G B A) of a packed spec texture that holds the shine, by its parameter name's layout
    /// (every layout with a shine has it in R; an unknown name: R); -1 when its layout has none (v1 emissivespecpow).</summary>
    static int ShineChannel(PackageMap spec)
    {
        var layout = SpecLayouts.All.OrderByDescending(l => l.Parameter.Length).FirstOrDefault(l => (spec.Param.Length > 0 ? spec.Param : spec.Texture).Contains(l.Parameter, StringComparison.OrdinalIgnoreCase));
        return layout == null ? 0 : Array.IndexOf(layout.Channels, "shine");
    }

    /// <summary>An image file's ARGB pixels at <paramref name="w"/> × <paramref name="h"/> (scaled when its size differs).</summary>
    static int[] PixelsAt(string file, int w, int h)
    {
        var (fw, fh, px) = NormalMapGen.LoadArgb(file);
        if (fw == w && fh == h) return px;
        string temp = Path.Combine(MapsCache, "scaled_" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            using (var src = ImagePixels.Open(file))
            using (var fit = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(fit))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.DrawImage(src, 0, 0, w, h);
                }
                ImagePixels.SavePng(fit, temp);
            }
            return NormalMapGen.LoadArgb(temp).Argb;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    static byte[] ToBgra(int[] argb, bool opaque)
    {
        var b = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++) { b[4 * i] = (byte)argb[i]; b[4 * i + 1] = (byte)(argb[i] >> 8); b[4 * i + 2] = (byte)(argb[i] >> 16); b[4 * i + 3] = opaque ? (byte)255 : (byte)(argb[i] >> 24); }
        return b;
    }

    void GenerateSpec(string recipe)
    {
        string label = SpecRecipeItems.First(x => x.Recipe == recipe).Label;
        if (SelectedPackageMap() is { } pm)
        {
            var (color, spec) = PackageSpecPair(pm);
            if (color == null || spec == null) return;
            int ch = ShineChannel(spec);
            if (ch < 0) return;
            string cpng = Path.Combine(MapsCache, $"specsrc_{Guid.NewGuid().ToString("N")[..6]}.png");
            string spng = Path.Combine(MapsCache, $"spec_{FbxExport.SafeName(pm.Material)}_{Guid.NewGuid().ToString("N")[..6]}.png");
            try
            {
                Directory.CreateDirectory(MapsCache);
                if (!ExportPackageMap(color, cpng) || !ExportPackageMap(spec, spng)) return;
                var (w, h, sp) = NormalMapGen.LoadArgb(spng);
                // (the color map's alpha isn't a cut-out in every material: ignored, every texel gets its shine)
                var shine = SpecMapGen.Make(w, h, ToBgra(PixelsAt(cpng, w, h), opaque: true), recipe);
                int shift = ch switch { 0 => 16, 1 => 8, 2 => 0, _ => 24 };
                for (int i = 0; i < sp.Length; i++) sp[i] = (int)(((uint)sp[i] & ~(0xFFu << shift)) | ((uint)shine[i] << shift));
                NormalMapGen.SaveArgb(w, h, sp, spng);
                if (ReplacePackageMap(spec, spng)) Log($"  shine ({"RGBA"[ch]}) generated from {color.Texture}: {SpecMapGen.Label(recipe)}; the other channels kept");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: the spec map couldn't be made: {ex.Message}"); }
            finally { foreach (var f in new[] { cpng, spng }) try { File.Delete(f); } catch (IOException) { } }
            return;
        }
        if (SelectedMap() is not { } sel) return;
        ChangeOverrides((_, e) => { e.Spec = null; e.SpecRecipe = recipe == "soft" ? null : recipe; }, sel.Material,
            $"{sel.Material}'s spec map is now generated from its color map: {label.ToLowerInvariant()}.");
    }

    /// <summary>The tags given to a package's color textures (WorkFolder\package_tags.json, kept with the mod): package|texture → tags.</summary>
    string PackageTagsFile => Path.Combine(host.WorkFolder, "package_tags.json");

    Dictionary<string, List<string[]>> LoadPackageTags()
    {
        try { return File.Exists(PackageTagsFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string[]>>>(File.ReadAllText(PackageTagsFile)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Tag Colors on a package's own textures (Kurt, 2026-11-08: "generate a map using the tag color feature"): the color
    /// texture's groups tagged in the Tag Colors window, then the material's packed spec texture made from them in its own
    /// layout (the texture's parameter name: shine, power, skin mask, reflectivity / reflection set from the tags; a glow
    /// channel lit where a group is tagged Glow). Untagged texels, and channels the tags have no value for, keep the texture's.
    /// </summary>
    void PackageTagColors()
    {
        if (SelectedPackageMap() is not { } pm || PackageSpecPair(pm) is not ({ } color, { } spec) || ChosenPackage is not CharacterList.Item item) return;
        string key = $"{(spec.InFile ?? item.Key)}|{spec.Texture}";
        var all = LoadPackageTags();
        var current = all.TryGetValue(key, out var had) ? had.Where(x => x.Length == 2).Select(x => (x[0], x[1])).ToList() : [];
        string cpng = Path.Combine(MapsCache, $"tagsrc_{Guid.NewGuid().ToString("N")[..6]}.png");
        string spng = Path.Combine(MapsCache, $"tagspec_{FbxExport.SafeName(pm.Material)}_{Guid.NewGuid().ToString("N")[..6]}.png");
        try
        {
            Directory.CreateDirectory(MapsCache);
            if (!ExportPackageMap(color, cpng) || !ExportPackageMap(spec, spng)) return;
            List<(string Color, string Tag)> tags;
            using (var f = new ColorTagForm(pm.Material, cpng, current))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                tags = f.Result;
            }
            if (tags.Count == 0) { Log($"Materials: {pm.Material}: no colors tagged; the spec texture is left as it is."); return; }
            var (w, h, sp) = NormalMapGen.LoadArgb(spng);
            var packed = ColorTags.MakePacked(w, h, ToBgra(PixelsAt(cpng, w, h), opaque: true), tags, out var texelTags);
            var layout = SpecLayouts.All.OrderByDescending(l => l.Parameter.Length).FirstOrDefault(l => (spec.Param.Length > 0 ? spec.Param : spec.Texture).Contains(l.Parameter, StringComparison.OrdinalIgnoreCase)) ?? SpecLayouts.All[0];
            // the tags' values (BGRA bytes of Angela's layout: R shine, G power, B skin mask, A reflectivity) by channel name
            int FromAngela(string name) => name switch { "shine" => 2, "power" => 1, "skin mask" => 0, "reflectivity" or "reflection" => 3, _ => -1 };
            int[] shifts = [16, 8, 0, 24];   // (R G B A in an ARGB int)
            for (int i = 0; i < sp.Length; i++)
            {
                if (string.IsNullOrEmpty(texelTags[i])) continue;   // (untagged: as it was)
                uint v = (uint)sp[i];
                for (int c = 0; c < 4; c++)
                {
                    string name = layout.Channels[c];
                    int src = FromAngela(name);
                    int val = src >= 0 ? packed[4 * i + src] : name is "glow" or "glow mask" ? (texelTags[i] == "glow" ? 255 : -1) : -1;
                    if (val >= 0) v = (v & ~(0xFFu << shifts[c])) | ((uint)val << shifts[c]);
                }
                sp[i] = (int)v;
            }
            NormalMapGen.SaveArgb(w, h, sp, spng);
            if (ReplacePackageMap(spec, spng))
            {
                all[key] = [.. tags.Select(t => new[] { t.Color, t.Tag })];
                try { File.WriteAllText(PackageTagsFile, System.Text.Json.JsonSerializer.Serialize(all)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("tags: " + ex.Message); }
                Log($"  {spec.Texture} made from your tags ({string.Join(", ", tags.GroupBy(t => ColorTags.Name(t.Tag)).Select(g => $"{g.Count()} {ColorTags.Label(g.Key).ToLowerInvariant()}"))}), in its layout ({layout.Label}); untagged parts kept");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: the spec map couldn't be made from the tags: {ex.Message}"); }
        finally { foreach (var f in new[] { cpng, spng }) try { File.Delete(f); } catch (IOException) { } }
    }

    void GenerateSpecColor(string kind)
    {
        if (kind == "one")
        {
            var anchor = matGrid.RectangleToScreen(matGrid.CurrentCell is { } c ? matGrid.GetCellDisplayRectangle(c.ColumnIndex, c.RowIndex, false) : matGrid.ClientRectangle);
            Color picked = Color.White; bool changed = false;
            var popup = ColorPickerPopup.Open(this, anchor, Color.White, null, col => { picked = col; changed = true; });
            popup.Closed += (_, _) => { if (changed) BeginInvoke(() => WriteSpecColor("one", picked)); };
            return;
        }
        WriteSpecColor(kind, Color.White);
    }

    void WriteSpecColor(string kind, Color one)
    {
        string label = kind == "one" ? $"one color (#{one.R:X2}{one.G:X2}{one.B:X2})" : SpecColorItems.First(x => x.Kind == kind).Label.ToLowerInvariant();
        int Pixel(int c)
        {
            if (kind == "white") return unchecked((int)0xFFFFFFFF);
            if (kind == "one") return unchecked((int)0xFF000000) | (one.R << 16) | (one.G << 8) | one.B;
            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            if (kind == "half") { int l = (int)(0.299f * r + 0.587f * g + 0.114f * b); r = (r + l) / 2; g = (g + l) / 2; b = (b + l) / 2; }
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        bool fromColor = kind is "color" or "half";
        if (SelectedPackageMap() is { } pm)
        {
            var (color, sc) = PackageSpecColorPair(pm);
            if (sc == null || fromColor && color == null) return;
            string cpng = Path.Combine(MapsCache, $"speccolsrc_{Guid.NewGuid().ToString("N")[..6]}.png");
            string outPng = Path.Combine(MapsCache, $"speccolor_{FbxExport.SafeName(pm.Material)}_{Guid.NewGuid().ToString("N")[..6]}.png");
            try
            {
                Directory.CreateDirectory(MapsCache);
                int w = Math.Max(4, sc.W), h = Math.Max(4, sc.H);
                int[] px = fromColor && ExportPackageMap(color!, cpng) ? PixelsAt(cpng, w, h) : new int[w * h];
                if (fromColor && !File.Exists(cpng)) return;
                NormalMapGen.SaveArgb(w, h, px.Select(Pixel).ToArray(), outPng);
                if (ReplacePackageMap(sc, outPng)) Log($"  spec color generated: {label}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: the spec color map couldn't be made: {ex.Message}"); }
            finally { foreach (var f in new[] { cpng, outPng }) try { File.Delete(f); } catch (IOException) { } }
            return;
        }
        if (SelectedMap() is not { } sel || OverridesPath() is not string path) return;
        var tex = shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex;
        if (fromColor && tex?.Diffuse == null) return;
        string dir = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string name = $"{FbxExport.SafeName(sel.Material)}_speccolor_generated_{(kind == "one" ? $"{one.R:x2}{one.G:x2}{one.B:x2}" : kind)}.png";
        try
        {
            if (fromColor)
            {
                var (w, h, px) = NormalMapGen.LoadArgb(tex!.Diffuse!);
                NormalMapGen.SaveArgb(w, h, px.Select(Pixel).ToArray(), Path.Combine(dir, name));
            }
            else NormalMapGen.SaveArgb(16, 16, Enumerable.Repeat(Pixel(0), 256).ToArray(), Path.Combine(dir, name));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: the spec color map couldn't be made: {ex.Message}"); return; }
        string rel = Path.Combine(Path.GetFileName(dir), name);
        ChangeOverrides((_, e) => e.SpecColor = rel, sel.Material, $"{sel.Material}'s spec color map is now generated: {label}.");
    }
}

using System.Numerics;
using AnimExportCli.Animation;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The stretch heat map (2026-10-25, with <see cref="FitDistort"/>): an MFF model fitted as the Model tab's preview fits it,
/// and the game's own model, both posed at the frame of an animation where the fitted model stretches most, coloured per
/// vertex by the largest stretch of its triangles against the bind pose (blue 1×, cyan 1.75×, green 2.5×, yellow 3.25×, red
/// 4× or more). Rendered off-screen by the 3D view to one PNG: fitted on top, game below, from the front, a three-quarter
/// view and the back. --fit-heat &lt;mff model&gt; &lt;package&gt; &lt;animation&gt; &lt;out.png&gt; (MHO_LEAN, MFF_* switches, MHO_FD_WEIGHTS apply).
/// </summary>
static class FitHeat
{
    static MhoExtendedModManager.Gui.ModelView.Look? look;

    static MhoExtendedModManager.Gui.ModelView.Look Look()
    {
        if (look != null) return look;
        const int W = 256, H = 4;
        (float At, Vector3 C)[] stops = [(0, new(0, 0, 255)), (0.25f, new(0, 255, 255)), (0.5f, new(0, 255, 0)), (0.75f, new(255, 255, 0)), (1, new(255, 0, 0))];
        var px = new byte[W * H * 4];
        for (int x = 0; x < W; x++)
        {
            float w = x / (W - 1f); var c = stops[^1].C;
            for (int i = 1; i < stops.Length; i++) if (w <= stops[i].At) { c = Vector3.Lerp(stops[i - 1].C, stops[i].C, (w - stops[i - 1].At) / (stops[i].At - stops[i - 1].At)); break; }
            for (int y = 0; y < H; y++) { int i = (y * W + x) * 4; px[i] = (byte)c.Z; px[i + 1] = (byte)c.Y; px[i + 2] = (byte)c.X; px[i + 3] = 255; }
        }
        var l = MhoExtendedModManager.Gui.ModelView.Look.Plain(new MhoExtendedModManager.Gui.ModelView.Map(px, W, H));
        l.Cutout = false; l.TwoSided = true; l.HalfLambert = true; l.UseRim = false;
        return look = l;
    }

    /// <summary>Per vertex: the largest stretch (max(s1, 1 / s2)) of the triangles around it, from rest to posed.</summary>
    static float[] VertexStretch(int[] tris, Vector3[] rest, Vector3[] posed)
    {
        var s = new float[rest.Length]; Array.Fill(s, 1f);
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (FitDistort.Deform(rest[a], rest[b], rest[c], posed[a], posed[b], posed[c]) is not var (s1, s2) || s2 < 1e-6f) continue;
            float st = MathF.Max(s1, 1 / s2);
            s[a] = MathF.Max(s[a], st); s[b] = MathF.Max(s[b], st); s[c] = MathF.Max(s[c], st);
        }
        return s;
    }

    static ModMeshes.Loaded Painted(ModMeshes.Loaded l, float[] stretch)
    {
        var uv = stretch.Select(x => new Vector2((0.5f + Math.Clamp((x - 1) / 3, 0, 1) * 255f) / 256f, 0.5f)).ToArray();
        return new ModMeshes.Loaded(l.Name, l.Positions, l.Normals, l.Tangents, uv, l.Indices, new int[l.Indices.Length / 3], [Look()], l.Info, l.Bones, l.Influences);
    }

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 4) { Console.WriteLine("--fit-heat <mff model> <package> <animation> <out.png>"); return 1; }
        float lean = float.TryParse(Environment.GetEnvironmentVariable("MHO_LEAN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lv) ? lv : 0;
        Settings.Reset();
        string pkg = BasePackage.Resolve(args[1], true);
        var model = MffModel.Load(Source.ResolveModelFile(args[0]));
        var prep = Gui.PreviewPanel.Prepare(model, null, pkg, null, lean: lean);
        var fitted = FitDistort.Experiment(prep, prep.Mesh with { Bones = prep.Bones });
        var ar = prep.Animations.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        BoneAnimation? ba = ar == null ? null : ModAnimations.Load(ar);
        if (ba == null && !args[2].Equals("rest", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"{args[2]}: not found"); return 1; }

        // the frame where the fitted model stretches most (its 99th percentile vertex)
        float frames = ba == null ? 0 : MeshAnimator.Span(ba).Frames, worstAt = 0, worst = 0;
        var fa = new MeshAnimator(fitted.Bones, fitted.Positions, fitted.Normals, fitted.Influences, fitted.Tangents);
        for (int k = 0; ba != null && k < 16; k++)
        {
            fa.Pose(ba, frames * k / 16f);
            var st = VertexStretch(fitted.Indices, fitted.Positions, fa.Positions).Order().ToArray();
            float p99 = st[(int)(st.Length * 0.99f)];
            if (p99 > worst) { worst = p99; worstAt = frames * k / 16f; }
        }
        Console.WriteLine(ba == null ? "rest pose" : $"{ar!.Name}: worst frame {worstAt:0.#} of {frames:0} (fitted 99th percentile stretch {worst:0.00})");

        var rows = new List<(string Label, ModMeshes.Loaded Mesh)> { ("fitted", fitted) };
        if (prep.Stock != null) rows.Add(("game", prep.Stock));
        float[] yaws = [0, 45, 180];
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        using var f = new System.Windows.Forms.Form { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(420, 620), ShowInTaskbar = false };
        var v = new MhoExtendedModManager.Gui.ModelView { Dock = System.Windows.Forms.DockStyle.Fill, Background = System.Drawing.Color.FromArgb(40, 40, 46) };
        f.Controls.Add(v);
        var shots = new List<List<System.Drawing.Bitmap>>();
        f.Shown += (_, _) => f.BeginInvoke(async () =>
        {
            foreach (var (label, mesh) in rows)
            {
                var an = new MeshAnimator(mesh.Bones, mesh.Positions, mesh.Normals, mesh.Influences, mesh.Tangents);
                an.Pose(ba, worstAt);
                var stretch = VertexStretch(mesh.Indices, mesh.Positions, an.Positions);
                Console.WriteLine($"  {label}: vertices over 2× {stretch.Count(x => x > 2)}, over 3× {stretch.Count(x => x > 3)}, over 4× {stretch.Count(x => x > 4)} (of {stretch.Length})");
                v.ShowMesh(Painted(mesh, stretch));
                v.UpdateGeometry(an);
                var row = new List<System.Drawing.Bitmap>();
                var start = v.ViewState;
                foreach (float yaw in yaws)
                {
                    var vs = (float[])start.Clone(); vs[0] += yaw * MathF.PI / 180; v.ViewState = vs;
                    await Task.Delay(200);
                    var b = new System.Drawing.Bitmap(v.Width, v.Height); v.DrawToBitmap(b, new System.Drawing.Rectangle(0, 0, v.Width, v.Height)); row.Add(b);
                }
                shots.Add(row);
            }
            int w = shots[0][0].Width, h = shots[0][0].Height;
            using var sheet = new System.Drawing.Bitmap(w * yaws.Length, h * shots.Count);
            using (var g = System.Drawing.Graphics.FromImage(sheet))
            {
                using var font = new System.Drawing.Font("Segoe UI", 11);
                for (int r = 0; r < shots.Count; r++)
                    for (int c = 0; c < shots[r].Count; c++) { g.DrawImage(shots[r][c], c * w, r * h); shots[r][c].Dispose(); }
                for (int r = 0; r < rows.Count; r++) g.DrawString(rows[r].Label, font, System.Drawing.Brushes.White, 6, r * h + 6);
            }
            sheet.Save(args[3]);
            f.Close();
        });
        System.Windows.Forms.Application.Run(f);
        return 0;
    }
}

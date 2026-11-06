using MhoPackageModifier;

namespace MhoExtendedModManager.FreeCam;

/// <summary>
/// FreeCam Options's changes to a MarvelGame.upk that already has Marvel Heroes FreeCam (M.H.F.C., by Michael / MichaelMerc,
/// nexusmods.com/marvelheroesomega/mods/378) installed. Only that package's script changes; Calligraphy.sip and every other
/// file stay as they are. Freecam's own camera code (orbit, RB height, mouse camera) is kept; this adds modes, invert Y,
/// zoom past the game's steps and limits. Worked out in C:\Dev\MHO-Camera (CLAUDE.md there: the bytecode facts).
///
/// State kept on the camera (MarvelPlayerCamera), in fields no stock script uses; Freecam relies on the X components:
///   LastCamLoc.X       RB / Page Up-Down held (Freecam)
///   LastCamLoc.Y       camera mode: 0 free orbit (Freecam), 1 behind the hero, 2 the game's own (isometric) direction
///   LastCamLoc.Z       gamepad camera button: 0 idle, 1 held, 2 held (already used), 3 released once (waiting for a
///                      second press), 4 short press to do, 6 double press to do (still held), 7 (let go)
///   LastMouseAdjVec.X  height offset ±250 (Freecam)
///   LastMouseAdjVec.Y  extra zoom steps n: distance × step^n
///   LastMouseAdjVec.Z  seconds since the camera button's last press or release (counted every frame)
/// Commands (script exec functions nobody binds by default, harmless stock bodies; never native ones: their script never runs):
///   MarvelPlayerController.CameraZoomToggle   cycle camera mode (keyboard)    (stock: toggles the zoom)
///   MarvelHUD.TraceAllMovieClips              snap behind (keyboard)          (stock: a Flash debug trace)
///   MarvelHUD.ToggleTraceActorLock            the gamepad camera button, bound "X | OnRelease X": each call is a press
///                                             or a release (stock: a debug trace pointer)
/// The per-frame code (AdjustCameraZoom) turns the button's presses into short (under a second), long (held a second) and
/// double presses and does the action chosen for each; a short press waits for a second one only when a double press is used.
/// Invert Y is baked in (settings), no command.
/// Extra zoom has no command: MarvelPlayerCamera.adjustZoomIndex (the mouse wheel) carries on past the ends.
/// </summary>
static class FreeCamPatch
{
    public const string CycleMode = "CameraZoomToggle";
    /// <summary>Snap Behind from the keyboard (MarvelHUD; stock: a Flash debug trace with no visible effect).</summary>
    public const string SnapBehind = "TraceAllMovieClips";
    /// <summary>The gamepad camera button's press / release (MarvelHUD; stock: a debug trace pointer).</summary>
    public const string PadButton = "ToggleTraceActorLock";
    /// <summary>The second gamepad button's press / release, when the two actions use different buttons (MarvelHUD; stock:
    /// closes the PvP scoreboard).</summary>
    public const string PadButton2 = "MarvelGFXHidePvPScores";
    /// <summary>The third gamepad button's press / release (MarvelHUD; stock: toggles the PvP score panel).</summary>
    public const string PadButton3 = "MarvelGFxTogglePvPScorePanel";
    /// <summary>Selfie from the keyboard (MarvelHUD; stock: opens the PvP score panel).</summary>
    public const string Selfie = "MarvelGFXShowPvPScores";
    const string Vector = "Core.Object.Vector", Rotator = "Core.Object.Rotator";
    const float DegToUnits = 65536f / 360f;

    /// <summary>The replaced exports (export index → new bytes), with a short log. Throws when the package isn't Freecam's.</summary>
    public static Dictionary<int, byte[]> Build(Package pkg, FreeCamSettings cfg, List<string> log)
    {
        cfg = cfg.Copy(); cfg.Clamp();
        var a = new Asm(pkg);
        var result = new Dictionary<int, byte[]>();
        int pitchMin = -(int)(cfg.TiltDown * DegToUnits), pitchMax = (int)(cfg.TiltUp * DegToUnits);
        float orbit = 30000f * cfg.OrbitSpeed / 100f;
        float pitchSign = cfg.InvertByDefault ? -1f : 1f;   // Y inverted every session
        float zoomStep = MathF.Log(1 + cfg.ZoomStepPercent / 100f);
        // the wheel's extra-step limits, widened to the shortcuts' preset zooms (so the wheel doesn't jump back from one)
        int Steps(double factor) => (int)Math.Ceiling(Math.Abs(cfg.StepsFor(factor)) - 1e-6) * Math.Sign(cfg.StepsFor(factor));
        int maxOut = new[] { cfg.ZoomStepsFarther, cfg.SnapKeepZoom ? 0 : -Steps(cfg.SnapZoomX), cfg.SelfieKeepZoom ? 0 : -Steps(cfg.SelfieZoomX) }.Max();
        int maxIn = new[] { cfg.ZoomStepsCloser, cfg.SnapKeepZoom ? 0 : Steps(cfg.SnapZoomX), cfg.SelfieKeepZoom ? 0 : Steps(cfg.SelfieZoomX) }.Max();

        Node Cam() => a.DynCast("MarvelPlayerCamera", a.Inst("Engine.PlayerController.PlayerCamera"));
        Node CamVec(string field, string comp, bool write = false) => a.Member(a.Ctx(Cam(), $"MarvelPlayerCamera.{field}"), Vector, comp, write);
        Node Mode() => CamVec("LastCamLoc", "Y");
        Node Input(string axis) => a.Ctx(a.DynCast("MarvelInput", a.Inst("Engine.PlayerController.PlayerInput")), $"MarvelInput.{axis}");
        Node Delta() => a.Local("MarvelPlayerController.AdjustCameraZoom.DeltaTime");
        const string Pitch = "MarvelPlayerController.vanityCamNum", Yaw = "MarvelPlayerController.turnTableNum";
        Node AgentYaw() => a.Member(a.Ctx(a.Inst("MarvelPlayerController.Agent"), "Engine.Actor.Rotation"), Rotator, "Yaw", false);

        // An action's statements in the controller's own code; the statement after them must carry <after>.
        List<Node> Action(string which, JumpLabel after)
        {
            if (which == "Mode")
            {
                var a1 = new JumpLabel("A"); var b1 = new JumpLabel("B");
                return [
                    Asm.Let(CamVec("LastCamLoc", "Y", true), Asm.Nat(Asm.AddF, Mode(), Asm.F(1))),
                    Asm.IfNot(Asm.Nat(Asm.GreaterF, Mode(), Asm.F(2)), a1),
                    Asm.Let(CamVec("LastCamLoc", "Y", true), Asm.F(0)),
                    Asm.At(a1, Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(1)), b1)),
                    Asm.Let(a.Inst(Pitch), Asm.I(-(int)(cfg.BehindTilt * DegToUnits))),
                    Asm.At(b1, Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(2)), after)),   // isometric: default height and zoom
                    Asm.Let(CamVec("LastMouseAdjVec", "X", true), Asm.F(0)),
                    Asm.Let(CamVec("LastMouseAdjVec", "Y", true), Asm.F(0)),
                ];
            }
            // Snap: behind the hero at Behind Mode's tilt, at once (a zero LastCamOffset makes UpdateViewTarget jump instead of
            // easing; Kurt: the easing lagged and the old tilt stayed). Free Orbit stays free afterwards.
            // Selfie: the same, from the front (half a circle round), facing the hero.
            if (which is "Snap" or "Selfie") return Shortcut(which == "Selfie", hud: false);
            return [];
        }
        // Snap Behind / Selfie: the hero's facing (Selfie: half a circle round), the shortcut's own tilt and height, the zoom
        // kept or set (the game's closest step, then the chosen extra steps), then a zero LastCamOffset so UpdateViewTarget
        // jumps there instead of easing (Kurt: the easing lagged). Free Orbit stays free afterwards. <hud>: the HUD's
        // keyboard command (the controller and camera through PlayerOwner) instead of the controller's own code.
        List<Node> Shortcut(bool selfie, bool hud)
        {
            Node Pc() => a.DynCast("MarvelPlayerController", a.Inst("Engine.HUD.PlayerOwner"));
            Node PcVar(string prop) => hud ? a.Ctx(Pc(), prop) : a.Inst(prop);
            Node C() => hud ? a.DynCast("MarvelPlayerCamera", a.Ctx(a.Inst("Engine.HUD.PlayerOwner"), "Engine.PlayerController.PlayerCamera")) : Cam();
            Node HeroYaw() => a.Member(a.Ctx(PcVar("MarvelPlayerController.Agent"), "Engine.Actor.Rotation"), Rotator, "Yaw", false);
            var (tilt, height, keepZoom, zoom) = selfie ? (cfg.SelfieTilt, cfg.SelfieHeight, cfg.SelfieKeepZoom, -(float)cfg.StepsFor(cfg.SelfieZoomX)) : (cfg.SnapTilt, cfg.SnapHeight, cfg.SnapKeepZoom, -(float)cfg.StepsFor(cfg.SnapZoomX));   // (zoom: extra steps, negative = closer; fractional between steps)
            var list = new List<Node>
            {
                Asm.Let(PcVar(Yaw), selfie ? Asm.Nat(Asm.AddInt, HeroYaw(), Asm.I(32768)) : HeroYaw()),
                Asm.Let(PcVar(Pitch), Asm.I(-(int)(tilt * DegToUnits))),
                Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "X", true), Asm.F(height)),   // Freecam's height
            };
            if (!keepZoom)
            {
                list.Add(Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "Y", true), Asm.F(zoom)));
                list.Add(Asm.Let(a.Ctx(C(), "MarvelPlayerCamera.currentSettingIndex"), a.CtxFinal(C(), "MarvelPlayerCamera.getMaxZoomIndex")));
                list.Add(Asm.LetBool(a.Ctx(C(), "MarvelPlayerCamera.gameCamIndexChanged"), Asm.True()));
                list.Add(Asm.Let(PcVar("MarvelPlayerController.m_CameraZoomIndex"), a.Ctx(C(), "MarvelPlayerCamera.currentSettingIndex")));
            }
            list.Add(Asm.Let(a.Ctx(C(), "MarvelPlayerCamera.LastCamOffset"), VectConst(0, 0, 0)));
            return list;
        }

        // Gamepad press trackers: one per distinct button (and RT setting), in the order the actions are listed; actions on
        // the same button share its tracker, told apart by press type (FreeCamSettings.PadGroups).
        var groups = cfg.PadGroups();
        var trackers = Trackers.Select((t, i) => t with
        {
            ActionFor = i < groups.Count ? press => groups[i].Actions.FirstOrDefault(x => x.Press == press).Action : _ => null,
        }).ToArray();

        // ---- AdjustCameraZoom (every frame): the camera button's presses, then stick / mouse input per mode
        {
            var (idx, f, code) = Load(pkg, "MarvelPlayerController.AdjustCameraZoom");
            int first = code.FindIndex(s => s.Label != null);
            if (first != 4 || code.Count != 10) throw new InvalidDataException("This MarvelGame.upk doesn't have Freecam's camera code (AdjustCameraZoom).");
            var height1 = code[6]; var height2 = code[7];
            Expect(height1.Tok == Asm.AddEqF && height2.Tok == Asm.AddEqF, "AdjustCameraZoom: Freecam's height statements not where expected");
            var resume = code[first].Label!;
            var end = new JumpLabel("End"); var behind = new JumpLabel("Behind"); var height = new JumpLabel("Height");
            height1.Label = null; height2.Label = null;
            var stmts = code.Take(first).ToList();
            var next = resume;
            foreach (var t in trackers)
            {
                var exit = new JumpLabel("Tracker");
                var block = TrackerFrame(a, t, CamVec, Delta(), Action, exit);
                block[0].Label = next;
                stmts.AddRange(block);
                next = exit;
            }
            var g5 = next;
            stmts.AddRange([
                Asm.At(g5, Asm.IfNot(Asm.Nat(Asm.NotEqF, Mode(), Asm.F(2)), end)),             // isometric: no orbit input
                Asm.Nat(Asm.AddEqInt, a.Inst(Pitch), Asm.Cast(Asm.FloatToInt,
                    Asm.Nat(Asm.MulF,
                        Asm.Nat(Asm.MulF, Asm.Nat(Asm.MulF, Input("GamepadRawJoyLookUp"), Delta()), Asm.F(-orbit * pitchSign)),
                        Asm.Nat(Asm.SubF, Asm.F(1), CamVec("LastCamLoc", "X"))))),            // not while RB held (Freecam)
                Asm.Let(a.Inst(Pitch), Asm.Nat(Asm.Clamp, a.Inst(Pitch), Asm.I(pitchMin), Asm.I(pitchMax))),
                Asm.IfNot(Asm.Nat(Asm.NotEqF, Mode(), Asm.F(1)), behind),
                Asm.Nat(Asm.AddEqInt, a.Inst(Yaw), Asm.Cast(Asm.FloatToInt,
                    Asm.Nat(Asm.MulF, Asm.Nat(Asm.MulF, Input("GamepadRawJoyLookRight"), Delta()), Asm.F(orbit)))),
                Asm.Goto(height),
                Asm.At(behind, Asm.Nat(Asm.AddEqInt, a.Inst(Yaw), Asm.Cast(Asm.FloatToInt,
                    Asm.Nat(Asm.MulF,
                        Asm.Cast(Asm.IntToFloat, Asm.Nat(Asm.SubInt,
                            Asm.Nat(Asm.AndInt, Asm.Nat(Asm.AddInt, Asm.Nat(Asm.SubInt, AgentYaw(), a.Inst(Yaw)), Asm.I(32768)), Asm.I(65535)),
                            Asm.I(32768))),
                        Asm.Nat(Asm.FMin, Asm.F(1), Asm.Nat(Asm.MulF, Delta(), Asm.F(cfg.FollowSpeed / 10f))))))),
                Asm.At(height, height1),
                height2,
                Asm.At(end, Asm.ReturnNothing()),
                Asm.End(),
            ]);
            result[idx] = f.Build(stmts);
            log.Add("AdjustCameraZoom: modes, gamepad presses, tilt limits" + (cfg.InvertByDefault ? ", Y inverted" : ""));
        }

        // ---- commands
        {
            var r1 = new JumpLabel("R");
            Replace(pkg, "MarvelPlayerController." + CycleMode, result, log, [.. Action("Mode", r1), Asm.At(r1, Asm.ReturnNothing()), Asm.End()]);

            Node Pc() => a.DynCast("MarvelPlayerController", a.Inst("Engine.HUD.PlayerOwner"));
            Replace(pkg, "MarvelHUD." + SnapBehind, result, log, [.. Shortcut(selfie: false, hud: true), Asm.ReturnNothing(), Asm.End()]);
            Replace(pkg, "MarvelHUD." + Selfie, result, log, [.. Shortcut(selfie: true, hud: true), Asm.ReturnNothing(), Asm.End()]);

            // The gamepad buttons: each tracker's command is called on press and on release ("X | OnRelease X").
            foreach (var t in trackers)
                Replace(pkg, "MarvelHUD." + t.Exec, result, log, TrackerCommand(a, t));
        }

        // (Overhead name labels: MarvelGFxInWorldEntityInfo.CallSetNamesVisibility / CallShowWorldEntityDisplayName looked like the
        // hooks, but the exe never calls them (their names aren't in it; Kurt saw no change, 0.60.418). The labels are driven from
        // C++ straight into Flash; hiding them would mean editing the Flash label movie. Removed in 0.60.419.)

        // ---- adjustZoomIndex (mouse wheel): past the closest / farthest step, extra steps n (wheel up = +1 = closer)
        {
            var (idx, f, code) = Load(pkg, "MarvelPlayerCamera.adjustZoomIndex");
            string L(string n) => $"MarvelPlayerCamera.adjustZoomIndex.{n}";
            int step = code.FindIndex(s => s.Tok == Asm.AddEqInt && s.Ops[0] is OParams { V: [{ Tok: 0x01 } t, ..] } && t.Ops[0] is OObj o && o.V == a.O("MarvelPlayerCamera.currentSettingIndex"));
            Expect(step == 5, $"adjustZoomIndex: the index step isn't statement 5 ({step})");
            var normal = code[step];
            normal.Label ??= new JumpLabel("Normal");
            Node N() => a.Member(a.Inst("MarvelPlayerCamera.LastMouseAdjVec"), Vector, "Y", false);
            Node NW() => a.Member(a.Inst("MarvelPlayerCamera.LastMouseAdjVec"), Vector, "Y", true);
            Node D() => a.Local(L("Delta"));
            var s1 = new JumpLabel("NotCloser"); var s2 = new JumpLabel("NotFarther"); var s3 = new JumpLabel("NotIn");
            var closer = new JumpLabel("Closer"); var farther = new JumpLabel("Farther");
            code.InsertRange(step, [
                Asm.IfNot(Asm.Nat(Asm.LessF, N(), Asm.F(0)), s1),
                Asm.Goto(closer),
                Asm.At(s1, Asm.IfNot(Asm.Nat(Asm.GreaterF, N(), Asm.F(0)), s2)),
                Asm.Goto(farther),
                Asm.At(s2, Asm.IfNot(Asm.Nat(Asm.GreaterInt, D(), Asm.I(0)), s3)),
                Asm.IfNot(Asm.Nat(Asm.GreaterEqInt, a.Local(L("old")), a.Local(L("Max"))), normal.Label),
                Asm.Goto(closer),
                Asm.At(s3, Asm.IfNot(Asm.Nat(Asm.LessInt, D(), Asm.I(0)), normal.Label)),
                Asm.IfNot(Asm.Nat(Asm.LessEqInt, a.Local(L("old")), a.Local(L("Min"))), normal.Label),
                Asm.At(farther, Asm.Let(NW(), Asm.Nat(Asm.FClamp, Asm.Nat(Asm.SubF, N(), Asm.Cast(Asm.IntToFloat, D())), Asm.F(0), Asm.F(maxOut)))),
                Asm.ReturnNothing(),
                Asm.At(closer, Asm.Let(NW(), Asm.Nat(Asm.FClamp, Asm.Nat(Asm.SubF, N(), Asm.Cast(Asm.IntToFloat, D())), Asm.F(-maxIn), Asm.F(0)))),
                Asm.ReturnNothing(),
            ]);
            result[idx] = f.Build(code);
            log.Add($"adjustZoomIndex: {cfg.ZoomStepsCloser} extra step(s) closer, {cfg.ZoomStepsFarther} farther, {cfg.ZoomStepPercent} % each");
        }

        // ---- UpdateViewTarget: isometric gate, behind shoulder offset, distance × step^n
        {
            var (idx, f, code) = Load(pkg, "MarvelPlayerCamera.UpdateViewTarget");
            Node SelfVec(string field, string comp) => a.Member(a.Inst($"MarvelPlayerCamera.{field}"), Vector, comp, false);
            string L(string n) => $"MarvelPlayerCamera.UpdateViewTarget.{n}";
            var gate = code.Where(s => s.Tok == 0x07 && s.Ops[1] is OExpr { V.Tok: 0x28 }).ToList();
            Expect(gate.Count == 1, $"UpdateViewTarget: {gate.Count} Freecam orbit gates found, expected 1");
            gate[0].Ops[1] = new OExpr(Asm.Nat(Asm.EqF, SelfVec("LastCamLoc", "Y"), Asm.F(2)));

            int pull = code.FindIndex(s => s.Tok == 0xE0 && s.Ops[0] is OParams { V: [{ Tok: 0x00 } d, ..] } && d.Ops[0] is OObj o && o.V == a.O(L("DesiredActorPos")));
            Expect(pull >= 0, "UpdateViewTarget: DesiredActorPos -= … not found");
            var next = code[pull + 1];
            next.Label ??= new JumpLabel("AfterShoulder");
            code.InsertRange(pull + 1, [
                Asm.IfNot(Asm.Nat(Asm.EqF, SelfVec("LastCamLoc", "Y"), Asm.F(1)), next.Label),
                Asm.Nat(Asm.AddEqV, a.Local(L("DesiredActorPos")),
                    Asm.Nat(Asm.MulVF,
                        Asm.Nat(Asm.Normal, Asm.Nat(Asm.Cross, VectConst(0, 0, 1), a.Local(L("CameraFocusAdjVector")))),
                        Asm.F(cfg.ShoulderOffset))),
            ]);

            int dist = code.FindIndex(s => s.Tok == 0x0F && s.Ops[1] is OExpr { V.Tok: (byte)Asm.MulVF } m
                && m.V.Ops[0] is OParams { V: [_, { Tok: 0x01 } v] } && v.Ops[0] is OObj o && o.V == a.O("MarvelPlayerCamera.sourceCamDist"));
            Expect(dist >= 0, "UpdateViewTarget: DesiredCamOffset = camViewDir * sourceCamDist not found");
            var mul = (OParams)((OExpr)code[dist].Ops[1]).V.Ops[0];
            mul.V[1] = Asm.Nat(Asm.MulF, mul.V[1], Asm.Nat(Asm.Exp, Asm.Nat(Asm.MulF, SelfVec("LastMouseAdjVec", "Y"), Asm.F(zoomStep))));
            result[idx] = f.Build(code);
            log.Add("UpdateViewTarget: isometric mode, behind offset, extra zoom");
        }
        return result;
    }

    /// <summary>Builds the whole package (uncompressed) and checks it by reading it back: tables equal, every other export
    /// byte-identical, each new function parses, has the right sizes and rewrites byte for byte.</summary>
    public static byte[] BuildPackage(byte[] freecamPackage, FreeCamSettings cfg, List<string> log)
    {
        var pkg = Package.FromBytes(freecamPackage);
        var replaced = Build(pkg, cfg, log);
        byte[] output = FreeCamPackageEdit.ReplaceExports(pkg, replaced);
        var problems = Check(pkg, output, replaced);
        if (problems.Count > 0) throw new InvalidDataException("The built package didn't check out: " + string.Join("; ", problems));
        return output;
    }

    public static List<string> Check(Package original, byte[] output, IReadOnlyDictionary<int, byte[]> replaced)
    {
        var written = Package.FromBytes(output);
        var problems = FreeCamPackageEdit.Verify(original, written, replaced);
        foreach (var i in replaced.Keys)
        {
            var e = written.Exports[i];
            var f = ScriptFunction.Read(written.ReadExportBytes(e));
            var code = Bytecode.Parse(f.Code);
            if (Bytecode.MemSize(code) != f.MemSize) problems.Add($"{e.ObjectName}: memory size field wrong");
            if (code[^1].Tok != Bytecode.EndOfScript) problems.Add($"{e.ObjectName}: no EndOfScript");
            Bytecode.BindLabels(code);
            if (!Bytecode.Write(code).AsSpan().SequenceEqual(f.Code)) problems.Add($"{e.ObjectName}: doesn't rewrite byte for byte");
        }
        return problems;
    }

    /// <summary>A gamepad press tracker: where its state and timer live on the camera (TimerField null: both packed in the
    /// state field as state × 1000 + seconds, the timer capped at 990 s), the HUD command its button calls, and which action
    /// each press type does.</summary>
    public sealed record Tracker(string Exec, string StateField, string StateComp, string? TimerField, string? TimerComp, Func<string, string?> ActionFor);

    /// <summary>
    /// Tracker 1: LastCamLoc.Z / LastMouseAdjVec.Z, MarvelHUD.ToggleTraceActorLock (stock: a debug trace pointer).
    /// Tracker 2: LastCamRotOffset.X / .Y (transient, no script uses it), MarvelHUD.MarvelGFXHidePvPScores.
    /// Tracker 3: LastCamRotOffset.Z, packed, MarvelHUD.MarvelGFxTogglePvPScorePanel.
    /// The PvP commands are script exec functions nothing calls and DefaultInput binds only in commented-out lines.
    /// </summary>
    public static readonly Tracker[] Trackers =
    [
        new(PadButton, "LastCamLoc", "Z", "LastMouseAdjVec", "Z", _ => null),
        new(PadButton2, "LastCamRotOffset", "X", "LastCamRotOffset", "Y", _ => null),
        new(PadButton3, "LastCamRotOffset", "Z", null, null, _ => null),
    ];

    /// <summary>A tracker's state and timer as code, through <paramref name="field"/> (the camera's vector fields, read or written).</summary>
    sealed class TrackerIO(Tracker t, Func<string, string, bool, Node> field)
    {
        bool Packed => t.TimerField == null;
        Node V(bool write = false) => field(t.StateField, t.StateComp, write);
        public Node State() => Packed
            ? Asm.Cast(Asm.IntToFloat, Asm.Cast(Asm.FloatToInt, Asm.Nat(Asm.MulF, Asm.Nat(Asm.AddF, V(), Asm.F(0.5f)), Asm.F(0.001f))))
            : V();
        public Node Timer() => Packed ? Asm.Nat(Asm.SubF, V(), Asm.Nat(Asm.MulF, State(), Asm.F(1000))) : field(t.TimerField!, t.TimerComp!, false);
        public Node SetState(float s) => Packed ? Asm.Let(V(true), Asm.Nat(Asm.AddF, Asm.F(s * 1000), Timer())) : Asm.Let(V(true), Asm.F(s));
        public Node ResetTimer() => Packed ? Asm.Let(V(true), Asm.Nat(Asm.MulF, State(), Asm.F(1000))) : Asm.Let(field(t.TimerField!, t.TimerComp!, true), Asm.F(0));
        public Node AddTimer(Node delta) => Packed
            ? Asm.Let(V(true), Asm.Nat(Asm.FMin, Asm.Nat(Asm.AddF, V(), delta), Asm.Nat(Asm.AddF, Asm.Nat(Asm.MulF, State(), Asm.F(1000)), Asm.F(990))))
            : Asm.Nat(Asm.AddEqF, field(t.TimerField!, t.TimerComp!, true), delta);
    }

    /// <summary>
    /// A tracker's per-frame part (states: 0 idle, 1 held, 2 held and already used, 3 released once and waiting for a second
    /// press, 4 short press to do, 6 double to do (still held), 7 double to do (let go); the timer counts seconds since the
    /// last press or release). <paramref name="exit"/> goes on the statement after the block.
    /// </summary>
    static List<Node> TrackerFrame(Asm a, Tracker t, Func<string, string, bool, Node> camVec, Node delta, Func<string, JumpLabel, List<Node>> action, JumpLabel exit)
    {
        var io = new TrackerIO(t, camVec);
        bool usesLong = t.ActionFor("Long") != null;
        List<Node> Gesture(float state, Node? extra, string? act, float next, JumpLabel skip)
        {
            var done = new JumpLabel("Done");
            var list = new List<Node> { Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(state)), skip) };
            if (extra != null) list.Add(Asm.IfNot(extra, skip));
            if (act != null) list.AddRange(action(act, done));
            list.Add(Asm.At(done, io.SetState(next)));
            return list;
        }
        var g1 = new JumpLabel("G1"); var g2 = new JumpLabel("G2"); var g3 = new JumpLabel("G3"); var g4 = new JumpLabel("G4");
        var stmts = new List<Node> { io.AddTimer(delta) };
        // held a second (only when a long press does something): the long action now; the release then does nothing
        stmts.AddRange(Gesture(1, usesLong ? Asm.Nat(Asm.GreaterEqF, io.Timer(), Asm.F(FreeCamSettings.LongPressSeconds)) : Asm.Nat(Asm.LessF, Asm.F(1), Asm.F(0)), t.ActionFor("Long"), 2, g1));
        // released once and no second press in time: the short action
        var t1 = Gesture(3, Asm.Nat(Asm.GreaterF, io.Timer(), Asm.F(FreeCamSettings.DoublePressSeconds)), t.ActionFor("Short"), 0, g2); t1[0].Label = g1; stmts.AddRange(t1);
        // a short press while no double press is in use: the short action at once
        var t2 = Gesture(4, null, t.ActionFor("Short"), 0, g3); t2[0].Label = g2; stmts.AddRange(t2);
        // a second press in time: the double action (the button is still held: its release does nothing)
        var t3 = Gesture(6, null, t.ActionFor("Double"), 2, g4); t3[0].Label = g3; stmts.AddRange(t3);
        // the same, with the button already let go again (both presses within one frame)
        var t4 = Gesture(7, null, t.ActionFor("Double"), 0, exit); t4[0].Label = g4; stmts.AddRange(t4);
        return stmts;
    }

    /// <summary>A tracker's HUD command: called on press and on release; which one it is follows from the state.</summary>
    static List<Node> TrackerCommand(Asm a, Tracker t)
    {
        Node HudVec(string field, string comp, bool write) => a.Member(a.Ctx(a.DynCast("MarvelPlayerCamera", a.Ctx(a.Inst("Engine.HUD.PlayerOwner"), "Engine.PlayerController.PlayerCamera")),
            $"MarvelPlayerCamera.{field}"), Vector, comp, write);
        var io = new TrackerIO(t, HudVec);
        bool usesDouble = t.ActionFor("Double") != null;
        var notDouble = new JumpLabel("NotDouble");
        var release = new JumpLabel("Release"); var notHeld = new JumpLabel("NotHeld"); var shortRel = new JumpLabel("ShortRelease");
        var press = new JumpLabel("Press"); var single = new JumpLabel("Single");
        return [
            Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(6)), notDouble),                    // released before the frame did the double
            io.SetState(7),
            Asm.ReturnNothing(),
            Asm.At(notDouble, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(1)), notHeld)),
            Asm.Goto(release),
            Asm.At(notHeld, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(2)), press)),
            // release
            Asm.At(release, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(2)), shortRel)),
            io.SetState(0),                                                                // the press was used already
            Asm.ReturnNothing(),
            Asm.At(shortRel, io.SetState(usesDouble ? 3 : 4)),                             // a short press: wait for a second one, or do it now
            io.ResetTimer(),
            Asm.ReturnNothing(),
            // press
            Asm.At(press, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(3)), single)),
            Asm.IfNot(Asm.Nat(Asm.LessEqF, io.Timer(), Asm.F(FreeCamSettings.DoublePressSeconds)), single),
            io.SetState(6),                                                                // a second press in time
            Asm.ReturnNothing(),
            Asm.At(single, io.SetState(1)),
            io.ResetTimer(),
            Asm.ReturnNothing(),
            Asm.End(),
        ];
    }

    static Node VectConst(float x, float y, float z)
    {
        var n = new Node(0x23);
        var b = new byte[12];
        BitConverter.GetBytes(x).CopyTo(b, 0); BitConverter.GetBytes(y).CopyTo(b, 4); BitConverter.GetBytes(z).CopyTo(b, 8);
        n.Ops.Add(new OBytes(b));
        return n;
    }

    public static (int, ExportEntry) Find(Package pkg, string path)
    {
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (string.Equals(pkg.PathOf(pkg.Exports[i]), path, StringComparison.OrdinalIgnoreCase)) return (i, pkg.Exports[i]);
        throw new FreeCamKeyError(path);
    }

    public static (int, ScriptFunction, List<Node>) Load(Package pkg, string path)
    {
        var (i, e) = Find(pkg, path);
        var f = ScriptFunction.Read(pkg.ReadExportBytes(e));
        var code = Bytecode.Parse(f.Code);
        Bytecode.BindLabels(code);
        return (i, f, code);
    }

    static void Replace(Package pkg, string path, Dictionary<int, byte[]> result, List<string> log, List<Node> code)
    {
        var (i, f, _) = Load(pkg, path);
        Expect((f.Flags & ScriptFunction.FuncExec) != 0, $"{path} is not an exec function");
        Expect((f.Flags & ScriptFunction.FuncNative) == 0, $"{path} is native: its script body would never run");
        Expect(ParamCount(pkg, i) == 0, $"{path} takes parameters");
        result[i] = f.Build(code);
        log.Add($"{path}: new command");
    }

    /// <summary>One statement put in front of a function's own code (the rest kept as it is).</summary>
    static void Prepend(Package pkg, string path, Dictionary<int, byte[]> result, List<string> log, Node statement)
    {
        var (i, f, code) = Load(pkg, path);
        code.Insert(0, statement);
        result[i] = f.Build(code);
        log.Add($"{path}: changed");
    }

    static int ParamCount(Package pkg, int funcIndex)
    {
        int n = 0;
        for (int j = 0; j < pkg.Exports.Length; j++)
        {
            var e = pkg.Exports[j];
            if (e.OuterIndex != funcIndex + 1 || !pkg.ClassOf(e).EndsWith("Property")) continue;
            if ((BitConverter.ToUInt64(pkg.ReadExportBytes(e), 20) & 0x80) != 0) n++;
        }
        return n;
    }

    static void Expect(bool ok, string what) { if (!ok) throw new InvalidDataException(what); }
}

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
    /// <summary>One mode each from the keyboard (MarvelHUD script exec functions nothing calls or binds: the debug game UI
    /// toggle, a focus log line). OrbitCmd (a UI debug mode) was Free Orbit's in an untested build: only listed so its key lines
    /// are taken out (Snap Behind switches to Free Orbit).</summary>
    public const string OrbitCmd = "UIDebugModeAll", BehindCmd = "MarvelGFxToggleDebugGameUI", IsoCmd = "LogFocus";
    /// <summary>Remember Camera's store: two of the HUD's config ints, unused in MHO (its HUD is Flash; Engine's canvas
    /// console messages use them), written by the HUD's SaveConfig into the game's settings.</summary>
    const string Store1 = "Engine.HUD.ConsoleFontSize", Store2 = "Engine.HUD.MessageFontOffset";
    internal const int Marker = 1_000_000_000;
    const string Vector = "Core.Object.Vector", Rotator = "Core.Object.Rotator";
    const float DegToUnits = 65536f / 360f;
    const int DivF = 0xAC, Loge = 0xC0;

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
        int maxOut = new[] { cfg.ZoomStepsFarther, cfg.SnapKeepZoom ? 0 : -Steps(cfg.SnapZoomX), cfg.SelfieKeepZoom ? 0 : -Steps(cfg.SelfieZoomX), cfg.DefaultGameZoom ? 0 : -Steps(cfg.DefaultZoomX), cfg.BehindKeepZoom ? 0 : -Steps(cfg.BehindZoomX) }.Max();
        int maxIn = new[] { cfg.ZoomStepsCloser, cfg.SnapKeepZoom ? 0 : Steps(cfg.SnapZoomX), cfg.SelfieKeepZoom ? 0 : Steps(cfg.SelfieZoomX), cfg.BehindKeepZoom ? 0 : Steps(cfg.BehindZoomX) }.Max();

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
                    .. BehindZoom(false),
                    Asm.At(b1, Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(2)), after)),   // Default MHO: its height and zoom
                    .. EnterDefault(false),
                ];
            }
            if (which is "Behind" or "Isometric") return SetMode(which == "Behind" ? 1 : 2, hud: false);
            // Snap: behind the hero at Behind Mode's tilt, at once (a zero LastCamOffset makes UpdateViewTarget jump instead of
            // easing; Kurt: the easing lagged and the old tilt stayed). Free Orbit stays free afterwards.
            // Selfie: the same, from the front (half a circle round), facing the hero.
            if (which is "Snap" or "Selfie") return Shortcut(which == "Selfie", hud: false);
            return [];
        }
        // One mode at once (the dedicated keys): Behind starts at its tilt, Isometric at the default height and zoom, as the cycle does.
        List<Node> SetMode(int m, bool hud)
        {
            Node C() => hud ? a.DynCast("MarvelPlayerCamera", a.Ctx(a.Inst("Engine.HUD.PlayerOwner"), "Engine.PlayerController.PlayerCamera")) : Cam();
            Node V(string field, string comp) => a.Member(a.Ctx(C(), $"MarvelPlayerCamera.{field}"), Vector, comp, true);
            Node PcVar(string prop) => hud ? a.Ctx(a.DynCast("MarvelPlayerController", a.Inst("Engine.HUD.PlayerOwner")), prop) : a.Inst(prop);
            var list = new List<Node> { Asm.Let(V("LastCamLoc", "Y"), Asm.F(m)) };
            if (m == 1) { list.Add(Asm.Let(PcVar(Pitch), Asm.I(-(int)(cfg.BehindTilt * DegToUnits)))); list.AddRange(BehindZoom(hud)); }
            if (m == 2) list.AddRange(EnterDefault(hud));
            return list;
        }
        // Default MHO's start: the height set (0 = the game's), the extra wheel steps taken off, and the game's zoom step kept,
        // or a zoom factor like Snap Behind's (of the closest step's distance). For the factor, the game's own steps are walked
        // out from the closest until one is as far as the factor's distance (its angle and height apply, and the wheel carries
        // on through the steps; Kurt 2026-11-09: extra steps only move the camera out, so the game's tilt curve didn't follow);
        // only past the farthest step do extra steps make up the rest.
        List<Node> EnterDefault(bool hud)
        {
            Node C() => hud ? a.DynCast("MarvelPlayerCamera", a.Ctx(a.Inst("Engine.HUD.PlayerOwner"), "Engine.PlayerController.PlayerCamera")) : Cam();
            Node PcVar(string prop) => hud ? a.Ctx(a.DynCast("MarvelPlayerController", a.Inst("Engine.HUD.PlayerOwner")), prop) : a.Inst(prop);
            var list = new List<Node> { Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "X", true), Asm.F(cfg.DefaultHeight)) };
            // Keep Current In-Game Zoom (0.60.451, Kurt: as on Snap Behind): the zoom is left as it is, extra wheel steps too
            if (cfg.DefaultGameZoom) return list;
            list.Add(Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "Y", true), Asm.F(0)));
            Node Max() => a.CtxFinal(C(), "MarvelPlayerCamera.getMaxZoomIndex");
            Node Idx() => a.Ctx(C(), "MarvelPlayerCamera.currentSettingIndex");
            Node Dist(Node i) => a.Member(Asm.Elem(i, a.Ctx(C(), "MarvelPlayerCamera.gameCamSettings")), "MarvelPlayerCamera.CamSetting", "ZoomDistance", false);
            Node Target() => Asm.Nat(DivF, Dist(Max()), Asm.F((float)cfg.DefaultZoomX));
            var walk = new JumpLabel("Walk"); var found = new JumpLabel("Found");
            list.AddRange([
                Asm.Let(Idx(), Max()),
                Asm.At(walk, Asm.IfNot(Asm.Nat(Asm.GreaterInt, Idx(), Asm.I(0)), found)),
                Asm.IfNot(Asm.Nat(Asm.LessF, Dist(Idx()), Target()), found),
                Asm.Let(Idx(), Asm.Nat(Asm.SubInt, Idx(), Asm.I(1))),
                Asm.Goto(walk),
                // past the farthest step: extra steps for the rest (distance x step^n)
                Asm.At(found, Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "Y", true),
                    Asm.Nat(Asm.FMax, Asm.F(0), Asm.Nat(DivF, Asm.Nat(Loge, Asm.Nat(DivF, Target(), Dist(Idx()))), Asm.F(MathF.Log(1 + cfg.ZoomStepPercent / 100f)))))),
            ]);
            list.Add(Asm.LetBool(a.Ctx(C(), "MarvelPlayerCamera.gameCamIndexChanged"), Asm.True()));
            list.Add(Asm.Let(PcVar("MarvelPlayerController.m_CameraZoomIndex"), a.Ctx(C(), "MarvelPlayerCamera.currentSettingIndex")));
            return list;
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
                // Free Orbit (Kurt, 2026-11-09): Locked Behind would swing the camera straight back and Default MHO ignores the angle
                Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastCamLoc"), Vector, "Y", true), Asm.F(0)),
                Asm.Let(PcVar(Yaw), selfie ? Asm.Nat(Asm.AddInt, HeroYaw(), Asm.I(32768)) : HeroYaw()),
                Asm.Let(PcVar(Pitch), Asm.I(-(int)(tilt * DegToUnits))),
                Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "X", true), Asm.F(height)),   // Freecam's height
            };
            if (!keepZoom) list.AddRange(SetZoom(C, PcVar, zoom));
            list.Add(Asm.Let(a.Ctx(C(), "MarvelPlayerCamera.LastCamOffset"), VectConst(0, 0, 0)));
            // (Kurt, 2026-11-11: still a third to half a second of lag in battle) a new zoom step also has its own rotation offset
            // and field of view, which UpdateViewTarget eases to (RInterpTo / FInterpTo); its reset values make it jump instead:
            // LastRotOffset rot(-1,-1,-1) takes sourceCamRot, LastCamFOV 0 takes sourceCamFOV
            list.Add(Asm.Let(a.Ctx(C(), "MarvelPlayerCamera.LastRotOffset"), RotConst(-1, -1, -1)));
            list.Add(Asm.Let(a.Ctx(C(), "MarvelPlayerCamera.LastCamFOV"), Asm.F(0)));
            return list;
        }

        // The zoom set as Snap Behind sets it: the game's closest step, then <zoom> extra steps (negative = closer).
        List<Node> SetZoom(Func<Node> C, Func<string, Node> PcVar, float zoom) =>
        [
            Asm.Let(a.Member(a.Ctx(C(), "MarvelPlayerCamera.LastMouseAdjVec"), Vector, "Y", true), Asm.F(zoom)),
            Asm.Let(a.Ctx(C(), "MarvelPlayerCamera.currentSettingIndex"), a.CtxFinal(C(), "MarvelPlayerCamera.getMaxZoomIndex")),
            Asm.LetBool(a.Ctx(C(), "MarvelPlayerCamera.gameCamIndexChanged"), Asm.True()),
            Asm.Let(PcVar("MarvelPlayerController.m_CameraZoomIndex"), a.Ctx(C(), "MarvelPlayerCamera.currentSettingIndex")),
        ];

        // Locked Behind switched on (the cycle or its key): Keep Current In-Game Zoom, or the zoom set as Snap Behind's (Kurt, 2026-11-09).
        List<Node> BehindZoom(bool hud)
        {
            if (cfg.BehindKeepZoom) return [];
            Node C() => hud ? a.DynCast("MarvelPlayerCamera", a.Ctx(a.Inst("Engine.HUD.PlayerOwner"), "Engine.PlayerController.PlayerCamera")) : Cam();
            Node PcVar(string prop) => hud ? a.Ctx(a.DynCast("MarvelPlayerController", a.Inst("Engine.HUD.PlayerOwner")), prop) : a.Inst(prop);
            return SetZoom(C, PcVar, -(float)cfg.StepsFor(cfg.BehindZoomX));
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
            var end = new JumpLabel("End"); var behind = new JumpLabel("Behind"); var height = new JumpLabel("Height"); var tiltFree = new JumpLabel("TiltFree");
            height1.Label = null; height2.Label = null;
            var stmts = code.Take(first).ToList();
            var next = resume;
            foreach (var t in trackers)
            {
                var exit = new JumpLabel("Tracker");
                var block = TrackerFrame(a, t, CamVec, Delta(), Action, exit, cfg.LongPressMs / 1000f);
                block[0].Label = next;
                stmts.AddRange(block);
                next = exit;
            }
            if (cfg.RememberCamera)
            {
                var block = Remember(a, Cam, CamVec, Input, next, cfg.DefaultHeight);
                stmts.AddRange(block.Code);
                next = block.Exit;
            }
            var g5 = next;
            stmts.AddRange([
                Asm.At(g5, Asm.IfNot(Asm.Nat(Asm.NotEqF, Mode(), Asm.F(2)), end)),             // isometric: no orbit input
                // Lock Tilt (Kurt, 2026-11-09): Locked Behind holds its Start Tilt every frame (stick and mouse camera can't tilt it)
                .. (cfg.BehindLockTilt ? new[] {
                    Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(1)), tiltFree),
                    Asm.Let(a.Inst(Pitch), Asm.I(-(int)(cfg.BehindTilt * DegToUnits))),
                    Asm.Goto(behind) } : []),
                Asm.At(tiltFree, Asm.Nat(Asm.AddEqInt, a.Inst(Pitch), Asm.Cast(Asm.FloatToInt,
                    Asm.Nat(Asm.MulF,
                        Asm.Nat(Asm.MulF, Asm.Nat(Asm.MulF, Input("GamepadRawJoyLookUp"), Delta()), Asm.F(-orbit * pitchSign)),
                        Asm.Nat(Asm.SubF, Asm.F(1), CamVec("LastCamLoc", "X")))))),            // not while RB held (Freecam)
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
            Replace(pkg, "MarvelHUD." + BehindCmd, result, log, [.. SetMode(1, hud: true), Asm.ReturnNothing(), Asm.End()]);
            Replace(pkg, "MarvelHUD." + IsoCmd, result, log, [.. SetMode(2, hud: true), Asm.ReturnNothing(), Asm.End()]);
            // Remember Camera: a new camera starts in "restore pending" (mode -1) and the first frame reads the store.
            if (cfg.RememberCamera)
                Prepend(pkg, "MarvelPlayerCamera.PostBeginPlay", result, log, Asm.Let(a.Member(a.Inst("MarvelPlayerCamera.LastCamLoc"), Vector, "Y", true), Asm.F(-1)));
        }

            // The gamepad buttons: each tracker's command is called on press and on release ("X | OnRelease X").
            foreach (var t in trackers)
                Replace(pkg, "MarvelHUD." + t.Exec, result, log, TrackerCommand(a, t));

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
            if (!cfg.DefaultGameTilt)
            {
                // Default MHO with its own tilt: the game's direction (both of its branches end on the labeled goto after the
                // normal one) turned to the set pitch, yaw kept. 0x50 vector -> rotator, 0x39 rotator -> vector (as compiled).
                Node CamViewDir() => a.Local(L("camViewDir"));
                Node CurrentRot() => a.Local(L("CurrentRot"));
                int dir = code.FindIndex(s => s.Tok == 0x0F && s.Ops[0] is OExpr { V: { Tok: 0x00 } t } && t.Ops[0] is OObj o && o.V == a.O(L("camViewDir"))
                    && s.Ops[1] is OExpr { V: { Tok: (byte)Asm.Normal } n } && n.Ops[0] is OParams { V: [{ } neg] } && neg.Ops.Count > 0 && neg.Ops[0] is OParams { V: [{ Tok: 0x01 } sd] } && sd.Ops[0] is OObj so && so.V == a.O("MarvelPlayerCamera.sourceCamDir"));
                Expect(dir >= 0 && code[dir + 1].Tok == 0x06 && code[dir + 1].Label != null, "UpdateViewTarget: the game's camera direction (Default MHO) not where expected");
                var join = code[dir + 1];
                var tilt = new List<Node>
                {
                    Asm.Let(CurrentRot(), Asm.Cast(0x50, CamViewDir())),
                    Asm.Let(a.Member(CurrentRot(), Rotator, "Pitch", true), Asm.I(-(int)(cfg.DefaultTilt * DegToUnits))),
                    Asm.Let(CamViewDir(), Asm.Cast(0x39, CurrentRot())),
                };
                tilt[0].Label = join.Label; join.Label = null;
                code.InsertRange(dir + 1, tilt);
            }

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

        // ---- RemoveControllerBindsWithName: our gamepad lines left alone (Kurt, 2026-11-09: start-screen crash; that theory was
        // WRONG, the crash came back: see SetBind below. Kept: the game also dropped our Back line in a session). The game removes
        // a controller button's bindings (Back, seen) and reports each one with a command through native OnBindChanged, which
        // reads a client object (global + 0x5B0) that doesn't exist yet during the splash screen: null read, crash at
        // MarvelHeroesOmega.exe+0x13C033D, on about every third start with an action on Back. The game's own lines there have
        // an empty command, so they never reach it. Our lines are skipped (one test per tracker command, jumping to the loop's
        // next element), so they're neither reported nor deleted (the game also dropped our Back line later in a session).
        {
            var (idx, f, code) = Load(pkg, "MarvelInput.RemoveControllerBindsWithName");
            // [4] = if !(&&(Shift, Control, name match)) goto L1 (next element); [5] = if !(Bindings[I].Command != "") goto L2
            Expect(code.Count > 6 && code[4].Tok == 0x07 && code[5].Tok == 0x07, "RemoveControllerBindsWithName: not the expected loop");
            var next = ((OJump)code[4].Ops[0]).Target!;
            Node CommandExpr()
            {
                var copy = Bytecode.Parse(f.Code);
                var cond = ((OExpr)copy[5].Ops[1]).V;                      // native7B(Bindings[I].Command, "")
                Expect(cond.Tok == 0x7B && cond.Ops[0] is OParams { V.Count: 2 }, "RemoveControllerBindsWithName: command test not found");
                return ((OParams)cond.Ops[0]).V[0];
            }
            var skips = Trackers.Select(t => $"{t.Exec} | OnRelease {t.Exec}")
                .Select(cmd => Asm.IfNot(Asm.Nat(0x7B, CommandExpr(), Asm.Str(cmd)), next)).ToList();
            code.InsertRange(5, skips);
            result[idx] = f.Build(code);
            log.Add("RemoveControllerBindsWithName: FreeCam Options's gamepad lines left alone");
        }

        // ---- SetBind: Freecam's KBM_ lines not reported (Kurt, 2026-11-10: the start-screen crash again after the fix above,
        // on the live client and the telemetry client). The minidumps (before and after that fix) all hold the command being
        // run, "SetBind MouseX KBM_CamX": the first part of Freecam's middle-mouse / Tilde lines (KBM_MMB, KBM_On; F5-F7 and
        // F10 also run SetBind). A press of one of those keys during the start movie runs SetBind (an exec), which calls
        // native OnBindChanged, which reads the client object that doesn't exist yet: the crash at exe+0x13C033D (stack: key
        // press, exec input command, script, execOnBindChanged). The binding itself is the script's Bindings change; only the
        // report is skipped for commands starting with KBM_ (Freecam's internal names, never shown in the key binding UI).
        {
            var (idx, f, code) = Load(pkg, "MarvelInput.SetBind");
            var calls = Enumerable.Range(0, code.Count - 1).Where(i => code[i].Tok is 0x1B or 0x1C && code[i + 1].Tok == 0x04).ToList();
            Expect(calls.Count == 2, "SetBind: its two OnBindChanged calls not found");
            foreach (int i in Enumerable.Reverse(calls))
            {
                var call = Bytecode.Parse(f.Code)[i];
                var args = call.Ops.OfType<OParams>().Single().V;
                Expect(args.Count == 4 && args[1].Tok == 0x00, "SetBind: OnBindChanged's command argument not found");
                var ret = code[i + 1].Label ??= new JumpLabel("Ret");
                code.Insert(i, Asm.IfNot(Asm.Nat(0x7B, Asm.Nat(0x80, args[1], Asm.I(4)), Asm.Str("KBM_")), ret));
            }
            result[idx] = f.Build(code);
            log.Add("SetBind: Freecam's KBM_ lines not reported (start-screen crash)");
        }
        return result;
    }

    /// <summary>
    /// Remember Camera, every frame in AdjustCameraZoom (controller context), on the label <paramref name="entry"/>:
    /// restore: a camera in mode -1 (new: PostBeginPlay) reads the store once the HUD is there; nothing saved yet = Free Orbit.
    /// save: when the mode, zoom (the game's step and the extra steps) or angle differ from the store and the camera isn't being
    /// turned (sticks / mouse camera at rest, RB up), the store is set and the HUD's SaveConfig writes it. Behind keeps the stored
    /// yaw (it follows the hero, so saving it would write every frame).
    /// Store 1 = 1e9 + mode × 1e8 + step × 1e6 + (extra steps × 100 + 500000); store 2 = (pitch + 16384) &lt;&lt; 16 | (yaw &amp; 65535).
    /// </summary>
    static (List<Node> Code, JumpLabel Exit) Remember(Asm a, Func<Node> cam, Func<string, string, bool, Node> camVec, Func<string, Node> input, JumpLabel entry, float defaultHeight)
    {
        const string Pitch = "MarvelPlayerController.vanityCamNum", Yaw = "MarvelPlayerController.turnTableNum";
        const int DivI = 0x91, MulI = 0x90, Shl = 0x94, Shr = 0x95, OrI = 0x9E, NotEqI = 0x9B, SaveConfig = 0x218;
        Node Hud() => a.Inst("Engine.PlayerController.myHUD");
        Node S1() => a.Ctx(Hud(), Store1);
        Node S2() => a.Ctx(Hud(), Store2);
        Node I(int v) => Asm.I(v);
        Node Mode() => camVec("LastCamLoc", "Y", false);
        Node Step() => a.Ctx(cam(), "MarvelPlayerCamera.currentSettingIndex");
        Node N() => camVec("LastMouseAdjVec", "Y", false);
        Node Int(Node f) => Asm.Cast(Asm.FloatToInt, f);
        Node Flt(Node i) => Asm.Cast(Asm.IntToFloat, i);
        var exit = new JumpLabel("RememberDone"); var save = new JumpLabel("Save"); var none = new JumpLabel("NoneSaved");
        var keepYaw = new JumpLabel("KeepYaw"); var compare = new JumpLabel("Compare"); var write = new JumpLabel("Write");
        var restoreAngle = new JumpLabel("RestoreAngle");
        // the packed current values (store 2's yaw: the stored one in Behind)
        Node Cur1() => Asm.Nat(Asm.AddInt, Asm.Nat(Asm.AddInt, Asm.Nat(Asm.AddInt, I(Marker), Asm.Nat(MulI, Int(Mode()), I(100_000_000))),
            Asm.Nat(MulI, Step(), I(1_000_000))), Int(Asm.Nat(Asm.AddF, Asm.Nat(Asm.MulF, N(), Asm.F(100)), Asm.F(500000.5f))));
        Node PitchPart() => Asm.Nat(Shl, Asm.Nat(Asm.AddInt, a.Inst(Pitch), I(16384)), I(16));
        var code = new List<Node>
        {
            // ---- restore (once per camera)
            Asm.At(entry, Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(-1)), save)),
            Asm.IfNot(Asm.Nat(Asm.NotEqObj, Hud(), Asm.None()), exit),                          // no HUD yet: next frame
            Asm.IfNot(Asm.Nat(Asm.GreaterEqInt, S1(), I(Marker)), none),
            Asm.Let(camVec("LastCamLoc", "Y", true), Flt(Asm.Nat(DivI, Asm.Nat(Asm.SubInt, S1(), I(Marker)), I(100_000_000)))),
            Asm.Let(a.Ctx(cam(), "MarvelPlayerCamera.currentSettingIndex"),
                Asm.Nat(Asm.SubInt, Asm.Nat(DivI, Asm.Nat(Asm.SubInt, S1(), I(Marker)), I(1_000_000)), Asm.Nat(MulI, Int(Mode()), I(100)))),
            Asm.LetBool(a.Ctx(cam(), "MarvelPlayerCamera.gameCamIndexChanged"), Asm.True()),
            Asm.Let(a.Inst("MarvelPlayerController.m_CameraZoomIndex"), Step()),
            Asm.Let(camVec("LastMouseAdjVec", "Y", true), Asm.Nat(Asm.MulF, Flt(Asm.Nat(Asm.SubInt,
                Asm.Nat(Asm.SubInt, S1(), Asm.Nat(MulI, Asm.Nat(DivI, S1(), I(1_000_000)), I(1_000_000))), I(500000))), Asm.F(0.01f))),
            Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(2)), restoreAngle),                          // Default MHO: its height
            Asm.Let(camVec("LastMouseAdjVec", "X", true), Asm.F(defaultHeight)),
            Asm.At(restoreAngle, Asm.Let(a.Inst(Yaw), Asm.Nat(Asm.AndInt, S2(), I(65535)))),
            Asm.Let(a.Inst(Pitch), Asm.Nat(Asm.SubInt, Asm.Nat(Shr, S2(), I(16)), I(16384))),
            Asm.Goto(exit),
            Asm.At(none, Asm.Let(camVec("LastCamLoc", "Y", true), Asm.F(0))),
            Asm.Goto(exit),
            // ---- save (when changed and at rest)
            Asm.At(save, Asm.IfNot(Asm.Nat(Asm.NotEqObj, Hud(), Asm.None()), exit)),
            Asm.IfNot(Asm.Nat(Asm.EqF, input("GamepadRawJoyLookUp"), Asm.F(0)), exit),
            Asm.IfNot(Asm.Nat(Asm.EqF, input("GamepadRawJoyLookRight"), Asm.F(0)), exit),
            Asm.IfNot(Asm.Nat(Asm.EqF, camVec("LastCamLoc", "X", false), Asm.F(0)), exit),
            Asm.IfNot(Asm.Nat(Asm.EqF, Mode(), Asm.F(1)), keepYaw),
            // Behind: compare with the stored yaw
            Asm.IfNot(Asm.Nat(NotEqI, Cur1(), S1()), compare),
            Asm.Goto(write),
            Asm.At(compare, Asm.IfNot(Asm.Nat(NotEqI, Asm.Nat(OrI, PitchPart(), Asm.Nat(Asm.AndInt, S2(), I(65535))), S2()), exit)),
            Asm.At(write, Asm.Let(S2(), Asm.Nat(OrI, PitchPart(), Asm.Nat(Asm.AndInt, S2(), I(65535))))),
            Asm.Let(S1(), Cur1()),
            Asm.CtxCall(Hud(), Asm.Nat(SaveConfig)),
            Asm.Goto(exit),
        };
        // other modes: yaw as it is
        var compare2 = new JumpLabel("Compare2"); var write2 = new JumpLabel("Write2");
        Node Cur2() => Asm.Nat(OrI, PitchPart(), Asm.Nat(Asm.AndInt, a.Inst(Yaw), I(65535)));
        code.AddRange([
            Asm.At(keepYaw, Asm.IfNot(Asm.Nat(NotEqI, Cur1(), S1()), compare2)),
            Asm.Goto(write2),
            Asm.At(compare2, Asm.IfNot(Asm.Nat(NotEqI, Cur2(), S2()), exit)),
            Asm.At(write2, Asm.Let(S2(), Cur2())),
            Asm.Let(S1(), Cur1()),
            Asm.CtxCall(Hud(), Asm.Nat(SaveConfig)),
        ]);
        return (code, exit);
    }

    /// <summary>The store's two HUD properties as imports (Engine.HUD's IntProperties; MarvelGame.upk doesn't name them).</summary>
    static (List<string> Names, List<NewImport> Imports) StoreImports(Package pkg)
    {
        var a = new Asm(pkg);
        var names = new List<string>(); var imports = new List<NewImport>();
        if (a.Has(Store1) && a.Has(Store2)) return (names, imports);
        int hud = a.O("Engine.HUD");
        if (hud >= 0) throw new InvalidDataException("Engine.HUD isn't an import in this MarvelGame.upk");
        foreach (var p in new[] { Store1, Store2 })
        {
            if (a.Has(p)) continue;
            string leaf = p[(p.LastIndexOf('.') + 1)..];
            if (!pkg.Names.Contains(leaf, StringComparer.OrdinalIgnoreCase)) names.Add(leaf);
            imports.Add(new NewImport("Core", "IntProperty", hud, leaf));
        }
        if (!pkg.Names.Contains("IntProperty", StringComparer.OrdinalIgnoreCase)) names.Add("IntProperty");
        return (names, imports);
    }

    /// <summary>Builds the whole package (uncompressed) and checks it by reading it back: tables equal, every other export
    /// byte-identical, each new function parses, has the right sizes and rewrites byte for byte.</summary>
    public static byte[] BuildPackage(byte[] freecamPackage, FreeCamSettings cfg, List<string> log)
    {
        var pkg = Package.FromBytes(freecamPackage);
        if (cfg.RememberCamera)
        {
            // Remember Camera's store: Engine.HUD's two ints become imports first (whole package rewritten, every existing
            // index kept; checked by PackageRebuilder's own verify), then the functions are replaced in that package.
            var (names, imports) = StoreImports(pkg);
            if (imports.Count > 0)
            {
                byte[] grown = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), [], out var grownData, names, imports);
                var problems0 = PackageRebuilder.Verify(pkg, grown, Array.Empty<int>(), [], grownData, names, imports);
                if (problems0.Count > 0) throw new InvalidDataException("Adding the camera store's imports didn't check out: " + string.Join("; ", problems0));
                pkg = Package.FromBytes(grown);
                log.Add($"MarvelGame.upk: {imports.Count} import(s) added for Remember Camera (Engine.HUD's ConsoleFontSize / MessageFontOffset)");
            }
        }
        var replaced = Build(pkg, cfg, log);
        if (cfg.WallsFade) WallsFade(pkg, replaced, log);
        byte[] output = FreeCamPackageEdit.ReplaceExports(pkg, replaced);
        var problems = Check(pkg, output, replaced);
        if (problems.Count > 0) throw new InvalidDataException("The built package didn't check out: " + string.Join("; ", problems));
        return output;
    }

    /// <summary>
    /// Walls fade at start: Freecam 1.2 adds OverrideOcclusion = true and OverrideOcclusionValue = 2 to Default__OcclusionManager
    /// (interior walls always visible; F11 = MarvelCheatManager.OcclusionOverrideToggle). Both tags are taken out, so the object
    /// is Freecam 1.1's (= the game's own) again and F11 still switches. (0.60.451's first version set only OverrideOcclusion to
    /// false and kept OverrideOcclusionValue: with a gamepad action on Back the game crashed at the start screen on about every
    /// second launch, Kurt 2026-11-09; 1.1's object never did.) Freecam 1.0 / 1.1: nothing to take out.
    /// </summary>
    static void WallsFade(Package pkg, Dictionary<int, byte[]> replaced, List<string> log)
    {
        int idx = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals("Default__OcclusionManager", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { log.Add("Walls fade: no OcclusionManager defaults in this MarvelGame.upk; nothing to change."); return; }
        byte[] d = pkg.ReadExportBytes(pkg.Exports[idx]);
        var keep = new List<byte>(d.Take(4));
        int p = 4, removed = 0;
        string Name(int at) { int n = BitConverter.ToInt32(d, at); return n >= 0 && n < pkg.Names.Length ? pkg.Names[n] : ""; }
        for (int guard = 0; guard < 64; guard++)
        {
            if (p + 8 > d.Length) { log.Add("Walls fade: OcclusionManager defaults didn't read as expected; left as they are."); return; }
            string name = Name(p);
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { keep.AddRange(d.Skip(p)); break; }
            string type = Name(p + 8);
            int size = BitConverter.ToInt32(d, p + 16);
            int value = p + 24;
            if (type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) || type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase)) value += 8;
            int end = type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? value + 1 : value + size;
            if (end > d.Length) { log.Add("Walls fade: OcclusionManager defaults didn't read as expected; left as they are."); return; }
            if (name.Equals("OverrideOcclusion", StringComparison.OrdinalIgnoreCase) || name.Equals("OverrideOcclusionValue", StringComparison.OrdinalIgnoreCase)) removed++;
            else keep.AddRange(d.Skip(p).Take(end - p));
            p = end;
        }
        if (removed == 0) { log.Add("Walls fade: this Freecam has no wall override (1.0 / 1.1): walls fade anyway."); return; }
        replaced[idx] = keep.ToArray();
        log.Add("Walls fade: interior walls fade at start (Freecam 1.2's wall override taken out, as in 1.1).");
        // F11 is left as Freecam 1.2 has it. A first version also called OcclusionOverrideBackground() before the toggle
        // (a guess from its name); the game crashed right at start with it (Kurt, 2026-11-09), so it was taken out.
    }

    public static List<string> Check(Package original, byte[] output, IReadOnlyDictionary<int, byte[]> replaced)
    {
        var written = Package.FromBytes(output);
        var problems = FreeCamPackageEdit.Verify(original, written, replaced);
        foreach (var i in replaced.Keys)
        {
            var e = written.Exports[i];
            if (e.ObjectName.Equals("Default__OcclusionManager", StringComparison.OrdinalIgnoreCase)) continue;   // not a function (WallsFade)
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
    /// press, 4 short press to do, 6 double to do (still held), 7 double to do (let go), 8 a second press held (Short Then Long
    /// in use: held for the long-press time = short then long, let go earlier = double); the timer counts seconds since the
    /// last press or release). <paramref name="exit"/> goes on the statement after the block.
    /// </summary>
    static List<Node> TrackerFrame(Asm a, Tracker t, Func<string, string, bool, Node> camVec, Node delta, Func<string, JumpLabel, List<Node>> action, JumpLabel exit, float longSeconds)
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
        stmts.AddRange(Gesture(1, usesLong ? Asm.Nat(Asm.GreaterEqF, io.Timer(), Asm.F(longSeconds)) : Asm.Nat(Asm.LessF, Asm.F(1), Asm.F(0)), t.ActionFor("Long"), 2, g1));
        // released once and no second press in time: the short action
        var t1 = Gesture(3, Asm.Nat(Asm.GreaterF, io.Timer(), Asm.F(FreeCamSettings.DoublePressSeconds)), t.ActionFor("Short"), 0, g2); t1[0].Label = g1; stmts.AddRange(t1);
        // a short press while no double press is in use: the short action at once
        var t2 = Gesture(4, null, t.ActionFor("Short"), 0, g3); t2[0].Label = g2; stmts.AddRange(t2);
        // a second press in time: the double action (the button is still held: its release does nothing)
        var t3 = Gesture(6, null, t.ActionFor("Double"), 2, g4); t3[0].Label = g3; stmts.AddRange(t3);
        // the same, with the button already let go again (both presses within one frame)
        var g5 = new JumpLabel("G5");
        var t4 = Gesture(7, null, t.ActionFor("Double"), 0, g5); t4[0].Label = g4; stmts.AddRange(t4);
        // short then long: the second press held for the long-press time (state 8, only when Short Then Long is in use)
        var t5 = Gesture(8, Asm.Nat(Asm.GreaterEqF, io.Timer(), Asm.F(longSeconds)), t.ActionFor("ShortLong"), 2, exit); t5[0].Label = g5; stmts.AddRange(t5);
        return stmts;
    }

    /// <summary>A tracker's HUD command: called on press and on release; which one it is follows from the state.</summary>
    static List<Node> TrackerCommand(Asm a, Tracker t)
    {
        Node HudVec(string field, string comp, bool write) => a.Member(a.Ctx(a.DynCast("MarvelPlayerCamera", a.Ctx(a.Inst("Engine.HUD.PlayerOwner"), "Engine.PlayerController.PlayerCamera")),
            $"MarvelPlayerCamera.{field}"), Vector, comp, write);
        var io = new TrackerIO(t, HudVec);
        bool usesDouble = t.ActionFor("Double") != null;
        bool usesShortLong = t.ActionFor("ShortLong") != null;
        var notSecond = new JumpLabel("NotSecond");
        var notDouble = new JumpLabel("NotDouble");
        var release = new JumpLabel("Release"); var notHeld = new JumpLabel("NotHeld"); var shortRel = new JumpLabel("ShortRelease");
        var press = new JumpLabel("Press"); var single = new JumpLabel("Single");
        return [
            // let go of a second press before the long-press time: a double press (the frame does it in state 7)
            Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(8)), notSecond),
            io.SetState(7),
            Asm.ReturnNothing(),
            Asm.At(notSecond, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(6)), notDouble)),    // released before the frame did the double
            io.SetState(7),
            Asm.ReturnNothing(),
            Asm.At(notDouble, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(1)), notHeld)),
            Asm.Goto(release),
            Asm.At(notHeld, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(2)), press)),
            // release
            Asm.At(release, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(2)), shortRel)),
            io.SetState(0),                                                                // the press was used already
            Asm.ReturnNothing(),
            Asm.At(shortRel, io.SetState(usesDouble || usesShortLong ? 3 : 4)),                             // a short press: wait for a second one, or do it now
            io.ResetTimer(),
            Asm.ReturnNothing(),
            // press
            Asm.At(press, Asm.IfNot(Asm.Nat(Asm.EqF, io.State(), Asm.F(3)), single)),
            Asm.IfNot(Asm.Nat(Asm.LessEqF, io.Timer(), Asm.F(FreeCamSettings.DoublePressSeconds)), single),
            // a second press in time: a double at once, or (Short Then Long in use) held to see which it is: let go early =
            // double (state 7 on release), held for the long-press time = short then long (state 8, the frame)
            usesShortLong ? io.SetState(8) : io.SetState(6),
            .. (usesShortLong ? new[] { io.ResetTimer() } : []),
            Asm.ReturnNothing(),
            Asm.At(single, io.SetState(1)),
            io.ResetTimer(),
            Asm.ReturnNothing(),
            Asm.End(),
        ];
    }

    /// <summary>A rotator constant (EX_RotationConst: pitch, yaw, roll as int32).</summary>
    static Node RotConst(int pitch, int yaw, int roll)
    {
        var n = new Node(0x22);
        var b = new byte[12];
        BitConverter.GetBytes(pitch).CopyTo(b, 0); BitConverter.GetBytes(yaw).CopyTo(b, 4); BitConverter.GetBytes(roll).CopyTo(b, 8);
        n.Ops.Add(new OBytes(b));
        return n;
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

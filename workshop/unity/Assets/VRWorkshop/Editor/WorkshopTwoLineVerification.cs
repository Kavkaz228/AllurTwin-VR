using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace VRWorkshop.Editor
{
    // Run in a graphics-enabled batch Editor WITHOUT -quit. A real local server
    // and the rebuilt workshop scene are required. Only simulation is permitted.
    [InitializeOnLoad]
    public static class WorkshopTwoLineVerification
    {
        const string SessionKey = "VRWorkshop.TwoLineVerification";
        const string Endpoint = "ws://127.0.0.1:4182/ws";
        static readonly string[] RobotIds = { "training-arm", "training-arm-2", "paint-arm", "paint-arm-2" };
        enum Stage { EnterPlay, Connect, PreviewSelection, SelectPaint, AcquirePaint, ReleasePaint, FinishingReset, FinishingStart, ResetWeld, StartWeld, ResetPaint, StartPaint,
            TransferManualReset, TransferManualStart, WaitWeldOutput, TransferForward, TransferRelease, TransferHold, TransferReverse, TransferReverseRelease,
            TransferStopped, TransferAutoReset, TransferAutoStart, Cycle, StopWeld, PaintContinues, StopPaint, TransferFinalStop,
            FinishingFinalStop, FinishingHold, FinishingFinalReset, ReconnectWait, Reconnect, Finish }

        [Serializable]
        sealed class Report
        {
            public bool success, authenticated, fourSimulationRobots, twoConveyors, separateLineFrames;
            public bool paintManualAcquired, paintManualReleased, simultaneousProduction, distinctProductionIds;
            public bool bothFullCycles, bothStationCycles, selectedLineSwitchPreservesHeartbeats;
            public bool paintMaterialMatchesTelemetry;
            public bool paintingInitiallyEmpty, transferPresent, sameBodyInWeldingTransferPainting, identicalBodyInstance;
            public bool manualTransferForward, manualTransferReleased, manualTransferReverse, automaticTransferContinued;
            public bool transferStopped, transferredBodyCompletedPainting;
            public bool transferPreviewCaptured;
            public bool finishingPresent, sameBodyInFinishing, assemblyProgressObserved, inspectionProgressObserved, qualityPassed;
            public bool parkedVehicleRetained, finishingStopResetPreserved, reconnectPreservedSameEntity, reconnectPreservedServerEpoch;
            public bool assembledWheelsAndWindowsVisible;
            public bool assemblyPreviewCaptured, inspectionPreviewCaptured;
            public bool allurAnalyticsReceived, allurRobotMapping, allurQualityRegistered, allurPanelRendered;
            public float maximumFinishingPositionErrorMetres;
            public string serverSessionId;
            public int parkedSlot = -1;
            public string trackedBodyId;
            public string trackedBodyEntityId;
            public float maximumTransferPositionErrorMetres;
            public bool fourRobotSelector, fourRobotLabels;
            public string[] selectorRobotIds = new string[4];
            public bool weldingStopIsolated, paintingContinuedAfterWeldingStop, bothStopped;
            public bool[] robotMoved = new bool[4];
            public bool[] activeProcessSeen = new bool[4];
            public bool[] partialCoverageSeen = new bool[2];
            public bool[] fullCoverageSeen = new bool[2];
            public int[] completedBodies = new int[2];
            public int[] stationCycles = new int[4];
            public float[] maximumBodyPositionErrorMetres = new float[2];
            public int selectionSwitches, frames;
            public double elapsedSeconds;
            public string startedUtc, finishedUtc, failedStage, error, lastStatus;
            public string endpoint = Endpoint;
            public string scope = "Actual Unity Play Mode client and local WebSocket server; four simulation robots; welding -> manual/AUTO freight -> reverse painting -> assembly -> simulated QC -> parked vehicle with the same ID and GameObject; independent heartbeat sessions, measured progress, server-driven motion/coating; stop/reset and same-server reconnect preserve vehicles; actual scene camera renders.";
            public string unverified = "Physical VIVE controllers and headset, OpenXR tracking and stereo view, physical robots/cameras, thermal welding, paint fluid chemistry, AMAS/Siemens hardware integration.";
            public bool syntheticDesktopFocus = true, releasedVirtualKeyboard = true;
            public string[] renders = Array.Empty<string>();
        }

        [Serializable]
        sealed class State
        {
            public Stage stage;
            public double started, stageStarted, nextSelectionSwitch;
            public int firstFrame, stageFrame, previewSelectorStep;
            public bool inputSaved, previewOnly;
            public int backgroundBehavior, editorInputBehavior;
            public int[] initialCompleted = new int[2];
            public int[] initialCycles = new int[4];
            public float[] initialJoints = new float[24];
            public string[] productionIds = new string[2];
            public float stoppedWeldTravel;
            public float transferForwardPosition, transferReleasedPosition;
            public bool sawTransferBody, sawPaintBody;
            public string finishingId, stoppedFinishingParts;
            public Report report = new Report();
        }

        static State state;
        static WorkshopClient client;
        static Keyboard keyboard;
        static bool finishing;

        static WorkshopTwoLineVerification()
        {
            string saved = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            state = JsonUtility.FromJson<State>(saved);
            Subscribe();
        }

        public static void Run() => Begin(false);
        public static void RunPreview() => Begin(true);
        static void Begin(bool previewOnly)
        {
            if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run the two-line verification from batch edit mode without -quit.");
            state = new State { stage = Stage.EnterPlay, started = EditorApplication.timeSinceStartup, stageStarted = EditorApplication.timeSinceStartup };
            state.previewOnly = previewOnly;
            state.report.startedUtc = DateTime.UtcNow.ToString("O");
            if (previewOnly) state.report.scope = "Read-only actual Unity scene preview: authenticated local WebSocket, four simulation robot definitions/telemetry, actual WorkshopUI next-robot action cycles all four stations with matching labels, two server-driven conveyors and real scene camera renders. No robot lease, reset, motion or production command requested.";
            Save(); Subscribe();
            try
            {
                if (!File.Exists(WorkshopBuild.ScenePath)) throw new FileNotFoundException("Rebuild the workshop scene first.", WorkshopBuild.ScenePath);
                EditorSceneManager.OpenScene(WorkshopBuild.ScenePath, OpenSceneMode.Single);
                Check(UnityEngine.Object.FindFirstObjectByType<WorkshopClient>() != null, "Authored scene has no WorkshopClient; repair its script imports before Play Mode.");
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { Fail(error.Message); }
        }

        static void Subscribe() { EditorApplication.update -= Update; EditorApplication.update += Update; }
        static void Save() => SessionState.SetString(SessionKey, JsonUtility.ToJson(state));
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Next(Stage stage)
        {
            state.stage = stage; state.stageStarted = EditorApplication.timeSinceStartup; state.stageFrame = Time.frameCount;
            Save(); Debug.Log("[Two-line verification] " + stage);
        }
        static string LineId(int index) => index == 1 ? "painting" : "welding";
        static ProductionState Line(int index) => client.GetConveyor(index)?.State;
        static bool Fresh(int index) => client.GetConveyor(index)?.Fresh == true;
        static bool FreshAll()
        {
            if (!client.Ready || !Fresh(0) || !Fresh(1) || client.Transfer?.Fresh != true || client.Finishing?.Fresh != true) return false;
            for (int i = 0; i < 4; i++)
            {
                var telemetry = client.GetStationTelemetry(i);
                if (telemetry?.connected != true || telemetry.tool?.position == null || telemetry.joints?.Length != 6) return false;
            }
            return true;
        }
        static bool ClearLine(int index)
        {
            if (!Fresh(index) || Line(index).running) return false;
            for (int i = index * 2; i < index * 2 + 2; i++)
                if (client.GetStationTelemetry(i) == null || !string.IsNullOrEmpty(client.GetStationTelemetry(i).fault) ||
                    !string.IsNullOrEmpty(client.GetStationTelemetry(i).leaseOwner)) return false;
            return true;
        }
        static string SelectedProductionId(int index)
        {
            int previous = client.SelectedLineIndex;
            client.SelectProductionLine(index);
            string result = client.ProductionId;
            client.SelectProductionLine(previous);
            return result;
        }

        static void PrepareInput()
        {
            if (!state.inputSaved)
            {
                state.backgroundBehavior = (int)InputSystem.settings.backgroundBehavior;
                state.editorInputBehavior = (int)InputSystem.settings.editorInputBehaviorInPlayMode;
                state.inputSaved = true; Save();
            }
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (keyboard == null || !keyboard.added) keyboard = InputSystem.AddDevice<Keyboard>("TwoLineVerificationKeyboard");
            keyboard.MakeCurrent();
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            client.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
            client.Rig.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
            EventSystem.current?.SetSelectedGameObject(null);
        }

        static void Update()
        {
            if (state == null || finishing) return;
            EditorApplication.QueuePlayerLoopUpdate();
            try
            {
                double now = EditorApplication.timeSinceStartup, elapsed = now - state.stageStarted;
                if (state.stage == Stage.Finish) { if (elapsed > .8) Complete(); return; }
                double timeout = state.stage == Stage.Cycle ? 260 : state.stage == Stage.WaitWeldOutput ? 120 : 30;
                if (elapsed > timeout) throw new TimeoutException(state.stage + " timed out. " + client?.Status);
                if (!EditorApplication.isPlaying)
                {
                    Check(state.stage == Stage.EnterPlay, "Play mode ended before verification finished.");
                    return;
                }
                if (client == null) client = UnityEngine.Object.FindFirstObjectByType<WorkshopClient>();
                if (client?.Rig == null) return;
                PrepareInput();
                Check(!client.Rig.InXR, "This verification requires desktop fallback and cannot claim physical VR validation.");
                Check(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "Actual scene rendering requires a graphics device; omit -nographics.");
                if (state.report.fourSimulationRobots)
                    for (int i = 0; i < 4; i++)
                        if (client.GetStationTelemetry(i) != null) Check(client.GetStationTelemetry(i).mode == "simulation", "A robot changed out of simulation mode.");

                switch (state.stage)
                {
                    case Stage.EnterPlay:
                        state.firstFrame = Time.frameCount;
                        client.Connect(Endpoint, System.Environment.GetEnvironmentVariable("VR_ACCESS_TOKEN") ?? "local-training-only", "Unity two-line verification");
                        Next(Stage.Connect);
                        break;
                    case Stage.Connect:
                        if (!client.Ready || !FreshAll() || !client.AllurFresh) break;
                        ValidateSceneAndServer();
                        client.SelectRobot(0);
                        state.previewSelectorStep = 0;
                        AdvancePreviewSelection();
                        break;
                    case Stage.PreviewSelection:
                        int expected = (state.previewSelectorStep + 1) % 4;
                        Check(client.RobotId == RobotIds[expected], "Actual WorkshopUI.ChangeRobot did not cycle to " + RobotIds[expected] + "; selected " + client.RobotId + ".");
                        if (client.Telemetry?.robotId != RobotIds[expected] || client.TelemetryAge > client.telemetryTimeoutSeconds) break;
                        ValidatePreviewLabels(expected);
                        state.report.selectorRobotIds[state.previewSelectorStep] = client.RobotId;
                        state.previewSelectorStep++;
                        if (state.previewSelectorStep < 4) { AdvancePreviewSelection(); break; }
                        state.report.fourRobotSelector = state.report.fourRobotLabels = true;
                        if (state.previewOnly) { state.report.success = true; Next(Stage.Finish); }
                        else { client.SelectRobot(2); Next(Stage.SelectPaint); }
                        break;
                    case Stage.SelectPaint:
                        if (client.RobotId != "paint-arm" || client.Telemetry?.robotId != "paint-arm" || client.TelemetryAge > client.telemetryTimeoutSeconds || client.Rig.Deadman) break;
                        client.Acquire(); Next(Stage.AcquirePaint);
                        break;
                    case Stage.AcquirePaint:
                        if (!client.HasControl || client.Telemetry?.robotId != "paint-arm") break;
                        state.report.paintManualAcquired = true;
                        client.Release(); Next(Stage.ReleasePaint);
                        break;
                    case Stage.ReleasePaint:
                        if (client.HasControl || client.HasPendingManualControl || !FreshAll() || !string.IsNullOrEmpty(client.GetStationTelemetry(2).leaseOwner)) break;
                        state.report.paintManualReleased = true;
                        client.ResetFinishing(); Next(Stage.FinishingReset);
                        break;
                    case Stage.FinishingReset:
                        if (!FreshAll() || client.Finishing.State.running || client.Finishing.State.fault) break;
                        client.StartFinishing(); Next(Stage.FinishingStart);
                        break;
                    case Stage.FinishingStart:
                        if (!FreshAll() || !client.HasFinishingControl || !client.Finishing.State.running) break;
                        state.finishingId = client.FinishingId;
                        client.SelectProductionLine(0); client.ResetProduction(); Next(Stage.ResetWeld);
                        break;
                    case Stage.ResetWeld:
                        if (!ClearLine(0) || !client.Status.StartsWith("Сброс линии выполнен.", StringComparison.Ordinal)) break;
                        client.StartProduction(); Next(Stage.StartWeld);
                        break;
                    case Stage.StartWeld:
                        if (!Fresh(0) || Line(0).running != true || string.IsNullOrEmpty(SelectedProductionId(0))) break;
                        state.productionIds[0] = SelectedProductionId(0);
                        client.SelectProductionLine(1); client.ResetProduction(); Next(Stage.ResetPaint);
                        break;
                    case Stage.ResetPaint:
                        if (!ClearLine(1) || !client.Status.StartsWith("Сброс линии выполнен.", StringComparison.Ordinal)) break;
                        Check(Line(0).running, "Resetting painting stopped welding.");
                        client.StartProduction(); Next(Stage.StartPaint);
                        break;
                    case Stage.StartPaint:
                        if (!FreshAll() || !Line(1).running || string.IsNullOrEmpty(SelectedProductionId(1))) break;
                        state.productionIds[1] = SelectedProductionId(1);
                        Check(Line(0).running && state.productionIds[0] != state.productionIds[1], "Independent production sessions were not established.");
                        state.report.simultaneousProduction = state.report.distinctProductionIds = true;
                        client.SelectRobot(3); // Real selection/subscription path while both AUTO sessions run.
                        client.SetTransferMode("manual"); client.ResetTransfer();
                        Next(Stage.TransferManualReset);
                        break;
                    case Stage.TransferManualReset:
                        if (!FreshAll() || client.Transfer.State.mode != "manual" || client.Transfer.State.fault || client.Transfer.State.running) break;
                        client.StartTransfer(); Next(Stage.TransferManualStart);
                        break;
                    case Stage.TransferManualStart:
                        if (!FreshAll() || !client.HasTransferControl || !client.Transfer.State.running || client.Transfer.State.mode != "manual") break;
                        Check(!client.Transfer.State.moving && !client.Transfer.State.deadman, "MANUAL start moved without a held jog command.");
                        Next(Stage.WaitWeldOutput);
                        break;
                    case Stage.WaitWeldOutput:
                        if (!FreshAll()) break;
                        ObserveProduction(); ObserveBodyIdentity();
                        Check(client.Transfer.State.parts.Length == 0, "MANUAL transfer loaded a body before a held command.");
                        var atOutput = Array.Find(Line(0).parts, part => part.id == state.report.trackedBodyId && part.processed == 2 && part.position >= Line(0).length - .001f);
                        if (atOutput == null) break;
                        client.BeginTransferJog(1); Next(Stage.TransferForward);
                        break;
                    case Stage.TransferForward:
                        if (!FreshAll()) break;
                        ObserveProduction(); ObserveBodyIdentity(); ObserveTransferPosition();
                        var forwardBody = TransferBody();
                        if (forwardBody == null || forwardBody.position < .35f) break;
                        state.report.manualTransferForward = true;
                        state.transferForwardPosition = forwardBody.position;
                        client.EndTransferJog(); Next(Stage.TransferRelease);
                        break;
                    case Stage.TransferRelease:
                        if (!FreshAll() || client.Transfer.State.moving || client.Transfer.State.deadman) break;
                        Check(TransferBody() != null, "Released transfer lost its body.");
                        state.transferReleasedPosition = TransferBody().position;
                        Next(Stage.TransferHold);
                        break;
                    case Stage.TransferHold:
                        if (!FreshAll()) break;
                        Check(TransferBody() != null && Mathf.Abs(TransferBody().position - state.transferReleasedPosition) < .0001f, "Body drifted after manual jog release.");
                        if (elapsed < .5) break;
                        state.report.manualTransferReleased = true;
                        client.BeginTransferJog(-1); Next(Stage.TransferReverse);
                        break;
                    case Stage.TransferReverse:
                        if (!FreshAll()) break;
                        ObserveProduction(); ObserveBodyIdentity(); ObserveTransferPosition();
                        Check(TransferBody() != null, "Reverse transfer lost its body.");
                        if (TransferBody().position > state.transferForwardPosition - .15f) break;
                        state.report.manualTransferReverse = true;
                        client.EndTransferJog(); Next(Stage.TransferReverseRelease);
                        break;
                    case Stage.TransferReverseRelease:
                        if (!FreshAll() || client.Transfer.State.moving || client.Transfer.State.deadman) break;
                        state.transferReleasedPosition = TransferBody().position;
                        client.StopTransfer(); Next(Stage.TransferStopped);
                        break;
                    case Stage.TransferStopped:
                        if (!FreshAll() || client.Transfer.State.running || client.HasTransferControl) break;
                        Check(Line(0).running && Line(1).running, "Stopping freight transfer stopped a production line.");
                        Check(Mathf.Abs(TransferBody().position - state.transferReleasedPosition) < .0001f, "Freight stop changed body position.");
                        client.SetTransferMode("auto"); client.ResetTransfer(); Next(Stage.TransferAutoReset);
                        break;
                    case Stage.TransferAutoReset:
                        if (!FreshAll() || client.Transfer.State.mode != "auto" || client.Transfer.State.fault || client.Transfer.State.running) break;
                        Check(TransferBody() != null && Mathf.Abs(TransferBody().position - state.transferReleasedPosition) < .0001f, "Freight reset cleared or moved the existing body.");
                        client.StartTransfer(); Next(Stage.TransferAutoStart);
                        break;
                    case Stage.TransferAutoStart:
                        if (!FreshAll() || !client.HasTransferControl || !client.Transfer.State.running || client.Transfer.State.mode != "auto") break;
                        state.nextSelectionSwitch = now + 1;
                        Next(Stage.Cycle);
                        break;
                    case Stage.Cycle:
                        if (!FreshAll()) break;
                        ObserveProduction(); ObserveBodyIdentity(); ObserveTransferPosition(); ObserveFinishing();
                        Check(client.Transfer.State.running && client.HasTransferControl, "Automatic freight transport lost its session: " + client.Transfer.State.stopReason);
                        Check(client.Finishing.State.running && client.HasFinishingControl && client.FinishingId == state.finishingId,
                            "Finishing lost its independent heartbeat session: " + client.Finishing.State.stopReason);
                        if (now >= state.nextSelectionSwitch)
                        {
                            client.SelectProductionLine(1 - client.SelectedLineIndex);
                            state.nextSelectionSwitch = now + 1; state.report.selectionSwitches++;
                        }
                        if (!CyclesComplete() || !state.report.parkedVehicleRetained) break;
                        Check(Array.TrueForAll(state.report.robotMoved, value => value), "Some robot never moved during its production cycle.");
                        Check(Array.TrueForAll(state.report.activeProcessSeen, value => value), "A process station never reported active welding/painting.");
                        Check(Array.TrueForAll(state.report.partialCoverageSeen, value => value) && Array.TrueForAll(state.report.fullCoverageSeen, value => value), "No measured progression from partial to complete welding/paint coverage.");
                        Check(state.report.paintMaterialMatchesTelemetry, "Paint coverage was not applied to the rendered body material.");
                        Check(state.sawTransferBody && state.sawPaintBody && state.report.identicalBodyInstance, "One body did not preserve its visual identity through all three stages.");
                        Check(Array.IndexOf(Line(1).completedIds ?? Array.Empty<string>(), state.report.trackedBodyId) >= 0, "The tracked welded body never finished painting.");
                        Check(state.report.sameBodyInFinishing && state.report.assemblyProgressObserved && state.report.inspectionProgressObserved && state.report.qualityPassed,
                            "The same body did not complete measured assembly/QC before parking.");
                        state.report.sameBodyInWeldingTransferPainting = state.report.transferredBodyCompletedPainting = state.report.automaticTransferContinued = true;
                        state.report.bothFullCycles = state.report.bothStationCycles = true;
                        state.report.selectedLineSwitchPreservesHeartbeats = elapsed > 3 && state.report.selectionSwitches >= 2;
                        Check(state.report.selectedLineSwitchPreservesHeartbeats, "Line selection did not exercise both heartbeat sessions over their timeout.");
                        client.SelectProductionLine(0); client.StopProduction(); Next(Stage.StopWeld);
                        break;
                    case Stage.StopWeld:
                        if (!FreshAll() || Line(0).running || !string.IsNullOrEmpty(SelectedProductionId(0))) break;
                        Check(Line(1).running && SelectedProductionId(1) == state.productionIds[1], "Stopping welding revoked painting AUTO.");
                        state.report.weldingStopIsolated = true;
                        state.stoppedWeldTravel = Line(0).conveyorPosition;
                        client.SelectProductionLine(1); Next(Stage.PaintContinues);
                        break;
                    case Stage.PaintContinues:
                        if (!FreshAll()) break;
                        Check(!Line(0).running && Mathf.Abs(Line(0).conveyorPosition - state.stoppedWeldTravel) < .0001f, "Stopped welding conveyor continued to advance.");
                        Check(Line(1).running && SelectedProductionId(1) == state.productionIds[1], "Painting did not retain its heartbeat session after welding stopped.");
                        if (elapsed < 3) break;
                        state.report.paintingContinuedAfterWeldingStop = true;
                        client.StopProduction(); Next(Stage.StopPaint);
                        break;
                    case Stage.StopPaint:
                        if (!FreshAll() || Line(0).running || Line(1).running || !string.IsNullOrEmpty(SelectedProductionId(1))) break;
                        for (int i = 0; i < 4; i++) Check(!client.GetStationTelemetry(i).moving, "A stopped line still has a moving robot.");
                        state.report.bothStopped = true;
                        client.StopTransfer(); Next(Stage.TransferFinalStop);
                        break;
                    case Stage.TransferFinalStop:
                        if (!FreshAll() || client.Transfer.State.running || client.HasTransferControl) break;
                        state.report.transferStopped = true;
                        client.StopFinishing(); Next(Stage.FinishingFinalStop);
                        break;
                    case Stage.FinishingFinalStop:
                        if (!FreshAll() || client.Finishing.State.running || client.HasFinishingControl) break;
                        ObserveBodyIdentity(); ObserveFinishing();
                        state.stoppedFinishingParts = FinishingPartsJson();
                        Next(Stage.FinishingHold);
                        break;
                    case Stage.FinishingHold:
                        if (!FreshAll()) break;
                        Check(FinishingPartsJson() == state.stoppedFinishingParts, "Finishing parts moved or changed after stop.");
                        if (elapsed < .6) break;
                        client.ResetFinishing(); Next(Stage.FinishingFinalReset);
                        break;
                    case Stage.FinishingFinalReset:
                        if (!FreshAll() || client.Finishing.State.fault || elapsed < .5) break;
                        Check(!client.Finishing.State.running && !client.HasFinishingControl, "Finishing reset restarted motion.");
                        Check(FinishingPartsJson() == state.stoppedFinishingParts, "Finishing reset deleted, moved or changed a vehicle.");
                        state.report.finishingStopResetPreserved = true;
                        client.Disconnect(); Next(Stage.ReconnectWait);
                        break;
                    case Stage.ReconnectWait:
                        ObserveBodyIdentity();
                        if (elapsed < .5) break;
                        client.Connect(Endpoint, System.Environment.GetEnvironmentVariable("VR_ACCESS_TOKEN") ?? "local-training-only", "Unity finishing reconnect verification"); Next(Stage.Reconnect);
                        break;
                    case Stage.Reconnect:
                        if (!FreshAll()) break;
                        if (!client.AllurFresh || client.Allur.readyVehicles < 1 || client.Allur.qualityPassed < 1) break;
                        state.report.allurQualityRegistered = true;
                        Check(ServerEpoch() == state.report.serverSessionId, "Reconnect unexpectedly replaced the simulation server epoch.");
                        Check(FinishingPartsJson() == state.stoppedFinishingParts, "Same-server reconnect lost a vehicle or its measured state.");
                        Check(!client.Finishing.State.running && !client.HasFinishingControl, "Reconnect resumed finishing without operator start.");
                        ObserveBodyIdentity(); ObserveFinishing();
                        Check(client.GetConveyor(0).TryGetBody(state.report.trackedBodyId, out GameObject retained, out int retainedStage) && retainedStage == 3 &&
                            retained.GetEntityId().ToString() == state.report.trackedBodyEntityId, "Reconnect recreated or removed the finished car GameObject.");
                        state.report.reconnectPreservedSameEntity = state.report.reconnectPreservedServerEpoch = true;
                        state.report.success = true;
                        Next(Stage.Finish);
                        break;
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }

        static void ValidateSceneAndServer()
        {
            Check(client.AllurFresh && client.Allur.robotInsights?.Length == 4, "AllurTwin analytics is unavailable or does not contain all four robots.");
            foreach (var id in RobotIds) Check(Array.Exists(client.Allur.robotInsights, insight => insight.robotId == id), "AllurTwin robot mapping missing: " + id);
            state.report.allurAnalyticsReceived = state.report.allurRobotMapping = true;
            Check(client.Robots.Length == 4, "The server must advertise exactly four simulation robots.");
            for (int i = 0; i < 4; i++)
            {
                Check(client.Robots[i].id == RobotIds[i] && client.Robots[i].mode == "simulation", "Unexpected robot definition; no control commands sent.");
                var telemetry = client.GetStationTelemetry(i);
                Check(telemetry.mode == "simulation", "Telemetry must represent simulation.");
                if (!state.previewOnly)
                    Check(!telemetry.moving && string.IsNullOrEmpty(telemetry.leaseOwner), "Robot already moving or leased; verification will not take over.");
                Check(telemetry.production?.lineId == LineId(i / 2), "Robot telemetry is associated with the wrong production line.");
                Check(client.GetStationView(i) != null && client.Environment.GetRobotBase(i) != null, "A rendered robot or its frame is missing.");
                Array.Copy(telemetry.joints, 0, state.initialJoints, i * 6, 6);
                var station = Array.Find(Line(i / 2).stations, item => item.robotId == RobotIds[i]);
                Check(station != null, "No production station for " + RobotIds[i]);
                state.initialCycles[i] = station.cycles;
            }
            for (int i = 0; i < 2; i++)
            {
                Check(client.GetConveyor(i) != null && client.GetConveyor(i).PanelCount == Line(i).parts.Length, "Server-driven conveyor body count does not match telemetry.");
                Check(Line(i).lineId == LineId(i) && Line(i).available && Line(i).stations?.Length == 2, "Line configuration is unavailable.");
                if (!state.previewOnly) Check(!Line(i).running, "Line already running; verification will not take over.");
                state.initialCompleted[i] = Line(i).completed;
            }
            Check(Vector3.Distance(client.Environment.GetConveyorOrigin(0).position, client.Environment.GetConveyorOrigin(1).position) > 6,
                "Two conveyor frames overlap.");
            Check(Mathf.Abs(Line(0).length - 11) < .001f && Mathf.Abs(Line(1).length - 11) < .001f, "Both work lines must provide the extended eleven-metre belt.");
            Check(Line(1).stations[0].robotId == "paint-arm-2" && Line(1).stations[1].robotId == "paint-arm" &&
                Mathf.Abs(Line(1).stations[0].position - 5) < .001f && Mathf.Abs(Line(1).stations[1].position - 9) < .001f,
                "Painting robot order/positions must follow its reversed flow.");
            Check(client.Transfer != null && client.Transfer.State.available && client.Transfer.State.capacity == 1 && client.Transfer.State.path?.Length == 2,
                "The bounded freight transfer route is missing.");
            Vector3 sourceExit = client.Environment.GetConveyorOrigin(0).position + Vector3.up * .06f + Vector3.forward * Line(0).length;
            Vector3 paintEntry = client.Environment.GetConveyorOrigin(1).position + Vector3.up * .06f;
            var route = client.Transfer.State.path;
            Check(Vector3.Distance(sourceExit, new Vector3(route[0].x, route[0].y, route[0].z)) < .01f &&
                Vector3.Distance(paintEntry, new Vector3(route[1].x, route[1].y, route[1].z)) < .01f,
                "Freight endpoints do not physically join the welding output and painting input.");
            state.report.transferPresent = true;
            Check(client.Finishing?.State != null && client.Finishing.State.available && client.Finishing.State.capacity == 1 &&
                client.Finishing.State.parkingCapacity == 4 && client.Finishing.State.slots?.Length == 4,
                "The assembly/QC work bay and four bounded parking places are missing.");
            for (int index = 0; index < 4; index++)
            {
                var slot = client.Finishing.State.slots[index];
                Check(slot.index == index && slot.positionWorld != null && Mathf.Abs(slot.positionWorld.z + 19) < .001f &&
                    Mathf.Abs(slot.positionWorld.y - .24f) < .001f, "A parking slot does not match the clear floor route.");
            }
            state.report.serverSessionId = ServerEpoch();
            Check(!string.IsNullOrEmpty(state.report.serverSessionId), "Server epoch is required to preserve vehicles on reconnect.");
            state.report.finishingPresent = true;
            if (!state.previewOnly)
            {
                Check(!client.Transfer.State.running && client.Transfer.State.parts.Length == 0 && Line(1).parts.Length == 0,
                    "Full transfer verification requires a fresh server with empty freight and painting inputs.");
                Check(!client.Finishing.State.running && client.Finishing.State.parts.Length == 0,
                    "Full production verification requires a fresh server with an empty assembly/QC/parking area.");
                Check(Line(0).parts.Length > 0, "Welding has no incoming body to trace.");
                state.report.paintingInitiallyEmpty = true;
                state.report.trackedBodyId = Line(0).parts[0].id;
                Check(client.GetConveyor(0).TryGetBody(state.report.trackedBodyId, out GameObject body, out int ownerStage) && ownerStage == 0,
                    "Initial welded-line body is missing from the shared visual registry.");
                state.report.trackedBodyEntityId = body.GetEntityId().ToString();
            }
            state.report.authenticated = state.report.fourSimulationRobots = state.report.twoConveyors = state.report.separateLineFrames = true;
            Save();
        }

        static void ObserveProduction()
        {
            for (int line = 0; line < 2; line++)
            {
                var snapshot = Line(line);
                Check(snapshot.running, LineId(line) + " stopped early: " + snapshot.stopReason);
                Check(SelectedProductionId(line) == state.productionIds[line], "AUTO session changed during line selection.");
                state.report.completedBodies[line] = snapshot.completed - state.initialCompleted[line];
                foreach (var part in snapshot.parts)
                {
                    float coverage = line == 1 ? part.coating : part.weldProgress;
                    Check(float.IsFinite(coverage) && coverage >= 0 && coverage <= 1, "Invalid process coverage in server telemetry.");
                    if (coverage > .001f && coverage < .999f) state.report.partialCoverageSeen[line] = true;
                    if (coverage >= .999f) state.report.fullCoverageSeen[line] = true;
                    if (line == 1 && coverage > .01f && coverage < .99f && !state.report.paintMaterialMatchesTelemetry)
                    {
                        Check(client.GetConveyor(0).TryGetBody(part.id, out GameObject body, out int ownerStage) && ownerStage == 2,
                            "The painted body has no rendered scene object.");
                        var skin = Array.Find(body.GetComponentsInChildren<Renderer>(), renderer => renderer.name.StartsWith("Stamped automotive shell", StringComparison.Ordinal));
                        Check(skin != null && skin.sharedMaterial.shader.name == "VRWorkshop/BodyCoating" && skin.sharedMaterial.shader.isSupported,
                            "The live painted body has no supported coating shader.");
                        var properties = new MaterialPropertyBlock(); skin.GetPropertyBlock(properties);
                        Check(Mathf.Abs(properties.GetFloat("_Coating") - coverage) < .0001f, "Rendered paint amount diverges from authoritative telemetry.");
                        state.report.paintMaterialMatchesTelemetry = true;
                    }
                    if (client.GetConveyor(line).TryGetPanelPosition(part.id, out Vector3 rendered))
                    {
                        Vector3 expected = client.Environment.GetConveyorOrigin(line).position + Vector3.up * .06f + (line == 1 ? Vector3.back : Vector3.forward) * part.position;
                        float error = Vector3.Distance(rendered, expected);
                        state.report.maximumBodyPositionErrorMetres[line] = Mathf.Max(state.report.maximumBodyPositionErrorMetres[line], error);
                        Check(error < .16f, LineId(line) + " visible body diverged from the server by " + error + "m.");
                    }
                }
                for (int local = 0; local < 2; local++)
                {
                    var station = snapshot.stations[local];
                    int index = Array.IndexOf(RobotIds, station.robotId);
                    Check(index >= 0 && index / 2 == line, "Process station points to the wrong robot.");
                    state.report.stationCycles[index] = station.cycles - state.initialCycles[index];
                    if (station.active) state.report.activeProcessSeen[index] = true;
                    var telemetry = client.GetStationTelemetry(index);
                    Check(string.IsNullOrEmpty(telemetry.fault), "Robot fault during AUTO: " + telemetry.fault);
                    for (int joint = 0; joint < 6; joint++)
                        if (Mathf.Abs(telemetry.joints[joint] - state.initialJoints[index * 6 + joint]) > .01f) state.report.robotMoved[index] = true;
                }
            }
        }
        static bool CyclesComplete() => Array.TrueForAll(state.report.completedBodies, value => value >= 1) && Array.TrueForAll(state.report.stationCycles, value => value >= 1);

        static ProductionPart TransferBody() => Array.Find(client.Transfer.State.parts, part => part.id == state.report.trackedBodyId);
        static string ServerEpoch() => typeof(WorkshopClient).GetField("serverSessionId", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client) as string;
        static string FinishingPartsJson() => JsonUtility.ToJson(new FinishingState { parts = client.Finishing.State.parts, slots = client.Finishing.State.slots });
        static void ObserveBodyIdentity()
        {
            if (!client.GetConveyor(0).TryGetBody(state.report.trackedBodyId, out GameObject body, out int stage)) return;
            Check(body.GetEntityId().ToString() == state.report.trackedBodyEntityId, "The physical scene body was destroyed/recreated during conveyor handoff.");
            if (stage == 1) state.sawTransferBody = true;
            if (stage == 2) state.sawPaintBody = true;
            if (stage == 3) state.report.sameBodyInFinishing = true;
            state.report.identicalBodyInstance = state.sawTransferBody && state.sawPaintBody;
        }
        static void ObserveFinishing()
        {
            var snapshot = client.Finishing.State;
            Check(snapshot.parts.Length <= 4, "Finishing exceeded its reserved parking capacity.");
            var vehicle = Array.Find(snapshot.parts, part => part.id == state.report.trackedBodyId);
            if (vehicle == null) return;
            Check(vehicle.weldProgress >= .999f && vehicle.coating >= .999f, "Finishing accepted an incompletely welded or coated body.");
            Check(vehicle.assemblyProgress >= 0 && vehicle.assemblyProgress <= 1 && vehicle.inspectionProgress >= 0 && vehicle.inspectionProgress <= 1,
                "Invalid finishing progress in server telemetry.");
            if (vehicle.assemblyProgress > .001f && vehicle.assemblyProgress < .999f) state.report.assemblyProgressObserved = true;
            if (vehicle.inspectionProgress > .001f && vehicle.inspectionProgress < .999f) state.report.inspectionProgressObserved = true;
            if (!client.GetConveyor(0).TryGetBody(vehicle.id, out GameObject body, out int ownerStage) || ownerStage != 3) return;
            Check(body.GetEntityId().ToString() == state.report.trackedBodyEntityId, "Assembly/QC replaced the original scene body.");
            var rigidbody = body.GetComponent<Rigidbody>();
            Check(rigidbody != null && vehicle.positionWorld != null, "Finishing body lacks its physical root or authoritative world position.");
            Vector3 expected = new Vector3(vehicle.positionWorld.x, vehicle.positionWorld.y, vehicle.positionWorld.z);
            float error = Vector3.Distance(rigidbody.position, expected);
            state.report.maximumFinishingPositionErrorMetres = Mathf.Max(state.report.maximumFinishingPositionErrorMetres, error);
            Check(error < .16f, "Finishing car diverged from server world position by " + error + "m.");
            if (!state.report.assemblyPreviewCaptured && vehicle.status == "assembling" && vehicle.assemblyProgress >= .65f)
            {
                Capture("assembly-workstation.png", new Vector3(-10.8f, 2.6f, -9.5f), new Vector3(-8, 1.6f, -7), 60, 1280, 800);
                state.report.assemblyPreviewCaptured = true;
            }
            if (!state.report.inspectionPreviewCaptured && vehicle.status == "inspecting" && vehicle.inspectionProgress >= .4f)
            {
                Capture("quality-inspection.png", new Vector3(-10.8f, 2.6f, -13.5f), new Vector3(-8, 1.6f, -11.5f), 60, 1280, 800);
                state.report.inspectionPreviewCaptured = true;
            }
            if (vehicle.status != "parked") { Check(!vehicle.ready, "A car became ready before parking and QC finished."); return; }
            Check(vehicle.ready && vehicle.quality == "passed" && vehicle.weldPassed && vehicle.paintPassed && vehicle.assemblyPassed,
                "A parked vehicle has not passed the measured simulation checks.");
            Check(vehicle.assemblyProgress >= .999f && vehicle.inspectionProgress >= .999f && vehicle.qualityChecks?.Length == 3,
                "Parked vehicle lacks assembly/inspection completion and three quality measurements.");
            foreach (var check in vehicle.qualityChecks)
                Check(check.passed && check.measured >= check.required - .0001f, "A failed QC measurement was reported as ready.");
            var assembled = body.GetComponent<FinishedVehicleView>();
            Check(assembled != null && assembled.AssemblyProgress >= .999f && assembled.WheelCount == 4 && assembled.WindowsCount == 6,
                "The retained finished car lacks the actual assembled wheels/glazing.");
            foreach (var wheel in assembled.Wheels) Check(wheel != null && wheel.gameObject.activeInHierarchy, "A finished wheel is not visible in the scene.");
            foreach (var window in assembled.Windows) Check(window != null && window.gameObject.activeInHierarchy, "Finished glazing is not visible in the scene.");
            state.report.assembledWheelsAndWindowsVisible = true;
            Check(vehicle.slotIndex >= 0 && vehicle.slotIndex < 4, "Parked vehicle has no reserved slot.");
            var slot = snapshot.slots[vehicle.slotIndex];
            Check(slot.occupied && slot.reserved && slot.partId == vehicle.id, "Parking slot ownership does not match the retained vehicle.");
            Check(Vector3.Distance(expected, new Vector3(slot.positionWorld.x, slot.positionWorld.y, slot.positionWorld.z)) < .001f,
                "Finished car is not at its authoritative parking position.");
            state.report.parkedSlot = vehicle.slotIndex;
            state.report.qualityPassed = state.report.parkedVehicleRetained = true;
        }
        static void ObserveTransferPosition()
        {
            var snapshot = client.Transfer.State;
            var start = snapshot.path[0]; var end = snapshot.path[snapshot.path.Length - 1];
            foreach (var part in snapshot.parts)
            {
                Check(part.weldProgress >= .999f && part.coating <= .001f, "Freight received an unwelded or prematurely painted body.");
                if (!client.Transfer.TryGetPanelPosition(part.id, out Vector3 rendered)) continue;
                Vector3 expected = Vector3.Lerp(new Vector3(start.x, start.y, start.z), new Vector3(end.x, end.y, end.z), part.position / snapshot.length);
                float error = Vector3.Distance(rendered, expected);
                state.report.maximumTransferPositionErrorMetres = Mathf.Max(state.report.maximumTransferPositionErrorMetres, error);
                Check(error < .16f, "Freight body diverged from its authoritative route by " + error + "m.");
                if (!state.report.transferPreviewCaptured && part.id == state.report.trackedBodyId && part.position >= 3.5f && part.position <= 4.5f)
                {
                    Capture("freight-transfer.png", new Vector3(-4, 4.7f, 5.4f), new Vector3(-4, 1.2f, 9), 65, 1280, 800);
                    state.report.transferPreviewCaptured = true;
                }
            }
        }

        static void AdvancePreviewSelection()
        {
            var ui = UnityEngine.Object.FindFirstObjectByType<WorkshopUI>();
            var method = typeof(WorkshopUI).GetMethod("ChangeRobot", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(ui != null && method != null, "Actual UI robot selector is missing.");
            Check(!client.HasControl && !client.HasPendingManualControl, "Read-only preview must not hold manual control.");
            // Exercise the exact callback used by the next-robot UI button. The
            // scene selection/subscription and telemetry refresh stay unmocked.
            method.Invoke(ui, new object[] { 1 });
            Next(Stage.PreviewSelection);
        }
        static void ValidatePreviewLabels(int expected)
        {
            var ui = UnityEngine.Object.FindFirstObjectByType<WorkshopUI>();
            Text ReadLabel(string field) => typeof(WorkshopUI).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ui) as Text;
            var header = ReadLabel("robotLabel");
            var selected = ReadLabel("selectedName");
            Check(header != null && selected != null, "Robot selector/SCADA labels are missing.");
            string name = client.Robots[expected].name;
            Check(selected.text == name, "Selected SCADA name does not match " + client.RobotId + ": " + selected.text);
            Check(header.text == name + "  /  " + (expected + 1) + " из 4", "Robot selector must display the matching position out of four: " + header.text);
        }

        static void Fail(string error)
        {
            if (state == null || state.stage == Stage.Finish) return;
            state.report.success = false; state.report.error = error; state.report.failedStage = state.stage.ToString();
            Debug.LogError("[Two-line verification] " + error);
            try
            {
                if (client != null)
                {
                    if (state.report.fourSimulationRobots && client.HasControl) { client.Stop(); client.Release(); }
                    client.Disconnect();
                }
            }
            catch (Exception cleanup) { state.report.error += " Cleanup: " + cleanup.Message; }
            Next(Stage.Finish);
        }

        static void Complete()
        {
            finishing = true;
            try
            {
                if (state.report.success)
                {
                    state.report.renders = state.previewOnly
                        ? new[] { "two-line-factory.png", "welding-line.png", "painting-line.png", "freight-overview.png", "finishing-overview.png" }
                        : new[] { "two-line-factory.png", "welding-line.png", "painting-line.png", "freight-transfer.png", "finishing-overview.png", "assembly-workstation.png", "quality-inspection.png", "parked-vehicle.png" };
                    // Keep the overview under the real ceiling and trusses.
                    Capture(state.report.renders[0], new Vector3(-4, 4.3f, -6.5f), new Vector3(-4, 1.2f, 2), 85);
                    Capture(state.report.renders[1], new Vector3(-2.8f, 2.5f, -3.4f), new Vector3(0, 1.4f, 1), 59);
                    Capture(state.report.renders[2], new Vector3(-9.9f, 2.4f, -3.7f), new Vector3(-8, 1.5f, 1), 59);
                    if (!state.report.transferPreviewCaptured)
                        Capture(state.report.renders[3], new Vector3(-4, 4.7f, 5.4f), new Vector3(-4, 1.2f, 9), 65);
                    Capture("finishing-overview.png", new Vector3(-2, 4.5f, -21.5f), new Vector3(-7.5f, 1, -13), 75);
                    var parked = Array.Find(client.Finishing?.State?.parts ?? Array.Empty<FinishingVehicle>(), vehicle => vehicle.status == "parked");
                    int parkingSlot = state.previewOnly ? parked?.slotIndex ?? -1 : state.report.parkedSlot;
                    if (parkingSlot >= 0)
                    {
                        var slot = client.Finishing.State.slots[parkingSlot].positionWorld;
                        Capture("parked-vehicle.png", new Vector3(slot.x - 2.7f, 2, slot.z - 2.8f), new Vector3(slot.x, 1, slot.z), 65);
                    }
                    CaptureAllurPanel();
                    state.report.allurPanelRendered = true;
                    var renders = state.report.renders; Array.Resize(ref renders, renders.Length + 1);
                    renders[renders.Length - 1] = "allurtwin-vr-analytics.png"; state.report.renders = renders;
                }
            }
            catch (Exception error) { state.report.success = false; state.report.error = "Scene capture: " + error; Debug.LogException(error); }
            finally
            {
                state.report.lastStatus = client?.Status;
                state.report.finishedUtc = DateTime.UtcNow.ToString("O");
                state.report.elapsedSeconds = EditorApplication.timeSinceStartup - state.started;
                state.report.frames = Math.Max(0, Time.frameCount - state.firstFrame);
                client?.Disconnect();
                if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
                if (state.inputSaved)
                {
                    InputSystem.settings.backgroundBehavior = (InputSettings.BackgroundBehavior)state.backgroundBehavior;
                    InputSystem.settings.editorInputBehaviorInPlayMode = (InputSettings.EditorInputBehaviorInPlayMode)state.editorInputBehavior;
                }
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, state.previewOnly ? "../Logs/two-line-preview.json" : "../Logs/two-line-verification.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(state.report, true));
                Debug.Log("[Two-line verification] Report: " + path);
                SessionState.EraseString(SessionKey); EditorApplication.update -= Update;
                EditorApplication.Exit(state.report.success ? 0 : 1);
            }
        }

        static void CaptureAllurPanel()
        {
            var ui = UnityEngine.Object.FindFirstObjectByType<WorkshopUI>();
            Check(ui != null, "Operator UI is missing.");
            ui.ShowAllurPanel();
            var condition = (Text)typeof(WorkshopUI).GetField("allurCondition", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui);
            Check(client.Allur?.robotInsights != null && client.Allur.robotInsights.Length == 4, "Allur snapshot lost its robot insight array before capture.");
            var insight = Array.Find(client.Allur.robotInsights, item => item.robotId == client.RobotId);
            Check(insight != null && condition.text.Contains(insight.condition), "Allur panel does not show the selected robot's returned insight.");
            var canvas = ui.GetComponentInChildren<Canvas>();
            var oldMode = canvas.renderMode; var oldCamera = canvas.worldCamera;
            var oldPosition = canvas.transform.position; var oldRotation = canvas.transform.rotation; var oldScale = canvas.transform.localScale;
            var canvasRect = (RectTransform)canvas.transform;
            var content = (RectTransform)typeof(WorkshopUI).GetField("panelRoot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui);
            var oldCanvasSize = canvasRect.sizeDelta; var oldCanvasPivot = canvasRect.pivot;
            var oldContentSize = content.sizeDelta; var oldContentPosition = content.anchoredPosition;
            var oldFactor = canvas.scaleFactor;
            var capture = new GameObject("Allur analytics capture camera").AddComponent<Camera>();
            var target = new RenderTexture(1600, 900, 24); target.Create();
            var previous = RenderTexture.active;
            try
            {
                canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = capture;
                canvas.scaleFactor = 1;
                canvasRect.pivot = new Vector2(.5f, .5f); canvasRect.sizeDelta = new Vector2(1600, 900);
                content.sizeDelta = new Vector2(1600, 900); content.anchoredPosition = Vector2.zero;
                canvas.transform.SetPositionAndRotation(new Vector3(100, 100, 100), Quaternion.identity);
                canvas.transform.localScale = Vector3.one * .001f;
                capture.transform.SetPositionAndRotation(canvas.transform.position - Vector3.forward * 2, Quaternion.identity);
                capture.orthographic = true; capture.orthographicSize = .46f; capture.nearClipPlane = .1f; capture.farClipPlane = 4;
                capture.cullingMask = 1 << 5; capture.clearFlags = CameraClearFlags.SolidColor; capture.backgroundColor = new Color(.025f, .045f, .065f);
                capture.targetTexture = target; Canvas.ForceUpdateCanvases(); capture.Render(); RenderTexture.active = target;
                var pixels = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); pixels.Apply();
                File.WriteAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/allurtwin-vr-analytics.png")), pixels.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(pixels);
            }
            finally
            {
                RenderTexture.active = previous; capture.targetTexture = null; target.Release();
                UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(capture.gameObject);
                canvas.renderMode = oldMode; canvas.worldCamera = oldCamera;
                canvas.scaleFactor = oldFactor; canvasRect.pivot = oldCanvasPivot; canvasRect.sizeDelta = oldCanvasSize;
                content.sizeDelta = oldContentSize; content.anchoredPosition = oldContentPosition;
                canvas.transform.SetPositionAndRotation(oldPosition, oldRotation); canvas.transform.localScale = oldScale;
            }
        }

        static void Capture(string name, Vector3 position, Vector3 focus, float fieldOfView, int width = 1600, int height = 1000)
        {
            var camera = new GameObject("Two-line actual scene capture").AddComponent<Camera>();
            camera.enabled = false; camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.fieldOfView = fieldOfView; camera.nearClipPlane = .05f; camera.farClipPlane = 75;
            camera.cullingMask = ~(1 << 5);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.045f, .065f, .085f);
            camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(focus - position, Vector3.up));
            var target = new RenderTexture(width, height, 24) { antiAliasing = 4 };
            var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                foreach (var text in UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsSortMode.None))
                    if (text.font != null) text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle);
                camera.targetTexture = target; target.Create(); camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/" + name));
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, ImageConversion.EncodeToPNG(pixels));
            }
            finally
            {
                RenderTexture.active = previous; camera.targetTexture = null; target.Release();
                UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels); UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }
    }
}

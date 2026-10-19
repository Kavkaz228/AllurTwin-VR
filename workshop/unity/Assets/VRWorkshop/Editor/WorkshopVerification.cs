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
    // Invoke in batch mode with -executeMethod VRWorkshop.Editor.WorkshopVerification.Run,
    // WITHOUT -quit. This class exits after the asynchronous play-mode verification.
    // It exercises desktop input and the actual MonoBehaviour/socket/server loop.
    // It does not verify Vive tracking, controller bindings, stereo rendering or hardware.
    [InitializeOnLoad]
    public static class WorkshopVerification
    {
        const string SessionKey = "VRWorkshop.PlaymodeVerification";
        const string Endpoint = "ws://127.0.0.1:4182/ws";
        const string RobotId = "training-arm";
        const string SecondRobotId = "training-arm-2";
        const double StageTimeout = 30;
        enum Stage { EnterPlay, SceneChecks, DoorOpen, DoorClose, PhysicsDrop, Connect, PreviewReady, Select, Acquire, Reset, RecordStart, Settle, Hold, Move, ReleaseKeys, RecordStop, ReleaseLease, ProductionReset, ProductionStart, ProductionAdvance, ProductionStop, Assistant, Finish }

        [Serializable]
        sealed class StationReport
        {
            public string robotId, recordingId;
            public bool controlAcquired, resetAccepted, recordingStarted, recordingStopped, deadmanStopped, controlReleased;
            public int movementFrames, recordedSamples, releasedSafeFrames;
            public float tcpDisplacementMetres;
            public Point3 initialTcp, finalTcp;
        }

        [Serializable]
        sealed class Report
        {
            public bool success;
            public string endpoint = Endpoint, robotId = RobotId, mode, error, failedStage, lastStatus;
            public string startedUtc, finishedUtc, recordingId;
            public string verificationScope = "Interactive workshop scene, cabinet door and synchronous pickup/drop methods, virtual camera render target where graphics are available; Unity desktop input -> two cabinet selections -> real local WebSocket -> independent server simulations -> telemetry";
            public string unverified = "Vive Pro 2, Vive controller bindings, OpenXR tracking, headset display, object selection by a physical ray, camera image quality or external cameras, physical robot, AMAS and Tecnomatix hardware integration";
            public bool syntheticApplicationFocus = true, virtualKeyboard = true;
            public bool authenticated, simulationValidated, controlAcquired, resetAccepted, recordingStarted, recordingStopped, deadmanStopped, controlReleased;
            public int movementFrames, playFrames, recordedSamples;
            public float tcpDisplacementMetres;
            public double elapsedSeconds;
            public Point3 initialTcp, finalTcp;
            public bool environmentPresent, robotBasesDistinct, cabinetDoorOpened, cabinetDoorClosed, pickupDropped;
            public int interactiveTargets, cabinetDoors, movableObjects;
            public float robotBaseSeparationMetres, firstRobotDriftMetres;
            public bool firstRobotUnchangedDuringSecondMotion, leaseSwitched, virtualVideoConfigured, virtualVideoRenderTarget;
            public string graphicsDevice, videoCheck;
            public bool productionStarted, productionCompleted, productionStopped, conveyorPanelMatchesServer, assistantAnswered;
            public int productionCompletedPanels, firstStationCycles, secondStationCycles;
            public float conveyorTravelMetres, panelPositionErrorMetres;
            public string assistantSource;
            public bool gravityAndFloorContact, beltMovesLooseProp;
            public bool cabinetGrabSnapAndRelease;
            public StationReport[] stations = new[] { new StationReport { robotId = RobotId }, new StationReport { robotId = SecondRobotId } };
        }

        [Serializable]
        sealed class State
        {
            public Stage stage;
            public double started, stageStarted;
            public int firstFrame, stageFrame;
            public int releasedSafeFrames, lastObservedSettleFrame = -1;
            public long stageSequence;
            public bool inputSettingsSaved;
            public int backgroundBehavior, editorInputBehavior;
            public int activeStation;
            public string firstLease;
            public float[] firstStoppedJoints;
            public Point3 firstStoppedTcp;
            public long firstStoppedSequence;
            public int initialCompleted, initialFirstCycles, initialSecondCycles;
            public float initialTravel, dropY, beltPropZ;
            public bool previewOnly;
            public Report report = new Report();
        }

        static State state;
        static WorkshopClient client;
        static Keyboard keyboard;
        static WorkshopTarget door;
        static WorkshopTarget physicsPart;
        static bool finishing;

        static WorkshopVerification()
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
            if (!Application.isBatchMode) throw new InvalidOperationException("WorkshopVerification requires batch mode because it exits the Editor.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Start verification from edit mode.");
            state = new State
            {
                stage = Stage.EnterPlay,
                started = EditorApplication.timeSinceStartup,
                stageStarted = EditorApplication.timeSinceStartup
            };
            state.previewOnly = previewOnly;
            if (previewOnly) state.report.verificationScope = "Read-only connection, real local AI answer and rendering of industrial scene/SCADA; production motion not requested.";
            state.report.startedUtc = DateTime.UtcNow.ToString("O");
            Save();
            Subscribe();
            try
            {
                const string scene = WorkshopBuild.ScenePath;
                if (!File.Exists(scene)) throw new FileNotFoundException("Create the application scene before verification.", scene);
                EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { Fail(error.Message); }
        }

        static void Subscribe()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        static void Save() => SessionState.SetString(SessionKey, JsonUtility.ToJson(state));

        static void Next(Stage stage)
        {
            state.stage = stage;
            state.stageStarted = EditorApplication.timeSinceStartup;
            state.stageFrame = Time.frameCount;
            state.stageSequence = client?.Telemetry?.sequence ?? -1;
            if (stage == Stage.Settle)
            {
                state.releasedSafeFrames = 0;
                state.lastObservedSettleFrame = -1;
            }
            Save();
            Debug.Log("Workshop verification: " + stage);
        }

        static void SetKeys(bool space, bool up)
        {
            if (keyboard == null || !keyboard.added) return;
            keyboard.MakeCurrent();
            InputSystem.QueueStateEvent(keyboard, space
                ? (up ? new KeyboardState(Key.Space, Key.E) : new KeyboardState(Key.Space))
                : new KeyboardState());
        }

        static void PrepareInput()
        {
            if (!state.inputSettingsSaved)
            {
                state.backgroundBehavior = (int)InputSystem.settings.backgroundBehavior;
                state.editorInputBehavior = (int)InputSystem.settings.editorInputBehaviorInPlayMode;
                state.inputSettingsSaved = true;
                Save();
            }
            // Test-only focus policy. Runtime focus-loss behavior is not changed.
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (keyboard == null || !keyboard.added)
                keyboard = InputSystem.AddDevice<Keyboard>("WorkshopVerificationKeyboard");
            keyboard.MakeCurrent();
        }

        static bool FreshTelemetry => client?.Telemetry?.tool?.position != null && client.Telemetry.connected && client.TelemetryAge <= client.telemetryTimeoutSeconds;

        static string SelectedRobotId => state.activeStation == 0 ? RobotId : SecondRobotId;
        static StationReport Station => state.report.stations[state.activeStation];
        static bool SimulationSelected => client != null && state.report.simulationValidated && client.RobotId == SelectedRobotId && client.Telemetry?.robotId == SelectedRobotId && client.Telemetry.mode == "simulation";

        static void Update()
        {
            if (state == null || finishing) return;
            try
            {
                double elapsed = EditorApplication.timeSinceStartup - state.stageStarted;
                if (state.stage == Stage.Finish)
                {
                    SetKeys(false, false);
                    if (elapsed >= .7) Complete();
                    else EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                double limit = state.stage == Stage.ProductionAdvance || state.stage == Stage.Assistant ? 120 : StageTimeout;
                if (elapsed > limit)
                    throw new TimeoutException("Stage timed out after " + limit + " seconds. " + Diagnostic());
                EditorApplication.QueuePlayerLoopUpdate();
                if (!EditorApplication.isPlaying)
                {
                    if (state.stage != Stage.EnterPlay) throw new InvalidOperationException("Play mode ended unexpectedly.");
                    return;
                }
                if (client == null) client = UnityEngine.Object.FindFirstObjectByType<WorkshopClient>();
                if (client == null || client.Rig == null) return;
                PrepareInput();
                // Batch Editor has no reliable game-window focus; report the synthetic focus.
                client.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
                client.Rig.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
                EventSystem.current?.SetSelectedGameObject(null);
                bool hold = state.stage == Stage.Hold || state.stage == Stage.Move;
                SetKeys(hold, state.stage == Stage.Move);
                if (client.Rig.InXR) throw new InvalidOperationException("This smoke test requires desktop fallback; an active XR display was detected.");
                if (state.report.simulationValidated && client.Telemetry != null && client.Telemetry.mode != "simulation")
                    throw new InvalidOperationException("Telemetry is not simulation. Test commands are prohibited.");

                switch (state.stage)
                {
                    case Stage.EnterPlay:
                        state.firstFrame = Time.frameCount;
                        Next(Stage.SceneChecks);
                        break;
                    case Stage.SceneChecks:
                        if (!VerifyScene()) break;
                        if (state.previewOnly)
                        {
                            client.Connect(Endpoint, System.Environment.GetEnvironmentVariable("VR_ACCESS_TOKEN") ?? "local-training-only", "Unity preview verification");
                            Next(Stage.Connect); break;
                        }
                        door = FindDoor();
                        client.Environment.Activate(door);
                        Next(Stage.DoorOpen);
                        break;
                    case Stage.DoorOpen:
                        if (door == null) door = FindDoor();
                        if (!door.IsDoorOpen || Quaternion.Angle(door.hinge.localRotation, Quaternion.identity) < 80) break;
                        state.report.cabinetDoorOpened = true;
                        client.Environment.Activate(door);
                        Next(Stage.DoorClose);
                        break;
                    case Stage.DoorClose:
                        if (door == null) door = FindDoor();
                        if (door.IsDoorOpen || Quaternion.Angle(door.hinge.localRotation, Quaternion.identity) > 1) break;
                        state.report.cabinetDoorClosed = true;
                        VerifyCabinetInteraction();
                        VerifyPickupDrop();
                        physicsPart = Array.Find(client.Environment.GetComponentsInChildren<WorkshopTarget>(true), p => p.action == WorkshopAction.Movable && p.movableBody != null);
                        physicsPart.movableBody.position = physicsPart.homePosition + Vector3.up * .7f;
                        physicsPart.movableBody.linearVelocity = Vector3.zero;
                        state.dropY = physicsPart.movableBody.position.y;
                        Next(Stage.PhysicsDrop);
                        break;
                    case Stage.PhysicsDrop:
                        if (elapsed < 1.3) break;
                        if (physicsPart.movableBody.position.y > state.dropY - .3f || physicsPart.movableBody.position.y < .01f)
                            throw new InvalidOperationException("Loose prop failed gravity/floor-contact check.");
                        state.report.gravityAndFloorContact = true;
                        RestorePhysicsProp();
                        client.Connect(Endpoint, System.Environment.GetEnvironmentVariable("VR_ACCESS_TOKEN") ?? "local-training-only", "Unity playmode verification");
                        Next(Stage.Connect);
                        break;
                    case Stage.Connect:
                        if (!client.Ready) break;
                        state.report.authenticated = true;
                        if (client.Robots.Length < 2 || client.Robots[0].id != RobotId || client.Robots[1].id != SecondRobotId ||
                            client.Robots[0].mode != "simulation" || client.Robots[1].mode != "simulation")
                            throw new InvalidOperationException("Welcome must advertise training-arm and training-arm-2 as the first two simulation stations. No motion commands sent.");
                        state.report.simulationValidated = true;
                        state.report.mode = "simulation";
                        if (state.previewOnly) { Next(Stage.PreviewReady); break; }
                        state.activeStation = 0;
                        Next(Stage.Select);
                        break;
                    case Stage.PreviewReady:
                        if (!FreshTelemetry || client.Conveyor?.PanelCount == 0) break;
                        UnityEngine.Object.FindFirstObjectByType<WorkshopUI>().SubmitAssistantQuestion("Сколько роботов на линии, сколько готовых панелей выпущено и почему сейчас конвейер стоит?");
                        Next(Stage.Assistant);
                        break;
                    case Stage.Select:
                        client.SelectCabinet(state.activeStation);
                        Next(Stage.Acquire);
                        break;
                    case Stage.Acquire:
                        if (!client.HasControl || client.Rig.Deadman || !FreshTelemetry || !SimulationSelected) break;
                        state.report.controlAcquired = true;
                        Station.controlAcquired = true;
                        if (state.activeStation == 0) state.firstLease = client.LeaseId;
                        else
                        {
                            if (client.LeaseId == state.firstLease) throw new InvalidOperationException("Switching cabinets reused the previous robot lease.");
                            state.report.leaseSwitched = true;
                        }
                        client.ResetCabinet(state.activeStation);
                        Next(Stage.Reset);
                        break;
                    case Stage.Reset:
                        if (!FreshTelemetry || !string.IsNullOrEmpty(client.Telemetry.fault) ||
                            !client.Status.StartsWith("Сброс принят.", StringComparison.Ordinal)) break;
                        state.report.resetAccepted = true;
                        Station.resetAccepted = true;
                        if (client.Telemetry.recording?.active == true)
                            throw new InvalidOperationException("An existing recording is active. Verification will not take it over.");
                        client.ToggleRecording();
                        Next(Stage.RecordStart);
                        break;
                    case Stage.RecordStart:
                        if (client.Telemetry?.recording?.active != true) break;
                        state.report.recordingStarted = true;
                        Station.recordingStarted = true;
                        Station.recordingId = client.Telemetry.recording.id;
                        if (state.activeStation == 0) state.report.recordingId = Station.recordingId;
                        Next(Stage.Settle);
                        break;
                    case Stage.Settle:
                        // An editor update can see a timer expire before WorkshopClient.Update
                        // has cleared releaseRequired. Observe distinct, consecutively safe
                        // player frames with keys released before injecting any Space event.
                        if (state.lastObservedSettleFrame == Time.frameCount) break;
                        state.lastObservedSettleFrame = Time.frameCount;
                        bool releasedAndSafe = FreshTelemetry && SimulationSelected && client.HasControl &&
                            !client.Rig.Deadman && !client.SceneInteractionBlocked && !client.IsEditingUI &&
                            client.Rig.HeadTracked && client.Rig.RightTracked && string.IsNullOrEmpty(client.Telemetry.fault);
                        state.releasedSafeFrames = releasedAndSafe ? state.releasedSafeFrames + 1 : 0;
                        if (state.releasedSafeFrames < 5) break;
                        RequireNoFault();
                        Station.releasedSafeFrames = state.releasedSafeFrames;
                        Station.initialTcp = Copy(client.Telemetry.tool.position);
                        if (state.activeStation == 0) state.report.initialTcp = Copy(Station.initialTcp);
                        Next(Stage.Hold);
                        break;
                    case Stage.Hold:
                        RequireNoFault();
                        if (!client.Driving || !client.Rig.Deadman || Time.frameCount - state.stageFrame < 3) break;
                        Next(Stage.Move);
                        break;
                    case Stage.Move:
                        RequireNoFault();
                        Station.movementFrames = Time.frameCount - state.stageFrame;
                        Station.finalTcp = Copy(client.Telemetry.tool.position);
                        Station.tcpDisplacementMetres = Distance(Station.initialTcp, Station.finalTcp);
                        if (state.activeStation == 0)
                        {
                            state.report.movementFrames = Station.movementFrames;
                            state.report.finalTcp = Copy(Station.finalTcp);
                            state.report.tcpDisplacementMetres = Station.tcpDisplacementMetres;
                        }
                        else VerifyFirstRobotUnchanged();
                        if (Station.movementFrames >= 6 && elapsed >= .12 && Station.tcpDisplacementMetres > .002f)
                        {
                            SetKeys(false, false);
                            Next(Stage.ReleaseKeys);
                        }
                        else if (elapsed > 1.5)
                            throw new InvalidOperationException("Space+E did not produce >2 mm of server TCP movement within 1.5 seconds. " + Diagnostic());
                        break;
                    case Stage.ReleaseKeys:
                        RequireNoFault();
                        if (!FreshTelemetry || client.Rig.Deadman || client.Driving || client.Telemetry.moving || client.Telemetry.sequence <= state.stageSequence) break;
                        state.report.deadmanStopped = true;
                        Station.deadmanStopped = true;
                        Station.recordedSamples = client.Telemetry.recording?.samples ?? 0;
                        if (state.activeStation == 0) state.report.recordedSamples = Station.recordedSamples;
                        if (Station.recordedSamples < 1) break;
                        client.ToggleRecording();
                        Next(Stage.RecordStop);
                        break;
                    case Stage.RecordStop:
                        if (client.Telemetry?.recording == null || client.Telemetry.recording.active || client.Telemetry.sequence <= state.stageSequence) break;
                        state.report.recordingStopped = true;
                        Station.recordingStopped = true;
                        client.ReleaseCabinet(state.activeStation);
                        Next(Stage.ReleaseLease);
                        break;
                    case Stage.ReleaseLease:
                        if (!FreshTelemetry || client.HasControl || !string.IsNullOrEmpty(client.Telemetry.leaseOwner) || client.Telemetry.sequence <= state.stageSequence) break;
                        state.report.controlReleased = true;
                        Station.controlReleased = true;
                        if (state.activeStation == 0)
                        {
                            state.firstStoppedJoints = (float[])client.Telemetry.joints.Clone();
                            state.firstStoppedTcp = Copy(client.Telemetry.tool.position);
                            state.firstStoppedSequence = client.Telemetry.sequence;
                            state.activeStation = 1;
                            Next(Stage.Select);
                        }
                        else
                        {
                            VerifyFirstRobotUnchanged();
                            client.ResetProduction();
                            Next(Stage.ProductionReset);
                        }
                        break;
                    case Stage.ProductionReset:
                        if (!FreshTelemetry || client.Telemetry.production == null || client.Telemetry.production.running || !string.IsNullOrEmpty(client.Telemetry.fault)) break;
                        if (client.GetStationTelemetry(0) == null || !string.IsNullOrEmpty(client.GetStationTelemetry(0).fault)) break;
                        state.initialCompleted = client.Telemetry.production.completed;
                        state.initialFirstCycles = client.Telemetry.production.stations[0].cycles;
                        state.initialSecondCycles = client.Telemetry.production.stations[1].cycles;
                        state.initialTravel = client.Telemetry.production.conveyorPosition;
                        // Test a loose local body before the first incoming fixture reaches it.
                        // Exact belt transport is assessed only for a newly started empty-input line.
                        if (state.initialTravel < .01f)
                        {
                            // Empty belt ahead of the first full-size body/carrier.
                            physicsPart.movableBody.position = new Vector3(.31f, 1.07f, 2.7f);
                            physicsPart.movableBody.linearVelocity = Vector3.zero;
                            state.beltPropZ = physicsPart.movableBody.position.z;
                        }
                        client.StartProduction();
                        Next(Stage.ProductionStart);
                        break;
                    case Stage.ProductionStart:
                        if (!FreshTelemetry || client.Telemetry.production?.running != true || string.IsNullOrEmpty(client.ProductionId)) break;
                        state.report.productionStarted = true;
                        Next(Stage.ProductionAdvance);
                        break;
                    case Stage.ProductionAdvance:
                        if (!FreshTelemetry) break;
                        var production = client.Telemetry.production;
                        if (production == null || !production.running) throw new InvalidOperationException("AUTO line stopped before a panel completed: " + production?.stopReason);
                        if (client.Conveyor == null || client.Conveyor.PanelCount == 0) throw new InvalidOperationException("No visible server-driven panels.");
                        if (state.initialTravel < .01f && !state.report.beltMovesLooseProp && elapsed > 1.5)
                        {
                            if (physicsPart.movableBody.position.z < state.beltPropZ + .08f)
                                throw new InvalidOperationException("Moving belt did not transport the free rigidbody through contact forces.");
                            state.report.beltMovesLooseProp = true;
                            RestorePhysicsProp();
                        }
                        foreach (var panel in production.parts)
                        {
                            if (client.Conveyor.TryGetPanelPosition(panel.id, out var rendered))
                            {
                                float error = Vector3.Distance(rendered, new Vector3(0, .95f, -2 + panel.position));
                                state.report.panelPositionErrorMetres = Mathf.Max(error, state.report.panelPositionErrorMetres);
                                if (error > .12f) throw new InvalidOperationException("Panel position diverged from server by " + error + "m.");
                                state.report.conveyorPanelMatchesServer = true;
                            }
                        }
                        if (production.completed <= state.initialCompleted || production.stations[0].cycles <= state.initialFirstCycles || production.stations[1].cycles <= state.initialSecondCycles) break;
                        state.report.productionCompleted = true;
                        state.report.productionCompletedPanels = production.completed - state.initialCompleted;
                        state.report.firstStationCycles = production.stations[0].cycles - state.initialFirstCycles;
                        state.report.secondStationCycles = production.stations[1].cycles - state.initialSecondCycles;
                        state.report.conveyorTravelMetres = production.conveyorPosition - state.initialTravel;
                        if (state.report.firstStationCycles < 1 || state.report.secondStationCycles < 1)
                            throw new InvalidOperationException("Both robotic stations must process a panel before production completion.");
                        client.StopProduction();
                        Next(Stage.ProductionStop);
                        break;
                    case Stage.ProductionStop:
                        if (!FreshTelemetry || client.Telemetry.production.running || Mathf.Abs(client.Telemetry.production.speed) > .0001f || client.Telemetry.moving || client.GetStationTelemetry(0).moving) break;
                        state.report.productionStopped = true;
                        client.AskAI("Объясни состояние конвейера и почему линия остановлена.");
                        Next(Stage.Assistant);
                        break;
                    case Stage.Assistant:
                        if (string.IsNullOrEmpty(client.AssistantText) || client.AssistantText == "Сервер готовит ответ…") break;
                        state.report.assistantAnswered = true;
                        state.report.assistantSource = client.AssistantSource;
                        if (state.previewOnly && client.AssistantSource != "ollama") throw new InvalidOperationException("Preview requires an actual local model answer, not fallback rules.");
                        state.report.success = true;
                        Next(Stage.Finish);
                        break;
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }

        static bool VerifyScene()
        {
            if (client.Environment == null) throw new InvalidOperationException("The interactive workshop environment is missing.");
            state.report.environmentPresent = true;
            Transform first = client.Environment.GetRobotBase(0), second = client.Environment.GetRobotBase(1);
            if (first == null || second == null || first == second) throw new InvalidOperationException("Two separate robot bases are required.");
            state.report.robotBaseSeparationMetres = Vector3.Distance(first.position, second.position);
            if (state.report.robotBaseSeparationMetres < 1) throw new InvalidOperationException("Robot workstation bases overlap.");
            state.report.robotBasesDistinct = true;
            var targets = client.Environment.GetComponentsInChildren<WorkshopTarget>(true);
            state.report.interactiveTargets = targets.Length;
            state.report.cabinetDoors = Array.FindAll(targets, target => target.action == WorkshopAction.ToggleDoor && target.hinge != null).Length;
            state.report.movableObjects = Array.FindAll(targets, target => target.action == WorkshopAction.Movable && target.movableBody != null).Length;
            if (targets.Length < 20 || state.report.cabinetDoors < 2 || state.report.movableObjects < 2)
                throw new InvalidOperationException("Workshop scene is missing interactive cabinets or movable parts.");
            if (client.Video == null || client.Video.SourceKind != "virtual")
                throw new InvalidOperationException("Verification requires the default virtual video source; external camera sources are not exercised.");
            state.report.virtualVideoConfigured = true;
            state.report.graphicsDevice = SystemInfo.graphicsDeviceType.ToString();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                state.report.videoCheck = "Render-target/live-video assertion skipped: Null graphics device (-nographics).";
                return true;
            }
            var texture = client.Video.Texture as RenderTexture;
            if (texture == null || !texture.IsCreated() || client.Video.State != "LIVE" || !client.Video.IsFresh) return false;
            state.report.virtualVideoRenderTarget = true;
            state.report.videoCheck = "Virtual source has a created RenderTexture and fresh LIVE frames; image pixels and external cameras are not assessed.";
            return true;
        }
        static void RestorePhysicsProp()
        {
            if (physicsPart == null) return;
            physicsPart.movableBody.position = physicsPart.homePosition;
            physicsPart.movableBody.rotation = physicsPart.homeRotation;
            physicsPart.movableBody.linearVelocity = Vector3.zero;
            physicsPart.movableBody.angularVelocity = Vector3.zero;
        }

        static WorkshopTarget FindDoor()
        {
            var targets = client.Environment.GetComponentsInChildren<WorkshopTarget>(true);
            var found = Array.Find(targets, target => target.action == WorkshopAction.ToggleDoor && target.stationIndex == 0 && target.hinge != null);
            return found != null ? found : throw new InvalidOperationException("Cabinet 1 has no animated door target.");
        }

        static void VerifyPickupDrop()
        {
            var part = Array.Find(client.Environment.GetComponentsInChildren<WorkshopTarget>(true),
                target => target.action == WorkshopAction.Movable && target.movableBody != null && target.stationIndex == 0);
            if (part == null || client.Environment.Interaction == null) throw new InvalidOperationException("A movable part or interaction controller is missing.");
            client.Rig.SetDesktopView(part.transform.position + new Vector3(0, .25f, -.7f), part.transform.position);
            bool wasKinematic = part.movableBody.isKinematic;
            if (!client.Environment.Interaction.BeginGrab(part)) throw new InvalidOperationException("BeginGrab rejected the nearby training part.");
            try
            {
                if (client.Environment.Interaction.HeldObject != part || !part.movableBody.isKinematic)
                    throw new InvalidOperationException("Pickup did not enter the held/kinematic state.");
            }
            finally { client.Environment.Interaction.DropHeld(); }
            if (client.Environment.Interaction.HeldObject != null || part.movableBody.isKinematic != wasKinematic)
                throw new InvalidOperationException("Drop did not clear the held object and restore its rigidbody state.");
            state.report.pickupDropped = true;
        }

        static void VerifyCabinetInteraction()
        {
            var trainer = client.Environment.GetComponentInChildren<CabinetTrainer>();
            if (trainer == null) throw new InvalidOperationException("Training cabinet missing.");
            var part = trainer.parts[0]; var target = part.GetComponent<WorkshopTarget>();
            var interaction = client.Environment.Interaction;
            trainer.ResetExercise();
            client.Rig.SetDesktopView(part.transform.position + new Vector3(0, .1f, -.5f), part.transform.position);
            if (!interaction.BeginGrab(target)) throw new InvalidOperationException("Cabinet component pickup failed.");
            foreach (var collider in part.GetComponentsInChildren<Collider>())
                if (collider.gameObject.layer != 2) throw new InvalidOperationException("Held compound collider can hit itself.");
            part.Body.position = trainer.transform.TransformPoint(trainer.slots[0].position);
            part.Body.rotation = trainer.transform.rotation;
            interaction.DropHeld(true);
            if (part.slot != 0 || !part.Body.isKinematic || interaction.HeldObject != null)
                throw new InvalidOperationException("Grip release did not mount the cabinet component: " + trainer.Feedback);
            foreach (var collider in part.GetComponentsInChildren<Collider>())
                if (collider.gameObject.layer == 2) throw new InvalidOperationException("Collider layer not restored after release.");
            var port = part.GetComponentInChildren<CabinetPort>();
            client.Environment.Activate(port.GetComponent<WorkshopTarget>());
            if (trainer.SelectedPort != port) throw new InvalidOperationException("Port click is not routed to cabinet.");
            if (!interaction.BeginGrab(target)) throw new InvalidOperationException("Mounted component cannot be detached.");
            interaction.DropHeld(); // Forced loss of tracking must never silently install a part.
            if (part.slot >= 0 || part.Body.isKinematic) throw new InvalidOperationException("Forced release incorrectly installed component.");
            trainer.ResetExercise(); state.report.cabinetGrabSnapAndRelease = true;
        }

        static void VerifyFirstRobotUnchanged()
        {
            var first = client.GetStationTelemetry(0);
            if (first?.tool?.position == null || first.robotId != RobotId || first.mode != "simulation" ||
                first.sequence <= state.firstStoppedSequence || first.sequence + 2 < client.Telemetry.sequence)
                throw new InvalidOperationException("Fresh telemetry for the unselected first robot is unavailable.");
            if (first.joints == null || state.firstStoppedJoints == null || first.joints.Length != state.firstStoppedJoints.Length)
                throw new InvalidOperationException("First robot joint telemetry is incomplete.");
            for (int i = 0; i < first.joints.Length; i++)
                if (Mathf.Abs(first.joints[i] - state.firstStoppedJoints[i]) > .00001f)
                    throw new InvalidOperationException("Moving cabinet 2 changed a joint of cabinet 1.");
            state.report.firstRobotDriftMetres = Distance(first.tool.position, state.firstStoppedTcp);
            if (first.moving || state.report.firstRobotDriftMetres > .00001f || !string.IsNullOrEmpty(first.leaseOwner))
                throw new InvalidOperationException("First robot moved or retained ownership during cabinet 2 control.");
            state.report.firstRobotUnchangedDuringSecondMotion = true;
        }

        static void RequireNoFault()
        {
            if (!client.HasControl || !FreshTelemetry || !SimulationSelected) throw new InvalidOperationException("Control or fresh telemetry for the selected simulation robot was lost. " + Diagnostic());
            if (!string.IsNullOrEmpty(client.Telemetry.fault)) throw new InvalidOperationException("Server fault: " + client.Telemetry.fault);
        }

        static string Diagnostic()
        {
            if (client == null) return "WorkshopClient is unavailable.";
            // Read-only diagnostics: runtime guards are never overridden by the harness.
            object releaseRequired = typeof(WorkshopClient).GetField("releaseRequired", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client);
            return $"Robot={client.RobotId}; station={state.activeStation}; Status={client.Status}; driving={client.Driving}; deadman={client.Rig?.Deadman}; " +
                $"blocked={client.SceneInteractionBlocked}; editing={client.IsEditingUI}; headTracked={client.Rig?.HeadTracked}; rightTracked={client.Rig?.RightTracked}; " +
                $"releaseRequired={releaseRequired}; releasedSafeFrames={state.releasedSafeFrames}; telemetryAge={client.TelemetryAge:F3}s; sequence={client.Telemetry?.sequence}";
        }

        static Point3 Copy(Point3 value) => new Point3(value.x, value.y, value.z);
        static float Distance(Point3 a, Point3 b) => Vector3.Distance(new Vector3(a.x, a.y, a.z), new Vector3(b.x, b.y, b.z));

        static void Fail(string error)
        {
            if (state == null || state.stage == Stage.Finish) return;
            state.report.success = false;
            state.report.error = error;
            state.report.failedStage = state.stage.ToString();
            Debug.LogError("Workshop verification failed: " + error);
            try
            {
                SetKeys(false, false);
                if (SimulationSelected && client.HasControl)
                {
                    client.Stop();
                    if (state.report.recordingStarted && client.Telemetry?.recording?.active == true) client.ToggleRecording();
                    client.Release();
                }
            }
            catch (Exception cleanup) { state.report.error += " Cleanup: " + cleanup.Message; }
            Next(Stage.Finish);
        }

        static void Complete()
        {
            finishing = true;
            state.report.lastStatus = client?.Status;
            state.report.finishedUtc = DateTime.UtcNow.ToString("O");
            state.report.elapsedSeconds = EditorApplication.timeSinceStartup - state.started;
            state.report.playFrames = Math.Max(0, Time.frameCount - state.firstFrame);
            try
            {
                if (state.report.success && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    RenderWorkshopPreview();
                SetKeys(false, false);
                client?.Disconnect();
                if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
                if (state.inputSettingsSaved)
                {
                    InputSystem.settings.backgroundBehavior = (InputSettings.BackgroundBehavior)state.backgroundBehavior;
                    InputSystem.settings.editorInputBehaviorInPlayMode = (InputSettings.EditorInputBehaviorInPlayMode)state.editorInputBehavior;
                }
                string reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, state.previewOnly ? "../Logs/preview-verification.json" : "../Logs/playmode-verification.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                File.WriteAllText(reportPath, JsonUtility.ToJson(state.report, true));
                Debug.Log("Workshop verification report: " + reportPath);
            }
            catch (Exception error)
            {
                state.report.success = false;
                Debug.LogError("Cannot complete verification report: " + error);
            }
            finally
            {
                SessionState.EraseString(SessionKey);
                EditorApplication.update -= Update;
                EditorApplication.Exit(state.report.success ? 0 : 1);
            }
        }
        static void RenderWorkshopPreview()
        {
            // Render the actual simulated scene to an artifact, without capturing the user's desktop.
            var cameraObject = new GameObject("Workshop preview camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.fieldOfView = 58;
            camera.cullingMask = ~(1 << 5);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.045f, .065f, .085f);
            camera.transform.position = new Vector3(-4.2f, 2.8f, -4.9f);
            camera.transform.LookAt(new Vector3(0, 1.3f, .8f));
            var target = new RenderTexture(1600, 900, 24) { antiAliasing = 4 };
            var pixels = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target; target.Create(); camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); pixels.Apply();
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/workshop-preview.png"));
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(pixels));
                CaptureScada(camera, target, pixels, false);
                CaptureScada(camera, target, pixels, true);
            }
            finally
            {
                RenderTexture.active = previous; camera.targetTexture = null;
                target.Release(); UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(pixels); UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
        static void CaptureScada(Camera camera, RenderTexture target, Texture2D pixels, bool assistant)
        {
            var ui = UnityEngine.Object.FindFirstObjectByType<WorkshopUI>();
            var canvasObject = GameObject.Find("SCADA operator workspace");
            if (ui == null || canvasObject == null) return;
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            if (assistant) ui.ShowAssistantPanel(); else ui.ShowManualPanel();
            RenderMode previousMode = canvas.renderMode;
            Camera previousCamera = canvas.worldCamera;
            float previousDistance = canvas.planeDistance;
            float previousScale = canvas.scaleFactor;
            var content = canvas.transform.Find("SCADA content") as RectTransform;
            Vector2 contentSize = content.sizeDelta;
            var hint = content.Find("Scene controls hint") as RectTransform;
            Vector2 hintPosition = hint != null ? hint.anchoredPosition : Vector2.zero;
            int previousMask = camera.cullingMask;
            CameraClearFlags previousClear = camera.clearFlags;
            try
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = .5f;
                canvas.scaleFactor = 1;
                content.sizeDelta = new Vector2(1600, 900);
                if (hint != null) hint.anchoredPosition = new Vector2(20, -816);
                foreach (var graphic in canvas.GetComponentsInChildren<Graphic>())
                {
                    if (graphic is Text label && label.font != null)
                        label.font.RequestCharactersInTexture(label.text, label.fontSize, label.fontStyle);
                    graphic.SetAllDirty();
                }
                Canvas.ForceUpdateCanvases();
                // Match the desktop ScreenSpaceOverlay ordering: scene first, UI second.
                // A near scene sign must not draw over the preview's temporary camera canvas.
                camera.cullingMask = ~(1 << 5); camera.Render();
                camera.cullingMask = 1 << 5; camera.clearFlags = CameraClearFlags.Depth; camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); pixels.Apply();
                string filename = assistant ? "industrial-assistant-preview.png" : "industrial-scada-preview.png";
                File.WriteAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/" + filename)), ImageConversion.EncodeToPNG(pixels));
            }
            finally
            {
                canvas.renderMode = previousMode; canvas.worldCamera = previousCamera; canvas.planeDistance = previousDistance;
                canvas.scaleFactor = previousScale; content.sizeDelta = contentSize;
                if (hint != null) hint.anchoredPosition = hintPosition;
                camera.cullingMask = previousMask; camera.clearFlags = previousClear;
            }
        }
    }
}

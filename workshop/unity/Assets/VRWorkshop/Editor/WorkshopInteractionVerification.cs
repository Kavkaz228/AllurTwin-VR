using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace VRWorkshop.Editor
{
    // Batch Play Mode entry WITHOUT -quit. Opens the saved scene without regenerating it.
    // No network connection, robot lease, production command, or headset motion is requested.
    [InitializeOnLoad]
    public static class WorkshopInteractionVerification
    {
        const string SessionKey = "VRWorkshop.InteractionVerification";
        [Serializable] sealed class Report
        {
            public bool success, aisleLanding, arcLanding, tableRejected, beltRejected, robotFootprintRejected;
            public bool fenceRejected, arcBlockedByFence, roomEdgeRejected, nonFloorArcRejected;
            public bool doorRayResolved, doorAction, movableRayResolved, pickupDropped;
            public bool teleportReleaseLatch, blockedAimCancelled;
            public bool cabinetPortGrab, cabinetSnap, assistedPortRay, guidedWireSelection;
            public bool cabinetToolSequence,simulationGraspRelease;
            public bool syntheticDesktopFocus = true, operatorPanelHiddenForGeometry = true;
            public string startedUtc, finishedUtc, error;
            public string scope = "Saved workshop scene: actual Physics ray/arc/capsule queries and first-collider target resolution; local door/pickup/drop; synthetic teleport release/guard state transitions";
            public string unverified = "Physical VIVE/SteamVR focus, hardware controller bindings, VR release-to-teleport input, stereo visibility, robot or production motion";
        }
        [Serializable] sealed class State
        {
            public double started;
            public int firstFrame = -1, hiddenFrame = -1;
            public Report report = new Report();
        }
        static State state;
        static WorkshopClient client;
        static bool finishing;

        static WorkshopInteractionVerification()
        {
            string saved = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            state = JsonUtility.FromJson<State>(saved);
            Subscribe();
        }
        public static void Run()
        {
            if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run interaction verification from batch edit mode without -quit.");
            // This test never inherits a launcher's one-session connection request.
            System.Environment.SetEnvironmentVariable("VR_WORKSHOP_CONNECT_URL", null);
            System.Environment.SetEnvironmentVariable("VR_WORKSHOP_ACCESS_TOKEN", null);
            state = new State { started = EditorApplication.timeSinceStartup };
            state.report.startedUtc = DateTime.UtcNow.ToString("O");
            Save(); Subscribe();
            try
            {
                if (!File.Exists(WorkshopBuild.ScenePath)) throw new FileNotFoundException("The saved workshop scene is missing.");
                EditorSceneManager.OpenScene(WorkshopBuild.ScenePath, OpenSceneMode.Single);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { Finish(error); }
        }
        static void Subscribe() { EditorApplication.update -= Update; EditorApplication.update += Update; }
        static void Save() => SessionState.SetString(SessionKey, JsonUtility.ToJson(state));
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Update()
        {
            if (state == null || finishing) return;
            try
            {
                if (EditorApplication.timeSinceStartup - state.started > 30) throw new TimeoutException("Interaction verification exceeded 30 seconds.");
                EditorApplication.QueuePlayerLoopUpdate();
                if (!EditorApplication.isPlaying) return;
                client = UnityEngine.Object.FindFirstObjectByType<WorkshopClient>();
                if (client == null || client.Environment?.Teleport == null) return;
                if (state.firstFrame < 0) { state.firstFrame = Time.frameCount; Save(); return; }
                if (Time.frameCount - state.firstFrame < 4) return;
                Check(!client.Ready && !client.HasControl, "This geometry test must stay disconnected.");
                Check(!client.Rig.InXR, "Use a desktop batch run for this test; it does not automate a live headset.");
                if (state.hiddenFrame < 0)
                {
                    var ui = UnityEngine.Object.FindFirstObjectByType<WorkshopUI>();
                    foreach (var canvas in ui.GetComponentsInChildren<Canvas>(true)) canvas.gameObject.SetActive(false);
                    EventSystem.current?.SetSelectedGameObject(null);
                    state.hiddenFrame = Time.frameCount; Save(); return;
                }
                if (Time.frameCount - state.hiddenFrame < 2) return;
                client.Rig.SendMessage("OnApplicationFocus", true, SendMessageOptions.RequireReceiver);
                client.SendMessage("OnApplicationFocus", true, SendMessageOptions.RequireReceiver);
                Physics.SyncTransforms();
                CheckGeometry();
                CheckActions();
                CheckCabinetGuidance();
                CheckSimulationGripper();
                CheckTeleportTransitions();
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        static void CheckGeometry()
        {
            var report = state.report;
            var teleport = client.Environment.Teleport;
            var colliders = client.Environment.GetComponentsInChildren<Collider>();
            var floor = colliders.First(c => c.name == "Пол — шлифованный бетон");
            var table = colliders.First(c => c.name == "Столешница");
            var belt = colliders.First(c => c.name == "Conveyor belt surface");
            var fence = colliders.First(c => c.name == "Industrial safety fence");
            float level = floor.bounds.max.y;

            report.aisleLanding = teleport.TryValidateLanding(new Vector3(-4.5f, level, -2.7f), out _, out string reason);
            Check(report.aisleLanding, "An empty pedestrian aisle was rejected: " + reason);
            report.arcLanding = teleport.TraceArc(new Ray(new Vector3(-4.5f, level + 1.4f, -4f), Vector3.forward));
            Check(report.arcLanding && teleport.ArcPointCount > 2, "An unobstructed floor arc was rejected: " + teleport.Hint);
            report.tableRejected = !teleport.TryValidateLanding(table.bounds.center + Vector3.up * table.bounds.extents.y, out _, out _) &&
                !teleport.TryValidateLanding(new Vector3(table.bounds.center.x, level, table.bounds.center.z), out _, out _);
            Check(report.tableRejected, "A tabletop or the occupied space underneath was accepted.");
            report.beltRejected = !teleport.TryValidateLanding(belt.bounds.center + Vector3.up * belt.bounds.extents.y, out _, out _) &&
                !teleport.TryValidateLanding(new Vector3(belt.bounds.center.x, level, belt.bounds.center.z), out _, out _);
            Check(report.beltRejected, "The conveyor or its occupied floor was accepted.");
            Vector3 robot = client.Environment.GetRobotBase(0).position; robot.y = level;
            report.robotFootprintRejected = !teleport.TryValidateLanding(robot, out _, out _);
            Check(report.robotFootprintRejected, "The robot pedestal footprint was accepted.");
            Vector3 fenceFoot = fence.bounds.center; fenceFoot.y = level;
            report.fenceRejected = !teleport.TryValidateLanding(fenceFoot, out _, out _);
            Check(report.fenceRejected, "A destination intersecting the fence was accepted.");
            Vector3 outside = Vector3.right * Mathf.Sign(fence.bounds.center.x);
            report.arcBlockedByFence = !teleport.TraceArc(new Ray(fence.bounds.center + outside * .6f, -outside));
            Check(report.arcBlockedByFence && Vector3.Distance(teleport.LandingPoint, fence.bounds.center) < .5f,
                "The arc crossed the physical safety fence.");
            report.roomEdgeRejected = !teleport.TryValidateLanding(new Vector3(floor.bounds.min.x + .1f, level, floor.bounds.min.z + .8f), out _, out _);
            Check(report.roomEdgeRejected, "A body footprint beyond the floor edge was accepted.");
            report.nonFloorArcRejected = !teleport.TraceArc(new Ray(belt.bounds.center + Vector3.up * 1f, Vector3.down));
            Check(report.nonFloorArcRejected, "A vertical arc accepted the conveyor as floor.");
        }

        static void CheckActions()
        {
            var interaction = client.Environment.Interaction;
            var targets = client.Environment.GetComponentsInChildren<WorkshopTarget>();
            var door = targets.First(t => t.action == WorkshopAction.ToggleDoor && t.hinge != null);
            Check(!door.hinge.GetComponentsInChildren<MeshRenderer>().Any(r=>r.isPartOfStaticBatch),"Moving door hardware was included in static rendering batch.");
            Ray doorRay = new Ray(door.transform.position - door.transform.forward * 1.2f, door.transform.forward);
            state.report.doorRayResolved = interaction.TryGetSceneTarget(doorRay, out WorkshopTarget hit, out _) && hit == door;
            Check(state.report.doorRayResolved, "The visible cabinet door could not be selected by the actual first-collider ray query.");
            bool originallyOpen = door.IsDoorOpen;
            client.Environment.Activate(hit);
            Check(door.IsDoorOpen != originallyOpen, "The resolved door action did not toggle the door.");
            client.Environment.Activate(hit);
            state.report.doorAction = door.IsDoorOpen == originallyOpen;
            Check(state.report.doorAction, "The door did not return to its original state.");

            var part = targets.First(t => t.action == WorkshopAction.Movable && t.movableBody != null);
            Vector3 origin = part.transform.position + new Vector3(0, .3f, -.65f);
            client.Rig.SetDesktopView(origin, part.transform.position);
            Ray partRay = new Ray(origin, part.transform.position - origin);
            state.report.movableRayResolved = interaction.TryGetSceneTarget(partRay, out hit, out _) && hit == part;
            Check(state.report.movableRayResolved, "The loose part could not be selected by the first-collider ray query.");
            bool kinematic = part.movableBody.isKinematic, gravity = part.movableBody.useGravity;
            Check(interaction.BeginGrab(hit) && interaction.HeldObject == part && part.movableBody.isKinematic,
                "The visible nearby part could not be grabbed using the runtime method.");
            interaction.DropHeld();
            state.report.pickupDropped = interaction.HeldObject == null && part.movableBody.isKinematic == kinematic && part.movableBody.useGravity == gravity;
            Check(state.report.pickupDropped, "Dropping the part failed to restore its body state.");
        }

        static void CheckTeleportTransitions()
        {
            var teleport = client.Environment.Teleport;
            var process = typeof(WorkshopTeleport).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(process != null, "Teleport input loop was not found.");
            Ray ray = new Ray(new Vector3(-4.5f, 1.4f, -4f), Vector3.forward);
            Action<bool, bool, string> step = (pressed, allowed, reason) => process.Invoke(teleport, new object[] { pressed, allowed, reason, ray });
            int before = teleport.AimAttempts, moved = teleport.CompletedTeleports;
            // Inject the result of input-focus/guard validation, not a hardware pose or a fake lease.
            step(false, false, "Synthetic focus unavailable");
            step(true, true, "");
            Check(!teleport.IsAiming && teleport.AimAttempts == before, "Held pad on focus restore started aiming without release.");
            step(false, true, "");
            step(true, true, "");
            Check(teleport.IsAiming && teleport.AimAttempts == before + 1, "A released-then-pressed pad failed to start the real arc loop.");
            step(true, false, "Synthetic editing guard");
            state.report.blockedAimCancelled = !teleport.IsAiming && teleport.DiagnosticState.Contains("Synthetic editing guard");
            Check(state.report.blockedAimCancelled, "A new guard did not cancel aiming and expose its reason.");
            step(true, true, "");
            Check(!teleport.IsAiming, "Clearing the guard while the pad was held resumed aiming.");
            step(false, true, "");
            step(true, true, "");
            state.report.teleportReleaseLatch = teleport.IsAiming && teleport.AimAttempts == before + 2;
            Check(state.report.teleportReleaseLatch, "A fresh press after release did not rearm aiming.");
            step(false, false, "Synthetic test cleanup");
            Check(teleport.CompletedTeleports == moved, "The state transition test unexpectedly moved the rig.");
        }

        static void CheckCabinetGuidance()
        {
            var trainer=client.Environment.GetComponentInChildren<CabinetTrainer>();
            var part=trainer.parts[0]; var port=part.GetComponentsInChildren<CabinetPort>().First(p=>p.portId=="+");
            trainer.ResetExercise();
            client.Rig.SetDesktopView(part.transform.position+new Vector3(0,.1f,-.5f),part.transform.position);
            var interaction=client.Environment.Interaction;
            state.report.cabinetPortGrab=interaction.BeginGrab(port.GetComponent<WorkshopTarget>());
            Check(state.report.cabinetPortGrab,"Cabinet part could not be grabbed when aimed at its terminal.");
            part.Body.position=trainer.transform.TransformPoint(trainer.slots[0].position); part.Body.rotation=trainer.transform.rotation;
            trainer.GuideHeld(part); interaction.DropHeld(true);
            state.report.cabinetSnap=part.slot==0 && part.Body.isKinematic;
            Check(state.report.cabinetSnap,"Guided release failed to install component.");
            Physics.SyncTransforms();
            var ray=new Ray(port.transform.position+new Vector3(.012f,0,-.25f),Vector3.forward);
            state.report.assistedPortRay=interaction.TryGetSceneTarget(ray,out var target,out _) && target.GetComponent<CabinetPort>()==port;
            Check(state.report.assistedPortRay,"Live scene ray did not assist terminal selection.");
            client.Environment.Activate(target);
            state.report.guidedWireSelection=trainer.SelectedPort==port && trainer.Cable==CabinetCable.Copper075;
            Check(state.report.guidedWireSelection,"Resolved terminal did not select cable automatically.");
            trainer.UndoWire();
            var fuse=trainer.parts[2];fuse.Body.position=trainer.transform.TransformPoint(trainer.slots[2].position);fuse.Body.rotation=trainer.transform.rotation;
            Check(fuse.TrySnap(),"Fuse mount failed in tool interaction fixture.");
            var fuseIn=fuse.GetComponentsInChildren<CabinetPort>().First(p=>p.portId=="IN");
            Check(trainer.Connect(port,fuseIn,CabinetCable.Copper075),"Tool fixture conductor could not be laid.");Physics.SyncTransforms();
            foreach(var kind in new[]{CabinetToolKind.Stripper,CabinetToolKind.Crimper,CabinetToolKind.Screwdriver})
            {
                var tool=trainer.GetComponentsInChildren<CabinetTool>().First(t=>t.kind==kind);
                client.Rig.SetDesktopView(tool.transform.position+new Vector3(0,.15f,-.4f),tool.transform.position);
                Check(interaction.BeginGrab(tool.GetComponent<WorkshopTarget>()),"Could not pick up "+kind);
                var toolRay=new Ray(port.transform.position-Vector3.forward*.25f,Vector3.forward);
                Check(interaction.TryUseHeldTool(toolRay),"Held tool ray did not process terminal: "+kind);
                interaction.DropHeld();
            }
            state.report.cabinetToolSequence=true;
            client.Rig.SetDesktopView(part.transform.position+new Vector3(0,.1f,-.5f),part.transform.position);
            Check(interaction.BeginGrab(port.GetComponent<WorkshopTarget>()),"Mounted component could not be removed by its terminal.");
            interaction.DropHeld(); Check(part.slot<0,"Forced drop silently mounted component.");
            trainer.ResetExercise();
        }
        static void CheckSimulationGripper()
        {
            var root=new GameObject("Synthetic simulation gripper fixture");root.transform.position=new Vector3(-6,2,-5);
            var tcp=new GameObject("Fixture TCP").transform;tcp.SetParent(root.transform,false);
            var grip=root.AddComponent<SimulationGripper>();grip.tcp=tcp;grip.robot=root.transform;
            var obj=GameObject.CreatePrimitive(PrimitiveType.Cube);obj.name="Fixture workpiece";obj.transform.position=tcp.TransformPoint(new Vector3(-.008f,.1f,0));obj.transform.localScale=Vector3.one*.045f;
            var body=obj.AddComponent<Rigidbody>();body.mass=5;var target=obj.AddComponent<WorkshopTarget>();target.action=WorkshopAction.Movable;target.movableBody=body;Physics.SyncTransforms();
            var state=new ServerMessage{mode="simulation",connected=true,tool=new ToolPose{gripper=0}};
            grip.Observe(state);Check(grip.PayloadKg==0,"Overweight workpiece was grabbed.");
            body.mass=.4f;state.mode="rosbridge";grip.Observe(state);Check(grip.PayloadKg==0,"Physical mode triggered local simulation grasp.");
            state.mode="simulation";grip.Observe(state);Check(grip.PayloadKg==.4f&&body.isKinematic&&body.transform.parent==tcp,"Closing jaws failed to grasp the small workpiece.");
            Check(!client.Environment.Interaction.BeginGrab(target)&&client.Environment.Interaction.Hint.Contains("захвате робота"),"Hand pickup stole a part from closed robot jaws.");
            Vector3 before=obj.transform.position;tcp.position+=Vector3.right*.2f;Check(Vector3.Distance(before,obj.transform.position)>.19f,"Payload did not follow TCP.");
            state.tool.gripper=1;grip.Observe(state);Check(grip.PayloadKg==0&&!body.isKinematic&&body.useGravity,"Opening jaws did not release payload.");
            UnityEngine.Object.Destroy(obj);UnityEngine.Object.Destroy(root);
            WorkshopInteractionVerification.state.report.simulationGraspRelease=true;
        }

        static void Finish(Exception error)
        {
            if (finishing || state == null) return;
            finishing = true;
            state.report.success = error == null;
            state.report.error = error?.ToString();
            state.report.finishedUtc = DateTime.UtcNow.ToString("O");
            if (client?.Environment?.Interaction != null) client.Environment.Interaction.DropHeld();
            string path = Path.GetFullPath("Logs/interaction-verification.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(state.report, true));
            SessionState.EraseString(SessionKey);
            EditorApplication.update -= Update;
            if (error != null) Debug.LogError(error); else Debug.Log("Workshop interaction verification PASS: " + path);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}

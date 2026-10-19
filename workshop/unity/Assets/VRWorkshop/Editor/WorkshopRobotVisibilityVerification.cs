using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRWorkshop.Editor
{
    // Edit-mode geometry check against the saved workshop. No socket, XR session,
    // acquisition, or robot motion is started. The live fixture is server FK output.
    // Invoke -executeMethod VRWorkshop.Editor.WorkshopRobotVisibilityVerification.Run.
    // This method exits the batch editor itself; do not add -quit.
    public static class WorkshopRobotVisibilityVerification
    {
        [Serializable] sealed class Report
        {
            public bool success;
            public string scope = "Saved-scene mesh bounds, immediate neutral pose, first-hit selection rays, live FK pose and missing/stale telemetry; no headset render or real robot";
            public string error, finishedUtc;
            public double elapsedSeconds;
            public StationResult[] stations;
        }
        [Serializable] sealed class StationResult
        {
            public int station, meshCount, colliderCount;
            public Vector3 basePosition, meshMinimum, meshMaximum;
            public bool neutralVisibleBeforeTelemetry, selectionRayHit, liveFrameApplied;
            public bool invalidFramePreserved, disconnectedPosePreserved, agedPoseIsStale;
        }

        // Independent fixture: server forwardKinematics(training-arm-6,
        // [.2,.9,-1.2,0,.3,0]), in right-handed protocol coordinates.
        static ServerMessage LiveFixture() => new ServerMessage
        {
            type = "telemetry", connected = true, mode = "simulation",
            tool = new ToolPose
            {
                position = new Point3(.903057103f, .548968411f, -.183058738f),
                orientation = new[] { 0f, .099833417f, 0f, .995004165f }, gripper = .6f
            },
            links = new[]
            {
                new Point3(0, .1f, 0), new Point3(0, .1f, 0), new Point3(0, .35f, 0),
                new Point3(.255872045f, .678997302f, -.051867831f),
                new Point3(.574211788f, .578520432f, -.116398492f),
                new Point3(.667841125f, .548968411f, -.135378098f),
                new Point3(.765847783f, .548968411f, -.155245031f),
                new Point3(.903057103f, .548968411f, -.183058738f)
            }
        };

        public static void Run()
        {
            var report = new Report();
            var results = new List<StationResult>();
            var created = new List<GameObject>();
            double started = EditorApplication.timeSinceStartup;
            try
            {
                Check(!Application.isPlaying, "This geometry check must run in edit mode.");
                EditorSceneManager.OpenScene(WorkshopBuild.ScenePath, OpenSceneMode.Single);
                var environment = UnityEngine.Object.FindFirstObjectByType<WorkshopEnvironment>();
                Check(environment != null, "Saved workshop has no environment.");
                Check(Vector3.Distance(environment.GetRobotBase(0).position, environment.GetRobotBase(1).position) > 2,
                    "The two robot bases occupy the same station.");
                for (int index = 0; index < 2; ++index)
                {
                    var result = new StationResult { station = index, basePosition = environment.GetRobotBase(index).position };
                    results.Add(result);
                    var robot = new GameObject("Visibility fixture robot " + index); created.Add(robot);
                    robot.transform.SetParent(environment.GetRobotBase(index), false);
                    var target = robot.AddComponent<WorkshopTarget>();
                    target.action = WorkshopAction.SelectRobot; target.stationIndex = index;
                    var view = robot.AddComponent<RobotView>(); view.Initialize();
                    Physics.SyncTransforms();
                    var bounds = MeshBounds(view, out result.meshCount);
                    result.meshMinimum = bounds.min; result.meshMaximum = bounds.max;
                    Check(result.meshCount > 100, "Station " + index + " has only a base/tool, not a full arm.");
                    Check(bounds.size.y > .65f && Mathf.Max(bounds.size.x, bounds.size.z) > 1,
                        "Station " + index + " robot geometry is collapsed.");
                    Check(bounds.min.y >= result.basePosition.y - .05f && bounds.max.y > result.basePosition.y + .7f,
                        "Station " + index + " arm is below its base or floor.");
                    Check(bounds.size.x < 1.5f && bounds.size.y < 1.1f && bounds.size.z < 1.5f,
                        "Robot metre coordinates were unexpectedly scaled.");
                    // The display name contains '/', which Transform.Find treats as
                    // a hierarchy separator. Match the direct child name literally.
                    Transform tool = DirectChild(robot.transform, "Server TCP / industrial parallel gripper");
                    Check(tool != null && tool.gameObject.activeInHierarchy &&
                        Vector3.Distance(tool.localPosition, new Vector3(.921424242f, .548968411f, 0)) < .00001f,
                        "Neutral TCP was not positioned immediately before the first physics tick.");
                    Check(!view.HasTelemetryPose && !view.IsTelemetryFresh && view.PoseStatus.Contains("нет телеметрии"),
                        "Offline geometry was represented as measured telemetry.");
                    var caption = robot.transform.Find("Robot pose source");
                    Check(caption != null && caption.gameObject.activeSelf, "Offline model caption is hidden.");
                    result.neutralVisibleBeforeTelemetry = true;
                    foreach (var collider in robot.GetComponentsInChildren<Collider>())
                    {
                        if (!collider.enabled) continue;
                        result.colliderCount++;
                        Check(collider.GetComponentInParent<WorkshopTarget>() == target, "Robot collision body cannot resolve its station target.");
                    }
                    Check(result.colliderCount >= 12, "Manipulator selection/contact colliders are missing.");
                    // Aim from the open front of each cell. The side teleport pads
                    // are behind a solid physical fence and are a separate UX issue.
                    var elbow = robot.transform.Find("Axis 3 gearbox");
                    Vector3 origin = result.basePosition + new Vector3(0, 1.05f, -1.7f);
                    var ray = new Ray(origin, elbow.position - origin);
                    Check(Physics.Raycast(ray, out var hit, 4, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore),
                        "Selection ray missed all geometry at station " + index + ".");
                    Check(hit.collider.GetComponentInParent<WorkshopTarget>() == target,
                        "Selection ray was intercepted by " + hit.collider.name + " at station " + index + ".");
                    result.selectionRayHit = true;

                    var frame = LiveFixture(); view.Apply(frame);
                    Check(view.HasTelemetryPose && view.IsTelemetryFresh && !caption.gameObject.activeSelf,
                        "Complete server pose did not replace the offline source status.");
                    Check(Vector3.Distance(tool.localPosition, Coordinates.ToUnity(frame.tool.position)) < .00001f &&
                        Quaternion.Angle(tool.localRotation, Coordinates.ToUnity(frame.tool.orientation)) < .01f,
                        "First live TCP was not applied in the station coordinate frame.");
                    Check(Vector3.Distance(tool.position, robot.transform.TransformPoint(Coordinates.ToUnity(frame.tool.position))) < .00001f,
                        "Mirrored workstation transformed the server TCP incorrectly.");
                    result.liveFrameApplied = true;
                    Vector3 lastPosition = tool.position;
                    Bounds liveBounds = MeshBounds(view, out int liveMeshes);
                    view.Apply(new ServerMessage { type = "telemetry", connected = true });
                    Check(!view.IsTelemetryFresh && caption.gameObject.activeSelf && view.PoseStatus.Contains("Последняя поза"),
                        "Incomplete telemetry was incorrectly marked fresh.");
                    Check(Vector3.Distance(tool.position, lastPosition) < .00001f && SameBounds(liveBounds, MeshBounds(view, out int invalidMeshes)) && invalidMeshes == liveMeshes,
                        "Missing telemetry hid or repositioned the arm.");
                    result.invalidFramePreserved = true;
                    view.Apply(frame); view.SetTelemetryAvailable(false);
                    Check(!view.IsTelemetryFresh && caption.gameObject.activeSelf && Vector3.Distance(tool.position, lastPosition) < .00001f,
                        "Disconnected last pose disappeared or remained marked fresh.");
                    result.disconnectedPosePreserved = true;
                    view.Apply(frame);
                    // Expire only the receipt timestamp to verify the actual age gate
                    // without pausing the editor or synthesizing a telemetry frame.
                    typeof(RobotView).GetField("poseReceivedAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .SetValue(view, Time.realtimeSinceStartupAsDouble - 1);
                    view.SetTelemetryAvailable(true);
                    Check(!view.IsTelemetryFresh && caption.gameObject.activeSelf, "Old pose remained visually fresh.");
                    result.agedPoseIsStale = true;
                }
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogError(report.error); }
            finally { foreach (var item in created) if (item != null) UnityEngine.Object.DestroyImmediate(item); }
            report.stations = results.ToArray();
            report.elapsedSeconds = EditorApplication.timeSinceStartup - started;
            report.finishedUtc = DateTime.UtcNow.ToString("O");
            string logs = Path.Combine(Directory.GetCurrentDirectory(), "Logs"); Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "robot-visibility-verification.json"), JsonUtility.ToJson(report, true));
            Debug.Log("Robot visibility verification " + (report.success ? "PASS" : "FAIL"));
            EditorApplication.Exit(report.success ? 0 : 1);
        }

        static Transform DirectChild(Transform parent, string name)
        {
            foreach (Transform child in parent) if (child.name == name) return child;
            return null;
        }
        static bool SameBounds(Bounds a, Bounds b) => Vector3.Distance(a.min, b.min) < .00001f && Vector3.Distance(a.max, b.max) < .00001f;
        static Bounds MeshBounds(RobotView view, out int count)
        {
            count = 0; Bounds bounds = default;
            foreach (var mesh in view.GetComponentsInChildren<MeshRenderer>())
            {
                if (!mesh.enabled || mesh.GetComponent<TextMesh>() != null) continue;
                if (count++ == 0) bounds = mesh.bounds; else bounds.Encapsulate(mesh.bounds);
            }
            return bounds;
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}

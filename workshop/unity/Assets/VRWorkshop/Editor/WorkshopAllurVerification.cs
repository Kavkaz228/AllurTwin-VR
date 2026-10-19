using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace VRWorkshop.Editor
{
    public static class WorkshopAllurVerification
    {
        [Serializable] sealed class Report
        {
            public bool success, omittedFieldInstantiatesObject, presentAccepted, absentRetained, unavailableAccepted, staleRejected;
            public string error, finishedUtc;
        }
        public static void Run()
        {
            var report = new Report();
            var root = new GameObject("Allur optional DTO verification"); root.SetActive(false);
            try
            {
                var client = root.AddComponent<WorkshopClient>();
                var accept = typeof(WorkshopClient).GetMethod("AcceptAllur", BindingFlags.Instance | BindingFlags.NonPublic);
                var present = JsonUtility.FromJson<ServerMessage>("{\"type\":\"telemetry\",\"allur\":{\"available\":true,\"generatedAtUnixMs\":1700000000000,\"source\":\"vr-simulation\",\"robotInsights\":[{\"robotId\":\"training-arm\",\"status\":\"idle\",\"cycles\":4,\"condition\":\"fixture observation\"}]}}");
                accept.Invoke(client, new object[] { present.allur, 1d });
                Need(client.Allur == present.allur && client.Allur.robotInsights.Length == 1, "Explicit analytical observation was not accepted.");
                report.presentAccepted = true;
                var omitted = JsonUtility.FromJson<ServerMessage>("{\"type\":\"telemetry\",\"robotId\":\"training-arm\",\"sequence\":20}");
                report.omittedFieldInstantiatesObject = omitted.allur != null;
                for (int i = 0; i < 100; i++) accept.Invoke(client, new object[] { omitted.allur, 2d });
                Need(client.Allur == present.allur && client.Allur.robotInsights.Length == 1, "Ordinary telemetry erased the last analytical snapshot.");
                report.absentRetained = true;
                var unavailable = JsonUtility.FromJson<ServerMessage>("{\"type\":\"telemetry\",\"allur\":{\"available\":false,\"generatedAtUnixMs\":0,\"source\":\"AllurTwin\",\"summary\":\"Offline\",\"robotInsights\":[]}}");
                accept.Invoke(client, new object[] { unavailable.allur, 3d });
                Need(client.Allur == unavailable.allur && !client.Allur.available, "Explicit unavailable state must replace a previously healthy observation.");
                report.unavailableAccepted = true;
                Need(present.allur.Fresh(1700000000000) && !present.allur.Fresh(1700000005001) && !present.allur.Fresh(1699999998999), "Stale or future analytics was accepted as fresh.");
                report.staleRejected = true; report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                string logs = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs")); Directory.CreateDirectory(logs);
                report.finishedUtc = DateTime.UtcNow.ToString("O");
                File.WriteAllText(Path.Combine(logs, "allur-dto-verification.json"), JsonUtility.ToJson(report, true));
            }
            if (!report.success) throw new InvalidOperationException(report.error);
        }
        static void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    }
}

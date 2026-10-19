using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VRWorkshop.Editor
{
    // Edit-mode lifecycle regression. The transport is a real loopback WebSocket;
    // robot/ownership states are explicit fixtures. No scene, XR session or robot runs.
    // Invoke -executeMethod VRWorkshop.Editor.WorkshopFocusVerification.Run without -quit.
    public static class WorkshopFocusVerification
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] sealed class Report
        {
            public bool success, readonlyConnectionSurvived, readonlyEchoAfterFocusLoss;
            public bool acquireBlocked, productionStartBlocked, productionResetBlocked, cabinetBlocked;
            public bool pauseDisconnected, focusReturnDidNotReconnect;
            public bool pendingAcquireCancelled, lateGrantReleased, pendingAcquireBlocksAuto, scadaWaitingIsNotFault;
            public bool explicitReconnectUsesSessionToken, reconnectDoesNotAcquire, tokenBoundToEndpoint;
            public string scope = "Edit-mode focus/pause callbacks with injected ownership fixtures and a real local WebSocket echo; no robot motion or XR hardware";
            public string error, finishedUtc;
            public string[] disconnectedCases;
            public double elapsedSeconds;
        }

        public static async void Run()
        {
            var report = new Report();
            var completed = new List<string>();
            double started = EditorApplication.timeSinceStartup;
            try
            {
                if (EditorApplication.isPlaying) throw new InvalidOperationException("Run this check in edit mode.");
                var waiting = new ServerMessage { connected = true, motionKnown = true, stopReason = "Awaiting operator enable" };
                var label = typeof(WorkshopUI).GetMethod("RobotStateLabel", BindingFlags.Static | BindingFlags.NonPublic);
                Check((string)label.Invoke(null, new object[] { true, true, waiting }) == "Ожидание команды", "Normal enable wait appeared as a fault.");
                waiting.fault = "Stopped by operator";
                Check((string)label.Invoke(null, new object[] { true, true, waiting }) == "Требуется сброс", "Latched stop was not shown as requiring reset.");
                Check((string)label.Invoke(null, new object[] { true, false, waiting }) == "Данные устарели", "Stale telemetry appeared current.");
                report.scadaWaitingIsNotFault = true;
                using (var fixture = new ClientFixture())
                {
                    await fixture.Connect();
                    fixture.Client.Acquire();
                    string acquireId = Get<string>(fixture.Client, "pendingAcquire");
                    Check(!string.IsNullOrEmpty(acquireId), "No pending acquire created.");
                    fixture.Client.StartProduction();
                    await fixture.Probe("after-pending-acquire");
                    foreach (string message in fixture.Server.Messages)
                        Check(!message.Contains("production.start"), "AUTO was sent during a pending manual acquire.");
                    report.pendingAcquireBlocksAuto = true;
                    fixture.Client.Release();
                    Check(!fixture.Client.HasPendingManualControl && !fixture.Client.HasControl, "Release did not cancel pending acquire.");
                    report.pendingAcquireCancelled = true;
                    typeof(WorkshopClient).GetMethod("Handle", Private).Invoke(fixture.Client, new object[] {
                        new ServerMessage { type = "ack", requestId = acquireId, ok = true, leaseId = "late-fixture-lease" }, RobotSocket.MonotonicSeconds });
                    await fixture.Probe("after-cancelled-grant");
                    bool released = false;
                    foreach (string message in fixture.Server.Messages)
                    {
                        var sent = JsonUtility.FromJson<ClientMessage>(message);
                        if (sent.type == "release" && sent.leaseId == "late-fixture-lease" && sent.robotId == "fixture-robot") released = true;
                        Check(sent.type != "command", "Cancelled acquisition emitted motion.");
                    }
                    Check(released && !fixture.Client.HasControl, "A delayed grant was retained instead of released.");
                    report.lateGrantReleased = true;
                }
                using (var fixture = new ClientFixture())
                {
                    await fixture.Connect();
                    int generation = fixture.Socket.Generation;
                    var telemetry = fixture.Client.Telemetry;
                    Invoke(fixture.Client, "OnApplicationFocus", false);
                    Check(fixture.Client.Ready && ReferenceEquals(telemetry, fixture.Client.Telemetry) &&
                        fixture.Socket.Online && fixture.Socket.Generation == generation && !fixture.Client.HasControl,
                        "Passive telemetry connection was lost on focus loss.");
                    report.readonlyConnectionSurvived = true;
                    await fixture.Probe("after-focus-loss");
                    report.readonlyEchoAfterFocusLoss = true;

                    var requests = Get<Dictionary<string, string>>(fixture.Client, "requests");
                    fixture.Client.Acquire();
                    Check(!fixture.Client.HasControl && Get<string>(fixture.Client, "pendingAcquire") == null && requests.Count == 0, "Acquire was not blocked.");
                    report.acquireBlocked = true;
                    fixture.Client.StartProduction();
                    Check(fixture.Client.ProductionId == null && requests.Count == 0, "Production start was not blocked.");
                    report.productionStartBlocked = true;
                    fixture.Client.ResetProduction();
                    Check(requests.Count == 0, "Production reset was not blocked.");
                    report.productionResetBlocked = true;
                    fixture.Client.SelectCabinet(0);
                    Check(Get<string>(fixture.Client, "pendingCabinetRobot") == null && requests.Count == 0, "Cabinet acquisition was not blocked.");
                    report.cabinetBlocked = true;
                    await fixture.Probe("after-blocked-actions");
                    foreach (string message in fixture.Server.Messages)
                        Check(message.Contains("\"type\":\"auth\"") || message.Contains("\"probe\":"), "An unfocused action reached the fixture: " + message);

                    Invoke(fixture.Client, "OnApplicationPause", true);
                    CheckDisconnected(fixture, generation);
                    report.pauseDisconnected = true;
                    int pausedGeneration = fixture.Socket.Generation;
                    Invoke(fixture.Client, "OnApplicationFocus", true);
                    Check(!fixture.Client.Ready && !fixture.Client.HasControl && fixture.Socket.Generation == pausedGeneration,
                        "Focus return resumed a disconnected session.");
                    report.focusReturnDidNotReconnect = true;
                }

                var cases = new KeyValuePair<string, Action<WorkshopClient>>[]
                {
                    Pair("active lease", c => SetProperty(c, "LeaseId", "fixture-lease")),
                    Pair("driving", c => SetProperty(c, "Driving", true)),
                    Pair("pending acquire", c => Set(c, "pendingAcquire", "fixture-acquire")),
                    Pair("pending cabinet", c => Set(c, "pendingCabinetRobot", "fixture-robot")),
                    Pair("AUTO ownership", c => SetProperty(c, "ProductionId", "fixture-production")),
                    Pair("pending AUTO start", c => Get<Dictionary<string, string>>(c, "requests").Add("fixture-start", "production.start")),
                    Pair("pending AUTO reset", c => Get<Dictionary<string, string>>(c, "requests").Add("fixture-reset", "production.reset"))
                };
                foreach (var item in cases)
                {
                    using (var fixture = new ClientFixture())
                    {
                        await fixture.Connect();
                        int generation = fixture.Socket.Generation;
                        item.Value(fixture.Client);
                        Invoke(fixture.Client, "OnApplicationFocus", false);
                        CheckDisconnected(fixture, generation);
                        completed.Add(item.Key);
                    }
                }
                string savedAddress = PlayerPrefs.GetString("AllurTwinVR.Server", "");
                string savedName = PlayerPrefs.GetString("AllurTwinVR.ClientName", "");
                bool hadAddress = PlayerPrefs.HasKey("AllurTwinVR.Server"), hadName = PlayerPrefs.HasKey("AllurTwinVR.ClientName");
                try
                {
                    using (var fixture = new ClientFixture())
                    {
                        fixture.Client.Connect(fixture.Server.Url, "reconnect-fixture-token", "Reconnect fixture");
                        await Wait(() => fixture.Socket.Online, "Initial explicit connection failed.");
                        await fixture.Probe("initial-token");
                        SetProperty(fixture.Client, "Ready", true);
                        SetProperty(fixture.Client, "LeaseId", "fixture-lease");
                        Invoke(fixture.Client, "OnApplicationFocus", false);
                        int stoppedGeneration = fixture.Socket.Generation;
                        Invoke(fixture.Client, "OnApplicationFocus", true);
                        Check(!fixture.Socket.Online && fixture.Socket.Generation == stoppedGeneration, "Focus return connected automatically.");
                        Check(fixture.Client.CanReconnect, "Session credential was lost on focus stop.");
                        fixture.Client.Connect(fixture.Server.Url + "/other", "", "Reconnect fixture");
                        Check(fixture.Socket.Generation == stoppedGeneration && !fixture.Socket.Online,
                            "Stored credential was reused for a different endpoint.");
                        report.tokenBoundToEndpoint = true;
                        fixture.Client.Reconnect();
                        await Wait(() => fixture.Socket.Online, "Explicit reconnect failed.");
                        await fixture.Probe("reconnected-token");
                        int authenticated = 0;
                        foreach (string json in fixture.Server.Messages)
                        {
                            var message = JsonUtility.FromJson<ClientMessage>(json);
                            if (message.type == "auth")
                            {
                                Check(message.accessToken == "reconnect-fixture-token", "Reconnect did not use the session token.");
                                authenticated++;
                            }
                            Check(message.type != "acquire" && message.type != "command" && message.type != "production.start",
                                "Reconnection resumed control without operator action.");
                        }
                        Check(authenticated == 2 && !fixture.Client.HasControl && !fixture.Client.Driving,
                            "Expected two authenticated transports and no restored control.");
                        report.explicitReconnectUsesSessionToken = report.reconnectDoesNotAcquire = true;
                    }
                }
                finally
                {
                    if (hadAddress) PlayerPrefs.SetString("AllurTwinVR.Server", savedAddress); else PlayerPrefs.DeleteKey("AllurTwinVR.Server");
                    if (hadName) PlayerPrefs.SetString("AllurTwinVR.ClientName", savedName); else PlayerPrefs.DeleteKey("AllurTwinVR.ClientName");
                    PlayerPrefs.Save();
                }
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = (error is TargetInvocationException invocation ? invocation.InnerException ?? error : error).ToString();
                Debug.LogError("Focus verification failed: " + report.error);
            }
            report.disconnectedCases = completed.ToArray();
            report.elapsedSeconds = EditorApplication.timeSinceStartup - started;
            report.finishedUtc = DateTime.UtcNow.ToString("O");
            string logs = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "focus-verification.json"), JsonUtility.ToJson(report, true));
            Debug.Log("Focus verification " + (report.success ? "PASS" : "FAIL"));
            EditorApplication.Exit(report.success ? 0 : 1);
        }

        static KeyValuePair<string, Action<WorkshopClient>> Pair(string name, Action<WorkshopClient> action) => new KeyValuePair<string, Action<WorkshopClient>>(name, action);
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value });
        static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
        static void CheckDisconnected(ClientFixture fixture, int originalGeneration)
        {
            Check(!fixture.Client.Ready && !fixture.Socket.Online && fixture.Socket.Generation > originalGeneration,
                "Control/pause event failed to cancel the WebSocket session.");
            Check(!fixture.Client.HasControl && !fixture.Client.Driving && fixture.Client.ProductionId == null && fixture.Client.Telemetry == null,
                "Ownership or telemetry survived a required disconnect.");
            Check(Get<string>(fixture.Client, "pendingAcquire") == null && Get<string>(fixture.Client, "pendingCabinetRobot") == null &&
                Get<bool>(fixture.Client, "releaseRequired"), "Pending control or deadman latch was not cleared.");
        }

        sealed class ClientFixture : IDisposable
        {
            readonly GameObject root;
            public readonly WorkshopClient Client;
            public readonly RobotSocket Socket;
            public readonly EchoFixture Server;
            public ClientFixture()
            {
                root = new GameObject("Focus lifecycle fixture"); root.SetActive(false);
                Client = root.AddComponent<WorkshopClient>();
                Check(Client.Rig == null, "Unexpected Awake execution: this fixture must not initialize a scene or XR.");
                var view = root.AddComponent<RobotView>();
                var marker = new GameObject("Fixture target marker"); marker.transform.SetParent(root.transform);
                Set(view, "target", marker.transform); Set(Client, "robotView", view);
                SetProperty(Client, "RobotId", "fixture-robot");
                Set(Client, "focused", true); Set(Client, "applicationPaused", false);
                Socket = Get<RobotSocket>(Client, "socket");
                Server = new EchoFixture();
            }
            public async Task Connect()
            {
                Socket.Connect(Server.Url, "{\"type\":\"auth\",\"accessToken\":\"local-test-only\"}");
                await Wait(() => Socket.Online, "Loopback connection did not open.");
                SetProperty(Client, "Ready", true);
                SetProperty(Client, "Telemetry", new ServerMessage { type = "telemetry", robotId = "fixture-robot", connected = true, sequence = 1 });
                await Probe("before-focus-loss");
            }
            public async Task Probe(string id)
            {
                string json = "{\"probe\":\"" + id + "\"}";
                Socket.Queue(json);
                await Wait(() =>
                {
                    while (Socket.Received.TryDequeue(out var incoming)) if (incoming.json == json) return true;
                    return false;
                }, "A WebSocket echo was not received: " + id);
            }
            public void Dispose()
            {
                Socket.Dispose(); Server.Dispose();
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        static async Task Wait(Func<bool> condition, string message)
        {
            double deadline = RobotSocket.MonotonicSeconds + 4;
            while (!condition())
            {
                if (RobotSocket.MonotonicSeconds > deadline) throw new TimeoutException(message);
                await Task.Delay(10);
            }
        }

        sealed class EchoFixture : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly CancellationTokenSource stop = new CancellationTokenSource();
            readonly Task serving;
            TcpClient peer;
            WebSocket webSocket;
            public readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
            public string Url { get; }
            public EchoFixture()
            {
                listener.Start(1);
                Url = "ws://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/fixture";
                serving = Serve();
            }
            async Task Serve()
            {
                while (!stop.IsCancellationRequested)
                {
                try
                {
                    peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    using (peer)
                    using (var stream = peer.GetStream())
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                    {
                        timeout.CancelAfter(3000);
                        var request = new StringBuilder(); byte[] one = new byte[1];
                        while (request.Length < 8192 && !request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (await stream.ReadAsync(one, 0, 1, timeout.Token).ConfigureAwait(false) == 0) throw new IOException("Handshake ended early.");
                            request.Append((char)one[0]);
                        }
                        string key = null;
                        foreach (string line in request.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(line.IndexOf(':') + 1).Trim();
                        if (key == null) throw new IOException("No WebSocket key in loopback handshake.");
                        string accept;
                        using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                        await stream.WriteAsync(response, 0, response.Length, timeout.Token).ConfigureAwait(false);
                        using (webSocket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20)))
                        {
                            byte[] buffer = new byte[4096];
                            while (!stop.IsCancellationRequested)
                            {
                                var frame = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token).ConfigureAwait(false);
                                if (frame.MessageType == WebSocketMessageType.Close) break;
                                if (!frame.EndOfMessage || frame.MessageType != WebSocketMessageType.Text) throw new IOException("Unexpected fixture frame.");
                                string value = Encoding.UTF8.GetString(buffer, 0, frame.Count); Messages.Enqueue(value);
                                await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, frame.Count), WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception error) when (stop.IsCancellationRequested || error is WebSocketException || error is IOException || error is ObjectDisposedException) { }
                }
            }
            public void Dispose()
            {
                stop.Cancel(); listener.Stop(); webSocket?.Abort(); peer?.Close();
                // All I/O is cancelled above; observe task completion without blocking the editor.
                _ = serving.ContinueWith(task => { _ = task.Exception; stop.Dispose(); }, TaskScheduler.Default);
            }
        }
    }
}

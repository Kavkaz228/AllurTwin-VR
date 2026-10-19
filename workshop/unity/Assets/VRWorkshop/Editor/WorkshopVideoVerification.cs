using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRWorkshop.Editor
{
    // Batch entry WITHOUT -quit. Uses only an ephemeral loopback HTTP fixture.
    // No system webcam, configured user URL, robot, or external service is opened.
    [InitializeOnLoad]
    public static class WorkshopVideoVerification
    {
        const string SessionKey = "VRWorkshop.VideoVerification";
        enum Stage { EnterPlay, MjpegLength, MjpegBoundary, Snapshot, Stale, Virtual, Finish }
        [Serializable]
        sealed class State
        {
            public Stage stage;
            public double started, stageStarted;
            public long framesBefore;
            public Report report = new Report();
        }
        [Serializable]
        sealed class Report
        {
            public bool success, jpegDecoded, mjpegWithLength, mjpegWithoutLength, snapshots, oldFrameClearedOnSwitch, interruptedFeedCleared, virtualSelected, virtualRenderTextureCreated;
            public string startedUtc, finishedUtc, failedStage, error, graphicsDevice;
            public string scope = "Unity WorkshopVideo receives/decode real loopback HTTP JPEG/MJPEG; latest frame switching, stale/error blanking; virtual render target";
            public string unverified = "Physical USB/IP camera, RTSP, external network, real workshop capture latency, VIVE display";
            public int decodedWidth, decodedHeight, fixtureRequests;
            public long displayedFrames;
            public double elapsedSeconds;
        }
        static State state;
        static WorkshopVideo video;
        static LoopbackCamera fixture;
        static bool finishing;

        static WorkshopVideoVerification()
        {
            var saved = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            state = JsonUtility.FromJson<State>(saved);
            Subscribe();
        }

        public static void Run()
        {
            if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run video verification from batch edit mode without -quit.");
            state = new State { stage = Stage.EnterPlay, started = EditorApplication.timeSinceStartup, stageStarted = EditorApplication.timeSinceStartup };
            state.report.startedUtc = DateTime.UtcNow.ToString("O");
            Save(); Subscribe();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }
        static void Subscribe() { EditorApplication.update -= Update; EditorApplication.update += Update; }
        static void Save() => SessionState.SetString(SessionKey, JsonUtility.ToJson(state));
        static void Next(Stage stage)
        {
            state.stage = stage; state.stageStarted = EditorApplication.timeSinceStartup;
            state.framesBefore = video?.DisplayedFrames ?? 0; Save();
        }

        static void StartFixture()
        {
            var pixels = new Texture2D(64, 48, TextureFormat.RGB24, false);
            var colors = new Color[64 * 48];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color(.12f, .8f, .22f);
            pixels.SetPixels(colors); pixels.Apply();
            byte[] jpeg = ImageConversion.EncodeToJPG(pixels, 85);
            UnityEngine.Object.DestroyImmediate(pixels);
            fixture = new LoopbackCamera(jpeg);
            var root = new GameObject("Video fixture screen").transform;
            var focus = new GameObject("Video fixture focus").transform;
            var options = new VideoSettings
            {
                initialSource = 0, staleAfterSeconds = .35f, requestTimeoutMilliseconds = 1500,
                sources = new[]
                {
                    new VideoSourceSettings { name = "Virtual fixture", kind = "virtual", width = 160, height = 120, framesPerSecond = 10 },
                    new VideoSourceSettings { name = "Loopback MJPEG", kind = "mjpeg", url = fixture.Url + "/live", framesPerSecond = 20 },
                    new VideoSourceSettings { name = "Loopback snapshots", kind = "snapshot", url = fixture.Url + "/snapshot", framesPerSecond = 20 }
                }
            };
            video = new GameObject("Video verification").AddComponent<WorkshopVideo>();
            video.Initialize(root, focus, options);
            video.SelectSource(1);
            if (video.Texture != null || video.IsFresh) throw new Exception("Switch to network retained virtual pixels.");
            state.report.graphicsDevice = SystemInfo.graphicsDeviceType.ToString();
        }

        static bool Decoded(int minimumFrames = 2)
        {
            if (!video.IsFresh || video.DisplayedFrames - state.framesBefore < minimumFrames) return false;
            if (!(video.Texture is Texture2D texture) || texture.width != 64 || texture.height != 48) throw new Exception("Incoming image was not decoded as 64x48 JPEG.");
            Color pixel = texture.GetPixel(20, 20);
            if (pixel.g < .5f || pixel.g <= pixel.r || pixel.g <= pixel.b) throw new Exception("Decoded pixels do not match loopback JPEG.");
            state.report.jpegDecoded = true; state.report.decodedWidth = texture.width; state.report.decodedHeight = texture.height;
            return true;
        }

        static void Update()
        {
            if (state == null || finishing) return;
            try
            {
                if (EditorApplication.timeSinceStartup - state.started > 30) throw new TimeoutException("Video verification exceeded 30 seconds: " + video?.Status);
                EditorApplication.QueuePlayerLoopUpdate();
                if (!EditorApplication.isPlaying) return;
                switch (state.stage)
                {
                    case Stage.EnterPlay:
                        StartFixture(); Next(Stage.MjpegLength); break;
                    case Stage.MjpegLength:
                        if (!Decoded(3)) break;
                        state.report.mjpegWithLength = true;
                        video.SetNetworkUrl(1, fixture.Url + "/boundary");
                        if (video.Texture != null || video.IsFresh) throw new Exception("Source restart retained an old network frame.");
                        state.report.oldFrameClearedOnSwitch = true;
                        Next(Stage.MjpegBoundary); break;
                    case Stage.MjpegBoundary:
                        if (!Decoded(3)) break;
                        state.report.mjpegWithoutLength = true;
                        video.SelectSource(2);
                        if (video.Texture != null || video.IsFresh) throw new Exception("Switch to snapshots retained the previous MJPEG frame.");
                        Next(Stage.Snapshot); break;
                    case Stage.Snapshot:
                        if (!Decoded()) break;
                        state.report.snapshots = true;
                        fixture.Dispose();
                        Next(Stage.Stale); break;
                    case Stage.Stale:
                        if (video.IsFresh || video.Texture != null) break;
                        if (video.State != "ERROR" && video.State != "STALE") throw new Exception("Disconnected source has no error/stale status.");
                        state.report.interruptedFeedCleared = true;
                        video.SelectSource(0);
                        Next(Stage.Virtual); break;
                    case Stage.Virtual:
                        if (video.SourceKind != "virtual") throw new Exception("Virtual source selection failed.");
                        state.report.virtualSelected = true;
                        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                        {
                            if (!(video.Texture is RenderTexture target) || !target.IsCreated() || !video.IsFresh) break;
                            state.report.virtualRenderTextureCreated = true;
                        }
                        Finish(true, ""); break;
                }
                if (video != null && video.State == "ERROR" && state.stage != Stage.Stale) throw new Exception(video.Status);
            }
            catch (Exception error) { Finish(false, error.Message); }
        }

        static void Finish(bool success, string error)
        {
            if (finishing) return;
            finishing = true;
            EditorApplication.update -= Update;
            state.report.success = success; state.report.error = error;
            state.report.failedStage = success ? "" : state.stage.ToString();
            state.report.finishedUtc = DateTime.UtcNow.ToString("O");
            state.report.elapsedSeconds = EditorApplication.timeSinceStartup - state.started;
            state.report.displayedFrames = video?.DisplayedFrames ?? 0;
            state.report.fixtureRequests = fixture?.Requests ?? 0;
            if (video != null) UnityEngine.Object.DestroyImmediate(video.gameObject);
            fixture?.Dispose(); fixture = null;
            string logDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "video-verification.json"), JsonUtility.ToJson(state.report, true));
            SessionState.EraseString(SessionKey);
            Debug.Log("Video verification " + (success ? "PASS" : "FAIL: " + error));
            EditorApplication.Exit(success ? 0 : 1);
        }

        sealed class LoopbackCamera : IDisposable
        {
            readonly TcpListener listener;
            readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            readonly ConcurrentDictionary<int, TcpClient> clients = new ConcurrentDictionary<int, TcpClient>();
            readonly byte[] jpeg;
            int serial, requests, disposed;
            public int Requests => Volatile.Read(ref requests);
            public string Url { get; }
            public LoopbackCamera(byte[] jpeg)
            {
                this.jpeg = jpeg;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(8);
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                _ = Task.Run(Accept);
            }
            async Task Accept()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        int id = Interlocked.Increment(ref serial);
                        clients[id] = client;
                        _ = Serve(client, id);
                    }
                }
                catch (Exception) { }
            }
            async Task Serve(TcpClient client, int id)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var header = new MemoryStream();
                        var single = new byte[1];
                        while (header.Length < 4096)
                        {
                            int size = await stream.ReadAsync(single, 0, 1, lifetime.Token).ConfigureAwait(false);
                            if (size == 0) return;
                            header.WriteByte(single[0]);
                            var bytes = header.GetBuffer(); int n = (int)header.Length;
                            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                        }
                        string request = Encoding.ASCII.GetString(header.ToArray());
                        if (!request.StartsWith("GET /", StringComparison.Ordinal)) return;
                        Interlocked.Increment(ref requests);
                        bool snapshot = request.StartsWith("GET /snapshot ", StringComparison.Ordinal);
                        bool noLength = request.StartsWith("GET /boundary ", StringComparison.Ordinal);
                        string response = snapshot
                            ? "HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpeg.Length + "\r\nConnection: close\r\n\r\n"
                            : "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=fixture-frame\r\nConnection: close\r\n\r\n";
                        await Write(stream, Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
                        if (snapshot) { await Write(stream, jpeg).ConfigureAwait(false); return; }
                        while (!lifetime.IsCancellationRequested)
                        {
                            string part = "--fixture-frame\r\nContent-Type: image/jpeg\r\n" + (noLength ? "" : "Content-Length: " + jpeg.Length + "\r\n") + "\r\n";
                            await Write(stream, Encoding.ASCII.GetBytes(part)).ConfigureAwait(false);
                            await Write(stream, jpeg).ConfigureAwait(false);
                            await Write(stream, new byte[] { 13, 10 }).ConfigureAwait(false);
                            await Task.Delay(55, lifetime.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception) { }
                finally { clients.TryRemove(id, out _); }
            }
            async Task Write(NetworkStream stream, byte[] bytes)
            {
                // Deliberately split MIME/JPEG bytes independently of frame boundaries.
                int first = Math.Min(11, bytes.Length);
                await stream.WriteAsync(bytes, 0, first, lifetime.Token).ConfigureAwait(false);
                if (bytes.Length > first) await stream.WriteAsync(bytes, first, bytes.Length - first, lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lifetime.Cancel(); listener.Stop();
                foreach (var pair in clients) pair.Value.Close();
            }
        }
    }
}

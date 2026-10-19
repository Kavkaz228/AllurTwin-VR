using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program {
    public static string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string Data = Environment.GetEnvironmentVariable("ALLUR_PORTABLE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllurTwinVR");
    public static EventWaitHandle Open;
    [STAThread] static void Main() {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try {
            Data = Path.GetFullPath(Data);
            string key; using (var sha = SHA256.Create()) { key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Data.ToLowerInvariant()))).Replace("-", "").Substring(0, 24); }
            bool created;
            using (var mutex = new Mutex(true, "Local\\AllurTwinDesktop-" + key, out created)) {
                if (!created) { try { using (var signal = EventWaitHandle.OpenExisting("Local\\AllurTwinOpen-"+key)) signal.Set(); } catch {} return; }
                using (Open = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AllurTwinOpen-"+key)) { Application.Run(new Launcher()); }
            }
        } catch (Exception ex) { MessageBox.Show(ex.Message, "Allur Twin — не удалось запустить", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

class Launcher : Form {
    Label state; Button panel, vr; System.Windows.Forms.Timer timer;
    IntPtr job; Process supervisor; bool ready, opened, opening;
    string Runtime { get { return Path.Combine(Program.Data, "runtime"); } }
    JavaScriptSerializer json = new JavaScriptSerializer();
    public Launcher() {
        Text = "Allur Twin · VR Workshop"; ClientSize = new Size(590, 335); MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; BackColor = Color.FromArgb(13, 20, 34); ForeColor = Color.White;
        Font = new Font("Segoe UI", 10); Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(new Label { Text = "ALLUR TWIN  ×  VR WORKSHOP", Location = new Point(30, 25), AutoSize = true, ForeColor = Color.FromArgb(108, 178, 255), Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Ваш виртуальный завод", Location = new Point(27, 57), AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold) });
        state = new Label { Text = "Подготовка приложения…", Location = new Point(30, 113), Size = new Size(525, 66), ForeColor = Color.FromArgb(192, 210, 231) }; Controls.Add(state);
        panel = Button("Открыть панель", 30, 184, 250, true); panel.Enabled = false; panel.Click += async (s,e) => await OpenPanel();
        vr = Button("Открыть VR", 300, 184, 250, false); vr.Enabled = false; vr.Click += (s,e) => { File.WriteAllText(Path.Combine(Runtime,"open-vr.request"), "open"); state.Text = "Открываем VR-цех…"; };
        var logs = Button("Журналы",30,248,120,false); logs.Click += (s,e) => Process.Start(new ProcessStartInfo(Runtime) {UseShellExecute=true});
        var quit = Button("Завершить",430,248,120,false); quit.Click += (s,e) => Close();
        Controls.Add(new Label {Text="Сохранения остаются на этом компьютере.",Location=new Point(165,263),Size=new Size(255,40),ForeColor=Color.FromArgb(129,151,177),Font=new Font("Segoe UI",8)});
        timer = new System.Windows.Forms.Timer {Interval=500}; timer.Tick += Tick;
        Shown += (s,e) => { try { Start(); timer.Start(); } catch(Exception ex) { state.Text=ex.Message; Cleanup(); } };
        FormClosed += (s,e) => {timer.Stop(); Cleanup();};
    }
    Button Button(string title, int x, int y, int w, bool primary) {
        var b = new Button { Text=title, Location=new Point(x,y), Size=new Size(w,46), FlatStyle=FlatStyle.Flat,
            BackColor=primary?Color.FromArgb(43,112,232):Color.FromArgb(25,38,58), ForeColor=Color.White, Cursor=Cursors.Hand };
        b.FlatAppearance.BorderSize=0; b.FlatAppearance.MouseOverBackColor=Color.FromArgb(57,118,192); Controls.Add(b); return b;
    }
    void Start() {
        var python=Path.Combine(Program.Root,"runtimes","python","python.exe");
        if (!File.Exists(python) || !File.Exists(Path.Combine(Program.Root,"packaging","supervisor.py")))
            throw new Exception("Сначала извлеките весь ZIP в отдельную папку, затем откройте AllurTwin.exe из этой папки.");
        Directory.CreateDirectory(Runtime);
        foreach(var name in new[]{"launcher.json","stop.request","open-vr.request"}) {var file=Path.Combine(Runtime,name); if(File.Exists(file)) File.Delete(file);}
        job=Native.CreateJobObject(IntPtr.Zero,null); if(job==IntPtr.Zero) throw new Exception("Не удалось создать группу процессов.");
        var limits=new Native.Extended(); limits.Basic.LimitFlags=0x2000;
        int size=Marshal.SizeOf(limits); IntPtr memory=Marshal.AllocHGlobal(size);
        try {Marshal.StructureToPtr(limits,memory,false); if(!Native.SetInformationJobObject(job,9,memory,(uint)size)) throw new Exception("Не удалось настроить завершение серверов.");} finally {Marshal.FreeHGlobal(memory);}
        var gate=Path.Combine(Runtime,"gate-"+Guid.NewGuid().ToString("N"));
        var info=new ProcessStartInfo(python,"-I -B \""+Path.Combine(Program.Root,"packaging","supervisor.py")+"\" \""+gate+"\"") {WorkingDirectory=Program.Root,UseShellExecute=false,CreateNoWindow=true};
        info.EnvironmentVariables["ALLUR_PORTABLE_DATA"]=Program.Data;
        supervisor=Process.Start(info);
        if(!Native.AssignProcessToJobObject(job,supervisor.Handle)) {supervisor.Kill(); throw new Exception("Не удалось связать серверы с окном приложения.");}
        File.WriteAllText(gate,"start");
    }
    async void Tick(object sender, EventArgs args) {
        if(Program.Open.WaitOne(0)) {WindowState=FormWindowState.Normal; Show(); Activate(); if(ready) await OpenPanel();}
        try {
            string file=Path.Combine(Runtime,"launcher.json");
            if(File.Exists(file)) {
                var data=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                state.Text=(string)data["message"]; string mode=(string)data["state"];
                ready=mode=="ready"; panel.Enabled=vr.Enabled=ready;
                if(mode=="error") {timer.Stop(); Cleanup();}
                if(ready && !opened) {opened=true; if(Environment.GetEnvironmentVariable("ALLUR_NO_BROWSER")!="1") await OpenPanel();}
            }
            if(supervisor!=null && supervisor.HasExited && job!=IntPtr.Zero) {ready=false; panel.Enabled=vr.Enabled=false; state.Text="Серверы остановились. Закройте и снова откройте приложение. Журналы содержат причину."; timer.Stop(); Cleanup();}
        } catch(IOException) {} catch(ArgumentException) {}
    }
    async Task OpenPanel() {
        if(opening) return; opening=true;
        try {
            var url=await Task.Run(() => {
                var serializer=new JavaScriptSerializer();
                var access=serializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Program.Data,"access.json")));
                using(var client=new System.Net.WebClient()) {
                    client.Proxy=null; client.Headers["Content-Type"]="application/json"; client.Headers["X-Desktop-Secret"]=access["desktopSecret"];
                    var response=serializer.Deserialize<Dictionary<string,string>>(client.UploadString("http://127.0.0.1:8184/api/desktop/ticket","POST","{}"));
                    return "http://127.0.0.1:8091/#desktop="+Uri.EscapeDataString(response["ticket"]);
                }
            });
            Process.Start(new ProcessStartInfo(url) {UseShellExecute=true});
        } catch(Exception ex) {state.Text="Панель не открылась: "+ex.Message;} finally {opening=false;}
    }
    void Cleanup() { if(job!=IntPtr.Zero) {Native.CloseHandle(job);job=IntPtr.Zero;} }
}

static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct Basic {public long ProcessTime,JobTime;public uint LimitFlags;public UIntPtr Minimum,Maximum;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] public struct Counters {public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] public struct Extended {public Basic Basic;public Counters IO;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr job,int info,IntPtr data,uint length);
    [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}

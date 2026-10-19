using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRWorkshop.Editor
{
    public static class WorkshopCabinetVerification
    {
        static void Check(bool value, string label) { if (!value) throw new Exception(label); Debug.Log("[Cabinet check] " + label); }
        public static void Run()
        {
            try
            {
                EditorSceneManager.OpenScene(WorkshopBuild.ScenePath);
                var trainer = UnityEngine.Object.FindFirstObjectByType<CabinetTrainer>();
                Check(trainer != null && trainer.parts.Length == 6, "six components serialized in workshop");
                var parts = trainer.parts;
                Check(!trainer.Validate(out _), "empty cabinet cannot be powered");
                for (int i=0;i<parts.Length;i++)
                {
                    var p=parts[i]; var collider=p.GetComponent<BoxCollider>();
                    Check(p.transform.lossyScale == Vector3.one && collider.size == p.dimensions, "true scale and collider: " + p.partId);
                    Check(p.GetComponent<WorkshopTarget>().movableBody==p.Body,"VIVE grab body: "+p.partId);
                }
                Check(!trainer.CanMount(parts[5],0,out _),"panel mounting rejected on rail");
                Check(!trainer.CanMount(parts[0],2,out _),"oversize module rejected");
                Check(!trainer.CanMount(parts[3],0,out _),"small module cannot reserve the only place for larger supply");
                parts[0].transform.SetPositionAndRotation(trainer.transform.TransformPoint(trainer.slots[0].position),Quaternion.Euler(0,90,0));
                Physics.SyncTransforms();
                Check(!parts[0].TrySnap(),"wrong mounting orientation rejected");
                for (int i=0;i<parts.Length;i++)
                {
                    // Parts order differs from slot order: fuse slot 3, network slot 4.
                    var p=parts[i]; p.transform.SetPositionAndRotation(trainer.transform.TransformPoint(trainer.slots[i].position),trainer.transform.rotation);
                    Physics.SyncTransforms();
                    Check(p.TrySnap() && p.Body.isKinematic,"mounted: "+p.partId);
                    Check(p.transform.lossyScale==Vector3.one,"mount never rescales "+p.partId);
                }
                Check(!trainer.CanMount(parts[1],0,out _),"occupied place rejected");
                var ports=trainer.GetComponentsInChildren<CabinetPort>();
                CabinetPort Port(string id)=>ports.Single(p=>p.Id==id);
                void RestoreWire(string a,string b)
                {
                    Check(trainer.Connect(Port(a),Port(b),CabinetCable.Copper075),"repair wire: "+a+" -> "+b);
                    foreach(string id in new[]{a,b})foreach(CabinetToolKind kind in new[]{CabinetToolKind.Stripper,CabinetToolKind.Crimper,CabinetToolKind.Screwdriver})
                        Check(trainer.ApplyTool(kind,Port(id).GetComponent<WorkshopTarget>()),"repair termination "+id);
                }
                trainer.EnableGuidePreview();
                trainer.Choose(Port("plc.LAN"));
                Check(trainer.Cable==CabinetCable.Ethernet,"cable selected automatically from first terminal");
                trainer.Choose(Port("supply.+"));
                Check(trainer.SelectedPort==Port("plc.LAN") && trainer.WireCount==0,"invalid second terminal keeps first selected");
                Render(trainer,"cabinet-wiring-guide.png");
                trainer.Choose(Port("io.LAN"));
                Check(trainer.SelectedPort==null && trainer.WireCount==1,"valid second click completes wire");
                trainer.UndoWire(); Check(trainer.WireCount==0,"undo removes latest wire");
                trainer.Choose(Port("supply.+")); trainer.Choose(Port("supply.+"));
                Check(trainer.SelectedPort==null,"repeat first terminal cancels selection");
                var assistPoint=Port("supply.+").transform.position;
                var assistRay=new Ray(assistPoint+new Vector3(.012f,0,-.25f),Vector3.forward);
                Physics.SyncTransforms();
                Check(CabinetTrainer.AssistedPort(assistRay,parts[0])==Port("supply.+"),"ray assistance selects nearby visible terminal");
                var blocker=GameObject.CreatePrimitive(PrimitiveType.Cube);
                blocker.transform.position=assistPoint+new Vector3(0,0,-.1f); blocker.transform.localScale=new Vector3(.1f,.1f,.02f); Physics.SyncTransforms();
                Check(CabinetTrainer.AssistedPort(assistRay,parts[0])==null,"ray assistance cannot select through an obstacle");
                UnityEngine.Object.DestroyImmediate(blocker);
                Check(!trainer.Connect(Port("supply.+"),Port("plc.0"),CabinetCable.Copper075),"polarity mismatch rejected");
                Check(!trainer.Connect(Port("plc.LAN"),Port("io.LAN"),CabinetCable.Copper075),"wire instead of RJ45 rejected");
                Check(!trainer.Connect(Port("supply.+"),Port("fuse.OUT"),CabinetCable.Copper075),"two outputs rejected");
                foreach(var pair in CabinetTrainer.Required)
                    Check(trainer.Connect(Port(pair[0]),Port(pair[1]),pair[0].EndsWith("LAN")?CabinetCable.Ethernet:CabinetCable.Copper075),"wire "+pair[0]+" -> "+pair[1]);
                Check(!trainer.Connect(Port("supply.+"),Port("plc.+"),CabinetCable.Copper075),"occupied terminal rejected");
                Check(!trainer.Validate(out _) && trainer.CorrectConnections==7,"topology alone cannot power unfinished mechanical assembly");
                Check(!trainer.ApplyTool(CabinetToolKind.Crimper,Port("supply.+").GetComponent<WorkshopTarget>()),"crimping before stripping is rejected");
                foreach(var screw in parts[5].GetComponentsInChildren<WorkshopTarget>().Where(t=>t.action==WorkshopAction.TrainingScrew))
                    Check(trainer.ApplyTool(CabinetToolKind.Screwdriver,screw),"indicator mounting screw tightened");
                foreach(var port in ports.Where(p=>p.signal!=CabinetSignal.Ethernet))
                {
                    var target=port.GetComponent<WorkshopTarget>();
                    Check(trainer.ApplyTool(CabinetToolKind.Stripper,target),"wire stripped: "+port.Id);
                    Check(trainer.ApplyTool(CabinetToolKind.Crimper,target),"ferrule crimped: "+port.Id);
                    Check(trainer.ApplyTool(CabinetToolKind.Screwdriver,target),"terminal tightened: "+port.Id);
                }
                Check(trainer.Validate(out _),"finished topology and mechanical assembly accepted");
                trainer.TogglePower(); Check(trainer.Powered,"valid assembly powers indicator");
                Check(trainer.VoltageAt(Port("plc.+"))==24 && trainer.VoltageAt(Port("lamp.+"))==24,"24 V reaches PLC and digital output");
                Check(!parts[1].BeginGrab(),"powered module cannot be removed");
                Render(trainer,"cabinet-assembled.png");
                trainer.CreateFault(); Check(trainer.FaultActive && trainer.VoltageAt(Port("fuse.IN"))==24 && trainer.VoltageAt(Port("fuse.OUT"))==0,"blown fuse isolates downstream power");
                Check(trainer.ApplyTool(CabinetToolKind.Multimeter,Port("fuse.OUT").GetComponent<WorkshopTarget>()),"multimeter diagnoses powered fault");
                trainer.TogglePower(); Check(!trainer.Powered,"faulty circuit switched off");
                Check(parts[2].BeginGrab(),"fuse detached for replacement"); trainer.ReplaceFuse(); Check(!trainer.FaultActive,"fuse replaced while isolated");
                // Restore this module and its two prepared wires, preserving later detach checks.
                parts[2].Body.position=trainer.transform.TransformPoint(trainer.slots[2].position);parts[2].Body.rotation=trainer.transform.rotation;Check(parts[2].TrySnap(),"replacement fuse mounted");
                Check(trainer.Connect(Port("supply.+"),Port("fuse.IN"),CabinetCable.Copper075),"supply wire restored");
                Check(trainer.Connect(Port("fuse.OUT"),Port("plc.+"),CabinetCable.Copper075),"PLC wire restored");
                foreach(var id in new[]{"supply.+","fuse.IN","fuse.OUT","plc.+"})foreach(CabinetToolKind kind in new[]{CabinetToolKind.Stripper,CabinetToolKind.Crimper,CabinetToolKind.Screwdriver})trainer.ApplyTool(kind,Port(id).GetComponent<WorkshopTarget>());
                trainer.TogglePower();Check(trainer.Powered,"repaired exercise powers normally");
                trainer.CreateFault();Check(trainer.FaultActive&&trainer.VoltageAt(Port("fuse.OUT"))==24&&trainer.VoltageAt(Port("plc.+"))==0,"open-wire fault detected downstream of fuse");
                trainer.TogglePower();trainer.SelectRemoval();trainer.Choose(Port("fuse.OUT"));Check(!trainer.FaultActive,"broken conductor removed");
                RestoreWire("fuse.OUT","plc.+");trainer.TogglePower();Check(trainer.Powered,"open wire repaired");
                trainer.CreateFault();Check(trainer.FaultActive&&trainer.VoltageAt(Port("plc.+"))==0&&trainer.VoltageAt(Port("plc.0"))==24,"reversed supply measurable at PLC");
                trainer.TogglePower();trainer.SelectRemoval();trainer.Choose(Port("plc.+"));trainer.SelectRemoval();trainer.Choose(Port("plc.0"));Check(!trainer.FaultActive,"incorrect conductors removed");
                RestoreWire("fuse.OUT","plc.+");RestoreWire("terminal.A","plc.0");trainer.TogglePower();Check(trainer.Powered,"wrong-terminal exercise repaired");
                trainer.TogglePower(); Check(parts[1].BeginGrab(),"unpowered module can be detached");
                Check(trainer.WireCount==3 && !trainer.Validate(out _),"detaching PLC removes its four wires");
                trainer.ReturnLooseParts(); Check(parts[0].slot==0 && parts[1].slot<0 && trainer.WireCount==3,"return loose parts preserves installed modules and wiring");
                trainer.ResetExercise(); Check(trainer.MountedCount==0 && trainer.WireCount==0 && !trainer.Powered,"reset clears assembly and returns parts to tray");
                Render(trainer,"cabinet-training.png");
                parts[0].Body.position=trainer.transform.TransformPoint(trainer.slots[0].position)+new Vector3(0,0,-.11f);
                parts[0].Body.rotation=Quaternion.Euler(0,45,0);
                Check(trainer.MountCandidate(parts[0])==0,"placement preview identifies compatible nearby slot");
                trainer.GuideHeld(parts[0]); Render(trainer,"cabinet-placement-guide.png");
                Check(parts[0].TrySnap(),"moderately rotated part aligns when released"); trainer.EndGuidance();
                trainer.ChangeMode();Check(trainer.Mode==CabinetMode.Learn&&trainer.MountedCount==1,"mode change requires confirmation before discarding work");trainer.ChangeMode();Check(trainer.Mode==CabinetMode.Practice&&trainer.MountedCount==0,"new practice starts from empty cabinet");trainer.ChangeMode();Check(trainer.Mode==CabinetMode.Exam&&trainer.MountedCount==0,"exam starts without a preassembled learning solution");
                Check(trainer.AdvisorContext().Contains("Экзамен"),"exam context restricts advisor hints");int score=trainer.Score;trainer.Hint();Check(trainer.Score<score,"explicit hint reduces exam score");
                File.WriteAllText("Logs/cabinet-verification.json","{\"success\":true,\"checks\":\"metric scale, mounting, occupied slots, orientation, polarity, cable type, occupied terminals, topology, power interlock, detachment and reset\",\"physicalViveVerified\":false}");
                EditorApplication.Exit(0);
            }
            catch(Exception e) { Debug.LogException(e); File.WriteAllText("Logs/cabinet-verification.json", "{\"success\":false}"); EditorApplication.Exit(1); }
        }
        static void Render(CabinetTrainer trainer,string name)
        {
            var camera=new GameObject("Cabinet verification camera").AddComponent<Camera>();
            camera.transform.position=trainer.transform.TransformPoint(new Vector3(.1f,1.65f,-2.5f));
            camera.transform.LookAt(trainer.transform.TransformPoint(new Vector3(-.1f,1.15f,0)));
            camera.fieldOfView=54; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.045f,.06f,.08f);
            var rt=new RenderTexture(1600,1100,24); camera.targetTexture=rt;
            foreach(var label in trainer.GetComponentsInChildren<TextMesh>()) label.font.RequestCharactersInTexture(label.text,label.fontSize,label.fontStyle);
            camera.Render(); var previous=RenderTexture.active; RenderTexture.active=rt;
            var image=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false); image.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); image.Apply();
            File.WriteAllBytes("Logs/"+name,image.EncodeToPNG()); RenderTexture.active=previous; camera.targetTexture=null;
            UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(camera.gameObject);
        }
    }
}

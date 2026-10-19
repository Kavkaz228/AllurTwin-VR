using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRWorkshop.Editor
{
    // Fast edit-mode checks of actual production meshes and the shared registry.
    // Server acceptance, QC and queue semantics are covered by server integration tests.
    public static class WorkshopFinishedVehicleVerification
    {
        [Serializable] sealed class Report
        {
            public bool success, originalRootAndBody, fourWheels, sixWindowApertures, metreScale;
            public bool wheelArchAlignment, floorContact, beltContact, carrierFloorContact, progressiveAssembly;
            public bool idempotentAssembly, forwardOwnership, stalePaintCannotReclaim, noTimedRetirement;
            public int wheelCount, windowCount, renderedMeshVertices;
            public Vector3 bodyBoundsMetres;
            public float groundClearanceErrorMetres, beltContactErrorMetres;
            public string error, renderedUtc;
            public string scope = "Real assembled vehicle meshes at 1:1 scale, actual rendered previews, and real shared-registry forward claims/releases from a prepared painted body. No network, autonomous-driving, physical-QC or headset validation is claimed.";
        }

        public static void Run()
        {
            var report = new Report();
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath,"../Logs"));
            Directory.CreateDirectory(folder);
            try
            {
                Need(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,"A graphics device is required; omit -nographics.");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                RenderSettings.ambientMode=AmbientMode.Flat;
                RenderSettings.ambientLight=new Color(.40f,.44f,.49f);RenderSettings.fog=false;
                var root = new GameObject("Finished vehicle verification / panel-fixture");
                root.transform.position=new Vector3(0,.24f,0);
                var body=root.AddComponent<Rigidbody>();body.isKinematic=true;body.useGravity=false;
                var shell = new GameObject("Original stamped shell",typeof(MeshFilter),typeof(MeshRenderer));
                shell.transform.SetParent(root.transform,false);
                shell.GetComponent<MeshFilter>().sharedMesh=AutomotiveGeometry.BodyShell();
                shell.GetComponent<MeshRenderer>().sharedMaterial=WorkshopSurfaceLibrary.Get(WorkshopSurfaceLibrary.Surface.PaintedBody);
                var vehicle=FinishedVehicleGeometry.Build(root.transform,"panel-fixture");
                Need(vehicle!=null,"Vehicle builder returned no component.");
                report.originalRootAndBody=vehicle.transform.IsChildOf(root.transform)&&root.GetComponent<Rigidbody>()==body&&root.GetComponentsInChildren<Rigidbody>(true).Length==1;
                Need(report.originalRootAndBody,"Assembly replaced the existing body or added an independent rigidbody.");
                var camera=CreateStudio();
                vehicle.SetProgress(0);
                int before=VisibleVertexCount(root.transform);
                Capture(camera,Path.Combine(folder,"finished-vehicle-before-assembly.png"));
                vehicle.SetProgress(.5f);
                int middle=VisibleVertexCount(root.transform);
                Need(Mathf.Abs(vehicle.AssemblyProgress-.5f)<.0001f,"Assembly progress is not server-addressable.");
                vehicle.SetProgress(1);
                int after=VisibleVertexCount(root.transform);
                report.progressiveAssembly=before<after&&middle>=before&&middle<=after;
                Need(report.progressiveAssembly,"Assembly does not reveal real components progressively.");
                report.renderedMeshVertices=after;
                int transforms=root.GetComponentsInChildren<Transform>(true).Length;
                vehicle.SetProgress(1);vehicle.SetProgress(1);
                report.idempotentAssembly=transforms==root.GetComponentsInChildren<Transform>(true).Length&&after==VisibleVertexCount(root.transform);
                Need(report.idempotentAssembly,"Repeated snapshots duplicate vehicle geometry.");
                report.wheelCount=vehicle.WheelCount;report.windowCount=vehicle.WindowsCount;
                report.fourWheels=vehicle.WheelCount==4&&vehicle.Wheels!=null&&vehicle.Wheels.Length==4;
                report.sixWindowApertures=vehicle.WindowsCount==6&&vehicle.Windows!=null&&vehicle.Windows.Length==6;
                Need(report.fourWheels&&report.sixWindowApertures,"Expected four wheels and six separate window meshes.");
                CheckWheels(root.transform,vehicle.Wheels,report);
                CheckWindows(root.transform,vehicle.Windows);
                root.transform.position=new Vector3(0,.30f,0);
                vehicle.SetCarrierVisible(true);
                var carrier=root.transform.Find("AGV — подкатная платформа");
                Need(carrier!=null,"Parking transport platform is missing.");
                report.carrierFloorContact=Mathf.Abs(.30f+MeshBounds(carrier,root.transform).min.y)<.01f;
                Need(report.carrierFloorContact,"AGV wheels do not touch the floor at the server's .30 m transport height.");
                vehicle.SetCarrierVisible(false);
                root.transform.position=new Vector3(0,.24f,0);
                var bounds=MeshBounds(root.transform,root.transform);
                report.bodyBoundsMetres=bounds.size;
                report.metreScale=(root.transform.lossyScale-Vector3.one).sqrMagnitude<.000001f&&
                    bounds.size.x>1.70f&&bounds.size.x<2.30f&&bounds.size.y>1.50f&&bounds.size.y<1.90f&&bounds.size.z>4.10f&&bounds.size.z<4.70f;
                Need(report.metreScale,"Finished car dimensions or root scale are inconsistent with the 4.24 m original shell: "+bounds.size);
                Need(after>5000,"Finished car is missing its detailed meshes.");
                VerifyRegistry(root,body,report);
                Capture(camera,Path.Combine(folder,"finished-vehicle-front-preview.png"));
                camera.transform.position=new Vector3(-5.3f,2.5f,5.9f);camera.transform.LookAt(new Vector3(0,.85f,0));
                Capture(camera,Path.Combine(folder,"finished-vehicle-rear-preview.png"));
                report.success=true;report.renderedUtc=DateTime.UtcNow.ToString("O");
                Debug.Log("Finished vehicle mesh, contact and shared-registry verification passed.");
            }
            catch(Exception error){report.error=error.ToString();Debug.LogException(error);}
            finally{File.WriteAllText(Path.Combine(folder,"finished-vehicle-verification.json"),JsonUtility.ToJson(report,true));}
            if(!report.success)throw new InvalidOperationException(report.error);
        }

        static void CheckWheels(Transform root,Transform[] wheels,Report report)
        {
            var corners=new System.Collections.Generic.HashSet<string>();
            foreach(var wheel in wheels)
            {
                Need(wheel!=null&&wheel.IsChildOf(root),"A wheel is outside the retained body.");
                Vector3 centre=root.InverseTransformPoint(wheel.position);
                Need(Mathf.Abs(Mathf.Abs(centre.x)-.85f)<.03f&&Mathf.Abs(centre.y-.14f)<.025f&&Mathf.Abs(Mathf.Abs(centre.z)-1.40f)<.025f,"Wheel is not centred inside its stamped arch: "+centre);
                corners.Add(Mathf.Sign(centre.x)+"/"+Mathf.Sign(centre.z));
                Bounds bounds=MeshBounds(wheel,root);
                Need(Mathf.Abs(bounds.size.y-.76f)<.035f&&Mathf.Abs(bounds.size.z-.76f)<.035f&&bounds.size.x>.15f&&bounds.size.x<.40f,"Tyre has an incorrect radius or axle orientation: "+bounds.size);
                report.groundClearanceErrorMetres=Mathf.Max(report.groundClearanceErrorMetres,Mathf.Abs(.24f+bounds.min.y));
                report.beltContactErrorMetres=Mathf.Max(report.beltContactErrorMetres,Mathf.Abs(1.13f+bounds.min.y-.89f));
            }
            report.wheelArchAlignment=corners.Count==4;
            report.floorContact=report.groundClearanceErrorMetres<.015f;
            report.beltContact=report.beltContactErrorMetres<.015f;
            Need(report.wheelArchAlignment&&report.floorContact&&report.beltContact,"Wheels duplicate an axle corner, float or penetrate the parking floor/conveyor.");
        }

        static void CheckWindows(Transform root,Transform[] windows)
        {
            foreach(var window in windows)
            {
                Need(window!=null&&window.IsChildOf(root),"Window is outside the retained body.");
                Bounds bounds=MeshBounds(window,root);
                Need(bounds.min.x>-.96f&&bounds.max.x<.96f&&bounds.min.y>.53f&&bounds.max.y<1.43f&&bounds.min.z>-1.08f&&bounds.max.z<1.39f,"Window protrudes outside the actual shell apertures: "+bounds);
                Need(bounds.size.y>.15f&&bounds.size.z>.15f,"Window aperture is empty or collapsed.");
                Need(window.GetComponentsInChildren<MeshFilter>(true).Length>0,"Window has no real mesh.");
            }
        }

        static void VerifyRegistry(GameObject root,Rigidbody body,Report report)
        {
            // Prepare a painted body exactly once; all transitions below invoke
            // production ClaimBody/ReleaseBody, not a reimplemented ownership rule.
            var registryObject=new GameObject("Isolated shared body registry");registryObject.SetActive(false);
            var registry=registryObject.AddComponent<ConveyorView>();
            var panelType=typeof(ConveyorView).GetNestedType("Panel",BindingFlags.NonPublic);
            Need(panelType!=null,"Shared production body type is missing.");
            object panel=Activator.CreateInstance(panelType,true);
            panelType.GetField("root").SetValue(panel,root);
            panelType.GetField("body").SetValue(panel,body);
            panelType.GetField("ownerStage").SetValue(panel,2);
            var entries=(IDictionary)typeof(ConveyorView).GetField("sharedPanels",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(registry);
            entries.Add("panel-fixture",panel);
            var claim=typeof(ConveyorView).GetMethod("ClaimBody",BindingFlags.Instance|BindingFlags.NonPublic);
            var release=typeof(ConveyorView).GetMethod("ReleaseBody",BindingFlags.Instance|BindingFlags.NonPublic);
            object downstream=claim.Invoke(registry,new object[]{"panel-fixture",3,root.transform.position});
            report.forwardOwnership=ReferenceEquals(panel,downstream)&&registry.TryGetBody("panel-fixture",out var retained,out int stage)&&retained==root&&stage==3&&retained.GetComponent<Rigidbody>()==body;
            Need(report.forwardOwnership,"Downstream transfer did not retain its existing body/Rigidbody.");
            object stale=claim.Invoke(registry,new object[]{"panel-fixture",2,new Vector3(20,20,20)});
            report.stalePaintCannotReclaim=stale==null&&registry.TryGetBody("panel-fixture",out retained,out stage)&&retained==root&&stage==3;
            Need(report.stalePaintCannotReclaim,"An older painting snapshot reclaimed the assembled car.");
            release.Invoke(registry,new object[]{"panel-fixture",2});
            release.Invoke(registry,new object[]{"panel-fixture",3});
            var retire=panelType.GetField("retireAt");
            bool noTimer=retire==null||Convert.ToDouble(retire.GetValue(panel))<=0;
            report.noTimedRetirement=noTimer&&registry.TryGetBody("panel-fixture",out retained,out stage)&&retained==root&&stage==3;
            Need(report.noTimedRetirement,"A missing/stopped intermediate snapshot schedules the retained QC/parked body for destruction.");
            // Also cover a gap before downstream has arrived: paint retirement
            // must not destroy a body during a long reconnect or a full queue.
            panelType.GetField("ownerStage").SetValue(panel,2);
            release.Invoke(registry,new object[]{"panel-fixture",2});
            Need(retire==null||Convert.ToDouble(retire.GetValue(panel))<=0,"Painting still retires its outgoing body before downstream claims it.");
            claim.Invoke(registry,new object[]{"panel-fixture",3,root.transform.position});
        }

        static int VisibleVertexCount(Transform root)
        {
            int total=0;
            foreach(var filter in root.GetComponentsInChildren<MeshFilter>())
            {
                var renderer=filter.GetComponent<Renderer>();
                if(renderer!=null&&renderer.enabled&&filter.sharedMesh!=null&&filter.transform.lossyScale.sqrMagnitude>.001f)total+=filter.sharedMesh.vertexCount;
            }
            return total;
        }
        static Bounds MeshBounds(Transform branch,Transform frame)
        {
            bool first=true;var bounds=new Bounds();
            foreach(var filter in branch.GetComponentsInChildren<MeshFilter>())
            {
                if(filter.sharedMesh==null)continue;
                foreach(var vertex in filter.sharedMesh.vertices)
                {
                    Vector3 point=frame.InverseTransformPoint(filter.transform.TransformPoint(vertex));
                    Need(float.IsFinite(point.x)&&float.IsFinite(point.y)&&float.IsFinite(point.z),"Vehicle has a non-finite mesh vertex.");
                    if(first){bounds=new Bounds(point,Vector3.zero);first=false;}else bounds.Encapsulate(point);
                }
            }
            Need(!first,"Vehicle branch contains no visible mesh vertices: "+branch.name);
            return bounds;
        }
        static Camera CreateStudio()
        {
            var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);floor.name="Parking contact reference floor";
            floor.transform.localScale=Vector3.one*2;
            floor.GetComponent<Renderer>().sharedMaterial=WorkshopSurfaceLibrary.Get(WorkshopSurfaceLibrary.Surface.Concrete);
            UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
            var key=new GameObject("Vehicle key",typeof(Light)).GetComponent<Light>();
            key.type=LightType.Directional;key.intensity=1.1f;key.shadows=LightShadows.Soft;key.transform.rotation=Quaternion.Euler(45,-35,0);
            var fill=new GameObject("Vehicle fill",typeof(Light)).GetComponent<Light>();
            fill.type=LightType.Directional;fill.intensity=.45f;fill.transform.rotation=Quaternion.Euler(25,135,0);
            var camera=new GameObject("Vehicle inspection camera",typeof(Camera)).GetComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.15f,.19f);
            camera.nearClipPlane=.04f;camera.farClipPlane=40;camera.fieldOfView=42;
            camera.transform.position=new Vector3(5.3f,2.6f,-5.9f);camera.transform.LookAt(new Vector3(0,.85f,0));
            return camera;
        }
        static void Capture(Camera camera,string path)
        {
            var target=new RenderTexture(1280,900,24){antiAliasing=4};
            var image=new Texture2D(1280,900,TextureFormat.RGB24,false);var previous=RenderTexture.active;
            try{camera.targetTexture=target;camera.Render();RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1280,900),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());}
            finally{RenderTexture.active=previous;camera.targetTexture=null;target.Release();UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);}
        }
        static void Need(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}

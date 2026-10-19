using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRWorkshop.Editor
{
    // An isolated mesh/pose check and real renderer preview. It does not claim
    // physical controller tracking or headset comfort validation.
    public static class WorkshopHandsVerification
    {
        [Serializable] sealed class Report
        {
            public bool success, colliderFreeHands, fingerAnimation, horizontalToolAxes, tipAtTcp, processToolsHaveNoGripper;
            public bool verticalHeadingStable, yawWrapContinuous, fullYawPreserved;
            public int bonesPerHand, verticesPerHand;
            public string error, renderedUtc;
        }
        public static void Run()
        {
            var report = new Report();
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath,"../Logs"));
            Directory.CreateDirectory(folder);
            try
            {
                VerifyManualHeading(report);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.44f,.46f,.50f);
                RenderSettings.fog=false;
                var camera=new GameObject("Glove and tool inspection camera",typeof(Camera)).GetComponent<Camera>();
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.11f,.14f,.18f);
                camera.nearClipPlane=.01f;camera.farClipPlane=10;camera.fieldOfView=38;
                var key=new GameObject("Broad inspection key",typeof(Light)).GetComponent<Light>();
                key.type=LightType.Directional;key.intensity=1.15f;key.transform.rotation=Quaternion.Euler(48,-35,0);
                var fill=new GameObject("Inspection fill",typeof(Light)).GetComponent<Light>();
                fill.type=LightType.Directional;fill.intensity=.45f;fill.transform.rotation=Quaternion.Euler(15,135,0);
                var left=new GameObject("Left glove").AddComponent<WorkshopGloveHand>();left.Initialize(true);
                var right=new GameObject("Right glove").AddComponent<WorkshopGloveHand>();right.Initialize(false);
                left.transform.position=new Vector3(-.095f,0,0);right.transform.position=new Vector3(.095f,0,0);
                var skin=right.GetComponent<SkinnedMeshRenderer>();
                report.bonesPerHand=skin.bones.Length;report.verticesPerHand=skin.sharedMesh.vertexCount;
                Need(report.bonesPerHand==16&&report.verticesPerHand>2000,"Glove mesh/bone count is incomplete.");
                report.colliderFreeHands=left.GetComponentsInChildren<Collider>().Length==0&&right.GetComponentsInChildren<Collider>().Length==0;
                Need(report.colliderFreeHands,"Visual glove must not block controller rays.");
                left.SetInput(0,0,true);right.SetInput(0,0,true);
                Quaternion indexOpen=skin.bones[1].localRotation,middleOpen=skin.bones[4].localRotation;
                camera.transform.position=new Vector3(.035f,.30f,.40f);camera.transform.LookAt(new Vector3(0,0,.035f));
                Capture(camera,Path.Combine(folder,"hands-open-preview.png"));
                left.SetInput(1,1,true);right.SetInput(1,1,true);
                report.fingerAnimation=Quaternion.Angle(indexOpen,skin.bones[1].localRotation)>40&&Quaternion.Angle(middleOpen,skin.bones[4].localRotation)>40;
                Need(report.fingerAnimation,"Controller input did not articulate the glove.");
                Capture(camera,Path.Combine(folder,"hands-grip-preview.png"));
                left.gameObject.SetActive(false);right.gameObject.SetActive(false);
                var weld=new GameObject("Welding robot geometry").AddComponent<RobotView>();weld.Initialize(0);
                var paint=new GameObject("Painting robot geometry").AddComponent<RobotView>();paint.Initialize(2);
                var torch=ExtractTool(weld,"Server TCP / horizontal welding torch");
                var spray=ExtractTool(paint,"Server TCP / horizontal paint atomizer");
                torch.position=new Vector3(0,.08f,0);spray.position=new Vector3(0,-.08f,0);
                torch.rotation=spray.rotation=Quaternion.identity;
                report.horizontalToolAxes=Mathf.Abs(torch.right.y)<.0001f&&Mathf.Abs(spray.right.y)<.0001f;
                report.tipAtTcp=CheckTip(torch,"Turned MIG copper gas nozzle")&&CheckTip(spray,"Machined fan spray air cap");
                report.processToolsHaveNoGripper=weld.GetComponent<SimulationGripper>()==null&&paint.GetComponent<SimulationGripper>()==null;
                Need(report.horizontalToolAxes&&report.tipAtTcp&&report.processToolsHaveNoGripper,"Process tooling contract is invalid.");
                camera.transform.position=new Vector3(.20f,.17f,-.40f);camera.transform.LookAt(new Vector3(-.065f,0,0));
                Capture(camera,Path.Combine(folder,"horizontal-tools-preview.png"));
                report.success=true;report.renderedUtc=DateTime.UtcNow.ToString("O");
                Debug.Log("Workshop hands/tool verification passed; actual rendered previews saved.");
            }
            catch(Exception error){report.error=error.ToString();Debug.LogException(error);}
            finally{File.WriteAllText(Path.Combine(folder,"hands-tool-verification.json"),JsonUtility.ToJson(report,true));}
            if(!report.success)throw new InvalidOperationException(report.error);
        }
        static void VerifyManualHeading(Report report)
        {
            foreach (float yaw in new[] { 0f, 63f })
            {
                Quaternion expected = Quaternion.Euler(0,yaw,0), previous = expected;
                foreach (float pitch in new[] { 70f,80f,85f,89.9f,90f,90.1f,95f,100f,110f,100f,90f,80f,70f })
                {
                    Quaternion tilted = expected * Quaternion.AngleAxis(pitch,Vector3.forward);
                    previous = WorkshopClient.HorizontalToolRotation(tilted,previous);
                    Need(Quaternion.Angle(previous,expected)<.1f,"Manual heading jumped while crossing vertical at yaw "+yaw+", pitch "+pitch);
                    Need(Mathf.Abs((previous*Vector3.right).y)<.00001f,"Manual tool target ceased to be level.");
                }
                Quaternion verticalSeed = WorkshopClient.HorizontalToolRotation(expected*Quaternion.AngleAxis(90,Vector3.forward));
                Need(Quaternion.Angle(verticalSeed,expected)<.1f,"New manual hold lost the measured heading at vertical.");
            }
            report.verticalHeadingStable=true;
            Quaternion beforeWrap=Quaternion.Euler(0,179,0), afterWrap=WorkshopClient.HorizontalToolRotation(Quaternion.Euler(0,181,0),beforeWrap);
            Need(Quaternion.Angle(afterWrap,Quaternion.Euler(0,181,0))<.1f&&Quaternion.Angle(beforeWrap,afterWrap)<2.1f,"Manual heading jumped at +/-180 wrap.");
            report.yawWrapContinuous=true;
            Quaternion previousYaw=Quaternion.identity;
            for(int yaw=0;yaw<=360;yaw+=3)
            {
                Quaternion expected=Quaternion.Euler(0,yaw,0);
                previousYaw=WorkshopClient.HorizontalToolRotation(expected,previousYaw);
                Need(Quaternion.Angle(previousYaw,expected)<.1f,"Continuous full-circle yaw was constrained at "+yaw);
            }
            report.fullYawPreserved=true;
        }
        static Transform ExtractTool(RobotView robot,string name)
        {
            Transform tool=null;
            foreach(Transform child in robot.transform)if(child.name==name){tool=child;break;}
            Need(tool!=null,"Missing process tool.");
            foreach(Transform child in robot.transform)child.gameObject.SetActive(child==tool);
            return tool;
        }
        static bool CheckTip(Transform tool,string name)
        {
            var nozzle=tool.Find(name);var mesh=nozzle.GetComponent<MeshFilter>().sharedMesh;
            return Mathf.Abs(mesh.bounds.max.x)<.00001f&&nozzle.localPosition.sqrMagnitude<.00001f;
        }
        static void Need(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static void Capture(Camera camera,string path)
        {
            // Multiple edit-mode renders in one frame reuse the skinning cache.
            // Bake each current bone pose to a temporary visual mesh for this image.
            var skins=UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
            var bakedObjects=new System.Collections.Generic.List<GameObject>();
            var bakedMeshes=new System.Collections.Generic.List<Mesh>();
            foreach(var skin in skins)
            {
                if(!skin.enabled||!skin.gameObject.activeInHierarchy)continue;
                var baked=new Mesh();skin.BakeMesh(baked);
                var preview=new GameObject("Baked preview pose",typeof(MeshFilter),typeof(MeshRenderer));
                preview.transform.SetPositionAndRotation(skin.transform.position,skin.transform.rotation);preview.transform.localScale=skin.transform.lossyScale;
                preview.GetComponent<MeshFilter>().sharedMesh=baked;preview.GetComponent<MeshRenderer>().sharedMaterials=skin.sharedMaterials;
                bakedObjects.Add(preview);bakedMeshes.Add(baked);skin.enabled=false;
            }
            var target=new RenderTexture(1280,900,24){antiAliasing=4};
            var image=new Texture2D(1280,900,TextureFormat.RGB24,false);
            RenderTexture previous=RenderTexture.active;
            try
            {
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1280,900),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active=previous;camera.targetTexture=null;target.Release();
                UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);
                foreach(var skin in skins)if(skin!=null)skin.enabled=true;
                foreach(var obj in bakedObjects)UnityEngine.Object.DestroyImmediate(obj);
                foreach(var mesh in bakedMeshes)UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}

using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VRWorkshop.Editor
{
    // Real uGUI raycast and pointer events with synthetic aim/trigger input.
    // This checks dispatch, occlusion and cancellation, not physical Vive hardware.
    public static class WorkshopPanelVerification
    {
        [Serializable] sealed class Report
        {
            public bool success, rayClick, occlusionBlocksClick, focusLossCancelsClick, heldTriggerRequiresRelease,
                handSwitchCancelsClick, rightHandClick;
            public bool panelDragged,panelSwiped,dragCancelled,pausedDragKeptPanel,fastMoveKeptPanel,
                jitterClick,ancestorDragDoesNotStealClick,hoverHighlighted,pressedHighlighted,disabledBlocksClick;
            public bool edgeHoverStable, cursorAligned, duplicateClickBlocked, momentaryOnce,
                momentaryRayLoss, momentaryFocusLoss, momentaryDisable, momentaryPanelHide,
                momentaryOverlayCancel, momentaryHandSwitch, ancestorSwipeDoesNotStealClick, overlayBlocksHeldButton;
            public string error, finishedUtc;
            public string scope = "World-space uGUI canvas, actual GraphicRaycaster and Button events; synthetic controller aim and trigger";
        }
        public static void Run()
        {
            var report = new Report();
            var root = new GameObject("Panel ray verification");
            RenderTexture texture = null;
            try
            {
                var rigObject = new GameObject("Inactive fixture rig"); rigObject.transform.SetParent(root.transform); rigObject.SetActive(false);
                var rig = rigObject.AddComponent<HeadsetRig>();
                var cameraObject = new GameObject("Fixture camera", typeof(Camera)); cameraObject.transform.SetParent(root.transform);
                var view = cameraObject.GetComponent<Camera>(); view.transform.position = new Vector3(100, 3, 100);
                view.nearClipPlane = .05f; view.farClipPlane = 10; view.fieldOfView = 60;
                texture = new RenderTexture(800, 600, 24); texture.Create(); view.targetTexture = texture;
                typeof(HeadsetRig).GetProperty("View").GetSetMethod(true).Invoke(rig, new object[] { view });
                var events = new GameObject("Fixture event system", typeof(EventSystem)); events.transform.SetParent(root.transform);
                typeof(EventSystem).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(events.GetComponent<EventSystem>(), null);
                var input = events.AddComponent<VrPanelInput>();
                // BaseInputModule does not receive play-mode lifecycle callbacks in this edit-mode fixture.
                typeof(BaseInputModule).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
                var canvasObject = new GameObject("Fixture world panel", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
                canvasObject.transform.SetParent(root.transform);
                var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = view;
                typeof(BaseRaycaster).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvasObject.GetComponent<GraphicRaycaster>(), null);
                ((RectTransform)canvas.transform).sizeDelta = new Vector2(400, 200);
                canvas.transform.SetPositionAndRotation(view.transform.position + Vector3.forward * 2, Quaternion.identity);
                canvas.transform.localScale = Vector3.one * .005f;
                var buttonObject = new GameObject("Fixture button", typeof(RectTransform), typeof(WorkshopRoundedImage), typeof(WorkshopButton),typeof(EventTrigger));
                buttonObject.transform.SetParent(canvas.transform, false);
                var rect = (RectTransform)buttonObject.transform; rect.sizeDelta = new Vector2(200, 60);
                var button = buttonObject.GetComponent<Button>(); button.targetGraphic = buttonObject.GetComponent<WorkshopRoundedImage>();
                var image=buttonObject.GetComponent<WorkshopRoundedImage>();
                var idle=new Color(.10f,.145f,.19f,1);((WorkshopButton)button).SetBaseColor(idle);
                var trigger=buttonObject.GetComponent<EventTrigger>();
                var down=new EventTrigger.Entry{eventID=EventTriggerType.PointerDown};down.callback.AddListener(_=>{});trigger.triggers.Add(down);
                int clicks = 0; button.onClick.AddListener(() => {clicks++;EventSystem.current.SetSelectedGameObject(null);});
                input.Initialize(rig, canvas);
                Canvas.ForceUpdateCanvases(); view.Render();
                Physics.SyncTransforms();
                var beam = new Ray(view.transform.position, Vector3.forward);
                var dispatch = typeof(VrPanelInput).GetMethod("ProcessRay", BindingFlags.Instance | BindingFlags.NonPublic);
                void Frame(bool pressed, bool valid = true) => dispatch.Invoke(input, new object[] { beam, pressed, valid });
                Frame(false); Frame(true); Frame(false);
                Check(clicks == 1, "Controller ray did not click the actual world-space Button."); report.rayClick = true;
                Check(image.BorderWidth>2 && image.color!=idle,"Hover did not visibly highlight the button.");report.hoverHighlighted=true;
                Color hover=image.color;Frame(true);
                Check(image.color!=hover && image.BorderColor==Color.white,"Press feedback did not differ from hover.");report.pressedHighlighted=true;
                var centreBeam=beam;
                beam=new Ray(view.transform.position,Vector3.forward*2+Vector3.right*.08f);Frame(true);Frame(false);
                Check(clicks==2,"EventTrigger drag cancelled a click during controller jitter.");report.jitterClick=true;
                UnityEngine.Object.DestroyImmediate(trigger);
                var ancestor=canvasObject.AddComponent<WorkshopPanelGesture>();ancestor.canvas=canvas;ancestor.content=(RectTransform)canvas.transform;ancestor.view=view;
                beam=centreBeam;Frame(false);Frame(true);
                beam=new Ray(view.transform.position,Vector3.forward*2+Vector3.right*.08f);Frame(true);Frame(false);
                Check(clicks==3&&!ancestor.IsDragging,"Ancestor drag stole a button click.");report.ancestorDragDoesNotStealClick=true;
                UnityEngine.Object.DestroyImmediate(ancestor);
                button.interactable=false;beam=centreBeam;Frame(false);Frame(true);Frame(false);
                Check(clicks==3,"Disabled button was activated.");report.disabledBlocksClick=true;button.interactable=true;
                clicks=1;beam=centreBeam;

                var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.transform.SetParent(root.transform);
                obstacle.transform.position = view.transform.position + Vector3.forward; obstacle.transform.localScale = Vector3.one * .3f;
                Physics.SyncTransforms(); Frame(true); Frame(false);
                Check(clicks == 1, "A button was clicked through a solid object."); report.occlusionBlocksClick = true;
                UnityEngine.Object.DestroyImmediate(obstacle); Physics.SyncTransforms();

                Frame(true); Frame(false, false); Frame(false);
                Check(clicks == 1, "Losing input focus released into a button click."); report.focusLossCancelsClick = true;
                Frame(false, false); Frame(true); Frame(true); Frame(false);
                Check(clicks == 1, "A held trigger clicked immediately after focus returned.");
                Frame(true); Frame(false);
                Check(clicks == 2, "A new trigger press after release failed."); report.heldTriggerRequiresRelease = true;

                var controllerDispatch = typeof(VrPanelInput).GetMethod("ProcessControllerRay", BindingFlags.Instance | BindingFlags.NonPublic);
                void HandFrame(bool pressed, bool right) => controllerDispatch.Invoke(input, new object[] { beam, pressed, true, right });
                HandFrame(false, false); HandFrame(true, false);
                HandFrame(true, true); HandFrame(false, true);
                Check(clicks == 2, "Changing controllers completed the previous hand's click."); report.handSwitchCancelsClick = true;
                HandFrame(true, true); HandFrame(false, true);
                Check(clicks == 3, "The available right controller could not click the actual panel button."); report.rightHandClick = true;

                var workButton=(WorkshopButton)button;
                beam=centreBeam;Frame(false);
                Transform cursor=canvas.transform.Find("Precise controller UI cursor");
                Check(cursor!=null&&cursor.gameObject.activeSelf&&Vector3.Distance(cursor.position,button.transform.position)<.003f,"Visible UI cursor does not match the ray target.");report.cursorAligned=true;
                // The current button keeps a small edge margin without enlarging
                // every neighbouring control or requiring a second aim attempt.
                Vector3 edge=rect.TransformPoint(new Vector3(rect.rect.xMax+3,0,0));
                beam=new Ray(view.transform.position,edge-view.transform.position);Frame(false);
                Check(image.BorderWidth>2,"A small pointer movement at the button edge lost hover.");
                Frame(true);Frame(false);Check(clicks==4,"Edge hover tolerance did not preserve a deliberate click.");report.edgeHoverStable=true;
                ExecuteEvents.Execute(button.gameObject,new PointerEventData(events.GetComponent<EventSystem>()){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
                Check(clicks==4,"A duplicate pointer-click event activated the control twice.");report.duplicateClickBlocked=true;
                beam=centreBeam;Frame(false);
                var swipeAncestor=canvasObject.AddComponent<WorkshopPanelGesture>();swipeAncestor.allowSwipe=true;swipeAncestor.canvas=canvas;swipeAncestor.content=(RectTransform)canvas.transform;swipeAncestor.view=view;
                Frame(true);beam=new Ray(view.transform.position,Vector3.forward*2+Vector3.right*.08f);Frame(true);Frame(false);
                Check(clicks==5&&!swipeAncestor.IsDragging,"A swipe-enabled ancestor consumed a button press.");report.ancestorSwipeDoesNotStealClick=true;
                UnityEngine.Object.DestroyImmediate(swipeAncestor);

                int held=0,released=0;
                Action onHeld=()=>held++,onReleased=()=>released++;
                workButton.Pressed+=onHeld;workButton.Released+=onReleased;workButton.Momentary=true;
                beam=centreBeam;Frame(false);Frame(true);Frame(true);Frame(true);Frame(false);
                Check(held==1&&released==1&&clicks==5&&!workButton.IsHeld,"Momentary control repeated its press, missed release, or fired onClick.");report.momentaryOnce=true;
                Frame(true);beam=new Ray(view.transform.position,Vector3.right);Frame(true);
                Check(held==2&&released==2&&!workButton.IsHeld,"Losing the ray did not release a held control.");
                beam=centreBeam;Frame(true);Frame(true);Check(held==2,"Returning a held trigger rearmed the momentary button.");Frame(false);report.momentaryRayLoss=true;
                Frame(true);Frame(true,false);Frame(false);Check(held==3&&released==3,"Focus/tracking loss did not release the momentary control once.");report.momentaryFocusLoss=true;
                Frame(true);button.interactable=false;Frame(true);Frame(false);button.interactable=true;
                Check(held==4&&released==4&&!workButton.IsHeld,"Disabling an active button did not release it once.");report.momentaryDisable=true;
                Frame(false);Frame(true);canvasObject.SetActive(false);Frame(true);canvasObject.SetActive(true);
                // Restoring a canvas normally gets a render/layout pass before
                // the next frame. This synchronous edit-mode fixture must do it.
                Canvas.ForceUpdateCanvases();view.Render();Frame(true);Frame(false);
                Check(held==5&&released==5&&!workButton.IsHeld,"Hiding/showing a panel retained or restarted its held command.");report.momentaryPanelHide=true;
                Frame(true);Check(held==6&&released==5&&workButton.IsHeld,"Fresh press after restored-canvas layout was not dispatched: presses="+held+", releases="+released);
                input.CancelInteraction();Frame(true);Frame(false);
                Check(held==6&&released==6,"Opening an overlay did not cancel and latch the held command: presses="+held+", releases="+released+", held="+workButton.IsHeld);report.momentaryOverlayCancel=true;
                HandFrame(false,true);HandFrame(true,true);HandFrame(true,false);HandFrame(false,false);
                Check(held==7&&released==7,"Changing controller ownership did not release a held command once.");report.momentaryHandSwitch=true;
                Frame(false);Frame(true);
                var overlay=new GameObject("Non-selectable popup background",typeof(RectTransform),typeof(WorkshopRoundedImage));overlay.transform.SetParent(canvas.transform,false);
                overlay.GetComponent<RectTransform>().sizeDelta=new Vector2(230,80);overlay.GetComponent<WorkshopRoundedImage>().raycastTarget=true;
                Canvas.ForceUpdateCanvases();view.Render();Frame(true);
                Check(held==8&&released==8&&!workButton.IsHeld&&image.BorderWidth<2,"Hover padding or pointer capture reached a button behind an overlay.");
                UnityEngine.Object.DestroyImmediate(overlay);Canvas.ForceUpdateCanvases();Frame(true);Frame(false);
                Check(held==8&&released==8,"Removing an overlay restarted a held trigger.");report.overlayBlocksHeldButton=true;
                workButton.Pressed-=onHeld;workButton.Released-=onReleased;workButton.Momentary=false;
                // Dragging is constrained to reachable cabinet height; the click fixture used y=3.
                Vector3 panelPosition=canvas.transform.position;panelPosition.y=2.4f;canvas.transform.position=panelPosition;
                var handle=new GameObject("Fixture drag handle",typeof(RectTransform),typeof(WorkshopRoundedImage),typeof(WorkshopPanelGesture));handle.transform.SetParent(canvas.transform,false);
                var handleRect=handle.GetComponent<RectTransform>();handleRect.sizeDelta=new Vector2(100,40);handleRect.localPosition=new Vector3(-140,70,0);
                var gesture=handle.GetComponent<WorkshopPanelGesture>();gesture.canvas=canvas;gesture.content=(RectTransform)canvas.transform;gesture.view=view;
                int hidden=0;gesture.hide=()=>hidden++;
                Canvas.ForceUpdateCanvases();view.Render();
                void Drag(float metres,bool cancel=false)
                {
                    Vector3 point=handle.transform.position;
                    beam=new Ray(view.transform.position,point-view.transform.position);Frame(false);Frame(true);
                    beam=new Ray(view.transform.position,point+Vector3.right*metres-view.transform.position);Frame(true);
                    Frame(false,!cancel);
                }
                Vector3 before=canvas.transform.position;Drag(.12f);
                Debug.Log("[Panel gesture check] displacement="+Vector3.Distance(before,canvas.transform.position)+"; hidden="+hidden+"; dragging="+gesture.IsDragging);
                Check(Vector3.Distance(before,canvas.transform.position)>.08f&&hidden==0,"Slow/small header drag did not move panel.");report.panelDragged=true;
                Drag(.5f);Check(hidden==0,"Ordinary fast title movement dismissed the panel.");report.fastMoveKeptPanel=true;
                gesture.allowSwipe=true;
                Drag(.5f);Check(hidden==1,"Dedicated swipe handle did not dismiss panel.");report.panelSwiped=true;
                Drag(.12f,true);Check(!gesture.IsDragging&&hidden==1,"Focus loss did not cancel drag safely.");report.dragCancelled=true;
                Vector3 start=handle.transform.position;beam=new Ray(view.transform.position,start-view.transform.position);Frame(false);Frame(true);
                beam=new Ray(view.transform.position,start+Vector3.right*.5f-view.transform.position);Frame(true);
                System.Threading.Thread.Sleep(250);Frame(false);
                Check(hidden==1,"Stopping the hand before release still dismissed the panel.");report.pausedDragKeptPanel=true;
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = (error is TargetInvocationException invocation ? invocation.InnerException ?? error : error).ToString();
                Debug.LogError(report.error);
            }
            finally
            {
                foreach (var line in UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None))
                    if (line.name == "Controller UI ray") UnityEngine.Object.DestroyImmediate(line.gameObject);
                UnityEngine.Object.DestroyImmediate(root);
                if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
            }
            report.finishedUtc = DateTime.UtcNow.ToString("O");
            Directory.CreateDirectory("Logs"); File.WriteAllText("Logs/panel-verification.json", JsonUtility.ToJson(report, true));
            Debug.Log("Workshop panel verification " + (report.success ? "PASS" : "FAIL"));
            if (!report.success) throw new InvalidOperationException(report.error);
        }
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}

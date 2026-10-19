using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.XR;
using ViveController = UnityEngine.XR.OpenXR.Features.Interactions.HTCViveControllerProfile.ViveController;

namespace VRWorkshop.Editor
{
    // Edit-mode check of the shipped OpenXR layout and the actual runtime reader.
    // This injects synthetic input; it does not assert a physical VIVE/SteamVR session.
    public static class WorkshopXRInputVerification
    {
        [Serializable] sealed class Report
        {
            public bool success, handIsolation, triggerAndDigitalGrip, rightTrackpad, independentAimPose;
            public bool trackingLoss, disconnectClearsInput, focusTruthTable;
            public bool rightUiFallback, triggerOwnership, handSwitchReleaseGuard, teleportInputIsolation;
            public bool leftTriggerHysteresis;
            public string scope = "Synthetic ViveController layout, Input System state events, runtime ControllerInput reader and pure focus policy; no physical headset";
            public string error, finishedUtc;
        }
        public static void Run() => RunChecks(true);
        // Run without -quit: the final saved-scene Play Mode verification owns exit.
        public static void RunRecovery()
        {
            RunChecks(false);
            WorkshopPanelVerification.Run();
            WorkshopInteractionVerification.Run();
        }
        static void RunChecks(bool exit)
        {
            var report = new Report();
            GameObject root = null;
            ViveController left = null, right = null;
            HeadsetRig.ControllerInput leftReader = null, rightReader = null;
            // The Input System destroys its temporary default settings when replaced.
            // Keep an independent backup so teardown can restore equivalent settings.
            InputSettings originalSettings = UnityEngine.Object.Instantiate(InputSystem.settings), testSettings = null;
            try
            {
                if (EditorApplication.isPlaying) throw new InvalidOperationException("Run in edit mode.");
                // InputActionState intentionally ignores Editor updates. Isolate the
                // package's edit-mode test switch on an unsaved clone; restore below.
                testSettings = UnityEngine.Object.Instantiate(originalSettings);
                InputSystem.settings = testSettings;
                testSettings.SetInternalFeatureFlag("RUN_PLAYER_UPDATES_IN_EDIT_MODE", true);
                // Edit-mode fixtures do not start the OpenXR loader; mirror its base layout registration.
                if (InputSystem.LoadLayout("Haptic") == null)
                    InputSystem.RegisterLayout<UnityEngine.XR.OpenXR.Input.HapticControl>("Haptic");
                // OpenXR's version define selects one of two identically named PoseControl
                // classes. Mirror the type actually compiled into this ViveController.
                Type poseType = typeof(ViveController).GetProperty("pointer").PropertyType;
                InputSystem.RegisterLayout(poseType, "Pose");
                if (InputSystem.LoadLayout("ViveController") == null) InputSystem.RegisterLayout<ViveController>();
                InputSystem.RegisterLayoutMatcher("ViveController", new InputDeviceMatcher()
                    .WithInterface(XRUtilities.InterfaceMatchAnyVersion).WithProduct("HTC Vive Controller OpenXR"));
                // OpenXR profiles are abstract control maps. Real XRInput devices supply
                // descriptors; XRLayoutBuilder assigns the packed, nonoverlapping offsets.
                // Creating the bare profile bypasses that step and overlaps both poses at 0.
                left = AddViveDevice();
                int leftId = left.deviceId;
                right = AddViveDevice();
                // Registering the descriptor for the second identical device rebuilds
                // the shared dynamic layout and can replace the first device instance.
                left = (ViveController)InputSystem.GetDeviceById(leftId);
                InputSystem.SetDeviceUsage(left, UnityEngine.InputSystem.CommonUsages.LeftHand);
                InputSystem.SetDeviceUsage(right, UnityEngine.InputSystem.CommonUsages.RightHand);
                leftReader = new HeadsetRig.ControllerInput("LeftHand"); rightReader = new HeadsetRig.ControllerInput("RightHand");
                root = new GameObject("Synthetic XR input verification");
                Transform leftHand = Child(root, "Left grip"), leftAim = Child(root, "Left aim");
                Transform rightHand = Child(root, "Right grip"), rightAim = Child(root, "Right aim");
                InputSystem.Update();
                Queue(left, .8f, false, true, false, true, new Vector3(-.25f, 1.2f, .3f), Quaternion.Euler(0, 17, 0));
                InputSystem.Update();
                leftReader.Read(leftHand, leftAim); rightReader.Read(rightHand, rightAim);
                Debug.Log("Synthetic Vive controls: " + leftReader.DeviceDescription + "; raw L/R trigger=" +
                    left.trigger.ReadValue() + "/" + right.trigger.ReadValue() + "; reader=" + leftReader.Trigger + "/" + rightReader.Trigger +
                    "; byte offsets trigger/gripPose.position/aim.position=" + left.trigger.stateBlock.byteOffset + "/" +
                    left.devicePose.position.stateBlock.byteOffset + "/" + left.pointer.position.stateBlock.byteOffset);
                Check(Mathf.Abs(leftReader.Trigger - .8f) < .001f && rightReader.Trigger == 0, "Left/right trigger bindings were not isolated.");
                report.handIsolation = true;
                Check(leftReader.GripPressed && leftReader.Grip > .99f, "VIVE digital squeeze/click did not close the gripper.");
                report.triggerAndDigitalGrip = true;
                Check(leftReader.GripTracked && leftReader.AimTracked &&
                    Vector3.Distance(leftHand.localPosition, new Vector3(-.25f, 1.2f, .3f)) < .001f &&
                    Vector3.Distance(leftAim.localPosition, leftHand.localPosition + new Vector3(0, .03f, .08f)) < .001f &&
                    Quaternion.Angle(leftAim.localRotation, Quaternion.Euler(12, 17, 0)) < .05f,
                    "Independent OpenXR aim pose was not applied.");
                report.independentAimPose = true;
                Queue(right, .2f, true, false, true, true, new Vector3(.25f, 1.2f, .3f), Quaternion.identity);
                InputSystem.Update(); rightReader.Read(rightHand, rightAim);
                Check(rightReader.PadPressed && rightReader.Trigger > .99f && !leftReader.PadPressed,
                    "Right trackpad or digital trigger click binding failed.");
                report.rightTrackpad = true;
                Queue(left, 0, false, false, false, false, Vector3.zero, Quaternion.identity);
                InputSystem.Update(); leftReader.Read(leftHand, leftAim);
                Check(!leftReader.GripTracked && !leftReader.AimTracked && leftReader.Trigger == 0 && !leftReader.GripPressed,
                    "Tracking loss or button release retained an active pose/input.");
                report.trackingLoss = true;
                InputSystem.RemoveDevice(right); rightReader.Read(rightHand, rightAim);
                Check(!rightReader.Connected && !rightReader.GripTracked && !rightReader.AimTracked &&
                    rightReader.Trigger == 0 && !rightReader.PadPressed, "Removing the controller retained input.");
                report.disconnectClearsInput = true;
                for (int bits = 0; bits < 16; bits++)
                {
                    bool xr = (bits & 1) != 0, desktop = (bits & 2) != 0, session = (bits & 4) != 0, paused = (bits & 8) != 0;
                    Check(HeadsetRig.EvaluateInputFocus(xr, desktop, session, paused) == (!paused && (xr ? session : desktop)),
                        "Focus policy rejected a valid focused XR session or accepted an unfocused/paused session.");
                }
                report.focusTruthTable = true;
                CheckTriggerRouting(report);
                report.success = true;
            }
            catch (Exception exception) { report.error = exception.ToString(); Debug.LogException(exception); }
            finally
            {
                leftReader?.Dispose(); rightReader?.Dispose();
                if (left != null && left.added) InputSystem.RemoveDevice(left);
                if (right != null && right.added) InputSystem.RemoveDevice(right);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                InputSystem.settings = originalSettings;
                if (testSettings != null) UnityEngine.Object.DestroyImmediate(testSettings);
            }
            report.finishedUtc = DateTime.UtcNow.ToString("O");
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/xr-input-verification.json", JsonUtility.ToJson(report, true));
            Debug.Log("XR Input verification " + (report.success ? "PASS" : "FAIL"));
            if (exit) EditorApplication.Exit(report.success ? 0 : 1);
            else if (!report.success) throw new InvalidOperationException(report.error);
        }
        static Transform Child(GameObject root, string name)
        {
            var child = new GameObject(name).transform; child.SetParent(root.transform, false); return child;
        }
        static void CheckTriggerRouting(Report report)
        {
            var router = new HeadsetRig.TriggerRouter();
            void Read(bool leftAim, float left, float right, bool uiContext, bool pad = false,
                bool valid = true, bool grip = false, bool rightTracked = true) =>
                router.Update(valid, leftAim, true, rightTracked, left, right, false, grip, uiContext, pad);
            Read(false, 0, 0, true); Read(false, 0, 0, true);
            Check(router.Ready, "Right-hand fallback did not arm after a released frame.");
            Read(false, 0, .9f, true);
            Check(router.UiClick && !router.Deadman, "Fallback UI press became a manual deadman.");
            report.rightUiFallback = true;
            // Lease acquired, pointer left the panel: keep the original UI ownership.
            Read(false, 0, .9f, false); Check(router.UiClick && !router.Deadman, "Lease acquisition converted a held click to movement.");
            Read(false, 0, .5f, false); Check(router.UiClick && !router.Deadman, "Partially releasing the trigger lost UI ownership.");
            Read(false, 0, .9f, false); Check(router.UiClick && !router.Deadman, "Re-squeezing a partial UI press started movement.");
            Read(false, 0, 0, false); Read(false, 0, .9f, false);
            Check(!router.UiClick && router.Deadman, "A new manual press did not acquire its own route.");
            Read(false, 0, .9f, true);
            Check(!router.UiClick && router.Deadman, "Manual press changed into a UI click when the ray crossed the panel.");
            report.triggerOwnership = true;
            Read(false, 0, .9f, true, pad: true);
            Check(!router.UiClick && !router.Deadman, "Trackpad did not suppress trigger actions.");
            Read(false, 0, .9f, true);
            Check(!router.UiClick && !router.Deadman, "Releasing only the trackpad rearmed a held trigger.");
            report.teleportInputIsolation = true;
            // A left hand appears while buttons are held: neither route may act.
            Read(true, .9f, .9f, true);
            Check(!router.Ready && !router.UiClick && !router.Deadman, "Hand switch reused a held trigger.");
            Read(true, 0, 0, true, grip: true); Check(!router.Ready, "Held grip did not block hand-switch rearming.");
            Read(true, 0, 0, true); Read(true, .9f, .9f, true);
            Check(router.UiClick && router.Deadman, "Two-controller left UI/right manual mapping changed.");
            Read(true, .9f, .9f, true, valid: false);
            Check(!router.Ready && !router.UiClick && !router.Deadman, "Focus loss kept an active input route.");
            Read(true, .9f, .9f, true); Read(true, .9f, .9f, true);
            Check(!router.Ready && !router.UiClick && !router.Deadman, "Focus recovery accepted held buttons.");
            Read(true, 0, 0, true); Read(true, 0, .9f, false, rightTracked: false);
            Check(!router.Ready && !router.Deadman, "Right tracking loss retained manual input.");
            report.handSwitchReleaseGuard = true;
            // A real VIVE analogue value can chatter around the press threshold.
            // One squeeze must remain one UI press until clearly released.
            var stable = new HeadsetRig.TriggerRouter();
            void Left(float trigger) => stable.Update(true,true,true,true,trigger,0,false,false,true,false);
            Left(0);Left(0);Left(.66f);Check(stable.UiClick,"Left UI did not cross its press threshold.");
            foreach(float value in new[]{.64f,.67f,.63f,.52f,.36f,.64f})
            { Left(value);Check(stable.UiClick,"Left UI trigger chatter split a held press at "+value); }
            Left(.34f);Check(!stable.UiClick,"Left UI failed to release at its release threshold.");
            Left(.50f);Check(!stable.UiClick,"Partial trigger after release generated another press.");
            Left(.70f);Check(stable.UiClick,"A fresh full trigger squeeze did not press again.");
            report.leftTriggerHysteresis=true;
        }
        static ViveController AddViveDevice()
        {
            var features = new List<XRFeatureDescriptor>();
            void Add(string name, FeatureType type) => features.Add(new XRFeatureDescriptor { name = name, featureType = type });
            Add("grip", FeatureType.Axis1D); Add("gripPressed", FeatureType.Binary);
            Add("menu", FeatureType.Binary); Add("select", FeatureType.Binary);
            Add("trigger", FeatureType.Axis1D); Add("triggerPressed", FeatureType.Binary);
            Add("trackpad", FeatureType.Axis2D); Add("trackpadTouched", FeatureType.Binary); Add("trackpadClicked", FeatureType.Binary);
            foreach (string pose in new[] { "devicePose", "pointer" })
            {
                Add(pose + "/isTracked", FeatureType.Binary); Add(pose + "/trackingState", FeatureType.DiscreteStates);
                Add(pose + "/position", FeatureType.Axis3D); Add(pose + "/rotation", FeatureType.Rotation);
                Add(pose + "/velocity", FeatureType.Axis3D); Add(pose + "/angularVelocity", FeatureType.Axis3D);
            }
            var descriptor = new XRDeviceDescriptor { deviceName = "HTC Vive Controller OpenXR", manufacturer = "HTC", inputFeatures = features };
            return (ViveController)InputSystem.AddDevice(new InputDeviceDescription
            {
                interfaceName = XRUtilities.InterfaceCurrent, product = descriptor.deviceName,
                manufacturer = "HTC", capabilities = descriptor.ToJson()
            });
        }
        static void Queue(ViveController controller, float trigger, bool triggerClick, bool grip, bool pad,
            bool tracked, Vector3 position, Quaternion rotation)
        {
            using (StateEvent.From(controller, out var state))
            {
                controller.trigger.WriteValueIntoEvent(trigger, state);
                controller.triggerPressed.WriteValueIntoEvent(triggerClick ? 1f : 0f, state);
                controller.gripPressed.WriteValueIntoEvent(grip ? 1f : 0f, state);
                controller.grip.WriteValueIntoEvent(grip ? 1f : 0f, state);
                controller.trackpadClicked.WriteValueIntoEvent(pad ? 1f : 0f, state);
                controller.devicePose.isTracked.WriteValueIntoEvent(tracked ? 1f : 0f, state);
                controller.devicePose.trackingState.WriteValueIntoEvent(tracked ? 3 : 0, state);
                controller.devicePose.position.WriteValueIntoEvent(position, state);
                controller.devicePose.rotation.WriteValueIntoEvent(rotation, state);
                controller.pointer.isTracked.WriteValueIntoEvent(tracked ? 1f : 0f, state);
                controller.pointer.trackingState.WriteValueIntoEvent(tracked ? 3 : 0, state);
                controller.pointer.position.WriteValueIntoEvent(position + new Vector3(0, .03f, .08f), state);
                controller.pointer.rotation.WriteValueIntoEvent(rotation * Quaternion.Euler(12, 0, 0), state);
                InputSystem.QueueEvent(state);
            }
        }
        static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    }
}

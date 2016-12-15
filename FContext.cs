﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using g3;

namespace f3 {

    public class FContext {

        // [TODO] would like to get rid of this...but in a few places it was clunky to keep a reference
        public static FContext ActiveContext_HACK;


        SceneOptions options;
        FScene scene;                            // set of objects in our universe
        VRMouseCursorController mouseCursor;		// handles mouse cursor interaction in VR
        SpatialInputController spatialCursor;	// handles spatial interaction in VR
        CameraTracking camTracker;              // tracks some camera stuff that we probably could just put here now...
        TransformManager transformManager;      // manages transform gizmos
        ToolManager toolManager;                // manages active tools

        ICameraInteraction mouseCamControls;    // camera hotkeys and interactions
        bool bInCameraControl;

        Capture captureMouse;                   // current object that is capturing mouse/gamepad input

        Capture captureLeft;                    // current object capturing left spatial controller input
        Capture captureRight;                   // ditto right

        Cockpit activeCockpit;                  // (optional) HUD that kind of sticks to view
        Stack<Cockpit> cockpitStack;

        InputBehaviorSet inputBehaviors;        // behaviors that can capture left/right input
        InputBehaviorSet overrideBehaviors;     // behaviors that do not capture, just respond
                                                // to specific events (like menu button)


        public TransformManager TransformManager {
            get { return this.transformManager; }
        }

        public ToolManager ToolManager {
            get { return this.toolManager; }
        }

        // [RMS] why do it this way? can't we just create in Start?
        public FScene Scene {
			get { return GetScene (); }
		}
		public FScene GetScene() {
			if (scene == null)
				scene = new FScene (this);
			return scene;
		}
        public Camera ActiveCamera
        {
            get { return Camera.main; }
        }

		public Cockpit ActiveCockpit { 
			get { return activeCockpit; }
		}

		public VRMouseCursorController MouseController {
            get {
                if (mouseCursor == null)
                    mouseCursor = new VRMouseCursorController(ActiveCamera, this);
                return mouseCursor;
            }
		}
        public SpatialInputController SpatialController
        {
            get {
                if (spatialCursor == null)
                    spatialCursor = new SpatialInputController(options.SpatialCameraRig, ActiveCamera, this);
                return spatialCursor;
            }
        }
        public InputDevice ActiveInputDevice = InputDevice.Mouse;

        public ICameraInteraction MouseCameraController
        {
            get { return this.mouseCamControls; }
            set { this.mouseCamControls = value; }
        }



        // Use this for initialization
        public void Start(SceneOptions options)
        {
            this.options = options;

            DebugUtil.LogLevel = options.LogLevel;

            InputExtension.Get.Start();

            GetScene();
            if (options.SceneInitializer != null)
                options.SceneInitializer.Initialize(GetScene());

            transformManager = new TransformManager();
            if (options.EnableTransforms) {
                transformManager.Initialize(this);
            }

            toolManager = new ToolManager();
            toolManager.Initialize(this);
            toolManager.OnToolActivationChanged += OnToolActivationChanged;

            MouseController.Start();
            SpatialController.Start();

            // intialize camera stuff
            camTracker = new CameraTracking();
            camTracker.Initialize(this);
            // [RMS] hardcode starting cam target point to origin
            ActiveCamera.gameObject.GetComponent<CameraTarget>().TargetPoint = Vector3.zero;

            if (options.MouseCameraControls != null)
                MouseCameraController = options.MouseCameraControls;

            // apply initial transformation to scene
            ActiveCamera.Manipulator().SceneTranslate(Scene, SceneGraphConfig.InitialSceneTranslate);

            // create behavior sets
            inputBehaviors = new InputBehaviorSet();
            overrideBehaviors = new InputBehaviorSet();

            // cockpit needs to go last because UI setup may depend on above
            cockpitStack = new Stack<Cockpit>();
            if (options.EnableCockpit)
                PushCockpit(options.CockpitInitializer);


            captureMouse = null;
            captureLeft = captureRight = null;
            bInCameraControl = false;

			// [RMS] this locks cursor to game unless user presses escape or exits
			Cursor.lockState = CursorLockMode.Locked;

            // set hacky hackenstein global
            ActiveContext_HACK = this;

            startup_checks();
        }


        // Update is called once per frame
        public void Update() {

            FPlatform.IncrementFrameCounter();

            // update our wrappers around various different Input modes
            InputExtension.Get.Update();

            // update cockpit tracking and let UI do per-frame rendering computations
            if (options.EnableCockpit) 
                ActiveCockpit.Update();

            // hardcoded Q key quits app
            if (Input.GetKeyUp(KeyCode.Q)) {
                Cursor.lockState = CursorLockMode.None;
                GlobalControl.Quit();
            }

            // can either use spacecontrols or mouse, but not both at same time
            // [TODO] ask spatial input controller instead, it knows better (?)
            if (SpatialController.CheckForSpatialInputActive()) {
                Configure_SpaceControllers();
                HandleInput_SpaceControllers();
            } else {
                Configure_MouseOrGamepad();
                HandleInput_MouseOrGamepad();
            }

            // after we have handled input, do per-frame rendering computations
            if (options.EnableCockpit)
                ActiveCockpit.PreRender();
            ToolManager.PreRender();
            Scene.PreRender();
        }


        void Configure_SpaceControllers()
        {
            SceneGraphConfig.ActiveDoubleClickDelay = SceneGraphConfig.TriggerDoubleClickDelay;
            ActiveInputDevice = InputDevice.OculusTouch;
        }

        void HandleInput_SpaceControllers()
        {
            // update cursors
            SpatialController.Update();
            MouseController.HideCursor();

            // have to do this after cursor update in case hotkey uses mouse position
            DoShortcutkeys();

            // create our super-input object  (wraps all supported input types)
            InputState input = new InputState();
            input.Initialize_Oculus(this);


            // run override behaviors
            overrideBehaviors.SendOverrideInputs(input);

 
            input.LeftCaptureActive = (captureLeft != null);
            input.RightCaptureActive = (captureRight != null);

            // update left-capture
            if (captureLeft != null) {
                Capture cap = captureLeft.element.UpdateCapture(input, captureLeft.data);
                if (cap.state == CaptureState.Continue) {
                    // (carry on)
                } else if (cap.state == CaptureState.End) {
                    DebugUtil.Log(10, "[SceneController] released left capture " + captureLeft.element.CaptureIdentifier);
                    if (captureRight == captureLeft)
                        captureRight = null;        // if we are doing a dual-capture, we only want to end once!!
                    captureLeft = null;
                }
            }

            // update right-capture
            // if we are doing a both-capture, we only want to send update once
            if ( captureRight != null && captureRight != captureLeft ) {
                Capture cap = captureRight.element.UpdateCapture(input, captureRight.data);
                if (cap.state == CaptureState.Continue) {
                    // (carry on)
                } else if (cap.state == CaptureState.End) {
                    DebugUtil.Log(10, "[SceneController] released right capture " + captureRight.element.CaptureIdentifier);
                    captureRight = null;
                }
            }

            // if we have a free device, check for capture. 
            bool bCanCapture = (bInCameraControl == false);
            if (bCanCapture && (captureLeft == null || captureRight == null) ) {

                // collect up capture requests 
                List<CaptureRequest> vRequests = new List<CaptureRequest>();
                inputBehaviors.CollectWantsCapture(input, vRequests);
                if ( vRequests.Count > 0 ) {

                    // end outstanding hovers
                    TerminateHovers(input);

                    // select one of the capture requests. technically we could end
                    //  up with none successfully Begin'ing, but behaviors should be
                    //  doing those checks in WantCapture, not BeginCapture !!
                    vRequests.OrderBy(x => x.element.Priority);
                    Capture capReq = null;
                    for ( int i = 0; i < vRequests.Count && capReq == null; ++i ) {

                        // filter out invalid requests
                        CaptureSide eUseSide = vRequests[i].side;
                        if (eUseSide == CaptureSide.Any)        // replace Any with Both. Does that make sense??
                            eUseSide = CaptureSide.Both;
                        if ( (eUseSide == CaptureSide.Left || eUseSide == CaptureSide.Both) 
                               && captureLeft != null)
                            continue;
                        if ( (eUseSide == CaptureSide.Right || eUseSide == CaptureSide.Both)
                               && captureRight != null)
                            continue;

                        Capture c = vRequests[i].element.BeginCapture(input, eUseSide);
                        if (c.state == CaptureState.Begin)
                            capReq = c;
                    }

                    if (capReq != null) {
                        // technically we only should terminate hover on capture controller,
                        // but that seems really hard. This will clear hovers but they will
                        // come back next frame. Perhaps revisit if this is causing flicker...
                        TerminateHovers(input);

                        // [RMS] most of this checking is redundant now, but leaving because of debug logging
                        if (capReq.data.which == CaptureSide.Left) {
                            if (captureLeft != null) {
                                DebugUtil.Warning("[SceneController.HandleInput_SpaceControllers] received Capture request for Left side from {0}, but already capturing! Ignoring.", capReq.element.CaptureIdentifier);
                            } else {
                                captureLeft = capReq;
                                DebugUtil.Log(10, "[SceneController] began left-capture" + captureLeft.element.CaptureIdentifier);
                            }
                        } else if (capReq.data.which == CaptureSide.Right) {
                            if (captureRight != null) {
                                DebugUtil.Warning("[SceneController.HandleInput_SpaceControllers] received Capture request for Right side from {0}, but already capturing! Ignoring.", capReq.element.CaptureIdentifier);
                            } else {
                                captureRight = capReq;
                                DebugUtil.Log(10, "[SceneController] began right-capture" + captureRight.element.CaptureIdentifier);
                            }
                        } else if (capReq.data.which == CaptureSide.Both || capReq.data.which == CaptureSide.Any) {
                            if (captureLeft != null || captureRight != null) {
                                DebugUtil.Warning("[SceneController.HandleInput_SpaceControllers] received Capture request for both sides from {0}, but already capturing! Ignoring.", capReq.element.CaptureIdentifier);
                            } else {
                                captureLeft = captureRight = capReq;
                                DebugUtil.Log(10, "[SceneController] began both-capture " + captureLeft.element.CaptureIdentifier);
                            }
                        }
                    }
                }
            }

            // update hover if we have a free device
            if ( captureLeft == null || captureRight == null )
                inputBehaviors.UpdateHover(input);
            
        }



        void Configure_MouseOrGamepad()
        {
            SceneGraphConfig.ActiveDoubleClickDelay = SceneGraphConfig.MouseDoubleClickDelay;
            ActiveInputDevice = InputDevice.Mouse | InputDevice.Gamepad;
        }

        void HandleInput_MouseOrGamepad()
        {
            // update mouse/gamepad cursor
            MouseController.Update();

            // have to do this after cursor update in case hotkey uses mouse position
            DoShortcutkeys();

            // create our super-input object  (wraps all supported input types)
            InputState input = new InputState();
            input.Initialize_MouseGamepad(this);

            CameraInteractionState eCamState = (MouseCameraController != null) 
                ? MouseCameraController.CheckCameraControls(input) : CameraInteractionState.Ignore;
            if (eCamState == CameraInteractionState.BeginCameraAction) {
                TerminateHovers(input);

                bInCameraControl = true;
                ActiveCamera.gameObject.GetComponent<CameraTarget>().ShowTarget = true;
            } else if (eCamState == CameraInteractionState.EndCameraAction) {
                bInCameraControl = false;
                ActiveCamera.gameObject.GetComponent<CameraTarget>().ShowTarget = false;
            } else if (bInCameraControl) {
                ActiveCamera.gameObject.GetComponent<CameraTarget>().ShowTarget = true;
                MouseCameraController.DoCameraControl(Scene, ActiveCamera, input);


            } else {
                input.MouseGamepadCaptureActive = (captureMouse != null);

                if (InCaptureMouse) {
                    Capture cap = captureMouse.element.UpdateCapture(input, captureMouse.data);
                    if (cap.state == CaptureState.Continue) {
                        // (carry on)
                    } else if (cap.state == CaptureState.End) {
                        captureMouse = null;
                    }

                } else {

                    // this is very simplistic...needs to be rewritten like space controllers

                    List<CaptureRequest> vRequests = new List<CaptureRequest>();
                    inputBehaviors.CollectWantsCapture(input, vRequests);
                    if (vRequests.Count > 0) {

                        // end outstanding hovers
                        TerminateHovers(input);

                        // select one of the capture requests. technically we could end
                        //  up with none successfully Begin'ing, but behaviors should be
                        //  doing those checks in WantCapture, not BeginCapture !!
                        vRequests.OrderBy(x => x.element.Priority);
                        Capture capReq = null;
                        for (int i = 0; i < vRequests.Count && capReq == null; ++i) {
                            if (vRequests[i].side != CaptureSide.Any)
                                continue;       // not possible in mouse paths...
                            Capture c = vRequests[i].element.BeginCapture(input, vRequests[i].side);
                            if (c.state == CaptureState.Begin)
                                capReq = c;
                        }

                        captureMouse = capReq;
                    }
                }

                // if we don't have a capture, do hover
                if (captureMouse == null)
                    inputBehaviors.UpdateHover(input);

            }
        }







        public bool InCaptureMouse {
            get { return (captureMouse != null); }
        }
        public bool InCameraManipulation
        {
            get { return bInCameraControl;  }
        }

        void TerminateHovers(InputState input)
        {
            inputBehaviors.EndHover(input);
        }



        public void PushCockpit(ICockpitInitializer initializer)
        {
            Cockpit trackingInitializer = null;
            if (activeCockpit != null) {
                trackingInitializer = activeCockpit;
                inputBehaviors.Remove(activeCockpit.InputBehaviors);
                overrideBehaviors.Remove(activeCockpit.OverrideBehaviors);
                cockpitStack.Push(activeCockpit);
                activeCockpit.RootGameObject.SetActive(false);
            }

            Cockpit c = new Cockpit(this);
            c.Start(initializer);
            if (trackingInitializer != null)
                c.InitializeTracking(trackingInitializer);
            inputBehaviors.Add(c.InputBehaviors);
            overrideBehaviors.Add(c.OverrideBehaviors);
            activeCockpit = c;

            mouseCursor.ResetCursorToCenter();
        }
        public void PopCockpit(bool bDestroy)
        {
            if (activeCockpit != null) {
                inputBehaviors.Remove(activeCockpit.InputBehaviors);
                overrideBehaviors.Remove(activeCockpit.OverrideBehaviors);
                activeCockpit.RootGameObject.SetActive(false);
                if (bDestroy)
                    activeCockpit.Destroy();
                activeCockpit = null;
            }

            activeCockpit = cockpitStack.Pop();
            if (activeCockpit != null) {
                activeCockpit.RootGameObject.SetActive(true);
                inputBehaviors.Add(activeCockpit.InputBehaviors);
                overrideBehaviors.Add(activeCockpit.OverrideBehaviors);
            }

            mouseCursor.ResetCursorToCenter();
        }




        protected virtual void OnToolActivationChanged(ITool tool, ToolSide eSide, bool bActivated)
        {
            if (bActivated)
                inputBehaviors.Add(tool.InputBehaviors);
            else
                inputBehaviors.Remove(tool.InputBehaviors);
        }




        // remove all scene stuff and reset view to default
        public void NewScene()
        {
            if (InCameraManipulation)
                return;     // not supported yet

            Scene.RemoveAllSceneObjects();
            Scene.RemoveAllUIElements();
            ResetView();
        }

        public void ResetView()
        {
            Scene.SetSceneScale(1.0f);
            ActiveCamera.Manipulator().ResetSceneOrbit(Scene, true, true, true);
            // [RMS] above should already do this, but sometimes it gets confused..
            Scene.RootGameObject.transform.rotation = Quaternion.identity;
            ActiveCamera.Manipulator().ResetScenePosition(scene);
            ActiveCamera.Manipulator().SceneTranslate(Scene, SceneGraphConfig.InitialSceneTranslate);
        }

        public void ScaleView(Vector3 vCenterW, float fRadiusW )
        {
            //Vector3f camTarget = ActiveCamera.GetTarget();
            //Vector3f localTarget = Scene.WorldFrame.ToFrameP(camTarget);
            Vector3f vDeltaOrig = Scene.SceneFrame.ToFrameP(vCenterW);

            ActiveCamera.gameObject.GetComponent<CameraManipulator>().ResetSceneOrbit(
                Scene, false, true, true);

            float fCurScale = Scene.GetSceneScale();

            Frame3f cockpitF = ActiveCockpit.GetLevelViewFrame(CoordSpace.WorldCoords);
            float fScale = 1.0f / fRadiusW;
            vDeltaOrig *= fScale;
            Frame3f deskF = cockpitF.Translated(1.2f, 2).Translated(-0.5f, 1).Translated(-vDeltaOrig);
            Scene.SceneFrame = deskF;
            Scene.SetSceneScale(fCurScale * fScale);
            Vector3f newTarget = Scene.SceneFrame.Origin + vDeltaOrig;
            ActiveCamera.SetTarget(newTarget);
        }




        public bool FindUIHit(Ray eyeRay, out UIRayHit bestHit)
        {
			bestHit = new UIRayHit();
			UIRayHit sceneHit = null, cockpitHit = null;

            bool bCockpitOnly = (options.EnableCockpit && activeCockpit.GrabFocus);
			if (bCockpitOnly == false && scene.FindUIRayIntersection(eyeRay, out sceneHit) ) {
				bestHit = sceneHit;
			}
			if ( options.EnableCockpit && activeCockpit.FindUIRayIntersection(eyeRay, out cockpitHit) ) {
				if ( cockpitHit.fHitDist < bestHit.fHitDist )
					bestHit = cockpitHit;
			}
			return bestHit.IsValid;
		}


        public bool FindUIHoverHit(Ray eyeRay, out UIRayHit bestHit)
        {
            bestHit = new UIRayHit();
            UIRayHit sceneHit = null, cockpitHit = null;

            bool bCockpitOnly = (options.EnableCockpit && activeCockpit.GrabFocus);
            if (bCockpitOnly == false && scene.FindUIHoverRayIntersection(eyeRay, out sceneHit)) {
                bestHit = sceneHit;
            }
            if (options.EnableCockpit && activeCockpit.FindUIHoverRayIntersection(eyeRay, out cockpitHit)) {
                if (cockpitHit.fHitDist < bestHit.fHitDist)
                    bestHit = cockpitHit;
            }
            return bestHit.IsValid;
        }


        public bool FindAnyRayIntersection(Ray eyeRay, out AnyRayHit anyHit)
        {
			anyHit = new AnyRayHit ();
			AnyRayHit sceneHit = null;
			UIRayHit cockpitHit = null;

            bool bCockpitOnly = (options.EnableCockpit && activeCockpit.GrabFocus);

            if (bCockpitOnly == false && scene.FindAnyRayIntersection (eyeRay, out sceneHit)) {
				anyHit = sceneHit;
			}
			if (options.EnableCockpit && activeCockpit.FindUIRayIntersection (eyeRay, out cockpitHit)) {
				if (cockpitHit.fHitDist < anyHit.fHitDist)
					anyHit = new AnyRayHit(cockpitHit);
			}
			return anyHit.IsValid;
		}










		bool DoShortcutkeys() {

            if (options.EnableCockpit) {
                bool bHandled = activeCockpit.HandleShortcutKeys();
                if (bHandled)
                    return true;
            }

            return false;
		}



        // this only makes sense in 2D, right?
        public Ray GetWorldRayAt2DViewCenter() {
			Ray eyeRay = ActiveCamera.ViewportPointToRay(new Vector3(0.5F, 0.5F, 0));
			return eyeRay;
		}

		public Ray GetWorldRayAtMouseCursor() {
			Vector3 camPos = MouseController.CurrentCursorRaySourceWorld;
			Vector3 cursorPos = MouseController.CurrentCursorPosWorld;
			Ray ray = new Ray (camPos, (cursorPos - camPos).normalized);
            if (Mathf.Abs(ray.direction.sqrMagnitude - 1) > 0.001f)
                ray = new Ray(camPos, Vector3.up);
			return ray;
		}




        void startup_checks()
        {
        }



	} // end SceneController

} // end namespace
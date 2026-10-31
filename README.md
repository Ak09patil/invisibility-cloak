# Team 4: AR/VR Course Projects

This repository holds two projects: (1) a gesture-controlled invisibility AR app and (2) a VR interview simulator.

---

# Project 1: Invisibility AR (Phone + Laptop)

## Gesture-Controlled Multi-Person Invisibility AR (Phone Client + Laptop Server)

A diminished-reality ("invisibility") filter. A phone app streams its own camera to a laptop server. The laptop runs the AI, and the phone shows people vanishing, each controlled by their own hand gesture, with a neon glow effect.

Built for the IVRAR (AR/VR) course.

### How it works
Phone camera -> Unity app (phone) --JPEG frames--> Python server (laptop)
| YOLOv8-seg: per-person masks
| MediaPipe Hands: gestures
| tracking + per-person state
Phone screen <- Unity shader (GPU) <--bg, frame, masks, JSON--+

- **Perception (Python, laptop):** pretrained YOLOv8n-seg gives a mask per person. MediaPipe Hands gives 21 landmarks per hand. Our own logic handles tracking, finger-extension gesture rules, a majority vote over the last 6 frames, and matching each hand to a person.
- **Rendering (Unity, phone):** a custom shader blends the live frame with a stored clean background per person, with a noise dissolve and neon glow. Everything is drawn on the phone's GPU.
- **Link:** TCP, port 5055. Each message is a 4-byte little-endian length followed by the payload. Phone to laptop: JPEG camera frames and text commands. Laptop to phone: background JPEG, a frame (JPEG + RGBA PNG of masks + JSON), and status text. The four PNG channels hold one person each, so the maximum is 4 people.

### Why it is AR
It runs live on a camera feed, reacts to the user in real time, and combines real and virtual content (the real scene, the stored background, and the virtual dissolve and glow). Registration is image-plane only, so the camera must stay still after the background is captured. This is a known limitation. Moving-camera support would need feature matching with a homography, or ARKit / ARCore.

### Gestures (per person)
| Gesture | Effect |
|---|---|
| Open palm | That person vanishes |
| Fist | That person reappears |
| Peace sign | Next effect for that person |

### Repository layout
ar-phone/
python/
invisibility_server.py laptop server for the phone app (run this)
invisibility_ar2.py core logic: tracking, gestures, masks, effects
invisibility_unity.py helpers + older “Unity on the same laptop” mode
invisibility_ar.py first single-person version (fallback)
unity/ Unity project (Assets, ProjectSettings, Packages)
Assets/Invisibility/
Invisibility.shader GPU compositing and effects
InvisibilityPhone.cs phone client: camera, streaming, UI

### Run it (Mac)

### 1. Laptop server
cd ar-phone/python
python3 -m venv gesture_env && source gesture_env/bin/activate
pip install opencv-python numpy mediapipe ultralytics
python invisibility_server.py
The first run downloads the YOLOv8n-seg weights. The terminal prints the laptop's IP address.

### 2. Phone app
Download `InvisibilityAR.apk` from the **Releases** page, or build it from the `unity/` project: open it in Unity 6, switch to Android in Build Profiles, set Internet Access to Require and Default Orientation to Landscape Left, then Build. Install the APK on an Android phone (allow installs from unknown sources).

### 3. Connect
Put the phone and laptop on the same network (a phone hotspot is the most reliable, as many campus networks block device-to-device traffic). In the app: type the laptop IP, tap **Connect**, tap **CAPTURE BACKGROUND**, step out of the frame for about 5 seconds, step back in, and show an open palm.

### App buttons
- **Connect / Disconnect** - start or stop the link to the laptop.
- **CAPTURE BACKGROUND** - store a clean photo of the empty scene.
- **Switch cam / Rotate 90 / Mirror** - fix which camera is used and the picture orientation.
- **Vanish** - manual vanish or reappear for everyone.
- **Effect** - cycle Shimmer / Neon glow / Hard cut.
- **Person/Cloth** - vanish the whole body, or only a calibrated cloth colour (calibrated on the laptop with `m` and `c`).

### Laptop keys (preview window)
`b` background, `m` mode, `c` calibrate cloth (then click), `e` effect, `space` vanish all, `q` quit.

### Limitations
- Static camera after the background is captured.
- At most 4 people at once.
- About 8-10 fps on a laptop CPU, plus a short network delay.
- Person IDs can reset if people cross or leave and re-enter.
- Everyone must be out of the frame while the background is captured.

### Credits
- YOLOv8 segmentation (Ultralytics) and MediaPipe Hands (Google) are used as pretrained models for inference only. Nothing was trained.
- Parts of the code were written with an AI coding assistant. The team integrated, tested and can explain the whole pipeline.

---

# Project 2: VR Interview Simulator (Meta Quest)

A virtual reality interview practice app built in Unity for Meta Quest. The user steps into a simulated interview room, faces a virtual interviewer, and answers interview questions by pointing at and clicking answer buttons with the VR controllers. The app scores the answers, so users can rehearse in a low-pressure setting before a real interview.

Built for the IVRAR (AR/VR) course.

## What it does
- Generates a virtual interview room with an interviewer and world-space UI panels (built automatically by an editor script).
- A **START INTERVIEW** button begins the session.
- The interviewer asks a series of questions, each with **4 answer buttons**.
- The interview manager tracks the **score**, moves between question and result panels, and plays **audio**.
- A results panel shows the outcome at the end.

## Tech used
- **Unity 6.3 LTS** (6000.3.x), with Android Build Support for Quest
- **OpenXR** with **Unity OpenXR: Meta**
- **XR Interaction Toolkit (XRI 3.x)**: ray interactors click the world-space UI, so the Meta XR All-in-One SDK is not needed
- Works with Quest 2, 3, 3S and Pro
- TextMesh Pro for the on-screen text

## Key scripts (`Assets/Scripts`, `Assets/Editor`)
| Script | Role |
|---|---|
| `InterviewManager.cs` | Questions, scoring, panels and audio |
| `AnswerButton.cs` | The four answer buttons |
| `StartInterview.cs` | The START INTERVIEW button |
| `InterviewSceneBuilder.cs` (Editor) | Adds a **VR Interview** menu that builds the room, interviewer, UI and wiring in one click |

## Repository layout
- `Assets/`, `Packages/`, `ProjectSettings/` - the Unity project
- `VRInterviewSimulator_Files/` - the original script files and `SETUP_GUIDE.md`
- `SETUP_GUIDE.md` - full step-by-step setup for Unity and the Quest

## How to run
Follow `SETUP_GUIDE.md`. In short:
1. Install Unity 6.3 LTS with Android Build Support (OpenJDK, Android SDK and NDK).
2. Open the project, use the **VR Interview** menu to build the scene, and configure OpenXR with the Meta feature group.
3. Build and run to a Meta Quest headset (or test in the editor with an XR simulator).

## Team contributions

**Team 4:** Akanksha Patil, Akshaya Epuri, Akhila Kuruva

Work was divided equally: each member owns one part of the AR project and one part of the VR project, and presents and explains both.

| Member | Project 1: Invisibility AR | Project 2: VR Interview Simulator |
|---|---|---|
| **Akanksha Patil** | Perception: YOLOv8 person masks, tracking, background capture (`invisibility_ar2.py`) | Interview logic: questions, scoring, results panel (`InterviewManager.cs`) |
| **Akshaya Epuri** | Gestures and link: hand gestures, hand-to-person matching, Python-to-phone protocol (`invisibility_server.py`) | Scene and environment: interview room, interviewer, scene builder (`InterviewSceneBuilder.cs`) |
| **Akhila Kuruva** | Unity phone app: shader, effects, camera streaming UI (`Invisibility.shader`, `InvisibilityPhone.cs`) | UI and XR: answer buttons, start button, OpenXR/XRI setup, Quest build and testing (`AnswerButton.cs`, `StartInterview.cs`) |

**Shared by all three:** testing, the demo, documentation, and the final presentation.

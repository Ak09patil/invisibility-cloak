# Gesture-Controlled Multi-Person Invisibility AR (Phone Client + Laptop Server)

A diminished-reality ("invisibility") filter. A phone app streams its own camera to a laptop server. The laptop runs the AI, and the phone shows people vanishing, each controlled by their own hand gesture, with a neon glow effect.

Built for the IVRAR (AR/VR) course.

## How it works
Phone camera -> Unity app (phone) --JPEG frames--> Python server (laptop)
| YOLOv8-seg: per-person masks
| MediaPipe Hands: gestures
| tracking + per-person state
Phone screen <- Unity shader (GPU) <--bg, frame, masks, JSON--+

- **Perception (Python, laptop):** pretrained YOLOv8n-seg gives a mask per person. MediaPipe Hands gives 21 landmarks per hand. Our own logic handles tracking, finger-extension gesture rules, a majority vote over the last 6 frames, and matching each hand to a person.
- **Rendering (Unity, phone):** a custom shader blends the live frame with a stored clean background per person, with a noise dissolve and neon glow. Everything is drawn on the phone's GPU.
- **Link:** TCP, port 5055. Each message is a 4-byte little-endian length followed by the payload. Phone to laptop: JPEG camera frames and text commands. Laptop to phone: background JPEG, a frame (JPEG + RGBA PNG of masks + JSON), and status text. The four PNG channels hold one person each, so the maximum is 4 people.

## Why it is AR
It runs live on a camera feed, reacts to the user in real time, and combines real and virtual content (the real scene, the stored background, and the virtual dissolve and glow). Registration is image-plane only, so the camera must stay still after the background is captured. This is a known limitation. Moving-camera support would need feature matching with a homography, or ARKit / ARCore.

## Gestures (per person)
| Gesture | Effect |
|---|---|
| Open palm | That person vanishes |
| Fist | That person reappears |
| Peace sign | Next effect for that person |

## Repository layout
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

## Run it (Mac)

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

## Limitations
- Static camera after the background is captured.
- At most 4 people at once.
- About 8-10 fps on a laptop CPU, plus a short network delay.
- Person IDs can reset if people cross or leave and re-enter.
- Everyone must be out of the frame while the background is captured.

## Credits
- YOLOv8 segmentation (Ultralytics) and MediaPipe Hands (Google) are used as pretrained models for inference only. Nothing was trained.
- Parts of the code were written with an AI coding assistant. The team integrated, tested and can explain the whole pipeline.

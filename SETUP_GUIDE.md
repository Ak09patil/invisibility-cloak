# VR Interview Simulator – Complete Setup Guide (Meta Quest, Unity 6.3 LTS)

**What you get in this folder**

```
VRInterviewSimulator_Files
├── SETUP_GUIDE.md                       <- this file
└── Assets
    ├── Scripts
    │   ├── InterviewManager.cs          (questions, scoring, panels, audio)
    │   ├── AnswerButton.cs              (the 4 answer buttons)
    │   └── StartInterview.cs            (START INTERVIEW button)
    └── Editor
        └── InterviewSceneBuilder.cs     (menu "VR Interview": builds room, interviewer, UI, wiring)
```

**Technology choice (and why):** Unity **OpenXR** + **XR Interaction Toolkit (XRI 3.x)** + **Unity OpenXR: Meta**.
This is free, works with Quest 2/3/3S/Pro, and its ray interactors click World Space UI out of the box.
You do **not** need the Meta XR All-in-One SDK for this project.

---

## STAGE 1 – Install Unity and prepare the Quest

### 1.1 Unity version
Install **Unity 6.3 LTS (6000.3.x)** – the latest Long Term Support release. (Unity 6.0 LTS 6000.0.x also works.)
Do **not** use Unity 2021/2022 for this guide.

### 1.2 Unity Hub modules (tick these when installing)
1. Open **Unity Hub** → **Installs** → **Install Editor** → pick **6000.3.x LTS**.
2. Tick:
   - **Android Build Support**
     - **OpenJDK**
     - **Android SDK & NDK Tools**
3. Click **Install**. (Windows/Mac Build Support is optional.)

### 1.3 Packages you need
| Package | Purpose | How you get it |
|---|---|---|
| XR Plugin Management | turns XR on | in the VR template |
| **OpenXR Plugin** (`com.unity.xr.openxr`) | OpenXR runtime | in the VR template |
| **Unity OpenXR: Meta** (`com.unity.xr.meta-openxr`) | Quest-specific OpenXR features (Meta recommends it for Quest) | Package Manager (step 2.3) |
| **XR Interaction Toolkit 3.x** (`com.unity.xr.interaction.toolkit`) | rig, controllers, ray + UI interaction | in the VR template |
| **Input System** | controller input | in the VR template |
| **Unity UI (uGUI) + TextMeshPro** | World Space UI text | in the VR template |

### 1.4 Meta Quest SDK
For this project: **none required** beyond the packages above. (Optional later: Meta XR Core SDK, if you want Meta-only features.)

### 1.5 Prepare the headset (one time)
1. Create a free account at **developers.meta.com** and create a **developer organization** (needed to unlock Developer Mode).
2. Install the **Meta Horizon** app on your phone, sign in with the same account, pair the Quest.
3. In the app: **Devices → your headset → Headset Settings → Developer Mode → ON**. Restart the headset.
4. Use a good **USB-C data cable** (not charge-only).
5. Windows: install **Meta Quest Developer Hub (MQDH)** – it installs the USB driver and makes device setup easier.

---

## STAGE 2 – Create the project and folders

### 2.1 Create the project from the VR template
1. Unity Hub → **Projects → New project**.
2. Choose Editor **6000.3.x**. In the template list pick **VR** ("VR Template", Universal 3D/URP). Click **Download template** if asked.
3. **Project name:** `VRInterviewSimulator`. Choose a location. Click **Create project**.

> The VR template already has: XR Origin rig, XRI, OpenXR, Input System, controllers with ray interactors.

### 2.2 Import TextMeshPro resources (needed once)
Menu: **Window → TextMeshPro → Import TMP Essential Resources** → **Import**. (Skip if the option is greyed out or already done.)

### 2.3 Add Unity OpenXR: Meta
**Window → Package Management → Package Manager** → top-left dropdown **Unity Registry** → search **Unity OpenXR: Meta** → **Install**.

### 2.4 Copy the files in
1. In your file explorer open `VRInterviewSimulator_Files/Assets`.
2. Drag the **Scripts** and **Editor** folders into Unity's **Project** window onto the **Assets** folder.
3. Wait for Unity to compile (spinner bottom-right). The Console (Ctrl+Shift+C) must show **no red errors**.
4. A new menu **VR Interview** appears in the top menu bar.
5. Click **VR Interview → 1 - Create Folder Structure**. This creates `Scenes, Scripts, Materials, Models, UI, Audio, Editor`.

---

## STAGE 3 – Create the InterviewRoom scene

1. In **Project** window open `Assets/Scenes` and double-click the template's sample scene (e.g. **SampleScene**).
2. **File → Save As…** → folder `Assets/Scenes`, name **`InterviewRoom`**, Save.
3. In the **Hierarchy** delete everything **except** the object that has the **XR Origin** component (usually named *XR Origin (XR Rig)* or *Complete XR Origin Set Up*).
   Delete: environment, interactables, template UI, lights, teleport areas, etc.
   (Select an item, press **Delete**. If you delete an XR Interaction Manager or EventSystem by accident, it is fine – the builder re-creates it.)

---

## STAGE 4 – VR / XR setup

### 4.1 Project Settings
**Edit → Project Settings → XR Plug-in Management**
1. Open the **Android** tab (the little robot icon).
2. Tick **OpenXR**. Leave **Initialize XR on Startup** ticked.
3. Click **OpenXR** (under XR Plug-in Management, left list):
   - **Enabled Interaction Profiles → +** → add **Oculus Touch Controller Profile**. (Also add *Meta Quest Touch Pro Controller Profile* / *Touch Plus* if listed.)
   - **OpenXR Feature Groups:** tick **Meta Quest Support**.
4. **Project Validation** tab (still in XR Plug-in Management) → click **Fix All** until nothing is red.

### 4.2 Switch to Android / Meta Quest
**File → Build Profiles** → select **Android** (or **Meta Quest** if it is in the list – it shares the Android settings) → **Switch Platform**. Wait.

### 4.3 Apply the Quest player settings with one click
**VR Interview → 2 - Apply Meta Quest Player Settings**. It sets: IL2CPP, ARM64, Minimum API Level 29, Linear colour space, Vulkan + OpenGLES3, ASTC textures.
Then double-check yourself: **Edit → Project Settings → Player → Android tab → Other Settings**:
| Setting | Value |
|---|---|
| Color Space | Linear |
| Auto Graphics API | OFF; list = Vulkan, OpenGLES3 |
| Minimum API Level | Android 10 (API level 29) |
| Scripting Backend | IL2CPP |
| Target Architectures | **ARM64** only |
| Active Input Handling | Input System Package (New) – or Both |

### 4.4 The XR Origin hierarchy (what it must look like)
```
XR Origin (XR Rig)            <- XR Origin component, Position (0, 0, -0.9)
├── Camera Offset
│   └── Main Camera           <- Camera (tag MainCamera), Tracked Pose Driver, AudioListener
├── Left Controller           <- has a Ray Interactor + Line Visual
└── Right Controller          <- has a Ray Interactor + Line Visual
```
The names may differ slightly in your template – that is fine. Required for UI clicking: each controller's **XR Ray Interactor** has **Enable UI Interaction** ticked (it is by default).
The builder also puts the rig at **(0, 0, -0.9)** – right in front of the candidate chair.

---

## STAGES 5 & 6 – Environment and UI (one click)

1. Make sure **InterviewRoom** is the open scene.
2. Click **VR Interview → 3 - Build Interview Room In Open Scene**.
3. Press **Ctrl+S** to save.

### What it creates (exact Hierarchy)
```
InterviewRoom
├── XR Origin (XR Rig)                (yours – moved to 0,0,-0.9)
├── XR Interaction Manager            (created if missing)
├── EventSystem                       (with XR UI Input Module)
├── Environment
│   ├── Floor, Wall_Left, Wall_Right, Wall_Back, Wall_Front, Ceiling
│   ├── Table  (TableTop + 4 TableLeg)
│   ├── InterviewerChair   (0, 0, 1.3), rotated 180°
│   ├── CandidateChair     (0, 0, -0.9)
│   └── PictureFrames, Window, Plant, Laptop, Notepad (office decoration)
├── Interviewer                       (primitive humanoid at 0, 0, 1.3, rotated 180°)
├── Canvas                            (World Space, 1600x900, scale 0.0012, position 0, 1.85, 0.6)
│   ├── WelcomePanel
│   │   ├── TitleText, InstructionText
│   │   └── StartButton               (Button + StartInterview.cs)
│   ├── QuestionPanel
│   │   ├── ProgressText ("Question 1 / 10"), TopicText, QuestionText
│   │   ├── AnswerButton1..4          (Button + AnswerButton.cs)
│   │   ├── ExplanationText
│   │   └── NextButton
│   ├── ScorePanel                    (ScoreText "Score: 3 / 10")
│   └── ResultPanel
│       ├── FinalTitleText ("Interview Completed")
│       ├── FinalScoreText ("Your Score: 8 / 10")
│       ├── PerformanceText (Excellent / Good / Needs Improvement)
│       └── RestartButton
├── InterviewManager                  (InterviewManager.cs)
│   └── Audio                         (AudioSource)
└── Lighting                          (1 soft directional + 2 warm point lights, no shadows)
```
> Small difference from your sketch: ProgressText, the answer buttons and NextButton are **inside QuestionPanel** (so one SetActive shows/hides the whole question screen).

### Doing it by hand instead (reference – what the tool does)
| Object | How to create | Key values |
|---|---|---|
| Floor | Hierarchy **+ → 3D Object → Cube** | Pos (0,-0.05,0), Scale (6.2,0.1,6.2), material FloorMaterial |
| Walls/Ceiling | Cube each | Back (0,1.5,3.05) scale (6.2,3,0.1); Front (0,1.5,-3.05); Left (-3.05,1.5,0) scale (0.1,3,6.2); Right (3.05,1.5,0); Ceiling (0,3.05,0) scale (6.2,0.1,6.2) |
| Materials | **Assets → Create → Material** (URP Lit) | set **Base Map** colour; save in `Assets/Materials` |
| Canvas | **+ → UI → Canvas** | **Render Mode = World Space**; Width 1600, Height 900; Scale 0.0012; Pos (0,1.85,0.6). **Remove** *Graphic Raycaster*, **Add Component → Tracked Device Graphic Raycaster** |
| EventSystem | **+ → UI → Event System** | replace *Input System UI Input Module* with **XR UI Input Module** |
| Buttons | **+ → UI → Button – TextMeshPro** | add `AnswerButton.cs` / `StartInterview.cs`; drag the scene's InterviewManager into the **Manager** slot |
| Audio | empty object `InterviewManager` → child `Audio` → **Add Component → Audio Source** | Play On Awake OFF, **Spatial Blend = 0** |

Text sizes used (readable in headset at 1.5 m): title 80, question 52, answers 40, explanation 34.

---

## STAGE 7 – The C# scripts
Already in `Assets/Scripts`. They are complete (no TODOs). How they cooperate:
- **InterviewManager** – owns the question list, score, panels, audio. Public methods: `BeginInterview()`, `SelectAnswer(int)`, `NextQuestion()`, `RestartInterview()`, `PlayClick()`.
- **AnswerButton** – calls `manager.SelectAnswer(optionIndex)`; colours itself green/red/grey.
- **StartInterview** – calls `manager.BeginInterview()`.
- NEXT and RESTART buttons are connected **inside InterviewManager's code** – do **not** add OnClick events for them in the Inspector (it would trigger twice).

## STAGE 8 – Inspector connections (the builder already did these; verify them)
Select **InterviewManager** in the Hierarchy. In the Inspector:

| Field | Drag from Hierarchy |
|---|---|
| Welcome Panel | Canvas/WelcomePanel |
| Question Panel | Canvas/QuestionPanel |
| Score Panel | Canvas/ScorePanel |
| Result Panel | Canvas/ResultPanel |
| Progress Text | QuestionPanel/ProgressText |
| Topic Text | QuestionPanel/TopicText |
| Question Text | QuestionPanel/QuestionText |
| Explanation Text | QuestionPanel/ExplanationText |
| Score Text | ScorePanel/ScoreText |
| Answer Buttons (Size 4) | AnswerButton1, 2, 3, 4 |
| Next Button / Next Button Label | NextButton / NextButton/Label |
| Final Title / Final Score / Performance Text | ResultPanel children |
| Restart Button | ResultPanel/RestartButton |
| Audio Source | InterviewManager/Audio |

Each **AnswerButton1-4** has `AnswerButton` (Label = its child text). **StartButton** has `StartInterview` (Manager = InterviewManager). Empty slots show "None" – fix any you see.

---

## STAGE 9 – Questions and scoring
10 questions are built into `InterviewManager.cs`. Score = number of correct answers; the result screen shows it and a message:
- **80% or more → Excellent** (change in Inspector: *Excellent Percent*)
- **50–79% → Good**
- **below 50% → Needs Improvement**

| # | Topic | Question | Correct answer |
|---|---|---|---|
| 1 | C++ | Keyword for runtime polymorphism | **virtual** |
| 2 | Python | Which type is immutable? | **tuple** |
| 3 | Data Structures | Average search time in a hash table | **O(1)** |
| 4 | DBMS | ACID property surviving a crash | **Durability** |
| 5 | Operating Systems | NOT a necessary deadlock condition | **Resource preemption** |
| 6 | Software Engineering | Model for changing requirements | **Agile** |
| 7 | Computer Networks | OSI layer that routes packets | **Network** |
| 8 | Computer Architecture | Purpose of a CPU cache | **Reduce average memory access time** |
| 9 | Algorithms | Quick Sort worst case | **O(n^2)** |
| 10 | General Computer Eng. | Gate that outputs 1 only if all inputs are 1 | **AND** |

**Edit or add your own questions:** select InterviewManager → right-click the **Interview Manager (Script)** header → **Load Default Questions Into Inspector** → now edit the **Questions** list (Topic, Question, 4 Options, Correct Index 0–3, Explanation). Click **+** to add more. Tick **Shuffle Questions** to randomise the order.

---

## STAGE 10 – Audio and interviewer

### Audio
- Put **one AudioSource** on `InterviewManager/Audio` (2D: Spatial Blend 0). It plays all five sounds.
- If the clip slots are empty, the script **generates simple beeps** automatically: welcome, click, correct, wrong, completion – so audio works with zero files.
- To use real sounds: copy `.wav/.mp3` files into `Assets/Audio`, then drag them into **Welcome Clip, Click Clip, Correct Clip, Wrong Clip, Complete Clip** on InterviewManager. Free sources: **freesound.org**, **kenney.nl/assets** (CC0 UI/interface sounds). Check each licence.

### Interviewer
**Default (works now):** a humanoid made of primitives (suit, tie, head, eyes, arms, legs), seated on the chair, facing you. No paid asset needed.

**Upgrade to a real model (free):**
1. Get a character: **mixamo.com** (free Adobe account; pick e.g. a male/female business character, download **FBX for Unity**) or CC0 characters from **quaternius.com** / **kenney.nl**.
2. Also on Mixamo download the animation **"Sitting Idle"** (FBX, *Without Skin*).
3. Drag the FBX files into Unity **Project → Assets/Models**.
4. Click the character FBX → Inspector **Rig** tab → **Animation Type = Humanoid** → **Apply**. Do the same for the animation FBX.
5. Drag the character from Project into the Hierarchy. Set **Position (0, 0.0, 1.3)**, **Rotation Y = 180**, adjust Y so the hips sit on the seat (about 0.45 m up).
6. **Assets → Create → Animator Controller** → name `InterviewerController` → double-click → drag the *Sitting Idle* clip from the animation FBX into the graph (it becomes the default). Select the clip → tick **Loop Time**.
7. Select the character → **Animator** component → **Controller = InterviewerController**.
8. Hide the primitive interviewer: select the old **Interviewer** object → untick the checkbox at the top of the Inspector (or delete it).

---

## STAGE 11 – Build and run on Meta Quest

### 11.1 Connect and allow debugging
1. Quest ON, Developer Mode ON (Stage 1.5).
2. Plug the Quest into the PC with the USB-C cable.
3. **Put on the headset.** A popup asks **"Allow USB debugging?"** → tick **Always allow from this computer** → **Allow**. (If you never see it, change the headset *USB connection* to "Data/File transfer" in its notifications, or replug.)
4. Optional check: open a terminal in
   `<Unity install>\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools` and run `adb devices`. You should see your device with status `device` (not `unauthorized`).

### 11.2 Build Profiles
1. **VR Interview → 4 - Add Open Scene To Build Settings** (or **File → Build Profiles → Scene List → Add Open Scenes**). Make sure **InterviewRoom** is ticked and is the only scene.
2. Platform **Android** (or **Meta Quest**) selected and active (**Switch Platform** done).
3. **Run Device:** click **Refresh** → choose your **Quest** from the list.
4. Texture Compression = **ASTC** (**Project Settings → Player → Android → Other Settings** shows it; **Build Profiles → Android → Texture compression** too).
5. Leave **Development Build** OFF for the final build (ON if you want logs).

### 11.3 Build and install
- Fast way: **Build And Run** → name the file `VRInterviewSimulator.apk` → Save. Unity builds and installs it and starts it on the headset.
- Manual way: **Build**, then in a terminal:
  `adb install -r VRInterviewSimulator.apk`
  On the Quest: **Library → filter "Unknown Sources"** → open **VRInterviewSimulator**.
- First build takes several minutes (IL2CPP).

### 11.4 Test the controllers
Put the headset on. You should be seated at the table, interviewer in front of you, the UI board in front/above the table.
1. A **ray (line)** comes out of each controller – aim at **START INTERVIEW**; it highlights.
2. Pull the **trigger** → the first question appears.
3. Aim at an answer, pull the trigger → green (correct) / red (wrong) + explanation + sound.
4. Press **NEXT** → next question … after question 10 press **FINISH**.
5. The result screen shows the score and message. Press **RESTART INTERVIEW** to start again.

### 11.5 Testing without building (optional)
Connect Quest with **Meta Quest Link** (cable or Air Link) → set Link as the PC's **active OpenXR runtime** (in the Meta Quest Link PC app: Settings → General → *Set Meta Quest Link as active OpenXR runtime*) → press **Play** in Unity.

---

## Troubleshooting
| Problem | Fix |
|---|---|
| Red errors about `Unity.XR.CoreUtils` / `XRUIInputModule` | XR Interaction Toolkit isn't installed – use the VR template or install it in Package Manager. |
| Menu "VR Interview" missing | Console has compile errors – fix first (usually a missing package). |
| Text invisible / pink squares | Import TMP Essential Resources (2.2); materials pink = project isn't URP, re-create from the VR template. |
| Ray visible but buttons don't react | Canvas needs **Tracked Device Graphic Raycaster** (no normal Graphic Raycaster); EventSystem needs **XR UI Input Module**; re-run menu 3. |
| No controllers/ray in headset | XR Plug-in Management → Android → OpenXR ticked; Oculus Touch Controller Profile added; Project Validation → Fix All. |
| Black screen in headset | Check Min API 29, ARM64, OpenXR active, only the InterviewRoom scene in the build. |
| Device not listed in Run Device | Replug, accept the popup in the headset, install MQDH for drivers, try another cable/port, run `adb kill-server` then `adb devices`. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | `adb uninstall com.defaultcompany.vrinterviewsimulator` then build again. |
| UI too small/large | Select **Canvas** → change Scale (0.0012) or Position Z/Y. |

---

## FINAL CHECKLIST (tick before submitting)

**Setup**
- [ ] Unity 6.3 LTS installed with Android Build Support + OpenJDK + SDK & NDK
- [ ] Project `VRInterviewSimulator` created (VR template), no red Console errors
- [ ] OpenXR ticked on the **Android** tab; Oculus Touch Controller Profile added; Meta Quest Support ticked
- [ ] Project Validation shows no errors
- [ ] Player settings: IL2CPP, ARM64, Min API 29, Linear, ASTC
- [ ] Platform switched to Android / Meta Quest

**Scene**
- [ ] Scene saved as `Assets/Scenes/InterviewRoom` and is the only scene in the Build list
- [ ] XR Origin with Main Camera + Left/Right controllers (ray visible)
- [ ] Floor, 4 walls, ceiling, table, 2 chairs, interviewer, lighting present
- [ ] Canvas is **World Space**, has **Tracked Device Graphic Raycaster**
- [ ] EventSystem has **XR UI Input Module**
- [ ] InterviewManager has no empty ("None") slots

**Function (test in headset)**
- [ ] Welcome screen: "Welcome to VR Interview Simulator" + START INTERVIEW works
- [ ] "Question 1 / 10" + question + 4 answers shown, readable
- [ ] Clicking an answer shows green/red, explanation and sound
- [ ] NEXT moves forward; last button says FINISH
- [ ] Score indicator updates after each answer
- [ ] Result screen shows "Interview Completed", "Your Score: X / 10", Excellent / Good / Needs Improvement
- [ ] RESTART INTERVIEW resets score and goes to question 1
- [ ] Welcome, click, correct, wrong and completion sounds all play
- [ ] APK installs and runs from Quest **Library → Unknown Sources**
- [ ] Frame rate smooth (no shadows, few lights)

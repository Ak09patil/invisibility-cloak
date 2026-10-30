#if UNITY_EDITOR
using System.IO;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Adds a "VR Interview" menu to Unity. It builds the interview room, the interviewer,
/// the World Space UI and connects all scripts, so you do not have to do it by hand.
/// Requires: XR Interaction Toolkit 3.x, TextMeshPro (uGUI), and an XR Origin in the open scene.
/// </summary>
public static class InterviewSceneBuilder
{
    private const string MaterialFolder = "Assets/Materials";

    // ------------------------------------------------------------------
    // Menu 1: folders
    // ------------------------------------------------------------------

    [MenuItem("VR Interview/1 - Create Folder Structure")]
    public static void CreateFolders()
    {
        string[] folders =
        {
            "Assets/Scenes", "Assets/Scripts", "Assets/Materials", "Assets/Models",
            "Assets/UI", "Assets/Audio", "Assets/Editor"
        };
        foreach (string f in folders) EnsureFolder(f);
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("VR Interview", "Folders created / verified in Assets.", "OK");
    }

    // ------------------------------------------------------------------
    // Menu 2: player settings
    // ------------------------------------------------------------------

    [MenuItem("VR Interview/2 - Apply Meta Quest Player Settings")]
    public static void ApplyQuestSettings()
    {
        PlayerSettings.companyName = "DefaultCompany";
        PlayerSettings.productName = "VRInterviewSimulator";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.defaultcompany.vrinterviewsimulator");

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });

        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("VR Interview",
            "Applied: IL2CPP, ARM64, Min API 29, Linear colour space, Vulkan + OpenGLES3, ASTC textures.\n\n" +
            "You still need to:\n" +
            "1. File > Build Profiles > select Android (or Meta Quest) > Switch Platform\n" +
            "2. Edit > Project Settings > XR Plug-in Management > Android tab > tick OpenXR",
            "OK");
    }

    // ------------------------------------------------------------------
    // Menu 4: add scene to build settings
    // ------------------------------------------------------------------

    [MenuItem("VR Interview/4 - Add Open Scene To Build Settings")]
    public static void AddSceneToBuild()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.path))
        {
            EditorUtility.DisplayDialog("VR Interview", "Save the scene first (Ctrl+S), then run this again.", "OK");
            return;
        }

        EditorBuildSettingsScene[] existing = EditorBuildSettings.scenes;
        foreach (EditorBuildSettingsScene s in existing)
        {
            if (s.path == scene.path)
            {
                EditorUtility.DisplayDialog("VR Interview", "Scene is already in Build Settings.", "OK");
                return;
            }
        }

        EditorBuildSettingsScene[] updated = new EditorBuildSettingsScene[existing.Length + 1];
        existing.CopyTo(updated, 0);
        updated[existing.Length] = new EditorBuildSettingsScene(scene.path, true);
        EditorBuildSettings.scenes = updated;
        EditorUtility.DisplayDialog("VR Interview", "Added to Build Settings:\n" + scene.path, "OK");
    }

    // ------------------------------------------------------------------
    // Menu 3: build everything
    // ------------------------------------------------------------------

    [MenuItem("VR Interview/3 - Build Interview Room In Open Scene")]
    public static void BuildRoom()
    {
        if (TMP_Settings.instance == null)
        {
            EditorUtility.DisplayDialog("TextMeshPro needs setup",
                "Please import the TMP resources first:\n\nWindow > TextMeshPro > Import TMP Essential Resources\n\nThen run this menu again.",
                "OK");
            return;
        }

        EnsureFolder("Assets/Materials");

        Scene scene = SceneManager.GetActiveScene();
        RemoveOldRoots(scene);

        // ---- materials ----
        Material floorMat = Mat("FloorMaterial", new Color(0.45f, 0.32f, 0.20f), 0.35f);
        Material wallMat = Mat("WallMaterial", new Color(0.86f, 0.86f, 0.83f), 0.1f);
        Material tableMat = Mat("TableMaterial", new Color(0.30f, 0.19f, 0.11f), 0.4f);
        Material chairMat = Mat("ChairMaterial", new Color(0.14f, 0.17f, 0.24f), 0.2f);
        Material suitMat = Mat("InterviewerSuitMaterial", new Color(0.10f, 0.14f, 0.28f), 0.2f);
        Material skinMat = Mat("InterviewerSkinMaterial", new Color(0.87f, 0.68f, 0.55f), 0.2f);
        Material hairMat = Mat("InterviewerHairMaterial", new Color(0.12f, 0.08f, 0.06f), 0.2f);
        Material tieMat = Mat("InterviewerTieMaterial", new Color(0.70f, 0.12f, 0.15f), 0.2f);
        Material darkMat = Mat("DarkDecorMaterial", new Color(0.08f, 0.08f, 0.09f), 0.3f);
        Material pictureMat = Mat("PictureMaterial", new Color(0.25f, 0.45f, 0.70f), 0.1f);
        Material plantMat = Mat("PlantMaterial", new Color(0.15f, 0.50f, 0.22f), 0.1f);
        Material potMat = Mat("PotMaterial", new Color(0.60f, 0.35f, 0.25f), 0.1f);
        Material windowMat = Mat("WindowMaterial", new Color(0.65f, 0.82f, 0.95f), 0.6f);

        // ---- environment ----
        GameObject env = new GameObject("Environment");
        Transform e = env.transform;

        Prim(PrimitiveType.Cube, "Floor", e, new Vector3(0, -0.05f, 0), new Vector3(6.2f, 0.1f, 6.2f), floorMat);
        Prim(PrimitiveType.Cube, "Wall_Left", e, new Vector3(-3.05f, 1.5f, 0), new Vector3(0.1f, 3f, 6.2f), wallMat);
        Prim(PrimitiveType.Cube, "Wall_Right", e, new Vector3(3.05f, 1.5f, 0), new Vector3(0.1f, 3f, 6.2f), wallMat);
        Prim(PrimitiveType.Cube, "Wall_Back", e, new Vector3(0, 1.5f, 3.05f), new Vector3(6.2f, 3f, 0.1f), wallMat);
        Prim(PrimitiveType.Cube, "Wall_Front", e, new Vector3(0, 1.5f, -3.05f), new Vector3(6.2f, 3f, 0.1f), wallMat);
        Prim(PrimitiveType.Cube, "Ceiling", e, new Vector3(0, 3.05f, 0), new Vector3(6.2f, 0.1f, 6.2f), wallMat);

        // table
        GameObject table = new GameObject("Table");
        table.transform.SetParent(e, false);
        Prim(PrimitiveType.Cube, "TableTop", table.transform, new Vector3(0, 0.75f, 0), new Vector3(2.0f, 0.06f, 0.9f), tableMat);
        float[] lx = { -0.9f, 0.9f };
        float[] lz = { -0.38f, 0.38f };
        foreach (float x in lx)
        {
            foreach (float z in lz)
            {
                Prim(PrimitiveType.Cylinder, "TableLeg", table.transform, new Vector3(x, 0.36f, z), new Vector3(0.06f, 0.36f, 0.06f), tableMat);
            }
        }

        // chairs
        MakeChair("InterviewerChair", e, new Vector3(0, 0, 1.3f), 180f, chairMat);
        MakeChair("CandidateChair", e, new Vector3(0, 0, -0.9f), 0f, chairMat);

        // office decoration
        Prim(PrimitiveType.Cube, "PictureFrame_1", e, new Vector3(-1.5f, 1.7f, 2.97f), new Vector3(0.9f, 0.6f, 0.04f), darkMat);
        Prim(PrimitiveType.Cube, "Picture_1", e, new Vector3(-1.5f, 1.7f, 2.945f), new Vector3(0.8f, 0.5f, 0.02f), pictureMat);
        Prim(PrimitiveType.Cube, "PictureFrame_2", e, new Vector3(1.5f, 1.7f, 2.97f), new Vector3(0.9f, 0.6f, 0.04f), darkMat);
        Prim(PrimitiveType.Cube, "Picture_2", e, new Vector3(1.5f, 1.7f, 2.945f), new Vector3(0.8f, 0.5f, 0.02f), pictureMat);
        Prim(PrimitiveType.Cube, "Window", e, new Vector3(-2.97f, 1.6f, 0.2f), new Vector3(0.04f, 1.2f, 1.8f), windowMat);
        Prim(PrimitiveType.Cylinder, "PlantPot", e, new Vector3(2.5f, 0.2f, 2.5f), new Vector3(0.35f, 0.2f, 0.35f), potMat);
        Prim(PrimitiveType.Sphere, "PlantLeaves", e, new Vector3(2.5f, 0.75f, 2.5f), new Vector3(0.7f, 0.7f, 0.7f), plantMat);
        Prim(PrimitiveType.Cube, "Laptop_Base", e, new Vector3(0.45f, 0.79f, 0.2f), new Vector3(0.35f, 0.02f, 0.25f), darkMat);
        GameObject screen = Prim(PrimitiveType.Cube, "Laptop_Screen", e, new Vector3(0.45f, 0.91f, 0.33f), new Vector3(0.35f, 0.22f, 0.02f), darkMat);
        screen.transform.localRotation = Quaternion.Euler(15f, 0f, 0f);
        Prim(PrimitiveType.Cube, "Notepad", e, new Vector3(-0.4f, 0.785f, 0.0f), new Vector3(0.21f, 0.01f, 0.30f), wallMat);

        // ---- interviewer (simple humanoid made from primitives) ----
        BuildInterviewer(suitMat, skinMat, hairMat, tieMat, darkMat);

        // ---- lighting ----
        BuildLighting();

        // ---- XR rig position ----
        XROrigin origin = Object.FindFirstObjectByType<XROrigin>();
        if (origin != null)
        {
            origin.transform.SetPositionAndRotation(new Vector3(0f, 0f, -0.9f), Quaternion.identity);
        }
        else
        {
            EditorUtility.DisplayDialog("No XR Origin found",
                "The room was built, but there is no XR Origin in this scene yet.\n\n" +
                "Add one (see Stage 4 of the guide), set its Position to (0, 0, -0.9), then save.",
                "OK");
        }

        if (Object.FindFirstObjectByType<XRInteractionManager>() == null)
        {
            new GameObject("XR Interaction Manager", typeof(XRInteractionManager));
        }

        EnsureXREventSystem();

        // ---- UI + manager ----
        BuildUiAndManager();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorUtility.DisplayDialog("VR Interview",
            "Interview room built!\n\nNext: press Ctrl+S to save the scene, then run\nVR Interview > 4 - Add Open Scene To Build Settings.",
            "OK");
    }

    // ------------------------------------------------------------------
    // Interviewer
    // ------------------------------------------------------------------

    private static void BuildInterviewer(Material suit, Material skin, Material hair, Material tie, Material dark)
    {
        GameObject root = new GameObject("Interviewer");
        root.transform.position = new Vector3(0f, 0f, 1.3f);
        root.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // faces the candidate
        Transform t = root.transform;

        Prim(PrimitiveType.Capsule, "Torso", t, new Vector3(0, 0.75f, 0), new Vector3(0.36f, 0.30f, 0.22f), suit);
        Prim(PrimitiveType.Cube, "Tie", t, new Vector3(0, 0.86f, 0.108f), new Vector3(0.05f, 0.20f, 0.02f), tie);
        Prim(PrimitiveType.Cylinder, "Neck", t, new Vector3(0, 1.08f, 0), new Vector3(0.07f, 0.05f, 0.07f), skin);
        Prim(PrimitiveType.Sphere, "Head", t, new Vector3(0, 1.20f, 0), new Vector3(0.24f, 0.26f, 0.24f), skin);
        Prim(PrimitiveType.Sphere, "Hair", t, new Vector3(0, 1.24f, -0.03f), new Vector3(0.25f, 0.23f, 0.25f), hair);
        Prim(PrimitiveType.Sphere, "Eye_L", t, new Vector3(-0.045f, 1.21f, 0.105f), new Vector3(0.03f, 0.03f, 0.03f), dark);
        Prim(PrimitiveType.Sphere, "Eye_R", t, new Vector3(0.045f, 1.21f, 0.105f), new Vector3(0.03f, 0.03f, 0.03f), dark);

        float[] side = { -1f, 1f };
        foreach (float s in side)
        {
            Prim(PrimitiveType.Capsule, "UpperArm", t, new Vector3(0.23f * s, 0.80f, 0.02f), new Vector3(0.09f, 0.18f, 0.09f), suit);
            GameObject fore = Prim(PrimitiveType.Cylinder, "ForeArm", t, new Vector3(0.22f * s, 0.68f, 0.17f), new Vector3(0.07f, 0.14f, 0.07f), suit);
            fore.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Prim(PrimitiveType.Sphere, "Hand", t, new Vector3(0.22f * s, 0.68f, 0.33f), new Vector3(0.08f, 0.08f, 0.08f), skin);

            GameObject thigh = Prim(PrimitiveType.Cylinder, "Thigh", t, new Vector3(0.10f * s, 0.48f, 0.2f), new Vector3(0.14f, 0.20f, 0.14f), dark);
            thigh.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Prim(PrimitiveType.Cylinder, "LowerLeg", t, new Vector3(0.10f * s, 0.25f, 0.4f), new Vector3(0.12f, 0.25f, 0.12f), dark);
        }
    }

    // ------------------------------------------------------------------
    // Lighting
    // ------------------------------------------------------------------

    private static void BuildLighting()
    {
        GameObject root = new GameObject("Lighting");

        GameObject dirGo = new GameObject("Directional Light (fill)");
        dirGo.transform.SetParent(root.transform, false);
        dirGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        Light dir = dirGo.AddComponent<Light>();
        dir.type = LightType.Directional;
        dir.intensity = 0.5f;
        dir.shadows = LightShadows.None;

        Vector3[] positions = { new Vector3(-1.5f, 2.7f, -0.5f), new Vector3(1.5f, 2.7f, 0.8f) };
        for (int i = 0; i < positions.Length; i++)
        {
            GameObject go = new GameObject("Ceiling Light " + (i + 1));
            go.transform.SetParent(root.transform, false);
            go.transform.position = positions[i];
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.95f, 0.85f);
            l.intensity = 1.5f;
            l.range = 7f;
            l.shadows = LightShadows.None; // shadows are expensive on Quest
        }

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.60f);
    }

    // ------------------------------------------------------------------
    // Event system for XR UI
    // ------------------------------------------------------------------

    private static void EnsureXREventSystem()
    {
        EventSystem es = Object.FindFirstObjectByType<EventSystem>();
        if (es == null)
        {
            GameObject go = new GameObject("EventSystem", typeof(EventSystem));
            es = go.GetComponent<EventSystem>();
        }

        if (es.GetComponent<XRUIInputModule>() == null)
        {
            BaseInputModule[] modules = es.GetComponents<BaseInputModule>();
            foreach (BaseInputModule m in modules) Object.DestroyImmediate(m);
            es.gameObject.AddComponent<XRUIInputModule>();
        }
    }

    // ------------------------------------------------------------------
    // UI + InterviewManager
    // ------------------------------------------------------------------

    private static void BuildUiAndManager()
    {
        Color panelBg = new Color(0.07f, 0.11f, 0.19f, 0.96f);
        Color lightBg = new Color(0.12f, 0.18f, 0.30f, 1f);
        Color white = Color.white;
        Color soft = new Color(0.75f, 0.82f, 0.92f, 1f);
        Color green = new Color(0.12f, 0.55f, 0.28f, 1f);
        Color blue = new Color(0.16f, 0.40f, 0.75f, 1f);

        // ---- canvas ----
        GameObject canvasGo = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
        Canvas canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(1600f, 900f);
        canvasRt.position = new Vector3(0f, 1.85f, 0.6f);
        canvasRt.localScale = new Vector3(0.0012f, 0.0012f, 0.0012f);

        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 2f;

        Transform c = canvasGo.transform;

        // ---- welcome panel ----
        RectTransform welcome = MakePanel("WelcomePanel", c, panelBg);
        MakeText(welcome, "TitleText", "Welcome to VR Interview Simulator", 80f, TextAlignmentOptions.Center, new Vector2(0.05f, 0.58f), new Vector2(0.95f, 0.92f), white, true);
        MakeText(welcome, "InstructionText", "Answer 10 technical questions.\nPoint at an answer with your controller and pull the trigger.", 40f, TextAlignmentOptions.Center, new Vector2(0.08f, 0.36f), new Vector2(0.92f, 0.56f), soft, true);
        Button startButton = MakeButton(welcome, "StartButton", "START INTERVIEW", 56f, green, new Vector2(0.28f, 0.08f), new Vector2(0.72f, 0.30f));

        // ---- question panel ----
        RectTransform question = MakePanel("QuestionPanel", c, panelBg);
        TextMeshProUGUI progress = MakeText(question, "ProgressText", "Question 1 / 10", 44f, TextAlignmentOptions.Left, new Vector2(0.03f, 0.89f), new Vector2(0.40f, 0.99f), white, true);
        TextMeshProUGUI topic = MakeText(question, "TopicText", "Topic: C++", 36f, TextAlignmentOptions.Center, new Vector2(0.40f, 0.89f), new Vector2(0.70f, 0.99f), soft, true);
        TextMeshProUGUI questionText = MakeText(question, "QuestionText", "Question text appears here", 52f, TextAlignmentOptions.Center, new Vector2(0.04f, 0.68f), new Vector2(0.96f, 0.88f), white, true);

        AnswerButton[] answerButtons = new AnswerButton[4];
        Vector2[] mins = { new Vector2(0.04f, 0.47f), new Vector2(0.52f, 0.47f), new Vector2(0.04f, 0.26f), new Vector2(0.52f, 0.26f) };
        Vector2[] maxs = { new Vector2(0.48f, 0.66f), new Vector2(0.96f, 0.66f), new Vector2(0.48f, 0.45f), new Vector2(0.96f, 0.45f) };
        for (int i = 0; i < 4; i++)
        {
            Button b = MakeButton(question, "AnswerButton" + (i + 1), "Option " + (char)('A' + i), 40f, blue, mins[i], maxs[i]);
            AnswerButton ab = b.gameObject.AddComponent<AnswerButton>();
            ab.label = b.GetComponentInChildren<TMP_Text>();
            ab.optionIndex = i;
            ab.SetState(AnswerVisualState.Normal);
            answerButtons[i] = ab;
        }

        TextMeshProUGUI explanation = MakeText(question, "ExplanationText", "", 34f, TextAlignmentOptions.Left, new Vector2(0.04f, 0.04f), new Vector2(0.72f, 0.24f), soft, true);
        Button nextButton = MakeButton(question, "NextButton", "NEXT", 48f, green, new Vector2(0.75f, 0.05f), new Vector2(0.96f, 0.22f));
        TextMeshProUGUI nextLabel = nextButton.GetComponentInChildren<TextMeshProUGUI>();

        // ---- score panel (small, top right, drawn above the question panel) ----
        RectTransform scorePanel = MakePanel("ScorePanel", c, lightBg);
        scorePanel.anchorMin = new Vector2(0.72f, 0.89f);
        scorePanel.anchorMax = new Vector2(0.97f, 0.99f);
        scorePanel.offsetMin = Vector2.zero;
        scorePanel.offsetMax = Vector2.zero;
        TextMeshProUGUI scoreText = MakeText(scorePanel, "ScoreText", "Score: 0 / 10", 40f, TextAlignmentOptions.Center, Vector2.zero, Vector2.one, white, true);

        // ---- result panel ----
        RectTransform result = MakePanel("ResultPanel", c, panelBg);
        TextMeshProUGUI finalTitle = MakeText(result, "FinalTitleText", "Interview Completed", 80f, TextAlignmentOptions.Center, new Vector2(0.05f, 0.72f), new Vector2(0.95f, 0.94f), white, true);
        TextMeshProUGUI finalScore = MakeText(result, "FinalScoreText", "Your Score: 0 / 10", 72f, TextAlignmentOptions.Center, new Vector2(0.05f, 0.50f), new Vector2(0.95f, 0.70f), white, true);
        TextMeshProUGUI performance = MakeText(result, "PerformanceText", "Excellent", 52f, TextAlignmentOptions.Center, new Vector2(0.08f, 0.28f), new Vector2(0.92f, 0.48f), white, true);
        Button restartButton = MakeButton(result, "RestartButton", "RESTART INTERVIEW", 52f, blue, new Vector2(0.28f, 0.06f), new Vector2(0.72f, 0.24f));

        // ---- manager ----
        GameObject mgrGo = new GameObject("InterviewManager");
        InterviewManager mgr = mgrGo.AddComponent<InterviewManager>();

        GameObject audioGo = new GameObject("Audio");
        audioGo.transform.SetParent(mgrGo.transform, false);
        AudioSource src = audioGo.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f;
        mgr.audioSource = src;

        mgr.welcomePanel = welcome.gameObject;
        mgr.questionPanel = question.gameObject;
        mgr.scorePanel = scorePanel.gameObject;
        mgr.resultPanel = result.gameObject;
        mgr.progressText = progress;
        mgr.topicText = topic;
        mgr.questionText = questionText;
        mgr.explanationText = explanation;
        mgr.scoreText = scoreText;
        mgr.answerButtons = answerButtons;
        mgr.nextButton = nextButton;
        mgr.nextButtonLabel = nextLabel;
        mgr.finalTitleText = finalTitle;
        mgr.finalScoreText = finalScore;
        mgr.performanceText = performance;
        mgr.restartButton = restartButton;
        mgr.questions = InterviewManager.CreateDefaultQuestions();

        for (int i = 0; i < answerButtons.Length; i++) answerButtons[i].manager = mgr;

        StartInterview start = startButton.gameObject.AddComponent<StartInterview>();
        start.manager = mgr;

        // Only the welcome panel is visible when the app starts.
        question.gameObject.SetActive(false);
        scorePanel.gameObject.SetActive(false);
        result.gameObject.SetActive(false);

        EditorUtility.SetDirty(mgr);
        Selection.activeGameObject = mgrGo;
    }

    // ------------------------------------------------------------------
    // Builders / helpers
    // ------------------------------------------------------------------

    private static void MakeChair(string name, Transform parent, Vector3 position, float yRotation, Material mat)
    {
        GameObject chair = new GameObject(name);
        chair.transform.SetParent(parent, false);
        chair.transform.localPosition = position;
        chair.transform.localRotation = Quaternion.Euler(0f, yRotation, 0f);
        Transform t = chair.transform;

        Prim(PrimitiveType.Cube, "Seat", t, new Vector3(0, 0.45f, 0), new Vector3(0.5f, 0.06f, 0.5f), mat);
        Prim(PrimitiveType.Cube, "Backrest", t, new Vector3(0, 0.78f, -0.22f), new Vector3(0.5f, 0.6f, 0.05f), mat);

        float[] o = { -0.2f, 0.2f };
        foreach (float x in o)
        {
            foreach (float z in o)
            {
                Prim(PrimitiveType.Cylinder, "Leg", t, new Vector3(x, 0.21f, z), new Vector3(0.05f, 0.21f, 0.05f), mat);
            }
        }
    }

    private static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        go.GetComponent<Renderer>().sharedMaterial = mat;

        Collider col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);
        return go;
    }

    private static Material Mat(string name, Color color, float smoothness)
    {
        string path = MaterialFolder + "/" + name + ".mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            m = new Material(shader);
            AssetDatabase.CreateAsset(m, path);
        }

        m.color = color;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static RectTransform NewRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
        return rt;
    }

    private static RectTransform MakePanel(string name, Transform parent, Color color)
    {
        RectTransform rt = NewRect(name, parent, Vector2.zero, Vector2.one);
        Image img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        return rt;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string name, string text, float size, TextAlignmentOptions align, Vector2 anchorMin, Vector2 anchorMax, Color color, bool autoSize)
    {
        RectTransform rt = NewRect(name, parent, anchorMin, anchorMax);
        TextMeshProUGUI tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.color = color;
        tmp.raycastTarget = false;
        tmp.margin = new Vector4(16f, 8f, 16f, 8f);
        if (autoSize)
        {
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = size;
            tmp.fontSizeMin = Mathf.Max(18f, size * 0.45f);
        }
        return tmp;
    }

    private static Button MakeButton(Transform parent, string name, string label, float fontSize, Color color, Vector2 anchorMin, Vector2 anchorMax)
    {
        RectTransform rt = NewRect(name, parent, anchorMin, anchorMax);
        Image img = rt.gameObject.AddComponent<Image>();
        img.color = Color.white;
        Button btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        ColorBlock block = btn.colors;
        block.normalColor = color;
        block.highlightedColor = Color.Lerp(color, Color.white, 0.25f);
        block.pressedColor = Color.Lerp(color, Color.black, 0.2f);
        block.selectedColor = color;
        block.disabledColor = color;
        btn.colors = block;

        MakeText(rt, "Label", label, fontSize, TextAlignmentOptions.Center, Vector2.zero, Vector2.one, Color.white, true);
        return btn;
    }

    private static void RemoveOldRoots(Scene scene)
    {
        string[] names = { "Environment", "Interviewer", "Canvas", "Lighting", "InterviewManager" };
        GameObject[] roots = scene.GetRootGameObjects();
        foreach (GameObject go in roots)
        {
            foreach (string n in names)
            {
                if (go != null && go.name == n)
                {
                    Object.DestroyImmediate(go);
                    break;
                }
            }
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
#endif

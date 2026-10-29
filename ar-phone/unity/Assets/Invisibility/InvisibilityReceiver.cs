using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Receives camera frame, background and per-person masks from the Python
/// perception script over TCP, drives the Custom/InvisibilityAR shader, and
/// emits sparks along each vanishing person's outline.
/// Put this on the Quad that has the InvisibilityAR material.
/// </summary>
public class InvisibilityReceiver : MonoBehaviour
{
    [Header("Network")]
    public int port = 5055;
    public bool listenOnAllInterfaces = false;

    [Header("Look")]
    public float fadeSeconds = 0.9f;
    public float sparksPerSecond = 260f;
    public Color neonColor = new Color(0.16f, 0.82f, 1f, 1f);
    public Color shimmerColor = new Color(0.85f, 0.92f, 1f, 1f);

    const int MaxSlots = 4;

    [Serializable] class PersonMsg
    {
        public int slot;
        public int id;
        public bool vanish;
        public int effect;
        public string gesture;
        public float[] contour;
    }

    [Serializable] class FrameMsg
    {
        public PersonMsg[] people;
    }

    Renderer rend;
    Material mat;
    Texture2D camTex, bgTex, maskTex, dot;

    Thread thread;
    volatile bool running;
    volatile bool connected;
    TcpListener listener;
    TcpClient client;
    readonly object gate = new object();
    byte[] pendingBg;
    byte[] pendingFrame;

    // per-slot state
    readonly bool[] present = new bool[MaxSlots];
    readonly bool[] vanish = new bool[MaxSlots];
    readonly int[] effect = new int[MaxSlots];
    readonly int[] ids = new int[MaxSlots];
    readonly float[] prog = new float[MaxSlots];
    readonly float[] emitAcc = new float[MaxSlots];
    readonly string[] gesture = new string[MaxSlots];
    readonly float[][] contour = new float[MaxSlots][];

    ParticleSystem sparks;
    bool gotFrame;

    void Awake()
    {
        Application.runInBackground = true; // keep rendering while the Python window has focus

        rend = GetComponent<Renderer>();
        mat = rend.material; // per-object instance

        camTex = MakeTexture(false);
        bgTex = MakeTexture(false);
        maskTex = MakeTexture(true);
        mat.SetTexture("_CamTex", camTex);
        mat.SetTexture("_BgTex", bgTex);
        mat.SetTexture("_MaskTex", maskTex);

        for (int s = 0; s < MaxSlots; s++) ids[s] = -1;
        SetupParticles();
    }

    static Texture2D MakeTexture(bool linear)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        return t;
    }

    void Start()
    {
        running = true;
        thread = new Thread(ListenLoop) { IsBackground = true };
        thread.Start();
    }

    // ------------------------------------------------------------ network
    void ListenLoop()
    {
        try
        {
            IPAddress ip = listenOnAllInterfaces ? IPAddress.Any : IPAddress.Loopback;
            listener = new TcpListener(ip, port);
            listener.Start();
            Debug.Log("InvisibilityReceiver: listening on port " + port);
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception) { break; }

                client = c;
                connected = true;
                Debug.Log("InvisibilityReceiver: Python connected");
                try { ReadLoop(c); }
                catch (Exception) { }
                connected = false;
                try { c.Close(); } catch (Exception) { }
                Debug.Log("InvisibilityReceiver: Python disconnected");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("InvisibilityReceiver: " + e.Message);
        }
    }

    void ReadLoop(TcpClient c)
    {
        NetworkStream s = c.GetStream();
        byte[] lenBuf = new byte[4];
        while (running)
        {
            ReadExact(s, lenBuf, 4);
            int len = (int)BitConverter.ToUInt32(lenBuf, 0);
            if (len <= 0 || len > 16 * 1024 * 1024) return;
            byte[] payload = new byte[len];
            ReadExact(s, payload, len);
            lock (gate)
            {
                if (payload[0] == 1) pendingBg = payload;
                else if (payload[0] == 2) pendingFrame = payload; // keep only the newest frame
            }
        }
    }

    static void ReadExact(NetworkStream s, byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) throw new IOException("closed");
            got += r;
        }
    }

    // --------------------------------------------------------------- frame
    void Update()
    {
        byte[] bg, fr;
        lock (gate)
        {
            bg = pendingBg; pendingBg = null;
            fr = pendingFrame; pendingFrame = null;
        }

        if (bg != null)
        {
            byte[] jpg = new byte[bg.Length - 1];
            Buffer.BlockCopy(bg, 1, jpg, 0, jpg.Length);
            bgTex.LoadImage(jpg);
        }

        if (fr != null) ApplyFrame(fr);

        // Ease each person's vanish progress on the Unity side.
        float dt = Time.deltaTime;
        Vector4 pv = Vector4.zero;
        Vector4 ev = Vector4.zero;
        for (int s = 0; s < MaxSlots; s++)
        {
            float target = (present[s] && vanish[s]) ? 1f : 0f;
            prog[s] = Mathf.MoveTowards(prog[s], target, dt / Mathf.Max(0.05f, fadeSeconds));
            pv[s] = prog[s];
            ev[s] = effect[s];
        }
        mat.SetVector("_Prog", pv);
        mat.SetVector("_Eff", ev);

        if (gotFrame) FitQuad();
        EmitSparks(dt);
    }

    void ApplyFrame(byte[] fr)
    {
        int o = 1;
        int jl = (int)BitConverter.ToUInt32(fr, o); o += 4;
        byte[] jpg = new byte[jl];
        Buffer.BlockCopy(fr, o, jpg, 0, jl); o += jl;

        int pl = (int)BitConverter.ToUInt32(fr, o); o += 4;
        byte[] png = new byte[pl];
        Buffer.BlockCopy(fr, o, png, 0, pl); o += pl;

        int sl = (int)BitConverter.ToUInt32(fr, o); o += 4;
        string json = System.Text.Encoding.UTF8.GetString(fr, o, sl);

        camTex.LoadImage(jpg);
        maskTex.LoadImage(png);
        ApplyMeta(json);
        gotFrame = true;
    }

    void ApplyMeta(string json)
    {
        for (int s = 0; s < MaxSlots; s++) present[s] = false;

        FrameMsg msg = null;
        try { msg = JsonUtility.FromJson<FrameMsg>(json); }
        catch (Exception) { }
        if (msg == null || msg.people == null) return;

        foreach (PersonMsg p in msg.people)
        {
            int s = p.slot;
            if (s < 0 || s >= MaxSlots) continue;
            if (ids[s] != p.id) { ids[s] = p.id; prog[s] = 0f; } // a new person starts visible
            present[s] = true;
            vanish[s] = p.vanish;
            effect[s] = p.effect;
            gesture[s] = p.gesture;
            contour[s] = p.contour;
        }
    }

    void FitQuad()
    {
        Camera cam = Camera.main;
        if (cam == null || !cam.orthographic) return;
        float h = cam.orthographicSize * 2f;
        float aspect = (float)camTex.width / Mathf.Max(1, camTex.height);
        transform.localScale = new Vector3(h * aspect, h, 1f);
        Vector3 p = transform.position;
        transform.position = new Vector3(cam.transform.position.x, cam.transform.position.y, p.z);
    }

    // ------------------------------------------------------------ particles
    void SetupParticles()
    {
        dot = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 16f;
                float a = Mathf.Clamp01(1f - d);
                dot.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        }
        dot.Apply();

        GameObject go = new GameObject("OutlineSparks");
        sparks = go.AddComponent<ParticleSystem>();

        var main = sparks.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 4000;
        main.startLifetime = 1f;
        main.startSpeed = 0f;
        main.startSize = 0.1f;

        var em = sparks.emission;
        em.enabled = false; // we emit by hand along the outlines

        var shape = sparks.shape;
        shape.enabled = false;

        var col = sparks.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);

        var size = sparks.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

        var r = go.GetComponent<ParticleSystemRenderer>();
        Material pm = new Material(Shader.Find("Sprites/Default"));
        pm.mainTexture = dot;
        r.material = pm;
        r.sortingOrder = 10;

        sparks.Play();
    }

    Vector3 UvToWorld(float u, float v)
    {
        return transform.TransformPoint(new Vector3(u - 0.5f, v - 0.5f, 0f));
    }

    void EmitSparks(float dt)
    {
        float unit = Mathf.Max(0.01f, transform.lossyScale.y);
        for (int s = 0; s < MaxSlots; s++)
        {
            if (!present[s] || effect[s] == 2) continue;
            float[] ct = contour[s];
            if (ct == null || ct.Length < 6) continue;
            if (prog[s] < 0.02f) continue;

            float rate = sparksPerSecond * (effect[s] == 1 ? 1f : 0.4f);
            emitAcc[s] += rate * dt;
            int count = (int)emitAcc[s];
            emitAcc[s] -= count;

            Color c = effect[s] == 1 ? neonColor : shimmerColor;
            int pts = ct.Length / 2;
            for (int k = 0; k < count; k++)
            {
                int idx = UnityEngine.Random.Range(0, pts);
                float u = ct[2 * idx];
                float v = 1f - ct[2 * idx + 1]; // Python y points down, Unity v points up

                var ep = new ParticleSystem.EmitParams();
                ep.position = UvToWorld(u, v);
                ep.velocity = new Vector3(
                    UnityEngine.Random.Range(-0.25f, 0.25f),
                    UnityEngine.Random.Range(0.15f, 0.8f),
                    0f) * unit * 0.15f;
                ep.startLifetime = UnityEngine.Random.Range(0.6f, 1.2f);
                ep.startSize = unit * UnityEngine.Random.Range(0.012f, 0.03f);
                ep.startColor = (Color32)c;
                sparks.Emit(ep, 1);
            }
        }
    }

    // --------------------------------------------------------------- status
    void OnGUI()
    {
        int n = 0;
        for (int s = 0; s < MaxSlots; s++) if (present[s]) n++;
        string status = connected ? "Python connected" : "Waiting for Python...";
        GUI.Label(new Rect(10, 8, 600, 24), status + "   people: " + n);
    }

    void OnDestroy()
    {
        running = false;
        try { if (listener != null) listener.Stop(); } catch (Exception) { }
        try { if (client != null) client.Close(); } catch (Exception) { }
    }
}

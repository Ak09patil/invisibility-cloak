using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// PHONE app for the invisibility AR system.
///   1. Opens the phone's own camera.
///   2. Streams its frames to the laptop server (invisibility_server.py) over Wi-Fi.
///   3. Receives the clean background, the processed frame, the person masks and the
///      per-person state back, and draws the vanish + neon glow with the
///      Custom/InvisibilityAR shader.
/// Put this on the Quad that has the InvisibilityAR material, and REMOVE (or untick)
/// the old InvisibilityReceiver component from that Quad.
/// </summary>
public class InvisibilityPhone : MonoBehaviour
{
    [Header("Server (the laptop running invisibility_server.py)")]
    public string serverIp = "";
    public int port = 5055;
    public bool autoConnectIfIpSaved = true;

    [Header("Camera / streaming")]
    public int requestWidth = 640;
    public int requestHeight = 480;
    public float sendFps = 12f;
    [Range(30, 95)] public int jpegQuality = 75;

    [Header("Look")]
    public float fadeSeconds = 0.9f;
    public Color neonColor = new Color(0.16f, 0.82f, 1f, 1f);

    const int MaxSlots = 4;
    const byte ToServerFrame = 0x10;
    const byte ToServerCommand = 0x11;

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

    // ---------------------------------------------------------- rendering
    Renderer rend;
    Material mat;
    Texture2D camTex, bgTex, maskTex;   // from the server
    Texture2D sendTex;                  // what we send (also the local preview)

    // ---------------------------------------------------------- network
    Thread netThread, sendThread;
    volatile bool running;
    volatile bool connected;
    volatile bool wantConnect;
    volatile string targetIp = "";
    volatile TcpClient client;
    volatile string lastError = "";
    readonly object gate = new object();
    readonly AutoResetEvent sendSignal = new AutoResetEvent(false);
    readonly Queue<byte[]> commandPackets = new Queue<byte[]>();
    byte[] pendingSend;       // newest camera packet (length prefix included)
    byte[] pendingBg;
    byte[] pendingFrame;
    string pendingStatus;
    string serverStatus = "";

    // ---------------------------------------------------------- per-person state
    readonly bool[] present = new bool[MaxSlots];
    readonly bool[] vanish = new bool[MaxSlots];
    readonly int[] effect = new int[MaxSlots];
    readonly int[] ids = new int[MaxSlots];
    readonly float[] prog = new float[MaxSlots];
    float lastServerFrame = -100f;
    bool gotAnyFrame;

    // ---------------------------------------------------------- camera
    WebCamTexture cam;
    bool useFront;
    bool mirror;
    int extraRot;
    string camError = "";
    float nextSend;
    Color32[] srcPx, dstPx, outPx;

    // ---------------------------------------------------------- ui
    bool showUi = true;

    bool Receiving { get { return connected && Time.realtimeSinceStartup - lastServerFrame < 1.0f; } }

    void Awake()
    {
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 30;

        rend = GetComponent<Renderer>();
        mat = rend.material;
        camTex = MakeTexture(false);
        bgTex = MakeTexture(false);
        maskTex = MakeTexture(true);
        mat.SetTexture("_CamTex", camTex);
        mat.SetTexture("_BgTex", bgTex);
        mat.SetTexture("_MaskTex", maskTex);
        mat.SetColor("_GlowColor", neonColor);
        for (int s = 0; s < MaxSlots; s++) ids[s] = -1;

        string saved = PlayerPrefs.GetString("serverIp", "");
        if (string.IsNullOrEmpty(serverIp)) serverIp = saved;
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
        netThread = new Thread(NetLoop) { IsBackground = true };
        netThread.Start();
        sendThread = new Thread(SendLoop) { IsBackground = true };
        sendThread.Start();

        StartCoroutine(StartCamera());
        if (autoConnectIfIpSaved && !string.IsNullOrEmpty(serverIp)) Connect();
    }

    // ================================================================ camera
    IEnumerator StartCamera()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
            float t0 = Time.realtimeSinceStartup;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera)
                   && Time.realtimeSinceStartup - t0 < 60f)
                yield return null;
        }
#endif
        yield return null;
        OpenCamera();
    }

    void OpenCamera()
    {
        WebCamDevice[] devs = WebCamTexture.devices;
        if (devs.Length == 0) { camError = "No camera found (allow camera permission)"; return; }
        int pick = -1;
        for (int i = 0; i < devs.Length; i++)
            if (devs[i].isFrontFacing == useFront) { pick = i; break; }
        if (pick < 0) pick = 0;

        if (cam != null) { cam.Stop(); Destroy(cam); }
        cam = new WebCamTexture(devs[pick].name, requestWidth, requestHeight, 30);
        mirror = devs[pick].isFrontFacing;   // selfie camera looks natural when mirrored
        extraRot = 0;
        camError = "";
        cam.Play();
    }

    /// <summary>
    /// Turns the raw camera pixels into an upright, 4:3 picture in sendTex:
    /// vertical flip if the camera says so, rotate clockwise by the camera's angle
    /// (plus the user's extra turns), optional mirror, then centre-crop to 4:3.
    /// </summary>
    bool CaptureFrame()
    {
        if (cam == null || !cam.isPlaying || cam.width < 100) return false;
        int w = cam.width, h = cam.height;
        if (srcPx == null || srcPx.Length != w * h)
        {
            srcPx = new Color32[w * h];
            dstPx = new Color32[w * h];
        }
        cam.GetPixels32(srcPx);

        int rot = (((cam.videoRotationAngle / 90) + extraRot) % 4 + 4) % 4;
        bool vflip = cam.videoVerticallyMirrored;
        bool odd = (rot % 2) == 1;
        int dw = odd ? h : w;
        int dh = odd ? w : h;

        for (int y = 0; y < h; y++)
        {
            int y1 = vflip ? (h - 1 - y) : y;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                if (rot == 0) { nx = x; ny = y1; }
                else if (rot == 1) { nx = y1; ny = w - 1 - x; }
                else if (rot == 2) { nx = w - 1 - x; ny = h - 1 - y1; }
                else { nx = h - 1 - y1; ny = x; }
                if (mirror) nx = dw - 1 - nx;
                dstPx[ny * dw + nx] = srcPx[row + x];
            }
        }

        // centre-crop to 4:3 (and halve very large pictures)
        int cw, ch;
        if (dw * 3 > dh * 4) { ch = dh; cw = (dh * 4) / 3; }
        else { cw = dw; ch = (dw * 3) / 4; }
        int ox = (dw - cw) / 2, oy = (dh - ch) / 2;
        int f = cw > 800 ? 2 : 1;
        int ow = cw / f, oh = ch / f;
        if (ow < 16 || oh < 16) return false;
        if (outPx == null || outPx.Length != ow * oh) outPx = new Color32[ow * oh];

        if (f == 1)
        {
            for (int r = 0; r < oh; r++)
                Array.Copy(dstPx, (oy + r) * dw + ox, outPx, r * ow, ow);
        }
        else
        {
            for (int r = 0; r < oh; r++)
            {
                int srow = (oy + r * f) * dw + ox;
                int drow = r * ow;
                for (int c = 0; c < ow; c++) outPx[drow + c] = dstPx[srow + c * f];
            }
        }

        if (sendTex == null || sendTex.width != ow || sendTex.height != oh)
            sendTex = new Texture2D(ow, oh, TextureFormat.RGB24, false);
        sendTex.SetPixels32(outPx);
        return true;
    }

    // ================================================================ network
    public void Connect()
    {
        string ip = (serverIp ?? "").Trim();
        if (ip.Length == 0) { lastError = "Type the laptop's IP first"; return; }
        PlayerPrefs.SetString("serverIp", ip);
        PlayerPrefs.Save();
        lastError = "";
        targetIp = ip;
        wantConnect = true;
    }

    public void Disconnect()
    {
        wantConnect = false;
        CloseClient();
    }

    void CloseClient()
    {
        TcpClient c = client;
        client = null;
        connected = false;
        try { if (c != null) c.Close(); } catch (Exception) { }
    }

    void NetLoop()
    {
        while (running)
        {
            if (!wantConnect) { Thread.Sleep(100); continue; }
            TcpClient c = null;
            try
            {
                c = new TcpClient();
                c.NoDelay = true;
                IAsyncResult ar = c.BeginConnect(targetIp, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new TimeoutException("Could not reach " + targetIp);
                c.EndConnect(ar);
                lock (gate) { pendingSend = null; commandPackets.Clear(); }
                client = c;
                connected = true;
                lastError = "";
                ReadLoop(c);
            }
            catch (Exception e)
            {
                if (wantConnect) lastError = e.Message;
            }
            finally
            {
                connected = false;
                if (client == c) client = null;
                try { if (c != null) c.Close(); } catch (Exception) { }
            }
            for (int i = 0; i < 15 && running && wantConnect; i++) Thread.Sleep(100);
        }
    }

    void ReadLoop(TcpClient c)
    {
        NetworkStream s = c.GetStream();
        byte[] lenBuf = new byte[4];
        while (running && wantConnect)
        {
            ReadExact(s, lenBuf, 4);
            int len = (int)BitConverter.ToUInt32(lenBuf, 0);
            if (len <= 0 || len > 16 * 1024 * 1024) return;
            byte[] payload = new byte[len];
            ReadExact(s, payload, len);
            lock (gate)
            {
                if (payload[0] == 1) pendingBg = payload;
                else if (payload[0] == 2) pendingFrame = payload;   // newest only
                else if (payload[0] == 3) pendingStatus = Encoding.UTF8.GetString(payload, 1, len - 1);
            }
        }
    }

    static void ReadExact(NetworkStream s, byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) throw new System.IO.IOException("closed");
            got += r;
        }
    }

    void SendLoop()
    {
        while (running)
        {
            sendSignal.WaitOne(200);
            while (running)
            {
                TcpClient c = client;
                byte[] packet = null;
                lock (gate)
                {
                    if (!connected || c == null) { pendingSend = null; commandPackets.Clear(); }
                    else if (commandPackets.Count > 0) packet = commandPackets.Dequeue();
                    else if (pendingSend != null) { packet = pendingSend; pendingSend = null; }
                }
                if (packet == null) break;
                try { c.GetStream().Write(packet, 0, packet.Length); }
                catch (Exception) { CloseClient(); break; }
            }
        }
    }

    static byte[] MakePacket(byte kind, byte[] data)
    {
        int n = 1 + data.Length;
        byte[] p = new byte[4 + n];
        p[0] = (byte)(n & 0xFF);
        p[1] = (byte)((n >> 8) & 0xFF);
        p[2] = (byte)((n >> 16) & 0xFF);
        p[3] = (byte)((n >> 24) & 0xFF);
        p[4] = kind;
        Buffer.BlockCopy(data, 0, p, 5, data.Length);
        return p;
    }

    void SendCommand(string cmd)
    {
        if (!connected) return;
        byte[] pkt = MakePacket(ToServerCommand, Encoding.UTF8.GetBytes(cmd));
        lock (gate) { commandPackets.Enqueue(pkt); }
        sendSignal.Set();
    }

    // ================================================================ per frame
    void Update()
    {
        // ---- results from the laptop
        byte[] bg, fr;
        string st;
        lock (gate)
        {
            bg = pendingBg; pendingBg = null;
            fr = pendingFrame; pendingFrame = null;
            st = pendingStatus; pendingStatus = null;
        }
        if (st != null) serverStatus = st;
        if (bg != null)
        {
            byte[] jpg = new byte[bg.Length - 1];
            Buffer.BlockCopy(bg, 1, jpg, 0, jpg.Length);
            bgTex.LoadImage(jpg);
        }
        if (fr != null) ApplyFrame(fr);

        // ---- our camera -> laptop (and local preview while nothing comes back)
        if (cam != null && cam.isPlaying && cam.didUpdateThisFrame
            && Time.realtimeSinceStartup >= nextSend)
        {
            nextSend = Time.realtimeSinceStartup + 1f / Mathf.Max(1f, sendFps);
            if (CaptureFrame())
            {
                if (connected)
                {
                    byte[] jpg = sendTex.EncodeToJPG(jpegQuality);
                    byte[] pkt = MakePacket(ToServerFrame, jpg);
                    lock (gate) { pendingSend = pkt; }
                    sendSignal.Set();
                }
                if (!Receiving) sendTex.Apply();
            }
        }

        bool showServer = Receiving && gotAnyFrame;
        mat.SetTexture("_CamTex", (showServer || sendTex == null) ? (Texture)camTex : (Texture)sendTex);

        if (!connected)
        {
            for (int s = 0; s < MaxSlots; s++) present[s] = false;
        }

        // ---- ease each person's vanish progress
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

        FitQuad(showServer ? (Texture)camTex : (Texture)sendTex);
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
        string json = Encoding.UTF8.GetString(fr, o, sl);

        camTex.LoadImage(jpg);
        maskTex.LoadImage(png);
        ApplyMeta(json);
        gotAnyFrame = true;
        lastServerFrame = Time.realtimeSinceStartup;
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
            if (ids[s] != p.id) { ids[s] = p.id; prog[s] = 0f; }   // a new person starts visible
            present[s] = true;
            vanish[s] = p.vanish;
            effect[s] = p.effect;
        }
    }

    void FitQuad(Texture t)
    {
        Camera c = Camera.main;
        if (c == null || !c.orthographic || t == null) return;
        float h = c.orthographicSize * 2f;
        float aspect = (float)t.width / Mathf.Max(1, t.height);
        transform.localScale = new Vector3(h * aspect, h, 1f);
        Vector3 p = transform.position;
        transform.position = new Vector3(c.transform.position.x, c.transform.position.y, p.z);
    }

    // ================================================================ lifecycle
    void OnApplicationPause(bool paused)
    {
        if (!paused && cam != null && !cam.isPlaying) cam.Play();
    }

    void OnDestroy()
    {
        running = false;
        wantConnect = false;
        sendSignal.Set();
        CloseClient();
        try { if (cam != null) cam.Stop(); } catch (Exception) { }
    }

    // ================================================================ on-screen UI
    void OnGUI()
    {
        float s = Screen.height / 540f;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        float W = Screen.width / s;
        float H = 540f;

        GUI.skin.label.fontSize = 16;
        GUI.skin.button.fontSize = 16;
        GUI.skin.textField.fontSize = 18;
        GUI.skin.box.fontSize = 14;

        if (GUI.Button(new Rect(W - 76, 6, 70, 30), showUi ? "Hide" : "UI")) showUi = !showUi;
        if (!showUi) return;

        GUI.Box(new Rect(0, 0, W - 82, 40), "");
        string conn = connected ? (Receiving ? "LIVE" : "connected") : (wantConnect ? "connecting..." : "not connected");
        string line = conn + "   " + serverStatus;
        if (!connected && lastError.Length > 0) line = conn + "   " + lastError;
        if (camError.Length > 0) line = camError;
        GUI.Label(new Rect(8, 8, W - 100, 30), line);

        GUI.Box(new Rect(0, H - 108, W, 108), "");
        float y1 = H - 100, y2 = H - 52;
        float x = 8;

        GUI.Label(new Rect(x, y1 + 4, 100, 36), "Laptop IP:"); x += 100;
        serverIp = GUI.TextField(new Rect(x, y1, 200, 40), serverIp ?? "", 45); x += 208;
        if (GUI.Button(new Rect(x, y1, 130, 40), (connected || wantConnect) ? "Disconnect" : "Connect"))
        {
            if (connected || wantConnect) Disconnect(); else Connect();
        }
        x += 138;
        if (GUI.Button(new Rect(x, y1, 220, 40), "CAPTURE BACKGROUND")) SendCommand("bg");

        x = 8;
        float bw = Mathf.Min(150f, (W - 16f - 5 * 6f) / 6f);
        if (GUI.Button(new Rect(x, y2, bw, 42), "Switch cam")) { useFront = !useFront; OpenCamera(); } x += bw + 6;
        if (GUI.Button(new Rect(x, y2, bw, 42), "Rotate 90")) { extraRot = (extraRot + 1) % 4; } x += bw + 6;
        if (GUI.Button(new Rect(x, y2, bw, 42), mirror ? "Mirror: on" : "Mirror: off")) { mirror = !mirror; } x += bw + 6;
        if (GUI.Button(new Rect(x, y2, bw, 42), "Vanish")) SendCommand("vanish"); x += bw + 6;
        if (GUI.Button(new Rect(x, y2, bw, 42), "Effect")) SendCommand("effect"); x += bw + 6;
        if (GUI.Button(new Rect(x, y2, bw, 42), "Person/Cloth")) SendCommand("mode");
    }
}

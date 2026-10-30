using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One interview question: topic, text, 4 options, the correct option and an explanation.
/// </summary>
[System.Serializable]
public class InterviewQuestion
{
    public string topic;
    [TextArea(2, 5)] public string question;
    public string[] options = new string[4];
    [Range(0, 3)] public int correctIndex;
    [TextArea(2, 5)] public string explanation;

    public InterviewQuestion() { }

    public InterviewQuestion(string topic, string question, string[] options, int correctIndex, string explanation)
    {
        this.topic = topic;
        this.question = question;
        this.options = options;
        this.correctIndex = correctIndex;
        this.explanation = explanation;
    }
}

/// <summary>
/// Controls the whole VR interview: welcome screen, questions, scoring, result screen and audio.
/// Put this on an empty GameObject called "InterviewManager".
/// All button clicks are wired from code, so you do NOT need to add OnClick events in the Inspector.
/// </summary>
public class InterviewManager : MonoBehaviour
{
    [Header("Panels (children of the World Space Canvas)")]
    public GameObject welcomePanel;
    public GameObject questionPanel;
    public GameObject scorePanel;
    public GameObject resultPanel;

    [Header("Question Screen")]
    public TMP_Text progressText;      // "Question 1 / 10"
    public TMP_Text topicText;         // "Topic: C++"
    public TMP_Text questionText;
    public TMP_Text explanationText;
    public TMP_Text scoreText;         // "Score: 3 / 10"
    public AnswerButton[] answerButtons = new AnswerButton[4];
    public Button nextButton;
    public TMP_Text nextButtonLabel;

    [Header("Result Screen")]
    public TMP_Text finalTitleText;    // "Interview Completed"
    public TMP_Text finalScoreText;    // "Your Score: 8 / 10"
    public TMP_Text performanceText;   // Excellent / Good / Needs Improvement
    public Button restartButton;

    [Header("Audio (optional - simple beeps are generated if left empty)")]
    public AudioSource audioSource;
    public AudioClip welcomeClip;
    public AudioClip clickClip;
    public AudioClip correctClip;
    public AudioClip wrongClip;
    public AudioClip completeClip;

    [Header("Settings")]
    public bool shuffleQuestions = false;
    [Range(0f, 100f)] public float excellentPercent = 80f;
    [Range(0f, 100f)] public float goodPercent = 50f;

    [Header("Questions (leave empty to use the 10 built-in questions)")]
    public List<InterviewQuestion> questions = new List<InterviewQuestion>();

    private readonly List<InterviewQuestion> session = new List<InterviewQuestion>();
    private int currentIndex;
    private int score;
    private bool answered;
    private bool interviewActive;

    public int Score { get { return score; } }
    public int TotalQuestions { get { return session.Count; } }

    // ------------------------------------------------------------------
    // Unity lifecycle
    // ------------------------------------------------------------------

    private void Awake()
    {
        if (questions == null || questions.Count == 0)
        {
            questions = CreateDefaultQuestions();
        }

        PrepareAudio();

        if (nextButton != null) nextButton.onClick.AddListener(NextQuestion);
        if (restartButton != null) restartButton.onClick.AddListener(RestartInterview);
    }

    private void Start()
    {
        ShowWelcome();
        PlayClip(welcomeClip);
    }

    // ------------------------------------------------------------------
    // Public API (called by StartInterview, AnswerButton and the UI buttons)
    // ------------------------------------------------------------------

    /// <summary>Starts (or restarts) the interview from question 1.</summary>
    public void BeginInterview()
    {
        session.Clear();
        session.AddRange(questions);
        if (shuffleQuestions) Shuffle(session);

        currentIndex = 0;
        score = 0;
        interviewActive = true;

        SetPanels(false, true, true, false);
        ShowQuestion();
    }

    /// <summary>Called by an AnswerButton with the index (0-3) of the chosen option.</summary>
    public void SelectAnswer(int optionIndex)
    {
        if (!interviewActive || answered) return;
        if (currentIndex < 0 || currentIndex >= session.Count) return;

        answered = true;
        InterviewQuestion q = session[currentIndex];
        bool isCorrect = optionIndex == q.correctIndex;
        if (isCorrect) score++;

        for (int i = 0; i < answerButtons.Length; i++)
        {
            if (answerButtons[i] == null) continue;

            if (i == q.correctIndex) answerButtons[i].SetState(AnswerVisualState.Correct);
            else if (i == optionIndex) answerButtons[i].SetState(AnswerVisualState.Wrong);
            else answerButtons[i].SetState(AnswerVisualState.Dimmed);
        }

        if (explanationText != null)
        {
            explanationText.gameObject.SetActive(true);
            explanationText.text = (isCorrect ? "Correct! " : "Not quite. ") + q.explanation;
        }

        UpdateScoreText();

        if (nextButton != null) nextButton.gameObject.SetActive(true);
        if (nextButtonLabel != null)
        {
            nextButtonLabel.text = (currentIndex >= session.Count - 1) ? "FINISH" : "NEXT";
        }

        PlayClip(isCorrect ? correctClip : wrongClip);
    }

    /// <summary>Goes to the next question, or to the result screen after the last one.</summary>
    public void NextQuestion()
    {
        if (!interviewActive || !answered) return;

        PlayClip(clickClip);
        currentIndex++;

        if (currentIndex >= session.Count) ShowResults();
        else ShowQuestion();
    }

    /// <summary>Restarts the interview from question 1.</summary>
    public void RestartInterview()
    {
        PlayClip(clickClip);
        BeginInterview();
    }

    /// <summary>Plays the button click sound.</summary>
    public void PlayClick()
    {
        PlayClip(clickClip);
    }

    // ------------------------------------------------------------------
    // Screens
    // ------------------------------------------------------------------

    private void ShowWelcome()
    {
        interviewActive = false;
        answered = false;
        SetPanels(true, false, false, false);
    }

    private void ShowQuestion()
    {
        answered = false;
        InterviewQuestion q = session[currentIndex];

        SetText(progressText, "Question " + (currentIndex + 1) + " / " + session.Count);
        SetText(topicText, "Topic: " + q.topic);
        SetText(questionText, q.question);
        UpdateScoreText();

        for (int i = 0; i < answerButtons.Length; i++)
        {
            if (answerButtons[i] == null) continue;

            bool hasOption = q.options != null && i < q.options.Length && !string.IsNullOrEmpty(q.options[i]);
            answerButtons[i].gameObject.SetActive(hasOption);
            if (hasOption) answerButtons[i].Setup(this, i, q.options[i]);
        }

        if (explanationText != null)
        {
            explanationText.text = "";
            explanationText.gameObject.SetActive(false);
        }

        if (nextButton != null) nextButton.gameObject.SetActive(false);
    }

    private void ShowResults()
    {
        interviewActive = false;
        SetPanels(false, false, false, true);

        int total = Mathf.Max(1, session.Count);
        float percent = score * 100f / total;

        string message;
        Color color;
        if (percent >= excellentPercent)
        {
            message = "Excellent! You are well prepared for a technical interview.";
            color = new Color(0.30f, 0.85f, 0.45f);
        }
        else if (percent >= goodPercent)
        {
            message = "Good. Review the topics you missed and try again.";
            color = new Color(1.00f, 0.80f, 0.25f);
        }
        else
        {
            message = "Needs Improvement. Keep practicing and try again.";
            color = new Color(1.00f, 0.45f, 0.40f);
        }

        SetText(finalTitleText, "Interview Completed");
        SetText(finalScoreText, "Your Score: " + score + " / " + session.Count);
        SetText(performanceText, message);
        if (performanceText != null) performanceText.color = color;

        PlayClip(completeClip);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void UpdateScoreText()
    {
        SetText(scoreText, "Score: " + score + " / " + session.Count);
    }

    private void SetPanels(bool welcome, bool question, bool scoreP, bool result)
    {
        if (welcomePanel != null) welcomePanel.SetActive(welcome);
        if (questionPanel != null) questionPanel.SetActive(question);
        if (scorePanel != null) scorePanel.SetActive(scoreP);
        if (resultPanel != null) resultPanel.SetActive(result);
    }

    private static void SetText(TMP_Text target, string value)
    {
        if (target != null) target.text = value;
    }

    private static void Shuffle(List<InterviewQuestion> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            InterviewQuestion tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------

    private void PrepareAudio()
    {
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f; // 2D sound, always clearly audible

        if (welcomeClip == null) welcomeClip = MakeTone("WelcomeTone", new float[] { 440f, 660f }, 0.15f);
        if (clickClip == null) clickClip = MakeTone("ClickTone", new float[] { 880f }, 0.06f);
        if (correctClip == null) correctClip = MakeTone("CorrectTone", new float[] { 660f, 880f }, 0.12f);
        if (wrongClip == null) wrongClip = MakeTone("WrongTone", new float[] { 220f, 165f }, 0.2f);
        if (completeClip == null) completeClip = MakeTone("CompleteTone", new float[] { 523f, 659f, 784f, 1046f }, 0.16f);
    }

    private void PlayClip(AudioClip clip)
    {
        if (audioSource != null && clip != null) audioSource.PlayOneShot(clip);
    }

    private static AudioClip MakeTone(string clipName, float[] frequencies, float secondsPerNote)
    {
        const int sampleRate = 44100;
        int samplesPerNote = Mathf.CeilToInt(sampleRate * secondsPerNote);
        float[] data = new float[samplesPerNote * frequencies.Length];

        for (int n = 0; n < frequencies.Length; n++)
        {
            for (int i = 0; i < samplesPerNote; i++)
            {
                float t = i / (float)sampleRate;
                float attack = i / (sampleRate * 0.005f);
                float release = (samplesPerNote - i) / (sampleRate * 0.03f);
                float envelope = Mathf.Clamp01(Mathf.Min(attack, release));
                data[n * samplesPerNote + i] = Mathf.Sin(2f * Mathf.PI * frequencies[n] * t) * envelope * 0.35f;
            }
        }

        AudioClip clip = AudioClip.Create(clipName, data.Length, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ------------------------------------------------------------------
    // Built-in questions
    // ------------------------------------------------------------------

    [ContextMenu("Load Default Questions Into Inspector")]
    private void LoadDefaultQuestionsIntoInspector()
    {
        questions = CreateDefaultQuestions();
    }

    public static List<InterviewQuestion> CreateDefaultQuestions()
    {
        List<InterviewQuestion> list = new List<InterviewQuestion>();

        list.Add(new InterviewQuestion(
            "C++",
            "Which keyword makes a member function support runtime polymorphism in C++?",
            new string[] { "static", "virtual", "inline", "explicit" },
            1,
            "A virtual function is resolved at runtime through the object's vtable, so the derived class version is called through a base class pointer or reference."));

        list.Add(new InterviewQuestion(
            "Python",
            "Which of these Python data types is immutable?",
            new string[] { "tuple", "list", "dict", "set" },
            0,
            "A tuple cannot be changed after it is created. Lists, dictionaries and sets can be modified in place."));

        list.Add(new InterviewQuestion(
            "Data Structures",
            "What is the average time complexity of searching in a hash table with a good hash function?",
            new string[] { "O(n)", "O(log n)", "O(1)", "O(n log n)" },
            2,
            "A good hash function spreads keys evenly, so a lookup takes constant time on average."));

        list.Add(new InterviewQuestion(
            "DBMS",
            "Which ACID property guarantees that a committed transaction survives a system crash?",
            new string[] { "Atomicity", "Consistency", "Isolation", "Durability" },
            3,
            "Durability means that once a transaction is committed, its changes are stored permanently, even if the system fails."));

        list.Add(new InterviewQuestion(
            "Operating Systems",
            "Which of these is NOT one of the four necessary conditions for deadlock?",
            new string[] { "Mutual exclusion", "Hold and wait", "Resource preemption", "Circular wait" },
            2,
            "Deadlock needs NO preemption (resources cannot be forcibly taken away). If preemption is allowed, that condition is broken."));

        list.Add(new InterviewQuestion(
            "Software Engineering",
            "Which development model is best suited to projects whose requirements change often?",
            new string[] { "Waterfall", "Agile", "V-Model", "Big Bang" },
            1,
            "Agile works in short iterations with continuous feedback, so changing requirements can be handled in every sprint."));

        list.Add(new InterviewQuestion(
            "Computer Networks",
            "Which OSI layer is responsible for routing packets between different networks?",
            new string[] { "Physical", "Data Link", "Transport", "Network" },
            3,
            "The Network layer (Layer 3) handles logical addressing such as IP and decides the route packets take between networks."));

        list.Add(new InterviewQuestion(
            "Computer Architecture",
            "What is the main purpose of a CPU cache?",
            new string[] { "Increase disk capacity", "Manage hardware interrupts", "Reduce the average memory access time", "Convert binary to decimal" },
            2,
            "A cache is small, very fast memory close to the CPU that keeps recently used data, which lowers the average time to access memory."));

        list.Add(new InterviewQuestion(
            "Algorithms",
            "What is the worst-case time complexity of Quick Sort?",
            new string[] { "O(n^2)", "O(n log n)", "O(n)", "O(log n)" },
            0,
            "With consistently bad pivots, for example already sorted input with the first element as pivot, partitions are unbalanced and the cost is O(n^2). The average case is O(n log n)."));

        list.Add(new InterviewQuestion(
            "General Computer Engineering",
            "Which logic gate outputs 1 only when ALL of its inputs are 1?",
            new string[] { "OR", "AND", "XOR", "NOR" },
            1,
            "An AND gate outputs 1 only if every input is 1. OR outputs 1 if any input is 1."));

        return list;
    }
}

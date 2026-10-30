using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Visual states of an answer button.</summary>
public enum AnswerVisualState
{
    Normal,
    Correct,
    Wrong,
    Dimmed
}

/// <summary>
/// Put this on each of the four answer buttons (AnswerButton1..AnswerButton4).
/// It needs a Button, an Image (background) and a child TextMeshPro text.
/// When the VR ray + trigger presses the button, it tells the InterviewManager which option was chosen.
/// </summary>
[RequireComponent(typeof(Button))]
public class AnswerButton : MonoBehaviour
{
    [Header("Set automatically by InterviewManager")]
    public InterviewManager manager;
    public int optionIndex;

    [Header("References (found automatically if left empty)")]
    public TMP_Text label;

    [Header("Colours")]
    public Color normalColor = new Color(0.16f, 0.28f, 0.45f, 1f);
    public Color correctColor = new Color(0.13f, 0.60f, 0.30f, 1f);
    public Color wrongColor = new Color(0.75f, 0.20f, 0.20f, 1f);
    public Color dimmedColor = new Color(0.20f, 0.22f, 0.27f, 1f);

    private Button button;
    private Image background;
    private bool listenerAdded;

    private void Awake()
    {
        CacheComponents();
        AddListener();
    }

    private void CacheComponents()
    {
        if (button == null) button = GetComponent<Button>();
        if (background == null) background = GetComponent<Image>();
        if (label == null) label = GetComponentInChildren<TMP_Text>(true);
    }

    private void AddListener()
    {
        if (listenerAdded || button == null) return;
        button.onClick.AddListener(OnClicked);
        listenerAdded = true;
    }

    /// <summary>Called by InterviewManager each time a new question is shown.</summary>
    public void Setup(InterviewManager owner, int index, string optionText)
    {
        CacheComponents();
        AddListener();

        manager = owner;
        optionIndex = index;

        if (label != null)
        {
            label.text = ((char)('A' + index)) + ".  " + optionText;
        }

        SetState(AnswerVisualState.Normal);
    }

    /// <summary>Changes the colour and clickability of the button.</summary>
    public void SetState(AnswerVisualState state)
    {
        CacheComponents();
        if (button == null) return;

        Color c;
        switch (state)
        {
            case AnswerVisualState.Correct: c = correctColor; break;
            case AnswerVisualState.Wrong: c = wrongColor; break;
            case AnswerVisualState.Dimmed: c = dimmedColor; break;
            default: c = normalColor; break;
        }

        if (background != null) background.color = Color.white; // the Button tint does the colouring

        ColorBlock block = button.colors;
        block.normalColor = c;
        block.highlightedColor = Color.Lerp(c, Color.white, 0.25f);
        block.pressedColor = Color.Lerp(c, Color.black, 0.20f);
        block.selectedColor = c;
        block.disabledColor = c;
        block.colorMultiplier = 1f;
        block.fadeDuration = 0.1f;
        button.colors = block;

        // Only a "Normal" button can be pressed. After answering, all buttons are locked.
        button.interactable = (state == AnswerVisualState.Normal);
    }

    /// <summary>Enables or disables clicking without changing the colour.</summary>
    public void SetInteractable(bool value)
    {
        CacheComponents();
        if (button != null) button.interactable = value;
    }

    private void OnClicked()
    {
        if (manager == null) manager = FindFirstObjectByType<InterviewManager>();
        if (manager != null) manager.SelectAnswer(optionIndex);
    }
}

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Put this on the START INTERVIEW button (inside WelcomePanel).
/// When the VR ray + trigger presses the button, the interview begins.
/// </summary>
[RequireComponent(typeof(Button))]
public class StartInterview : MonoBehaviour
{
    public InterviewManager manager;

    private Button button;

    private void Awake()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(OnStartPressed);
    }

    private void OnDestroy()
    {
        if (button != null) button.onClick.RemoveListener(OnStartPressed);
    }

    private void OnStartPressed()
    {
        Debug.Log("START BUTTON CLICK RECEIVED!");
        if (manager == null) manager = FindFirstObjectByType<InterviewManager>();
        if (manager == null)
        {
            Debug.LogError("StartInterview: no InterviewManager found in the scene.");
            return;
        }

        manager.PlayClick();
        manager.BeginInterview();
    }
}

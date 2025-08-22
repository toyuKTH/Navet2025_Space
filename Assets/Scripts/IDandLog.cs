using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;

public class SessionLogger : MonoBehaviour
{
    [Header("UI 设置")]
    public GameObject welcomePanel;   // 拖 Main Canvas 里的 Welcome Panel
    public Text unityText;            // 如果用普通 Text
    public TMP_Text tmpText;          // 如果用 TextMeshPro

    private static int userID;
    private string logPath;

    void Start()
    {
        if (userID == 0)
            userID = Random.Range(1000, 10000);

        logPath = Path.Combine(Application.persistentDataPath, "UserLog_" + userID + ".txt");

        WriteLog($"[{System.DateTime.Now}] User {userID} started session");

        UpdateWelcomeText();
        ShowWelcomePanel(true);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            WriteLog($"[{System.DateTime.Now}] User {userID} pressed R (reset)");
            ShowWelcomePanel(true);
        }
    }

    private void UpdateWelcomeText()
    {
        string message = "Welcome, your ID is " + userID+"\nThumb up to explore!";

        if (unityText != null)
            unityText.text = message;

        if (tmpText != null)
            tmpText.text = message;
    }

    private void ShowWelcomePanel(bool show)
    {
        if (welcomePanel != null)
            welcomePanel.SetActive(show);
    }

    private void WriteLog(string message)
    {
        File.AppendAllText(logPath, message + "\n");
        Debug.Log(message);
        Debug.Log("Log file saved at: " + logPath);
    }
}

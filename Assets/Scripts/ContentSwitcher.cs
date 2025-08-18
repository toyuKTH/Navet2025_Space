using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;

public class ContentSwitcher : MonoBehaviour
{
    [Header("UI面板")]
    public GameObject welcomePanel;
    public GameObject targetPanel;

    [Header("调试")]
    public bool showDebugInfo = true;

    public enum ContentState
    {
        Welcome,
        Target
    }

    private ContentState currentState = ContentState.Welcome;
    private ContentState previousState = ContentState.Welcome;

    // ✅ OSC部分
    private UdpClient oscClient;
    private IPEndPoint supercolliderEndPoint;

    void Start()
    {
        InitOSC();
        ShowWelcomeContent();
    }

    void InitOSC()
    {
        oscClient = new UdpClient();
        supercolliderEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 57121);//先检查用的是哪个端口NetAddr.langPort.postln;
        Debug.Log("📡 OSC初始化成功，目标端口：127.0.0.1:57120");
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            SwitchToWelcome();
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            SwitchToTarget();
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            GoBack();
        }
    }

    public void SwitchToWelcome()
    {
        Debug.Log("切换到欢迎界面");

        previousState = currentState;
        currentState = ContentState.Welcome;
        SetPanelActive(ContentState.Welcome);

        SendStringMessage("welcome");
    }

    public void SwitchToTarget()
    {
        Debug.Log("切换到目标界面");

        previousState = currentState;
        currentState = ContentState.Target;
        SetPanelActive(ContentState.Target);

        SendStringMessage("target");
    }

    public void GoBack()
    {
        Debug.Log($"返回上一个界面: {previousState}");

        if (previousState == currentState)
        {
            Debug.Log("上一个状态与当前相同，默认回Welcome");
            SwitchToWelcome();
            return;
        }

        ContentState temp = currentState;
        currentState = previousState;
        previousState = temp;

        SetPanelActive(currentState);

        if (currentState == ContentState.Welcome)
            SendStringMessage("welcome");
        else if (currentState == ContentState.Target)
            SendStringMessage("target");
    }

    public bool CanGoBack() => previousState != currentState;
    public ContentState GetCurrentState() => currentState;
    public ContentState GetPreviousState() => previousState;
    public bool IsInWelcomeState() => currentState == ContentState.Welcome;
    public bool IsInTargetState() => currentState == ContentState.Target;

    void ShowWelcomeContent()
    {
        currentState = ContentState.Welcome;
        previousState = ContentState.Welcome;
        SetPanelActive(ContentState.Welcome);
    }

    void SetPanelActive(ContentState state)
    {
        if (welcomePanel != null) welcomePanel.SetActive(false);
        if (targetPanel != null) targetPanel.SetActive(false);

        switch (state)
        {
            case ContentState.Welcome:
                if (welcomePanel != null) welcomePanel.SetActive(true);
                break;
            case ContentState.Target:
                if (targetPanel != null) targetPanel.SetActive(true);
                break;
        }
    }

    // ✅ 发送 /unity + 字符串参数，如 "welcome" 或 "target"
    void SendStringMessage(string keyword)
    {
        if (oscClient == null) return;

        List<byte> message = new List<byte>();

        string address = "/unity";
        message.AddRange(EncodeOSCString(address));  // 地址
        message.AddRange(EncodeOSCString(",s"));     // 类型标签
        message.AddRange(EncodeOSCString(keyword));  // 字符串参数

        oscClient.Send(message.ToArray(), message.Count, supercolliderEndPoint);
        Debug.Log($"📤 已发送 OSC：{address} {keyword}");
    }

    byte[] EncodeOSCString(string str)
    {
        var bytes = Encoding.ASCII.GetBytes(str);
        int pad = 4 - (bytes.Length % 4);
        if (pad == 4) pad = 0;
        byte[] padded = new byte[bytes.Length + pad];
        System.Array.Copy(bytes, padded, bytes.Length);
        return padded;
    }

    void OnDestroy()
    {
        oscClient?.Close();
    }

    void OnGUI()
    {
        if (showDebugInfo)
        {
            GUILayout.BeginArea(new Rect(10, 300, 250, 150));
            GUILayout.Label("=== 内容切换调试 ===");
            GUILayout.Label($"当前状态: {currentState}");
            GUILayout.Label($"上一个状态: {previousState}");
            GUILayout.Label($"可以返回: {(CanGoBack() ? "是" : "否")}");

            GUILayout.Space(10);
            GUILayout.Label("键盘控制:");
            GUILayout.Label("1键：切换到 Welcome");
            GUILayout.Label("2键：切换到 Target");
            GUILayout.Label("3键：返回上一界面");

            GUILayout.EndArea();
        }
    }
}

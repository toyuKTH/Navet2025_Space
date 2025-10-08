using UnityEngine;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

public class SpaceToCC7 : MonoBehaviour
{
    [Header("MIDI Output")]
    [Tooltip("留空=使用系统中的第一个输出设备；否则填写 loopMIDI/IAC 的端口名（例如 \"MIDI7\"）")]
    public string midiOutName = "MIDI7";
    [Range(0, 15)] public int channel = 0; // 0=CH1

    [Header("Controller")]
    [Tooltip("7 = Channel Volume；想用 Expression 可改为 11")]
    [Range(0, 127)] public int ccNumber = 7;

    [Header("Values")]
    [Range(0, 127)] public int pressValue = 110;   // 按下空格 → 目标值
    [Range(0, 127)] public int releaseValue = 0;   // 松开空格 → 回落值

    [Header("Fade")]
    [Min(0f)] public float fadeSeconds = 0.15f;
    public bool sendInitialOnEnable = true;

    [Header("Diagnostics")]
    public bool debugLogs = true;
    [Tooltip("自动测试：每1秒做一次 0→127→0 的扫动，便于验证 DAW 是否有响应")]
    public bool autoTest = false;

    private OutputDevice outDev;
    private Coroutine fadeCo;
    private Coroutine autoTestCo;
    private int currentValue = -1; // -1=未知

    void Log(string s)
    {
        if (debugLogs) Debug.Log($"[SpaceToCC7] {s}");
    }

    // ===== 枚举所有设备，便于核对端口名 =====
    static List<OutputDevice> GetAllOutputsSafe()
    {
        try { return OutputDevice.GetAll()?.ToList() ?? new List<OutputDevice>(); }
        catch { return new List<OutputDevice>(); }
    }

    void Awake()
    {
        var all = GetAllOutputsSafe();
        if (all.Count == 0)
        {
            Debug.LogWarning("[SpaceToCC7] 未找到任何 MIDI 输出设备。请确认已创建并启用了 loopMIDI/IAC 端口。");
        }
        else
        {
            Log("可用输出设备： " + string.Join(", ", all.Select(d => d.Name)));
        }

        try
        {
            if (!string.IsNullOrEmpty(midiOutName))
            {
                // 精确匹配
                outDev = all.FirstOrDefault(d => d.Name == midiOutName);

                // 没找到就尝试模糊匹配
                if (outDev == null)
                {
                    outDev = all.FirstOrDefault(d => d.Name.ToLower().Contains(midiOutName.ToLower()));
                    if (outDev != null)
                        Debug.LogWarning($"[SpaceToCC7] 未找到精确名 \"{midiOutName}\"，使用模糊匹配：{outDev.Name}");
                }
            }
            else
            {
                outDev = all.FirstOrDefault();
            }
        }
        catch { outDev = null; }

        if (outDev == null)
            Debug.LogWarning($"[SpaceToCC7] 未连接到输出设备（期望名：\"{midiOutName}\"）。");
        else
            Log($"已连接输出设备：{outDev.Name} | CH{channel + 1}");
    }

    void OnEnable()
    {
        if (outDev != null && sendInitialOnEnable)
        {
            currentValue = Mathf.Clamp(releaseValue, 0, 127);
            SendCC(ccNumber, currentValue);
            Log($"OnEnable -> CC{ccNumber}={currentValue}");
        }

        if (autoTest && autoTestCo == null)
            autoTestCo = StartCoroutine(AutoTestRoutine());
    }

    void OnDisable()
    {
        if (fadeCo != null) StopCoroutine(fadeCo);
        if (autoTestCo != null) { StopCoroutine(autoTestCo); autoTestCo = null; }

        if (outDev != null)
        {
            SendCC(ccNumber, Mathf.Clamp(releaseValue, 0, 127));
            Log($"OnDisable -> CC{ccNumber}={releaseValue}");
        }
    }

    void OnDestroy()
    {
        try { outDev?.Dispose(); } catch { }
    }

    void Update()
    {
        // 提示：键盘事件只在 Game 窗口焦点内有效
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Log("捕获到 Space KeyDown");
            StartFadeTo(pressValue);
        }

        if (Input.GetKeyUp(KeyCode.Space))
        {
            Log("捕获到 Space KeyUp");
            StartFadeTo(releaseValue);
        }
    }

    // ===== 手动按钮（右键组件有菜单）=====
    [ContextMenu("Send Press (to pressValue)")]
    public void ContextSendPress() => StartFadeTo(pressValue);

    [ContextMenu("Send Release (to releaseValue)")]
    public void ContextSendRelease() => StartFadeTo(releaseValue);

    // ===== 自动测试：每秒扫一次 0→127→0 =====
    IEnumerator AutoTestRoutine()
    {
        var wait = new WaitForSeconds(1f);
        while (enabled)
        {
            yield return wait;
            Log("AutoTest: sweep up");
            yield return StartCoroutine(FadeTo(127, 0.25f));
            Log("AutoTest: sweep down");
            yield return StartCoroutine(FadeTo(0, 0.25f));
        }
    }

    // ===== Core =====
    void StartFadeTo(int target)
    {
        if (outDev == null)
        {
            Log("无输出设备，忽略（检查端口名、loopMIDI/IAC、Waveform是否打开该端口输入）。");
            return;
        }

        target = Mathf.Clamp(target, 0, 127);

        if (fadeSeconds <= 0f)
        {
            currentValue = target;
            SendCC(ccNumber, currentValue);
            Log($"瞬时 -> CC{ccNumber}={currentValue}");
            return;
        }

        if (fadeCo != null) StopCoroutine(fadeCo);
        fadeCo = StartCoroutine(FadeTo(target, fadeSeconds));
    }

    IEnumerator FadeTo(int target, float seconds)
    {
        int start = (currentValue < 0) ? Mathf.Clamp(releaseValue, 0, 127) : currentValue;
        float t = 0f;
        seconds = Mathf.Max(0.001f, seconds);

        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            int v = Mathf.RoundToInt(Mathf.Lerp(start, target, t));
            if (v != currentValue)
            {
                currentValue = v;
                SendCC(ccNumber, currentValue);
            }
            yield return null;
        }

        currentValue = target;
        SendCC(ccNumber, currentValue);
        fadeCo = null;
        Log($"完成 -> CC{ccNumber}={currentValue}");
    }

    void SendCC(int cc, int value)
    {
        if (outDev == null) return;
        value = Mathf.Clamp(value, 0, 127);

        try
        {
            outDev.SendEvent(
                new ControlChangeEvent((SevenBitNumber)cc, (SevenBitNumber)value)
                { Channel = (FourBitNumber)channel }
            );
            Log($"→ 发送 CC{cc}={value} (CH{channel + 1})");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SpaceToCC7] 发送失败：{e.Message}");
        }
    }
}

using UnityEngine;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

public class SonificationMelody : MonoBehaviour
{
    [Header("Debug")]
    public bool debugLogs = true;
    void Log(string msg) { if (debugLogs) Debug.Log($"[SonificationMelody:{name}] {msg}"); }

    [Header("MIDI Output")]
    [Tooltip("留空=自动选第一个输出设备；或填 loopMIDI/IAC 的精确名称")]
    public string midiOutName = "";
    [Range(0, 15)] public int channel = 0; // 0 = CH1

    [Header("Volume / Fade")]
    public int volumeCC = 7;
    [Range(0, 127)] public int targetVolume = 110;
    public float fadeInSeconds = 1.5f;
    public float fadeOutSeconds = 0.8f;

    [Header("Pitch Bend")]
    [Range(-1f, 1f)] public float pitchBendNorm = 0f; // -1..1
    [Range(0f, 0.99f)] public float bendSlew = 0.85f;

    [Header("Timbre CC")]
    public int ccCutoff = 74, ccResonance = 71, ccDistortion = 20;
    [Range(0f, 1f)] public float timbre01 = 0.5f;
    [Range(0f, 0.99f)] public float timbreSlew = 0.85f;

    [Header("Rate (兼容占位，不驱动节奏)")]
    [Range(0f, 1f)] public float rate01 = 0.5f;

    [Header("Note Defaults")]
    public int defaultNote = 60;
    public int defaultVelocity = 100;
    public float defaultDuration = 0.25f;

    // ===== 内部 =====
    OutputDevice outDev;
    Coroutine fadeCo, ctrlCo;
    int currentVolume = 0;
    readonly List<int> onNotes = new List<int>();

    void Awake()
    {
        try
        {
            if (!string.IsNullOrEmpty(midiOutName))
                outDev = OutputDevice.GetByName(midiOutName);
            else
                outDev = OutputDevice.GetAll().FirstOrDefault();
        }
        catch { outDev = null; }

        if (outDev == null) Log("未找到 MIDI 输出设备（请检查 loopMIDI 端口名称/是否已打开）");
        else Log($"已连接 MIDI 输出设备：{outDev.Name}  通道: CH{channel + 1}");

        currentVolume = 0;
        SendCC(volumeCC, 0);
    }

    void OnEnable()
    {
        if (outDev == null) { Log("OnEnable 但无输出设备"); return; }

        // 只淡入 + 开连续控制；不再启动任何自动旋律
        if (fadeCo != null) StopCoroutine(fadeCo);
        fadeCo = StartCoroutine(FadeVolumeTo(targetVolume, fadeInSeconds));
        if (ctrlCo == null) ctrlCo = StartCoroutine(ContinuousControllers());
        Log($"淡入至 {targetVolume}，时长 {fadeInSeconds}s");
    }

    void OnDisable()
    {
        if (outDev == null) return;

        if (fadeCo != null) { StopCoroutine(fadeCo); fadeCo = null; }
        if (ctrlCo != null) { StopCoroutine(ctrlCo); ctrlCo = null; }

        StartCoroutine(FadeOutThenAllNotesOff());
        Log("OnDisable：开始淡出并清音");
    }

    void OnDestroy()
    {
        try { AllNotesOff(); outDev?.Dispose(); } catch { }
    }

    // 外部控制生成ambient music↓
    public void SetMasterGain01(float v01, float fadeSeconds = 0.08f)
    {
        v01 = Mathf.Clamp01(v01);
        int tgt = Mathf.RoundToInt(v01 * 127f);
        targetVolume = tgt;
        if (fadeCo != null) StopCoroutine(fadeCo);
        fadeCo = StartCoroutine(FadeVolumeTo(targetVolume, Mathf.Max(0.001f, fadeSeconds)));
    }

    public void SetExpression01(float v01)
    {
        // CC11，一些音源用它做“细音量”
        int val = Mathf.RoundToInt(Mathf.Clamp01(v01) * 127f);
        SendCC(11, val);
    }



    // ===== 连续控制（PB/CC） =====
    IEnumerator ContinuousControllers()
    {
        var wait = new WaitForSeconds(1f / 60f);
        float lastPB = 0f, lastTim = 0f;
        bool first = true;
        float nextLog = 0f;

        while (enabled && outDev != null)
        {
            // PB
            float pb = Mathf.Lerp(lastPB, pitchBendNorm, 1f - bendSlew);
            lastPB = pb;
            int pb14 = Mathf.RoundToInt((pb * 0.5f + 0.5f) * 16383f);
            SendPitchBend(pb14);

            // Timbre CC
            float tim = Mathf.Lerp(lastTim, timbre01, 1f - timbreSlew);
            lastTim = tim;
            int ccVal = Mathf.RoundToInt(Mathf.Clamp01(tim) * 127f);
            SendCC(ccCutoff, ccVal);
            SendCC(ccResonance, Mathf.RoundToInt(ccVal * 0.75f));
            SendCC(ccDistortion, Mathf.RoundToInt(ccVal * 0.6f));

            if (first || Time.realtimeSinceStartup >= nextLog)
            {
                Log($"CTRL tick  PB14={pb14}  CC74={ccVal}  rate01={rate01:0.00}");
                nextLog = Time.realtimeSinceStartup + 2f;
                first = false;
            }
            yield return wait;
        }
        ctrlCo = null;
    }

    // ===== 对外接口：ApplyControls + 触发音符 =====
    public void ApplyControls(float pitch01_, float timbre01_, float rate01_)
    {
        pitchBendNorm = Mathf.Lerp(-1f, 1f, Mathf.Clamp01(pitch01_));
        timbre01 = Mathf.Clamp01(timbre01_);
        rate01 = Mathf.Clamp01(rate01_);
    }

    public void SetTranspose(int semitones) { /* 留空：移调由外部算入 note 值 */ }
    public void SetPitchBend01(float v01) => pitchBendNorm = Mathf.Lerp(-1f, 1f, Mathf.Clamp01(v01));
    public void SetTimbre01(float v01) => timbre01 = Mathf.Clamp01(v01);

    // === 新增：兼容占位 ===
    public void SetRate01(float v01) => rate01 = Mathf.Clamp01(v01);

    public void PlayNote(int midiNote, int velocity = -1, float duration = -1f)
    {
        if (outDev == null) { Log("PlayNote 失败：无输出设备"); return; }
        if (velocity < 0) velocity = defaultVelocity;
        if (duration <= 0f) duration = defaultDuration;

        midiNote = Mathf.Clamp(midiNote, 0, 127);
        velocity = Mathf.Clamp(velocity, 0, 127);

        SendNoteOn(midiNote, velocity);
        onNotes.Add(midiNote);
        StartCoroutine(NoteOffLater(midiNote, duration));
        Log($"PlayNote {midiNote} vel={velocity} dur={duration:0.00}s");
    }

    public void PlayChord(int[] midiNotes, int velocity = -1, float duration = -1f)
    {
        if (midiNotes == null || midiNotes.Length == 0) return;
        foreach (var n in midiNotes) PlayNote(n, velocity, duration);
    }

    public void AllNotesOff()
    {
        SendCC(123, 0); // All Notes Off
        SendCC(120, 0); // All Sound Off
        foreach (var n in onNotes) SendNoteOff(n);
        onNotes.Clear();
    }

    // ===== 低层 MIDI 发送 =====
    void SendNoteOn(int note, int vel)
    {
        if (outDev == null) return;
        outDev.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel)
        { Channel = (FourBitNumber)channel });
    }

    void SendNoteOff(int note)
    {
        if (outDev == null) return;
        outDev.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0)
        { Channel = (FourBitNumber)channel });
    }

    void SendCC(int cc, int value)
    {
        if (outDev == null) return;
        value = Mathf.Clamp(value, 0, 127);
        outDev.SendEvent(new ControlChangeEvent((SevenBitNumber)cc, (SevenBitNumber)value)
        { Channel = (FourBitNumber)channel });
    }

    void SendPitchBend(int value014)
    {
        if (outDev == null) return;
        int clamped = Mathf.Clamp(value014, 0, 16383);
        outDev.SendEvent(new PitchBendEvent((ushort)clamped) { Channel = (FourBitNumber)channel });
    }

    IEnumerator NoteOffLater(int note, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        SendNoteOff(note);
        onNotes.Remove(note);
        Log($"NoteOff {note}");
    }

    IEnumerator FadeVolumeTo(int target, float seconds)
    {
        seconds = Mathf.Max(0.001f, seconds);
        int start = currentVolume;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            int v = Mathf.RoundToInt(Mathf.Lerp(start, target, t));
            if (v != currentVolume) { SendCC(volumeCC, v); currentVolume = v; }
            yield return null;
        }
        SendCC(volumeCC, target);
        currentVolume = target;
        Log($"淡入完成，音量={target}");
        fadeCo = null;
    }

    IEnumerator FadeOutThenAllNotesOff()
    {
        int start = currentVolume;
        float t = 0f;
        float seconds = Mathf.Max(0.001f, fadeOutSeconds);
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            int v = Mathf.RoundToInt(Mathf.Lerp(start, 0, t));
            if (v != currentVolume) { SendCC(volumeCC, v); currentVolume = v; }
            yield return null;
        }
        SendCC(volumeCC, 0);
        currentVolume = 0;
        AllNotesOff();
    }

    [ContextMenu("Test Ping")]
    public void TestPing() => PlayNote(defaultNote, defaultVelocity, defaultDuration);
}

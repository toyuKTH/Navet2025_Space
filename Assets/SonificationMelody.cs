using UnityEngine;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

public class SonificationMelody : MonoBehaviour
{
    // ===== 可调：淡入淡出 & 音量通道 =====
    [Header("Fade & Volume")]
    [Tooltip("组件启用时的淡入秒数")]
    public float fadeInSeconds = 1.5f;
    [Tooltip("组件禁用时的淡出秒数")]
    public float fadeOutSeconds = 1.2f;
    [Tooltip("用于淡入淡出的音量CC（11=Expression，7=Main Volume）")]
    public int volumeCC = 11; // CC11更适合做表情/淡变
    [Range(0, 127)] public int targetVolume = 110; // 淡入后的目标音量

    // ===== 和声风格（更“稳”的ambient）=====
    private int lastRootMidi = int.MinValue;
    [SerializeField] int maxRootStep = 5;     // 相邻和弦根音最大跳进（半音）
    [SerializeField] int registerLow = 50;   // 下限
    [SerializeField] int registerHigh = 76;   // 上限
    [SerializeField] float triadBias = 0.6f; // 三和弦概率

    [Header("MIDI Output")]
    [Tooltip("留空=自动选第一个输出设备；或填 loopMIDI/IAC 的精确名称")]
    public string midiOutName = "";
    [Range(0, 15)] public int channel = 0; // 0=CH1

    [Header("Harmony & Notes")]
    [Tooltip("根音（MIDI 音高），C4=60")]
    public int baseNote = 60;
    [Tooltip("协和度较高的音程集合（相对根音）")]
    public int[] consonantIntervals = new int[] { 0, 3, 4, 5, 7, 9, 12 };
    [Tooltip("力度范围")]
    public Vector2Int velocityRange = new Vector2Int(70, 110);

    [Header("Rate (触发频率)")]
    [Tooltip("最小/最大触发间隔（秒）")]
    public Vector2 intervalRange = new Vector2(0.25f, 0.8f);
    [Range(0, 1)] public float rate01 = 0.5f; // 0=慢，1=快
    [Tooltip("触发时间抖动（百分比）")]
    [Range(0, 0.5f)] public float intervalJitter = 0.08f;

    [Header("Pitch (整体音高 & 滑音)")]
    [Tooltip("整体移调（半音）")]
    [Range(-24, 24)] public int transposeSemitones = 0;
    [Tooltip("Pitch Bend 范围（半音），需与合成器一致")]
    [Range(1, 24)] public int pitchBendRangeSemitones = 12;
    [Tooltip("外部输入映射到 Pitch Bend（-1..+1）")]
    [Range(-1f, 1f)] public float pitchBendNorm = 0f;
    [Tooltip("Pitch Bend 平滑（0=快，1=很平滑）")]
    [Range(0, 0.99f)] public float bendSlew = 0.8f;

    [Header("Timbre (音色调制)")]
    [Tooltip("外部输入映射到音色（0..1），越大越亮/更强失真")]
    [Range(0f, 1f)] public float timbre01 = 0.5f;
    [Tooltip("音色平滑（0=快，1=很平滑）")]
    [Range(0, 0.99f)] public float timbreSlew = 0.85f;

    [Tooltip("滤波截止 CC（通常 74）/ 共振 CC（通常 71）/ 失真 CC（自行 MIDI Learn）")]
    public int ccCutoff = 74, ccResonance = 71, ccDistortion = 20;

    [Header("Runtime")]
    public bool autoStart = true;

    // ===== 内部 =====
    private OutputDevice outDev;
    private Coroutine noteLoopCo, ctrlCo, fadeCo;
    private float smoothedTimbre = 0.5f;
    private float smoothedBend = 0f;
    private int currentVolume = 0; // 记住最近一次发送的音量CC
    private readonly List<int> currentlyOnNotes = new List<int>();

    // ---------- 生命周期 ----------
    void Awake()
    {
        // 打开MIDI设备
        var devices = OutputDevice.GetAll();
        if (devices.Count == 0)
        {
            Debug.LogError("No MIDI output devices found. Start loopMIDI/IAC first, then restart Unity.");
            return;
        }
        foreach (var d in devices) Debug.Log("[MIDI OUT] " + d.Name);

        outDev = string.IsNullOrEmpty(midiOutName)
            ? OutputDevice.GetByIndex(0)
            : devices.FirstOrDefault(d => d.Name == midiOutName) ?? OutputDevice.GetByIndex(0);

        Debug.Log("Using MIDI OUT: " + outDev.Name);
        outDev.PrepareForEventsSending();

        lastRootMidi = baseNote + transposeSemitones;
    }

    void OnEnable()
    {
        if (outDev == null) return;

        // 先把音量拉到0，再淡入到 targetVolume
        if (fadeCo != null) StopCoroutine(fadeCo);
        SendCC(volumeCC, 0);
        currentVolume = 0;
        fadeCo = StartCoroutine(FadeVolumeTo(targetVolume, fadeInSeconds));

        if (autoStart) StartMelody();
        if (ctrlCo == null) ctrlCo = StartCoroutine(ContinuousControllers());
    }

    void OnDisable()
    {
        if (outDev == null) return;

        // 淡出，然后关掉所有正在发声的音
        if (fadeCo != null) StopCoroutine(fadeCo);
        fadeCo = StartCoroutine(FadeOutThenSilence());
    }

    void OnDestroy()
    {
        try { AllNotesOff(); outDev?.Dispose(); }
        catch { }
    }

    // ---------- 外部控制入口 ----------
    /// <summary>
    /// 外部统一控制入口（0..1）：pitch01 -> PitchBend；timbre01 -> 音色；rate01 -> 触发频率
    /// </summary>
    public void ApplyControls(float pitch01, float timbre01_, float rate01_)
    {
        SetPitchBend01(Mathf.Lerp(-1f, 1f, Mathf.Clamp01(pitch01)));
        SetTimbre01(Mathf.Clamp01(timbre01_));
        SetRate01(Mathf.Clamp01(rate01_));
    }

    // 单独旋钮（保持原有接口）
    public void SetRate01(float x) { rate01 = Mathf.Clamp01(x); }
    public void SetTimbre01(float x) { timbre01 = Mathf.Clamp01(x); }
    public void SetPitchBend01(float x) { pitchBendNorm = Mathf.Clamp(x, -1f, 1f); }
    public void SetTranspose(int semis) { transposeSemitones = Mathf.Clamp(semis, -24, 24); }

    public void StartMelody()
    {
        if (noteLoopCo == null) noteLoopCo = StartCoroutine(NoteLoop());
    }
    public void StopMelody()
    {
        if (noteLoopCo != null) { StopCoroutine(noteLoopCo); noteLoopCo = null; }
        AllNotesOff();
    }

    // ---------- 主循环：和谐随机的ambient ----------
    IEnumerator NoteLoop()
    {
        while (enabled) // 跟随组件启用状态
        {
            // 1) 计算触发间隔（rate01 从最大间隔渐变到最小间隔）
            float baseInterval = Mathf.Lerp(intervalRange.y, intervalRange.x, rate01);
            float jitter = 1f + Random.Range(-intervalJitter, intervalJitter);
            float interval = Mathf.Max(0.08f, baseInterval * jitter);

            // 2) 选根音（小步进行 + 限定音域），构建“和谐的双音/三音”
            int root = ChooseNextRoot();
            var chordNotes = BuildChord(root);

            // 3) 力度与音长（更连奏：70%~95% 的占空比）
            int vel = Random.Range(velocityRange.x, velocityRange.y + 1);
            float dur = Mathf.Clamp(interval * Random.Range(0.70f, 0.95f), 0.05f, 4f);

            // 4) 发音
            for (int i = 0; i < chordNotes.Count; i++)
            {
                SendNoteOn(chordNotes[i], vel);
                currentlyOnNotes.Add(chordNotes[i]);
            }

            yield return new WaitForSeconds(dur);

            for (int i = 0; i < chordNotes.Count; i++)
            {
                SendNoteOff(chordNotes[i]);
                currentlyOnNotes.Remove(chordNotes[i]);
            }

            // 5) 留一点空隙
            float rest = Mathf.Max(0.01f, interval - dur);
            yield return new WaitForSeconds(rest);
        }
    }

    // ---------- 连续控制：滤波/失真 + Pitch Bend ----------
    IEnumerator ContinuousControllers()
    {
        var wait = new WaitForSeconds(1f / 60f); // ~60Hz
        while (enabled)
        {
            smoothedTimbre = Mathf.Lerp(smoothedTimbre, timbre01, 1f - timbreSlew);
            smoothedBend = Mathf.Lerp(smoothedBend, pitchBendNorm, 1f - bendSlew);

            int cutoff = Mathf.RoundToInt(Mathf.Lerp(20f, 127f, smoothedTimbre));
            int reso = Mathf.RoundToInt(Mathf.Lerp(30f, 110f, smoothedTimbre));
            int distort = Mathf.RoundToInt(Mathf.Lerp(0f, 127f, Mathf.Pow(smoothedTimbre, 0.8f)));
            SendCC(ccCutoff, cutoff);
            SendCC(ccResonance, reso);
            SendCC(ccDistortion, distort);

            int bend14 = Mathf.RoundToInt((smoothedBend * 0.5f + 0.5f) * 16383f);
            SendPitchBend(bend14);

            yield return wait;
        }
        ctrlCo = null;
    }

    // ---------- 发送事件 ----------
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

    // ---------- 和声工具 ----------
    int ChooseNextRoot()
    {
        int deg = consonantIntervals[Random.Range(0, consonantIntervals.Length)];
        int candidate = baseNote + transposeSemitones + deg;

        if (lastRootMidi == int.MinValue)
        {
            candidate = Mathf.Clamp(candidate, registerLow, registerHigh);
            lastRootMidi = candidate;
            return candidate;
        }

        // 小步进行：把candidate拉到靠近lastRootMidi的邻近八度
        while (candidate - lastRootMidi > maxRootStep) candidate -= 12;
        while (lastRootMidi - candidate > maxRootStep) candidate += 12;

        candidate = Mathf.Clamp(candidate, registerLow, registerHigh);
        lastRootMidi = candidate;
        return candidate;
    }

    List<int> BuildChord(int root)
    {
        var notes = new List<int> { root };

        bool makeTriad = Random.value < triadBias; // 约60%为三和弦
        if (makeTriad)
        {
            int third = (Random.value < 0.5f) ? 3 : 4; // m3 / M3
            notes.Add(root + third);
            notes.Add(root + 7); // P5

            int max = Mathf.Max(notes[0], notes[1], notes[2]);
            int min = Mathf.Min(notes[0], notes[1], notes[2]);
            if (max > registerHigh) { for (int i = 0; i < notes.Count; i++) notes[i] -= 12; }
            if (min < registerLow) { for (int i = 0; i < notes.Count; i++) notes[i] += 12; }
        }
        else
        {
            int[] dyads = new int[] { 3, 4, 7, 9 }; // m3, M3, P5, M6
            int iv = dyads[Random.Range(0, dyads.Length)];
            notes.Add(root + iv);

            int max = Mathf.Max(notes[0], notes[1]);
            int min = Mathf.Min(notes[0], notes[1]);
            if (max > registerHigh) { notes[0] -= 12; notes[1] -= 12; }
            if (min < registerLow) { notes[0] += 12; notes[1] += 12; }
        }
        return notes;
    }

    // ---------- 淡入淡出 & 安全停止 ----------
    IEnumerator FadeVolumeTo(int target, float seconds)
    {
        target = Mathf.Clamp(target, 0, 127);
        seconds = Mathf.Max(0f, seconds);
        if (seconds == 0f)
        {
            SendCC(volumeCC, target);
            currentVolume = target;
            yield break;
        }

        int start = currentVolume;
        float t = 0f;
        while (t < 1f && enabled)
        {
            t += Time.deltaTime / seconds;
            int v = Mathf.RoundToInt(Mathf.Lerp(start, target, t));
            if (v != currentVolume) { SendCC(volumeCC, v); currentVolume = v; }
            yield return null;
        }
        if (enabled && currentVolume != target) { SendCC(volumeCC, target); currentVolume = target; }
    }

    IEnumerator FadeOutThenSilence()
    {
        // 淡出音量
        float seconds = Mathf.Max(0f, fadeOutSeconds);
        int start = currentVolume;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            int v = Mathf.RoundToInt(Mathf.Lerp(start, 0, t));
            if (v != currentVolume) { SendCC(volumeCC, v); currentVolume = v; }
            yield return null;
        }
        SendCC(volumeCC, 0);
        currentVolume = 0;

        // 停掉音符/协程
        StopMelody(); // 会AllNotesOff
        fadeCo = null;
    }

    public void AllNotesOff()
    {
        // CC123: All Notes Off, CC120: All Sound Off（有的合成器支持）
        SendCC(123, 0);
        SendCC(120, 0);
        // 保险关音：把还在记录里的音符逐个NoteOff
        for (int i = 0; i < currentlyOnNotes.Count; i++)
            SendNoteOff(currentlyOnNotes[i]);
        currentlyOnNotes.Clear();
    }
}

using UnityEngine;
using System.Collections;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

/// 持续生成“原声化旋律”（随机协和音阶音符），并暴露可调参数
/// - 触发频率（Rate）：控制音符间隔
/// - 音高（Pitch）：可整体移调 + 连续 Pitch Bend
/// - 音色（Timbre）：连续 CC 控制（滤波截止/共振/失真）
///
/// 用法：挂到任意 GameObject；在 Inspector 里把 midiOutName
/// 设为 loopMIDI / IAC 里能看到的精确名称（或留空自动取第一个）。
public class SonificationMelody : MonoBehaviour
{
    [Header("MIDI Output")]
    [Tooltip("留空=自动选第一个输出设备；或填 loopMIDI/IAC 的精确名称")]
    public string midiOutName = "";
    [Range(0, 15)] public int channel = 0; // 0=CH1

    [Header("Harmony & Notes")]
    [Tooltip("根音（MIDI 音高），C4=60")]
    public int baseNote = 60;
    [Tooltip("可选音程集合（相对根音）。默认为常见协和度较高的集合。")]
    public int[] consonantIntervals = new int[] { 0, 3, 4, 5, 7, 9, 12 }; // m3, M3, P4, P5, M6, 8ve
    [Tooltip("允许上下多少个八度做随机漂移")]
    public int octaveRange = 1; // 上下各 1 个八度
    [Tooltip("力度范围")]
    public Vector2Int velocityRange = new Vector2Int(70, 110);
    [Tooltip("音长（秒），可与间隔分离")]
    public Vector2 noteLengthRange = new Vector2(0.15f, 0.35f);

    [Header("Rate (触发频率)")]
    [Tooltip("最小/最大触发间隔（秒）")]
    public Vector2 intervalRange = new Vector2(0.25f, 0.8f);
    [Range(0, 1)] public float rate01 = 0.5f; // 0=慢，1=快（会在区间内插值）
    [Tooltip("触发时间抖动（百分比）")]
    [Range(0, 0.5f)] public float intervalJitter = 0.1f;

    [Header("Pitch (整体音高 & 滑音)")]
    [Tooltip("整体移调（半音）")]
    [Range(-24, 24)] public int transposeSemitones = 0;
    [Tooltip("Pitch Bend 范围（半音），需与合成器的 Bend Range 设置一致")]
    [Range(1, 24)] public int pitchBendRangeSemitones = 12;
    [Tooltip("外部输入映射到 Pitch Bend（-1..+1），0 为不弯音")]
    [Range(-1f, 1f)] public float pitchBendNorm = 0f;
    [Tooltip("Pitch Bend 平滑（0=跟随快，1=非常平滑）")]
    [Range(0, 0.99f)] public float bendSlew = 0.8f;

    [Header("Timbre (音色：滤波/失真)")]
    [Tooltip("外部输入映射到音色（0..1），越大越明亮/更强失真）")]
    [Range(0f, 1f)] public float timbre01 = 0.5f;
    [Tooltip("音色平滑（0=跟随快，1=非常平滑）")]
    [Range(0, 0.99f)] public float timbreSlew = 0.85f;

    [Tooltip("滤波截止 CC（通常 74）/ 共振 CC（通常 71）/ 失真 CC（自行在插件里 MIDI Learn）")]
    public int ccCutoff = 74, ccResonance = 71, ccDistortion = 20;

    [Header("Runtime")]
    public bool autoStart = true;

    private OutputDevice outDev;
    private Coroutine noteLoopCo;
    private float smoothedTimbre = 0.5f;
    private float smoothedBend = 0f;

    void Start()
    {
        // 列设备
        var devices = OutputDevice.GetAll();
        if (devices.Count == 0)
        {
            Debug.LogError("No MIDI output devices found. Start loopMIDI/IAC first, then restart Unity.");
            return;
        }
        foreach (var d in devices) Debug.Log("[MIDI OUT] " + d.Name);

        // 选设备
        outDev = string.IsNullOrEmpty(midiOutName)
            ? OutputDevice.GetByIndex(0)
            : devices.FirstOrDefault(d => d.Name == midiOutName) ?? OutputDevice.GetByIndex(0);

        Debug.Log("Using MIDI OUT: " + outDev.Name);
        outDev.PrepareForEventsSending();

        if (autoStart) StartMelody();
        // 连续 CC / PitchBend 更新
        StartCoroutine(ContinuousControllers());
    }

    void OnDestroy() { outDev?.Dispose(); }

    // —— 对外：可在别的脚本里实时调用这些方法来“拧旋钮” —— //
    public void SetRate01(float x) { rate01 = Mathf.Clamp01(x); }
    public void SetTimbre01(float x) { timbre01 = Mathf.Clamp01(x); }
    // -1..+1：负值向下弯，正值向上弯（取决于 pitchBendRangeSemitones）
    public void SetPitchBend01(float x) { pitchBendNorm = Mathf.Clamp(x, -1f, 1f); }
    public void SetTranspose(int semis) { transposeSemitones = Mathf.Clamp(semis, -24, 24); }

    public void StartMelody()
    {
        if (noteLoopCo == null) noteLoopCo = StartCoroutine(NoteLoop());
    }
    public void StopMelody()
    {
        if (noteLoopCo != null) { StopCoroutine(noteLoopCo); noteLoopCo = null; }
    }

    // —— 主循环：随机协和音符 —— //
    IEnumerator NoteLoop()
    {
        var wait = new WaitForSeconds(0.2f);

        while (true)
        {
            // 1) 计算当前触发间隔（rate01 从最大间隔渐变到最小间隔）
            float baseInterval = Mathf.Lerp(intervalRange.y, intervalRange.x, rate01);
            float jitter = 1f + Random.Range(-intervalJitter, intervalJitter);
            float interval = Mathf.Max(0.05f, baseInterval * jitter);

            // 2) 选一个协和音程 + 随机八度
            int deg = consonantIntervals[Random.Range(0, consonantIntervals.Length)];
            int oct = Random.Range(-octaveRange, octaveRange + 1) * 12;
            int note = baseNote + transposeSemitones + deg + oct;

            // 3) 力度与音长
            int vel = Random.Range(velocityRange.x, velocityRange.y + 1);
            float dur = Random.Range(noteLengthRange.x, noteLengthRange.y);

            // 4) 发音
            SendNoteOn(note, vel);
            // 提前结束保护
            if (dur > interval * 0.9f) dur = interval * 0.9f;
            yield return new WaitForSeconds(dur);
            SendNoteOff(note);

            // 5) 等到下一次触发
            float rest = Mathf.Max(0.01f, interval - dur);
            yield return new WaitForSeconds(rest);
        }
    }

    // —— 连续控制：滤波/共振/失真 + Pitch Bend —— //
    IEnumerator ContinuousControllers()
    {
        var wait = new WaitForSeconds(1f / 60f); // 60Hz 刷新
        while (true)
        {
            // 指数平滑
            smoothedTimbre = Mathf.Lerp(smoothedTimbre, timbre01, 1f - timbreSlew);
            smoothedBend = Mathf.Lerp(smoothedBend, pitchBendNorm, 1f - bendSlew);

            // 映射到 CC（0..127）
            int cutoff = Mathf.RoundToInt(Mathf.Lerp(20f, 127f, smoothedTimbre));
            int reso = Mathf.RoundToInt(Mathf.Lerp(30f, 110f, smoothedTimbre));
            int distort = Mathf.RoundToInt(Mathf.Lerp(0f, 127f, Mathf.Pow(smoothedTimbre, 0.8f)));

            SendCC(ccCutoff, cutoff);
            SendCC(ccResonance, reso);
            SendCC(ccDistortion, distort); // 在你的合成器里对这个 CC 做 MIDI Learn

            // Pitch Bend：-1..+1 -> 0..16383，中心 8192
            int bend14 = Mathf.RoundToInt((smoothedBend * 0.5f + 0.5f) * 16383f);
            SendPitchBend(bend14);

            yield return wait;
        }
    }

    // —— 发送事件 —— //
    void SendNoteOn(int note, int vel)
    {
        outDev?.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel)
        { Channel = (FourBitNumber)channel });
    }
    void SendNoteOff(int note)
    {
        outDev?.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0)
        { Channel = (FourBitNumber)channel });
    }
    void SendCC(int cc, int value)
    {
        value = Mathf.Clamp(value, 0, 127);
        outDev?.SendEvent(new ControlChangeEvent((SevenBitNumber)cc, (SevenBitNumber)value)
        { Channel = (FourBitNumber)channel });
    }
    void SendPitchBend(int value014)
    {
        // 0..16383，中心 8192
        int clamped = Mathf.Clamp(value014, 0, 16383);
        outDev?.SendEvent(new PitchBendEvent((ushort)clamped)
        {
            Channel = (FourBitNumber)channel
        });
    }
}

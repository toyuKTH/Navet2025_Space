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

    // === 轻量参数（可在 Inspector 暂时不暴露）===
    private int lastRootMidi = int.MinValue;         // 记住上一个根音，做小步进行
    [SerializeField] int maxRootStep = 5;            // 相邻根音最大跳进（半音），避免大跳
    [SerializeField] int registerLow = 50;           // 限定和弦演奏音域下限（可按乐器调）
    [SerializeField] int registerHigh = 76;          // 限定和弦演奏音域上限
    [SerializeField] float triadBias = 0.6f;         // 生成三和弦的概率（其余是双音）

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

        lastRootMidi = baseNote + transposeSemitones;

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
        while (true)
        {
            // 1) 计算当前触发间隔（rate01 从最大间隔渐变到最小间隔），稍降抖动
            float baseInterval = Mathf.Lerp(intervalRange.y, intervalRange.x, rate01);
            float jitter = 1f + Random.Range(-Mathf.Min(intervalJitter, 0.08f), Mathf.Min(intervalJitter, 0.08f));
            float interval = Mathf.Max(0.08f, baseInterval * jitter);

            // 2) 选根音（限制与上一次的跳进 & 保持在指定音域），再构建“和谐的双音/三音”
            int root = ChooseNextRoot();
            var chordNotes = BuildChord(root);  // 包含 root，本次要同时发音的若干音

            // 3) 力度与音长（音长更接近“连奏”，占比 70%~95%）
            int vel = Random.Range(velocityRange.x, velocityRange.y + 1);
            float dur = Mathf.Clamp(interval * Random.Range(0.70f, 0.95f), 0.05f, 4f);

            // 4) 发音（同一时刻发出和弦的每个音）
            for (int i = 0; i < chordNotes.Count; i++)
                SendNoteOn(chordNotes[i], vel);

            yield return new WaitForSeconds(dur);

            for (int i = 0; i < chordNotes.Count; i++)
                SendNoteOff(chordNotes[i]);

            // 5) 边留一点点空隙，避免完全糊成一片
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

    // 选一个“接近上次”的根音，避免频繁跨八度；同时限定在一个舒适音域
    int ChooseNextRoot()
    {
        // 从你的“协和集合”里挑度数（相对 baseNote）
        int deg = consonantIntervals[Random.Range(0, consonantIntervals.Length)];
        int candidate = baseNote + transposeSemitones + deg;

        // 第一次：把它夹到音域里并记录
        if (lastRootMidi == int.MinValue)
        {
            candidate = Mathf.Clamp(candidate, registerLow, registerHigh);
            lastRootMidi = candidate;
            return candidate;
        }

        // 尽量靠近上一次根音（跨 12 调整落位），以减少跳进
        // 让 candidate 向 lastRootMidi 的邻近八度对齐
        while (candidate - lastRootMidi > maxRootStep) candidate -= 12;
        while (lastRootMidi - candidate > maxRootStep) candidate += 12;

        // 最终再夹到音域里（若触边，顺便记一下）
        candidate = Mathf.Clamp(candidate, registerLow, registerHigh);
        lastRootMidi = candidate;
        return candidate;
    }

    // 构建“听感和谐”的双音/三音（根音 + 三度(+五度)）
    // 三和弦：随机大小三度 + 完全五度；双音：在 {m3, M3, P5, M6} 中挑一个
    System.Collections.Generic.List<int> BuildChord(int root)
    {
        var notes = new System.Collections.Generic.List<int>();
        notes.Add(root);

        bool makeTriad = Random.value < triadBias; // 约 60% 概率做三和弦
        if (makeTriad)
        {
            int third = (Random.value < 0.5f) ? 3 : 4; // m3 或 M3
            notes.Add(root + third);
            notes.Add(root + 7); // 完全五度

            // 防止过高：若最高音超域，整体降八度；若过低则整体升八度
            int max = Mathf.Max(notes[0], notes[1], notes[2]);
            int min = Mathf.Min(notes[0], notes[1], notes[2]);
            if (max > registerHigh) { for (int i = 0; i < notes.Count; i++) notes[i] -= 12; }
            if (min < registerLow) { for (int i = 0; i < notes.Count; i++) notes[i] += 12; }
        }
        else
        {
            // 双音：挑一个“协和双音”间隔
            int[] dyads = new int[] { 3, 4, 7, 9 }; // m3, M3, P5, M6
            int iv = dyads[Random.Range(0, dyads.Length)];
            notes.Add(root + iv);

            // 简单夹域
            int max = Mathf.Max(notes[0], notes[1]);
            int min = Mathf.Min(notes[0], notes[1]);
            if (max > registerHigh) { notes[0] -= 12; notes[1] -= 12; }
            if (min < registerLow) { notes[0] += 12; notes[1] += 12; }
        }

        return notes;
    }

}

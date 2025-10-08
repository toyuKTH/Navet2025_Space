using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GenerativeAmbientMidi : MonoBehaviour
{
    [Header("Targets")]
    public SonificationMelody[] tracks;         // 这些轨会被“生成器”喂音符

    [Header("Transport")]
    public float baseBpm = 60f;                 // 基础 BPM
    [Range(0.25f, 4f)] public float tempoMultiplier = 1f;  // 手势可控：0.5 慢、1 正常、2 加速
    public bool swing = false;
    [Range(0f, 0.5f)] public float swingAmount = 0.12f;    // 奇偶 16 分的交替偏移

    [Header("Scale / Pitch")]
    public int rootMidi = 60;                   // C4
    public int[] scaleSemis = new int[] { 0, 2, 3, 5, 7, 9, 10 }; // 多利安举例
    public int lowOctave = -2;                  // 相对 root 的八度偏移
    public int highOctave = 1;

    [Header("Density & Length")]
    [Range(0f, 1f)] public float noteDensity = 0.35f;      // 每个 8 分音位置上触发概率
    public Vector2 lengthBeatsRange = new Vector2(0.25f, 1.5f); // 时值范围（单位：拍）
    public Vector2 velocityRange = new Vector2(70, 110);

    [Header("Chords (Pads)")]
    public bool enablePads = true;
    public float padEveryBeats = 8f;                           // 每隔多少拍来一次和弦
    public Vector2Int chordSizeRange = new Vector2Int(2, 4);   // 和弦大小
    public Vector2 padLengthBeatsRange = new Vector2(2f, 8f);  // 和弦持续

    [Header("Humanize")]
    public float timeJitterMs = 12f;       // 触发时间轻微抖动（毫秒）
    public int seed = 12345;

    [Header("Routing / Per-Track Scales")]
    [Range(0f, 1f)] public float masterVolume01 = 0.7f; // 手势可控
    [Range(0.25f, 4f)] public float masterTempoMult = 1f; // 手势可控（叠加到 tempoMultiplier）
    public float[] trackGains01;             // 每轨额外音量（0~1）

    [Header("Lifecycle")]
    public bool autoEnableTracksOnEnable = true;
    public bool autoDisableTracksOnDisable = true;

    private System.Random rng;
    private Coroutine loopCo, padCo;

    void OnEnable()
    {
        rng = new System.Random(seed);
        EnsureArrays();

        if (autoEnableTracksOnEnable) SetTracksEnabled(true);   // ⬅️ 新增
        ApplyVolumeToAll();

        loopCo = StartCoroutine(LoopNotes());
        if (enablePads) padCo = StartCoroutine(LoopPads());
    }

    void OnDisable()
    {
        if (loopCo != null) StopCoroutine(loopCo);
        if (padCo != null) StopCoroutine(padCo);

        if (autoDisableTracksOnDisable) SetTracksEnabled(false); // ⬅️ 新增
    }

    void SetTracksEnabled(bool on)                                // ⬅️ 新增
    {
        if (tracks == null) return;
        foreach (var t in tracks)
        {
            if (t == null) continue;
            t.enabled = on;               // 触发 SonificationMelody.OnEnable / OnDisable
        }
    }

    void EnsureArrays()
    {
        if (tracks == null) tracks = new SonificationMelody[0];
        if (trackGains01 == null || trackGains01.Length != tracks.Length)
        {
            var g = new float[tracks.Length];
            for (int i = 0; i < g.Length; i++) g[i] = 1f;
            trackGains01 = g;
        }
    }

    // ===== 外部控制（给手势脚本调用）=====
    public void SetVolume01(float v01)
    {
        masterVolume01 = Mathf.Clamp01(v01);
        ApplyVolumeToAll();
    }

    public void SetTempoMultiplier(float mult)
    {
        masterTempoMult = Mathf.Clamp(mult, 0.25f, 4f);
    }

    void ApplyVolumeToAll()
    {
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null) continue;
            float per = (i < trackGains01.Length) ? Mathf.Clamp01(trackGains01[i]) : 1f;
            // 直接通过 CC7 发送通道音量
            t.SendVolumeCC01(masterVolume01 * per, 0.08f);
            t.SetExpression01(1f); // 拉起 CC11，避免音源静音
        }
    }

    // ===== 主循环：基于 8 分音格点的“概率触发” =====
    IEnumerator LoopNotes()
    {
        while (enabled)
        {
            float bpm = Mathf.Max(1f, baseBpm * tempoMultiplier * masterTempoMult);
            float beatSecs = 60f / bpm;
            float grid = beatSecs * 0.5f; // 8 分音格

            // swing
            float wait = grid;
            if (swing)
            {
                // 交替偏移：奇拍延长、偶拍缩短
                bool odd = (Time.frameCount % 2) == 1;
                wait = grid * (odd ? (1f + swingAmount) : (1f - swingAmount));
            }

            // 触发概率
            if (rng.NextDouble() < noteDensity)
            {
                // 选择一个目标轨（均匀或加权）
                int idx = PickEnabledTrackIndex();
                if (idx >= 0)
                {
                    var t = tracks[idx];
                    if (t != null && t.isActiveAndEnabled)
                    {
                        int note = PickScaleNote();
                        int vel = Mathf.RoundToInt(Mathf.Lerp(velocityRange.x, velocityRange.y, (float)rng.NextDouble()));
                        float lenBeats = Mathf.Lerp(lengthBeatsRange.x, lengthBeatsRange.y, (float)rng.NextDouble());
                        float lenSecs = lenBeats * beatSecs;

                        // 轻微抖动
                        float jitter = (float)(rng.NextDouble() * 2 - 1) * (timeJitterMs / 1000f);
                        if (jitter > 0) yield return new WaitForSeconds(jitter);

                        t.PlayNote(note, vel, lenSecs);
                    }
                }
            }

            yield return new WaitForSeconds(wait);
        }
    }

    // ===== 和弦铺底 =====
    IEnumerator LoopPads()
    {
        while (enabled)
        {
            float bpm = Mathf.Max(1f, baseBpm * tempoMultiplier * masterTempoMult);
            float beatSecs = 60f / bpm;

            int idx = PickEnabledTrackIndex();
            if (idx >= 0)
            {
                var t = tracks[idx];
                if (t != null && t.isActiveAndEnabled)
                {
                    int chordSize = Mathf.Clamp(rng.Next(chordSizeRange.x, chordSizeRange.y + 1), 1, 6);
                    List<int> chord = new List<int>();
                    // 和弦根音
                    int root = PickScaleNote();
                    chord.Add(root);
                    // 叠添其它度
                    for (int k = 1; k < chordSize; k++)
                    {
                        int note = root + PickChordInterval();
                        chord.Add(Mathf.Clamp(note, 0, 127));
                    }
                    float lenBeats = Mathf.Lerp(padLengthBeatsRange.x, padLengthBeatsRange.y, (float)rng.NextDouble());
                    float lenSecs = lenBeats * beatSecs;
                    int vel = Mathf.RoundToInt(Mathf.Lerp(velocityRange.x, velocityRange.y, 0.6f));

                    t.PlayChord(chord.ToArray(), vel, lenSecs);
                }
            }

            yield return new WaitForSeconds(padEveryBeats * beatSecs);
        }
    }

    int PickEnabledTrackIndex()
    {
        if (tracks == null || tracks.Length == 0) return -1;
        // 简单随机选择一个已启用的
        for (int tries = 0; tries < 8; tries++)
        {
            int idx = rng.Next(0, tracks.Length);
            if (tracks[idx] != null && tracks[idx].isActiveAndEnabled) return idx;
        }
        // 兜底顺序找
        for (int i = 0; i < tracks.Length; i++)
            if (tracks[i] != null && tracks[i].isActiveAndEnabled) return i;
        return -1;
    }

    int PickScaleNote()
    {
        if (scaleSemis == null || scaleSemis.Length == 0) scaleSemis = new int[] { 0, 2, 4, 5, 7, 9, 11 };
        int deg = scaleSemis[rng.Next(0, scaleSemis.Length)];
        int oct = rng.Next(lowOctave, highOctave + 1);
        int note = rootMidi + deg + (oct * 12);
        return Mathf.Clamp(note, 0, 127);
    }

    int PickChordInterval()
    {
        // 常见和弦叠置（3/4/5/7 度）；可按需扩展
        int[] opts = new int[] { 3, 4, 7, 10, 12 };
        return opts[rng.Next(0, opts.Length)];
    }
}

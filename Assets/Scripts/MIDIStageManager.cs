using System;
using System.Collections.Generic;
using UnityEngine;

public class MIDIStageManager : MonoBehaviour
{
    [Header("Debug")]
    public bool debugLogs = true;
    void Log(string m) { if (debugLogs) Debug.Log($"[MIDIStageManager:{name}] {m}"); }

    // 兼容老项目 Stage 枚举
    public enum Stage { Intro = 0, SpaceFX = 1, Planet1 = 2, Planet2 = 3, Planet3 = 4 }
    public Stage CurrentStage { get; private set; } = Stage.Intro;

    [Header("Tracks (拖入若干 SonificationMelody)")]
    public SonificationMelody[] tracks = new SonificationMelody[6];

    [Header("Stage Profiles（控制哪些轨开启+全局缩放+移调）")]
    public List<StageProfile> stageProfiles = new List<StageProfile>() { new StageProfile("Default", 6) };

    [Header("Per-Track Control Scaling（叠加缩放）")]
    public TrackScale[] perTrackScales = new TrackScale[6];

    [Header("行为设置")]
    public bool applyStageGlobalTranspose = true;   // 把 Stage 的移调用于 TriggerNote
    public bool broadcastControlsToAllTracks = true;// 忽略 Stage，直接全轨分发
    public bool firstActiveOnly = true;

    [SerializeField] private int activeStageIndex = 0;

    [Serializable]
    public class StageProfile
    {
        public string name = "Stage";
        public bool active = true;
        public float globalPitchScale = 1f;
        public float globalTimbreScale = 1f;
        public float globalRateScale = 1f;
        public int globalTransposeSemis = 0;
        public bool[] trackEnabled;

        public StageProfile(string n, int trackCount)
        {
            name = n; active = true;
            globalTransposeSemis = 0;
            globalPitchScale = 1f; globalTimbreScale = 1f; globalRateScale = 1f;
            trackEnabled = new bool[Mathf.Max(1, trackCount)];
            for (int i = 0; i < trackEnabled.Length; i++) trackEnabled[i] = true;
        }
    }

    [Serializable]
    public struct TrackScale { [Range(0,2)] public float pitch; [Range(0,2)] public float timbre; [Range(0,2)] public float rate; }

    void Reset()      => EnsureArrays();
    void OnValidate() => EnsureArrays();

    void EnsureArrays()
    {
        int n = Mathf.Max(1, tracks != null ? tracks.Length : 0);
        if (perTrackScales == null || perTrackScales.Length != n)
        {
            var arr = new TrackScale[n];
            for (int i = 0; i < n; i++) { arr[i].pitch = 1f; arr[i].timbre = 1f; arr[i].rate = 1f; }
            perTrackScales = arr;
        }
        foreach (var sp in stageProfiles)
        {
            if (sp.trackEnabled == null || sp.trackEnabled.Length != n)
            {
                sp.trackEnabled = new bool[n];
                for (int i = 0; i < n; i++) sp.trackEnabled[i] = true;
            }
        }
    }

    // ===== 连续控制分发（手势用于 timbre/rate/pitchbend） =====
    public void ApplyControls(float pitch01, float timbre01, float rate01)
    {
        pitch01 = Mathf.Clamp01(pitch01);
        timbre01 = Mathf.Clamp01(timbre01);
        rate01  = Mathf.Clamp01(rate01);

        Log($"ApplyControls pitch={pitch01:0.00} timbre={timbre01:0.00} rate={rate01:0.00}");

        if (tracks == null || tracks.Length == 0) { Log("无 tracks"); return; }

        var stage = GetActiveStageProfile();
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null) { Log($"Track[{i}] = null"); continue; }
            if (!t.isActiveAndEnabled) { Log($"Track[{i}] disabled"); continue; }

            bool enabledByStage = broadcastControlsToAllTracks || (stage == null || (i < stage.trackEnabled.Length && stage.trackEnabled[i]));
            if (!enabledByStage) { Log($"Track[{i}] 在 Stage[{activeStageIndex}] 中被禁用"); continue; }

            float p = pitch01, tm = timbre01, r = rate01;
            if (i < perTrackScales.Length)
            {
                p *= perTrackScales[i].pitch; tm *= perTrackScales[i].timbre; r *= perTrackScales[i].rate;
            }
            if (stage != null)
            {
                p *= stage.globalPitchScale; tm *= stage.globalTimbreScale; r *= stage.globalRateScale;
            }

            p = Mathf.Clamp01(p); tm = Mathf.Clamp01(tm); r = Mathf.Clamp01(r);
            t.ApplyControls(p, tm, r);
            Log($"Track[{i}] <- p={p:0.00} t={tm:0.00} r={r:0.00}");
        }
    }

    // ===== 新增：触发音符（手势击发） =====
    public void TriggerNote(int note, int velocity = 100, float duration = 0.25f)
    {
        var stage = GetActiveStageProfile();
        int transpose = (applyStageGlobalTranspose && stage != null) ? stage.globalTransposeSemis : 0;

        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null || !t.isActiveAndEnabled) continue;

            bool enabledByStage = broadcastControlsToAllTracks || (stage == null || (i < stage.trackEnabled.Length && stage.trackEnabled[i]));
            if (!enabledByStage) continue;

            int n = Mathf.Clamp(note + transpose, 0, 127);
            t.PlayNote(n, velocity, duration);
        }
        Log($"TriggerNote note={note} vel={velocity} dur={duration:0.00}s (transpose={transpose})");
    }

    StageProfile GetActiveStageProfile()
    {
        if (broadcastControlsToAllTracks) return null;

        // 优先第一个 active
        for (int i = 0; i < stageProfiles.Count; i++)
        {
            if (stageProfiles[i] != null && stageProfiles[i].active)
            {
                activeStageIndex = i;
                if (firstActiveOnly) return stageProfiles[i];
            }
        }
        if (stageProfiles.Count > 0)
        {
            activeStageIndex = Mathf.Clamp(activeStageIndex, 0, stageProfiles.Count - 1);
            return stageProfiles[activeStageIndex];
        }
        return null;
    }

    // ===== Stage 切换（兼容旧接口） =====
    public void SetStage(Stage stage) { CurrentStage = stage; SetStage((int)stage); }
    public void SetStage(int index)
    {
        if (stageProfiles == null || stageProfiles.Count == 0) return;
        index = Mathf.Clamp(index, 0, stageProfiles.Count - 1);
        for (int i = 0; i < stageProfiles.Count; i++) stageProfiles[i].active = (i == index);
        activeStageIndex = index;
        Log($"激活 Stage[{index}]：{stageProfiles[index].name}");
    }
}

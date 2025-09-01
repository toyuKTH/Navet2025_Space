using UnityEngine;
using System;
using System.Collections.Generic;

public class MIDIStageManager : MonoBehaviour
{
    public enum Stage
    {
        Intro = 0,        // 阶段1：具象自然声+白噪
        SpaceFX = 1,      // 阶段2：回响/滤波/宇宙噪声
        Planet1 = 2,      // 阶段3：星球1
        Planet2 = 3,      // 阶段4：星球2
        Planet3 = 4       // 阶段5：星球3
        // 需要更多阶段可以继续加
    }

    [Header("Tracks (size=6) - 拖入 6 个 SonificationMelody")]
    [Tooltip("按照 MIDI1..MIDI6 的顺序把 6 个子物体的 SonificationMelody 拖进来")]
    public SonificationMelody[] tracks = new SonificationMelody[6];

    [Serializable]
    public class TrackControlScale
    {
        [Range(0f, 2f)] public float pitchScale = 1f;   // 对外部 pitch01 的缩放
        [Range(0f, 2f)] public float timbreScale = 1f;  // 对外部 timbre01 的缩放
        [Range(0f, 2f)] public float rateScale = 1f;    // 对外部 rate01 的缩放
    }

    [Serializable]
    public class StageProfile
    {
        public string displayName = "Stage";
        public Stage stage;
        [Tooltip("激活哪些轨道（和 tracks 数组等长）")]
        public bool[] activeMask = new bool[6];

        [Tooltip("（可选）进入该阶段时额外整体移调半音（会覆盖每轨道内部的 transpose 设置）")]
        public int globalTransposeSemis = 0;

        [Tooltip("（可选）对所有激活轨道的控制缩放")]
        public TrackControlScale globalScales = new TrackControlScale();
    }

    [Header("Stage Profiles - 在 Inspector 配置每个阶段勾选哪些轨道开")]
    public List<StageProfile> stageProfiles = new List<StageProfile>();

    [Header("Per-Track Control Scaling（单独缩放，可叠加在 Stage 的 globalScales 上）")]
    public TrackControlScale[] perTrackScales = new TrackControlScale[6]
    {
        new TrackControlScale(), new TrackControlScale(), new TrackControlScale(),
        new TrackControlScale(), new TrackControlScale(), new TrackControlScale()
    };

    [Header("行为设置")]
    [Tooltip("切换阶段时，是否给所有激活轨道应用 Stage 的全局移调（半音）")]
    public bool applyStageGlobalTranspose = false;

    [Tooltip("将控制广播给所有“当前激活”的轨道；如关闭则只发给 firstActiveOnly 指定的第一个激活轨道")]
    public bool broadcastControlsToAllActive = true;

    [Tooltip("当不广播时，仅把控制发给激活列表中的第一个轨道")]
    public bool firstActiveOnly = true;

    // 当前状态
    public Stage CurrentStage { get; private set; } = Stage.Intro;

    // 最近一次外部控制值（0..1）
    float _lastPitch01 = 0f, _lastTimbre01 = 0.5f, _lastRate01 = 0.5f;

    // 供外部：设置阶段
    public void SetStage(Stage stage)
    {
        CurrentStage = stage;
        var prof = FindProfile(stage);
        if (prof == null)
        {
            Debug.LogWarning($"[MIDIStageManager] No StageProfile for {stage}.");
            return;
        }

        // 启用/禁用轨道（SonificationMelody 的 OnEnable/OnDisable 会自己淡入淡出）
        for (int i = 0; i < tracks.Length; i++)
        {
            var comp = SafeGet(tracks, i);
            if (comp == null) continue;

            bool shouldEnable = (i < prof.activeMask.Length) ? prof.activeMask[i] : false;

            if (shouldEnable && !comp.enabled)
            {
                comp.enabled = true;

                // 可选：进入阶段时对每个激活轨道应用全局移调
                if (applyStageGlobalTranspose)
                    comp.SetTranspose(prof.globalTransposeSemis);
            }
            else if (!shouldEnable && comp.enabled)
            {
                comp.enabled = false;
            }
        }

        // 切换完立即把当前控制值应用到新激活的轨道，避免状态不同步
        ApplyControls(_lastPitch01, _lastTimbre01, _lastRate01);
    }

    // 供外部：传入手势/事件控制（0..1）
    public void ApplyControls(float pitch01, float timbre01, float rate01)
    {
        _lastPitch01 = Mathf.Clamp01(pitch01);
        _lastTimbre01 = Mathf.Clamp01(timbre01);
        _lastRate01 = Mathf.Clamp01(rate01);

        var prof = FindProfile(CurrentStage);
        if (prof == null) return;

        // 如果只发给首个激活轨道
        if (!broadcastControlsToAllActive && firstActiveOnly)
        {
            int idx = FirstActiveIndex(prof);
            if (idx >= 0) ApplyToOne(idx, prof, _lastPitch01, _lastTimbre01, _lastRate01);
            return;
        }

        // 广播给所有激活轨道
        for (int i = 0; i < tracks.Length; i++)
        {
            bool active = (i < prof.activeMask.Length) ? prof.activeMask[i] : false;
            if (!active) continue;

            ApplyToOne(i, prof, _lastPitch01, _lastTimbre01, _lastRate01);
        }
    }

    // ============ 内部实现 ============

    StageProfile FindProfile(Stage s)
    {
        for (int i = 0; i < stageProfiles.Count; i++)
            if (stageProfiles[i] != null && stageProfiles[i].stage == s)
                return stageProfiles[i];
        return null;
    }

    int FirstActiveIndex(StageProfile prof)
    {
        if (prof == null || prof.activeMask == null) return -1;
        for (int i = 0; i < Mathf.Min(tracks.Length, prof.activeMask.Length); i++)
            if (prof.activeMask[i]) return i;
        return -1;
    }

    void ApplyToOne(int trackIndex, StageProfile prof, float pitch01, float timbre01, float rate01)
    {
        var comp = SafeGet(tracks, trackIndex);
        if (comp == null || !comp.enabled) return;

        // 组合缩放：Stage 全局 * 每轨道
        var st = perTrackScales[trackIndex] ?? new TrackControlScale();
        float p = Mathf.Clamp01(pitch01 * (prof?.globalScales?.pitchScale ?? 1f) * st.pitchScale);
        float t = Mathf.Clamp01(timbre01 * (prof?.globalScales?.timbreScale ?? 1f) * st.timbreScale);
        float r = Mathf.Clamp01(rate01 * (prof?.globalScales?.rateScale ?? 1f) * st.rateScale);

        // 下发到 SonificationMelody 的统一入口
        comp.ApplyControls(p, t, r);
    }

    T SafeGet<T>(T[] arr, int i) where T : class
    {
        if (arr == null || i < 0 || i >= arr.Length) return null;
        return arr[i];
    }

    // ============ 便捷测试 ============

    [ContextMenu("Set Stage: Intro")]
    void _SetIntro() { SetStage(Stage.Intro); }

    [ContextMenu("Set Stage: SpaceFX")]
    void _SetSpaceFX() { SetStage(Stage.SpaceFX); }

    [ContextMenu("Set Stage: Planet1")]
    void _SetPlanet1() { SetStage(Stage.Planet1); }

    [ContextMenu("Set Stage: Planet2")]
    void _SetPlanet2() { SetStage(Stage.Planet2); }

    [ContextMenu("Set Stage: Planet3")]
    void _SetPlanet3() { SetStage(Stage.Planet3); }
}

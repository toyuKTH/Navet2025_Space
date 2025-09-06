using UnityEngine;
using System.Collections;
using System.Linq;
using System.Reflection;

public class testStageControl : MonoBehaviour
{
    [Header("References")]
    public MIDIStageManager manager;   // 若留空，会在场景里自动查找
    [Tooltip("用于最小自检的激活轨道索引（默认用第0轨）")]
    public int activeTrackIndex = 0;

    [Header("Timings")]
    [Tooltip("进入 Intro 阶段后等待的秒数（让你能听到一些音）")]
    public float listenSecondsIntro = 3f;
    [Tooltip("切到静音阶段后等待的秒数（等待淡出完成）")]
    public float listenSecondsMuted = 2f;

    [Header("Controls to Apply during Intro / Live")]
    [Range(0f, 1f)] public float testPitch01 = 0.55f;  // -> PitchBend（0..1 映射到 -1..+1）
    [Range(0f, 1f)] public float testTimbre01 = 0.65f;  // -> CC 74/71/失真
    [Range(0f, 1f)] public float testRate01 = 0.95f;  // -> 触发速度（越大越快）

    [Header("Live Inspector Control")]
    [Tooltip("勾选：在播放时持续把上面三个旋钮值下发到当前阶段（你拖动就能听见变化）")]
    public bool liveFromInspector = true;
    [Tooltip("最大发送频率（Hz），避免过度刷帧")]
    [Range(1f, 60f)] public float liveSendHz = 30f;
    [Tooltip("只有当旋钮变化超过这个阈值时才发送（减少无谓刷新）")]
    [Range(0f, 0.1f)] public float changeThreshold = 0.002f;

    [Header("Pitch - Transpose (optional)")]
    [Tooltip("勾选：将半音移调应用到当前阶段所有激活轨道")]
    public bool liveTranspose = false;
    [Range(-24, 24)] public int transposeSemis = 0;

    [Header("End Behavior")]
    [Tooltip("勾选后：完成自检后保持在 Intro 持续播放；取消勾选：自检后切到静音阶段。")]
    public bool keepPlaying = true;

    // --- 内部缓存（用于判定是否需要重新下发） ---
    float _lp, _lt, _lr;
    float _nextSendTime;
    int _lastTranspose;

    void Reset()
    {
        manager = FindObjectOfType<MIDIStageManager>();
    }

    IEnumerator Start()
    {
        if (manager == null)
        {
            manager = FindObjectOfType<MIDIStageManager>();
            if (manager == null)
            {
                Debug.LogError("[testStageControl] 没找到 MIDIStageManager。把它拖到场景里或拖到脚本引用上。");
                yield break;
            }
        }

        if (manager.tracks == null || manager.tracks.Length == 0 || manager.tracks[activeTrackIndex] == null)
        {
            Debug.LogError("[testStageControl] Manager 的 tracks 没配置好（至少确保第 0 轨存在且已拖入 SonificationMelody）。");
            yield break;
        }

        // 确保被测轨道自动开始
        var testTrack = manager.tracks[activeTrackIndex];
        testTrack.autoStart = true;

        // 1) 自动补最小阶段配置：Intro（只开 activeTrackIndex）、SpaceFX（全关）
        EnsureMinimalProfiles(manager, activeTrackIndex);

        // 2) 等一帧，保证 SonificationMelody.Awake 打开 MIDI 端口
        yield return null;

        // A. 端口就绪检测
        bool portOk = IsMidiPortReady(testTrack);
        if (portOk)
            Debug.Log("[testStageControl] PASS A：检测到 MIDI 输出设备就绪。");
        else
            Debug.LogWarning("[testStageControl] WARN A：未确认 MIDI 输出设备。请检查 loopMIDI/IAC。");

        // B. 切到 Intro 并启动旋律
        manager.SetStage(MIDIStageManager.Stage.Intro);
        yield return new WaitForSeconds(0.25f);
        bool enabledOk = testTrack.enabled;
        bool loopLikelyRunning = IsNoteLoopLikelyRunning(testTrack);

        if (enabledOk && loopLikelyRunning)
            Debug.Log("[testStageControl] PASS B：进入 Intro，轨道已启用且旋律循环已启动。");
        else
            Debug.LogWarning($"[testStageControl] WARN B：进入 Intro 后状态异常。enabled={enabledOk}, loopRunning~={loopLikelyRunning}");

        // C. 初始控制值（也作为 Inspector 实时控制的初始状态）
        manager.ApplyControls(testPitch01, testTimbre01, testRate01);
        _lp = testPitch01; _lt = testTimbre01; _lr = testRate01;
        _lastTranspose = transposeSemis;
        Debug.Log($"[testStageControl] 初始控制下发：pitch01={_lp:F2}, timbre01={_lt:F2}, rate01={_lr:F2}。");
        yield return new WaitForSeconds(listenSecondsIntro);

        // 结束行为
        if (keepPlaying)
        {
            Debug.Log("[testStageControl] 持续播放：保持在 Intro。现在你可以在 Inspector 拖动旋钮实时听变化。");
            yield break;
        }

        manager.SetStage(MIDIStageManager.Stage.SpaceFX);
        yield return new WaitForSeconds(listenSecondsMuted);

        bool disabledOk = !testTrack.enabled;
        if (disabledOk)
            Debug.Log("[testStageControl] PASS D：切到静音阶段后轨道已禁用，预期已淡出并停声。");
        else
            Debug.LogWarning("[testStageControl] WARN D：静音阶段后轨道仍启用。检查 StageProfile。");

        Debug.Log("[testStageControl] 自检完成。");
    }

    void Update()
    {
        if (!Application.isPlaying || manager == null) return;

        // —— 实时从 Inspector 推送三大控制（Pitch/Timbre/Rate）——
        if (liveFromInspector)
        {
            float now = Time.time;
            if (now >= _nextSendTime)
            {
                bool changed =
                    Mathf.Abs(testPitch01 - _lp) > changeThreshold ||
                    Mathf.Abs(testTimbre01 - _lt) > changeThreshold ||
                    Mathf.Abs(testRate01 - _lr) > changeThreshold;

                if (changed)
                {
                    manager.ApplyControls(
                        Mathf.Clamp01(testPitch01),
                        Mathf.Clamp01(testTimbre01),
                        Mathf.Clamp01(testRate01)
                    );
                    _lp = testPitch01; _lt = testTimbre01; _lr = testRate01;
                }
                _nextSendTime = now + 1f / Mathf.Max(1f, liveSendHz);
            }
        }

        // —— 可选：实时移调（半音）作用到当前阶段的激活轨道 —— 
        if (liveTranspose && transposeSemis != _lastTranspose)
        {
            ApplyTransposeToActive(transposeSemis);
            _lastTranspose = transposeSemis;
        }
    }

    // —— Helpers ——

    void EnsureMinimalProfiles(MIDIStageManager mgr, int activeIdx)
    {
        bool hasIntro = mgr.stageProfiles.Any(p => p != null && p.stage == MIDIStageManager.Stage.Intro);
        bool hasSpace = mgr.stageProfiles.Any(p => p != null && p.stage == MIDIStageManager.Stage.SpaceFX);

        int n = Mathf.Max((mgr.tracks?.Length ?? 0), 6);
        bool[] maskAllOff = Enumerable.Repeat(false, n).ToArray();
        bool[] maskOnlyOne = Enumerable.Repeat(false, n).ToArray();
        if (activeIdx >= 0 && activeIdx < n) maskOnlyOne[activeIdx] = true;

        if (!hasIntro)
        {
            var intro = new MIDIStageManager.StageProfile()
            {
                displayName = "Intro (Auto)",
                stage = MIDIStageManager.Stage.Intro,
                activeMask = maskOnlyOne
            };
            mgr.stageProfiles.Add(intro);
            Debug.Log("[testStageControl] 自动添加最小 Intro 配置：只启用第 " + activeIdx + " 轨。");
        }
        if (!hasSpace)
        {
            var space = new MIDIStageManager.StageProfile()
            {
                displayName = "SpaceFX (Muted, Auto)",
                stage = MIDIStageManager.Stage.SpaceFX,
                activeMask = maskAllOff
            };
            mgr.stageProfiles.Add(space);
            Debug.Log("[testStageControl] 自动添加最小 SpaceFX 配置：所有轨道关闭（静音阶段）。");
        }
    }

    bool IsMidiPortReady(SonificationMelody tr)
    {
        if (tr == null) return false;
        var f = typeof(SonificationMelody).GetField("outDev", BindingFlags.Instance | BindingFlags.NonPublic);
        var dev = f?.GetValue(tr);
        return dev != null;
    }

    bool IsNoteLoopLikelyRunning(SonificationMelody tr)
    {
        if (tr == null) return false;
        var f = typeof(SonificationMelody).GetField("noteLoopCo", BindingFlags.Instance | BindingFlags.NonPublic);
        var co = f?.GetValue(tr) as Coroutine;
        return co != null;
    }

    void ApplyTransposeToActive(int semis)
    {
        var prof = manager.stageProfiles.FirstOrDefault(p => p != null && p.stage == manager.CurrentStage);
        if (prof == null || prof.activeMask == null) return;

        for (int i = 0; i < manager.tracks.Length && i < prof.activeMask.Length; i++)
        {
            if (!prof.activeMask[i]) continue;
            var tr = manager.tracks[i];
            if (tr != null && tr.enabled) tr.SetTranspose(semis);
        }
        Debug.Log($"[testStageControl] 已对当前阶段的激活轨道应用移调：{semis} 半音。");
    }
}

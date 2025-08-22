using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class PanelSwitcher : MonoBehaviour
{
    [Header("当前激活面板")]
    public GameObject current;

    [Header("过渡面板与时长")]
    public GameObject transitionPanel;     // 宇航员行走的过渡面板
    public float transitionTime = 3f;      // 过渡时长（秒）
    public bool useTransition = true;      // 是否启用过渡

    [Header("Canvas 相机修正")]
    public bool forceCanvasUseMainCamera = true;

    private readonly Stack<GameObject> history = new Stack<GameObject>();
    private bool busy = false;

    void Awake()
    {
        if (transitionPanel != null) transitionPanel.SetActive(false);
        if (current != null) Activate(current);
    }

    /// from -> 过渡 -> to
    public void SwitchPanel(GameObject from, GameObject to)
    {
        if (busy) return;
        StartCoroutine(DoSwitch(from, to, pushHistory: true));
    }

    /// current -> to
    public void SwitchTo(GameObject to)
    {
        if (busy) return;
        StartCoroutine(DoSwitch(current, to, pushHistory: true));
    }

    /// 返回上一个
    public void Back()
    {
        if (busy) return;
        if (history.Count == 0) return;
        var prev = history.Pop();
        StartCoroutine(DoSwitch(current, prev, pushHistory: false));
    }

    private IEnumerator DoSwitch(GameObject from, GameObject to, bool pushHistory)
    {
        busy = true;

        if (to == null)
        {
            Debug.LogWarning("[PanelSwitcher] 目标面板为空，忽略切换。");
            busy = false;
            yield break;
        }

        // 入栈并关掉 from
        if (from != null)
        {
            if (pushHistory) history.Push(from);
            from.SetActive(false);
        }

        // 过渡面板 + 宇航员行走
        if (useTransition && transitionPanel != null)
        {
            transitionPanel.SetActive(true);

            // 每次启用 transitionPanel 时，自动触发行走
            var walker = transitionPanel.GetComponentInChildren<AstronautPlayer.AstronautAutoWalker>();
            if (walker != null)
            {
                walker.StartWalking();
            }

            yield return new WaitForSeconds(Mathf.Max(0f, transitionTime));
            transitionPanel.SetActive(false);
        }

        // 打开目标面板
        Activate(to);
        busy = false;
    }

    private void Activate(GameObject panel)
    {
        panel.SetActive(true);
        current = panel;

        if (forceCanvasUseMainCamera)
        {
            var canvas = panel.GetComponentInChildren<Canvas>(true);
            if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == null)
            {
                canvas.worldCamera = Camera.main;
            }
        }
    }
}

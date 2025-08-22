using UnityEngine;
using UnityEngine.UI;

public class ProgressBarAuto : MonoBehaviour
{
    public Slider progressBar;   // 拖你的 Slider
    public float duration = 3f;  // 进度条填满所需时间（秒）

    private float timer = 0f;
    private bool running = false;

    void OnEnable()
    {
        // 每次启用时重置
        timer = 0f;
        if (progressBar != null)
            progressBar.value = 0f;
        running = true;
    }

    void Update()
    {
        if (!running || progressBar == null) return;

        timer += Time.deltaTime;
        float progress = Mathf.Clamp01(timer / duration);
        progressBar.value = progress;

        if (progress >= 1f)
        {
            running = false;
            Debug.Log("✅ 进度条填满！");
            // TODO: 这里可以加事件，例如切换到下一个画面
        }
    }
}

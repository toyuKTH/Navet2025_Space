using UnityEngine;

// 保证本脚本在最后执行，晚于 Mediapipe 的 Annotation 代码
[DefaultExecutionOrder(10000)]
public class AlwaysHideAnnotations : MonoBehaviour
{
    [Tooltip("不指定则以当前物体为根")]
    public Transform root;

    void Awake()
    {
        if (root == null) root = transform;
    }

    void LateUpdate()
    {
        // 关闭所有渲染器（LineRenderer 也是 Renderer 的子类）
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i].enabled) renderers[i].enabled = false;

        // 如果它把材质颜色改回来了，顺便把透明度拉到 0（可选）
        for (int i = 0; i < renderers.Length; i++)
        {
            var mat = renderers[i].material; // 实例材质，避免改到共享材质
            if (mat != null && mat.HasProperty("_Color"))
            {
                var c = mat.color;
                if (c.a != 0f) { c.a = 0f; mat.color = c; }
            }
        }
    }
}

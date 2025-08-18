using UnityEngine;
// 起别名避免和 UnityEngine.Screen 混淆
using MP = Mediapipe.Unity;

public class BindScreenTextureToMaterial : MonoBehaviour
{
    public Renderer targetRenderer;

    private void Reset()
    {
        targetRenderer = GetComponent<Renderer>();
    }

    private System.Collections.IEnumerator Start()
    {
        if (!targetRenderer) yield break;

        // 等到场景里有 MP.Screen 且它已经拿到纹理
        MP.Screen src = null;
        while ((src = Object.FindObjectOfType<MP.Screen>(true)) == null || src.texture == null)
            yield return null;

        // 绑定到材质
        targetRenderer.material.mainTexture = src.texture;
        // 如果你用共享材质：targetRenderer.sharedMaterial.mainTexture = src.texture;
    }
}

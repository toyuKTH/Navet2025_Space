using UnityEngine;
using Mediapipe;

public class AstronautBodyTracker : MonoBehaviour
{
    [Header("调试选项")]
    [SerializeField] private bool showDebugInfo = true;

    // 用于存储最新姿态
    private Vector3[] currentPose;

    /// <summary>
    /// 更新 Mediapipe 传过来的 pose 数据
    /// </summary>
    public void UpdatePoseData(NormalizedLandmarkList poseData)
    {
        if (poseData == null || poseData.Landmark == null) return;

        int count = poseData.Landmark.Count;
        currentPose = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            var lm = poseData.Landmark[i];
            currentPose[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }

        if (showDebugInfo)
            Debug.Log($"AstronautBodyTracker 接收到 {count} 个关键点");
    }

    /// <summary>
    /// 更新测试数据（Vector3[]）
    /// </summary>
    public void UpdatePoseData(Vector3[] poseData)
    {
        if (poseData == null) return;
        currentPose = poseData;

        if (showDebugInfo)
            Debug.Log($"AstronautBodyTracker 接收到测试数据: {poseData.Length} 个关键点");
    }

    /// <summary>
    /// 对外提供获取最新 pose
    /// </summary>
    public Vector3[] GetCurrentPose()
    {
        return currentPose;
    }
}

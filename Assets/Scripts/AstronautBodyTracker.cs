using UnityEngine;
using UnityEngine.UI; // 添加UI引用
using Mediapipe.Unity; // MediaPipe引用

public class PoseBridgeConnector : MonoBehaviour
{
    [Header("连接设置")]
    [SerializeField] private AstronautBodyTracker astronautTracker;
    
    [Header("数据监控")]
    [SerializeField] private bool enableDataMonitoring = true;
    [SerializeField] private bool showDebugInfo = true;
    
    private bool isConnected = false;

    void Start()
    {
        SetupConnection();
    }

    void SetupConnection()
    {
        // 查找AstronautBodyTracker
        if (astronautTracker == null)
        {
            astronautTracker = FindObjectOfType<AstronautBodyTracker>();
        }
        
        if (astronautTracker != null)
        {
            Debug.Log("✓ 桥接器找到宇航服追踪器");
            isConnected = true;
        }
        else
        {
            Debug.LogWarning("✗ 未找到宇航服追踪器");
        }
    }

    // 这个方法将被MediaPipe调用
    public void ReceivePoseData(NormalizedLandmarkList poseData)
    {
        if (!enableDataMonitoring || !isConnected) return;
        
        if (astronautTracker != null && poseData != null)
        {
            astronautTracker.UpdatePoseData(poseData);
            
            if (showDebugInfo)
            {
                Debug.Log($"传递pose数据: {poseData.Landmark?.Count ?? 0} 个关键点");
            }
        }
    }

    // 测试方法
    [ContextMenu("测试数据传输")]
    public void TestDataTransfer()
    {
        if (astronautTracker == null)
        {
            Debug.LogError("AstronautBodyTracker未找到！");
            return;
        }
        
        // 创建测试数据
        Vector3[] testData = new Vector3[33];
        
        // 设置关键身体点
        testData[11] = new Vector3(0.3f, 0.6f, 0); // LEFT_SHOULDER
        testData[12] = new Vector3(0.7f, 0.6f, 0); // RIGHT_SHOULDER
        testData[23] = new Vector3(0.35f, 0.3f, 0); // LEFT_HIP
        testData[24] = new Vector3(0.65f, 0.3f, 0); // RIGHT_HIP
        testData[0] = new Vector3(0.5f, 0.8f, 0);   // NOSE
        
        astronautTracker.UpdatePoseData(testData);
        Debug.Log("✓ 测试数据已发送");
    }
    
    [ContextMenu("重新连接")]
    public void ReconnectTracker()
    {
        SetupConnection();
    }
}
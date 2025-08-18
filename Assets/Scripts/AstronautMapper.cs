using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Color = UnityEngine.Color;
using Rect = UnityEngine.Rect;

public class SafeAstronautMapper : MonoBehaviour
{
    [Header("Astronaut bones")]
    public Transform hips;          // 模型根（髋）
    public Transform spineOrChest;  // 胸/上身骨
    public Transform head;          // 头
    public Transform leftUpperArm;  // Arm_1_Left
    public Transform leftLowerArm;  // Arm_2_Left
    public Transform rightUpperArm; // Arm_1_Right
    public Transform rightLowerArm; // Arm_2_Right

    [Header("Safety Settings")]
    public bool enablePositionTracking = false; // 先禁用位置追踪，只做旋转
    public bool enableRotationTracking = true;
    public float maxMovementSpeed = 2f; // 限制移动速度
    
    [Header("Tuning")]
    public float metersToUnity = 1.0f;
    public float posSmooth = 8f;
    public float rotSmooth = 12f;
    public bool mirrorHorizontally = true;
    
    [Header("Debug")]
    public bool showDebugInfo = true;
    public bool showGizmos = true;

    private HolisticTrackingGraph graph;
    private Vector3[] worldLandmarks = new Vector3[33];
    private bool hasValidData = false;
    
    // 记录初始状态
    private Vector3 initialPosition;
    private Vector3 lastValidPosition;
    private bool isInitialized = false;
    
    // MediaPipe关键点索引
    const int NOSE = 0, L_EAR = 7, R_EAR = 8;
    const int L_SHO = 11, R_SHO = 12, L_ELB = 13, R_ELB = 14, L_WRI = 15, R_WRI = 16;
    const int L_HIP = 23, R_HIP = 24;

    void Start()
    {
        // 记录初始位置
        if (hips != null)
        {
            initialPosition = hips.position;
            lastValidPosition = initialPosition;
        }
        
        // 禁用可能的物理组件
        DisablePhysics();
        
        // 连接MediaPipe
        ConnectToMediaPipe();
        
        if (showDebugInfo)
            Debug.Log($"SafeAstronautMapper启动 - 初始位置: {initialPosition}");
    }

    void DisablePhysics()
    {
        // 禁用所有可能导致掉落的组件
        var rigidbody = GetComponent<Rigidbody>();
        if (rigidbody) rigidbody.isKinematic = true;
        
        var collider = GetComponent<Collider>();
        if (collider) collider.enabled = false;
        
        var animator = GetComponent<Animator>();
        if (animator && animator.enabled)
        {
            animator.enabled = false;
            if (showDebugInfo) Debug.Log("已禁用Animator组件");
        }
    }

    void ConnectToMediaPipe()
    {
        var solution = FindObjectOfType<HolisticTrackingSolution>(true);
        if (!solution)
        {
            Debug.LogError("找不到HolisticTrackingSolution");
            return;
        }

        var field = typeof(HolisticTrackingSolution)
            .GetField("graphRunner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        graph = field?.GetValue(solution) as HolisticTrackingGraph;
        
        if (graph != null)
        {
            graph.OnPoseWorldLandmarksOutput += OnPoseWorldLandmarks;
            if (showDebugInfo) Debug.Log("成功连接MediaPipe");
        }
        else
        {
            Debug.LogError("无法连接MediaPipe图形");
        }
    }

    void OnDestroy()
    {
        if (graph != null)
            graph.OnPoseWorldLandmarksOutput -= OnPoseWorldLandmarks;
    }

    void OnPoseWorldLandmarks(object sender, OutputStream<LandmarkList>.OutputEventArgs e)
    {
        var landmarkList = e.packet.Get(LandmarkList.Parser);
        if (landmarkList == null || landmarkList.Landmark.Count < 33)
        {
            hasValidData = false;
            return;
        }

        // 安全地转换坐标
        bool dataValid = true;
        for (int i = 0; i < 33; i++)
        {
            var lm = landmarkList.Landmark[i];
            
            // 检查数据是否有效
            if (float.IsNaN(lm.X) || float.IsNaN(lm.Y) || float.IsNaN(lm.Z) ||
                float.IsInfinity(lm.X) || float.IsInfinity(lm.Y) || float.IsInfinity(lm.Z))
            {
                dataValid = false;
                break;
            }
            
            // 坐标转换：MediaPipe世界坐标 -> Unity
            float x = lm.X;
            float y = lm.Y; 
            float z = -lm.Z; // Z轴翻转
            
            if (mirrorHorizontally) x = -x;
            
            worldLandmarks[i] = new Vector3(x, y, z) * metersToUnity;
        }
        
        hasValidData = dataValid;
        
        if (!isInitialized && hasValidData)
        {
            InitializeFromFirstFrame();
        }
    }

    void InitializeFromFirstFrame()
    {
        // 使用第一帧数据来设置偏移
        Vector3 mpHipCenter = (worldLandmarks[L_HIP] + worldLandmarks[R_HIP]) * 0.5f;
        
        if (hips != null)
        {
            // 计算偏移，让模型保持在原位
            Vector3 offset = initialPosition - mpHipCenter;
            
            // 应用偏移到所有关键点
            for (int i = 0; i < worldLandmarks.Length; i++)
            {
                worldLandmarks[i] += offset;
            }
        }
        
        isInitialized = true;
        if (showDebugInfo) Debug.Log("已初始化姿态追踪偏移");
    }

    void LateUpdate()
    {
        if (!hasValidData || !isInitialized) return;
        
        UpdatePoseSafely();
    }

    void UpdatePoseSafely()
    {
        // 1. 安全更新髋部位置
        if (enablePositionTracking && hips != null)
        {
            UpdateHipPositionSafely();
        }
        
        // 2. 更新旋转（相对安全）
        if (enableRotationTracking)
        {
            UpdateBodyOrientation();
            UpdateArms();
        }
    }

    void UpdateHipPositionSafely()
    {
        Vector3 leftHip = worldLandmarks[L_HIP];
        Vector3 rightHip = worldLandmarks[R_HIP];
        Vector3 targetPosition = (leftHip + rightHip) * 0.5f;
        
        // 安全检查：限制移动距离
        float distance = Vector3.Distance(lastValidPosition, targetPosition);
        if (distance > maxMovementSpeed * Time.deltaTime)
        {
            // 距离太大，可能是数据错误，使用限制后的位置
            Vector3 direction = (targetPosition - lastValidPosition).normalized;
            targetPosition = lastValidPosition + direction * maxMovementSpeed * Time.deltaTime;
        }
        
        // 额外安全检查：不要离初始位置太远
        if (Vector3.Distance(initialPosition, targetPosition) > 5f)
        {
            if (showDebugInfo) Debug.LogWarning("目标位置距离初始位置太远，跳过此帧");
            return;
        }
        
        hips.position = Vector3.Lerp(hips.position, targetPosition, Time.deltaTime * posSmooth);
        lastValidPosition = hips.position;
    }

    void UpdateBodyOrientation()
    {
        if (!spineOrChest) return;
        
        Vector3 leftShoulder = worldLandmarks[L_SHO];
        Vector3 rightShoulder = worldLandmarks[R_SHO];
        
        Vector3 shoulderDirection = (rightShoulder - leftShoulder).normalized;
        Vector3 forward = Vector3.Cross(shoulderDirection, Vector3.up).normalized;
        
        if (forward.sqrMagnitude > 0.01f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(forward, Vector3.up);
            spineOrChest.rotation = Quaternion.Slerp(spineOrChest.rotation, targetRotation, 
                                                   Time.deltaTime * rotSmooth);
        }
    }

    void UpdateArms()
    {
        // 左臂
        UpdateArmSafely(leftUpperArm, leftLowerArm, 
                       worldLandmarks[L_SHO], worldLandmarks[L_ELB], worldLandmarks[L_WRI]);
        
        // 右臂
        UpdateArmSafely(rightUpperArm, rightLowerArm,
                       worldLandmarks[R_SHO], worldLandmarks[R_ELB], worldLandmarks[R_WRI]);
    }

    void UpdateArmSafely(Transform upperArm, Transform lowerArm, 
                        Vector3 shoulderPos, Vector3 elbowPos, Vector3 wristPos)
    {
        if (!upperArm || !lowerArm) return;
        
        // 上臂方向
        Vector3 upperArmDir = (elbowPos - shoulderPos).normalized;
        if (upperArmDir.sqrMagnitude > 0.01f)
        {
            Quaternion upperTarget = Quaternion.LookRotation(upperArmDir, Vector3.up);
            upperArm.rotation = Quaternion.Slerp(upperArm.rotation, upperTarget, 
                                               Time.deltaTime * rotSmooth);
        }
        
        // 前臂方向
        Vector3 lowerArmDir = (wristPos - elbowPos).normalized;
        if (lowerArmDir.sqrMagnitude > 0.01f)
        {
            Quaternion lowerTarget = Quaternion.LookRotation(lowerArmDir, Vector3.up);
            lowerArm.rotation = Quaternion.Slerp(lowerArm.rotation, lowerTarget, 
                                               Time.deltaTime * rotSmooth);
        }
    }

    // 调试信息
    void OnGUI()
    {
        if (!showDebugInfo) return;
        
        GUILayout.BeginArea(new Rect(10, 10, 300, 200));
        GUILayout.Label($"MediaPipe连接: {(graph != null ? "成功" : "失败")}");
        GUILayout.Label($"数据有效: {hasValidData}");
        GUILayout.Label($"已初始化: {isInitialized}");
        if (hips != null)
        {
            GUILayout.Label($"当前位置: {hips.position:F2}");
            GUILayout.Label($"初始位置: {initialPosition:F2}");
        }
        GUILayout.EndArea();
    }

    // Gizmos绘制
    void OnDrawGizmos()
    {
        if (!showGizmos || !hasValidData) return;
        
        // 绘制关键点
        Gizmos.color = Color.red;
        int[] keyPoints = { L_SHO, R_SHO, L_ELB, R_ELB, L_WRI, R_WRI, L_HIP, R_HIP };
        
        foreach (int i in keyPoints)
        {
            if (i < worldLandmarks.Length)
            {
                Gizmos.DrawSphere(worldLandmarks[i], 0.02f);
            }
        }
        
        // 绘制骨骼连接
        Gizmos.color = Color.blue;
        DrawLine(L_SHO, L_ELB);
        DrawLine(L_ELB, L_WRI);
        DrawLine(R_SHO, R_ELB);
        DrawLine(R_ELB, R_WRI);
        
        // 绘制初始位置参考
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(initialPosition, Vector3.one * 0.1f);
    }

    void DrawLine(int from, int to)
    {
        if (from < worldLandmarks.Length && to < worldLandmarks.Length)
        {
            Gizmos.DrawLine(worldLandmarks[from], worldLandmarks[to]);
        }
    }
}
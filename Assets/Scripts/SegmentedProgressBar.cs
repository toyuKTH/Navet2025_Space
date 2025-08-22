using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public class FlexibleProgressBar : MonoBehaviour
{
    [System.Serializable]
    public class ProgressSection
    {
        [Range(0f, 1f)]
        public float initialRatio = 0.25f;
        public Color color = Color.white;
        [HideInInspector]
        public float currentRatio;
        [HideInInspector]
        public Image imageComponent;
    }

    [Header("进度条设置")]
    public List<ProgressSection> sections = new List<ProgressSection>(4);
    
    [Header("尺寸设置")]
    public float progressBarWidth = 400f;
    public float progressBarHeight = 50f;
    
    [Header("间隔设置")]
    public float sectionSpacing = 2f;
    
    [Header("第一部分增长设置")]
    public float stepGrowthAmount = 0.05f; // 每次按键增长的量
    public float maxFirstSectionRatio = 0.8f;
    
    [Header("按键设置")]
    public KeyCode growthKey = KeyCode.Space;
    
    private RectTransform progressBarRect;
    
    void Start()
    {
        Debug.Log("ProgressBar Start() called");
        InitializeProgressBar();
        Debug.Log("ProgressBar initialized with " + sections.Count + " sections");
    }
    
    void Update()
    {
        HandleInput();
    }
    
    void InitializeProgressBar()
    {
        // 确保有4个部分
        while (sections.Count < 4)
        {
            sections.Add(new ProgressSection());
        }
        
        // 标准化初始比例，确保总和为1
        NormalizeRatios();
        
        // 设置进度条容器
        SetupProgressBarContainer();
        
        // 创建各个部分的UI
        CreateSectionImages();
        
        // 初始化当前比例
        for (int i = 0; i < sections.Count; i++)
        {
            sections[i].currentRatio = sections[i].initialRatio;
        }
        
        // 更新UI显示
        UpdateVisuals();
    }
    
    void SetupProgressBarContainer()
    {
        progressBarRect = GetComponent<RectTransform>();
        if (progressBarRect == null)
        {
            progressBarRect = gameObject.AddComponent<RectTransform>();
        }
        
        progressBarRect.sizeDelta = new Vector2(progressBarWidth, progressBarHeight);
    }
    
    void CreateSectionImages()
    {
        // 清除现有的子对象
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            if (Application.isPlaying)
                Destroy(transform.GetChild(i).gameObject);
            else
                DestroyImmediate(transform.GetChild(i).gameObject);
        }
        
        // 为每个部分创建Image组件
        for (int i = 0; i < sections.Count; i++)
        {
            GameObject sectionObj = new GameObject($"Section_{i + 1}");
            sectionObj.transform.SetParent(transform, false);
            
            // 添加Image组件
            Image img = sectionObj.AddComponent<Image>();
            img.color = sections[i].color;
            sections[i].imageComponent = img;
            
            // 设置RectTransform
            RectTransform sectionRect = sectionObj.GetComponent<RectTransform>();
            sectionRect.anchorMin = new Vector2(0, 0);
            sectionRect.anchorMax = new Vector2(0, 1);
            sectionRect.pivot = new Vector2(0, 0.5f);
            sectionRect.anchoredPosition = Vector2.zero;
            
            Debug.Log($"Created Section_{i + 1} with color {sections[i].color}");
        }
    }
    
    void NormalizeRatios()
    {
        float total = 0f;
        foreach (var section in sections)
        {
            total += section.initialRatio;
        }
        
        if (total > 0)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                sections[i].initialRatio /= total;
            }
        }
        else
        {
            // 如果总和为0，平均分配
            float averageRatio = 1f / sections.Count;
            for (int i = 0; i < sections.Count; i++)
            {
                sections[i].initialRatio = averageRatio;
            }
        }
    }
    
    void HandleInput()
    {
        if (Input.GetKeyDown(growthKey))
        {
            GrowFirstSectionOneStep();
        }
    }
    
    void GrowFirstSectionOneStep()
    {
        if (sections.Count == 0) return;
        
        // 计算第一部分可以增长的最大值
        float currentFirstRatio = sections[0].currentRatio;
        if (currentFirstRatio >= maxFirstSectionRatio) 
        {
            Debug.Log("第一部分已达到最大占比：" + maxFirstSectionRatio);
            return;
        }
        
        // 计算本次增长量
        float growthAmount = stepGrowthAmount;
        float newFirstRatio = Mathf.Min(currentFirstRatio + growthAmount, maxFirstSectionRatio);
        float actualGrowthAmount = newFirstRatio - currentFirstRatio;
        
        // 计算其他部分的总比例
        float otherSectionsTotal = 0f;
        for (int i = 1; i < sections.Count; i++)
        {
            otherSectionsTotal += sections[i].currentRatio;
        }
        
        if (otherSectionsTotal > 0)
        {
            // 计算压缩比例
            float compressionRatio = (otherSectionsTotal - actualGrowthAmount) / otherSectionsTotal;
            
            // 应用变化
            sections[0].currentRatio = newFirstRatio;
            
            // 等比压缩其他部分
            for (int i = 1; i < sections.Count; i++)
            {
                sections[i].currentRatio *= compressionRatio;
            }
            
            Debug.Log($"第一部分增长到：{newFirstRatio:F3}, 其他部分总占比：{(1 - newFirstRatio):F3}");
        }
        
        UpdateVisuals();
    }
    
    void UpdateVisuals()
    {
        float currentX = 0f;
        float totalWidth = progressBarWidth - (sections.Count - 1) * sectionSpacing;
        
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].imageComponent != null)
            {
                float sectionWidth = totalWidth * sections[i].currentRatio;
                
                RectTransform sectionRect = sections[i].imageComponent.rectTransform;
                sectionRect.anchoredPosition = new Vector2(currentX, 0);
                sectionRect.sizeDelta = new Vector2(sectionWidth, progressBarHeight);
                
                // 更新颜色
                sections[i].imageComponent.color = sections[i].color;
                
                currentX += sectionWidth + sectionSpacing;
            }
        }
    }
    
    // 重置进度条到初始状态
    [ContextMenu("重置进度条")]
    public void ResetProgressBar()
    {
        for (int i = 0; i < sections.Count; i++)
        {
            sections[i].currentRatio = sections[i].initialRatio;
        }
        UpdateVisuals();
    }
    
    // 手动设置第一部分的比例
    public void SetFirstSectionRatio(float ratio)
    {
        ratio = Mathf.Clamp01(ratio);
        
        if (sections.Count == 0) return;
        
        float otherSectionsTotal = 0f;
        for (int i = 1; i < sections.Count; i++)
        {
            otherSectionsTotal += sections[i].currentRatio;
        }
        
        float remainingRatio = 1f - ratio;
        if (otherSectionsTotal > 0 && remainingRatio > 0)
        {
            float compressionRatio = remainingRatio / otherSectionsTotal;
            
            sections[0].currentRatio = ratio;
            for (int i = 1; i < sections.Count; i++)
            {
                sections[i].currentRatio *= compressionRatio;
            }
        }
        else if (remainingRatio <= 0)
        {
            sections[0].currentRatio = ratio;
            float averageOtherRatio = (1f - ratio) / (sections.Count - 1);
            for (int i = 1; i < sections.Count; i++)
            {
                sections[i].currentRatio = averageOtherRatio;
            }
        }
        
        UpdateVisuals();
    }
    
    // 获取指定部分的当前比例
    public float GetSectionRatio(int index)
    {
        if (index >= 0 && index < sections.Count)
        {
            return sections[index].currentRatio;
        }
        return 0f;
    }
    
    // 在编辑器中预览变化
    void OnValidate()
    {
        if (sections.Count < 4)
        {
            while (sections.Count < 4)
            {
                sections.Add(new ProgressSection());
            }
        }
        
        if (Application.isPlaying)
        {
            NormalizeRatios();
            for (int i = 0; i < sections.Count; i++)
            {
                sections[i].currentRatio = sections[i].initialRatio;
            }
            UpdateVisuals();
        }
    }
}
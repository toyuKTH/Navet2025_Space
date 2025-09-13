using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Mediapipe.Unity
{
  public class Screen : MonoBehaviour
  {
    [SerializeField] private RawImage _screen;   // 在 Inspector 里拖你的 RawImage
    private ImageSource _imageSource;
    private string _overlay = "";
    private bool _listedCams = false;

    public Texture texture
    {
      get => _screen != null ? _screen.texture : null;
      set { if (_screen != null) _screen.texture = value; }
    }

    public UnityEngine.Rect uvRect
    {
      set { if (_screen != null) _screen.uvRect = value; }
    }

    public void Initialize(ImageSource imageSource)
    {
      _imageSource = imageSource;

      if (_screen == null)
      {
        Debug.LogError("[Screen] 请把 RawImage 赋给 _screen。");
        return;
      }

      // 设备列表（只列一次）
      if (!_listedCams)
      {
        var devices = WebCamTexture.devices;
        for (int i = 0; i < devices.Length; i++)
          Debug.Log($"[Screen] Cam[{i}] name=\"{devices[i].name}\", front={devices[i].isFrontFacing}");
        _listedCams = true;
      }

      Resize(_imageSource.textureWidth, _imageSource.textureHeight);
      Rotate(_imageSource.rotation.Reverse());
      ResetUvRect(RunningMode.Async);

      texture = _imageSource.GetCurrentTexture();   // 通常是 WebCamTexture
      UpdateOverlay("Initialize (Async)");
    }

    public void Resize(int width, int height)
    {
      if (_screen == null) return;
      _screen.rectTransform.sizeDelta = new Vector2(width, height);
    }

    public void Rotate(RotationAngle rotationAngle)
    {
      if (_screen == null) return;
      _screen.rectTransform.localEulerAngles = rotationAngle.GetEulerAngles();
    }

    // Sync 模式下被调用：把帧数据拷进运行时 Texture2D
    public void ReadSync(Experimental.TextureFrame textureFrame)
    {
      if (_screen == null) return;

      if (!(texture is Texture2D))
      {
        texture = new Texture2D(_imageSource.textureWidth, _imageSource.textureHeight, TextureFormat.RGBA32, false);
        ResetUvRect(RunningMode.Sync);
        UpdateOverlay("ReadSync (create Texture2D)");
      }

      textureFrame.CopyTexture(texture);
    }

    private void ResetUvRect(RunningMode runningMode)
    {
      var rect = new UnityEngine.Rect(0, 0, 1, 1);

      if (_imageSource.isVerticallyFlipped && runningMode == RunningMode.Async)
      {
        rect = FlipVertically(rect);
      }

      if (_imageSource.isFrontFacing)
      {
        var rotation = _imageSource.rotation;
        rect = (rotation == RotationAngle.Rotation0 || rotation == RotationAngle.Rotation180)
          ? FlipHorizontally(rect)
          : FlipVertically(rect);
      }

      uvRect = rect;
    }

    private UnityEngine.Rect FlipHorizontally(UnityEngine.Rect rect)
    {
      return new UnityEngine.Rect(1 - rect.x, rect.y, -rect.width, rect.height);
    }

    private UnityEngine.Rect FlipVertically(UnityEngine.Rect rect)
    {
      return new UnityEngine.Rect(rect.x, 1 - rect.y, rect.width, -rect.height);
    }

    // —— 叠层信息 —— //
    private void Update()
    {
      if (_imageSource != null) UpdateOverlay("Update");
    }

    private void UpdateOverlay(string when)
    {
      // 取当前纹理
      var tex = texture;
      // 尝试从纹理/源拿 WebCamTexture -> 设备名
      string deviceName = null;
      WebCamTexture wct = tex as WebCamTexture;
      if (wct == null && _imageSource != null)
        wct = _imageSource.GetCurrentTexture() as WebCamTexture;
      if (wct != null) deviceName = wct.deviceName;

      // 兜底：反射从 WebCamSource 里取可能存在的字段
      if (string.IsNullOrEmpty(deviceName) && _imageSource != null)
      {
        var wcs = _imageSource as WebCamSource;
        if (wcs != null)
        {
          FieldInfo f =
            wcs.GetType().GetField("deviceName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
            wcs.GetType().GetField("DeviceName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
            wcs.GetType().GetField("requestedDeviceName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
          if (f != null) deviceName = f.GetValue(wcs) as string;
        }
      }

      string texName = tex != null ? tex.name : "null";
      string texSize = (tex is Texture2D t2d) ? $"{t2d.width}x{t2d.height}" :
                       (tex != null ? $"{_imageSource.textureWidth}x{_imageSource.textureHeight}" : "N/A");

      _overlay =
        $"{when}\n" +
        $"source={_imageSource?.GetType().Name}\n" +
        $"device={(string.IsNullOrEmpty(deviceName) ? "(unknown)" : deviceName)}\n" +
        $"frontFacing={_imageSource?.isFrontFacing}\n" +
        $"rotation={_imageSource?.rotation}\n" +
        $"flippedY={_imageSource?.isVerticallyFlipped}\n" +
        $"texture={texName}\n" +
        $"size={texSize}";
    }

    private void OnGUI()
    {
      if (string.IsNullOrEmpty(_overlay)) return;
      GUI.color = UnityEngine.Color.yellow;
      GUI.Label(new UnityEngine.Rect(10, 10, 900, 260), _overlay);
    }
  }
}

using UnityEngine;
using UnityEngine.UI;

namespace Mediapipe.Unity
{
  public class Screen : MonoBehaviour
  {
    [SerializeField] private RawImage _screen;   // Inspector 里拖入 RawImage
    private ImageSource _imageSource;

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

      Resize(_imageSource.textureWidth, _imageSource.textureHeight);
      Rotate(_imageSource.rotation.Reverse());
      ResetUvRect(RunningMode.Async);

      texture = _imageSource.GetCurrentTexture();   // 通常是 WebCamTexture
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

    // Sync 模式：把帧数据拷进运行时 Texture2D
    public void ReadSync(Experimental.TextureFrame textureFrame)
    {
      if (_screen == null) return;

      if (!(texture is Texture2D))
      {
        texture = new Texture2D(_imageSource.textureWidth, _imageSource.textureHeight, TextureFormat.RGBA32, false);
        ResetUvRect(RunningMode.Sync);
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
  }
}

using UnityEngine;
using UnityEngine.UI;

public class CameraDisplay : MonoBehaviour
{
    public RawImage cameraView;

    private WebCamTexture webcamTexture;

    void Start()
    {
        webcamTexture = new WebCamTexture();
        cameraView.texture = webcamTexture;
        cameraView.material.mainTexture = webcamTexture;
        webcamTexture.Play();
    }
}

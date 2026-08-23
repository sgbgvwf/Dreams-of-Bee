using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 遮罩相机同步脚本。
/// 挂在主相机上，把主相机的位置/旋转/FOV/近远裁剪面/宽高比同步给遮罩相机。
/// 遮罩相机与主相机投影完全一致，深度单位与比较才成立。
/// </summary>
public class CameraSync : MonoBehaviour
{
    private Camera mainCamera;
    public Camera cameraToSync;   // 遮罩相机（跟随主相机）

    void Awake()
    {
        mainCamera = GetComponent<Camera>();
    }

    void Update()
    {
        if (mainCamera == null || cameraToSync == null)
            return;

        cameraToSync.transform.SetPositionAndRotation(
            mainCamera.transform.position, mainCamera.transform.rotation);
        cameraToSync.fieldOfView = mainCamera.fieldOfView;
        cameraToSync.nearClipPlane = mainCamera.nearClipPlane;
        cameraToSync.farClipPlane = mainCamera.farClipPlane;
        cameraToSync.aspect = mainCamera.aspect;
    }
}

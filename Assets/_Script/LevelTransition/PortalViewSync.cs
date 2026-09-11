using UnityEngine;

/// <summary>
/// 门后渲染相机位姿同步 —— 挂在【目标关】场景的门后相机上。
///
/// 是什么:读 PortalPoseSO 总线(出口门侧 PortalDoor 每帧写),按
/// 『相机相对目标关门洞 = 玩家相机相对本关门洞』摆自己:
///   位置 = 目标门 .TransformPoint(本关门 .InverseTransformPoint(玩家));
///   朝向 = 目标门.rotation * Inverse(本关门.rotation) * 玩家相机朝向。
/// 与传送公式同一套相对位姿 —— 门面上看到的 = 玩家站到门洞前会看到的目标关画面。
/// 同时把本相机的 FOV / 宽高比同步成玩家相机的(见下条),让门面材质能直接按玩家屏幕坐标取画面。
///
/// 放哪:目标关场景里的门后相机上(相机位置随意,位姿每帧被本组件覆盖)。
///
/// 绑什么:相机先配好三样 —— Target Texture 指向你的 RenderTexture(与门面平面共用同一张)、
/// 挂 HDAdditionalCameraData(HDRP 必需)、Culling Mask 建议剔除 Portal 与 Player 层;
/// 再把 PortalPoseSO 资产拖进本组件的 Pose 槽(不拖则自动加载)。
/// 玩家视角相机须标 MainCamera(Camera.main 取它)—— 投影同步要用。
///
/// 投影同步:门面材质(Assets/Shader/Portal/PortalScreen.shader)是拿『本像素在玩家屏幕上的位置』
/// 去采 RT 的 —— 这要求本相机与玩家相机投影一致:不一致门面整体错位;一致还顺带保证
/// uv 恒在 [0,1] 内(贴近门洞 / 斜视时不拉花)。故每帧抄 FOV + 宽高比;近/远裁剪面不参与该映射,
/// 保持本相机自己的值(近裁剪面还能挡住门洞另一侧的几何)。开关见 matchPlayerProjection。
///
/// 生命周期:总线写入中(门面工作期)→ 启用本相机渲染;总线停写/穿过/未过渡 → 自动停渲染省 GPU。
/// 门面平面(贴同一张 RT)是作者自己的场景资产,本组件不碰。
/// </summary>
public class PortalViewSync : MonoBehaviour
{
    [SerializeField, Tooltip("可选的 PortalPoseSO 资产引用(场景持有 = 引擎级强引用,防卸载关卡时被 Resources.UnloadUnusedAssets 卸掉;空 = 从 Resources 惰性加载)")]
    private PortalPoseSO pose;

    [SerializeField, Tooltip("同步延迟容差(秒):总线停止写入超过此时长就认为门面工作期结束,停用相机。")]
    private float staleTimeout = 0.2f;

    [SerializeField, Tooltip("把本相机的 FOV / 宽高比同步成玩家相机(见类头『投影同步』)。门面材质按玩家屏幕坐标采 RT,关掉就会整体错位。")]
    private bool matchPlayerProjection = true;

    private PortalPoseSO Bus => pose != null ? pose : PortalPoseSO.Instance;

    // --- 运行时解析 ---
    private Camera playerCamera;      // 玩家视角相机(常驻 Player 场景,标 MainCamera)
    private bool playerCameraWarned;  // 找不到玩家相机只报一次错,不刷屏

    private void Awake()
    {
        if (pose == null)
            pose = PortalPoseSO.Instance;   // 惰性加载后保持引用(防卸载)
    }

    private void Update()
    {
        var bus = Bus;
        var cam = GetComponent<Camera>();
        if (cam == null) return;

        bool live = bus != null && bus.IsActive && bus.Age <= staleTimeout;
        if (!live)
        {
            if (cam.enabled)
                cam.enabled = false;   // 门面工作期结束:停渲染省 GPU(画面随门面一起消失)
            return;
        }

        // 相对位姿公式(与传送同一套):
        //   位置 = 目标门洞 .TransformPoint(本关门洞 .InverseTransformPoint(玩家))，即
        //   玩家相对本关门洞的偏移(offset 已是本关门本地系)再乘 dstRot 转到目标门系、
        //   从目标门洞出发 —— 注意这里乘的是 dstRot 而不是 relRot：
        //   再乘 Inv(srcRot) 会双重反转,两门世界朝向相同时把相机送到门洞另一侧,
        //   违反『相机到门向量与穿越方向点乘 ≥ 0』;
        //   朝向 = 目标门.rotation * Inverse(本关门.rotation) * 玩家相机朝向。
        Vector3 srcPos = bus.SrcDoorPosition;
        Quaternion srcRot = bus.SrcDoorRotation;
        Vector3 dstPos = bus.DstDoorPosition;
        Quaternion dstRot = bus.DstDoorRotation;
        Vector3 offset = Quaternion.Inverse(srcRot) * (bus.PlayerPosition - srcPos);
        Quaternion relRot = dstRot * Quaternion.Inverse(srcRot);
        transform.SetPositionAndRotation(
            dstPos + dstRot * offset,
            relRot * bus.PlayerRotation);

        if (matchPlayerProjection)
            SyncPlayerProjection(cam);

        if (!cam.enabled)
            cam.enabled = true;
    }

    /// <summary>把本相机的投影参数同步成玩家相机 —— 门面材质按玩家屏幕坐标采 RT,
    /// 两者投影必须一致(见类头『投影同步』)。只抄 FOV 与宽高比。</summary>
    private void SyncPlayerProjection(Camera cam)
    {
        if (playerCamera == null)
        {
            playerCamera = Camera.main;   // 玩家视角相机:常驻 Player 场景,标 MainCamera
            if (playerCamera == null)
            {
                if (!playerCameraWarned)
                {
                    playerCameraWarned = true;
                    Debug.LogError("[PortalViewSync] 找不到玩家相机(Camera.main / MainCamera 标签),门面投影无法对齐 —— 门面画面会错位", this);
                }
                return;
            }
        }

        cam.fieldOfView = playerCamera.fieldOfView;
        cam.aspect = playerCamera.aspect;
    }
}

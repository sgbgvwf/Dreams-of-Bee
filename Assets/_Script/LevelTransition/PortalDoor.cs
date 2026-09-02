using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// 传送门（门后渲染纹理 + 穿门传送）。挂在关卡出口锚点（LevelAnchor 所在物体）上，由
/// LevelTransitionManager 在 T1（下一关加载完成、出口门开启）时通过 Activate 激活。
///
/// 原理：下一关与当前关各自独立摆放在自己的世界坐标（不再对齐拼接）。出口门洞上盖一张
/// Quad（门面），用一台"相对门的位置与玩家相对门的位置相同"的渲染相机把下一关实时渲染到
/// RenderTexture 贴在门面上 —— 看起来门后就是下一关；玩家走过门洞时被瞬间传送到下一关
/// 入口门洞的对应位置，速度 / 朝向按门的相对变换同步换算，画面天然连续。
///
/// 约定（场景作者）：出口锚点与入口锚点都放在门洞正中，+Z = 穿越方向，localScale = 1；
/// 门板 +Z 指向穿越方向；门洞尺寸取门板 BoxCollider 的 X×Y。
/// 单向：穿过判定触发一次后永久失效；前场景随后被管理器卸载。
/// </summary>
public class PortalDoor : MonoBehaviour
{
    [SerializeField, Tooltip("距门超过此距离停止渲染门面纹理（省 GPU）")]
    private float renderRange = 25f;

    [SerializeField, Tooltip("距门超过此距离不做穿过判定")]
    private float holdRange = 12f;

    private const int rtBaseWidth = 1024;   // 门面纹理基准宽度（高度按门洞高宽比计算）

    // --- 运行时解析 ---
    private SlidingDoor exitDoor;       // 本关出口门（面板碰撞体 = 门洞尺寸）
    private Transform player;           // 玩家（tag "Player"，Player 常驻场景）
    private BeeFlightController flight; // 玩家身上的飞行控制器（传送 + 视角读取）
    private Transform entryAnchor;      // 下一关入口锚点（Activate 时注入）

    // --- 门平面基准（由出口锚点 + 门板碰撞体推导） ---
    private Vector3 planePos;           // 门面平面位置（锚点沿穿越方向反向退回门板深度的一半；门面与穿过判定共用）
    private Quaternion planeRot;        // 门洞平面朝向（锚点旋转，+Z = 穿越方向）
    private Vector2 planeSize;          // 门洞宽 × 高（门板 BoxCollider size 的 X/Y）

    // --- 运行时创建 ---
    private Camera portalCamera;        // 门后渲染相机（渲染到 rt）
    private RenderTexture rt;           // 门面纹理
    private Material portalMaterial;    // HDRP/Unlit，_UnlitColorMap = rt
    private Transform portalPlane;      // 门面 Quad（挂在出口锚点下，随场景卸载）

    // --- 状态 ---
    private bool active;                // Activate 后为 true
    private bool crossed;               // 已穿过：一次性（单向）
    private bool setupDone;
    private bool lastSide;              // 上一帧玩家相对门平面所在的一侧（true = 平面法线侧）

    /// <summary>由 LevelTransitionManager 在 T1 激活（下一关已加载、出口门即将打开）。</summary>
    public void Activate(Transform entryAnchor)
    {
        this.entryAnchor = entryAnchor;
        if (!EnsureSetup()) return;

        // 玩家必然在门内一侧：以当前所在侧作为"未穿过"基准
        lastSide = GetSide(player.position);
        active = true;
        Debug.Log($"[PortalDoor] 传送门已激活：{name} → {entryAnchor.name}", this);
    }

    private void Update()
    {
        if (!active || crossed) return;
        if (player == null && !ResolvePlayer()) return;
        if (portalCamera == null) return;

        float dist = Vector3.Distance(player.position, planePos);
        portalCamera.gameObject.SetActive(dist < renderRange);   // 走远停渲染，省 GPU

        if (dist > holdRange) return;

        UpdatePortalCameraPose();
        TryCrossing();
    }

    // ==================== 门面与渲染相机 ====================

    private bool EnsureSetup()
    {
        if (setupDone) return true;
        setupDone = true;

        ResolvePlayer();
        if (!TryGetDoorBox(out var box) || entryAnchor == null)
        {
            Debug.LogError($"[PortalDoor] 传送门无法初始化：需要出口锚点(LevelAnchor)、出口门(门板 BoxCollider)与入口锚点。{name}", this);
            return false;
        }

        // 门平面基准：锚点 = 门洞中心（作者约定 +Z = 穿越方向，localScale = 1）
        planeRot = transform.rotation;
        planeSize = new Vector2(box.size.x, box.size.y);
        planePos = ComputePlanePos(box);   // 贴房间内侧墙面：门面与穿过判定共用同一平面

        CreatePortalPlane();
        CreatePortalCamera();
        return true;
    }

    /// <summary>门面平面位置：门洞中心沿穿越方向反向退回门板深度的一半（贴房间内侧墙面）。</summary>
    private Vector3 ComputePlanePos(BoxCollider box) =>
        transform.position - transform.rotation * Vector3.forward * (box.size.z * 0.5f);

    /// <summary>解析出口门及其门板碰撞体（运行时与 Gizmos 共用；编辑期同样可用）。</summary>
    private bool TryGetDoorBox(out BoxCollider box)
    {
        box = null;
        var anchor = GetComponent<LevelAnchor>();
        exitDoor = anchor != null ? anchor.ResolveExitDoor() : null;
        if (exitDoor != null)
            box = exitDoor.DoorPanelCollider as BoxCollider;
        return box != null;
    }

    /// <summary>门面 Quad：贴房间内侧墙面（见 ComputePlanePos），尺寸 = 门洞尺寸。</summary>
    private void CreatePortalPlane()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "PortalPlane";
        go.layer = LayerMask.NameToLayer("Portal");
        go.transform.SetParent(transform, false);
        Destroy(go.GetComponent<MeshCollider>());   // 门面是视觉层，不能挡玩家

        // Quad 可见面朝 -Z → 与锚点同向即可朝房间内（锚点 +Z = 穿越方向 = 背离玩家）
        go.transform.rotation = planeRot;
        go.transform.localScale = new Vector3(planeSize.x, planeSize.y, 1f);
        go.transform.position = planePos;

        portalPlane = go.transform;
    }

    /// <summary>门后渲染相机：姿态每帧由玩家相对出口锚点的位姿映射到入口锚点。</summary>
    private void CreatePortalCamera()
    {
        var shader = Shader.Find("HDRP/Unlit");
        if (shader == null)
            Debug.LogError("[PortalDoor] 找不到 HDRP/Unlit 着色器，门面纹理将无法显示（构建时需把 HDRP/Unlit 加入 Always Included Shaders）", this);

        var go = new GameObject("Portal Camera");
        go.transform.SetParent(transform, false);
        var cam = go.AddComponent<Camera>();
        go.AddComponent<HDAdditionalCameraData>();   // HDRP 相机必需

        var main = Camera.main;
        cam.fieldOfView = main != null ? main.fieldOfView : 60f;
        cam.nearClipPlane = main != null ? main.nearClipPlane : 0.3f;
        cam.farClipPlane = main != null ? main.farClipPlane : 1000f;
        cam.clearFlags = CameraClearFlags.SolidColor;   // 防意外渲染虚空 / 天空
        cam.backgroundColor = Color.black;
        // 不渲染门面平面（防递归渲染）与玩家层
        cam.cullingMask = ~((1 << LayerMask.NameToLayer("Portal")) | (1 << LayerMask.NameToLayer("Player")));

        // 纹理分辨率按门洞高宽比生成（门洞 3.36×4.92 → 1024×1500）
        int h = Mathf.Max(64, Mathf.RoundToInt(rtBaseWidth * planeSize.y / planeSize.x));
        rt = new RenderTexture(rtBaseWidth, h, 0, RenderTextureFormat.DefaultHDR);
        cam.targetTexture = rt;
        portalCamera = cam;

        if (shader != null)
        {
            portalMaterial = new Material(shader);
            portalMaterial.SetColor("_UnlitColor", Color.white);
            portalMaterial.SetTexture("_UnlitColorMap", rt);
            portalPlane.GetComponent<MeshRenderer>().sharedMaterial = portalMaterial;
        }
    }

    // ==================== 每帧：姿态同步 + 穿过判定 ====================

    /// <summary>渲染相机姿态 = 玩家相对出口锚点的位姿，映射到入口锚点（与传送同一公式）。</summary>
    private void UpdatePortalCameraPose()
    {
        portalCamera.transform.SetPositionAndRotation(
            entryAnchor.TransformPoint(transform.InverseTransformPoint(player.position)),
            entryAnchor.rotation * Quaternion.Inverse(transform.rotation) * flight.CameraRotation);
    }

    /// <summary>玩家相对门平面处于哪一侧（true = 平面法线侧 = 穿越方向侧）。</summary>
    private bool GetSide(Vector3 p) => Vector3.Dot(p - planePos, planeRot * Vector3.forward) >= 0f;

    /// <summary>
    /// 穿过判定：玩家位置落在门洞矩形内，且相对门平面的一侧发生翻转。
    /// 门洞矩形判定防止从门洞上方 / 侧面越过（墙外）时误传送。
    /// </summary>
    private void TryCrossing()
    {
        Vector3 rel = player.position - planePos;
        Vector3 local = Quaternion.Inverse(planeRot) * rel;   // 门平面本地坐标

        bool inRect = Mathf.Abs(local.x) < planeSize.x * 0.5f && Mathf.Abs(local.y) < planeSize.y * 0.5f;
        bool side = local.z >= 0f;
        if (inRect && side != lastSide)
        {
            Cross();
            return;
        }
        lastSide = side;   // 未触发也持续更新，防矩形外绕行后状态陈旧
    }

    private void Cross()
    {
        crossed = true;
        if (portalCamera != null) portalCamera.gameObject.SetActive(false);   // 场景即将卸载，停渲染

        flight.ApplyPortalTransform(transform, entryAnchor);   // 传送（速度 / 朝向同步换算）

        if (LevelTransitionManager.Instance != null)
            LevelTransitionManager.Instance.OnPortalCrossed(this);
        else
            Debug.LogError("[PortalDoor] 找不到 LevelTransitionManager，传送后无法卸载前场景", this);
    }

    private bool ResolvePlayer()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        if (go == null) return false;
        player = go.transform;
        flight = go.GetComponent<BeeFlightController>();
        return flight != null;
    }

    // ==================== 编辑期辅助：Gizmos ====================

    private void OnDrawGizmos()
    {
        // 门面平面 + 穿过判定矩形 + 穿越方向（编辑期即可核对门洞尺寸、平面位置与朝向）
        if (!TryGetDoorBox(out var box)) return;

        Vector3 pos = ComputePlanePos(box);
        Quaternion rot = transform.rotation;
        Vector3 size = new Vector3(box.size.x, box.size.y, 0.01f);

        Gizmos.matrix = Matrix4x4.TRS(pos, rot, size);
        Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.30f);
        Gizmos.DrawCube(Vector3.zero, Vector3.one);          // 门面平面（半透明，实际渲染位置）
        Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);      // 穿过判定矩形边框
        Gizmos.matrix = Matrix4x4.identity;

        // 穿越方向（+Z）
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        Vector3 tip = pos + rot * Vector3.forward * 1.2f;
        Gizmos.DrawLine(pos, tip);
        Gizmos.DrawSphere(tip, 0.07f);

#if UNITY_EDITOR
        // 约定检查（仅编辑期）：门板关闭位应与锚点重合 —— 门面以锚点为基准
        if (!Application.isPlaying && exitDoor != null && exitDoor.DoorPanel != null)
        {
            float d = Vector3.Distance(transform.position, exitDoor.DoorPanel.position);
            if (d > 0.2f)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(transform.position, exitDoor.DoorPanel.position);
                Gizmos.DrawWireSphere(exitDoor.DoorPanel.position, 0.3f);
            }
        }
#endif
    }

    private void OnDrawGizmosSelected()
    {
        // 选中时：穿过判定 / RT 渲染的球形范围；运行期再叠加传送对连线与门后相机视锥
        Gizmos.color = new Color(1f, 0.9f, 0.3f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, holdRange);
        Gizmos.color = new Color(1f, 1f, 1f, 0.15f);
        Gizmos.DrawWireSphere(transform.position, renderRange);

        if (entryAnchor != null)   // 运行期激活后可见：到入口锚点的连线（传送对）
        {
            Gizmos.color = new Color(0.2f, 1f, 0.5f, 0.8f);
            Gizmos.DrawLine(transform.position, entryAnchor.position);
            Gizmos.DrawSphere(entryAnchor.position, 0.15f);
            Gizmos.DrawWireSphere(entryAnchor.position, 0.35f);
        }

        if (portalCamera != null)   // 运行期：门后渲染相机视锥（可检查它是否对准入口门洞）
        {
            Gizmos.matrix = portalCamera.transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.35f, 0.35f, 0.6f);
            Gizmos.DrawFrustum(Vector3.zero, portalCamera.fieldOfView,
                portalCamera.farClipPlane, portalCamera.nearClipPlane, portalCamera.aspect);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }

    private void OnDestroy()
    {
        // 运行时创建的纹理在场景卸载时释放 GPU 内存
        if (rt != null) rt.Release();
    }
}

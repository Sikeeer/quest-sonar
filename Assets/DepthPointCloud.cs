using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Выключает passthrough и показывает облако точек датчика глубины (Depth API) в 3D.
/// Основной режим (useGpuCloud): точки берутся из GPU-буфера DepthSampler (десятки тысяч, кольцевая история кадров)
/// и рисуются обычным мешем со спрайтовым материалом: compute-шейдер каждый кадр строит квады прямо в вершинном буфере,
/// CPU к точкам не прикасается. Старые кадры прозрачнее.
/// Запасной режим (DepthSampler не готов или useGpuCloud выключен): частицы по ObstacleMap.OnDepthHit, как в v1.
/// Цвет = высота над полом (синий внизу, красный вверху).
///
/// Безопасность (без камер реального мира не видно!):
///  - кнопка B или Y переключает камеры;
///  - камеры включаются сами, если карта видит препятствие ближе autoPassthroughBelow
///    ИЛИ несколько точек подряд попадают ближе closeRayDistance (то, что ближе карты: стена, столб, предмет в руках).
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class DepthPointCloud : MonoBehaviour
{
    [Header("Режим")]
    public bool startWithoutPassthrough = true;
    public bool toggleWithButtons = true;   // B (правый) / Y (левый)
    [Tooltip("Рисовать облако из GPU-буфера DepthSampler. Выкл = частицы по OnDepthHit, как в v1")]
    public bool useGpuCloud = true;

    [Header("Безопасность")]
    [Tooltip("Автовключение камер. 0 = ВЫКЛЮЧЕНО (отключает и проверку близких точек). Например 0.5 = включить при препятствии ближе 0.5 м")]
    public float autoPassthroughBelow = 0f;
    [Tooltip("Если за кадр минимум closeRayCount точек (OnDepthHit) попали ближе этого расстояния, камеры включаются сами")]
    public float closeRayDistance = 0.4f;
    [Tooltip("В режиме точек из текстуры событий в кадр в десятки раз больше, чем лучей: порог срабатывает легче")]
    public int closeRayCount = 3;
    public float autoHoldSeconds = 3f;

    [Header("Облако точек")]
    [Tooltip("Частицы (запасной режим): максимум частиц")]
    public int maxPoints = 30000;
    public float pointLifetime = 2.5f;
    [Tooltip("Частицы: сколько точек в кадр. maxEmitPerFrame * FPS * pointLifetime не должно сильно превышать maxPoints")]
    public int maxEmitPerFrame = 120;
    [Tooltip("Размер точки, м (и для частиц, и для GPU-облака)")]
    public float pointSize = 0.025f;
    public float colorHeightRange = 2.5f;
    [Tooltip("GPU-облако: прозрачность самого старого кадра истории (свежий кадр = 1)")]
    [Range(0f, 1f)] public float minAlpha = 0.25f;

    const int VertexStride = 20;    // float3 позиция + RGBA8 цвет + half2 uv, см. DepthSample.compute
    const float WaitSamplerSeconds = 30f;

    ParticleSystem ps;
    ParticleSystemRenderer psRenderer;
    ParticleSystem.EmitParams ep;
    int emitted;
    int closeThisFrame;
    bool closeLastFrame;

    Mesh cloudMesh;
    MeshRenderer cloudRenderer;
    GraphicsBuffer cloudVerts;
    bool gpuCloudActive;

    ObstacleMap map;
    Camera cam;
    CameraClearFlags origFlags;
    Color origBg;
    OVRPassthroughLayer[] layers;
    bool passthroughOn = true;
    float autoUntil;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DepthPointCloud>() == null)
            new GameObject("DepthPointCloud").AddComponent<DepthPointCloud>();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);

        var head = SonarCommon.FindHead();
        if (head != null) cam = head.GetComponent<Camera>();
        if (cam == null) cam = Camera.main;
        if (cam != null) { origFlags = cam.clearFlags; origBg = cam.backgroundColor; }

        map = ObstacleMap.Instance;
        if (map == null) { Debug.LogError("DepthPointCloud: нет ObstacleMap."); yield break; }

        layers = FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None);
        if (layers.Length == 0)
            Debug.LogWarning("DepthPointCloud: OVRPassthroughLayer не найден, будет только чёрный фон.");

        CreateCloud();
        map.OnDepthHit += OnHit;

        SetPassthrough(!startWithoutPassthrough);

        if (useGpuCloud) StartCoroutine(SetupGpuCloud());
    }

    void OnDestroy()
    {
        if (map != null) map.OnDepthHit -= OnHit;
        cloudVerts?.Dispose();
        cloudVerts = null;
        if (cloudMesh != null) Destroy(cloudMesh);
    }

    // ---------------------------------------------------------------- частицы (запасной режим)
    void CreateCloud()
    {
        var go = new GameObject("PointCloudParticles");
        ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = maxPoints;
        main.startSpeed = 0f;
        main.gravityModifier = 0f;
        main.startLifetime = pointLifetime;

        var em = ps.emission; em.enabled = false;   // точки добавляем вручную через Emit
        var sh = ps.shape; sh.enabled = false;

        // плавное исчезновение к концу жизни
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        psRenderer = go.GetComponent<ParticleSystemRenderer>();
        psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        psRenderer.sortMode = ParticleSystemSortMode.None;
        psRenderer.material = SonarCommon.NewSpriteMaterial();
        psRenderer.shadowCastingMode = ShadowCastingMode.Off;
        psRenderer.receiveShadows = false;

        ps.Play();
    }

    void OnHit(Vector3 p)
    {
        if (cam != null && (p - cam.transform.position).sqrMagnitude < closeRayDistance * closeRayDistance)
            closeThisFrame++;

        if (ps == null || gpuCloudActive || emitted >= maxEmitPerFrame) return;
        emitted++;

        float t = Mathf.Clamp01((p.y - map.floorY) / colorHeightRange);
        ep.position = p;
        ep.velocity = Vector3.zero;
        ep.startColor = Color.HSVToRGB((1f - t) * 0.66f, 1f, 1f); // низ = синий, верх = красный
        ep.startSize = pointSize;
        ep.startLifetime = pointLifetime;
        ps.Emit(ep, 1);
    }

    // ---------------------------------------------------------------- GPU-облако (основной режим)
    IEnumerator SetupGpuCloud()
    {
        // DepthSampler сам ждёт глубину до 15 с; ждём его решения (ready или failed)
        DepthSampler ds = null;
        float t = 0f;
        while (t < WaitSamplerSeconds)
        {
            ds = DepthSampler.Instance;
            if (ds != null && (ds.ready || ds.failed)) break;
            t += Time.deltaTime;
            yield return null;
        }
        if (ds == null || !ds.ready)
        {
            Debug.Log("DepthPointCloud: DepthSampler не готов, остаются частицы.");
            yield break;
        }
        if (!CreateCloudMesh(ds)) yield break;

        gpuCloudActive = true;
        if (ps != null) ps.Clear();
        SetPassthrough(passthroughOn);   // обновить видимость: меш вместо частиц
        Debug.Log($"DepthPointCloud: GPU-облако на {ds.SlotSize * ds.History} точек.");
    }

    /// <summary>
    /// Меш из квадов: 4 вершины по 20 байт на точку, статичные индексы. Вершины каждый кадр пишет compute-шейдер
    /// (DepthSampler.BuildQuads) прямо в GPU-буфер меша, CPU-копия не трогается.
    /// </summary>
    bool CreateCloudMesh(DepthSampler ds)
    {
        int n = ds.SlotSize * ds.History;
        var mesh = new Mesh { name = "DepthCloud", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertexBufferParams(n * 4,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2));

        var indices = new int[n * 6];
        for (int i = 0, v = 0, k = 0; i < n; i++, v += 4)
        {
            indices[k++] = v; indices[k++] = v + 1; indices[k++] = v + 2;
            indices[k++] = v; indices[k++] = v + 2; indices[k++] = v + 3;
        }
        const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
        mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
        mesh.SetIndexBufferData(indices, 0, 0, indices.Length, flags);
        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length, MeshTopology.Triangles), flags);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2000f);   // точки в мировых координатах, не отсекать
        mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;           // доступ compute-шейдера к вершинам
        mesh.UploadMeshData(false);

        int stride = mesh.GetVertexBufferStride(0);
        if (stride != VertexStride)
        {
            Debug.LogError($"DepthPointCloud: размер вершины {stride} байт вместо {VertexStride}, GPU-облако выключено, остаются частицы.");
            Destroy(mesh);
            return false;
        }
        cloudVerts = mesh.GetVertexBuffer(0);
        if (cloudVerts == null)
        {
            Debug.LogError("DepthPointCloud: не удалось получить вершинный буфер меша, остаются частицы.");
            Destroy(mesh);
            return false;
        }

        var go = new GameObject("PointCloudMesh");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        cloudRenderer = go.AddComponent<MeshRenderer>();
        cloudRenderer.sharedMaterial = SonarCommon.NewSpriteMaterial();
        cloudRenderer.shadowCastingMode = ShadowCastingMode.Off;
        cloudRenderer.receiveShadows = false;
        cloudRenderer.lightProbeUsage = LightProbeUsage.Off;
        cloudRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        cloudMesh = mesh;
        return true;
    }

    // ---------------------------------------------------------------- управление
    void Update()
    {
        if (map == null) return;

        if (toggleWithButtons &&
            (OVRInput.GetDown(OVRInput.Button.Two) || OVRInput.GetDown(OVRInput.Button.Four)))
        {
            autoUntil = 0f;
            SetPassthrough(!passthroughOn);
        }

        if (autoPassthroughBelow > 0f)
        {
            float n = map.nearestDistance;
            bool close = (n >= 0f && n < autoPassthroughBelow) || closeLastFrame;
            if (!passthroughOn && close)
            {
                SetPassthrough(true);
                autoUntil = Time.time + autoHoldSeconds;
            }
            else if (passthroughOn && autoUntil > 0f && Time.time > autoUntil && !close)
            {
                autoUntil = 0f;
                SetPassthrough(false);
            }
        }
    }

    void LateUpdate()
    {
        emitted = 0;
        closeLastFrame = closeThisFrame >= closeRayCount;
        closeThisFrame = 0;

        if (gpuCloudActive && !passthroughOn && cam != null)
        {
            var ds = DepthSampler.Instance;
            if (ds != null && ds.ready)
                ds.BuildQuads(cloudVerts, cam.transform.right, cam.transform.up, pointSize, map.floorY, colorHeightRange, minAlpha);
        }
    }

    public void SetPassthrough(bool on)
    {
        passthroughOn = on;

        if (layers != null)
            foreach (var l in layers) if (l != null) l.enabled = on;

        if (cam != null)
        {
            if (on) { cam.clearFlags = origFlags; cam.backgroundColor = origBg; }
            else { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black; }
        }

        if (psRenderer != null) psRenderer.enabled = !on && !gpuCloudActive;
        if (cloudRenderer != null) cloudRenderer.enabled = !on;
    }
}

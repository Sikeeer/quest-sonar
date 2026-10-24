using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Читает текстуру глубины Quest 3 (EnvironmentDepthManager, Meta XR Core SDK) compute-шейдером
/// Resources/DepthSample.compute и даёт облако мировых точек:
///  - кольцевой GPU-буфер PointsBuffer на historyFrames кадров: из него DepthPointCloud рисует десятки тысяч точек;
///  - копия последнего кадра на CPU (Points, задержка 1-3 кадра): по ней ObstacleMap строит карту, а AudioSonar звучит.
/// Каждая точка: xyz = мир, w = расстояние от головы (0 = нет данных).
/// Самопроверка: случайные точки сравниваются с EnvironmentRaycastManager.Raycast (calibErrorMedian, м).
/// Скрипт создаётся сам при старте сцены. Если глубина недоступна, ready = false и карта остаётся на лучах (как в v1).
/// </summary>
public class DepthSampler : MonoBehaviour
{
    public static DepthSampler Instance { get; private set; }

    [Header("Сетка точек за кадр (менять до старта)")]
    [Tooltip("Точек по горизонтали в кадр. Текстура глубины квадратная, поэтому сетка тоже")]
    public int gridW = 112;
    public int gridH = 112;
    [Tooltip("Сколько кадров хранится в GPU-буфере для отрисовки облака")]
    public int historyFrames = 8;

    [Header("Чтение")]
    [Tooltip("Читать оба глаза по очереди (чуть шире покрытие)")]
    public bool alternateEyes = true;
    [Tooltip("Какой глаз читать, если alternateEyes выключен: 0 = левый, 1 = правый")]
    public int eye = 0;
    public float minDistance = 0.2f;
    public float maxDistance = 4f;
    [Tooltip("Убирать руки из текстуры глубины (иначе они дают близкие точки)")]
    public bool removeHands = true;

    [Header("Отладка (переключать в Play-режиме, если самопроверка даёт большую ошибку)")]
    [Tooltip("Перевернуть текстуру по вертикали. По исходникам Meta не нужно, оставлено на случай другой версии SDK")]
    public bool flipY = false;
    [Tooltip("Привязать к шейдеру именно depth-часть RenderTexture (если по умолчанию читаются нули)")]
    public bool bindDepthElement = false;

    [Header("Самопроверка относительно Raycast")]
    public bool calibrate = true;
    [Tooltip("Сколько лучей пускать раз в полсекунды")]
    public int calibrationSamples = 8;

    [Header("Состояние (только чтение)")]
    public bool ready;
    public bool failed;
    public string status = "старт";
    public string textureInfo = "-";
    [Tooltip("Валидных точек в последнем прочитанном кадре")]
    public int validCount;
    public float pointsPerSecond;
    [Tooltip("Медианное расстояние между точкой и попаданием Raycast в ту же сторону, м (-1 = ещё нет). Хорошо: < 0.05")]
    public float calibErrorMedian = -1f;

    /// <summary>Кольцевой GPU-буфер: SlotSize * History точек float4.</summary>
    public ComputeBuffer PointsBuffer { get; private set; }
    /// <summary>Точек в одном кадре (слоте) = gridW * gridH.</summary>
    public int SlotSize { get; private set; }
    /// <summary>Слотов в кольце = historyFrames.</summary>
    public int History { get; private set; }
    /// <summary>Слот, записанный последним Dispatch (самый свежий кадр).</summary>
    public int WriteSlot { get; private set; }
    /// <summary>Копия последнего прочитанного кадра на CPU (SlotSize точек).</summary>
    public Vector4[] Points { get; private set; }
    /// <summary>Растёт на 1 с каждым готовым readback: потребители обрабатывают каждый кадр один раз.</summary>
    public int ReadbackFrame { get; private set; }

    const int Stride = sizeof(float) * 4;
    const float WaitDepthSeconds = 15f;
    const float CalibInterval = 0.5f;     // как часто пускать проверочные лучи, сек
    const int CalibMedianOf = 16;         // по скольким пробам считать медиану
    const float LogInterval = 2f;

    static readonly int DepthTexId = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int MatricesId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    static readonly int ZBufferId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

    ComputeShader cs;
    int kernelArray, kernel2D, kernelBuild;
    Transform head;
    EnvironmentRaycastManager raycaster;
    readonly List<Matrix4x4> matrices = new();
    readonly List<float> errors = new();
    Texture lastTex;
    bool readbackSupported, readbackBusy, warnedDimension;
    int frame, pointsThisSecond;
    float rateTimer, calibTimer, logTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DepthSampler>() == null)
            new GameObject("DepthSampler").AddComponent<DepthSampler>();
    }

    // ---------------------------------------------------------------- инициализация
    IEnumerator Start()
    {
        Instance = this;
        yield return SonarCommon.WaitUntilReady();
        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();
        if (head == null) { Fail("нет головы (OVRCameraRig)"); yield break; }
        if (!SystemInfo.supportsComputeShaders) { Fail("compute-шейдеры не поддерживаются"); yield break; }

        cs = Resources.Load<ComputeShader>("DepthSample");
        if (cs == null) { Fail("не найден Assets/Resources/DepthSample.compute"); yield break; }
        kernelArray = cs.FindKernel("Sample");
        kernel2D = cs.FindKernel("Sample2D");
        kernelBuild = cs.FindKernel("BuildQuads");

        if (!EnvironmentDepthManager.IsSupported) { Fail("Depth API не поддерживается (нужен Quest 3 и разрешение на сцену)"); yield break; }

        var mgr = FindFirstObjectByType<EnvironmentDepthManager>();
        if (mgr == null)
        {
            mgr = gameObject.AddComponent<EnvironmentDepthManager>();
            mgr.OcclusionShadersMode = OcclusionShadersMode.None;   // окклюзия не нужна, экономим препроцессинг на GPU
        }
        mgr.RemoveHands = removeHands;
        mgr.enabled = true;

        status = "жду глубину";
        float t = 0f;
        while (!mgr.IsDepthAvailable && t < WaitDepthSeconds) { t += Time.deltaTime; yield return null; }
        if (!mgr.IsDepthAvailable) { Fail($"глубина недоступна {WaitDepthSeconds:F0} с (разрешение на сцену? Quest 3?)"); yield break; }

        gridW = Mathf.Max(8, gridW);
        gridH = Mathf.Max(8, gridH);
        History = Mathf.Max(1, historyFrames);
        SlotSize = gridW * gridH;
        PointsBuffer = new ComputeBuffer(SlotSize * History, Stride);
        PointsBuffer.SetData(new Vector4[SlotSize * History]);   // содержимое нового буфера не гарантировано нулевое
        Points = new Vector4[SlotSize];
        WriteSlot = History - 1;

        readbackSupported = SystemInfo.supportsAsyncGPUReadback;
        if (!readbackSupported)
            Debug.LogWarning("DepthSampler: AsyncGPUReadback не поддерживается, точек на CPU (карта, звук) не будет, только облако.");

        ready = true;
        status = "работает";
        Debug.Log($"DepthSampler: готов, сетка {gridW}x{gridH}, история {History} кадров.");
    }

    void Fail(string why)
    {
        failed = true;
        status = why;
        Debug.LogError("DepthSampler: " + why);
    }

    void OnDestroy()
    {
        PointsBuffer?.Release();
        PointsBuffer = null;
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------- главный цикл
    void Update()
    {
        if (!ready || head == null) return;

        var tex = Shader.GetGlobalTexture(DepthTexId);
        if (tex == null) { status = "нет текстуры"; return; }

        int kernel;
        string texProp;
        if (tex.dimension == TextureDimension.Tex2DArray) { kernel = kernelArray; texProp = "_Depth"; }
        else if (tex.dimension == TextureDimension.Tex2D) { kernel = kernel2D; texProp = "_Depth2D"; }
        else
        {
            status = "неизвестный тип текстуры";
            if (!warnedDimension)
            {
                warnedDimension = true;
                Debug.LogError("DepthSampler: _EnvironmentDepthTexture " + Describe(tex) + " - не умею читать. Пришлите это сообщение.");
            }
            return;
        }

        matrices.Clear();
        Shader.GetGlobalMatrixArray(MatricesId, matrices);
        if (matrices.Count == 0) { status = "нет матриц"; return; }

        if (tex != lastTex)
        {
            lastTex = tex;
            textureInfo = Describe(tex);
            Debug.Log("DepthSampler: текстура глубины " + textureInfo + ", матриц " + matrices.Count);
        }

        frame++;
        int slice = Mathf.Clamp(alternateEyes ? (frame & 1) : eye, 0, matrices.Count - 1);
        WriteSlot = (WriteSlot + 1) % History;

        if (bindDepthElement && tex is RenderTexture rt) cs.SetTexture(kernel, texProp, rt, 0, RenderTextureSubElement.Depth);
        else cs.SetTexture(kernel, texProp, tex);
        cs.SetBuffer(kernel, "_Points", PointsBuffer);
        cs.SetMatrix("_InvVP", matrices[slice].inverse);
        cs.SetVector("_HeadPos", head.position);
        cs.SetVector("_ZB", Shader.GetGlobalVector(ZBufferId));
        cs.SetInt("_GridW", gridW);
        cs.SetInt("_GridH", gridH);
        cs.SetInt("_DepthW", tex.width);
        cs.SetInt("_DepthH", tex.height);
        cs.SetInt("_Slice", slice);
        cs.SetInt("_FlipY", flipY ? 1 : 0);
        cs.SetInt("_BaseIndex", WriteSlot * SlotSize);
        cs.SetVector("_Jitter", new Vector2(Halton(frame, 2), Halton(frame, 3)));
        cs.SetFloat("_MinDist", minDistance);
        cs.SetFloat("_MaxDist", maxDistance);
        cs.Dispatch(kernel, Mathf.CeilToInt(gridW / 8f), Mathf.CeilToInt(gridH / 8f), 1);

        if (readbackSupported && !readbackBusy)
        {
            readbackBusy = true;
            AsyncGPUReadback.Request(PointsBuffer, SlotSize * Stride, WriteSlot * SlotSize * Stride, OnReadback);
        }

        status = "работает";
        rateTimer += Time.unscaledDeltaTime;
        if (rateTimer >= 1f)
        {
            pointsPerSecond = pointsThisSecond / rateTimer;
            pointsThisSecond = 0;
            rateTimer = 0f;
        }
    }

    /// <summary>
    /// Строит квады для всех точек кольцевого буфера прямо в вершинный буфер меша (см. DepthPointCloud).
    /// verts: GraphicsBuffer вершин меша (Target.Raw), 4 вершины по 20 байт на точку.
    /// </summary>
    public void BuildQuads(GraphicsBuffer verts, Vector3 right, Vector3 up, float size, float floorY, float colorRange, float minAlpha)
    {
        if (!ready || verts == null) return;
        int total = SlotSize * History;
        cs.SetBuffer(kernelBuild, "_Points", PointsBuffer);
        cs.SetBuffer(kernelBuild, "_Verts", verts);
        cs.SetInt("_TotalPoints", total);
        cs.SetInt("_SlotSize", SlotSize);
        cs.SetInt("_History", History);
        cs.SetInt("_WriteSlot", WriteSlot);
        cs.SetVector("_CamRight", right);
        cs.SetVector("_CamUp", up);
        cs.SetFloat("_PointSize", size);
        cs.SetFloat("_FloorY", floorY);
        cs.SetFloat("_ColorHeightRange", Mathf.Max(0.01f, colorRange));
        cs.SetFloat("_MinAlpha", minAlpha);
        cs.Dispatch(kernelBuild, Mathf.CeilToInt(total / 64f), 1, 1);
    }

    void OnReadback(AsyncGPUReadbackRequest req)
    {
        readbackBusy = false;
        if (req.hasError || Points == null) return;

        var data = req.GetData<Vector4>();
        int n = Mathf.Min(data.Length, Points.Length);
        NativeArray<Vector4>.Copy(data, Points, n);

        int valid = 0;
        for (int i = 0; i < n; i++) if (Points[i].w > 0f) valid++;
        validCount = valid;
        pointsThisSecond += valid;
        ReadbackFrame++;

        if (calibrate && raycaster != null)
        {
            calibTimer += Time.unscaledDeltaTime;
            if (calibTimer >= CalibInterval) { calibTimer = 0f; Calibrate(); }
        }
    }

    // ---------------------------------------------------------------- самопроверка
    void Calibrate()
    {
        if (validCount < calibrationSamples * 4) return;

        Vector3 origin = head.position;
        for (int s = 0; s < calibrationSamples; s++)
        {
            Vector4 p = default;
            for (int tries = 0; tries < 20 && p.w <= 0f; tries++)
                p = Points[Random.Range(0, Points.Length)];
            if (p.w <= 0f) continue;

            Vector3 point = p;
            Vector3 dir = point - origin;
            if (dir.sqrMagnitude < 1e-4f) continue;
            dir.Normalize();
            if (!raycaster.Raycast(new Ray(origin, dir), out EnvironmentRaycastHit hit, maxDistance + 0.5f)) continue;
            if (hit.status != EnvironmentRaycastHitStatus.Hit) continue;

            errors.Add(Vector3.Distance(hit.point, point));
        }

        if (errors.Count >= CalibMedianOf)
        {
            errors.Sort();
            calibErrorMedian = errors[errors.Count / 2];
            errors.Clear();
        }

        logTimer += CalibInterval;
        if (logTimer >= LogInterval)
        {
            logTimer = 0f;
            string err = calibErrorMedian >= 0f ? (calibErrorMedian * 100f).ToString("F1") + " см" : "-";
            Debug.Log($"DepthSampler: точек {validCount}/{SlotSize}, {pointsPerSecond:F0}/с, медианная ошибка vs Raycast {err} " +
                      $"(flipY={flipY}, alternateEyes={alternateEyes}, eye={eye}, bindDepthElement={bindDepthElement})");
        }
    }

    static string Describe(Texture t)
    {
        string fmt = t is RenderTexture r ? r.graphicsFormat + "/" + r.depthStencilFormat : t.graphicsFormat.ToString();
        return $"{t.width}x{t.height} {t.dimension} {fmt} ({t.GetType().Name})";
    }

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0) { f /= b; r += f * (index % b); index /= b; }
        return r;
    }
}

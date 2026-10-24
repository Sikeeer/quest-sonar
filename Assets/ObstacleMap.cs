using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR;
using UnityEngine;

/// <summary>
/// Карта препятствий в реальном времени (2D occupancy grid, вид сверху, мировые координаты).
/// Только Depth API, без комнатной модели MRUK. Подходит для ходьбы.
///
///  1) Источник точек (depthSource):
///     - Texture: точки из текстуры глубины (DepthSampler): тысячи точек в кадр, копия с GPU с задержкой 1-3 кадра;
///     - Raycast: каждый кадр пачка лучей EnvironmentRaycastManager из головы в поле зрения (последовательность Хэлтона):
///       столько, сколько влезает в scanBudgetMs, либо фиксированные raysPerFrame (useTimeBudget = false);
///     - Auto (по умолчанию): Texture, когда DepthSampler готов и его самопроверка в норме, иначе Raycast.
///  2) Попадание на высоте [stepHeight .. headClearance] над полом = препятствие (+log-odds).
///     Попадание в пол = проходимо. Потолок игнорируется.
///  3) Вдоль луча до точки попадания клетки "очищаются" (-log-odds): исчезнувшие предметы стираются.
///  4) Высота разбита на 3 слоя, чтобы луч над низким предметом не стирал его.
///  5) Высота пола учится по лучам вниз.
///  6) Минимап (вверх = направление взгляда) + плитки на полу + API для звука/вибрации.
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class ObstacleMap : MonoBehaviour
{
    public static ObstacleMap Instance { get; private set; }

    public enum DepthSource { Auto, Raycast, Texture }

    [Header("Источник глубины")]
    [Tooltip("Auto: точки из текстуры глубины (DepthSampler), если он готов и самопроверка в норме, иначе лучи. Raycast: всегда лучи (как в v1). Texture: точки, пока DepthSampler готов")]
    public DepthSource depthSource = DepthSource.Auto;
    [Tooltip("Auto: минимум валидных точек в кадре, чтобы перейти на текстуру")]
    public int textureMinPoints = 500;
    [Tooltip("Auto: если медианная ошибка самопроверки DepthSampler больше этого (м), остаёмся на лучах")]
    public float textureMaxError = 0.10f;
    [Tooltip("Режим точек: очищать клетки вдоль пути только к каждой N-й точке (обход клеток дорог для CPU)")]
    public int carveEvery = 8;
    [Tooltip("Режим точек: событие OnDepthHit (звук, частицы) только для каждой N-й точки")]
    public int eventEvery = 4;

    [Header("Сетка")]
    public float cellSize = 0.1f;
    [Tooltip("Клетки дальше от игрока удаляются")]
    public float keepRadius = 40f;

    [Header("Высотное окно препятствий (над полом)")]
    public float stepHeight = 0.15f;
    public float headClearance = 1.9f;
    [Tooltip("Рост до глаз: используется, пока пол ещё не определён по лучам")]
    public float fallbackEyeHeight = 1.6f;

    [Header("Лучи")]
    public float maxDistance = 4f;
    [Tooltip("Если падает FPS - уменьшайте")]
    public int raysPerFrame = 64;
    public float yawRange = 110f;
    [Tooltip("Угол относительно взгляда: минус = ВВЕРХ, плюс = ВНИЗ (так работает Quaternion.Euler)")]
    public float pitchMin = -25f;
    public float pitchMax = 45f;
    [Header("Зона под ноги (ступеньки, пороги)")]
    [Tooltip("Доля лучей, направленных в узкую зону впереди-внизу. 0 = выключено")]
    [Range(0f, 0.8f)] public float footShare = 0.35f;
    public float footYaw = 35f;
    public float footPitchMin = 10f;
    public float footPitchMax = 50f;
    [Header("Память")]
    [Tooltip("За сколько секунд карта забывает клетки, которые не подтверждаются лучами. 0 = не забывать")]
    public float memorySeconds = 4f;

    [Header("Скорость лучей")]
    [Tooltip("Вкл: за кадр пускается столько лучей, сколько влезает в бюджет времени (авто-подбор под FPS). Выкл: фиксированные raysPerFrame")]
    public bool useTimeBudget = true;
    [Tooltip("Сколько миллисекунд за кадр можно тратить на лучи. 72 Гц = 13.9 мс на кадр, 90 Гц = 11 мс, 120 Гц = 8.3 мс")]
    public float scanBudgetMs = 5f;
    public int minRaysPerFrame = 32;
    public int maxRaysPerFrame = 512;

    [Header("Вероятности (log-odds)")]
    public float hitAdd = 0.9f;
    public float missAdd = -0.35f;
    public float minL = -2f;
    public float maxL = 5f;
    public float occThreshold = 1.0f;

    [Header("Визуализация")]
    public bool showMinimap = true;
    public int minimapPixels = 100;  // 100 * 0.1 м = 10 м (радиус 5 м)
    public float minimapSize = 0.38f;
    public Vector3 minimapOffset = new Vector3(-0.40f, -0.04f, 1.0f);
    public bool show3DTiles = true;
    public int max3DTiles = 250;
    public float tiles3DRadius = 3f;
    public float warnDistance = 1.2f;
    public float dangerDistance = 0.6f;

    [Header("Состояние (только чтение)")]
    public float floorY;
    public int occupiedCount;
    public float nearestDistance = -1f;
    public Vector3 nearestPoint;
    public int lastRays, lastHits;
    public float raysPerSecond;
    [Tooltip("Сколько мс ушло на лучи или точки в последнем кадре")]
    public float scanMs;
    [Tooltip("Откуда берутся точки сейчас (см. depthSource)")]
    public DepthSource activeSource;

    /// <summary>Статусы лучей за последний кадр (для диагностики: почему нет попаданий).</summary>
    public readonly Dictionary<EnvironmentRaycastHitStatus, int> statusCounts = new();

    /// <summary>Каждая точка попадания луча в мировых координатах (до фильтра по высоте). Нужна для облака точек.</summary>
    public event System.Action<Vector3> OnDepthHit;

    const int Layers = 3;
    static readonly float[] LayerTop = { 0.6f, 1.2f, float.MaxValue };

    const float MinHitDistance = 0.2f;    // ближе - считаем помехой (руки, шлем), не точкой
    const float CarveStart = 0.25f;       // с какого расстояния от головы начинаем "очищать" клетки вдоль луча
    const float SeenEps = 0.05f;          // |log-odds| меньше этого = клетка пустая, ничего не знаем
    const float StatsInterval = 0.1f;     // как часто обновлять статистику, минимап и плитки, сек
    const float PruneInterval = 2f;       // как часто удалять пустые и далёкие клетки, сек

    class Cell { public float[] l = new float[Layers]; }

    readonly Dictionary<Vector2Int, Cell> cells = new();
    float learnedFloorY = float.NaN;
    float floorCandidate = float.MaxValue;
    bool floorInit;
    readonly List<Vector2Int> tmpKeys = new();
    readonly List<Transform> tiles = new();

    Transform head;
    EnvironmentRaycastManager raycaster;
    Material matGreen, matYellow, matRed, mapMat;
    Texture2D mapTex;
    Color32[] mapPixels;
    TextMesh mapLabel;
    Vector3 lastFwd = Vector3.forward;
    int sampleIndex;
    int seenReadback = -1, cursor, phase;   // режим Texture: какой readback обработан, позиция в нём и фаза подвыборки
    bool pending;                            // режим Texture: в последнем readback остались необработанные точки
    float tick, pruneTick;
    int raysThisSecondCounter;
    float rayTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<ObstacleMap>() == null)
            new GameObject("ObstacleMap").AddComponent<ObstacleMap>();
    }

    // ---------------------------------------------------------------- инициализация
    IEnumerator Start()
    {
        Instance = this;
        matGreen = MakeMat(Color.green);
        matYellow = MakeMat(Color.yellow);
        matRed = MakeMat(Color.red);

        yield return SonarCommon.WaitUntilReady(() => FindFirstObjectByType<EnvironmentRaycastManager>() != null);

        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();
        if (head == null || raycaster == null)
        {
            Debug.LogError("ObstacleMap: нет головы или EnvironmentRaycastManager.");
            yield break;
        }

        if (showMinimap) CreateMinimap();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Destroy(matGreen);
        Destroy(matYellow);
        Destroy(matRed);
        Destroy(mapMat);
        Destroy(mapTex);
    }

    static Material MakeMat(Color c)
    {
        var m = SonarCommon.NewSpriteMaterial();
        m.color = c;
        return m;
    }

    // ---------------------------------------------------------------- главный цикл
    void Update()
    {
        if (head == null || raycaster == null) return;

        UpdateFloor();
        Scan();

        rayTimer += Time.deltaTime;
        if (rayTimer >= 1f)
        {
            raysPerSecond = raysThisSecondCounter / rayTimer;
            raysThisSecondCounter = 0;
            rayTimer = 0f;
        }

        tick += Time.deltaTime;
        if (tick >= StatsInterval)
        {
            if (memorySeconds > 0f) DecayMap(tick);
            tick = 0f;
            UpdateStats();
            if (showMinimap) DrawMinimap();
            if (show3DTiles) Update3DTiles();
        }

        pruneTick += Time.deltaTime;
        if (pruneTick >= PruneInterval)
        {
            pruneTick = 0f;
            Prune();
        }
    }

    void UpdateFloor()
    {
        float target = !float.IsNaN(learnedFloorY)
            ? learnedFloorY
            : head.position.y - fallbackEyeHeight;

        if (!floorInit) { floorY = target; floorInit = true; }
        else floorY = Mathf.Lerp(floorY, target, 0.1f);
    }

    // ---------------------------------------------------------------- сканирование
    void Scan()
    {
        activeSource = ResolveSource();
        if (activeSource == DepthSource.Texture) ScanTexture();
        else ScanRays();
    }

    /// <summary>Какой источник использовать в этом кадре (см. depthSource).</summary>
    DepthSource ResolveSource()
    {
        if (depthSource == DepthSource.Raycast) return DepthSource.Raycast;
        var ds = DepthSampler.Instance;
        bool have = ds != null && ds.ready && ds.Points != null;
        if (depthSource == DepthSource.Texture) return have ? DepthSource.Texture : DepthSource.Raycast;

        // Auto: нужны точки и приемлемая самопроверка (пока медианы нет, остаёмся на лучах)
        if (!have || ds.validCount < textureMinPoints) return DepthSource.Raycast;
        if (ds.calibrate && (ds.calibErrorMedian < 0f || ds.calibErrorMedian > textureMaxError)) return DepthSource.Raycast;
        return DepthSource.Texture;
    }

    /// <summary>
    /// Режим Texture: обход копии последнего кадра DepthSampler.Points. Каждый readback обрабатывается один раз,
    /// в пределах scanBudgetMs за кадр (остаток - в следующем кадре). Очистка клеток вдоль пути и событие OnDepthHit
    /// делаются для подвыборки точек (carveEvery, eventEvery); фаза подвыборки сдвигается с каждым новым кадром.
    /// </summary>
    void ScanTexture()
    {
        var ds = DepthSampler.Instance;
        var pts = ds.Points;
        int cEvery = Mathf.Max(1, carveEvery), eEvery = Mathf.Max(1, eventEvery);

        if (ds.ReadbackFrame != seenReadback)
        {
            seenReadback = ds.ReadbackFrame;
            cursor = 0;
            phase = (phase + 1) % (cEvery * eEvery);
            pending = true;
        }

        floorCandidate = float.MaxValue;
        statusCounts.Clear();
        if (!pending)
        {
            lastRays = 0;
            lastHits = 0;
            scanMs = 0f;
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Vector3 origin = head.position;
        int processed = 0, hits = 0;
        while (cursor < pts.Length)
        {
            int i = cursor++;
            processed++;
            Vector4 p = pts[i];
            if (p.w <= 0f) continue;
            int k = i + phase;
            if (ProcessHit(origin, p, k % cEvery == 0, k % eEvery == 0)) hits++;
            if (useTimeBudget && (processed & 63) == 0 && sw.Elapsed.TotalMilliseconds >= scanBudgetMs) break;
        }
        if (cursor >= pts.Length) pending = false;
        scanMs = (float)sw.Elapsed.TotalMilliseconds;

        LearnFloor();
        lastRays = processed;
        lastHits = hits;
        raysThisSecondCounter += processed;
    }

    /// <summary>Режим Raycast: пачка лучей из головы в поле зрения, как в v1.</summary>
    void ScanRays()
    {
        Vector3 origin = head.position;
        int hits = 0;
        floorCandidate = float.MaxValue;
        statusCounts.Clear();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int limit = useTimeBudget ? maxRaysPerFrame : raysPerFrame;
        int fired = 0;
        Quaternion headRot = head.rotation;

        while (fired < limit)
        {
            sampleIndex = sampleIndex % 100000 + 1;
            float yaw, pitch;
            if (footShare > 0f && Halton(sampleIndex, 5) < footShare)
            {
                yaw = Mathf.Lerp(-footYaw, footYaw, Halton(sampleIndex, 2));
                pitch = Mathf.Lerp(footPitchMin, footPitchMax, Halton(sampleIndex, 3));
            }
            else
            {
                yaw = Mathf.Lerp(-yawRange * 0.5f, yawRange * 0.5f, Halton(sampleIndex, 2));
                pitch = Mathf.Lerp(pitchMin, pitchMax, Halton(sampleIndex, 3));
            }
            Vector3 dir = headRot * (Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);

            if (ProcessRay(origin, dir)) hits++;
            fired++;

            if (useTimeBudget && fired >= minRaysPerFrame && sw.Elapsed.TotalMilliseconds >= scanBudgetMs) break;
        }
        scanMs = (float)sw.Elapsed.TotalMilliseconds;

        LearnFloor();
        lastRays = fired;
        lastHits = hits;
        raysThisSecondCounter += fired;
    }

    /// <summary>Обучение высоты пола по самой низкой точке кадра, полученной лучом или точкой вниз (floorCandidate).</summary>
    void LearnFloor()
    {
        if (floorCandidate >= float.MaxValue) return;
        float eye = head.position.y - floorCandidate;
        if (eye > 0.9f && eye < 2.2f)
            learnedFloorY = float.IsNaN(learnedFloorY) ? floorCandidate : Mathf.Lerp(learnedFloorY, floorCandidate, 0.05f);
    }

    bool ProcessRay(Vector3 origin, Vector3 dir)
    {
        bool ok = raycaster.Raycast(new Ray(origin, dir), out EnvironmentRaycastHit hit, maxDistance);
        statusCounts.TryGetValue(hit.status, out int sc);
        statusCounts[hit.status] = sc + 1;

        if (!ok || hit.status != EnvironmentRaycastHitStatus.Hit) return false;
        return ProcessHit(origin, hit.point, true, true);
    }

    /// <summary>
    /// Общая обработка точки поверхности (от луча или из текстуры глубины): событие OnDepthHit, пол,
    /// классификация по высоте, запись в карту. carve = очищать клетки вдоль пути от головы до точки.
    /// </summary>
    bool ProcessHit(Vector3 origin, Vector3 point, bool carve, bool raiseEvent)
    {
        Vector3 dir = point - origin;
        float dist = dir.magnitude;
        if (dist < MinHitDistance) return false;
        dir /= dist;

        if (raiseEvent) OnDepthHit?.Invoke(point);

        if (dir.y < -0.55f && dist < 2.5f && point.y < floorCandidate) floorCandidate = point.y;

        float h = point.y - floorY;

        if (h < stepHeight || h > headClearance)
        {
            // пол или потолок / что-то выше головы: путь свободен на всю длину, препятствием не считаем
            if (carve) CarveFree(origin, dir, dist);
            return true;
        }

        // препятствие в рабочем диапазоне высот
        if (carve) CarveFree(origin, dir, dist - cellSize * 1.2f);
        Vector3 p = point + dir * 0.03f;
        AddLog(ToCell(p), LayerOf(h), hitAdd);
        return true;
    }

    void CarveFree(Vector3 origin, Vector3 dir, float until)
    {
        var last = new Vector2Int(int.MinValue, int.MinValue);
        float step = cellSize * 0.8f;
        for (float d = CarveStart; d < until; d += step)
        {
            Vector3 p = origin + dir * d;
            float h = p.y - floorY;
            if (h < stepHeight || h > headClearance) continue;
            var key = ToCell(p);
            if (key == last) continue;
            last = key;
            AddLog(key, LayerOf(h), missAdd);
        }
    }

    static int LayerOf(float h)
    {
        for (int i = 0; i < Layers; i++) if (h < LayerTop[i]) return i;
        return Layers - 1;
    }

    void AddLog(Vector2Int key, int layer, float delta)
    {
        if (!cells.TryGetValue(key, out var c))
        {
            if (delta < 0f) return; // "свободно" для неизвестной клетки не создаём
            c = new Cell();
            cells[key] = c;
        }
        c.l[layer] = Mathf.Clamp(c.l[layer] + delta, minL, maxL);
    }

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }
        return r;
    }

    // ---------------------------------------------------------------- публичный API
    public Vector2Int ToCell(Vector3 p)
    {
        return new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));
    }

    public Vector3 CellCenter(Vector2Int k)
    {
        return new Vector3((k.x + 0.5f) * cellSize, floorY, (k.y + 0.5f) * cellSize);
    }

    public bool IsOccupiedCell(Vector2Int key)
    {
        return cells.TryGetValue(key, out var c) && IsCellOcc(c);
    }

    public bool IsOccupied(Vector3 world) { return IsOccupiedCell(ToCell(world)); }

    /// <summary>Расстояние по горизонтали до ближайшего препятствия вдоль направления (-1, если нет).</summary>
    public float DistanceAlong(Vector3 dir, float max)
    {
        if (head == null) return -1f;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return -1f;
        dir.Normalize();
        Vector3 o = head.position;
        for (float d = cellSize; d <= max; d += cellSize * 0.5f)
            if (IsOccupied(o + dir * d)) return d;
        return -1f;
    }

    public void ClearMap() { cells.Clear(); }

    // ---------------------------------------------------------------- обслуживание
    void UpdateStats()
    {
        int occ = 0;
        float best = float.MaxValue;
        Vector3 bestP = Vector3.zero;
        Vector3 hp = head.position;

        foreach (var kv in cells)
        {
            if (!IsCellOcc(kv.Value)) continue;
            occ++;
            Vector3 c = CellCenter(kv.Key);
            float d2 = HorizSqrDistance(c, hp);
            if (d2 < best) { best = d2; bestP = c; }
        }

        occupiedCount = occ;
        nearestDistance = best < float.MaxValue ? Mathf.Sqrt(best) : -1f;
        nearestPoint = bestP;
    }

    static float HorizSqrDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    /// <summary>Подпись источника точек для HUD и миникарты.</summary>
    public string SourceLabel => activeSource == DepthSource.Texture ? "точки" : "лучи";

    /// <summary>Цвет по расстоянию до ближайшего препятствия: красный (опасно), жёлтый (близко), иначе белый.</summary>
    public Color LevelColor()
    {
        if (nearestDistance < 0f) return Color.white;
        if (nearestDistance < dangerDistance) return Color.red;
        return nearestDistance < warnDistance ? Color.yellow : Color.white;
    }

    /// <summary>Плавное забывание: все значения log-odds стремятся к нулю с постоянной времени memorySeconds.</summary>
    void DecayMap(float dt)
    {
        float f = Mathf.Exp(-dt / memorySeconds);
        foreach (var kv in cells)
        {
            var l = kv.Value.l;
            for (int i = 0; i < Layers; i++) l[i] *= f;
        }
    }

    bool IsCellOcc(Cell c)
    {
        for (int i = 0; i < Layers; i++) if (c.l[i] >= occThreshold) return true;
        return false;
    }

    /// <summary>Клетка хоть что-то "знает" (свободно или занято), а не пустая.</summary>
    static bool IsCellSeen(Cell c)
    {
        for (int i = 0; i < Layers; i++) if (Mathf.Abs(c.l[i]) > SeenEps) return true;
        return false;
    }

    void Prune()
    {
        tmpKeys.Clear();
        Vector3 hp = head.position;
        float r2 = keepRadius * keepRadius;
        foreach (var kv in cells)
        {
            bool far = HorizSqrDistance(CellCenter(kv.Key), hp) > r2;
            if (far || !IsCellSeen(kv.Value)) tmpKeys.Add(kv.Key);
        }
        foreach (var k in tmpKeys) cells.Remove(k);
    }

    // ---------------------------------------------------------------- минимап
    void CreateMinimap()
    {
        mapTex = new Texture2D(minimapPixels, minimapPixels, TextureFormat.RGBA32, false);
        mapTex.filterMode = FilterMode.Point;
        mapTex.wrapMode = TextureWrapMode.Clamp;
        mapPixels = new Color32[minimapPixels * minimapPixels];

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = "ObstacleMinimap";
        quad.transform.SetParent(head, false);
        quad.transform.localPosition = minimapOffset;
        quad.transform.localScale = Vector3.one * minimapSize;
        mapMat = SonarCommon.NewSpriteMaterial();
        mapMat.mainTexture = mapTex;
        quad.GetComponent<Renderer>().sharedMaterial = mapMat;

        var lg = new GameObject("MinimapLabel");
        lg.transform.SetParent(quad.transform, false);
        lg.transform.localPosition = new Vector3(0f, -0.56f, 0f);
        lg.transform.localScale = Vector3.one * (1f / minimapSize);
        mapLabel = lg.AddComponent<TextMesh>();
        mapLabel.characterSize = 0.008f;
        mapLabel.fontSize = 48;
        mapLabel.anchor = TextAnchor.UpperCenter;
        mapLabel.color = Color.white;
    }

    void DrawMinimap()
    {
        if (mapTex == null) return;

        Vector3 fwd = head.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude > 1e-4f) lastFwd = fwd.normalized;
        fwd = lastFwd;
        Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
        Vector3 hp = head.position;
        int n = minimapPixels;
        float half = n * 0.5f;

        var unknown = new Color32(0, 0, 0, 90);
        var freeC = new Color32(0, 150, 0, 120);
        var occC = new Color32(255, 30, 30, 240);

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                Vector3 off = right * ((x + 0.5f - half) * cellSize) + fwd * ((y + 0.5f - half) * cellSize);
                var key = ToCell(hp + off);

                Color32 col = unknown;
                if (cells.TryGetValue(key, out var c))
                    col = IsCellOcc(c) ? occC : IsCellSeen(c) ? freeC : unknown;
                mapPixels[y * n + x] = col;
            }
        }

        // игрок в центре + метка направления
        int c0 = n / 2;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                mapPixels[(c0 + dy) * n + (c0 + dx)] = new Color32(255, 255, 255, 255);
        for (int k = 2; k <= 4; k++) mapPixels[(c0 + k) * n + c0] = new Color32(255, 255, 255, 255);

        mapTex.SetPixels32(mapPixels);
        mapTex.Apply(false);

        if (mapLabel != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(nearestDistance >= 0 ? $"Ближайшее: {nearestDistance:F1} м" : "Ближайшее: -");
            sb.Append($"Клеток: {occupiedCount}  {SourceLabel} {lastHits}/{lastRays}  {raysPerSecond:F0}/с");
            mapLabel.text = sb.ToString();
            mapLabel.color = LevelColor();
        }
    }

    // ---------------------------------------------------------------- плитки на полу
    void Update3DTiles()
    {
        int used = 0;
        Vector3 hp = head.position;
        float r2 = tiles3DRadius * tiles3DRadius;

        foreach (var kv in cells)
        {
            if (used >= max3DTiles) break;
            if (!IsCellOcc(kv.Value)) continue;
            Vector3 c = CellCenter(kv.Key);
            float d2 = HorizSqrDistance(c, hp);
            if (d2 > r2) continue;
            PlaceTile(used++, new Vector3(c.x, floorY + 0.015f, c.z), Mathf.Sqrt(d2));
        }
        for (int i = used; i < tiles.Count; i++) tiles[i].gameObject.SetActive(false);
    }

    void PlaceTile(int index, Vector3 pos, float dist)
    {
        while (tiles.Count <= index)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(transform, false);
            g.transform.localScale = new Vector3(cellSize * 0.9f, 0.02f, cellSize * 0.9f);
            tiles.Add(g.transform);
        }
        var t = tiles[index];
        t.gameObject.SetActive(true);
        t.position = pos;
        t.GetComponent<Renderer>().sharedMaterial =
            dist < dangerDistance ? matRed : dist < warnDistance ? matYellow : matGreen;
    }
}
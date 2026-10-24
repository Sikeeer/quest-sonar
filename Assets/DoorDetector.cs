using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Поиск дверных проёмов и звуковой маяк на них.
///
/// Проём не хранится как фигура из точек: это РАЗРЫВ в стене. Алгоритм:
///  1. Веером по горизонту (в пределах взгляда) берём расстояние до ближайшей ВЫСОКОЙ преграды
///     (точки выше 1.2 м над полом: стена, косяк, шкаф; стулья и столы не считаются).
///  2. Лучи без стены = "открыто". Серия открытых лучей между двумя стенами = кандидат.
///  3. Ширина считается по реальным точкам краёв (не по углу), должна быть 0.65-1.3 м,
///     а стены слева и справа примерно на одном расстоянии (одна плоскость).
///  4. Подтверждение: сквозь разрыв реально видны точки ДАЛЬШЕ линии стены (пол или стена соседней комнаты).
///  5. Кандидат должен повториться в нескольких проверках подряд, иначе дверь "мигает".
///
/// Звук: двойной восходящий "динь-дон" из направления проёма. Сначала при обнаружении, потом
/// раз в beaconInterval, пока проём впереди и недалеко. Если звук сонара выключен (A/X), маяк тоже молчит.
///
/// Ограничения (честно): не видит стеклянные и чёрные двери, не проверяет высоту проёма, может принять
/// за дверь любой проход между двумя высокими предметами (шкаф-шкаф, колонна-стена).
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class DoorDetector : MonoBehaviour
{
    public static DoorDetector Instance { get; private set; }

    [Header("Поиск")]
    [Tooltip("Как часто проверять, сек")]
    public float evalInterval = 0.1f;
    [Tooltip("Полуширина просматриваемого веера, градусы (по взгляду)")]
    public float scanHalfAngle = 50f;
    public float binStep = 2f;
    [Tooltip("Дальше этого стена не считается стеной проёма")]
    public float maxWallDistance = 3.5f;
    public float minDoorDistance = 0.5f;
    public float minWidth = 0.65f;
    public float maxWidth = 1.3f;
    [Tooltip("Насколько могут отличаться расстояния до левой и правой стены")]
    public float maxWallMismatch = 0.7f;
    [Tooltip("За линией стены должны быть точки дальше на столько метров")]
    public float beyondMargin = 0.4f;
    [Tooltip("Доля лучей разрыва, за которыми видны точки дальше стены")]
    [Range(0.3f, 1f)] public float beyondShare = 0.6f;

    [Header("Отслеживание")]
    public int confirmHits = 3;
    public float matchRadius = 0.6f;
    public float memorySeconds = 4f;
    public int maxDoors = 4;

    [Header("Звук")]
    public bool beacon = true;
    public float beaconInterval = 2f;
    [Range(0f, 1f)] public float beaconVolume = 0.6f;
    public float beaconFov = 60f;
    public float beaconRange = 3.5f;
    [Tooltip("Источник ставится не дальше этого расстояния от головы (как в AudioSonar)")]
    public float maxSourceDistance = 1.5f;

    [Header("Состояние (для отладки)")]
    public int doorCount;
    public float nearestDoorDistance = -1f;
    public float nearestDoorWidth;

    /// <summary>Подтверждённый проём найден: (центр в мировых координатах, ширина в метрах).</summary>
    public event Action<Vector3, float> OnDoorFound;

    class Door
    {
        public Vector3 center;
        public float width;
        public int hits;
        public float lastSeen;
        public bool announced;
    }

    const int SampleRate = 48000;
    const float EvidenceCell = 0.2f;
    const float EvidenceSeconds = 2f;
    const float EvidenceRange = 4.5f;
    const int Closed = 0, Open = 1;

    ObstacleMap map;
    Transform head;
    AudioSonar sonar;
    AudioSource src;
    AudioClip chime;

    readonly List<Door> doors = new();
    readonly Dictionary<Vector2Int, float> evidence = new();
    readonly List<Vector2Int> pruneTmp = new();
    readonly List<Vector2Int> pruneTallTmp = new();

    int[] state;
    float[] wall;
    Vector3[] dirs;
    float nextEval, nextPrune, nextBeacon;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DoorDetector>() == null)
            new GameObject("DoorDetector").AddComponent<DoorDetector>();
    }

    System.Collections.IEnumerator Start()
    {
        Instance = this;
        float wt = 0f;
        while ((FindFirstObjectByType<OVRCameraRig>() == null || ObstacleMap.Instance == null) && wt < 15f)
        {
            wt += Time.deltaTime;
            yield return null;
        }

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null) head = rig.centerEyeAnchor;
        else if (Camera.main != null) head = Camera.main.transform;
        map = ObstacleMap.Instance;
        if (head == null || map == null) { Debug.LogError("DoorDetector: нет головы или ObstacleMap."); yield break; }
        sonar = FindFirstObjectByType<AudioSonar>();

        int n = Mathf.RoundToInt(2f * scanHalfAngle / binStep) + 1;
        state = new int[n];
        wall = new float[n];
        dirs = new Vector3[n];

        chime = MakeChime();
        var go = new GameObject("DoorBeacon");
        go.transform.SetParent(head, false);
        src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 1f;
        src.spatialize = true;
        src.dopplerLevel = 0f;
        src.rolloffMode = AudioRolloffMode.Linear;
        src.minDistance = 0.3f;
        src.maxDistance = 6f;
        TryAddMetaAudio(go);

        map.OnDepthHit += OnHit;
    }

    void OnDestroy()
    {
        if (map != null) map.OnDepthHit -= OnHit;
        if (chime != null) Destroy(chime);
        if (Instance == this) Instance = null;
    }

    // Meta XR Audio SDK подключаем через рефлексию (если пакета нет, будет обычная панорама Unity)
    static Type metaAudioType;
    static bool metaSearched;

    static void TryAddMetaAudio(GameObject go)
    {
        if (!metaSearched)
        {
            metaSearched = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "MetaXRAudioSource" && typeof(Component).IsAssignableFrom(t)) { metaAudioType = t; break; }
                }
                catch { /* сборка не читается, пропускаем */ }
                if (metaAudioType != null) break;
            }
        }
        if (metaAudioType != null && go.GetComponent(metaAudioType) == null) go.AddComponent(metaAudioType);
    }

    // ---------------------------------------------------------------- свидетельства "там что-то видно"
    class TallCell { public float t; public int n; }
    readonly Dictionary<Vector2Int, TallCell> tall = new();
    const float TallCellSize = 0.1f;
    const float TallMinHeight = 1.2f;   // выше этого над полом = стена/косяк/шкаф (стулья и столы не считаются)
    const int TallMinHits = 2;          // одиночная точка не считается стеной (шум)

    void OnHit(Vector3 p)
    {
        float now = Time.time;
        var k = new Vector2Int(Mathf.FloorToInt(p.x / EvidenceCell), Mathf.FloorToInt(p.z / EvidenceCell));
        evidence[k] = now;

        float h = p.y - map.floorY;
        if (h > TallMinHeight && h < map.headClearance)
        {
            var tk = new Vector2Int(Mathf.FloorToInt(p.x / TallCellSize), Mathf.FloorToInt(p.z / TallCellSize));
            if (tall.TryGetValue(tk, out var c))
            {
                if (now - c.t > map.memorySeconds) c.n = 0;
                c.n++;
                c.t = now;
            }
            else tall[tk] = new TallCell { t = now, n = 1 };
        }
    }

    /// <summary>Расстояние по горизонтали от головы до ближайшей высокой преграды вдоль направления (-1, если нет).</summary>
    float DistanceAlongTall(Vector3 dir, float max, float now)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return -1f;
        dir.Normalize();
        Vector3 o = head.position;
        for (float d = TallCellSize; d <= max; d += TallCellSize * 0.5f)
        {
            Vector3 w = o + dir * d;
            var tk = new Vector2Int(Mathf.FloorToInt(w.x / TallCellSize), Mathf.FloorToInt(w.z / TallCellSize));
            if (tall.TryGetValue(tk, out var c) && c.n >= TallMinHits && now - c.t <= map.memorySeconds) return d;
        }
        return -1f;
    }

    bool HasEvidence(Vector3 w, float now)
    {
        var k = new Vector2Int(Mathf.FloorToInt(w.x / EvidenceCell), Mathf.FloorToInt(w.z / EvidenceCell));
        return evidence.TryGetValue(k, out float t) && now - t <= EvidenceSeconds;
    }

    void Prune(float now)
    {
        pruneTmp.Clear();
        foreach (var kv in evidence)
            if (now - kv.Value > EvidenceSeconds) pruneTmp.Add(kv.Key);
        for (int i = 0; i < pruneTmp.Count; i++) evidence.Remove(pruneTmp[i]);

        pruneTallTmp.Clear();
        foreach (var kv in tall)
            if (now - kv.Value.t > map.memorySeconds) pruneTallTmp.Add(kv.Key);
        for (int i = 0; i < pruneTallTmp.Count; i++) tall.Remove(pruneTallTmp[i]);
    }

    // ---------------------------------------------------------------- основной цикл
    void Update()
    {
        if (state == null || head == null || map == null) return;
        float now = Time.time;

        if (now >= nextPrune) { nextPrune = now + 1f; Prune(now); }

        if (now >= nextEval)
        {
            nextEval = now + evalInterval;
            Evaluate(now);
            UpdateTracks(now);
        }

        if (beacon) TickBeacon(now);
    }

    // кандидаты текущей проверки (без выделения памяти каждый кадр)
    bool candFound;
    Vector3 candCenter;
    float candWidth;

    void Evaluate(float now)
    {
        candFound = false;

        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.05f) return;   // смотрим в пол или в потолок
        f.Normalize();
        Vector3 o = head.position;

        int n = state.Length;
        float probe = maxWallDistance + 0.3f;
        for (int i = 0; i < n; i++)
        {
            float off = -scanHalfAngle + i * binStep;
            dirs[i] = Quaternion.AngleAxis(off, Vector3.up) * f;
            float d = DistanceAlongTall(dirs[i], probe, now);
            wall[i] = d;
            state[i] = (d > 0f && d <= maxWallDistance) ? Closed : Open;
        }

        // одиночный "закрытый" луч внутри разрыва (шум, тонкий предмет) считаем открытым
        for (int i = 1; i < n - 1; i++)
            if (state[i] == Closed && state[i - 1] == Open && state[i + 1] == Open) state[i] = Open;

        float bestScore = float.MaxValue;
        int s = 0;
        while (s < n)
        {
            if (state[s] != Open) { s++; continue; }
            int e = s;
            while (e + 1 < n && state[e + 1] == Open) e++;

            // нужны по две закрытые стенки с обеих сторон веера
            if (s >= 2 && e <= n - 3
                && state[s - 1] == Closed && state[s - 2] == Closed
                && state[e + 1] == Closed && state[e + 2] == Closed)
            {
                if (TryMakeCandidate(o, s, e, now, out Vector3 center, out float width))
                {
                    float score = (center - o).sqrMagnitude;   // ближайший проём важнее
                    if (score < bestScore)
                    {
                        bestScore = score;
                        candFound = true;
                        candCenter = center;
                        candWidth = width;
                    }
                }
            }
            s = e + 1;
        }
    }

    bool TryMakeCandidate(Vector3 o, int s, int e, float now, out Vector3 center, out float width)
    {
        center = Vector3.zero;
        width = 0f;

        float dl = wall[s - 1], dr = wall[e + 1];
        if (Mathf.Abs(dl - dr) > maxWallMismatch) return false;

        Vector3 pl = o + dirs[s - 1] * dl;
        Vector3 pr = o + dirs[e + 1] * dr;
        pl.y = 0f; pr.y = 0f;
        width = Vector3.Distance(pl, pr);
        if (width < minWidth || width > maxWidth) return false;

        center = (pl + pr) * 0.5f;
        Vector3 oFlat = new Vector3(o.x, 0f, o.z);
        if (Vector3.Distance(center, oFlat) < minDoorDistance) return false;

        // сквозь разрыв должны быть видны точки дальше линии стены (пол или стена соседней комнаты)
        float dRef = (dl + dr) * 0.5f;
        int total = e - s + 1, seen = 0;
        for (int i = s; i <= e; i++)
        {
            for (float d = dRef + beyondMargin; d <= EvidenceRange; d += 0.1f)
            {
                if (HasEvidence(o + dirs[i] * d, now)) { seen++; break; }
            }
        }
        if (seen < Mathf.CeilToInt(total * beyondShare)) return false;

        center.y = o.y;
        return true;
    }

    // ---------------------------------------------------------------- отслеживание
    void UpdateTracks(float now)
    {
        if (candFound)
        {
            Door match = null;
            float best = matchRadius;
            for (int i = 0; i < doors.Count; i++)
            {
                float d = Vector3.Distance(doors[i].center, candCenter);
                if (d < best) { best = d; match = doors[i]; }
            }

            if (match != null)
            {
                match.center = Vector3.Lerp(match.center, candCenter, 0.35f);
                match.width = Mathf.Lerp(match.width, candWidth, 0.35f);
                match.hits++;
                match.lastSeen = now;
            }
            else if (doors.Count < maxDoors)
            {
                doors.Add(new Door { center = candCenter, width = candWidth, hits = 1, lastSeen = now });
            }
        }

        // чистка: неподтверждённые гаснут быстро, подтверждённые живут memorySeconds
        for (int i = doors.Count - 1; i >= 0; i--)
        {
            var d = doors[i];
            bool confirmed = d.hits >= confirmHits;
            float ttl = confirmed ? memorySeconds : 1f;
            if (now - d.lastSeen > ttl) { doors.RemoveAt(i); continue; }

            if (confirmed && !d.announced)
            {
                d.announced = true;
                Debug.Log($"DoorDetector: проём, ширина {d.width:F2} м, до него {Vector3.Distance(d.center, head.position):F1} м");
                OnDoorFound?.Invoke(d.center, d.width);
                if (beacon) Play(d, now);
            }
        }

        // статистика для HUD
        int cnt = 0;
        float nearest = float.MaxValue;
        float nearestW = 0f;
        for (int i = 0; i < doors.Count; i++)
        {
            if (doors[i].hits < confirmHits) continue;
            cnt++;
            float dist = FlatDistance(doors[i].center);
            if (dist < nearest) { nearest = dist; nearestW = doors[i].width; }
        }
        doorCount = cnt;
        nearestDoorDistance = cnt > 0 ? nearest : -1f;
        nearestDoorWidth = cnt > 0 ? nearestW : 0f;
    }

    float FlatDistance(Vector3 p)
    {
        Vector3 h = head.position;
        float dx = p.x - h.x, dz = p.z - h.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ---------------------------------------------------------------- звук
    void TickBeacon(float now)
    {
        if (now < nextBeacon || src == null) return;

        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.05f) return;
        f.Normalize();

        // маяк на ближайший подтверждённый проём впереди, который недавно видели
        Door target = null;
        float best = float.MaxValue;
        for (int i = 0; i < doors.Count; i++)
        {
            var d = doors[i];
            if (d.hits < confirmHits || now - d.lastSeen > 1f) continue;
            float dist = FlatDistance(d.center);
            if (dist > beaconRange) continue;
            Vector3 to = d.center - head.position;
            to.y = 0f;
            if (Vector3.Angle(f, to) > beaconFov) continue;
            if (dist < best) { best = dist; target = d; }
        }
        if (target != null) Play(target, now);
    }

    void Play(Door d, float now)
    {
        nextBeacon = now + beaconInterval;
        if (sonar != null && sonar.muted) return;
        if (src == null || chime == null) return;

        Vector3 to = d.center - head.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist < 0.05f) return;

        // источник в горизонтальной плоскости, в локальных координатах головы
        Vector3 local = head.InverseTransformDirection(to / dist);
        src.transform.localPosition = local * Mathf.Min(dist, maxSourceDistance);

        float master = sonar != null ? sonar.masterVolume : 1f;
        src.PlayOneShot(chime, beaconVolume * master);
    }

    // двойной восходящий "динь-дон": E5 затем B5, мягкая атака, ноль в конце (без щелчков)
    static AudioClip MakeChime()
    {
        const float len = 0.42f;
        int n = (int)(SampleRate * len);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            data[i] = 0.5f * (Note(t, 0f, 659.25f, 0.18f) + Note(t, 0.16f, 987.77f, 0.26f));
        }
        var c = AudioClip.Create("doorChime", n, 1, SampleRate, false);
        c.SetData(data, 0);
        return c;
    }

    static float Note(float t, float start, float freq, float dur)
    {
        float tt = t - start;
        if (tt < 0f || tt > dur) return 0f;
        float attack = Mathf.Clamp01(tt / 0.008f);
        float decay = Mathf.Exp(-tt / (dur * 0.45f));
        float tail = Mathf.Clamp01((dur - tt) / 0.02f);
        float w = 2f * Mathf.PI * freq * tt;
        return (Mathf.Sin(w) + 0.25f * Mathf.Sin(2f * w)) * attack * decay * tail;
    }
}

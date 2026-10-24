using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Звуковой "парктроник" для слепых: превращает точки глубины (ObstacleMap.OnDepthHit) в пространственные писки.
///
///  - Поле зрения делится на sectors секторов по горизонтали. В каждом берётся ближайшая точка за короткое окно.
///  - Каждый сектор = пространственный источник звука (Meta XR Audio SDK, HRTF) в сторону ближайшей точки.
///  - Чем ближе, тем чаще писк и громче. Ближе dangerDistance: почти сплошной тон + вибрация контроллеров.
///  - Тембр: низкий мягкий = предмет ниже пояса, высокий резкий = на уровне корпуса/головы (трость такое не находит).
///  - Обрыв / ступеньки вниз: отдельный нисходящий сигнал.
///  - Кнопка A или X включает/выключает звук (B/Y остаются для камер).
/// Скрипт создаётся сам при старте сцены.
/// ВАЖНО: это помощь, а не замена трости или собаки-проводника. Датчик не видит стекло и тонкие предметы.
/// </summary>
public class AudioSonar : MonoBehaviour
{
    [Header("Секторы")]
    public int sectors = 5;
    [Tooltip("Дальше этого расстояния сектор молчит (не больше maxDistance в ObstacleMap)")]
    public float maxRange = 3f;
    public float minRange = 0.2f;
    [Tooltip("Окно накопления ближайшей точки, сек. Меньше = быстрее реакция, но больше дрожания")]
    public float windowSec = 0.12f;

    [Header("Ритм и громкость")]
    public float minInterval = 0.09f;   // у самого препятствия
    public float maxInterval = 0.8f;    // на дальней границе
    public float nearVolume = 0.9f;
    public float farVolume = 0.25f;
    [Tooltip("Одновременно звучат только столько ближайших секторов (в обзорах рекомендуют 3-4, иначе звуки сливаются)")]
    public int activeSectors = 3;
    [Tooltip("Минимальная пауза между началами звуков разных секторов, сек (звуки не накладываются)")]
    public float minOnsetGap = 0.06f;
    [Tooltip("На сколько ступеней пентатонической гаммы делится расстояние: ближе = выше нота")]
    public int noteBands = 6;
    public float masterVolume = 1f;
    [Tooltip("Источник ставится не дальше этого расстояния от головы, чтобы громкость задавалась нами, а не спадом звука")]
    public float maxSourceDistance = 1.5f;

    [Header("Тембр по высоте")]
    [Tooltip("Точка выше этого над полом = высокий резкий тон")]
    public float highFrom = 0.9f;

    [Header("Опасность (порог берётся из ObstacleMap.dangerDistance)")]
    public bool haptics = true;

    [Header("Обрыв / ступеньки вниз")]
    public bool detectDropOff = true;
    [Tooltip("Точка ниже пола на столько метров считается обрывом")]
    public float dropDepth = 0.3f;
    public int dropMinVotes = 3;
    public float dropRepeat = 0.7f;

    [Header("Управление")]
    public bool toggleWithButtons = true;   // A (правый) / X (левый)
    public bool muted;

    ObstacleMap map;
    Transform head;
    AudioSource[] src;
    AudioSource dropSrc;
    AudioClip clipLow, clipHigh, clipDrop;

    float[] acc, pub;
    Vector3[] accDir, pubDir;      // направление в локальных координатах головы
    int[] accHigh, pubHigh;        // 1 = высокий тон
    float[] nextBeep;
    float window;
    int dropVotes;
    Vector3 dropDirAcc, dropDir;
    bool dropActive;
    float nextDrop;
    bool hapticOn;
    float half;
    int[] order;
    float lastOnset;
    // пентатоника (полутоны от основной ноты): любые две ноты звучат созвучно, поэтому писки не режут слух
    static readonly int[] Penta = { 0, 2, 4, 7, 9, 12, 14, 16 };

    const float RampStart = 0.3f;     // до этого расстояния звук считается "вплотную" (интервал и громкость на максимуме)
    const int SampleRate = 48000;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<AudioSonar>() == null)
            new GameObject("AudioSonar").AddComponent<AudioSonar>();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);

        head = SonarCommon.FindHead();
        map = ObstacleMap.Instance;
        if (head == null || map == null) { Debug.LogError("AudioSonar: нет головы или ObstacleMap."); yield break; }

        half = map.yawRange * 0.5f;
        sectors = Mathf.Max(1, sectors);
        acc = new float[sectors]; pub = new float[sectors];
        accDir = new Vector3[sectors]; pubDir = new Vector3[sectors];
        accHigh = new int[sectors]; pubHigh = new int[sectors];
        nextBeep = new float[sectors];
        order = new int[sectors];
        ResetWindow();
        for (int i = 0; i < sectors; i++) pub[i] = float.MaxValue;

        clipLow = MakeSoft("sonarLow", 330f, false, 0.14f);    // тёплый "щипок" (предмет ниже)
        clipHigh = MakeSoft("sonarHigh", 523f, true, 0.18f);   // "колокольчик" (предмет выше)
        clipDrop = MakeChirp("sonarDrop", 900f, 300f, 0.35f);

        src = new AudioSource[sectors];
        for (int i = 0; i < sectors; i++) src[i] = MakeSource("Sonar" + i);
        dropSrc = MakeSource("SonarDrop");

        map.OnDepthHit += OnHit;
    }

    void OnDestroy()
    {
        if (map != null) map.OnDepthHit -= OnHit;
        SetHaptics(false);
    }

    // ---------------------------------------------------------------- приём точек
    void OnHit(Vector3 p)
    {
        if (head == null || acc == null) return;

        Vector3 local = head.InverseTransformPoint(p);
        float d = local.magnitude;
        if (d < minRange || d > maxRange) return;

        float h = p.y - map.floorY;

        // обрыв: точка заметно ниже пола
        if (detectDropOff && h < -dropDepth)
        {
            dropVotes++;
            dropDirAcc += local.normalized;
            return;
        }

        if (h < map.stepHeight) return;          // пол
        if (h > map.headClearance) return;       // потолок

        float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        if (Mathf.Abs(yaw) > half) return;

        int s = Mathf.Clamp((int)((yaw + half) / (half * 2f) * sectors), 0, sectors - 1);
        if (d < acc[s])
        {
            acc[s] = d;
            accDir[s] = local / d;
            accHigh[s] = h >= highFrom ? 1 : 0;
        }
    }

    void ResetWindow()
    {
        for (int i = 0; i < sectors; i++) acc[i] = float.MaxValue;
        dropVotes = 0;
        dropDirAcc = Vector3.zero;
    }

    // ---------------------------------------------------------------- звук
    void Update()
    {
        if (map == null || head == null || src == null) return;

        if (toggleWithButtons &&
            (OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three)))
        {
            // подтверждающий сигнал: при выключении низкий, при включении высокий
            dropSrc.volume = masterVolume;
            dropSrc.PlayOneShot(muted ? clipHigh : clipLow);
            muted = !muted;
            if (muted) { SetHaptics(false); foreach (var s in src) s.Stop(); }
        }

        // публикуем накопленное за окно
        window += Time.deltaTime;
        if (window >= windowSec)
        {
            window = 0f;
            Array.Copy(acc, pub, sectors);
            Array.Copy(accDir, pubDir, sectors);
            Array.Copy(accHigh, pubHigh, sectors);
            dropActive = dropVotes >= dropMinVotes;
            dropDir = dropActive ? dropDirAcc.normalized : Vector3.forward;
            ResetWindow();
        }

        if (muted) return;

        float now = Time.time;

        // озвучиваем только activeSectors ближайших секторов
        SortSectorsByDistance();
        float nearest = pub[order[0]] < maxRange ? pub[order[0]] : float.MaxValue;
        float danger = map.dangerDistance;

        for (int n = 0; n < sectors && n < activeSectors; n++)
        {
            int i = order[n];
            float d = pub[i];
            if (d >= maxRange) break;

            if (now < nextBeep[i] || now - lastOnset < minOnsetGap) continue;

            bool isDanger = d < danger;
            float k = Mathf.Clamp01((d - RampStart) / Mathf.Max(0.01f, maxRange - RampStart));
            float interval = isDanger ? minInterval : Mathf.Lerp(minInterval, maxInterval, k);
            float vol = isDanger ? nearVolume : Mathf.Lerp(nearVolume, farVolume, k);

            var s = src[i];
            s.transform.localPosition = pubDir[i] * Mathf.Min(d, maxSourceDistance);
            s.clip = pubHigh[i] == 1 ? clipHigh : clipLow;
            s.volume = vol * masterVolume;
            int band = Mathf.Clamp((int)((1f - k) * noteBands), 0, Mathf.Min(noteBands, Penta.Length) - 1);
            s.pitch = Mathf.Pow(2f, Penta[band] / 12f);   // ближе = выше нота
            s.Play();
            nextBeep[i] = now + interval;
            lastOnset = now;
        }

        if (dropActive && now >= nextDrop)
        {
            dropSrc.transform.localPosition = dropDir;
            dropSrc.volume = masterVolume;
            dropSrc.PlayOneShot(clipDrop);
            nextDrop = now + dropRepeat;
        }

        SetHaptics(haptics && nearest < danger);
    }

    /// <summary>Заполняет order индексами секторов по возрастанию расстояния (секторов мало, хватает вставок).</summary>
    void SortSectorsByDistance()
    {
        for (int i = 0; i < sectors; i++)
        {
            int j = i;
            while (j > 0 && pub[order[j - 1]] > pub[i]) { order[j] = order[j - 1]; j--; }
            order[j] = i;
        }
    }

    void SetHaptics(bool on)
    {
        if (on == hapticOn) return;
        hapticOn = on;
        float frequency = on ? 0.5f : 0f;
        float amplitude = on ? 0.6f : 0f;
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.LTouch);
    }

    // ---------------------------------------------------------------- создание источников и клипов
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
            Debug.Log(metaAudioType != null
                ? "AudioSonar: Meta XR Audio SDK найден."
                : "AudioSonar: Meta XR Audio SDK НЕ найден, будет обычная панорама Unity.");
        }
        if (metaAudioType != null && go.GetComponent(metaAudioType) == null) go.AddComponent(metaAudioType);
    }

    AudioSource MakeSource(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(head, false);
        var a = go.AddComponent<AudioSource>();
        a.playOnAwake = false;
        a.loop = false;
        a.spatialBlend = 1f;
        a.spatialize = true;            // включает плагин-спатиализатор (Meta XR Audio), если выбран в Project Settings
        a.dopplerLevel = 0f;
        a.rolloffMode = AudioRolloffMode.Linear;
        a.minDistance = 0.3f;
        a.maxDistance = 6f;
        TryAddMetaAudio(go);
        return a;
    }

    // Мягкий "щипковый" звук: плавная атака, экспоненциальное затухание, чуть шума (~5%, он помогает локализации).
    static AudioClip MakeSoft(string name, float freq, bool bell, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        var rnd = new System.Random(1234);
        float tau = len * 0.35f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float a = Mathf.Clamp01(t / 0.012f);
            a = a * a * (3f - 2f * a);                       // плавная атака без щелчка
            float decay = Mathf.Exp(-t / tau);
            float tail = Mathf.Clamp01((len - t) / 0.02f);   // гарантированный ноль в конце
            float w = 2f * Mathf.PI * freq * t;
            float s;
            if (bell)
                s = Mathf.Sin(w)
                  + 0.5f * Mathf.Sin(2.76f * w) * Mathf.Exp(-t / (tau * 0.5f))
                  + 0.25f * Mathf.Sin(5.4f * w) * Mathf.Exp(-t / (tau * 0.3f));
            else
                s = Mathf.Sin(w) + 0.35f * Mathf.Sin(2f * w) * Mathf.Exp(-t / (tau * 0.5f));
            float noise = ((float)rnd.NextDouble() * 2f - 1f) * 0.05f;
            data[i] = (s * 0.5f + noise) * a * decay * tail * 0.8f;
        }
        return MakeClip(name, data);
    }

    static AudioClip MakeChirp(string name, float f0, float f1, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n;
            float f = Mathf.Lerp(f0, f1, u);
            phase += 2f * Mathf.PI * f / sr;
            float env = Mathf.Min(1f, i / (0.01f * sr)) * (1f - u);
            data[i] = (Mathf.Sin(phase) + 0.4f * Mathf.Sin(2f * phase)) * 0.6f * env;
        }
        return MakeClip(name, data);
    }

    static AudioClip MakeClip(string name, float[] data)
    {
        var c = AudioClip.Create(name, data.Length, 1, SampleRate, false);
        c.SetData(data, 0);
        return c;
    }
}

using System.Collections;
using UnityEngine;

/// <summary>
/// Выключает passthrough и показывает облако точек датчика глубины (Depth API) в 3D.
/// Точки берутся из ObstacleMap.OnDepthHit. Цвет = высота над полом (синий внизу, красный вверху).
///
/// Безопасность (без камер реального мира не видно!):
///  - кнопка B или Y переключает камеры;
///  - камеры включаются сами, если карта видит препятствие ближе autoPassthroughBelow
///    ИЛИ несколько лучей подряд попадают ближе closeRayDistance (то, что ближе карты: стена, столб, предмет в руках).
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class DepthPointCloud : MonoBehaviour
{
    [Header("Режим")]
    public bool startWithoutPassthrough = true;
    public bool toggleWithButtons = true;   // B (правый) / Y (левый)

    [Header("Безопасность")]
    [Tooltip("Автовключение камер. 0 = ВЫКЛЮЧЕНО (отключает и проверку близких лучей). Например 0.5 = включить при препятствии ближе 0.5 м")]
    public float autoPassthroughBelow = 0f;
    [Tooltip("Если за кадр минимум closeRayCount лучей попали ближе этого расстояния, камеры включаются сами")]
    public float closeRayDistance = 0.4f;
    public int closeRayCount = 3;
    public float autoHoldSeconds = 3f;

    [Header("Облако точек")]
    public int maxPoints = 30000;
    public float pointLifetime = 2.5f;
    [Tooltip("Сколько точек в кадр. maxEmitPerFrame * FPS * pointLifetime не должно сильно превышать maxPoints")]
    public int maxEmitPerFrame = 120;
    public float pointSize = 0.025f;
    public float colorHeightRange = 2.5f;

    ParticleSystem ps;
    ParticleSystemRenderer psRenderer;
    ParticleSystem.EmitParams ep;
    int emitted;
    int closeThisFrame;
    bool closeLastFrame;

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
        float t = 0;
        while ((FindFirstObjectByType<OVRCameraRig>() == null || ObstacleMap.Instance == null) && t < 15f)
        {
            t += Time.deltaTime;
            yield return null;
        }

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null) cam = rig.centerEyeAnchor.GetComponent<Camera>();
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
    }

    void OnDestroy()
    {
        if (map != null) map.OnDepthHit -= OnHit;
    }

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
        psRenderer.material = new Material(Shader.Find("Sprites/Default"));
        psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        psRenderer.receiveShadows = false;

        ps.Play();
    }

    void OnHit(Vector3 p)
    {
        if (cam != null && (p - cam.transform.position).sqrMagnitude < closeRayDistance * closeRayDistance)
            closeThisFrame++;

        if (ps == null || emitted >= maxEmitPerFrame) return;
        emitted++;

        float t = Mathf.Clamp01((p.y - map.floorY) / colorHeightRange);
        ep.position = p;
        ep.velocity = Vector3.zero;
        ep.startColor = Color.HSVToRGB((1f - t) * 0.66f, 1f, 1f); // низ = синий, верх = красный
        ep.startSize = pointSize;
        ep.startLifetime = pointLifetime;
        ps.Emit(ep, 1);
    }

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

        if (psRenderer != null) psRenderer.enabled = !on;
    }
}

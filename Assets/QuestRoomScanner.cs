using System.Collections;
using System.Text;
using UnityEngine;

/// <summary>
/// Панель статуса (HUD) для диагностики ObstacleMap. Без MRUK и без своих лучей.
/// Показывает: частоту кадров скана, лучи или точки (попадания/всего) и их число в секунду,
/// статусы лучей (почему нет попаданий), высоту пола, ближайшее препятствие,
/// источник глубины и состояние DepthSampler (точек в кадре, скорость, ошибка самопроверки).
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class QuestRoomScanner : MonoBehaviour
{
    public bool showHud = true;
    public Vector3 hudOffset = new Vector3(0f, -0.22f, 1.0f);
    public float characterSize = 0.012f;

    Transform head;
    TextMesh hud;
    float timer;
    int frames;
    float fps;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<QuestRoomScanner>() == null)
            new GameObject("QuestRoomScanner").AddComponent<QuestRoomScanner>();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);

        head = SonarCommon.FindHead();

        if (showHud && head != null) CreateHud();
    }

    void CreateHud()
    {
        var go = new GameObject("HUD");
        go.transform.SetParent(head, false);
        go.transform.localPosition = hudOffset;
        hud = go.AddComponent<TextMesh>();
        hud.characterSize = characterSize;
        hud.fontSize = 48;
        hud.anchor = TextAnchor.UpperCenter;
        hud.color = Color.white;
    }

    void Update()
    {
        var map = ObstacleMap.Instance;
        if (hud == null || map == null) return;

        frames++;
        timer += Time.unscaledDeltaTime;
        if (timer < 0.2f) return;
        fps = frames / timer;
        frames = 0;
        timer = 0f;

        var sb = new StringBuilder();
        sb.AppendLine(map.nearestDistance >= 0 ? $"Препятствие: {map.nearestDistance:F1} м" : "Препятствие: нет");
        sb.AppendLine($"Пол: y={map.floorY:F2}  клеток: {map.occupiedCount}");
        sb.Append($"Скан: {fps:F0}/с  {map.SourceLabel} {map.lastHits}/{map.lastRays}  {map.raysPerSecond:F0}/с  {map.scanMs:F1} мс  ");
        foreach (var kv in map.statusCounts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
        sb.Append('\n').Append(DepthLine(map));
        hud.text = sb.ToString();

        hud.color = map.LevelColor();
    }

    /// <summary>Строка про источник глубины: режим карты и состояние DepthSampler (точки, скорость, ошибка самопроверки).</summary>
    static string DepthLine(ObstacleMap map)
    {
        string src = map.activeSource == ObstacleMap.DepthSource.Texture ? "текстура" : "лучи";
        var ds = DepthSampler.Instance;
        if (ds == null) return $"Глубина: {src}  шейдер: нет";
        if (!ds.ready) return $"Глубина: {src}  шейдер: {ds.status}";
        string err = ds.calibErrorMedian >= 0f ? (ds.calibErrorMedian * 100f).ToString("F1") + " см" : "-";
        return $"Глубина: {src}  шейдер: {ds.validCount} т.  {ds.pointsPerSecond / 1000f:F0}k/с  ошибка {err}";
    }
}

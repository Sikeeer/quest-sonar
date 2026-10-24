using System.Collections;
using System.Text;
using UnityEngine;

/// <summary>
/// Панель статуса (HUD) для диагностики ObstacleMap. Без MRUK и без своих лучей.
/// Показывает: частоту кадров скана, лучи (попадания/всего), лучей в секунду,
/// статусы лучей (почему нет попаданий), высоту пола, ближайшее препятствие.
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
        float t = 0;
        while ((FindFirstObjectByType<OVRCameraRig>() == null || ObstacleMap.Instance == null) && t < 15f)
        {
            t += Time.deltaTime;
            yield return null;
        }

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null) head = rig.centerEyeAnchor;
        else if (Camera.main != null) head = Camera.main.transform;

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
        sb.Append($"Скан: {fps:F0}/с  лучи {map.lastHits}/{map.lastRays}  {map.raysPerSecond:F0} лучей/с  {map.scanMs:F1} мс  ");
        foreach (var kv in map.statusCounts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
        hud.text = sb.ToString();

        hud.color = map.nearestDistance >= 0 && map.nearestDistance < map.dangerDistance ? Color.red
                  : map.nearestDistance >= 0 && map.nearestDistance < map.warnDistance ? Color.yellow : Color.white;
    }
}

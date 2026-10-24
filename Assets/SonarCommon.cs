using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Общие мелочи для скриптов проекта: ожидание рига, поиск головы, материал со спрайтовым шейдером.
/// Сам по себе ничего не делает и в сцене не создаётся.
/// </summary>
static class SonarCommon
{
    /// <summary>Сколько секунд скрипты ждут появления рига и остальных зависимостей.</summary>
    const float WaitTimeout = 15f;

    /// <summary>
    /// Ждёт OVRCameraRig и (если задано) дополнительное условие, но не дольше WaitTimeout.
    /// Использование: <c>yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);</c>
    /// </summary>
    public static IEnumerator WaitUntilReady(Func<bool> also = null)
    {
        float t = 0f;
        while ((UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>() == null || (also != null && !also()))
               && t < WaitTimeout)
        {
            t += Time.deltaTime;
            yield return null;
        }
    }

    /// <summary>Голова = centerEyeAnchor рига; если рига нет, то Camera.main (в сцене их может быть две, поэтому это запасной вариант).</summary>
    public static Transform FindHead()
    {
        var rig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
        if (rig != null) return rig.centerEyeAnchor;
        return Camera.main != null ? Camera.main.transform : null;
    }

    /// <summary>Новый материал на шейдере Sprites/Default (цвет вершин и альфа). Если шейдера нет в билде, берётся Unlit/Color.</summary>
    public static Material NewSpriteMaterial()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogWarning("Sonar: шейдер Sprites/Default не найден в билде, запасной Unlit/Color (без прозрачности и цвета вершин). " +
                             "Добавьте Sprites/Default в Project Settings > Graphics > Always Included Shaders.");
            shader = Shader.Find("Unlit/Color");
        }
        return new Material(shader);
    }
}

using UnityEngine;
using Live2D.Cubism.Core;
using System.Collections;
using System.Collections.Generic;
using uOSC;

/// <summary>
/// Mia_Voice 파이프라인(LLM이 고른 감정/행동, mia/avatar/osc_sender.py)이 보내는 OSC 메시지를
/// 받아 Live2D Cubism 모델 파라미터/표현에 적용합니다.
///
/// 이 오브젝트엔 <see cref="uOSC.uOscServer"/> 컴포넌트가 함께 있어야 합니다(자동 추가됨).
/// uOscServer.port 를 mia/avatar/osc_sender.py 의 DEFAULT_PORT(39570)와 반드시 맞추세요.
///
/// 수신 주소 스킴:
///   /mia/param/&lt;ParamID&gt;    float    — 연속 파라미터 절대값(스무딩 적용, 예: ParamAngleX)
///   /mia/expr/&lt;ParamID&gt;     30|0     — 표현 토글(즉시 적용, 예: Param71=Hat_OnOff, Param68=IsCry)
///   /mia/trigger/&lt;name&gt;     (무시)   — 순간 제스처 코루틴 재생(nod/shake_head/wink_left/wink_right)
///
/// ParamID는 mia/avatar/avatar_actions.py의 PARAM_ID/EXPR_ID와 일치해야 하며,
/// 모델에 없는 ID는 WarnUnknown으로 한 번만 경고하고 무시합니다.
/// </summary>
[RequireComponent(typeof(uOscServer))]
public class MiaAvatarOscController : MonoBehaviour
{
    [Header("Live2D Model")]
    public CubismModel targetModel;

    [Header("Tuning")]
    [Tooltip("연속 파라미터(/mia/param/*) 스무딩. 0이면 즉시 적용. 표현 토글(/mia/expr/*)은 항상 즉시 적용.")]
    [Range(0f, 1f)]
    public float smoothing = 0.15f;

    [Header("제스처(트리거) 크기")]
    public float nodAngle = 12f;
    public float shakeAngle = 15f;

    [Header("Debug")]
    public bool debugLog = true;

    // ---- Cubism 파라미터 캐시(모델의 모든 파라미터를 ID로 조회) ----
    private Dictionary<string, CubismParameter> paramsById;
    // ---- /mia/param/* 스무딩 목표값(Update()에서 매 프레임 보간) ----
    private readonly Dictionary<string, float> targets = new Dictionary<string, float>(32);
    private readonly HashSet<string> unknownWarned = new HashSet<string>();

    private uOscServer server;

    // ======================= Unity lifecycle =======================

    void Awake()
    {
        server = GetComponent<uOscServer>();
        CacheParameters();
    }

    void OnEnable()
    {
        server.onDataReceived.AddListener(OnDataReceived);
    }

    void OnDisable()
    {
        server.onDataReceived.RemoveListener(OnDataReceived);
    }

    void Update()
    {
        if (targets.Count == 0) return;

        float t = smoothing > 0f ? 1f - Mathf.Pow(smoothing, Time.deltaTime * 60f) : 1f;
        foreach (var kv in targets)
        {
            if (!paramsById.TryGetValue(kv.Key, out var p)) continue;
            p.Value = Mathf.Lerp(p.Value, kv.Value, t);
        }
    }

    // ======================= 파라미터 캐시 =======================

    void CacheParameters()
    {
        paramsById = new Dictionary<string, CubismParameter>();

        if (targetModel == null || targetModel.Parameters == null)
        {
            Debug.LogWarning("[MiaAvatarOsc] Target Model이 비어 있습니다. Inspector에서 할당하세요.");
            return;
        }

        foreach (var p in targetModel.Parameters)
        {
            if (p == null || string.IsNullOrEmpty(p.Id)) continue;
            paramsById[p.Id] = p;
        }

        if (debugLog)
        {
            Debug.Log($"[MiaAvatarOsc] Cubism 파라미터 {paramsById.Count}개 캐시 완료.");
        }
    }

    // ======================= OSC 수신 =======================
    // uOscServer.onDataReceived는 메인 스레드에서 호출된다.

    public void OnDataReceived(Message message)
    {
        var addr = message.address;
        if (string.IsNullOrEmpty(addr) || message.values == null || message.values.Length == 0) return;
        float value = ToFloat(message.values[0]);
        if (float.IsNaN(value) || float.IsInfinity(value)) return;

        if (addr.StartsWith("/mia/param/"))
        {
            string id = addr.Substring("/mia/param/".Length);
            Set(id, value, immediate: false);
        }
        else if (addr.StartsWith("/mia/expr/"))
        {
            string id = addr.Substring("/mia/expr/".Length);
            Set(id, value, immediate: true);
        }
        else if (addr.StartsWith("/mia/trigger/"))
        {
            string name = addr.Substring("/mia/trigger/".Length);
            PlayTrigger(name);
        }
        else if (debugLog)
        {
            Debug.LogWarning($"[MiaAvatarOsc] 알 수 없는 주소: {addr}");
        }
    }

    static float ToFloat(object v)
    {
        switch (v)
        {
            case float f: return f;
            case int i: return i;
            case bool b: return b ? 1f : 0f;
            default:
                float.TryParse(v?.ToString(), out var parsed);
                return parsed;
        }
    }

    void Set(string id, float value, bool immediate)
    {
        if (!paramsById.TryGetValue(id, out var p))
        {
            WarnUnknown(id);
            return;
        }
        if (immediate)
        {
            p.Value = value;
            targets.Remove(id);
        }
        else targets[id] = value;
    }

    void WarnUnknown(string id)
    {
        if (unknownWarned.Contains(id)) return;
        unknownWarned.Add(id);
        Debug.LogWarning(
            $"[MiaAvatarOsc] 모델에 없는 파라미터/표현 ID: '{id}' — " +
            "Cubism 모델(targetModel)의 실제 파라미터 목록과 " +
            "mia/avatar/avatar_actions.py 의 PARAM_ID/EXPR_ID 매핑을 맞춰보세요.");
    }

    // ======================= 순간 제스처(트리거) =======================
    // 정적 파라미터로 표현하기 애매한 짧은 동작(끄덕임/도리도리/윙크)은 코루틴으로 재생합니다.

    void PlayTrigger(string name)
    {
        switch (name)
        {
            case "nod": StartCoroutine(Gesture("ParamAngleY", 0.6f,
                (b, p) => b - nodAngle * Mathf.Sin(p * Mathf.PI * 2f) * (1f - p))); break;
            case "shake_head": StartCoroutine(Gesture("ParamAngleX", 0.6f,
                (b, p) => b + shakeAngle * Mathf.Sin(p * Mathf.PI * 3f) * (1f - p))); break;
            case "wink_left": StartCoroutine(Gesture("ParamEyeLOpen", 0.4f, Wink)); break;
            case "wink_right": StartCoroutine(Gesture("ParamEyeROpen", 0.4f, Wink)); break;
            default:
                if (debugLog) Debug.LogWarning($"[MiaAvatarOsc] 알 수 없는 트리거: {name}");
                break;
        }
    }

    IEnumerator Gesture(string id, float duration, System.Func<float, float, float> valueAt)
    {
        if (!paramsById.TryGetValue(id, out var p)) yield break;
        float baseVal = p.Value;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float phase = Mathf.Clamp01(t / duration);
            p.Value = valueAt(baseVal, phase);
            yield return null;
        }
        p.Value = baseVal;
    }

    static float Wink(float baseValue, float phase) => Mathf.Lerp(
        baseValue, 0f, phase < 0.5f ? phase * 2f : (1f - phase) * 2f);
}

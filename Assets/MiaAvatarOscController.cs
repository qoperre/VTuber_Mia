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
///   /mia/expr/&lt;ExprID&gt;      0|1      — 표현 토글(즉시 적용, 예: Hat_OnOff, IsCry, IsAngry)
///   /mia/trigger/&lt;name&gt;     (무시)   — 순간 제스처 코루틴 재생(nod/shake_head/wink_left/wink_right)
///
/// 주의: ParamID/ExprID 문자열은 이 프로젝트 Mia 모델의 실제 Cubism 파라미터와 정확히 일치해야
/// 합니다. mia/avatar/avatar_actions.py 의 PARAM_ID/EXPR_ID 는 최선 추정치(일부는
/// MeowFaceLive2DController.cs 에서 이미 확인된 값)이므로, 모델에 없는 ID가 오면 콘솔에 경고만
/// 남기고 무시합니다(WarnUnknown) — Inspector에서 Cubism 모델의 실제 파라미터 목록을 확인해
/// avatar_actions.py 쪽을 맞춰 고치세요.
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
    }

    void Start()
    {
        CacheParameters();
    }

    void OnEnable()
    {
        if (server == null) server = GetComponent<uOscServer>();
        if (server != null) server.onDataReceived.AddListener(OnDataReceived);
    }

    void OnDisable()
    {
        if (server != null) server.onDataReceived.RemoveListener(OnDataReceived);
    }

    void Update()
    {
        if (targets.Count == 0) return;

        foreach (var kv in targets)
        {
            if (!paramsById.TryGetValue(kv.Key, out var p)) continue;
            if (smoothing > 0f)
            {
                float t = 1f - Mathf.Pow(smoothing, Time.deltaTime * 60f);
                p.Value = Mathf.Lerp(p.Value, kv.Value, t);
            }
            else
            {
                p.Value = kv.Value;
            }
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
    // uOscServer.onDataReceived 는 uOscServer.Update()(메인 스레드)에서 Invoke 되므로
    // 여기서 Cubism 파라미터에 바로 접근해도 안전합니다(MeowFaceLive2DController.cs 와 달리
    // 별도 스레드 동기화가 필요 없음).

    public void OnDataReceived(Message message)
    {
        var addr = message.address;
        if (string.IsNullOrEmpty(addr) || message.values == null || message.values.Length == 0) return;

        if (addr.StartsWith("/mia/param/"))
        {
            string id = addr.Substring("/mia/param/".Length);
            SetParamTarget(id, ToFloat(message.values[0]));
        }
        else if (addr.StartsWith("/mia/expr/"))
        {
            string id = addr.Substring("/mia/expr/".Length);
            ApplyExprImmediate(id, ToFloat(message.values[0]));
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
            case double d: return (float)d;
            case bool b: return b ? 1f : 0f;
            default:
                float.TryParse(v?.ToString(), out var parsed);
                return parsed;
        }
    }

    void SetParamTarget(string id, float value)
    {
        if (!paramsById.ContainsKey(id))
        {
            WarnUnknown(id);
            return;
        }
        targets[id] = value;
    }

    void ApplyExprImmediate(string id, float value)
    {
        if (!paramsById.TryGetValue(id, out var p))
        {
            WarnUnknown(id);
            return;
        }
        p.Value = value;
        targets.Remove(id); // Update() 스무딩 루프가 다시 덮어쓰지 않도록
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
            case "nod": StartCoroutine(GestureNod()); break;
            case "shake_head": StartCoroutine(GestureShake()); break;
            case "wink_left": StartCoroutine(GestureWink(true)); break;
            case "wink_right": StartCoroutine(GestureWink(false)); break;
            default:
                if (debugLog) Debug.LogWarning($"[MiaAvatarOsc] 알 수 없는 트리거: {name}");
                break;
        }
    }

    IEnumerator GestureNod()
    {
        if (!paramsById.TryGetValue("ParamAngleY", out var p)) yield break;
        float baseVal = p.Value;
        const float duration = 0.6f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float phase = Mathf.Clamp01(t / duration);
            p.Value = baseVal - nodAngle * Mathf.Sin(phase * Mathf.PI * 2f) * (1f - phase);
            yield return null;
        }
        p.Value = baseVal;
    }

    IEnumerator GestureShake()
    {
        if (!paramsById.TryGetValue("ParamAngleX", out var p)) yield break;
        float baseVal = p.Value;
        const float duration = 0.6f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float phase = Mathf.Clamp01(t / duration);
            p.Value = baseVal + shakeAngle * Mathf.Sin(phase * Mathf.PI * 3f) * (1f - phase);
            yield return null;
        }
        p.Value = baseVal;
    }

    IEnumerator GestureWink(bool left)
    {
        string id = left ? "ParamEyeLOpen" : "ParamEyeROpen";
        if (!paramsById.TryGetValue(id, out var p)) yield break;
        float baseVal = p.Value;
        const float duration = 0.4f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float phase = Mathf.Clamp01(t / duration);           // 0..1
            float closeAmt = phase < 0.5f ? phase * 2f : (1f - phase) * 2f; // 감았다 뜨기
            p.Value = Mathf.Lerp(baseVal, 0f, closeAmt);
            yield return null;
        }
        p.Value = baseVal;
    }
}

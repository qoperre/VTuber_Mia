using UnityEngine;
using Live2D.Cubism.Core;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// MeowFace(스마트폰 얼굴 트래킹 앱)가 UDP로 보내는 VTube Studio JSON 데이터를 직접 수신해
/// Live2D Cubism 모델 파라미터에 적용합니다.
///
/// 주의: MeowFace는 OSC가 아니라 JSON을 보내므로 uOSC(uOscServer)로는 받을 수 없습니다.
///       이 컴포넌트가 직접 UDP 소켓을 열기 때문에, 같은 오브젝트의 uOscServer 컴포넌트는
///       제거하거나 비활성화해야 합니다(포트 충돌 방지).
/// </summary>
public class MeowFaceLive2DController : MonoBehaviour
{
    [Header("Network")]
    [Tooltip("MeowFace 앱의 'Sending' 포트와 동일해야 합니다.")]
    public int port = 11573;

    [Header("Live2D Model")]
    public CubismModel targetModel;

    [Header("Tuning")]
    [Tooltip("값이 클수록 부드럽지만 반응이 느려집니다. 0이면 스무딩 없음. 권장 0.1~0.3")]
    [Range(0f, 1f)]
    public float smoothing = 0.2f;

    [Tooltip("머리 회전 각도 배율(도). 표정이 약하면 키우세요. 권장 60~90")]
    public float headAngleGain = 70f;

    [Tooltip("머리 각도를 몸통(ParamBodyAngle)에도 반영하는 비율. 0이면 몸통 고정.")]
    [Range(0f, 1f)]
    public float bodyFollow = 0.35f;

    [Header("Axis Invert (좌우/상하가 반대면 체크)")]
    public bool invertAngleX = false;
    public bool invertAngleY = false;
    public bool invertAngleZ = false;
    [Tooltip("모델과 사용자의 좌/우 눈이 반대로 깜빡이면 체크")]
    public bool swapEyes = false;

    [Header("Debug")]
    public bool debugLog = false;

    // ---- Cubism 파라미터 캐시 ----
    private CubismParameter pAngleX, pAngleY, pAngleZ;
    private CubismParameter pEyeLOpen, pEyeROpen;
    private CubismParameter pMouthOpenY, pMouthForm;
    private CubismParameter pEyeBallX, pEyeBallY;
    private CubismParameter pCheek;
    private CubismParameter pBodyX, pBodyY, pBodyZ;

    // ---- UDP 수신 ----
    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool running;
    private readonly object lockObj = new object();
    private string latestJson;          // 가장 최근 수신된 JSON(메인 스레드에서 소비)
    private bool hasNewData;

    // ---- 파싱/적용용 ----
    private readonly Dictionary<string, float> blend = new Dictionary<string, float>(64);
    private bool loggedOnce;

    // ======================= Unity lifecycle =======================

    void Start()
    {
        CacheParameters();
    }

    void OnEnable()
    {
        StartReceiver();
    }

    void OnDisable()
    {
        StopReceiver();
    }

    void OnDestroy()
    {
        StopReceiver();
    }

    void Update()
    {
        string json = null;
        lock (lockObj)
        {
            if (hasNewData)
            {
                json = latestJson;
                hasNewData = false;
            }
        }

        if (json == null) return;

        // JsonUtility는 메인 스레드에서만 안전하므로 여기서 파싱합니다.
        MeowFaceData data;
        try
        {
            data = JsonUtility.FromJson<MeowFaceData>(json);
        }
        catch (Exception e)
        {
            if (debugLog) Debug.LogWarning("[MeowFace] JSON parse failed: " + e.Message);
            return;
        }

        if (data == null) return;

        if (debugLog && !loggedOnce)
        {
            loggedOnce = true;
            Debug.Log($"[MeowFace] 첫 데이터 수신 OK. FaceFound={data.FaceFound}, BlendShapes={(data.BlendShapes != null ? data.BlendShapes.Length : 0)}개");
        }

        ApplyData(data);
    }

    // ======================= Receiver =======================

    void StartReceiver()
    {
        if (running) return;

        try
        {
            udpClient = new UdpClient(port);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MeowFace] 포트 {port} 열기 실패: {e.Message}\n" +
                           "→ 같은 오브젝트의 uOscServer 컴포넌트를 제거/비활성화했는지 확인하세요.");
            return;
        }

        running = true;
        receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        receiveThread.Start();

        if (debugLog) Debug.Log($"[MeowFace] UDP {port} 수신 시작");
    }

    void StopReceiver()
    {
        running = false;

        if (udpClient != null)
        {
            udpClient.Close();   // 블로킹 중인 Receive를 깨웁니다.
            udpClient = null;
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(500);
        }
        receiveThread = null;
    }

    void ReceiveLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] buf = udpClient.Receive(ref remote);
                if (buf == null || buf.Length == 0) continue;

                string json = Encoding.UTF8.GetString(buf);
                lock (lockObj)
                {
                    latestJson = json;   // 최신 것만 유지(밀린 프레임 버림)
                    hasNewData = true;
                }
            }
            catch (SocketException)
            {
                // 소켓 종료 시 발생 → 루프 탈출
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                if (debugLog) Debug.LogWarning("[MeowFace] receive error: " + e.Message);
            }
        }
    }

    // ======================= Apply =======================

    void CacheParameters()
    {
        if (targetModel == null || targetModel.Parameters == null)
        {
            Debug.LogWarning("[MeowFace] Target Model이 비어 있습니다. Inspector에서 할당하세요.");
            return;
        }

        foreach (var p in targetModel.Parameters)
        {
            if (p == null) continue;
            switch (p.Id)
            {
                case "ParamAngleX":    pAngleX = p; break;
                case "ParamAngleY":    pAngleY = p; break;
                case "ParamAngleZ":    pAngleZ = p; break;
                case "ParamEyeLOpen":  pEyeLOpen = p; break;
                case "ParamEyeROpen":  pEyeROpen = p; break;
                case "ParamMouthOpenY": pMouthOpenY = p; break;
                case "ParamMouthForm": pMouthForm = p; break;
                case "ParamEyeBallX":  pEyeBallX = p; break;
                case "ParamEyeBallY":  pEyeBallY = p; break;
                case "ParamCheek":     pCheek = p; break;
                case "ParamBodyAngleX": pBodyX = p; break;
                case "ParamBodyAngleY": pBodyY = p; break;
                case "ParamBodyAngleZ": pBodyZ = p; break;
            }
        }
    }

    void ApplyData(MeowFaceData data)
    {
        if (targetModel == null) return;

        // BlendShapes를 딕셔너리로 변환
        blend.Clear();
        if (data.BlendShapes != null)
        {
            for (int i = 0; i < data.BlendShapes.Length; i++)
            {
                var bs = data.BlendShapes[i];
                if (!string.IsNullOrEmpty(bs.k)) blend[bs.k] = bs.v;
            }
        }

        // --- 머리 각도 (MeowFace의 head* 상대값 사용: 0~1) ---
        float angleX = (B("headLeft") - B("headRight")) * headAngleGain;
        float angleY = (B("headUp") - B("headDown")) * headAngleGain;
        float angleZ = (B("headRollLeft") - B("headRollRight")) * headAngleGain;

        if (invertAngleX) angleX = -angleX;
        if (invertAngleY) angleY = -angleY;
        if (invertAngleZ) angleZ = -angleZ;

        angleX = Mathf.Clamp(angleX, -30f, 30f);
        angleY = Mathf.Clamp(angleY, -30f, 30f);
        angleZ = Mathf.Clamp(angleZ, -30f, 30f);
        SetParam(pAngleX, angleX);
        SetParam(pAngleY, angleY);
        SetParam(pAngleZ, angleZ);

        // 몸통도 머리를 일부 따라가게(ParamBodyAngle 범위는 보통 ±10)
        if (bodyFollow > 0f)
        {
            SetParam(pBodyX, Mathf.Clamp(angleX * bodyFollow, -10f, 10f));
            SetParam(pBodyY, Mathf.Clamp(angleY * bodyFollow, -10f, 10f));
            SetParam(pBodyZ, Mathf.Clamp(angleZ * bodyFollow, -10f, 10f));
        }

        // --- 눈 깜빡임 (blink=1이면 감은 것 → Open은 1-blink) ---
        float blinkL = B("eyeBlinkLeft");
        float blinkR = B("eyeBlinkRight");
        if (swapEyes) { var t = blinkL; blinkL = blinkR; blinkR = t; }
        SetParam(pEyeLOpen, Mathf.Clamp01(1f - blinkL));
        SetParam(pEyeROpen, Mathf.Clamp01(1f - blinkR));

        // --- 시선(눈동자) ---
        float ballX = (B("eyeLookOutLeft") + B("eyeLookInRight")) - (B("eyeLookInLeft") + B("eyeLookOutRight"));
        float ballY = (B("eyeLookUpLeft") + B("eyeLookUpRight")) - (B("eyeLookDownLeft") + B("eyeLookDownRight"));
        SetParam(pEyeBallX, Mathf.Clamp(ballX, -1f, 1f));
        SetParam(pEyeBallY, Mathf.Clamp(ballY, -1f, 1f));

        // --- 입 ---
        SetParam(pMouthOpenY, Mathf.Clamp01(B("jawOpen")));

        // MouthForm: 미소(+) / 오므림(-)
        float smile = (B("mouthSmileLeft") + B("mouthSmileRight")) * 0.5f;
        float pucker = B("mouthPucker");
        SetParam(pMouthForm, Mathf.Clamp(smile - pucker, -1f, 1f));

        // --- 볼 ---
        SetParam(pCheek, Mathf.Clamp01(B("cheekPuff")));
    }

    // 스무딩을 적용해 파라미터 값을 설정
    void SetParam(CubismParameter p, float target)
    {
        if (p == null) return;
        if (smoothing > 0f)
        {
            // 프레임레이트 보정된 지수 스무딩
            float t = 1f - Mathf.Pow(smoothing, Time.deltaTime * 60f);
            p.Value = Mathf.Lerp(p.Value, target, t);
        }
        else
        {
            p.Value = target;
        }
    }

    // BlendShape 값 조회(없으면 0)
    float B(string key)
    {
        return blend.TryGetValue(key, out float v) ? v : 0f;
    }

    // ======================= JSON DTO =======================

    [Serializable]
    public class MeowFaceData
    {
        public long Timestamp;
        public bool FaceFound;
        public Vec3 Rotation;
        public Vec3 Position;
        public BlendShape[] BlendShapes;
    }

    [Serializable]
    public struct Vec3 { public float x, y, z; }

    [Serializable]
    public struct BlendShape { public string k; public float v; }
}

using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 용접 토치 각도를 실시간 계산해서 Canvas(UI)에 각도 숫자를 표시하고,
/// 적정 범위(okMin~okMax)면 초록색/아니면 빨간색으로 표시합니다.
/// </summary>
public sealed class AngleFeedbackUI : MonoBehaviour
{
    public enum AngleReferenceMode
    {
        /// <summary>World Up(Vector3.up) 기준으로 토치 Forward와의 각도</summary>
        WorldUp = 0,

        /// <summary>referenceTransform.up 기준으로 토치 Forward와의 각도</summary>
        ReferenceUp = 1,

        /// <summary>referenceTransform.forward 기준으로 토치 Forward와의 각도</summary>
        ReferenceForward = 2,

        /// <summary>
        /// referenceTransform이 워크피스(모재)라 가정하고,
        /// 워크피스 Up(법선) 대비 토치 Forward의 각도(= ReferenceUp과 동일 개념).
        /// </summary>
        WorkpieceNormal = 3,
    }

    [Header("입력(토치)")]
    [SerializeField] private Transform torchTransform;
    [Tooltip("토치의 전진 방향으로 사용할 로컬 축. (기본: forward)")]
    [SerializeField] private Vector3 torchLocalForwardAxis = Vector3.forward;

    [Header("각도 기준")]
    [SerializeField] private AngleReferenceMode referenceMode = AngleReferenceMode.ReferenceUp;
    [Tooltip("ReferenceUp/ReferenceForward/WorkpieceNormal 모드에서 사용")]
    [SerializeField] private Transform referenceTransform;

    [Header("적정 범위(도)")]
    [SerializeField] private float okMinDegrees = 10f;
    [SerializeField] private float okMaxDegrees = 20f;

    [Header("UI")]
    [Tooltip("UGUI Text (Legacy) 사용 시 연결")]
    [SerializeField] private Text angleText;

    [Tooltip("TextMeshPro 사용 시 연결")]
    [SerializeField] private TMP_Text angleTmpText;

    [Tooltip("텍스트 외에 함께 색을 바꿀 Image(배경 등)")]
    [SerializeField] private Image statusImage;

    [Header("색상")]
    [SerializeField] private Color okColor = new Color(0.2f, 1.0f, 0.2f, 1f);
    [SerializeField] private Color badColor = new Color(1.0f, 0.2f, 0.2f, 1f);

    [Header("표시 형식")]
    [SerializeField] private bool showOneDecimal = true;
    [SerializeField] private string suffix = "°";

    [Header("(선택) 월드 타겟 따라다니기")]
    [Tooltip("지정하면 이 UI(자기 RectTransform)가 torchTransform 위치를 화면으로 따라갑니다.")]
    [SerializeField] private bool followTorchOnScreen = false;
    [SerializeField] private Camera worldToScreenCamera;
    [SerializeField] private Vector3 worldOffset = Vector3.zero;

    private RectTransform _rect;

    private void Awake()
    {
        _rect = GetComponent<RectTransform>();
        if (followTorchOnScreen && worldToScreenCamera == null)
            worldToScreenCamera = Camera.main;
    }

    private void Reset()
    {
        torchTransform = transform;
    }

    private void Update()
    {
        if (torchTransform == null)
            return;

        if (followTorchOnScreen)
            UpdateScreenFollow();

        float angle = ComputeTorchAngleDegrees();
        bool isOk = angle >= okMinDegrees && angle <= okMaxDegrees;

        ApplyUI(angle, isOk);
    }

    private void UpdateScreenFollow()
    {
        if (_rect == null || worldToScreenCamera == null)
            return;

        Vector3 worldPos = torchTransform.position + worldOffset;
        Vector3 screenPos = worldToScreenCamera.WorldToScreenPoint(worldPos);

        // Screen Space - Overlay 캔버스라면 anchoredPosition에 screenPos를 그대로 넣으면 동작합니다.
        // 다른 렌더 모드(Screen Space - Camera/World Space)인 경우엔 캔버스/카메라 설정에 맞게 변환이 필요할 수 있습니다.
        _rect.position = screenPos;
    }

    private float ComputeTorchAngleDegrees()
    {
        Vector3 torchForwardWorld = torchTransform.TransformDirection(torchLocalForwardAxis.normalized);

        Vector3 referenceDirWorld;
        switch (referenceMode)
        {
            case AngleReferenceMode.WorldUp:
                referenceDirWorld = Vector3.up;
                break;

            case AngleReferenceMode.ReferenceForward:
                referenceDirWorld = referenceTransform != null ? referenceTransform.forward : Vector3.forward;
                break;

            case AngleReferenceMode.ReferenceUp:
            case AngleReferenceMode.WorkpieceNormal:
            default:
                referenceDirWorld = referenceTransform != null ? referenceTransform.up : Vector3.up;
                break;
        }

        // 0~180 범위 각도
        return Vector3.Angle(torchForwardWorld, referenceDirWorld);
    }

    private void ApplyUI(float angleDegrees, bool isOk)
    {
        string formatted = showOneDecimal ? angleDegrees.ToString("F1") : Mathf.RoundToInt(angleDegrees).ToString();
        string textValue = formatted + suffix;

        Color c = isOk ? okColor : badColor;

        if (angleText != null)
        {
            angleText.text = textValue;
            angleText.color = c;
        }

        if (angleTmpText != null)
        {
            angleTmpText.text = textValue;
            angleTmpText.color = c;
        }

        if (statusImage != null)
            statusImage.color = c;
    }

    // 외부에서 범위를 런타임에 바꾸고 싶을 때 사용
    public void SetOkRange(float minDegrees, float maxDegrees)
    {
        okMinDegrees = minDegrees;
        okMaxDegrees = maxDegrees;
    }
}

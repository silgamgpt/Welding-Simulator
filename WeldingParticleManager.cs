using UnityEngine;

/// <summary>
/// 용접 상태에 따라 스파크 파티클을 재생하고,
/// 기준 Transform(예: 카메라/플레이어)과 용접 지점 사이 거리에 따라 방출량을 조절합니다.
/// </summary>
public sealed class WeldingParticleManager : MonoBehaviour
{
    [Header("Particle")]
    [SerializeField] private ParticleSystem sparks;

    [Tooltip("sparks가 비어있을 때 용접 시작 시 생성할 파티클 프리팹(선택)")]
    [SerializeField] private ParticleSystem sparksPrefab;

    [Tooltip("sparks가 비어있고 sparksPrefab이 있으면 자동 생성")]
    [SerializeField] private bool instantiateIfMissing = true;

    [Header("Transforms")]
    [Tooltip("파티클이 생성될 용접 지점(토치 끝 등)")]
    [SerializeField] private Transform weldPoint;

    [Tooltip("거리 기준 Transform(보통 Main Camera 또는 플레이어)")]
    [SerializeField] private Transform distanceReference;

    [Header("Distance -> Emission")]
    [Min(0f)]
    [Tooltip("이 거리 이하에서는 최대 방출량(가까울수록 파티클 많음)")]
    [SerializeField] private float minDistance = 0.5f;

    [Min(0.0001f)]
    [Tooltip("이 거리 이상에서는 최소 방출량")]
    [SerializeField] private float maxDistance = 5f;

    [Min(0f)]
    [Tooltip("최대 거리 근처에서의 방출량(초당)")]
    [SerializeField] private float minRateOverTime = 0f;

    [Min(0f)]
    [Tooltip("최소 거리 근처에서의 방출량(초당)")]
    [SerializeField] private float maxRateOverTime = 200f;

    [Tooltip("입력값=near01(0=멀다, 1=가깝다) / 출력값=방출량 보간 가중치")]
    [SerializeField] private AnimationCurve near01ToRateT = AnimationCurve.Linear(0f, 0f, 1f, 1f);

    [Header("Behaviour")]
    [Tooltip("용접 지점으로 파티클 위치를 매 프레임 따라가게 함")]
    [SerializeField] private bool followWeldPoint = true;

    [Tooltip("용접 종료 시 StopEmittingAndClear로 바로 정리")]
    [SerializeField] private bool clearOnStop = false;

    [Tooltip("distanceReference가 비어있으면 런타임에 Camera.main을 자동으로 사용")]
    [SerializeField] private bool autoFindMainCamera = true;

    [Header("Quick Test (Optional)")]
    [Tooltip("에디터에서 빠르게 테스트용. None이면 입력을 사용하지 않음.")]
    [SerializeField] private KeyCode holdToWeldKey = KeyCode.None;

    private bool _isWelding;

    private ParticleSystem.EmissionModule _emission;
    private bool _emissionCached;

    private void Awake()
    {
        EnsureSparksInstance();
    }

    private void OnEnable()
    {
        if (autoFindMainCamera && distanceReference == null)
        {
            var cam = Camera.main;
            if (cam != null) distanceReference = cam.transform;
        }

        // 시작 시 의도치 않게 방출되는 걸 막기 위해 기본은 0으로 둡니다.
        ApplyEmissionRate(0f);
    }

    private void Update()
    {
        if (holdToWeldKey != KeyCode.None)
        {
            if (Input.GetKey(holdToWeldKey)) StartWelding();
            else StopWelding();
        }

        if (!_isWelding) return;

        if (followWeldPoint && weldPoint != null)
        {
            transform.position = weldPoint.position;
            transform.rotation = weldPoint.rotation;
        }

        UpdateEmissionByDistance();
    }

    /// <summary>외부(용접 시스템/애니메이션 이벤트 등)에서 호출: 용접 시작</summary>
    public void StartWelding()
    {
        if (_isWelding) return;
        _isWelding = true;

        EnsureSparksInstance();
        if (sparks == null) return;

        if (followWeldPoint && weldPoint != null)
        {
            transform.position = weldPoint.position;
            transform.rotation = weldPoint.rotation;
        }

        UpdateEmissionByDistance();
        if (!sparks.isPlaying) sparks.Play(true);
    }

    /// <summary>외부(용접 시스템/애니메이션 이벤트 등)에서 호출: 용접 종료</summary>
    public void StopWelding()
    {
        if (!_isWelding) return;
        _isWelding = false;

        ApplyEmissionRate(0f);

        if (sparks == null) return;

        if (clearOnStop) sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        else sparks.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private void UpdateEmissionByDistance()
    {
        if (sparks == null) return;
        if (weldPoint == null && followWeldPoint) return;

        var reference = distanceReference;
        if (reference == null && autoFindMainCamera)
        {
            var cam = Camera.main;
            if (cam != null) reference = cam.transform;
        }

        if (reference == null)
        {
            // 거리 기준이 없으면 최대 방출로 고정(“가까움”으로 취급)
            ApplyEmissionRate(maxRateOverTime);
            return;
        }

        Vector3 weldPos = followWeldPoint && weldPoint != null ? weldPoint.position : transform.position;
        float distance = Vector3.Distance(reference.position, weldPos);

        // near01: 0(멀다) ~ 1(가깝다)
        float safeMax = Mathf.Max(maxDistance, minDistance + 0.0001f);
        float near01 = Mathf.InverseLerp(safeMax, minDistance, distance);
        float t = Mathf.Clamp01(near01ToRateT.Evaluate(near01));
        float rate = Mathf.Lerp(minRateOverTime, maxRateOverTime, t);

        ApplyEmissionRate(rate);
    }

    private void ApplyEmissionRate(float rateOverTime)
    {
        EnsureSparksInstance();
        if (sparks == null) return;
        CacheEmissionIfNeeded();

        var rot = _emission.rateOverTime;
        rot.mode = ParticleSystemCurveMode.Constant;
        rot.constant = Mathf.Max(0f, rateOverTime);
        _emission.rateOverTime = rot;
    }

    private void EnsureSparksInstance()
    {
        if (sparks != null)
        {
            CacheEmissionIfNeeded();
            return;
        }

        if (!instantiateIfMissing) return;
        if (sparksPrefab == null) return;

        Vector3 pos = weldPoint != null ? weldPoint.position : transform.position;
        Quaternion rot = weldPoint != null ? weldPoint.rotation : transform.rotation;

        sparks = Instantiate(sparksPrefab, pos, rot, transform);
        CacheEmissionIfNeeded();
    }

    private void CacheEmissionIfNeeded()
    {
        if (_emissionCached) return;
        if (sparks == null) return;

        _emission = sparks.emission;
        _emissionCached = true;
    }
}


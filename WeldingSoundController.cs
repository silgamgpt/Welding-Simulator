using UnityEngine;

/// <summary>
/// 아크(용접) 사운드를 루프로 재생하고, 용접 강도(0~1)에 따라 볼륨/피치를 조절합니다.
///
/// 사용 방법(예시):
/// - 씬 오브젝트에 이 컴포넌트 추가
/// - AudioSource가 없으면 자동으로 요구(RequireComponent)
/// - arcLoopClip에 루프용 아크 사운드 지정
/// - 용접 시작/종료 시 SetArcActive(true/false) 호출
/// - 매 프레임(또는 강도 변경 시) SetWeldStrength(strength01) 호출
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class WeldingSoundController : MonoBehaviour
{
    [Header("Clips")]
    [Tooltip("아크(접 아크) 루프 사운드 클립")]
    [SerializeField] private AudioClip arcLoopClip;

    [Header("Strength (0~1)")]
    [Tooltip("외부에서 넘겨주는 용접 강도(0~1). SetWeldStrength로도 설정 가능.")]
    [Range(0f, 1f)]
    [SerializeField] private float weldStrength01;

    [Header("Mapping")]
    [Tooltip("강도(0~1) -> 볼륨(0~1) 매핑 커브 (x: strength, y: volume)")]
    [SerializeField] private AnimationCurve volumeByStrength = AnimationCurve.Linear(0f, 0.1f, 1f, 1f);

    [Tooltip("강도(0~1) -> 피치 매핑 커브 (x: strength, y: pitch)")]
    [SerializeField] private AnimationCurve pitchByStrength = AnimationCurve.Linear(0f, 0.9f, 1f, 1.15f);

    [Header("Smoothing")]
    [Tooltip("볼륨이 목표값으로 따라가는 속도(초당). 클수록 빠르게 반응.")]
    [Min(0f)]
    [SerializeField] private float volumeFollowSpeed = 8f;

    [Tooltip("피치가 목표값으로 따라가는 속도(초당). 클수록 빠르게 반응.")]
    [Min(0f)]
    [SerializeField] private float pitchFollowSpeed = 8f;

    [Tooltip("강도 입력값 자체를 스무딩(초당). 클수록 빠르게 반응.")]
    [Min(0f)]
    [SerializeField] private float strengthFollowSpeed = 20f;

    [Header("Arc texture (optional)")]
    [Tooltip("강도에 비례해서 약간의 피치 흔들림(지터)을 추가합니다.")]
    [Min(0f)]
    [SerializeField] private float pitchJitter = 0.03f;

    [Tooltip("강도에 비례해서 약간의 볼륨 흔들림(지터)을 추가합니다.")]
    [Min(0f)]
    [SerializeField] private float volumeJitter = 0.03f;

    [Tooltip("지터 변화 속도. 클수록 더 빠르게 흔들립니다.")]
    [Min(0f)]
    [SerializeField] private float jitterSpeed = 12f;

    [Header("3D Audio (optional)")]
    [Tooltip("AudioSource를 3D로 사용할지 여부. 켜면 spatialBlend=1로 설정됩니다.")]
    [SerializeField] private bool use3DAudio = true;

    [Tooltip("3D 사운드 최대 거리(AudioSource.maxDistance)")]
    [Min(0.01f)]
    [SerializeField] private float maxDistance = 10f;

    private AudioSource _source;
    private bool _arcActive;
    private float _smoothedStrength01;
    private float _jitterSeed;

    /// <summary>현재 용접 강도(0~1)</summary>
    public float CurrentStrength01 => weldStrength01;

    /// <summary>현재 아크 활성 여부</summary>
    public bool IsArcActive => _arcActive;

    private void Awake()
    {
        _source = GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _source.clip = arcLoopClip;

        if (use3DAudio)
        {
            _source.spatialBlend = 1f;
            _source.maxDistance = maxDistance;
        }
        else
        {
            _source.spatialBlend = 0f;
        }

        _source.volume = 0f;
        _source.pitch = 1f;

        _smoothedStrength01 = Mathf.Clamp01(weldStrength01);
        _jitterSeed = Random.value * 1000f;
    }

    private void OnValidate()
    {
        weldStrength01 = Mathf.Clamp01(weldStrength01);
        if (maxDistance < 0.01f) maxDistance = 0.01f;
    }

    private void Update()
    {
        // 강도 입력 스무딩
        float targetStrength = Mathf.Clamp01(weldStrength01);
        _smoothedStrength01 = ExpFollow(_smoothedStrength01, targetStrength, strengthFollowSpeed, Time.deltaTime);

        // 목표 볼륨/피치 산출
        float targetVolume = volumeByStrength != null ? volumeByStrength.Evaluate(_smoothedStrength01) : _smoothedStrength01;
        float targetPitch = pitchByStrength != null ? pitchByStrength.Evaluate(_smoothedStrength01) : Mathf.Lerp(0.9f, 1.15f, _smoothedStrength01);

        targetVolume = Mathf.Clamp01(targetVolume);
        targetPitch = Mathf.Clamp(targetPitch, -3f, 3f); // Unity pitch 제한 범위 고려(보수적으로)

        // 지터(자연스러운 아크 느낌) - 아크 활성일 때만
        if (_arcActive && (pitchJitter > 0f || volumeJitter > 0f))
        {
            float t = (Time.time * jitterSpeed) + _jitterSeed;
            float n1 = Mathf.PerlinNoise(t, 0.123f) * 2f - 1f;  // -1..1
            float n2 = Mathf.PerlinNoise(0.456f, t) * 2f - 1f;  // -1..1

            float jitterScale = _smoothedStrength01; // 강할수록 더 "거칠게"
            targetPitch += n1 * pitchJitter * jitterScale;
            targetVolume += n2 * volumeJitter * jitterScale;
        }

        // 아크가 꺼졌으면 볼륨 목표를 0으로 강제(페이드아웃)
        if (!_arcActive)
        {
            targetVolume = 0f;
        }

        // 재생/정지 제어
        EnsurePlayingIfNeeded();

        // 볼륨/피치 스무딩 적용
        _source.volume = ExpFollow(_source.volume, targetVolume, volumeFollowSpeed, Time.deltaTime);
        _source.pitch = ExpFollow(_source.pitch, targetPitch, pitchFollowSpeed, Time.deltaTime);

        // 완전히 꺼진 상태면 정지(불필요한 오디오 업데이트 방지)
        if (!_arcActive && _source.isPlaying && _source.volume <= 0.001f)
        {
            _source.Stop();
        }
    }

    /// <summary>
    /// 용접 강도를 설정합니다. 입력은 0~1로 클램프됩니다.
    /// </summary>
    public void SetWeldStrength(float strength01)
    {
        weldStrength01 = Mathf.Clamp01(strength01);
    }

    /// <summary>
    /// 아크(용접) 활성 여부를 설정합니다.
    /// true면 루프 재생(볼륨은 강도에 의해 올라감), false면 페이드아웃 후 정지합니다.
    /// </summary>
    public void SetArcActive(bool active)
    {
        _arcActive = active;
        if (_arcActive)
        {
            EnsurePlayingIfNeeded(forceRestart: false);
        }
    }

    /// <summary>아크 시작</summary>
    public void StartArc() => SetArcActive(true);

    /// <summary>아크 종료</summary>
    public void StopArc() => SetArcActive(false);

    private void EnsurePlayingIfNeeded(bool forceRestart = false)
    {
        if (!_arcActive)
        {
            return;
        }

        if (arcLoopClip != null && _source.clip != arcLoopClip)
        {
            _source.clip = arcLoopClip;
        }

        if (forceRestart)
        {
            _source.Stop();
        }

        if (!_source.isPlaying && _source.clip != null)
        {
            _source.Play();
        }
    }

    /// <summary>
    /// 지수 형태로 목표값을 따라가는 스무딩.
    /// followSpeed가 0이면 즉시 목표값으로 점프합니다.
    /// </summary>
    private static float ExpFollow(float current, float target, float followSpeed, float dt)
    {
        if (followSpeed <= 0f || dt <= 0f)
        {
            return target;
        }

        // 1 - e^(-k*dt): 프레임레이트에 강한 스무딩 계수
        float a = 1f - Mathf.Exp(-followSpeed * dt);
        return Mathf.LerpUnclamped(current, target, a);
    }
}

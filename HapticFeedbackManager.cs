using System;

namespace WeldingSimulator.Haptics
{
    /// <summary>
    /// 실제 진동 출력(컨트롤러/장치)에 대한 최소 인터페이스.
    /// - Unity(OpenXR/XR)든, XInput/SDL 등 네이티브든 이 인터페이스만 구현해서 주입하면 됨.
    /// </summary>
    public interface IHapticsOutput
    {
        /// <summary>진동 출력 가능 여부(장치 연결/지원 여부).</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 진동 임펄스 전송.
        /// amplitude: 0..1, durationSeconds: 초 단위(0보다 커야 함).
        /// </summary>
        void SendImpulse(float amplitude, float durationSeconds);

        /// <summary>진동 즉시 정지(지원 시).</summary>
        void Stop();
    }

    /// <summary>
    /// 용접 중 기본 진동을 주고, 품질이 나쁠수록 진동 강도를 높이는 매니저.
    ///
    /// 품질 값(0..1):
    /// - 1에 가까울수록 품질 좋음(진동 약함)
    /// - 0에 가까울수록 품질 나쁨(진동 강함)
    ///
    /// 사용 방법(엔진/루프 공통):
    /// - SetWelding(true/false)로 용접 상태 갱신
    /// - SetQuality01(0..1)로 품질 갱신
    /// - 매 프레임/틱마다 Update(deltaTimeSeconds) 호출
    /// </summary>
    public sealed class HapticFeedbackManager
    {
        private readonly IHapticsOutput _output;

        private float _currentAmplitude;
        private float _pulseAccumulator;

        public HapticFeedbackManager(IHapticsOutput output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>용접 중인지 여부.</summary>
        public bool IsWelding { get; private set; }

        /// <summary>용접 품질(0..1). 1=좋음, 0=나쁨.</summary>
        public float Quality01 { get; private set; } = 1f;

        /// <summary>용접 중 최소(기본) 진동 강도(0..1).</summary>
        public float BaseAmplitude01 { get; set; } = 0.15f;

        /// <summary>품질이 매우 나쁠 때 최대 진동 강도(0..1).</summary>
        public float MaxAmplitude01 { get; set; } = 0.9f;

        /// <summary>
        /// 품질 나쁨(1-Quality)을 얼마나 공격적으로 증폭할지(>= 1 권장).
        /// - 1: 선형
        /// - 2: 나쁠수록 더 급격히 증가
        /// </summary>
        public float BadQualityExponent { get; set; } = 2.0f;

        /// <summary>
        /// 펄스 빈도(Hz). 일부 장치는 "지속 진동"보다 펄스가 안정적이라 기본값을 둠.
        /// </summary>
        public float PulseHz { get; set; } = 60f;

        /// <summary>각 펄스의 길이(초). PulseHz 기반 자동 계산에 곱해지는 배율.</summary>
        public float PulseDurationFactor { get; set; } = 0.75f;

        /// <summary>강도 변화의 완만함(초). 값이 클수록 천천히 변함.</summary>
        public float SmoothingSeconds { get; set; } = 0.05f;

        /// <summary>용접 상태 설정.</summary>
        public void SetWelding(bool welding)
        {
            if (IsWelding == welding) return;

            IsWelding = welding;
            _pulseAccumulator = 0f;

            if (!IsWelding)
            {
                _currentAmplitude = 0f;
                _output.Stop();
            }
        }

        /// <summary>품질 설정(0..1). 값 범위는 자동 클램프.</summary>
        public void SetQuality01(float quality01)
        {
            Quality01 = Clamp01(quality01);
        }

        /// <summary>
        /// 외부 게임 루프/프레임 루프에서 호출.
        /// deltaTimeSeconds는 프레임 간 시간(초)이어야 함.
        /// </summary>
        public void Update(float deltaTimeSeconds)
        {
            if (deltaTimeSeconds <= 0f) return;
            if (!_output.IsAvailable) return;

            if (!IsWelding)
            {
                // 용접이 아니면 안전하게 정지
                _output.Stop();
                return;
            }

            var baseAmp = Clamp01(BaseAmplitude01);
            var maxAmp = Clamp01(MaxAmplitude01);
            if (maxAmp < baseAmp) maxAmp = baseAmp;

            var badness = 1f - Clamp01(Quality01); // 0(좋음) ~ 1(나쁨)
            var exponent = BadQualityExponent < 0.0001f ? 0.0001f : BadQualityExponent;
            var shaped = PowSafe(badness, exponent);

            var targetAmplitude = baseAmp + (maxAmp - baseAmp) * shaped;
            targetAmplitude = Clamp01(targetAmplitude);

            // 간단한 1차 지연(프레임 독립)으로 스무딩
            var smooth = SmoothingSeconds < 0f ? 0f : SmoothingSeconds;
            var alpha = smooth <= 0.0001f ? 1f : (deltaTimeSeconds / (smooth + deltaTimeSeconds));
            _currentAmplitude = Lerp(_currentAmplitude, targetAmplitude, alpha);

            // 펄스 기반 출력
            var hz = PulseHz < 1f ? 1f : PulseHz;
            var period = 1f / hz;
            _pulseAccumulator += deltaTimeSeconds;

            // 여러 프레임이 밀려도 펄스를 "가능한 만큼" 내보냄(최대 3개로 제한해 폭주 방지)
            var guard = 0;
            while (_pulseAccumulator >= period && guard++ < 3)
            {
                _pulseAccumulator -= period;

                var duration = period * Clamp01(PulseDurationFactor);
                if (duration <= 0.0001f) duration = period * 0.5f;

                _output.SendImpulse(_currentAmplitude, duration);
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        private static float PowSafe(float x, float p)
        {
            // .NET 표준 MathF를 쓰되, 타깃 프레임워크/런타임에서 MathF가 없을 가능성을 최소화하려고 Math로 폴백
            // (대부분의 현대 런타임에서는 문제 없음)
            try
            {
                return (float)Math.Pow(x, p);
            }
            catch
            {
                return x; // 최악의 경우 선형
            }
        }
    }

    /// <summary>
    /// 아무 것도 하지 않는 출력(테스트/서버/에디터용).
    /// </summary>
    public sealed class NullHapticsOutput : IHapticsOutput
    {
        public static readonly NullHapticsOutput Instance = new NullHapticsOutput();
        private NullHapticsOutput() { }
        public bool IsAvailable => false;
        public void SendImpulse(float amplitude, float durationSeconds) { }
        public void Stop() { }
    }
}

#if UNITY_2019_1_OR_NEWER
// Unity용 어댑터(콘솔/.NET 빌드에서는 컴파일 제외)
namespace WeldingSimulator.Haptics.Unity
{
    using UnityEngine;
    using UnityEngine.XR;

    /// <summary>
    /// Unity XR(InputDevice.SendHapticImpulse)를 이용해 컨트롤러 진동을 출력하는 컴포넌트.
    /// - Inspector에서 손(좌/우) 선택 가능
    /// - 외부에서 SetWelding / SetQuality01 호출만 해주면 동작
    /// </summary>
    public sealed class HapticFeedbackManagerBehaviour : MonoBehaviour
    {
        public enum Hand
        {
            Left,
            Right
        }

        [Header("Target")]
        [SerializeField] private Hand targetHand = Hand.Right;

        [Header("Welding State (Optional)")]
        [Tooltip("외부 시스템이 상태를 직접 주입할 수도 있고, 이 값을 Inspector/디버그로만 쓸 수도 있습니다.")]
        [SerializeField] private bool isWelding;

        [Range(0f, 1f)]
        [Tooltip("0=나쁨, 1=좋음. 외부에서 갱신하는 것을 권장.")]
        [SerializeField] private float quality01 = 1f;

        [Header("Tuning")]
        [Range(0f, 1f)] public float baseAmplitude01 = 0.15f;
        [Range(0f, 1f)] public float maxAmplitude01 = 0.9f;
        [Min(0.1f)] public float badQualityExponent = 2.0f;
        [Min(1f)] public float pulseHz = 60f;
        [Range(0f, 1f)] public float pulseDurationFactor = 0.75f;
        [Min(0f)] public float smoothingSeconds = 0.05f;

        private HapticFeedbackManager _manager;
        private UnityXrHapticsOutput _output;

        private void Awake()
        {
            _output = new UnityXrHapticsOutput(targetHand);
            _manager = new HapticFeedbackManager(_output)
            {
                BaseAmplitude01 = baseAmplitude01,
                MaxAmplitude01 = maxAmplitude01,
                BadQualityExponent = badQualityExponent,
                PulseHz = pulseHz,
                PulseDurationFactor = pulseDurationFactor,
                SmoothingSeconds = smoothingSeconds
            };
        }

        private void OnEnable()
        {
            // enable 시점에 상태 반영
            _manager.SetWelding(isWelding);
            _manager.SetQuality01(quality01);
        }

        private void OnDisable()
        {
            _manager?.SetWelding(false);
        }

        private void Update()
        {
            // Inspector 값을 통해 디버깅 가능하게 동기화
            SyncTuning();
            _manager.SetWelding(isWelding);
            _manager.SetQuality01(quality01);
            _manager.Update(Time.deltaTime);
        }

        private void SyncTuning()
        {
            if (_manager == null) return;

            _manager.BaseAmplitude01 = baseAmplitude01;
            _manager.MaxAmplitude01 = maxAmplitude01;
            _manager.BadQualityExponent = badQualityExponent;
            _manager.PulseHz = pulseHz;
            _manager.PulseDurationFactor = pulseDurationFactor;
            _manager.SmoothingSeconds = smoothingSeconds;
        }

        /// <summary>외부 시스템에서 용접 시작/종료를 호출.</summary>
        public void SetWelding(bool welding) => isWelding = welding;

        /// <summary>외부 시스템에서 품질(0..1)을 호출.</summary>
        public void SetQuality01(float q01) => quality01 = Mathf.Clamp01(q01);

        private sealed class UnityXrHapticsOutput : IHapticsOutput
        {
            private readonly Hand _hand;
            private InputDevice _device;
            private float _nextSearchAt;

            public UnityXrHapticsOutput(Hand hand)
            {
                _hand = hand;
            }

            public bool IsAvailable
            {
                get
                {
                    EnsureDevice();
                    if (!_device.isValid) return false;
                    return _device.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse;
                }
            }

            public void SendImpulse(float amplitude, float durationSeconds)
            {
                EnsureDevice();
                if (!_device.isValid) return;
                if (durationSeconds <= 0f) return;

                // channel 0 사용(대부분의 XR 컨트롤러가 단일 채널)
                _device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude), durationSeconds);
            }

            public void Stop()
            {
                EnsureDevice();
                if (!_device.isValid) return;
                _device.StopHaptics();
            }

            private void EnsureDevice()
            {
                // 매 프레임 전체 검색은 피하고, 최초/무효화 시에만 다시 찾음
                if (_device.isValid) return;

                var now = Time.realtimeSinceStartup;
                if (now < _nextSearchAt) return;
                _nextSearchAt = now + 1.0f; // 1초에 한 번만 재검색(연결 대기/재연결 대응)

                var ch = InputDeviceCharacteristics.Controller |
                         (_hand == Hand.Left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right);
                var devices = new System.Collections.Generic.List<InputDevice>(4);
                InputDevices.GetDevicesWithCharacteristics(ch, devices);
                if (devices.Count > 0)
                {
                    _device = devices[0];
                }
            }
        }
    }
}
#endif

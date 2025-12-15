using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

/// <summary>
/// Meta Quest(또는 OpenXR) 컨트롤러의 트리거 입력으로 용접 아크를 On/Off 합니다.
/// - 트리거 당김: 아크 On
/// - 트리거 놓음: 아크 Off
///
/// 선택 옵션:
/// - XR Grab(잡고 있을 때만) 조건을 걸고 싶으면 requireGrab = true 로 설정하세요.
///   XR Interaction Toolkit이 프로젝트에 있을 경우 XRGrabInteractable의 isSelected를 리플렉션으로 감지합니다(직접 참조 X).
/// </summary>
public sealed class WeldingTorchController : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("Quest에서 보통 RightHand를 토치 손으로 사용합니다.")]
    public XRNode controllerNode = XRNode.RightHand;

    [Range(0f, 1f)]
    [Tooltip("이 값 이상이면 트리거를 '당김'으로 판정합니다.")]
    public float triggerOnThreshold = 0.55f;

    [Range(0f, 1f)]
    [Tooltip("이 값 이하이면 트리거를 '놓음'으로 판정합니다. (히스테리시스)")]
    public float triggerOffThreshold = 0.45f;

    [Header("Grab gating (optional)")]
    [Tooltip("토치를 잡고 있을 때만 아크가 켜지도록 제한합니다.")]
    public bool requireGrab = true;

    [Tooltip("명시적으로 Grab 컴포넌트를 지정하고 싶으면 여기에 넣으세요(예: XRGrabInteractable). 비우면 같은 오브젝트에서 자동 탐색합니다.")]
    public Component grabComponent;

    [Header("Arc Outputs")]
    [Tooltip("아크 관련 오브젝트(파티클/라이트/사운드 부모). On일 때 SetActive(true) 됩니다.")]
    public GameObject arcRoot;

    [Tooltip("아크 On/Off에 맞춰 Play/Stop 시킬 ParticleSystem들")]
    public ParticleSystem[] arcParticles;

    [Tooltip("아크 On/Off에 맞춰 enable/disable 할 라이트")]
    public Light arcLight;

    [Tooltip("아크 On/Off에 맞춰 Play/Stop 할 오디오")]
    public AudioSource arcAudio;

    [Header("Haptics (optional)")]
    [Range(0f, 1f)]
    public float hapticAmplitude = 0.35f;

    [Min(0f)]
    public float hapticDurationSeconds = 0.05f;

    [Header("Events")]
    public UnityEvent onArcStarted;
    public UnityEvent onArcStopped;

    [Header("Debug")]
    public bool logStateChanges;

    public bool IsArcOn => _arcOn;

    private InputDevice _device;
    private bool _arcOn;

    private void Awake()
    {
        AcquireDevice();
        SetArc(false, force: true);
    }

    private void OnEnable()
    {
        InputDevices.deviceConnected += OnDeviceConnected;
        InputDevices.deviceDisconnected += OnDeviceDisconnected;
        AcquireDevice();
    }

    private void OnDisable()
    {
        InputDevices.deviceConnected -= OnDeviceConnected;
        InputDevices.deviceDisconnected -= OnDeviceDisconnected;
        SetArc(false, force: true);
    }

    private void Update()
    {
        if (!_device.isValid)
            AcquireDevice();

        if (!_device.isValid)
            return;

        if (requireGrab && !IsGrabbed())
        {
            if (_arcOn)
                SetArc(false);
            return;
        }

        if (!_device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue))
            triggerValue = 0f;

        if (!_arcOn)
        {
            if (triggerValue >= triggerOnThreshold)
                SetArc(true);
        }
        else
        {
            if (triggerValue <= triggerOffThreshold)
                SetArc(false);
        }
    }

    private void SetArc(bool on, bool force = false)
    {
        if (!force && _arcOn == on)
            return;

        _arcOn = on;

        if (arcRoot != null)
            arcRoot.SetActive(on);

        if (arcParticles != null)
        {
            for (int i = 0; i < arcParticles.Length; i++)
            {
                var ps = arcParticles[i];
                if (ps == null) continue;
                if (on) ps.Play(true);
                else ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        if (arcLight != null)
            arcLight.enabled = on;

        if (arcAudio != null)
        {
            if (on)
            {
                if (!arcAudio.isPlaying)
                    arcAudio.Play();
            }
            else
            {
                if (arcAudio.isPlaying)
                    arcAudio.Stop();
            }
        }

        if (logStateChanges)
            Debug.Log($"[WeldingTorchController] Arc {(on ? "ON" : "OFF")} ({name})", this);

        if (on)
        {
            TryHaptics();
            onArcStarted?.Invoke();
        }
        else
        {
            onArcStopped?.Invoke();
        }
    }

    private void AcquireDevice()
    {
        _device = InputDevices.GetDeviceAtXRNode(controllerNode);
    }

    private void OnDeviceConnected(InputDevice device)
    {
        // 해당 노드의 디바이스가 다시 잡히도록 갱신
        AcquireDevice();
    }

    private void OnDeviceDisconnected(InputDevice device)
    {
        if (_device == device)
            _device = default;
    }

    private void TryHaptics()
    {
        if (!_device.isValid)
            return;

        if (hapticAmplitude <= 0f || hapticDurationSeconds <= 0f)
            return;

        // 일부 디바이스는 지원하지 않을 수 있음(지원 안 하면 false 반환)
        _device.SendHapticImpulse(0u, Mathf.Clamp01(hapticAmplitude), hapticDurationSeconds);
    }

    private bool IsGrabbed()
    {
        var source = grabComponent != null ? grabComponent : TryFindGrabComponent();
        if (source == null)
            return false;

        // XR Interaction Toolkit의 XRBaseInteractable/XRGrabInteractable에는 isSelected(bool) 프로퍼티가 존재합니다.
        // 패키지 의존성을 피하기 위해 리플렉션으로 읽습니다.
        try
        {
            var t = source.GetType();
            var prop = t.GetProperty("isSelected", BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(source);

            // 다른 구현체 대비: isGrabbed / isHeld 같은 이름도 시도
            prop = t.GetProperty("isGrabbed", BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(source);

            prop = t.GetProperty("isHeld", BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(source);
        }
        catch
        {
            // grab 상태 확인 실패 시 "잡지 않음"으로 처리
        }

        return false;
    }

    private Component TryFindGrabComponent()
    {
        // 1) 같은 GameObject에 XRGrabInteractable이 있으면 그걸 우선 사용
        var xrGrabType = FindTypeByName(
            "UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable",
            "UnityEngine.XR.Interaction.Toolkit.XRBaseInteractable");

        if (xrGrabType != null)
        {
            var c = GetComponent(xrGrabType);
            if (c != null)
                return c;
        }

        // 2) 후보가 없으면 컴포넌트 이름 기반으로 fallback 탐색
        var comps = GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null) continue;
            var n = c.GetType().Name;
            if (string.Equals(n, "XRGrabInteractable", StringComparison.Ordinal) ||
                string.Equals(n, "XRBaseInteractable", StringComparison.Ordinal))
                return c;
        }

        return null;
    }

    private static Type FindTypeByName(params string[] fullNames)
    {
        // Unity에서는 Type.GetType이 어셈블리명이 없으면 실패하는 경우가 많아서
        // 로드된 어셈블리를 순회하며 타입을 찾습니다.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < fullNames.Length; i++)
        {
            var fullName = fullNames[i];
            if (string.IsNullOrWhiteSpace(fullName))
                continue;

            for (int a = 0; a < assemblies.Length; a++)
            {
                var asm = assemblies[a];
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null)
                        return t;
                }
                catch
                {
                    // 무시
                }
            }
        }
        return null;
    }
}


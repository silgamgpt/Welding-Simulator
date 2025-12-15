using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 용접 토치가 금속 표면을 따라 이동할 때, 표면에 투영된 경로를 기반으로
/// 실시간으로 용접 비드(튜브/오벌 단면) 메쉬를 생성합니다.
///
/// 사용법(요약)
/// - 빈 GameObject에 붙이고, torchTip(토치 끝 Transform) 지정
/// - MeshFilter/MeshRenderer는 자동으로 붙습니다(없으면 추가)
/// - 용접 시작/종료: StartWelding()/StopWelding() 또는 isWelding 토글
/// - surfaceMask에 용접 대상(금속) 콜라이더 레이어를 지정
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class WeldingBeadGenerator : MonoBehaviour
{
    [Header("Inputs")]
    public Transform torchTip;

    [Tooltip("표면을 찾기 위한 Raycast 대상 레이어")]
    public LayerMask surfaceMask = ~0;

    [Tooltip("Raycast 방향. 비워두면 -torchTip.up 사용")]
    public Vector3 overrideRayDirection = Vector3.zero;

    [Tooltip("Raycast 최대 거리")]
    public float raycastDistance = 0.25f;

    [Header("Sampling")]
    [Tooltip("토치가 이 거리 이상 이동할 때만 새 샘플을 추가")]
    public float minSegmentLength = 0.0025f;

    [Tooltip("샘플 개수 상한(성능/메모리). 0이면 무제한")]
    public int maxSamples = 0;

    [Tooltip("표면에서 약간 띄워 z-fighting/깜빡임 방지")]
    public float surfaceLift = 0.0005f;

    [Header("Bead Shape")]
    [Tooltip("비드 폭(단면 X축 지름)")]
    public float beadWidth = 0.006f;

    [Tooltip("비드 높이(단면 Y축 지름). 표면 법선 방향")]
    public float beadHeight = 0.003f;

    [Tooltip("단면 원주 분할 수 (>= 3). 높을수록 매끈")]
    [Range(3, 64)]
    public int radialSegments = 12;

    [Header("Mesh")]
    [Tooltip("용접 중 매 프레임 메쉬 갱신")]
    public bool updateEveryFrameWhileWelding = true;

    [Tooltip("생성된 메쉬에 MeshCollider를 동기화")]
    public bool generateMeshCollider = false;

    [Tooltip("UV V방향 스케일(길이 대비 텍스처 반복)")]
    public float uvVScale = 1.0f;

    [Header("State")]
    public bool isWelding = true;

    private readonly List<Sample> _samples = new List<Sample>(1024);
    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;

    // 누적 길이(UV V)
    private float _accumulatedLength;

    [Serializable]
    private struct Sample
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector3 tangent;
        public float v; // UV V(누적 길이)
    }

    private void Awake()
    {
        _meshFilter = GetComponent<MeshFilter>();
        if (_meshFilter.sharedMesh == null)
        {
            _mesh = new Mesh { name = "WeldingBead" };
            _mesh.MarkDynamic();
            _meshFilter.sharedMesh = _mesh;
        }
        else
        {
            _mesh = _meshFilter.sharedMesh;
        }

        if (generateMeshCollider)
        {
            _meshCollider = GetComponent<MeshCollider>();
            if (_meshCollider == null) _meshCollider = gameObject.AddComponent<MeshCollider>();
        }
    }

    private void OnValidate()
    {
        minSegmentLength = Mathf.Max(0.00001f, minSegmentLength);
        raycastDistance = Mathf.Max(0.0001f, raycastDistance);
        beadWidth = Mathf.Max(0.00001f, beadWidth);
        beadHeight = Mathf.Max(0.00001f, beadHeight);
        radialSegments = Mathf.Clamp(radialSegments, 3, 64);
        surfaceLift = Mathf.Max(0f, surfaceLift);
        uvVScale = Mathf.Max(0.00001f, uvVScale);
    }

    private void Update()
    {
        if (!isWelding) return;
        if (torchTip == null) return;

        bool added = TryAppendSample(torchTip.position);
        if (updateEveryFrameWhileWelding && (added || _samples.Count > 1))
        {
            RebuildMesh();
        }
    }

    public void StartWelding() => isWelding = true;
    public void StopWelding() => isWelding = false;

    public void Clear()
    {
        _samples.Clear();
        _accumulatedLength = 0f;
        if (_mesh == null) return;
        _mesh.Clear();
        if (_meshCollider != null) _meshCollider.sharedMesh = null;
    }

    private bool TryAppendSample(Vector3 tipWorldPos)
    {
        // 최소 이동 거리 체크
        if (_samples.Count > 0)
        {
            float dist = Vector3.Distance(_samples[_samples.Count - 1].position, tipWorldPos);
            if (dist < minSegmentLength) return false;
        }

        // 표면 Raycast
        Vector3 rayDir = overrideRayDirection.sqrMagnitude > 0.000001f
            ? overrideRayDirection.normalized
            : (-torchTip.up).normalized;

        Ray ray = new Ray(tipWorldPos, rayDir);
        if (!Physics.Raycast(ray, out RaycastHit hit, raycastDistance, surfaceMask, QueryTriggerInteraction.Ignore))
        {
            // 표면을 못 찾으면 샘플을 추가하지 않음(공중에서 비드 생성 방지)
            return false;
        }

        Vector3 p = hit.point + hit.normal * surfaceLift;
        Vector3 n = hit.normal.normalized;

        float v = 0f;
        Vector3 tangent = Vector3.forward;

        if (_samples.Count > 0)
        {
            Vector3 prevP = _samples[_samples.Count - 1].position;
            float segLen = Vector3.Distance(prevP, p);
            _accumulatedLength += segLen;
            v = _accumulatedLength / uvVScale;

            tangent = (p - prevP);
            if (tangent.sqrMagnitude < 1e-10f) tangent = _samples[_samples.Count - 1].tangent;
            else tangent.Normalize();
        }

        // 첫 샘플은 이후 샘플 들어오면 tangent를 보정
        Sample s = new Sample
        {
            position = p,
            normal = n,
            tangent = tangent,
            v = v,
        };

        _samples.Add(s);

        // 방금 추가한 샘플로 이전 tangent 보정(마지막-1)
        if (_samples.Count == 2)
        {
            Sample a = _samples[0];
            Sample b = _samples[1];
            Vector3 t = (b.position - a.position);
            if (t.sqrMagnitude > 1e-10f) t.Normalize();
            a.tangent = t;
            _samples[0] = a;
        }
        else if (_samples.Count > 2)
        {
            int i = _samples.Count - 2;
            Sample mid = _samples[i];
            Vector3 t = (_samples[i + 1].position - _samples[i - 1].position);
            if (t.sqrMagnitude > 1e-10f) t.Normalize();
            mid.tangent = t;
            _samples[i] = mid;
        }

        if (maxSamples > 0 && _samples.Count > maxSamples)
        {
            int removeCount = _samples.Count - maxSamples;
            _samples.RemoveRange(0, removeCount);
            // 제거로 인해 UV v가 불연속 될 수 있지만, 실시간 표시 목적엔 허용.
        }

        return true;
    }

    private void RebuildMesh()
    {
        if (_mesh == null) return;
        if (_samples.Count < 2)
        {
            _mesh.Clear();
            if (_meshCollider != null) _meshCollider.sharedMesh = null;
            return;
        }

        int ringCount = _samples.Count;
        int seg = Mathf.Max(3, radialSegments);

        int vertexCount = ringCount * seg;
        int triCount = (ringCount - 1) * seg * 2;
        int indexCount = triCount * 3;

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var triangles = new int[indexCount];

        float rx = beadWidth * 0.5f;
        float ry = beadHeight * 0.5f;

        // 링별 프레임(표면 normal + 경로 tangent로 안정적인 단면축 구성)
        for (int i = 0; i < ringCount; i++)
        {
            Sample s = _samples[i];

            Vector3 t = s.tangent;
            if (t.sqrMagnitude < 1e-10f)
            {
                t = (i > 0) ? (s.position - _samples[i - 1].position) : (_samples[i + 1].position - s.position);
                if (t.sqrMagnitude < 1e-10f) t = Vector3.forward;
                else t.Normalize();
            }

            Vector3 nSurface = s.normal.sqrMagnitude < 1e-10f ? Vector3.up : s.normal.normalized;

            // b = t x n  (단면 X축)
            Vector3 b = Vector3.Cross(t, nSurface);
            if (b.sqrMagnitude < 1e-10f)
            {
                // tangent와 normal이 거의 평행하면 fallback 축
                b = Vector3.Cross(t, Vector3.right);
                if (b.sqrMagnitude < 1e-10f) b = Vector3.Cross(t, Vector3.up);
            }
            b.Normalize();

            // n = b x t (단면 Y축, 표면 normal에 가깝게)
            Vector3 n = Vector3.Cross(b, t).normalized;

            // 비드를 표면 위로 올리기: center를 법선 방향으로 ry만큼 이동
            Vector3 center = s.position + nSurface * ry;

            for (int j = 0; j < seg; j++)
            {
                float u = (float)j / seg;
                float ang = u * Mathf.PI * 2f;

                // 오벌 단면: x=cos, y=sin
                Vector3 offset = b * (Mathf.Cos(ang) * rx) + n * (Mathf.Sin(ang) * ry);
                int idx = i * seg + j;

                vertices[idx] = center + offset;
                normals[idx] = offset.normalized; // 튜브 기준 법선
                uvs[idx] = new Vector2(u, s.v);
            }
        }

        int ti = 0;
        for (int i = 0; i < ringCount - 1; i++)
        {
            int base0 = i * seg;
            int base1 = (i + 1) * seg;

            for (int j = 0; j < seg; j++)
            {
                int j0 = j;
                int j1 = (j + 1) % seg;

                int a = base0 + j0;
                int b = base1 + j0;
                int c = base1 + j1;
                int d = base0 + j1;

                // a-b-c
                triangles[ti++] = a;
                triangles[ti++] = b;
                triangles[ti++] = c;

                // a-c-d
                triangles[ti++] = a;
                triangles[ti++] = c;
                triangles[ti++] = d;
            }
        }

        _mesh.Clear();
        _mesh.vertices = vertices;
        _mesh.normals = normals;
        _mesh.uv = uvs;
        _mesh.triangles = triangles;
        _mesh.RecalculateBounds();

        if (_meshCollider != null)
        {
            // MeshCollider는 sharedMesh 재할당이 필요
            _meshCollider.sharedMesh = null;
            _meshCollider.sharedMesh = _mesh;
        }
    }
}

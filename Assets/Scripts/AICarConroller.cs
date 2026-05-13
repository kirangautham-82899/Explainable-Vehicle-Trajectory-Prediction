using System.Collections.Generic;
using UnityEngine;

public class AICarController : MonoBehaviour
{
    // ── Public state read by TrafficLightManager / DataCollection ────
    [HideInInspector] public TrafficLight nearestSignal;
    [HideInInspector] public float        newSteer;
    [HideInInspector] public float        currentSpeed;

    // ── Path ─────────────────────────────────────────────────────────
    [Header("Path")]
    [Tooltip("Assign the path GameObject (child transforms or LineRenderer). Leave empty to auto-find 'Car Paths/<laneName>'.")]
    public Transform path;
    [SerializeField] private string laneName       = "Path_neon";
    [SerializeField] private float  waypointRadius = 2f;
    [SerializeField] private float  lookaheadDist  = 20f;

    // ── Motor ────────────────────────────────────────────────────────
    [Header("Motor")]
    [SerializeField] private float maxSpeed      = 10f;   // m/s
    [SerializeField] private float motorForce    = 1500f;
    [SerializeField] private float breakForce    = 3000f;
    [SerializeField] private float maxSteerAngle = 35f;
    [SerializeField] private float steerRate     = 90f;   // deg/s smooth steer

    // ── Wheels (auto-detected; assign manually as fallback) ──────────
    [Header("Wheels (auto-detected)")]
    [SerializeField] private WheelCollider frontLeftWheelCollider;
    [SerializeField] private WheelCollider frontRightWheelCollider;
    [SerializeField] private WheelCollider rearLeftWheelCollider;
    [SerializeField] private WheelCollider rearRightWheelCollider;
    [SerializeField] private Transform     frontLeftWheelTransform;
    [SerializeField] private Transform     frontRightWheelTransform;
    [SerializeField] private Transform     rearLeftWheelTransform;
    [SerializeField] private Transform     rearRightWheelTransform;

    // ── Obstacle avoidance ───────────────────────────────────────────
    [Header("Obstacle Avoidance")]
    [SerializeField] private float avoidRayLen   = 18f;
    [SerializeField] private float laneShift     = 0.5f;   // max steer fraction for overtake
    [SerializeField] private float maxLaneDrift  = 3.0f;   // m before overtake steer is cancelled
    [SerializeField] private float junctionRadius= 7f;
    [SerializeField] private float brakeDist     = 12f;
    [SerializeField] private float hardBrakeDist = 9f;

    // ── Building avoidance ───────────────────────────────────────────
    [Header("Building Avoidance")]
    [SerializeField] private LayerMask buildingLayer;
    [SerializeField] private float     buildingDetectRange = 12f;
    [SerializeField] private float     buildingHardBrake   = 5f;

    // ── Recovery ─────────────────────────────────────────────────────
    [Header("Recovery")]
    [SerializeField] private float stuckTimeout = 2f;
    [SerializeField] private float reverseTime  = 1.5f;

    // ── Runtime ──────────────────────────────────────────────────────
    private Rigidbody rb;
    private Vector3[] pathPts;
    private int       wpIdx;

    private float appliedSteer   = 0f;
    private float overtakeSteering = 0f;

    private float stuckTimer;
    private float recoveryTimer;
    private bool  recovering;
    private bool  stoppedForRed;

    private static readonly Collider[] _overlapBuf = new Collider[32];

    // ─────────────────────────────────────────────────────────────────
    // Start
    // ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = new Vector3(0f, -0.45f, 0.05f);

        DetectWheels();
        DisableConflictingControllers();
        EnableAICarColliders();
        BuildPath();
    }

    // ─────────────────────────────────────────────────────────────────
    // FixedUpdate
    // ─────────────────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        float dt       = Time.fixedDeltaTime;
        float speedMs  = rb.linearVelocity.magnitude;
        currentSpeed   = speedMs;

        // ── Red light ─────────────────────────────────────────────────
        float redLightThrottle = EvaluateRedLight();

        if (stoppedForRed)
        {
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            SetBrakes(breakForce);
            SyncWheels();
            return;
        }

        // ── Stuck detection & recovery ────────────────────────────────
        bool atRedLight = redLightThrottle < 1f;
        stuckTimer = (speedMs < 0.4f && !atRedLight) ? stuckTimer + dt : 0f;

        if (!recovering && stuckTimer >= stuckTimeout)
        {
            recovering    = true;
            recoveryTimer = reverseTime;
            stuckTimer    = 0f;
        }

        if (recovering)
        {
            recoveryTimer -= dt;
            DoRecover();
            SyncWheels();
            if (recoveryTimer <= 0f) recovering = false;
            return;
        }

        // ── Steering ──────────────────────────────────────────────────
        float targetSteer = PathSteer();

        float avoidSteer, buildingSteer;
        float carThrottle      = ObstacleAvoid(out avoidSteer);
        float buildingThrottle = BuildingAvoid(out buildingSteer);
        float junctionScale    = JunctionBrake();

        // Cancel overtake steer if already drifted too far from path
        float lateralOffset = PathLateralOffset();
        if (avoidSteer > 0f && lateralOffset >  maxLaneDrift) avoidSteer = 0f;
        if (avoidSteer < 0f && lateralOffset < -maxLaneDrift) avoidSteer = 0f;

        float throttleScale = Mathf.Min(Mathf.Min(carThrottle, buildingThrottle),
                              Mathf.Min(redLightThrottle, junctionScale));

        float overtakeBlend = Mathf.Clamp01(Mathf.Abs(overtakeSteering) / Mathf.Max(maxSteerAngle * laneShift, 1f));
        float desiredSteer  = Mathf.Clamp(
            Mathf.Lerp(targetSteer, 0f, overtakeBlend) + avoidSteer + buildingSteer,
            -maxSteerAngle, maxSteerAngle);

        appliedSteer = Mathf.MoveTowards(appliedSteer, desiredSteer, steerRate * dt);
        newSteer     = appliedSteer;

        frontLeftWheelCollider.steerAngle  = appliedSteer;
        frontRightWheelCollider.steerAngle = appliedSteer;

        // ── Drive ─────────────────────────────────────────────────────
        if (throttleScale <= 0f)
        {
            SetBrakes(breakForce);
        }
        else if (speedMs < maxSpeed)
        {
            ClearBrakes();
            float gentleBrake = (1f - throttleScale) * motorForce * 0.5f;
            frontLeftWheelCollider.brakeTorque  = gentleBrake;
            frontRightWheelCollider.brakeTorque = gentleBrake;
            rearLeftWheelCollider.brakeTorque   = gentleBrake * 0.6f;
            rearRightWheelCollider.brakeTorque  = gentleBrake * 0.6f;
            rearLeftWheelCollider.motorTorque   = motorForce * throttleScale;
            rearRightWheelCollider.motorTorque  = motorForce * throttleScale;
        }
        else
        {
            rearLeftWheelCollider.motorTorque  = 0f;
            rearRightWheelCollider.motorTorque = 0f;
        }

        SyncWheels();
    }

    // ─────────────────────────────────────────────────────────────────
    // Red light — returns throttle scale [0,1]; sets stoppedForRed
    // ─────────────────────────────────────────────────────────────────

    private float EvaluateRedLight()
    {
        var tl = TrafficLightManager.NearestRedAhead(transform);
        if (tl == null) { stoppedForRed = false; return 1f; }
        Vector3 toLight = tl.transform.position - transform.position;
        toLight.y = 0f;
        // Latch only at the stop line so physics braking runs across the full approach zone.
        if (toLight.magnitude <= 4f) stoppedForRed = true;
        return 0f;   // throttleScale → 0 → SetBrakes(breakForce) via normal drive path
    }

    // ─────────────────────────────────────────────────────────────────
    // Pure-pursuit path steering
    // ─────────────────────────────────────────────────────────────────

    private float PathSteer()
    {
        if (pathPts == null || pathPts.Length == 0) return 0f;

        // Advance past reached or behind waypoints (max 4 per frame)
        int maxAdv = 4;
        while (maxAdv-- > 0)
        {
            if (wpIdx >= pathPts.Length) { wpIdx = 0; break; }
            Vector3 toWp    = pathPts[wpIdx] - transform.position;
            bool    reached = toWp.magnitude < waypointRadius;
            bool    behind  = Vector3.Dot(transform.forward, toWp.normalized) < -0.3f;
            if (reached || behind)
                wpIdx = (wpIdx + 1 < pathPts.Length) ? wpIdx + 1 : 0;
            else break;
        }

        // Walk lookahead distance along path
        float   accumulated = 0f;
        Vector3 target      = pathPts[wpIdx];
        for (int i = wpIdx; i < pathPts.Length - 1; i++)
        {
            float seg = Vector3.Distance(pathPts[i], pathPts[i + 1]);
            if (accumulated + seg >= lookaheadDist)
            {
                float t = (lookaheadDist - accumulated) / seg;
                target = Vector3.Lerp(pathPts[i], pathPts[i + 1], t);
                break;
            }
            accumulated += seg;
            target = pathPts[i + 1];
        }

        Debug.DrawLine(transform.position + Vector3.up * 0.5f, target + Vector3.up * 0.5f, Color.yellow);
        Debug.DrawRay(transform.position + Vector3.up * 0.5f, transform.forward * 8f, Color.green);

        Vector3 dir     = (target - transform.position).normalized;
        float   lateral = Vector3.Dot(transform.right, dir);
        return Mathf.Clamp(lateral * 22f, -maxSteerAngle, maxSteerAngle);
    }

    // ─────────────────────────────────────────────────────────────────
    // Lateral offset from current path segment
    // ─────────────────────────────────────────────────────────────────

    private float PathLateralOffset()
    {
        if (pathPts == null || pathPts.Length < 2) return 0f;
        int     idx   = Mathf.Clamp(wpIdx, 0, pathPts.Length - 2);
        Vector3 seg   = (pathPts[idx + 1] - pathPts[idx]).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, seg).normalized;
        return Vector3.Dot(right, transform.position - pathPts[idx]);
    }

    // ─────────────────────────────────────────────────────────────────
    // Obstacle avoidance — OverlapSphere, smooth overtake steer
    // ─────────────────────────────────────────────────────────────────

    private float ObstacleAvoid(out float steerOffset)
    {
        steerOffset = 0f;
        float closestFwd = float.MaxValue;
        float closestLat = 0f;
        bool  carInLane  = false;

        int count = Physics.OverlapSphereNonAlloc(transform.position, avoidRayLen, _overlapBuf);
        var seen  = new HashSet<Rigidbody>();

        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapBuf[i];
            if (col.isTrigger) continue;

            Rigidbody otherRb = col.GetComponentInParent<Rigidbody>();
            if (otherRb == null || otherRb == rb) continue;
            if (!seen.Add(otherRb)) continue;

            Vector3 toCar = otherRb.position - transform.position;
            float   fwd   = Vector3.Dot(transform.forward, toCar);
            if (fwd <= 0f || fwd > avoidRayLen) continue;
            float lat = Vector3.Dot(transform.right, toCar);
            if (Mathf.Abs(lat) > 3.5f) continue;

            carInLane = true;
            if (fwd < closestFwd) { closestFwd = fwd; closestLat = lat; }
        }

        float rate = maxSteerAngle * 2.5f * Time.fixedDeltaTime;
        if (carInLane)
        {
            float side        = closestLat >= 0f ? -1f : 1f;
            float targetSteer = side * maxSteerAngle * laneShift;
            overtakeSteering  = Mathf.MoveTowards(overtakeSteering, targetSteer, rate);
        }
        else
        {
            overtakeSteering = Mathf.MoveTowards(overtakeSteering, 0f, rate * 0.5f);
        }

        steerOffset = overtakeSteering;

        if (!carInLane || closestFwd >= brakeDist) return 1f;
        if (closestFwd <= hardBrakeDist)            return 0f;

        bool fullySidestepped = Mathf.Abs(overtakeSteering) >= maxSteerAngle * laneShift * 0.9f
                             && Mathf.Abs(closestLat) > 2.5f;
        if (fullySidestepped) return 1f;

        return Mathf.Clamp01((closestFwd - hardBrakeDist) / (brakeDist - hardBrakeDist));
    }

    // ─────────────────────────────────────────────────────────────────
    // Building avoidance — forward + diagonal raycasts
    // ─────────────────────────────────────────────────────────────────

    private float BuildingAvoid(out float steerOffset)
    {
        steerOffset = 0f;
        if (buildingLayer == 0) return 1f;

        Vector3 origin = transform.position + Vector3.up * 0.5f;

        if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, buildingDetectRange, buildingLayer))
        {
            float dist    = hit.distance;
            float lateral = Vector3.Dot(transform.right, hit.normal);
            steerOffset   = Mathf.Clamp(-lateral * maxSteerAngle, -maxSteerAngle, maxSteerAngle);
            Debug.DrawRay(origin, transform.forward * dist, Color.red);
            if (dist <= buildingHardBrake) return 0f;
            return Mathf.Clamp01((dist - buildingHardBrake) / (buildingDetectRange - buildingHardBrake));
        }

        Debug.DrawRay(origin, transform.forward * buildingDetectRange, Color.white);
        Vector3 leftDir  = (transform.forward * 4f - transform.right).normalized;
        Vector3 rightDir = (transform.forward * 4f + transform.right).normalized;
        if (Physics.Raycast(origin, leftDir,  buildingDetectRange * 0.5f, buildingLayer)) steerOffset += maxSteerAngle * 0.3f;
        if (Physics.Raycast(origin, rightDir, buildingDetectRange * 0.5f, buildingLayer)) steerOffset -= maxSteerAngle * 0.3f;
        return 1f;
    }

    // ─────────────────────────────────────────────────────────────────
    // Junction braking — cross-traffic closing-rate guard
    // ─────────────────────────────────────────────────────────────────

    private float JunctionBrake()
    {
        float minScale = 1f;
        int   count    = Physics.OverlapSphereNonAlloc(transform.position, junctionRadius, _overlapBuf);
        var   seen     = new HashSet<Rigidbody>();

        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapBuf[i];
            if (col.isTrigger) continue;

            Rigidbody otherRb = col.GetComponentInParent<Rigidbody>();
            if (otherRb == null || otherRb == rb) continue;
            if (!seen.Add(otherRb)) continue;

            Vector3 toCar   = otherRb.position - transform.position;
            float   dist    = toCar.magnitude;
            if (dist < 0.5f) continue;

            float fwdFrac = Vector3.Dot(transform.forward, toCar) / dist;
            if (fwdFrac >  0.6f) continue;   // ahead — ObstacleAvoid handles it
            if (fwdFrac < -0.3f) continue;   // behind

            float closingRate = Vector3.Dot(rb.linearVelocity - otherRb.linearVelocity, toCar.normalized);
            if (closingRate < 0.5f) continue;

            float scale = dist <= hardBrakeDist ? 0f :
                          Mathf.Clamp01((dist - hardBrakeDist) / (junctionRadius - hardBrakeDist));
            if (scale < minScale) minScale = scale;
        }
        return minScale;
    }

    // ─────────────────────────────────────────────────────────────────
    // Stuck recovery — reverse straight back
    // ─────────────────────────────────────────────────────────────────

    private void DoRecover()
    {
        appliedSteer = Mathf.MoveTowards(appliedSteer, 0f, steerRate * Time.fixedDeltaTime);
        frontLeftWheelCollider.steerAngle  = appliedSteer;
        frontRightWheelCollider.steerAngle = appliedSteer;

        rearLeftWheelCollider.motorTorque  = -motorForce * 0.4f;
        rearRightWheelCollider.motorTorque = -motorForce * 0.4f;
        frontLeftWheelCollider.brakeTorque  = 0f;
        frontRightWheelCollider.brakeTorque = 0f;
        rearLeftWheelCollider.brakeTorque   = 0f;
        rearRightWheelCollider.brakeTorque  = 0f;
    }

    // ─────────────────────────────────────────────────────────────────
    // Path builder — LineRenderer or child transforms, interpolated
    // ─────────────────────────────────────────────────────────────────

    private void BuildPath()
    {
        Transform pathRoot = path;

        // Auto-find if not assigned
        if (pathRoot == null)
        {
            var laneGo = GameObject.Find("Car Paths/" + laneName) ?? GameObject.Find(laneName);
            if (laneGo != null) pathRoot = laneGo.transform;
        }
        if (pathRoot == null)
        {
            var go = GameObject.Find("Car Paths");
            if (go != null) pathRoot = go.transform;
        }
        if (pathRoot == null)
        {
            Debug.LogWarning($"[AICarController] '{name}': No path found. Assign Path in Inspector or name it '{laneName}'.", this);
            return;
        }

        // Prefer LineRenderer
        var lr = pathRoot.GetComponent<LineRenderer>();
        if (lr == null)
            foreach (Transform child in pathRoot) { lr = child.GetComponent<LineRenderer>(); if (lr != null) break; }

        if (lr != null && lr.positionCount >= 2)
        {
            BuildFromLineRenderer(lr);
            return;
        }

        // Fallback: child transform positions
        if (pathRoot.childCount < 2)
        {
            Debug.LogWarning($"[AICarController] '{name}': Path '{pathRoot.name}' has no LineRenderer and <2 children.", this);
            return;
        }

        int n   = pathRoot.childCount;
        var pts = new List<Vector3>();
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 a = pathRoot.GetChild(i).position;
            Vector3 b = pathRoot.GetChild(i + 1).position;
            for (int s = 0; s < 15; s++) pts.Add(Vector3.Lerp(a, b, s / 15f));
        }
        pts.Add(pathRoot.GetChild(n - 1).position);
        pathPts = pts.ToArray();
        ApplyBestStart();
        Debug.Log($"[AICarController] '{name}': path from child transforms — {pathPts.Length} pts, start {wpIdx}");
        DrawDebugPath();
    }

    private void BuildFromLineRenderer(LineRenderer lr)
    {
        int n   = lr.positionCount;
        var pts = new List<Vector3>();
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 a = lr.GetPosition(i), b = lr.GetPosition(i + 1);
            for (int s = 0; s < 15; s++) pts.Add(Vector3.Lerp(a, b, s / 15f));
        }
        pts.Add(lr.GetPosition(n - 1));
        pathPts = pts.ToArray();
        ApplyBestStart();
        Debug.Log($"[AICarController] '{name}': path from LineRenderer '{lr.gameObject.name}' — {pathPts.Length} pts, start {wpIdx}");
        DrawDebugPath();
    }

    private void ApplyBestStart()
    {
        int best = 0; float bestScore = float.MinValue;
        for (int i = 0; i < pathPts.Length; i++)
        {
            Vector3 d  = pathPts[i] - transform.position;
            float   sc = Vector3.Dot(transform.forward, d.normalized) - d.magnitude * 0.01f;
            if (sc > bestScore) { bestScore = sc; best = i; }
        }
        wpIdx = best;
    }

    private void DrawDebugPath()
    {
        for (int i = 0; i < pathPts.Length - 1; i++)
            Debug.DrawLine(pathPts[i] + Vector3.up, pathPts[i + 1] + Vector3.up, Color.cyan, 60f);
    }

    // ─────────────────────────────────────────────────────────────────
    // Auto wheel detection — position-based (front/rear, left/right)
    // Falls back to Inspector-assigned references if already set.
    // ─────────────────────────────────────────────────────────────────

    private void DetectWheels()
    {
        // Skip if all manually assigned
        if (frontLeftWheelCollider && frontRightWheelCollider &&
            rearLeftWheelCollider  && rearRightWheelCollider)
            return;

        foreach (var wc in GetComponentsInChildren<WheelCollider>())
        {
            Vector3 lp = transform.InverseTransformPoint(wc.transform.position);
            bool f = lp.z > 0f, l = lp.x < 0f;
            if (f  &&  l)  frontLeftWheelCollider  = wc;
            if (f  && !l)  frontRightWheelCollider = wc;
            if (!f &&  l)  rearLeftWheelCollider   = wc;
            if (!f && !l)  rearRightWheelCollider  = wc;
        }

        foreach (var t in GetComponentsInChildren<Transform>())
        {
            if (t.GetComponent<WheelCollider>() != null) continue;
            if (t.GetComponent<MeshRenderer>()  == null) continue;
            string n = t.name.ToLower();
            if (!n.Contains("wheel") && !n.Contains("tire") && !n.Contains("tyre")) continue;
            Vector3 lp = transform.InverseTransformPoint(t.position);
            bool f = lp.z > 0f, l = lp.x < 0f;
            if (f  &&  l)  frontLeftWheelTransform  = t;
            if (f  && !l)  frontRightWheelTransform = t;
            if (!f &&  l)  rearLeftWheelTransform   = t;
            if (!f && !l)  rearRightWheelTransform  = t;
        }

        Debug.Log($"[AICarController] '{name}': wheels FL={frontLeftWheelCollider?.name} FR={frontRightWheelCollider?.name} RL={rearLeftWheelCollider?.name} RR={rearRightWheelCollider?.name}");
    }

    // ─────────────────────────────────────────────────────────────────
    // Enable solid colliders on known AI car parent objects
    // ─────────────────────────────────────────────────────────────────

    private void EnableAICarColliders()
    {
        string[] parentNames = { "AI Cars", "AI Cars Red", "AI Cars Neon", "AI Cars neon", "AI Cars red", "AICars", "BlueCars" };
        foreach (string pname in parentNames)
        {
            var go = GameObject.Find(pname);
            if (go == null) continue;
            int count = 0;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (col is WheelCollider) continue;
                col.enabled   = true;
                col.isTrigger = false;
                count++;
            }
            if (count > 0)
                Debug.Log($"[AICarController] Solid colliders enabled: {count} under '{pname}'");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Disable any other CarController-style scripts on this GameObject
    // ─────────────────────────────────────────────────────────────────

    private void DisableConflictingControllers()
    {
        foreach (var mb in GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb == this) continue;   // null guard: missing script slots return null
            string n = mb.GetType().Name.ToLower();
            if (n.Contains("car") && n.Contains("controller"))
            {
                mb.enabled = false;
                Debug.Log($"[AICarController] Disabled conflicting script: '{mb.GetType().Name}'");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Wheel helpers
    // ─────────────────────────────────────────────────────────────────

    private void SyncWheels()
    {
        SyncWheel(frontLeftWheelCollider,  frontLeftWheelTransform);
        SyncWheel(frontRightWheelCollider, frontRightWheelTransform);
        SyncWheel(rearLeftWheelCollider,   rearLeftWheelTransform);
        SyncWheel(rearRightWheelCollider,  rearRightWheelTransform);
    }

    private static void SyncWheel(WheelCollider c, Transform t)
    {
        if (c == null || t == null) return;
        c.GetWorldPose(out Vector3 p, out Quaternion r);
        t.SetPositionAndRotation(p, r);
    }

    private void SetBrakes(float force)
    {
        rearLeftWheelCollider.motorTorque   = 0f;
        rearRightWheelCollider.motorTorque  = 0f;
        frontLeftWheelCollider.brakeTorque  = force;
        frontRightWheelCollider.brakeTorque = force;
        rearLeftWheelCollider.brakeTorque   = force * 0.6f;
        rearRightWheelCollider.brakeTorque  = force * 0.6f;
    }

    private void ClearBrakes()
    {
        frontLeftWheelCollider.brakeTorque  = 0f;
        frontRightWheelCollider.brakeTorque = 0f;
        rearLeftWheelCollider.brakeTorque   = 0f;
        rearRightWheelCollider.brakeTorque  = 0f;
    }
}

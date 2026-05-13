using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.InferenceEngine;

public class onnxcontroller : MonoBehaviour
{
    [Header("ONNX")]
    [SerializeField] private ModelAsset modelAsset;
    [SerializeField] private Camera     screenshotCamera;
    [SerializeField][Range(0.05f,0.5f)] private float inferenceInterval = 0.1f;

    [Header("Motor")]
    [SerializeField] private float motorForce   = 1500f;
    [SerializeField] private float maxSpeedKmh  = 45f;   // raised: must exceed AI (≈40 km/h) to overtake
    [SerializeField] private float minThrottle  = 0.45f;
    [SerializeField] private float maxSteerAngle = 30f;   // slight increase for tighter overtake manoeuvres
    [SerializeField] private float steerRate     = 90f;

    [Header("Path  (reads LineRenderer from Car Paths/laneName)")]
    [SerializeField] private Transform waypointParent;                 // leave empty to auto-find
    [SerializeField] private string    laneName        = "Path_neon";  // child of 'Car Paths' to follow
    [SerializeField] private float     waypointRadius  = 2f;
    [SerializeField] private float     lookaheadDist   = 20f;

    [Header("Obstacle Avoidance")]
    [Tooltip("Set to ONLY the layer your blue AI cars are on. Leave as 'Nothing' to disable avoidance (prevents buildings from causing false turns).")]
    [SerializeField] private LayerMask carLayer       = 0;   // Nothing by default
    [SerializeField] private float     avoidRayLen    = 18f;
    [Tooltip("Max steer fraction used for avoidance — keeps car within its lane (yellow line)")]
    [SerializeField] private float     laneShift      = 0.5f;
    [Tooltip("Max metres the car can drift sideways from its path before overtake steer is cancelled")]
    [SerializeField] private float     maxLaneDrift   = 3.0f;
    [Tooltip("Any car within this radius triggers emergency braking regardless of direction (junction safety)")]
    [SerializeField] private float     junctionRadius = 7f;
    [Tooltip("Distance at which throttle starts reducing (steer-only beyond this)")]
    [SerializeField] private float     brakeDist      = 12f;
    [Tooltip("Distance at which full brakes apply regardless of steering room")]
    [SerializeField] private float     hardBrakeDist  = 9f;

    [Header("Building Avoidance")]
    [Tooltip("Layer mask for static buildings / walls. Assign in Inspector.")]
    [SerializeField] private LayerMask buildingLayer;
    [Tooltip("How far ahead to raycast for buildings")]
    [SerializeField] private float     buildingDetectRange = 12f;
    [Tooltip("Distance at which full brakes apply for a building")]
    [SerializeField] private float     buildingHardBrake   = 5f;

    [Header("Recovery")]
    [SerializeField] private float stuckTimeout = 2f;
    [SerializeField] private float reverseTime  = 1.5f;

    // Wheels — auto-detected
    private WheelCollider flWC, frWC, rlWC, rrWC;
    private Transform     flWT, frWT, rlWT, rrWT;

    // Image constants
    private const int IW = 224, IH = 224, IC = 3;
    private static readonly float[] MEAN = {0.485f, 0.456f, 0.406f};
    private static readonly float[] STD  = {0.229f, 0.224f, 0.225f};

    // Runtime
    private Worker        worker;
    private RenderTexture captureRT;
    private Rigidbody     rb;

    private Vector3[] path;
    private int       wpIdx;

    private Transform[] aiCarTransforms;

    [HideInInspector] public TrafficLight    nearestSignal;

    // ── HUD data (read by HUDController) ─────────────────────────
    [HideInInspector] public float           hudSpeed;
    [HideInInspector] public float           hudThrottle;
    [HideInInspector] public float           hudSteer;
    [HideInInspector] public float           hudModelW, hudModelA, hudModelS, hudModelD, hudModelSpace;
    [HideInInspector] public bool            hudRecovering;
    [HideInInspector] public int             hudWpIdx, hudWpTotal;
    [HideInInspector] public RenderTexture   hudRT;

    private float throttle         = 0.5f;
    private float smoothedThrottle = 0.5f;
    private float appliedSteer     = 0f;

    private bool  readbackPending;
    private float nextInferTime;

    private float stuckTimer;
    private float recoveryTimer;
    private bool  recovering;
    private float overtakeSteering = 0f;
    // Latched true when the car commits to stopping for a red light.
    // Cleared only when the light turns green — prevents oscillation and stuck-recovery false triggers.
    private bool  stoppedForRed;

    // ─────────────────────────────────────────────────────────────
    // Start
    // ─────────────────────────────────────────────────────────────

    private void Start()
    {
        worker    = new Worker(ModelLoader.Load(modelAsset), BackendType.GPUCompute);
        captureRT = new RenderTexture(IW, IH, 0, RenderTextureFormat.ARGB32);
        captureRT.Create();
        hudRT = captureRT;
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = new Vector3(0f, -0.45f, 0.05f);

        DetectWheels();
        DetectCamera();
        DisableConflictingControllers();
        EnableAICarColliders();
        BuildPath();
        CacheAICars();
    }

    // ─────────────────────────────────────────────────────────────
    // Update
    // ─────────────────────────────────────────────────────────────

    private void Update()
    {
        if (!readbackPending && Time.time >= nextInferTime)
        {
            nextInferTime = Time.time + inferenceInterval;
            CaptureFrame();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // FixedUpdate
    // ─────────────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        float dt       = Time.fixedDeltaTime;
        float speedKmh = rb.linearVelocity.magnitude * 3.6f;

        // ── Red light ─────────────────────────────────────────────
        var redTL = TrafficLightManager.NearestRedAhead(transform);
        if (redTL != null)
        {
            Vector3 toLight = redTL.transform.position - transform.position;
            toLight.y = 0f;
            float redDist = toLight.magnitude;

            // Full brakes across entire approach zone (physics deceleration).
            rlWC.motorTorque = 0f; rrWC.motorTorque = 0f;
            flWC.brakeTorque = motorForce; frWC.brakeTorque = motorForce;
            rlWC.brakeTorque = motorForce * 0.6f; rrWC.brakeTorque = motorForce * 0.6f;

            // Clamp velocity only at the stop line to prevent slow creep.
            if (redDist <= 4f || stoppedForRed)
            {
                stoppedForRed      = true;
                rb.linearVelocity  = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            SyncWheels();
            return;
        }
        stoppedForRed = false;
        bool atRedLight = false;
        float redLightThrottle = 1f;

        stuckTimer = (speedKmh < 1.5f && !atRedLight) ? stuckTimer + dt : 0f;
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

        // ── Path following ────────────────────────────────────────
        float targetSteer = PathSteer();

        // ── Avoidance: other cars + buildings ─────────────────────────────────────────────
        float avoidSteer,  buildingSteer;
        float carThrottle      = ObstacleAvoid(out avoidSteer);

        // Hard boundary: if the car has already drifted past maxLaneDrift metres from
        // its path centre, cancel the overtake steer in that direction immediately.
        float lateralOffset = PathLateralOffset();
        if (avoidSteer > 0f && lateralOffset >  maxLaneDrift) avoidSteer = 0f;
        if (avoidSteer < 0f && lateralOffset < -maxLaneDrift) avoidSteer = 0f;

        float buildingThrottle = BuildingAvoid(out buildingSteer);
        float throttleScale    = Mathf.Min(Mathf.Min(carThrottle, buildingThrottle),
                                 Mathf.Min(redLightThrottle, JunctionBrake()));

        // Blend based on the persistent overtakeSteering so path steer stays suppressed
        // even when avoidSteer is clamped to 0 at the lane boundary.
        float overtakeBlend = Mathf.Clamp01(Mathf.Abs(overtakeSteering) / Mathf.Max(maxSteerAngle * laneShift, 1f));
        float desiredSteer  = Mathf.Clamp(
            Mathf.Lerp(targetSteer, 0f, overtakeBlend) + avoidSteer + buildingSteer,
            -maxSteerAngle, maxSteerAngle);

        // Smooth steer (no snap)
        appliedSteer = Mathf.MoveTowards(appliedSteer, desiredSteer, steerRate * dt);

        flWC.steerAngle = appliedSteer;
        frWC.steerAngle = appliedSteer;

        // ── Rule 2: red light stop (only if light is directly ahead) ─────────────────────
        if (throttleScale <= 0f)
        {
            rlWC.motorTorque = 0f; rrWC.motorTorque = 0f;
            flWC.brakeTorque = motorForce; frWC.brakeTorque = motorForce;
            rlWC.brakeTorque = motorForce * 0.6f; rrWC.brakeTorque = motorForce * 0.6f;
            SyncWheels();
            return;
        }

        // ── Throttle (brakes cleared — only fire for the three rules above) ──────────────
        float speedFrac = Mathf.Clamp01(speedKmh / maxSpeedKmh);
        float thr       = Mathf.Max(throttle, minThrottle) * throttleScale;
        float torque    = thr * motorForce * (1f - speedFrac * speedFrac);

        rlWC.motorTorque = torque;
        rrWC.motorTorque = torque;

        float gentleBrake = (1f - throttleScale) * motorForce * 0.5f;
        flWC.brakeTorque = gentleBrake;
        frWC.brakeTorque = gentleBrake;
        rlWC.brakeTorque = gentleBrake * 0.6f;
        rrWC.brakeTorque = gentleBrake * 0.6f;

        SyncWheels();

        // ── HUD feed ──────────────────────────────────────────────
        hudSpeed     = speedKmh;
        hudThrottle  = throttle;
        hudSteer     = appliedSteer;
        hudRecovering = recovering;
        hudWpIdx     = wpIdx;
        hudWpTotal   = path != null ? path.Length : 0;
    }

    private void OnDestroy()
    {
        worker?.Dispose();
        if (captureRT != null) captureRT.Release();
    }

    // ─────────────────────────────────────────────────────────────
    // Path steering — pure-pursuit lookahead (no left bias)
    // ─────────────────────────────────────────────────────────────

    private float PathSteer()
    {
        if (path == null || path.Length == 0) return 0f;

        // Advance past reached or clearly-behind waypoints; cap at 4 advances per frame
        // to prevent runaway jumps after collisions or recovery. Loop path at end.
        int maxAdv = 4;
        while (maxAdv-- > 0)
        {
            if (wpIdx >= path.Length) { wpIdx = 0; break; }
            Vector3 toWp = path[wpIdx] - transform.position;
            bool reached = toWp.magnitude < waypointRadius;
            bool behind  = Vector3.Dot(transform.forward, toWp.normalized) < -0.3f;
            if (reached || behind)
                wpIdx = (wpIdx + 1 < path.Length) ? wpIdx + 1 : 0;
            else break;
        }

        // Walk forward along the path to find a point lookaheadDist metres ahead
        float   accumulated = 0f;
        Vector3 target      = path[wpIdx];

        for (int i = wpIdx; i < path.Length - 1; i++)
        {
            float seg = Vector3.Distance(path[i], path[i + 1]);
            if (accumulated + seg >= lookaheadDist)
            {
                float t = (lookaheadDist - accumulated) / seg;
                target = Vector3.Lerp(path[i], path[i + 1], t);
                break;
            }
            accumulated += seg;
            target = path[i + 1];
        }

        // Draw steering target in Scene view (yellow = target, green = forward)
        Debug.DrawLine(transform.position + Vector3.up * 0.5f, target + Vector3.up * 0.5f, Color.yellow);
        Debug.DrawRay(transform.position + Vector3.up * 0.5f, transform.forward * 8f, Color.green);

        Vector3 dir     = (target - transform.position).normalized;
        float   lateral = Vector3.Dot(transform.right, dir); // –1=left, +1=right
        float   steer   = Mathf.Clamp(lateral * 22f, -maxSteerAngle, maxSteerAngle);

        if (Time.frameCount % 60 == 0)
            Debug.Log($"[AI] wpIdx={wpIdx}/{path.Length-1}  lateral={lateral:F2}  steer={steer:F1}°  target={target}");

        return steer;
    }

    // Returns signed metres the car is laterally displaced from its current path segment.
    // Positive = car is to the RIGHT of the path, negative = to the LEFT.
    private float PathLateralOffset()
    {
        if (path == null || path.Length < 2) return 0f;
        int idx = Mathf.Clamp(wpIdx, 0, path.Length - 2);
        Vector3 seg   = (path[idx + 1] - path[idx]).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, seg).normalized;
        return Vector3.Dot(right, transform.position - path[idx]);
    }

    // ─────────────────────────────────────────────────────────────
    // Cache AI car transforms once at startup
    // ─────────────────────────────────────────────────────────────

    private void CacheAICars()
    {
        var controllers = FindObjectsByType<AICarController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        aiCarTransforms = new Transform[controllers.Length];
        for (int i = 0; i < controllers.Length; i++)
            aiCarTransforms[i] = controllers[i].transform;
        Debug.Log($"[AI] Tracking {aiCarTransforms.Length} AI cars for avoidance.");
    }

    // ─────────────────────────────────────────────────────────────
    // Obstacle avoidance — proximity-based (no layer mask needed)
    // Checks actual AI car positions: if one is ahead and close,
    // steer away and brake.
    // ─────────────────────────────────────────────────────────────

    // Smooth overtake avoidance — live OverlapSphere detects ALL car types,
    // no stale cache. overtakeSteering persists and interpolates via MoveTowards.
    private static readonly Collider[] _overlapBuf = new Collider[32];

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
            if (!seen.Add(otherRb)) continue;   // deduplicate multi-collider cars

            Vector3 toCar = otherRb.position - transform.position;
            float   fwd   = Vector3.Dot(transform.forward, toCar);
            if (fwd <= 0f || fwd > avoidRayLen) continue;
            float lat = Vector3.Dot(transform.right, toCar);
            if (Mathf.Abs(lat) > 3.5f) continue;

            carInLane = true;
            if (fwd < closestFwd) { closestFwd = fwd; closestLat = lat; }
        }

        float steerRate = maxSteerAngle * 2.5f * Time.fixedDeltaTime;
        if (carInLane)
        {
            float side        = closestLat >= 0f ? -1f : 1f;
            float targetSteer = side * maxSteerAngle * laneShift;
            overtakeSteering  = Mathf.MoveTowards(overtakeSteering, targetSteer, steerRate);
        }
        else
        {
            overtakeSteering = Mathf.MoveTowards(overtakeSteering, 0f, steerRate * 0.5f);
        }

        steerOffset = overtakeSteering;

        if (!carInLane || closestFwd >= brakeDist) return 1f;

        // Full brake within hardBrakeDist — no override
        if (closestFwd <= hardBrakeDist) return 0f;

        // Throttle restores only when fully sidestepped AND lateral gap > 2.5 m
        bool fullySidestepped = Mathf.Abs(overtakeSteering) >= maxSteerAngle * laneShift * 0.9f
                             && Mathf.Abs(closestLat) > 2.5f;
        if (fullySidestepped) return 1f;

        return Mathf.Clamp01((closestFwd - hardBrakeDist) / (brakeDist - hardBrakeDist));
    }

    // Raycasts forward and diagonally for buildings/walls.
    // Steers away from the surface normal; brakes if too close.
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

        float diagRange  = buildingDetectRange * 0.5f;
        Vector3 leftDir  = (transform.forward * 4f - transform.right).normalized;
        Vector3 rightDir = (transform.forward * 4f + transform.right).normalized;

        if (Physics.Raycast(origin, leftDir,  diagRange, buildingLayer))
            steerOffset += maxSteerAngle * 0.3f;   // nudge right
        if (Physics.Raycast(origin, rightDir, diagRange, buildingLayer))
            steerOffset -= maxSteerAngle * 0.3f;   // nudge left

        return 1f;
    }

    // Cross-traffic brake — live OverlapSphere, closing-rate guard prevents
    // startup deadlock when cars spawn near each other.
    private float JunctionBrake()
    {
        float minScale = 1f;

        int count = Physics.OverlapSphereNonAlloc(transform.position, junctionRadius, _overlapBuf);
        var seen  = new HashSet<Rigidbody>();

        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapBuf[i];
            if (col.isTrigger) continue;

            Rigidbody otherRb = col.GetComponentInParent<Rigidbody>();
            if (otherRb == null || otherRb == rb) continue;
            if (!seen.Add(otherRb)) continue;

            Vector3 toCar = otherRb.position - transform.position;
            float   dist  = toCar.magnitude;
            if (dist < 0.5f) continue;

            float fwdFrac = Vector3.Dot(transform.forward, toCar) / dist;
            if (fwdFrac >  0.6f) continue;   // ahead — ObstacleAvoid handles it
            if (fwdFrac < -0.3f) continue;   // behind — no threat

            // Only brake if this car is actually closing in (avoid startup freeze)
            Vector3 relVel      = rb.linearVelocity - otherRb.linearVelocity;
            float   closingRate = Vector3.Dot(relVel, toCar.normalized);
            if (closingRate < 0.5f) continue;

            float scale = dist <= hardBrakeDist ? 0f :
                          Mathf.Clamp01((dist - hardBrakeDist) / (junctionRadius - hardBrakeDist));
            if (scale < minScale) minScale = scale;
        }
        return minScale;
    }

    // ─────────────────────────────────────────────────────────────
    // Recovery — reverse straight back
    // ─────────────────────────────────────────────────────────────

    private void DoRecover()
    {
        appliedSteer = Mathf.MoveTowards(appliedSteer, 0f, steerRate * Time.fixedDeltaTime);
        flWC.steerAngle = appliedSteer;
        frWC.steerAngle = appliedSteer;

        rlWC.motorTorque = -motorForce * 0.4f;
        rrWC.motorTorque = -motorForce * 0.4f;

        flWC.brakeTorque = 0f;
        frWC.brakeTorque = 0f;
        rlWC.brakeTorque = 0f;
        rrWC.brakeTorque = 0f;
    }

    // ─────────────────────────────────────────────────────────────
    // Path builder — reads LineRenderer from the lane path object
    // ─────────────────────────────────────────────────────────────

    private void BuildPath()
    {
        // 1. Try to find the specific lane by name under "Car Paths"
        if (waypointParent == null)
        {
            var laneGo = GameObject.Find("Car Paths/" + laneName)
                      ?? GameObject.Find(laneName);
            if (laneGo != null)
            {
                waypointParent = laneGo.transform;
                Debug.Log($"[AI] Found lane: '{laneGo.name}'");
            }
        }

        // 2. Fallback: use "Car Paths" root itself
        if (waypointParent == null)
        {
            var go = GameObject.Find("Car Paths");
            if (go != null) waypointParent = go.transform;
        }

        if (waypointParent == null) { Debug.LogWarning("[AI] No path found!"); return; }

        // 3. Prefer LineRenderer — Path (Script) draws the lane with one
        var lr = waypointParent.GetComponent<LineRenderer>();
        if (lr != null && lr.positionCount >= 2)
        {
            BuildFromLineRenderer(lr);
            return;
        }
        // Also check direct children for a LineRenderer
        foreach (Transform child in waypointParent)
        {
            lr = child.GetComponent<LineRenderer>();
            if (lr != null && lr.positionCount >= 2)
            {
                BuildFromLineRenderer(lr);
                return;
            }
        }

        // 4. Fallback: use child transform positions as waypoints
        if (waypointParent.childCount < 2)
        {
            Debug.LogWarning($"[AI] '{waypointParent.name}': no LineRenderer and <2 children. Assign the correct path in Inspector.");
            return;
        }
        int n   = waypointParent.childCount;
        var pts = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 a = waypointParent.GetChild(i).position;
            Vector3 b = waypointParent.GetChild(i + 1).position;
            for (int s = 0; s < 15; s++)
                pts.Add(Vector3.Lerp(a, b, s / 15f));
        }
        pts.Add(waypointParent.GetChild(n - 1).position);
        path = pts.ToArray();
        ApplyBestStart();
        Debug.Log($"[AI] Path (child transforms): {n} waypoints → {path.Length} pts, starting at {wpIdx}");
        DrawDebugPath();
    }

    private void BuildFromLineRenderer(LineRenderer lr)
    {
        int n   = lr.positionCount;
        var pts = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 a = lr.GetPosition(i);
            Vector3 b = lr.GetPosition(i + 1);
            for (int s = 0; s < 15; s++)
                pts.Add(Vector3.Lerp(a, b, s / 15f));
        }
        pts.Add(lr.GetPosition(n - 1));
        path = pts.ToArray();
        ApplyBestStart();
        Debug.Log($"[AI] Path (LineRenderer on '{lr.gameObject.name}'): {n} pts → {path.Length} pts, starting at {wpIdx}");
        DrawDebugPath();
    }

    private void ApplyBestStart()
    {
        int best = 0; float bestScore = float.MinValue;
        for (int i = 0; i < path.Length; i++)
        {
            Vector3 d  = path[i] - transform.position;
            float   sc = Vector3.Dot(transform.forward, d.normalized) - d.magnitude * 0.01f;
            if (sc > bestScore) { bestScore = sc; best = i; }
        }
        wpIdx = best;
    }

    private void DrawDebugPath()
    {
        for (int i = 0; i < path.Length - 1; i++)
            Debug.DrawLine(path[i] + Vector3.up, path[i + 1] + Vector3.up, Color.cyan, 60f);
    }

    // ─────────────────────────────────────────────────────────────
    // Enable colliders on all blue AI cars so avoidance rays hit them
    // ─────────────────────────────────────────────────────────────

    private void EnableAICarColliders()
    {
        string[] parentNames = { "AI Cars", "AI Cars Red", "AI Cars Neon", "AI Cars neon", "AI Cars red", "AICars", "BlueCars" };
        int total = 0;
        foreach (string pname in parentNames)
        {
            var go = GameObject.Find(pname);
            if (go == null) continue;

            // Make every collider solid — do NOT touch Rigidbodies (AICarConroller owns them)
            int count = 0;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                // Skip WheelColliders — they are physics joints, not solid surfaces
                if (col is WheelCollider) continue;
                col.enabled   = true;
                col.isTrigger = false;
                count++;
            }

            if (count > 0)
            {
                Debug.Log($"[AI] Solid colliders enabled: {count} under '{pname}'");
                total += count;
            }
        }
        if (total == 0)
            Debug.LogWarning("[AI] No AI car colliders found — check parent names in Hierarchy.");
    }

    // ─────────────────────────────────────────────────────────────
    // Disable CarController and similar competing scripts
    // ─────────────────────────────────────────────────────────────

    private void DisableConflictingControllers()
    {
        foreach (var mb in GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb == this) continue;   // null guard: missing script slots return null
            string n = mb.GetType().Name.ToLower();
            if (n.Contains("car") && n.Contains("controller"))
            {
                mb.enabled = false;
                Debug.Log($"[AI] Disabled: '{mb.GetType().Name}'");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Frame capture → ONNX (throttle only)
    // ─────────────────────────────────────────────────────────────

    private void CaptureFrame()
    {
        RenderTexture prev = screenshotCamera.targetTexture;
        screenshotCamera.targetTexture = captureRT;
        screenshotCamera.Render();
        screenshotCamera.targetTexture = prev;
        readbackPending = true;
        AsyncGPUReadback.Request(captureRT, 0, TextureFormat.RGBA32, OnReadback);
    }

    private void OnReadback(AsyncGPUReadbackRequest req)
    {
        readbackPending = false;
        // Guard: callback can fire after OnDestroy disposes the worker
        if (this == null || worker == null) return;
        if (req.hasError) return;
        try
        {
            var raw   = req.GetData<byte>();
            var frame = new float[IC * IH * IW];
            for (int row = 0; row < IH; row++)
            {
                int src = IH - 1 - row;
                for (int col = 0; col < IW; col++)
                {
                    int s = (src * IW + col) * 4, b = row * IW + col;
                    frame[0 * IH * IW + b] = (raw[s]     / 255f - MEAN[0]) / STD[0];
                    frame[1 * IH * IW + b] = (raw[s + 1] / 255f - MEAN[1]) / STD[1];
                    frame[2 * IH * IW + b] = (raw[s + 2] / 255f - MEAN[2]) / STD[2];
                }
            }
            RunInfer(frame);
        }
        catch (System.Exception e) { Debug.LogError($"[AI] {e.Message}"); }
    }

    private void RunInfer(float[] frame)
    {
        if (worker == null) return;
        using var t = new Tensor<float>(new TensorShape(1, IC, IH, IW), frame);
        worker.SetInput(0, t);
        worker.Schedule();

        // PeekOutput by index — falls back to name "output" if index returns null
        var raw = worker.PeekOutput(0) as Tensor<float>
               ?? worker.PeekOutput("output") as Tensor<float>;
        if (raw == null) return; // model not ready yet, keep last throttle

        var cpu = raw.ReadbackAndClone();
        hudModelW     = Sigmoid(cpu[0, 0]);
        hudModelA     = Sigmoid(cpu[0, 1]);
        hudModelS     = Sigmoid(cpu[0, 2]);
        hudModelD     = cpu.shape[1] > 3 ? Sigmoid(cpu[0, 3]) : 0f;
        hudModelSpace = cpu.shape[1] > 4 ? Sigmoid(cpu[0, 4]) : 0f;
        cpu.Dispose();
        float rawThr = Mathf.Max(hudModelW - hudModelS, minThrottle);
        smoothedThrottle = Mathf.Lerp(smoothedThrottle, rawThr, 0.18f);
        throttle = smoothedThrottle;
    }

    // ─────────────────────────────────────────────────────────────
    // Wheel auto-detection
    // ─────────────────────────────────────────────────────────────

    private void DetectWheels()
    {
        foreach (var wc in GetComponentsInChildren<WheelCollider>())
        {
            Vector3 lp = transform.InverseTransformPoint(wc.transform.position);
            bool f = lp.z > 0f, l = lp.x < 0f;
            if (f && l)  { flWC = wc; flWT = wc.transform; }
            if (f && !l) { frWC = wc; frWT = wc.transform; }
            if (!f && l) { rlWC = wc; rlWT = wc.transform; }
            if (!f && !l){ rrWC = wc; rrWT = wc.transform; }
        }
        foreach (var t in GetComponentsInChildren<Transform>())
        {
            if (t.GetComponent<WheelCollider>() != null) continue;
            if (t.GetComponent<MeshRenderer>()  == null) continue;
            string n = t.name.ToLower();
            if (!n.Contains("wheel") && !n.Contains("tire") && !n.Contains("tyre")) continue;
            Vector3 lp = transform.InverseTransformPoint(t.position);
            bool f = lp.z > 0f, l = lp.x < 0f;
            if (f && l)  flWT = t;
            if (f && !l) frWT = t;
            if (!f && l) rlWT = t;
            if (!f && !l)rrWT = t;
        }
        Debug.Log($"[AI] Wheels FL={flWC?.name} FR={frWC?.name} RL={rlWC?.name} RR={rrWC?.name}");
    }

    private void DetectCamera()
    {
        if (screenshotCamera != null) return;
        screenshotCamera = GetComponentInChildren<Camera>()
                        ?? Camera.main
                        ?? FindFirstObjectByType<Camera>();
    }

    // ─────────────────────────────────────────────────────────────
    // Wheel sync
    // ─────────────────────────────────────────────────────────────

    private void SyncWheels()
    {
        Sync(flWC, flWT); Sync(frWC, frWT);
        Sync(rlWC, rlWT); Sync(rrWC, rrWT);
    }

    private static void Sync(WheelCollider c, Transform t)
    {
        if (c == null || t == null) return;
        c.GetWorldPose(out Vector3 p, out Quaternion r);
        t.SetPositionAndRotation(p, r);
    }

    private bool IsSignalAhead(Vector3 lightPos, float maxDist)
    {
        Vector3 toLight = lightPos - transform.position;
        if (toLight.magnitude > maxDist) return false;
        return Vector3.Dot(transform.forward, toLight.normalized) > 0.4f;
    }

    private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));
}

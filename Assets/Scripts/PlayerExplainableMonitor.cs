using UnityEngine;

/// <summary>
/// Draws a real-time "Explainable AI" style overlay for the player car.
/// Attach to any GameObject in the scene (e.g. the player car itself or a
/// dedicated HUD object).  Press [M] (or set toggleKey in Inspector) to
/// show / hide the panel.
/// </summary>
[DefaultExecutionOrder(200)]
public class PlayerExplainableMonitor : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CarController playerCar;
    [SerializeField] private KeyCode       toggleKey = KeyCode.M;

    [Header("Risk Detection")]
    [SerializeField] private float     vehicleDetectRange  = 18f;
    [SerializeField] private float     buildingDetectRange = 12f;
    [SerializeField] private LayerMask buildingLayer;

    // ── Runtime ───────────────────────────────────────────────────
    private bool      _visible = true;
    private Rigidbody _rb;

    // Detected wheel colliders — read physics state directly from wheels
    private WheelCollider _wcFL, _wcFR, _wcRL, _wcRR;

    // Live telemetry — refreshed every Update() directly from Input + Rigidbody
    private float  _speed;
    private float  _hInput;
    private float  _vInput;
    private bool   _braking;
    private float  _steerDeg;
    private float  _brkForce;
    private float  _brkMax   = 3000f;
    private float  _maxSt    = 30f;
    private bool   _redStop;
    private TrafficLight.State? _sig;

    private bool   _predBrake;   // red light is detected ahead — braking phase
    private bool   _vehRisk;
    private float  _vehDist  = float.MaxValue;
    private string _vehName  = "none";
    private bool   _bldRisk;
    private float  _bldDist  = float.MaxValue;
    private string _bldName  = "none";

    // ── GUI styles (built on first OnGUI call) ────────────────────
    private bool     _stylesReady;
    private GUIStyle _sTitle, _sInfo, _sInfoR,
                     _sTileH, _sTileV, _sTileS,
                     _sExpHead, _sExpBody,
                     _sFlagH, _sFlag,
                     _sBotH, _sBotB,
                     _sStatus,
                     _sBtnDrive, _sBtnHide, _sBtnShow;

    private static readonly Collider[] _buf = new Collider[32];

    // ── Color palette ─────────────────────────────────────────────
    static readonly Color Cd    = new Color(0.050f, 0.080f, 0.140f, 0.97f);
    static readonly Color Cp    = new Color(0.070f, 0.105f, 0.175f, 1.00f);
    static readonly Color Ct    = new Color(0.090f, 0.130f, 0.205f, 1.00f);
    static readonly Color Ca    = new Color(0.000f, 0.510f, 0.660f, 1.00f);
    static readonly Color Cbar  = new Color(0.035f, 0.060f, 0.115f, 1.00f);
    static readonly Color Ccyan = new Color(0.420f, 0.890f, 1.000f, 1.00f);
    static readonly Color Cwh   = Color.white;
    static readonly Color Cyel  = new Color(1.000f, 0.870f, 0.200f, 1.00f);
    static readonly Color Cgr   = new Color(0.220f, 0.900f, 0.340f, 1.00f);
    static readonly Color Cred  = new Color(0.940f, 0.260f, 0.200f, 1.00f);
    static readonly Color Corg  = new Color(1.000f, 0.610f, 0.100f, 1.00f);
    static readonly Color Csub  = new Color(0.710f, 0.810f, 0.910f, 1.00f);
    static readonly Color CgBtn = new Color(0.140f, 0.560f, 0.230f, 1.00f);
    static readonly Color CrBtn = new Color(0.550f, 0.130f, 0.110f, 1.00f);

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
        TryBindCar();
    }

    // This script sits ON the Player Car — search this GameObject and its
    // children/parents directly instead of hunting through the scene.
    void TryBindCar()
    {
        // ── Rigidbody: check self, then walk up the hierarchy ─────────
        if (_rb == null)
        {
            Transform t = transform;
            while (t != null && _rb == null)
            {
                _rb = t.GetComponent<Rigidbody>();
                t   = t.parent;
            }
        }
        if (_rb == null)
            _rb = GetComponentInChildren<Rigidbody>();

        // ── CarController: same object or children ────────────────────
        if (playerCar == null)
        {
            playerCar = GetComponent<CarController>()
                     ?? GetComponentInChildren<CarController>();
        }
        if (playerCar == null)
        {
            Transform t = transform.parent;
            while (t != null && playerCar == null)
            {
                playerCar = t.GetComponent<CarController>();
                t = t.parent;
            }
        }

        // ── WheelColliders: scan all children of this car root ────────
        if (_wcFL == null)
        {
            Transform root = transform;
            while (root.parent != null) root = root.parent;

            foreach (var wc in root.GetComponentsInChildren<WheelCollider>())
            {
                Vector3 lp   = root.InverseTransformPoint(wc.transform.position);
                bool isFront = lp.z > 0f;
                bool isLeft  = lp.x < 0f;
                if ( isFront &&  isLeft && _wcFL == null) _wcFL = wc;
                if ( isFront && !isLeft && _wcFR == null) _wcFR = wc;
                if (!isFront &&  isLeft && _wcRL == null) _wcRL = wc;
                if (!isFront && !isLeft && _wcRR == null) _wcRR = wc;
            }
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) _visible = !_visible;

        if (playerCar == null || _rb == null || _wcFL == null) TryBindCar();

        // ── Speed: always from Rigidbody — never from telemetry ──────
        _speed = _rb != null ? _rb.linearVelocity.magnitude : 0f;

        // ── Steering: actual front-wheel steer angle ──────────────────
        if (_wcFL != null || _wcFR != null)
        {
            float fl = _wcFL != null ? _wcFL.steerAngle : 0f;
            float fr = _wcFR != null ? _wcFR.steerAngle : 0f;
            _steerDeg = (_wcFL != null && _wcFR != null) ? (fl + fr) * 0.5f
                      : (_wcFL != null ? fl : fr);
        }

        // ── Throttle: rear-wheel motor torque, normalised ─────────────
        if (_wcRL != null || _wcRR != null)
        {
            float rl = _wcRL != null ? _wcRL.motorTorque : 0f;
            float rr = _wcRR != null ? _wcRR.motorTorque : 0f;
            float rawTorque = (_wcRL != null && _wcRR != null) ? (rl + rr) * 0.5f
                            : (_wcRL != null ? rl : rr);
            float maxMotor = (playerCar != null && playerCar.telMotorForce > 0f)
                           ? playerCar.telMotorForce : 1500f;
            _vInput = Mathf.Clamp(rawTorque / maxMotor, -1f, 1f);
        }

        // ── Brake: front-wheel brake torque ───────────────────────────
        if (_wcFL != null || _wcFR != null)
        {
            float bl = _wcFL != null ? _wcFL.brakeTorque : 0f;
            float br = _wcFR != null ? _wcFR.brakeTorque : 0f;
            _brkForce = (_wcFL != null && _wcFR != null) ? (bl + br) * 0.5f
                      : (_wcFL != null ? bl : br);
            _braking  = _brkForce > 10f;
        }

        // ── Supplementary telemetry from CarController if available ───
        if (playerCar != null)
        {
            if (playerCar.telMaxBrakeForce > 0f) _brkMax = playerCar.telMaxBrakeForce;
            if (playerCar.telMaxSteerAngle > 0f) _maxSt  = playerCar.telMaxSteerAngle;
            _redStop = playerCar.telStoppedForRed;
        }
        else
        {
            _redStop = false;
        }

        // ── Traffic signal state ──────────────────────────────────────
        _sig = NearestSignalAhead();

        // ── Predictive brake: red light detected ahead ────────────────
        Transform carTr = playerCar != null ? playerCar.transform
                        : (_rb != null      ? _rb.transform : null);
        _predBrake = carTr != null && TrafficLightManager.NearestRedAhead(carTr) != null;

        if (_visible) ScanObstacles();
    }

    // ─────────────────────────────────────────────────────────────
    // Risk scan — forward vehicles + building raycast
    // ─────────────────────────────────────────────────────────────
    void ScanObstacles()
    {
        _vehRisk = false; _bldRisk = false;
        _vehDist = float.MaxValue; _bldDist = float.MaxValue;
        _vehName = "none"; _bldName = "none";

        Transform tr = playerCar != null ? playerCar.transform
                     : (_rb != null      ? _rb.transform : null);
        if (tr == null) return;

        int n = Physics.OverlapSphereNonAlloc(tr.position, vehicleDetectRange, _buf);
        for (int i = 0; i < n; i++)
        {
            if (_buf[i].isTrigger) continue;
            Rigidbody r2 = _buf[i].GetComponentInParent<Rigidbody>();
            if (r2 == null || r2 == _rb) continue;
            if (r2.GetComponent<AICarController>() == null &&
                r2.GetComponent<onnxcontroller>()  == null) continue;

            Vector3 toCar = r2.position - tr.position;
            if (Vector3.Dot(tr.forward, toCar) <= 0f) continue;
            float dist = toCar.magnitude;
            if (dist < _vehDist)
            {
                _vehDist = dist;
                _vehRisk = true;
                _vehName = r2.gameObject.name;
            }
        }

        if (buildingLayer != 0)
        {
            Vector3 org = tr.position + Vector3.up * 0.5f;
            if (Physics.Raycast(org, tr.forward, out RaycastHit hit, buildingDetectRange, buildingLayer))
            {
                _bldRisk = true;
                _bldDist = hit.distance;
                _bldName = hit.collider.gameObject.name;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Nearest traffic light ahead — any state, direct registry query
    // ─────────────────────────────────────────────────────────────
    TrafficLight.State? NearestSignalAhead()
    {
        Transform src = playerCar != null ? playerCar.transform
                      : (_rb != null      ? _rb.transform : null);
        if (src == null) return null;
        TrafficLight best  = null;
        float        bestS = float.MaxValue;
        Vector3      pos   = src.position;
        Vector3      fwd   = src.forward;
        Vector3      right = src.right;

        foreach (var tl in TrafficLightManager.AllLights)
        {
            if (tl == null) continue;
            Vector3 toLight = tl.transform.position - pos;
            toLight.y = 0f;
            float dist = toLight.magnitude;
            if (dist > 35f) continue;
            float dot = dist > 0.1f ? Vector3.Dot(fwd, toLight / dist) : 1f;
            if (dot < 0.2f) continue;
            float lat = Mathf.Abs(Vector3.Dot(right, toLight));
            if (lat > 12f) continue;
            float s = dist + lat * 1.5f;
            if (s < bestS) { bestS = s; best = tl; }
        }
        return best?.CurrentState;
    }

    // ─────────────────────────────────────────────────────────────
    // OnGUI
    // ─────────────────────────────────────────────────────────────
    void OnGUI()
    {
        if (!_stylesReady) BuildStyles();

        // ── Collapsed state: small show button ───────────────────
        if (!_visible)
        {
            if (GUI.Button(new Rect(10, 10, 210, 36), "▶  PLAYER MONITOR  ON", _sBtnShow))
                _visible = true;
            return;
        }

        // ── Live snapshot — all values set in Update() this frame ────
        float spd   = _speed;
        float deg   = _steerDeg;
        float thr   = _vInput;
        float brkF  = _brkForce;
        float brkM  = _brkMax;
        float maxSt = _maxSt;
        bool  brk   = _braking;
        bool  redSt = _redStop;
        TrafficLight.State? sig = _sig;

        // ── Panel bounds ──────────────────────────────────────────
        float sw = Screen.width, sh = Screen.height;
        float pw = Mathf.Min(sw - 40f, 1060f);
        float ph = Mathf.Min(sh - 40f, 664f);
        float px = (sw - pw) * 0.5f;
        float py = 20f;

        DrawR(new Rect(px, py, pw, ph), Cd);
        DrawB(new Rect(px, py, pw, ph), Ca, 2);

        float y = py;

        // ── Sections ──────────────────────────────────────────────
        y = TitleBar  (px, y, pw, redSt);
        y = InfoBar   (px, y, pw, spd, deg);
        y = TilesRow  (px, y, pw, spd, deg, thr, brkF, brkM, maxSt);

        // Middle: explanation panel (left) + live flags (right)
        float midH = ph - (y - py) - 130f - 35f - 8f;
        if (midH < 100f) midH = 100f;

        float lw = pw * 0.62f - 2f;
        float rw = pw - lw - 6f;

        DrawR(new Rect(px,          y, lw, midH), Cp);
        DrawB(new Rect(px,          y, lw, midH), Ca, 1);
        ExplainPanel(new Rect(px, y, lw, midH), spd, deg, thr, brk, brkF, brkM, redSt, sig);

        DrawR(new Rect(px + lw + 4, y, rw, midH), Cp);
        DrawB(new Rect(px + lw + 4, y, rw, midH), Ca, 1);
        FlagsPanel(new Rect(px + lw + 4, y, rw, midH), brk, brkF, redSt);

        y += midH + 4f;

        // Bottom panels
        float bh = 126f;
        float hw = (pw - 4f) * 0.5f;

        DrawR(new Rect(px,          y, hw, bh), Cp);
        DrawB(new Rect(px,          y, hw, bh), Ca, 1);
        DriverPanel(new Rect(px, y, hw, bh), brk, brkF, brkM, deg, thr, spd);

        DrawR(new Rect(px + hw + 4, y, hw, bh), Cp);
        DrawB(new Rect(px + hw + 4, y, hw, bh), Ca, 1);
        SafetyPanel(new Rect(px + hw + 4, y, hw, bh), spd);

        y += bh + 4f;

        // Status bar
        DrawR(new Rect(px, y, pw, 31), Cbar);
        StatusBar(new Rect(px, y, pw, 31), spd, brkF, redSt);
    }

    // ─────────────────────────────────────────────────────────────
    // Title bar
    // ─────────────────────────────────────────────────────────────
    float TitleBar(float x, float y, float w, bool redSt)
    {
        DrawR(new Rect(x, y, w, 52), Cbar);
        GUI.Label(new Rect(x + 16, y + 9, w * 0.65f, 36),
            "PLAYER EXPLAINABLE MONITOR · LIVE", _sTitle);

        // State indicator (not a button — just a colored label)
        Color stateCol = redSt ? Cred : CgBtn;
        DrawR(new Rect(x + w - 234, y + 10, 114, 32), stateCol);
        GUI.Label(new Rect(x + w - 234, y + 10, 114, 32),
            redSt ? "STOP" : "DRIVE", _sBtnDrive);

        // HIDE button (interactive)
        if (GUI.Button(new Rect(x + w - 112, y + 10, 92, 32), "HIDE", _sBtnHide))
            _visible = false;

        return y + 52f;
    }

    // ─────────────────────────────────────────────────────────────
    // Info bar
    // ─────────────────────────────────────────────────────────────
    float InfoBar(float x, float y, float w, float spd, float deg)
    {
        DrawR(new Rect(x, y, w, 28), new Color(0.058f, 0.088f, 0.158f, 1f));
        string obj     = (_vehRisk || _bldRisk) ? "Object DETECTED" : "Object CLEAR";
        string carName = playerCar != null ? playerCar.gameObject.name : "Player Car";
        GUI.Label(new Rect(x + 14, y + 5, w * 0.68f, 20),
            $"Player car {carName}  |  Speed {spd:F1} m/s  |  Steering {deg:F1} deg  |  {obj}",
            _sInfo);
        GUI.Label(new Rect(x + w - 258, y + 5, 246, 20),
            $"Player telemetry locked  [{toggleKey}] panel", _sInfoR);
        return y + 28f;
    }

    // ─────────────────────────────────────────────────────────────
    // 4-tile data row
    // ─────────────────────────────────────────────────────────────
    float TilesRow(float x, float y, float w,
                   float spd, float deg, float thr,
                   float brkF, float brkM, float maxSt)
    {
        float tw = (w - 6f) / 4f;
        float g  = 2f;
        float h  = 98f;

        // SPEED
        Tile(new Rect(x, y, tw, h), "SPEED",
            $"{spd:F1} m/s",
            brkF > 0f
                ? $"BRAKE ON  ({brkF:F0}/{brkM:F0})"
                : $"BRAKE OFF  (0/{brkM:F0})",
            brkF > 0f ? Cred : Csub);

        // STEERING + INPUT
        Tile(new Rect(x + (tw + g), y, tw, h), "STEERING + INPUT",
            $"{deg:F1} deg",
            $"Throttle {thr:F2}  |  User brake {(brkF > 0f ? "ON" : "OFF")}",
            Csub);

        // PLAYER PATH  — pred = 2.5 s lookahead; steer demand normalised
        float pred = spd * 2.5f;
        float sdem = maxSt > 0f ? deg / maxSt : 0f;
        Tile(new Rect(x + (tw + g) * 2, y, tw, h), "PLAYER PATH",
            $"Pred {pred:F1}m",
            $"Steer demand {sdem:F2}",
            Csub);

        // PREDICTIVE RISK
        RiskTile(new Rect(x + (tw + g) * 3, y, tw, h));

        return y + h + 4f;
    }

    void Tile(Rect r, string head, string val, string sub, Color subCol)
    {
        DrawR(r, Ct);
        DrawB(r, new Color(Ca.r, Ca.g, Ca.b, 0.35f), 1);
        float ix = r.x + 10f, iy = r.y + 8f;
        GUI.Label(new Rect(ix, iy,       r.width - 14f, 16f), head, _sTileH);
        GUI.Label(new Rect(ix, iy + 19f, r.width - 14f, 28f), val,  _sTileV);
        var ss = new GUIStyle(_sTileS);
        ss.normal.textColor = subCol;
        GUI.Label(new Rect(ix, iy + 51f, r.width - 14f, 16f), sub, ss);
    }

    void RiskTile(Rect r)
    {
        DrawR(r, Ct);
        DrawB(r, new Color(Ca.r, Ca.g, Ca.b, 0.35f), 1);
        float ix = r.x + 10f, iy = r.y + 8f;
        GUI.Label(new Rect(ix, iy, r.width - 14f, 16f), "PREDICTIVE RISK", _sTileH);

        string vd = _vehRisk ? $"{_vehDist:F1}m" : "none";
        string bd = _bldRisk ? $"{_bldDist:F1}m" : "none";

        var vs = new GUIStyle(_sTileS); vs.normal.textColor = _vehRisk ? Corg : Csub;
        var bs = new GUIStyle(_sTileS); bs.normal.textColor = _bldRisk ? Corg : Csub;
        GUI.Label(new Rect(ix, iy + 21f, r.width - 14f, 16f),
            $"Vehicle {(_vehRisk ? "YES" : "NO ")}  ({vd})", vs);
        GUI.Label(new Rect(ix, iy + 44f, r.width - 14f, 28f),
            $"Building/Obstacle {(_bldRisk ? "YES" : "NO ")}  ({bd})", bs);
    }

    // ─────────────────────────────────────────────────────────────
    // Explanation panel
    // ─────────────────────────────────────────────────────────────
    void ExplainPanel(Rect r, float spd, float deg, float thr,
                      bool brk, float brkF, float brkM,
                      bool redSt, TrafficLight.State? sig)
    {
        float ix = r.x + 14f, iy = r.y + 12f;

        string head, body;

        if (redSt)
        {
            head = "Predictive hold: held at red signal stop line.";
            body = "Player car has committed to a full stop at the red light. " +
                   "Brake torque is holding all four wheels. Awaiting GREEN signal before resuming.";
        }
        else if (_predBrake)
        {
            head = "Predictive brake: decelerating for red light ahead.";
            body = "A red traffic signal is detected ahead. Player car is applying brakes progressively " +
                   "to stop before the line. Throttle is suppressed until the signal turns GREEN.";
        }
        else if (brk && brkF > 0f)
        {
            head = "Driver brake applied: manual braking active.";
            body = $"Space-bar brake is engaged. Current brake force: {brkF:F0} / {brkM:F0} N. " +
                   "All four wheels receiving brake torque. Vehicle speed will reduce.";
        }
        else if (_vehRisk && _vehDist < 8f)
        {
            head = $"Vehicle conflict: proximity warning — {_vehDist:F1} m ahead.";
            body = $"Vehicle '{_vehName}' detected {_vehDist:F1} m ahead in the forward arc. " +
                   $"Apply brakes (Space) or steer to avoid collision. Current speed: {spd:F1} m/s.";
        }
        else if (_bldRisk && _bldDist < 6f)
        {
            head = $"Obstacle warning: building / wall {_bldDist:F1} m ahead.";
            body = $"Static obstacle '{_bldName}' is {_bldDist:F1} m ahead on current heading. " +
                   "Steer away or brake immediately to avoid impact.";
        }
        else if (_vehRisk)
        {
            head = $"Vehicle detected: {_vehDist:F1} m ahead — monitoring distance.";
            body = $"AI vehicle '{_vehName}' is {_vehDist:F1} m ahead. No immediate collision risk at " +
                   $"current speed ({spd:F1} m/s). Continue monitoring and maintain safe following distance.";
        }
        else if (_bldRisk)
        {
            head = $"Building / obstacle in range: {_bldDist:F1} m ahead.";
            body = $"Obstacle '{_bldName}' is {_bldDist:F1} m on current heading. " +
                   "No immediate danger — adjust course if heading directly toward it.";
        }
        else if (spd < 0.5f)
        {
            head = "Vehicle stationary: awaiting driver input.";
            body = "No forward motion detected. Throttle input is at idle. " +
                   "Apply throttle (W / Up Arrow) to accelerate, or steer to resume.";
        }
        else
        {
            head = "Cruise reason: no active brake trigger.";
            body = $"No brake trigger is active. Car is continuing under driver throttle/steering input.";
        }

        GUI.Label(new Rect(ix, iy, r.width - 22f, 20f), head, _sExpHead);
        // Separator
        DrawR(new Rect(ix, iy + 23f, r.width - 22f, 1f), new Color(Ca.r, Ca.g, Ca.b, 0.45f));
        GUI.Label(new Rect(ix, iy + 29f, r.width - 22f, r.height - 50f), body, _sExpBody);
    }

    // ─────────────────────────────────────────────────────────────
    // Live flags panel
    // ─────────────────────────────────────────────────────────────
    void FlagsPanel(Rect r, bool brk, float brkF, bool redSt)
    {
        float ix = r.x + 10f, iy = r.y + 8f;
        GUI.Label(new Rect(ix, iy, r.width - 12f, 16f), "Live Flags", _sFlagH);

        float ly = iy + 22f;
        float lh = 18f;

        bool brkActive = brkF > 0f;
        string vStr = _vehRisk ? $"YES  ({_vehDist:F1}m)" : "NO  (none)";
        string bStr = _bldRisk ? $"YES  ({_bldDist:F1}m)" : "NO  (none)";

        Flag(ix, ly + lh * 0, r.width - 14f, "Driver brake key:",       brk        ? "ON"       : "OFF",      brk);
        Flag(ix, ly + lh * 1, r.width - 14f, "Brake torque active:",    brkActive  ? "ON"       : "OFF",      brkActive);
        Flag(ix, ly + lh * 2, r.width - 14f, "Predictive brake:",       _predBrake ? "ON"       : "OFF",      _predBrake);
        Flag(ix, ly + lh * 3, r.width - 14f, "Predictive hold:",        redSt      ? "active"   : "inactive", redSt);
        Flag(ix, ly + lh * 4, r.width - 14f, "Vehicle risk:",           vStr,                                 _vehRisk);
        Flag(ix, ly + lh * 5, r.width - 14f, "Building/Obstacle risk:", bStr,                                 _bldRisk);
    }

    void Flag(float x, float y, float w, string label, string val, bool alert)
    {
        GUI.Label(new Rect(x, y, w * 0.56f, 16f), "– " + label, _sFlag);
        var vs = new GUIStyle(_sFlag);
        vs.normal.textColor = alert ? Corg : Cgr;
        GUI.Label(new Rect(x + w * 0.56f, y, w * 0.44f, 16f), val, vs);
    }

    // ─────────────────────────────────────────────────────────────
    // Driver + Control Signals
    // ─────────────────────────────────────────────────────────────
    void DriverPanel(Rect r, bool brk, float brkF, float brkM,
                     float deg, float thr, float spd)
    {
        float ix = r.x + 12f, iy = r.y + 10f;
        GUI.Label(new Rect(ix, iy, r.width - 14f, 16f), "Driver + Control Signals", _sBotH);
        DrawR(new Rect(ix, iy + 18f, r.width - 20f, 1f), new Color(Ca.r, Ca.g, Ca.b, 0.4f));

        float by = iy + 26f;
        GUI.Label(new Rect(ix, by,       r.width - 14f, 18f),
            $"User brake input: {(brk ? "ON" : "OFF")}.  Current brake force: {brkF:F0}/{brkM:F0}.",
            _sBotB);
        GUI.Label(new Rect(ix, by + 22f, r.width - 14f, 18f),
            $"Steering angle: {deg:F1} deg.  Throttle input: {thr:F2}.",
            _sBotB);
        GUI.Label(new Rect(ix, by + 46f, r.width - 14f, 18f),
            $"Speed: {spd:F1} m/s  ({spd * 3.6f:F1} km/h).",
            _sBotB);
    }

    // ─────────────────────────────────────────────────────────────
    // Player Safety Rationale
    // ─────────────────────────────────────────────────────────────
    void SafetyPanel(Rect r, float spd)
    {
        float ix = r.x + 12f, iy = r.y + 10f;
        GUI.Label(new Rect(ix, iy, r.width - 14f, 16f), "Player Safety Rationale", _sBotH);
        DrawR(new Rect(ix, iy + 18f, r.width - 20f, 1f), new Color(Ca.r, Ca.g, Ca.b, 0.4f));

        float by = iy + 26f;
        string vConfl = _vehRisk ? $"{_vehName}  ({_vehDist:F1}m)" : "none";
        string bConfl = _bldRisk ? $"{_bldName}  ({_bldDist:F1}m)" : "none";
        float  pred   = spd * 2.5f;

        GUI.Label(new Rect(ix, by,       r.width - 14f, 18f),
            $"Vehicle conflict:             {vConfl}", _sBotB);
        GUI.Label(new Rect(ix, by + 22f, r.width - 14f, 18f),
            $"Building/obstacle conflict:  {bConfl}", _sBotB);
        GUI.Label(new Rect(ix, by + 46f, r.width - 14f, 18f),
            $"Predicted path length:        {pred:F1}m.", _sBotB);
    }

    // ─────────────────────────────────────────────────────────────
    // Status bar
    // ─────────────────────────────────────────────────────────────
    void StatusBar(Rect r, float spd, float brkF, bool redSt)
    {
        string state = redSt     ? "STOP"     : "DRIVE";
        string bStr  = brkF > 0f ? "BRAKE ON" : "BRAKE OFF";
        string vStr  = _vehRisk  ? "YES"       : "NO";
        string oStr  = _bldRisk  ? "YES"       : "NO";
        string carName = playerCar != null ? playerCar.gameObject.name : "Player Car";
        GUI.Label(new Rect(r.x + 12f, r.y + 8f, r.width - 20f, 18f),
            $"PLAYER LIVE  |  State {state}  |  {bStr}  |  VehicleRisk {vStr}  " +
            $"|  ObstacleRisk {oStr}  |  {spd * 3.6f:F1} km/h  |  Press [{toggleKey}] to toggle",
            _sStatus);
    }

    // ─────────────────────────────────────────────────────────────
    // Drawing helpers
    // ─────────────────────────────────────────────────────────────
    void DrawR(Rect r, Color c)
    {
        var prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
    }

    void DrawB(Rect r, Color c, float t)
    {
        DrawR(new Rect(r.x,          r.y,          r.width,   t), c);
        DrawR(new Rect(r.x,          r.yMax - t,   r.width,   t), c);
        DrawR(new Rect(r.x,          r.y,          t, r.height), c);
        DrawR(new Rect(r.xMax - t,   r.y,          t, r.height), c);
    }

    Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    GUIStyle LblStyle(int sz, Color col,
                      FontStyle fs     = FontStyle.Normal,
                      TextAnchor align = TextAnchor.UpperLeft,
                      bool wrap        = false)
    {
        var s = new GUIStyle(GUI.skin.label)
        {
            fontSize  = sz,
            fontStyle = fs,
            wordWrap  = wrap,
            alignment = align
        };
        s.normal.textColor = col;
        return s;
    }

    GUIStyle BtnStyle(Color normalCol, Color hoverCol, Color activeCol)
    {
        var s = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            border    = new RectOffset(0, 0, 0, 0)
        };
        s.normal.background  = MakeTex(normalCol);
        s.hover.background   = MakeTex(hoverCol);
        s.active.background  = MakeTex(activeCol);
        s.focused.background = MakeTex(normalCol);
        s.normal.textColor   = Cwh;
        s.hover.textColor    = Cwh;
        s.active.textColor   = Cwh;
        s.focused.textColor  = Cwh;
        return s;
    }

    // ─────────────────────────────────────────────────────────────
    // Build all GUIStyles (called once inside OnGUI so skin is ready)
    // ─────────────────────────────────────────────────────────────
    void BuildStyles()
    {
        _sTitle   = LblStyle(20, Ccyan, FontStyle.Bold);
        _sInfo    = LblStyle(11, Csub);
        _sInfoR   = LblStyle(11, new Color(0.60f, 0.70f, 0.80f, 1f),
                             align: TextAnchor.MiddleRight);

        _sTileH   = LblStyle(10, Ccyan, FontStyle.Bold);
        _sTileV   = LblStyle(18, Cwh,   FontStyle.Bold);
        _sTileS   = LblStyle(10, Csub);

        _sExpHead = LblStyle(13, Cyel, FontStyle.Bold);
        _sExpBody = LblStyle(11, Csub, wrap: true);

        _sFlagH   = LblStyle(11, Cwh, FontStyle.Bold);
        _sFlag    = LblStyle(10, Csub);

        _sBotH    = LblStyle(11, Cwh, FontStyle.Bold);
        _sBotB    = LblStyle(10, Csub);

        _sStatus  = LblStyle(11, Csub);

        // State indicator label (centred, non-interactive)
        _sBtnDrive = LblStyle(13, Cwh, FontStyle.Bold, TextAnchor.MiddleCenter);

        // HIDE button  (red)
        _sBtnHide = BtnStyle(CrBtn,
                             new Color(0.70f, 0.18f, 0.15f, 1f),
                             new Color(0.38f, 0.09f, 0.07f, 1f));

        // SHOW button  (accent teal)
        _sBtnShow = BtnStyle(Ca,
                             new Color(0.12f, 0.64f, 0.80f, 1f),
                             new Color(0.04f, 0.34f, 0.44f, 1f));

        _stylesReady = true;
    }
}

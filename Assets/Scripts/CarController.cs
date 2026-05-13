using UnityEngine;

public class CarController : MonoBehaviour
{
    [HideInInspector] public TrafficLight nearestSignal;

    // Telemetry — read by PlayerExplainableMonitor
    [HideInInspector] public float telSpeed;
    [HideInInspector] public float telSteerAngle;
    [HideInInspector] public float telThrottle;
    [HideInInspector] public float telBrakeForce;
    [HideInInspector] public float telMaxBrakeForce;
    [HideInInspector] public float telMaxSteerAngle;
    [HideInInspector] public float telMotorForce;
    [HideInInspector] public bool  telIsBreaking;
    [HideInInspector] public bool  telStoppedForRed;

    private const string HORIZONTAL = "Horizontal";
    private const string VERTICAL   = "Vertical";

    private float horizontalInput;
    private float verticalInput;
    private float currentbreakForce;
    private bool  isBreaking;

    // Latched true when car commits to a red-light stop; cleared on green.
    private bool stoppedForRed;

    [SerializeField] private float motorForce;
    [SerializeField] private float breakForce;
    [SerializeField] private float maxSteerAngle;

    [SerializeField] private WheelCollider frontLeftWheelCollider;
    [SerializeField] private WheelCollider frontRightWheelCollider;
    [SerializeField] private WheelCollider rearLeftWheelCollider;
    [SerializeField] private WheelCollider rearRightWheelCollider;

    [SerializeField] private Transform frontLeftWheelTransform;
    [SerializeField] private Transform frontRightWheelTransform;
    [SerializeField] private Transform rearLeftWheelTransform;
    [SerializeField] private Transform rearRightWheelTransform;

    private Rigidbody rb;

    private void Start()
    {
        rb = GetComponent<Rigidbody>()
          ?? GetComponentInParent<Rigidbody>()
          ?? GetComponentInChildren<Rigidbody>();
    }

    private void FixedUpdate()
    {
        GetInput();
        HandleMotor();
        HandleSteering();
        UpdateWheels();
        UpdateTelemetry();
    }

    private void UpdateTelemetry()
    {
        telSpeed         = rb != null ? rb.linearVelocity.magnitude : 0f;
        telSteerAngle    = maxSteerAngle * horizontalInput;
        telThrottle      = verticalInput;
        telBrakeForce    = currentbreakForce;
        telMaxBrakeForce = breakForce;
        telMaxSteerAngle = maxSteerAngle;
        telMotorForce    = motorForce;
        telIsBreaking    = isBreaking;
        telStoppedForRed = stoppedForRed;
    }

    private void GetInput()
    {
        horizontalInput = Input.GetAxis(HORIZONTAL);
        verticalInput   = Input.GetAxis(VERTICAL);
        isBreaking      = Input.GetKey(KeyCode.Space);
    }

    // Distance from the light's position where the car should be fully stopped.
    private const float StopLineDist = 7f;
    // Distance at which braking begins (ramps up from 0 → full over this range).
    private const float BrakeZoneDist = 35f;

    private void HandleMotor()
    {
        // ── Red light — proportional braking ─────────────────────────
        var tl = TrafficLightManager.NearestRedAhead(transform, 50f);
        if (tl != null)
        {
            Vector3 toLight = tl.transform.position - transform.position;
            toLight.y = 0f;
            float dist = toLight.magnitude;

            rearLeftWheelCollider.motorTorque  = 0f;
            rearRightWheelCollider.motorTorque = 0f;

            // How far the car still needs to travel before the stop line.
            float distToStop = dist - StopLineDist;

            if (distToStop <= 0f)
            {
                // At or past the stop line — full brakes and freeze.
                stoppedForRed     = true;
                currentbreakForce = breakForce;
            }
            else if (distToStop < BrakeZoneDist)
            {
                // Proportional ramp: light at the edge of the zone, increases toward stop line.
                float t = 1f - (distToStop / BrakeZoneDist);
                // Ease-in curve so deceleration feels natural, not sudden.
                currentbreakForce = breakForce * (t * t);
            }
            else
            {
                currentbreakForce = 0f;
            }

            ApplyBreaking();

            // Hold perfectly still at the stop line.
            if (stoppedForRed && rb != null)
            {
                rb.linearVelocity  = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            return;
        }

        // Light turned green — release the latch and resume normal input.
        stoppedForRed = false;

        // ── Normal player input ───────────────────────────────────────
        rearLeftWheelCollider.motorTorque  = verticalInput * motorForce;
        rearRightWheelCollider.motorTorque = verticalInput * motorForce;
        currentbreakForce = isBreaking ? breakForce : 0f;
        ApplyBreaking();
    }

    private void ApplyBreaking()
    {
        frontRightWheelCollider.brakeTorque = currentbreakForce;
        frontLeftWheelCollider.brakeTorque  = currentbreakForce;
        rearLeftWheelCollider.brakeTorque   = currentbreakForce * 0.6f;
        rearRightWheelCollider.brakeTorque  = currentbreakForce * 0.6f;
    }

    private void HandleSteering()
    {
        float currentSteerAngle = maxSteerAngle * horizontalInput;
        frontLeftWheelCollider.steerAngle  = currentSteerAngle;
        frontRightWheelCollider.steerAngle = currentSteerAngle;
    }

    private void UpdateWheels()
    {
        UpdateSingleWheel(frontLeftWheelCollider, frontLeftWheelTransform);
        UpdateSingleWheel(frontRightWheelCollider, frontRightWheelTransform);
        UpdateSingleWheel(rearRightWheelCollider, rearRightWheelTransform);
        UpdateSingleWheel(rearLeftWheelCollider, rearLeftWheelTransform);
    }

    private void UpdateSingleWheel(WheelCollider wheelCollider, Transform wheelTransform)
    {
        Vector3 pos;
        Quaternion rot;
        wheelCollider.GetWorldPose(out pos, out rot);
        wheelTransform.rotation = rot;
        wheelTransform.position = pos;
    }
}

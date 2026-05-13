using UnityEngine;

// Attach to a traffic light GameObject (done automatically by TrafficLightManager for TL_West).
// When any car enters the detection sphere, the linked TrafficLight is forced red —
// creating a traffic queue scene as cars pile up behind the stopped lead car.
public class TrafficTriggerZone : MonoBehaviour
{
    [Tooltip("The traffic light this zone controls.")]
    public TrafficLight targetLight;

    [Tooltip("Sphere radius around this zone's position that detects approaching cars.")]
    public float detectionRadius = 40f;

    [Tooltip("How long the light stays red after detecting a car. Extended on each detection.")]
    public float forceRedDuration = 12f;

    [Tooltip("How often (seconds) the zone checks for approaching cars.")]
    public float checkInterval = 0.4f;

    // Only trigger when a car is broadly heading toward the light, not away from it.
    [Tooltip("Forward dot threshold — 0 = any direction, 1 = must be heading straight at the light.")]
    public float approachDotThreshold = 0.15f;

    private float nextCheckTime;
    private static readonly Collider[] _buf = new Collider[64];

    private void Update()
    {
        if (Time.time < nextCheckTime) return;
        nextCheckTime = Time.time + checkInterval;

        if (targetLight == null) return;

        int count = Physics.OverlapSphereNonAlloc(transform.position, detectionRadius, _buf);
        for (int i = 0; i < count; i++)
        {
            Collider col = _buf[i];
            if (col.isTrigger) continue;

            Rigidbody rb = col.GetComponentInParent<Rigidbody>();
            if (rb == null) continue;

            if (!IsCar(rb)) continue;

            Vector3 toLight = transform.position - rb.position;
            toLight.y = 0f;
            float dist = toLight.magnitude;

            // Ignore cars already at the stop line — they're stopped because of this
            // light and must not re-trigger the hold (that would lock the light red forever).
            if (dist < 8f) continue;

            // Only trigger for cars that are actually moving toward the light.
            // Stopped cars must never extend the hold.
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            if (vel.sqrMagnitude < 0.01f) continue;

            float dot = Vector3.Dot(vel.normalized, toLight.normalized);
            if (dot < approachDotThreshold) continue;

            targetLight.ForceRed(forceRedDuration);
            break;
        }
    }

    private static bool IsCar(Rigidbody rb)
    {
        return rb.GetComponent<AICarController>() != null
            || rb.GetComponent<CarController>()    != null
            || rb.GetComponent<onnxcontroller>()   != null;
    }

    // Draw the detection zone in the Scene view
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
    }
}

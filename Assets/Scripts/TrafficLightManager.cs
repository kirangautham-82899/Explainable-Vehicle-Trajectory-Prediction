using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-100)]
public class TrafficLightManager : MonoBehaviour
{
    [System.Serializable]
    public struct SignalPoint
    {
        public Vector3 position;
        public string  label;
    }

    private SignalPoint[] signalPoints = new SignalPoint[]
    {
        new SignalPoint { position = new Vector3(   0f,  1.7f,  130.4f), label = "TL_North"  },
        new SignalPoint { position = new Vector3(-226.08f,  6.25f,   -9.18f), label = "TL_NW"     },
        new SignalPoint { position = new Vector3(-279.8f,  0.97f, -60f   ), label = "TL_West"   },
        new SignalPoint { position = new Vector3(-207.46f,  5.2f,  -8.29f), label = "TL_SW"     },
        new SignalPoint { position = new Vector3(  46f,  2.0f,   35f), label = "TL_Center" },
    };

    [SerializeField] private bool  spawnLightsAtRuntime = true;
    [SerializeField] private float reassignmentInterval = 1f;
    [Header("Signal Assignment")]
    [Tooltip("Minimum forward dot product for a light to count as ahead of the car")]
    [SerializeField] private float signalAheadDotThreshold = 0.55f;
    [Tooltip("Max lateral lane distance for ahead-light matching")]
    [SerializeField] private float signalLaneHalfWidth = 10f;
    [Tooltip("Extra cost per metre of lateral offset when choosing among ahead lights")]
    [SerializeField] private float signalLateralPenalty = 1.5f;

    [Header("TL_West Trigger Zone")]
    [Tooltip("Detection radius for cars approaching TL_West (metres).")]
    [SerializeField] private float westTriggerRadius = 40f;
    [Tooltip("How long TL_West stays red after a car is detected (seconds).")]
    [SerializeField] private float westForceRedDuration = 12f;

    // Static registry — all cars query this directly every FixedUpdate so
    // there is no dependence on the 1-second assignment coroutine.
    public static readonly List<TrafficLight> AllLights = new List<TrafficLight>();

    // Returns the nearest red traffic light that is ahead of <car> and within
    // <maxDist> metres, or null if none qualifies.
    public static TrafficLight NearestRedAhead(Transform car, float maxDist = 28f)
    {
        TrafficLight best  = null;
        float        score = float.MaxValue;
        Vector3      pos   = car.position;
        Vector3      fwd   = car.forward;
        Vector3      right = car.right;

        foreach (var tl in AllLights)
        {
            if (tl == null || !tl.IsRed) continue;
            Vector3 toLight = tl.transform.position - pos;
            toLight.y = 0f;
            float dist = toLight.magnitude;
            if (dist > maxDist) continue;
            float dot = dist > 0.1f ? Vector3.Dot(fwd, toLight / dist) : 1f;
            if (dot < 0.25f) continue;                          // must be broadly ahead
            float lat = Mathf.Abs(Vector3.Dot(right, toLight));
            if (lat > 10f) continue;                            // not in another lane
            float s = dist + lat * 1.5f;
            if (s < score) { score = s; best = tl; }
        }
        return best;
    }

    private List<TrafficLight> lights = new List<TrafficLight>();

    // Called by Unity when the component is first added, or the user clicks
    // "Reset" in the Inspector gear menu — stamps the correct defaults.
    private void Reset() => ApplyDefaultSignalPoints();

    [ContextMenu("Reset Signal Points to Defaults")]
    private void ApplyDefaultSignalPoints()
    {
        signalPoints = new SignalPoint[]
        {
            new SignalPoint { position = new Vector3(   0f,     1.7f,  130.4f ), label = "TL_North"  },
            new SignalPoint { position = new Vector3(-226.08f,  6.25f,  -9.18f), label = "TL_NW"     },
            new SignalPoint { position = new Vector3(-279.8f,   0.97f, -60f   ), label = "TL_West"   },
            new SignalPoint { position = new Vector3(-207.46f,  5.2f,   -8.29f), label = "TL_SW"     },
            new SignalPoint { position = new Vector3(  46f,     2.0f,   35f   ), label = "TL_Center" },
        };
        Debug.Log("[TrafficLightManager] Signal points reset to code defaults.");
    }

    // Awake runs before any Start/FixedUpdate — lights are in AllLights before cars ever query.
    private void Awake()
    {
        if (spawnLightsAtRuntime)
            SpawnLights();
        else
        {
            var found = FindObjectsByType<TrafficLight>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            lights.AddRange(found);
            AllLights.Clear();
            AllLights.AddRange(found);
        }
    }

    private void Start()
    {
        StartCoroutine(AssignmentLoop());
    }

    // TL_NW and TL_SW guard the same corridor — they must share a phase so a car
    // stopped at one never sees the other as a fresh red obstacle once it moves off.
    private static float StaggerForLabel(string label) => label switch
    {
        "TL_North"  => 0f,
        "TL_NW"     => 4f,
        "TL_SW"     => 4f,   // same phase as TL_NW
        "TL_West"   => 8f,
        "TL_Center" => 2f,
        _           => 0f
    };

    private void SpawnLights()
    {
        AllLights.Clear();
        foreach (var sp in signalPoints)
        {
            var go = new GameObject(sp.label);
            // Use the exact position supplied — no ground-snap override.
            go.transform.position = sp.position;
            var tl = go.AddComponent<TrafficLight>();
            tl.startDelay = StaggerForLabel(sp.label);
            lights.Add(tl);
            AllLights.Add(tl);
        }
    }

    private IEnumerator AssignmentLoop()
    {
        var wait = new WaitForSeconds(reassignmentInterval);
        while (true)
        {
            AssignNearestSignals();
            yield return wait;
        }
    }

    private void AssignNearestSignals()
    {
        foreach (var car in FindObjectsByType<onnxcontroller>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            car.nearestSignal = FindRelevantLight(car.transform);

        foreach (var car in FindObjectsByType<AICarController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            car.nearestSignal = FindRelevantLight(car.transform);

        // Player car also obeys traffic lights
        foreach (var car in FindObjectsByType<CarController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            car.nearestSignal = FindRelevantLight(car.transform);
    }

    private TrafficLight FindRelevantLight(Transform car)
    {
        if (car == null) return null;

        Vector3 pos     = car.position;
        Vector3 forward = car.forward;
        Vector3 right   = car.right;

        TrafficLight nearest = null;
        float nearestDist = float.MaxValue;

        TrafficLight aheadBest = null;
        float aheadScore = float.MaxValue;

        foreach (var tl in lights)
        {
            if (tl == null) continue;

            Vector3 toLight = tl.transform.position - pos;
            float dist = toLight.magnitude;
            if (dist < nearestDist) { nearestDist = dist; nearest = tl; }
            if (dist < 0.001f) continue;

            Vector3 dir = toLight / dist;
            float fwdDot = Vector3.Dot(forward, dir);
            if (fwdDot < signalAheadDotThreshold) continue;

            float lateral = Mathf.Abs(Vector3.Dot(right, toLight));
            if (lateral > signalLaneHalfWidth) continue;

            float score = dist + lateral * signalLateralPenalty;
            if (score < aheadScore)
            {
                aheadScore = score;
                aheadBest = tl;
            }
        }

        return aheadBest != null ? aheadBest : nearest;
    }
}

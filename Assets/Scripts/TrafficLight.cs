using UnityEngine;

public class TrafficLight : MonoBehaviour
{
    public enum State { Red, Yellow, Green }

    [SerializeField] private float greenDuration  = 10f;
    [SerializeField] private float yellowDuration = 3f;
    [SerializeField] private float redDuration    = 10f;

    [HideInInspector] public float startDelay;

    public State CurrentState { get; private set; } = State.Red;

    public bool IsRed   => CurrentState == State.Red || CurrentState == State.Yellow;
    public bool IsGreen => CurrentState == State.Green;

    private float timer;
    private float forceRedRemaining = 0f;   // when > 0, light is held red regardless of timer

    private Renderer redLight, yellowLight, greenLight;

    private void Awake()  => BuildVisuals();

    private void Start()
    {
        timer = -startDelay;
        RefreshLights();
    }

    // Force this light to stay red for at least 'duration' more seconds.
    // Can be called repeatedly to extend the hold.
    public void ForceRed(float duration)
    {
        forceRedRemaining = Mathf.Max(forceRedRemaining, duration);
        CurrentState      = State.Red;
        RefreshLights();
    }

    private void Update()
    {
        // Forced-red hold (triggered by approaching cars via TrafficTriggerZone).
        // When the hold expires the light jumps straight to Green — no extra red cycle.
        if (forceRedRemaining > 0f)
        {
            forceRedRemaining -= Time.deltaTime;
            CurrentState       = State.Red;
            if (forceRedRemaining <= 0f)
            {
                CurrentState = State.Green;
                timer        = 0f;
            }
            RefreshLights();
            return;
        }

        timer += Time.deltaTime;
        if (timer < 0f) return;   // still in start-delay window

        switch (CurrentState)
        {
            case State.Green:
                if (timer >= greenDuration)  { CurrentState = State.Yellow; timer = 0f; }
                break;
            case State.Yellow:
                if (timer >= yellowDuration) { CurrentState = State.Red;    timer = 0f; }
                break;
            case State.Red:
                if (timer >= redDuration)    { CurrentState = State.Green;  timer = 0f; }
                break;
        }

        RefreshLights();
    }

    // ─────────────────────────────────────────────────────────────
    // Visual construction
    // ─────────────────────────────────────────────────────────────

    // Tints a renderer using its own pipeline-default material instance —
    // works with Built-in, URP, and HDRP without any Shader.Find calls.
    private static void Tint(Renderer r, Color col)
    {
        var m = r.material;          // Unity creates a correct pipeline instance
        m.color = col;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);   // URP
    }

    // Creates a primitive, parents it, removes its collider, and tints it.
    private Transform MakePart(PrimitiveType type, Color col,
                                Vector3 pos, Vector3 scale,
                                Quaternion? rot = null)
    {
        var go = GameObject.CreatePrimitive(type);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale    = scale;
        if (rot.HasValue) go.transform.localRotation = rot.Value;
        Tint(go.GetComponent<Renderer>(), col);
        Destroy(go.GetComponent<Collider>());
        return go.transform;
    }

    private void BuildVisuals()
    {
        // Pipeline-agnostic colors — no Shader.Find needed.
        var cConcrete = new Color(0.52f, 0.50f, 0.46f);
        var cSteel    = new Color(0.19f, 0.20f, 0.22f);
        var cHousing  = new Color(0.07f, 0.07f, 0.07f);
        var cRing     = new Color(0.03f, 0.03f, 0.03f);

        // ── Concrete base ──────────────────────────────────────────
        MakePart(PrimitiveType.Cylinder, cConcrete,
            new Vector3(0f, 0.055f, 0f), new Vector3(0.72f, 0.055f, 0.72f));
        MakePart(PrimitiveType.Cylinder, cConcrete,
            new Vector3(0f, 0.14f, 0f),  new Vector3(0.44f, 0.04f, 0.44f));

        // ── Main pole ──────────────────────────────────────────────
        MakePart(PrimitiveType.Cylinder, cSteel,
            new Vector3(0f, 2.30f, 0f), new Vector3(0.130f, 2.30f, 0.130f));
        MakePart(PrimitiveType.Cylinder, cSteel,
            new Vector3(0f, 4.56f, 0f), new Vector3(0.105f, 0.18f, 0.105f));
        MakePart(PrimitiveType.Cylinder, cSteel,
            new Vector3(0f, 4.72f, 0f), new Vector3(0.22f,  0.05f, 0.22f));

        // ── Horizontal arm ─────────────────────────────────────────
        MakePart(PrimitiveType.Cylinder, cSteel,
            new Vector3(0f, 4.69f, 0.28f), new Vector3(0.07f, 0.28f, 0.07f),
            Quaternion.Euler(90f, 0f, 0f));
        MakePart(PrimitiveType.Cylinder, cSteel,
            new Vector3(0f, 4.69f, 0.56f), new Vector3(0.17f, 0.04f, 0.17f));

        // ── Signal housing ─────────────────────────────────────────
        const float HZ  = 0.76f;
        const float HHH = 0.76f;
        const float HHW = 0.26f;
        const float HHD = 0.20f;
        const float HY  = 4.65f;

        MakePart(PrimitiveType.Cube, cHousing,
            new Vector3(0f, HY, HZ), new Vector3(HHW * 2f, HHH * 2f, HHD * 2f));
        MakePart(PrimitiveType.Sphere, cHousing,
            new Vector3(0f, HY + HHH, HZ), new Vector3(HHW * 2f, 0.20f, HHD * 2f));
        MakePart(PrimitiveType.Sphere, cHousing,
            new Vector3(0f, HY - HHH, HZ), new Vector3(HHW * 2f, 0.20f, HHD * 2f));
        MakePart(PrimitiveType.Cube, cHousing,
            new Vector3(-HHW + 0.025f, HY, HZ), new Vector3(0.05f, HHH * 2f + 0.10f, HHD * 2f + 0.02f));
        MakePart(PrimitiveType.Cube, cHousing,
            new Vector3( HHW - 0.025f, HY, HZ), new Vector3(0.05f, HHH * 2f + 0.10f, HHD * 2f + 0.02f));

        // ── Three signal lenses ─────────────────────────────────────
        float   frontZ  = HZ + HHD;
        float[] lightY  = { HY + 0.52f, HY, HY - 0.52f };
        Color[] lCols   =
        {
            new Color(1.00f, 0.05f, 0.05f),
            new Color(1.00f, 0.78f, 0.00f),
            new Color(0.05f, 0.95f, 0.05f)
        };

        for (int i = 0; i < 3; i++)
        {
            float ly = lightY[i];

            MakePart(PrimitiveType.Cube, cHousing,
                new Vector3(0f,      ly + 0.155f, frontZ - 0.055f), new Vector3(0.40f,  0.040f, 0.16f));
            MakePart(PrimitiveType.Cube, cHousing,
                new Vector3(-0.185f, ly + 0.085f, frontZ - 0.025f), new Vector3(0.025f, 0.115f, 0.10f));
            MakePart(PrimitiveType.Cube, cHousing,
                new Vector3( 0.185f, ly + 0.085f, frontZ - 0.025f), new Vector3(0.025f, 0.115f, 0.10f));
            MakePart(PrimitiveType.Cylinder, cRing,
                new Vector3(0f, ly, frontZ + 0.006f), new Vector3(0.245f, 0.007f, 0.245f),
                Quaternion.Euler(90f, 0f, 0f));

            // Lens bulb — needs its own material instance for per-state emission
            var lens = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lens.transform.SetParent(transform, false);
            lens.transform.localPosition = new Vector3(0f, ly, frontZ + 0.015f);
            lens.transform.localScale    = new Vector3(0.21f, 0.21f, 0.07f);
            Destroy(lens.GetComponent<Collider>());
            Tint(lens.GetComponent<Renderer>(), lCols[i]);   // pipeline-safe tint

            if (i == 0) redLight    = lens.GetComponent<Renderer>();
            if (i == 1) yellowLight = lens.GetComponent<Renderer>();
            if (i == 2) greenLight  = lens.GetComponent<Renderer>();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Light state
    // ─────────────────────────────────────────────────────────────

    private void RefreshLights()
    {
        if (redLight == null) return;
        SetEmission(redLight,    CurrentState == State.Red,    new Color(1.00f, 0.05f, 0.05f));
        SetEmission(yellowLight, CurrentState == State.Yellow, new Color(1.00f, 0.78f, 0.00f));
        SetEmission(greenLight,  CurrentState == State.Green,  new Color(0.05f, 0.95f, 0.05f));
    }

    private static void SetEmission(Renderer r, bool on, Color col)
    {
        var m = r.material;
        if (on)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", col * 4f);
            m.color = col;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);  // URP
        }
        else
        {
            m.DisableKeyword("_EMISSION");
            Color dim = col * 0.10f;
            m.color = dim;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", dim);  // URP
        }
    }
}

using UnityEngine;

public class SpeedometerUI : MonoBehaviour
{
    [Header("References")]
    public Rigidbody carRigidbody;

    [Header("UI Settings")]
    [Range(60f, 200f)] public float maxSpeed = 120f;   // km/h — full arc

    private Texture2D _whiteTex;

    void Start()
    {
        if (carRigidbody == null)
        {
            carRigidbody = GetComponent<Rigidbody>()
                        ?? GetComponentInParent<Rigidbody>()
                        ?? GetComponentInChildren<Rigidbody>();
        }

        _whiteTex = new Texture2D(1, 1);
        _whiteTex.SetPixel(0, 0, Color.white);
        _whiteTex.Apply();
    }

    void OnGUI()
    {
        if (carRigidbody == null) return;

        float kmh   = carRigidbody.linearVelocity.magnitude * 3.6f;
        float t     = Mathf.Clamp01(kmh / maxSpeed);
        int   kmhI  = Mathf.RoundToInt(kmh);

        // ── Panel geometry ────────────────────────────────────────────
        float pw = 220f, ph = 110f;
        float px = Screen.width  - pw - 18f;
        float py = Screen.height - ph - 18f;

        // ── Background card ───────────────────────────────────────────
        DrawRect(px, py, pw, ph, new Color(0.04f, 0.07f, 0.14f, 0.93f));
        DrawBorder(px, py, pw, ph, new Color(0f, 0.55f, 0.75f, 1f), 2f);

        // ── Header strip ──────────────────────────────────────────────
        DrawRect(px, py, pw, 22f, new Color(0f, 0.45f, 0.62f, 1f));
        GUI.Label(new Rect(px + 10f, py + 2f, pw, 18f),
            "SPEEDOMETER",
            Style(10, new Color(0.85f, 0.97f, 1f), FontStyle.Bold));

        // ── Speed value ───────────────────────────────────────────────
        string numStr = kmhI.ToString();
        GUI.Label(new Rect(px + 8f, py + 22f, pw - 16f, 54f),
            numStr,
            Style(46, Color.white, FontStyle.Bold, TextAnchor.MiddleLeft));

        GUI.Label(new Rect(px + 8f, py + 24f, pw - 16f, 54f),
            "km/h",
            Style(13, new Color(0.55f, 0.78f, 0.90f), FontStyle.Bold, TextAnchor.MiddleRight));

        // ── Speed bar ─────────────────────────────────────────────────
        float barX = px + 10f, barY = py + 80f;
        float barW = pw - 20f, barH = 10f;

        DrawRect(barX, barY, barW, barH, new Color(0.06f, 0.10f, 0.18f, 1f));

        // Colour shifts green → yellow → red with speed
        Color barCol = t < 0.5f
            ? Color.Lerp(new Color(0.1f, 0.85f, 0.35f), new Color(1f, 0.85f, 0.1f), t * 2f)
            : Color.Lerp(new Color(1f, 0.85f, 0.1f),    new Color(0.95f, 0.2f, 0.1f), (t - 0.5f) * 2f);

        if (t > 0f)
            DrawRect(barX, barY, barW * t, barH, barCol);

        DrawBorder(barX, barY, barW, barH, new Color(0f, 0.45f, 0.62f, 0.7f), 1f);

        // ── Tick marks ────────────────────────────────────────────────
        int ticks = 6;
        for (int i = 0; i <= ticks; i++)
        {
            float tx = barX + barW * (i / (float)ticks);
            float th = (i % 2 == 0) ? 5f : 3f;
            DrawRect(tx - 0.5f, barY - th, 1f, th, new Color(0.5f, 0.75f, 0.9f, 0.6f));
        }

        // ── Speed zone label ──────────────────────────────────────────
        string zone = t < 0.4f ? "NORMAL" : t < 0.7f ? "FAST" : "OVER SPEED";
        Color zoneCol = t < 0.4f
            ? new Color(0.2f, 0.9f, 0.4f)
            : t < 0.7f ? new Color(1f, 0.85f, 0.1f)
                       : new Color(0.95f, 0.25f, 0.15f);

        GUI.Label(new Rect(px + 10f, py + 91f, pw - 16f, 16f),
            zone,
            Style(10, zoneCol, FontStyle.Bold, TextAnchor.MiddleRight));
    }

    // ── Drawing helpers ───────────────────────────────────────────────

    void DrawRect(float x, float y, float w, float h, Color c)
    {
        var prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y, w, h), _whiteTex);
        GUI.color = prev;
    }

    void DrawBorder(float x, float y, float w, float h, Color c, float t)
    {
        DrawRect(x,         y,         w,  t,  c);
        DrawRect(x,         y + h - t, w,  t,  c);
        DrawRect(x,         y,         t,  h,  c);
        DrawRect(x + w - t, y,         t,  h,  c);
    }

    GUIStyle Style(int size, Color col,
                   FontStyle fs    = FontStyle.Normal,
                   TextAnchor align = TextAnchor.UpperLeft)
    {
        var s = new GUIStyle(GUI.skin.label)
        {
            fontSize  = size,
            fontStyle = fs,
            alignment = align
        };
        s.normal.textColor = col;
        return s;
    }
}

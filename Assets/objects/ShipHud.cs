using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Flight telemetry shown while a player is piloting this ship: speed, throttle, altitude,
/// gravity state, a live brake/free-look indicator and an optional controls cheat-sheet.
///
/// Self-managing: it watches <see cref="VehicleBoarding.IsPiloted"/> and shows itself when
/// someone takes the seat. It deliberately does NOT go in
/// <see cref="VehicleBoarding.pilotedController"/> — that single slot belongs to
/// <see cref="ShipController"/>, and the HUD must not be an either/or with flight.
///
/// This component is READ-ONLY with respect to flight. In particular it never calls
/// <c>Enable()</c> on any of <see cref="ShipController"/>'s actions: that script disables them
/// outright on disembark, and because <c>InputAction.Enable/Disable</c> is not reference
/// counted, a HUD that enabled them too would have them switched off underneath it. Reading
/// <c>IsPressed()</c> is safe; enabling is not.
///
/// Every widget is a serialized field. Left empty — the normal case — the widget is built at
/// runtime under the scene's Canvas, so adding this component is the only wiring needed.
/// Assign your own hand-authored <see cref="Text"/>/<see cref="Image"/> objects to take over
/// the layout; the ones you assign are driven and never rebuilt.
///
/// Uses legacy uGUI rather than TextMeshPro for the same reason <see cref="BoardingPrompt"/>
/// does: TMP's essential resources are not imported in this project.
/// </summary>
[RequireComponent(typeof(VehicleBoarding))]
[RequireComponent(typeof(ShipController))]
[DisallowMultipleComponent]
public class ShipHud : MonoBehaviour
{
    [Header("Widgets (leave empty to build them at runtime)")]
    [Tooltip("Value label for the SPD row.")]
    public Text speedLabel;

    [Tooltip("Value label for the THR row (the percentage beside the bar).")]
    public Text throttleLabel;

    [Tooltip("Value label for the ALT row.")]
    public Text altitudeLabel;

    [Tooltip("Value label for the GRAV row.")]
    public Text gravityLabel;

    [Tooltip("The filled part of the throttle bar. Driven by its right anchor, so when you " +
             "author this yourself it must be a child of the bar background, stretched to it.")]
    public Image throttleBarFill;

    [Tooltip("Lights up while the brake (X) is held.")]
    public Text brakeIndicator;

    [Tooltip("Lights up while free-look (Alt) is held.")]
    public Text freeLookIndicator;

    [Tooltip("The controls cheat-sheet. Its text is written once, at build time.")]
    public Text controlsLabel;

    [Header("Options")]
    [Tooltip("Show the controls cheat-sheet in the top-left corner. Takes effect immediately, " +
             "including while playing, so you can switch it off once you know the keys.")]
    public bool showControls = true;

    [Tooltip("How far down to look for the ground, in metres. Beyond this — and any time the " +
             "ship is outside a gravity field, where 'down' is no longer known — the altitude " +
             "reads '---' rather than guessing.")]
    public float altitudeProbeRange = 500f;

    [Tooltip("Seconds between text refreshes. The throttle bar moves every frame regardless; " +
             "this only rate-limits rebuilding the number strings.")]
    public float refreshInterval = 0.1f;

    [Header("Appearance")]
    public Color textColor = new Color(0.75f, 0.9f, 1f, 0.95f);

    [Tooltip("Colour of an indicator whose key is held, and of the throttle bar fill.")]
    public Color activeColor = new Color(1f, 0.55f, 0.2f, 1f);

    [Tooltip("Colour of an indicator whose key is not held.")]
    public Color idleColor = new Color(0.6f, 0.65f, 0.7f, 0.35f);

    public int fontSize = 16;

    VehicleBoarding boarding;
    ShipController ship;
    PlanetGravityBody gravityBody;

    // Every graphic this HUD drives, owned or assigned. Visibility is toggled through
    // Graphic.enabled on these rather than by deactivating GameObjects — same reasoning as
    // BoardingPrompt, and it keeps us from deactivating an object someone assigned to us and
    // may be sharing with something else.
    readonly List<Graphic> graphics = new List<Graphic>();

    // Buffer for the downward ground probe. RaycastNonAlloc does not sort and truncates
    // silently, so an overfull buffer degrades in two ways: we report the nearest of what we
    // got rather than the true nearest, and in the pathological case where every slot is one of
    // our own colliders we report no ground at all. Sized well above anything this scene puts
    // under the ship.
    readonly RaycastHit[] probeHits = new RaycastHit[16];

    // Runtime-built widgets that have no serialized field of their own, kept so appearance
    // stays live-tunable and the hierarchy can be torn down with the component.
    Image barBackground;
    GameObject builtRoot;

    bool visible;
    bool controlsVisible;
    float nextRefresh;

    // Last values actually rendered, so a string is rebuilt only when the display would change.
    // Speed is kept in tenths and altitude in whole metres — the units shown.
    int lastSpeedTenths = int.MinValue;
    float lastMaxSpeed = float.NaN;
    int lastThrottlePercent = int.MinValue;
    int lastAltitudeMetres = int.MinValue;
    bool lastHadAltitude;
    bool lastInGravity;
    GravityField lastField;

    // The gravity pair has no out-of-band sentinel available: false/null are exactly the values
    // a ship sitting outside every field produces, so without this flag the first refresh would
    // compare equal, skip the write, and leave the build-time placeholder on screen — which
    // reads as "no gravity component" rather than "FREE DRIFT".
    bool lastGravityValid;

    void Awake()
    {
        boarding = GetComponent<VehicleBoarding>();
        ship = GetComponent<ShipController>();
        gravityBody = GetComponent<PlanetGravityBody>();

        // Order matters: the builders register the graphics they create as they go, then the
        // assigned ones are folded in. Clearing after building would drop everything we own.
        graphics.Clear();
        BuildMissingWidgets();
        CollectAssignedGraphics();

        // Nobody is piloting during Awake. Seed the cached flags to the opposite of what we
        // want so the first call actually applies rather than short-circuiting.
        visible = true;
        controlsVisible = true;
        SetVisible(false);
    }

    void OnDisable()
    {
        SetVisible(false);
    }

    /// <summary>
    /// Tear down what we built. The widgets live under the scene's Canvas, not under this
    /// object, so destroying the ship would otherwise leave an orphaned HUD hierarchy parented
    /// to the Canvas for the rest of the scene's life. Widgets assigned in the Inspector are
    /// left alone — we did not create them and do not own them.
    /// </summary>
    void OnDestroy()
    {
        if (builtRoot != null)
            Destroy(builtRoot);
    }

    void Update()
    {
        bool piloted = boarding != null && boarding.IsPiloted;
        SetVisible(piloted);

        if (!piloted)
            return;

        // Every frame: both are transform/colour tweaks that allocate nothing, and a throttle
        // gauge that only moved ten times a second would look broken.
        UpdateThrottleBar();
        UpdateIndicators();

        if (Time.unscaledTime < nextRefresh)
            return;

        nextRefresh = Time.unscaledTime + Mathf.Max(0f, refreshInterval);
        RefreshReadouts();
        ApplyAppearance();
    }

    /// <summary>
    /// Push the appearance fields onto the widgets every refresh rather than only at build time,
    /// so dragging them in the Inspector mid-flight does something — the same convention
    /// <see cref="ShipController"/> follows for its tuning values. An Inspector field that only
    /// takes effect on the frame the object was created reads as broken, not as by-design.
    ///
    /// The uGUI setters all early-out on an unchanged value, so this costs nothing when idle.
    /// </summary>
    void ApplyAppearance()
    {
        for (int i = 0; i < graphics.Count; i++)
        {
            Graphic graphic = graphics[i];
            if (graphic == null)
                continue;

            Text text = graphic as Text;
            if (text != null)
            {
                text.fontSize = Mathf.Max(1, fontSize);

                // The two indicators own their own colour: it is their state readout, and
                // overwriting it here would stamp out the brake/free-look highlight.
                if (text != brakeIndicator && text != freeLookIndicator)
                    text.color = textColor;

                continue;
            }

            if (graphic == throttleBarFill)
                graphic.color = activeColor;
            else if (graphic == barBackground)
                graphic.color = new Color(textColor.r, textColor.g, textColor.b, 0.18f);
        }
    }

    // ---------------------------------------------------------------------
    // Readouts
    // ---------------------------------------------------------------------

    void RefreshReadouts()
    {
        if (ship != null)
        {
            int tenths = Mathf.RoundToInt(ship.Speed * 10f);
            if (tenths != lastSpeedTenths || !Mathf.Approximately(ship.maxSpeed, lastMaxSpeed))
            {
                lastSpeedTenths = tenths;
                lastMaxSpeed = ship.maxSpeed;
                SetText(speedLabel, (tenths * 0.1f).ToString("0.0") + " / "
                                    + ship.maxSpeed.ToString("0") + " m/s");
            }

            int percent = Mathf.RoundToInt(ship.Throttle * 100f);
            if (percent != lastThrottlePercent)
            {
                lastThrottlePercent = percent;
                SetText(throttleLabel, percent.ToString() + "%");
            }
        }

        float altitude;
        bool hasAltitude = TryGetAltitude(out altitude);
        int metres = hasAltitude ? Mathf.RoundToInt(altitude) : 0;
        if (hasAltitude != lastHadAltitude || metres != lastAltitudeMetres)
        {
            lastHadAltitude = hasAltitude;
            lastAltitudeMetres = metres;
            SetText(altitudeLabel, hasAltitude ? metres.ToString() + " m" : "---");
        }

        bool inGravity = gravityBody != null && gravityBody.InGravity;
        GravityField field = gravityBody != null ? gravityBody.CurrentField : null;
        if (!lastGravityValid || inGravity != lastInGravity || field != lastField)
        {
            lastGravityValid = true;
            lastInGravity = inGravity;
            lastField = field;

            string state;
            if (gravityBody == null)
                state = "---";
            else if (!inGravity)
                state = "FREE DRIFT";
            else
                state = field != null ? field.name.ToUpperInvariant() : "GRAVITY";

            SetText(gravityLabel, state);
        }
    }

    void UpdateThrottleBar()
    {
        if (throttleBarFill == null || ship == null)
            return;

        // The fill is stretched to its parent, so its RIGHT anchor is the gauge. Driving the
        // anchor rather than Image.fillAmount keeps this working with no sprite assigned.
        RectTransform rect = throttleBarFill.rectTransform;
        Vector2 max = rect.anchorMax;
        max.x = Mathf.Clamp01(ship.Throttle);
        rect.anchorMax = max;
    }

    void UpdateIndicators()
    {
        if (ship == null)
            return;

        SetIndicator(brakeIndicator, IsPressed(ship.brakeAction));
        SetIndicator(freeLookIndicator, IsPressed(ship.freeLookAction));
    }

    void SetIndicator(Text label, bool on)
    {
        if (label == null)
            return;

        Color wanted = on ? activeColor : idleColor;
        if (label.color != wanted)
            label.color = wanted;
    }

    /// <summary>
    /// Distance from the ship's origin to whatever is directly below it, along the current
    /// gravity "up". Measured from the origin, not the belly, so a landed ship reads the hull's
    /// half-height rather than zero.
    ///
    /// A gravity field's radius is its RANGE (thousands of units), not the planet's surface, so
    /// it cannot give height above ground — this probes for the real surface instead. The ray
    /// starts inside the hull, so hits belonging to this ship (and to anything parented to it,
    /// which includes the seated pilot) are skipped rather than reported as zero altitude.
    ///
    /// Reports nothing at all when out of gravity. <see cref="PlanetGravityBody.GravityUp"/> is
    /// deliberately FROZEN at its last value once the ship leaves a field, so "down" would still
    /// be whatever direction the planet was in at the moment of departure. Probing along it
    /// while drifting away gives a number that looks authoritative and is wrong by an amount
    /// that grows with every metre travelled — worse than showing "---".
    /// </summary>
    bool TryGetAltitude(out float altitude)
    {
        altitude = 0f;

        if (gravityBody == null || !gravityBody.InGravity)
            return false;

        Vector3 up = gravityBody.GravityUp;
        if (up.sqrMagnitude < 1e-6f)
            return false;

        // DefaultRaycastLayers, not ~0: the latter re-includes the built-in Ignore Raycast
        // layer, which is how a designer says "physics queries must not see this".
        int count = Physics.RaycastNonAlloc(transform.position, -up.normalized, probeHits,
                                            Mathf.Max(0f, altitudeProbeRange),
                                            Physics.DefaultRaycastLayers,
                                            QueryTriggerInteraction.Ignore);

        float nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Collider hit = probeHits[i].collider;
            if (hit == null || hit.transform.IsChildOf(transform))
                continue; // our own hull, or the pilot riding in it

            if (probeHits[i].distance < nearest)
                nearest = probeHits[i].distance;
        }

        if (float.IsPositiveInfinity(nearest))
            return false;

        altitude = nearest;
        return true;
    }

    static bool IsPressed(InputActionReference reference)
    {
        // Read only — see the class comment on why this must never Enable() the action.
        return reference != null && reference.action != null && reference.action.IsPressed();
    }

    static void SetText(Text label, string value)
    {
        if (label != null)
            label.text = value;
    }

    // ---------------------------------------------------------------------
    // Visibility
    // ---------------------------------------------------------------------

    void SetVisible(bool show)
    {
        bool wantControls = show && showControls;

        if (show == visible && wantControls == controlsVisible)
            return;

        visible = show;
        controlsVisible = wantControls;

        for (int i = 0; i < graphics.Count; i++)
        {
            Graphic graphic = graphics[i];
            if (graphic == null)
                continue;

            bool isControls = controlsLabel != null && graphic == controlsLabel;
            graphic.enabled = isControls ? wantControls : show;
        }
    }

    void CollectAssignedGraphics()
    {
        Track(speedLabel);
        Track(throttleLabel);
        Track(altitudeLabel);
        Track(gravityLabel);
        Track(throttleBarFill);
        Track(brakeIndicator);
        Track(freeLookIndicator);
        Track(controlsLabel);
    }

    /// <summary>Register a graphic for visibility toggling. Ignores nulls and duplicates.</summary>
    void Track(Graphic graphic)
    {
        if (graphic != null && !graphics.Contains(graphic))
            graphics.Add(graphic);
    }

    // ---------------------------------------------------------------------
    // Runtime construction
    //
    // Only widgets left empty in the Inspector are built. Everything created here — including
    // the row prefixes ("SPD", "THR", ...) and the throttle bar background, which have no
    // serialized field of their own — is registered for visibility as it is made. Miss one and
    // it stays on screen after the pilot has walked away.
    // ---------------------------------------------------------------------

    void BuildMissingWidgets()
    {
        bool needsTelemetry = speedLabel == null || throttleLabel == null
                              || altitudeLabel == null || gravityLabel == null
                              || throttleBarFill == null;
        bool needsIndicators = brakeIndicator == null || freeLookIndicator == null;
        bool needsControls = controlsLabel == null;

        if (!needsTelemetry && !needsIndicators && !needsControls)
            return;

        Canvas canvas = ResolveCanvas();
        if (canvas == null)
        {
            // ResolveCanvas creates one rather than returning null, so this is belt-and-braces
            // against a future edit. It must complain rather than build nothing in silence.
            Debug.LogWarning($"{name}: ShipHud found no Canvas to build under; no HUD will be " +
                             "shown.", this);
            return;
        }

        Font font = ResolveFont();
        if (font == null)
        {
            Debug.LogWarning($"{name}: ShipHud found no usable font, so its labels will render " +
                             "nothing. Assign the widgets manually.", this);
        }

        RectTransform root = MakeRect("ShipHud (runtime)", canvas.transform,
                                      Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        builtRoot = root.gameObject;
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        if (needsTelemetry)
            BuildTelemetry(root, font);

        if (needsIndicators)
            BuildIndicators(root, font);

        if (needsControls)
            BuildControls(root, font);
    }

    // Rows run bottom-up, so the block grows upward from the bottom-left corner.
    const float RowHeight = 22f;
    const float BlockWidth = 320f;
    const float ValueX = 52f;
    const float BarWidth = 130f;

    void BuildTelemetry(RectTransform root, Font font)
    {
        RectTransform block = MakeRect("Telemetry", root, new Vector2(0f, 0f), new Vector2(0f, 0f),
                                       new Vector2(24f, 24f),
                                       new Vector2(BlockWidth, RowHeight * 4f));

        if (gravityLabel == null)
            gravityLabel = BuildRow(block, font, 0f, "GRAV", "---");

        if (altitudeLabel == null)
            altitudeLabel = BuildRow(block, font, RowHeight, "ALT", "---");

        if (throttleLabel == null || throttleBarFill == null)
            BuildThrottleRow(block, font, RowHeight * 2f);

        if (speedLabel == null)
            speedLabel = BuildRow(block, font, RowHeight * 3f, "SPD", "0.0 m/s");
    }

    Text BuildRow(RectTransform block, Font font, float y, string prefix, string initial)
    {
        MakeText(prefix, block, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, y),
                 new Vector2(ValueX, RowHeight), TextAnchor.MiddleLeft, font, textColor, prefix);

        return MakeText(prefix + " value", block, new Vector2(0f, 0f), new Vector2(0f, 0f),
                        new Vector2(ValueX, y), new Vector2(BlockWidth - ValueX, RowHeight),
                        TextAnchor.MiddleLeft, font, textColor, initial);
    }

    void BuildThrottleRow(RectTransform block, Font font, float y)
    {
        MakeText("THR", block, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, y),
                 new Vector2(ValueX, RowHeight), TextAnchor.MiddleLeft, font, textColor, "THR");

        if (throttleBarFill == null)
        {
            const float barHeight = 10f;
            RectTransform barRect = MakeRect("Throttle bar", block, new Vector2(0f, 0f),
                                             new Vector2(0f, 0f),
                                             new Vector2(ValueX, y + (RowHeight - barHeight) * 0.5f),
                                             new Vector2(BarWidth, barHeight));

            barBackground = barRect.gameObject.AddComponent<Image>();
            barBackground.color = new Color(textColor.r, textColor.g, textColor.b, 0.18f);
            barBackground.raycastTarget = false;
            Track(barBackground);

            RectTransform fillRect = MakeRect("Fill", barRect, Vector2.zero, Vector2.zero,
                                              Vector2.zero, Vector2.zero);
            // Stretched to the background; the RIGHT anchor is what the throttle then drives.
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            throttleBarFill = fillRect.gameObject.AddComponent<Image>();
            throttleBarFill.color = activeColor;
            throttleBarFill.raycastTarget = false;
            Track(throttleBarFill);
        }

        if (throttleLabel == null)
        {
            throttleLabel = MakeText("THR value", block, new Vector2(0f, 0f), new Vector2(0f, 0f),
                                     new Vector2(ValueX + BarWidth + 8f, y),
                                     new Vector2(70f, RowHeight), TextAnchor.MiddleLeft,
                                     font, textColor, "0%");
        }
    }

    void BuildIndicators(RectTransform root, Font font)
    {
        RectTransform block = MakeRect("Indicators", root, new Vector2(0.5f, 0f),
                                       new Vector2(0.5f, 0f), new Vector2(0f, 150f),
                                       new Vector2(300f, RowHeight));

        if (brakeIndicator == null)
        {
            brakeIndicator = MakeText("Brake", block, new Vector2(0.5f, 0.5f),
                                      new Vector2(1f, 0.5f), new Vector2(-12f, 0f),
                                      new Vector2(130f, RowHeight), TextAnchor.MiddleRight,
                                      font, idleColor, "BRAKE");
        }

        if (freeLookIndicator == null)
        {
            freeLookIndicator = MakeText("FreeLook", block, new Vector2(0.5f, 0.5f),
                                         new Vector2(0f, 0.5f), new Vector2(12f, 0f),
                                         new Vector2(130f, RowHeight), TextAnchor.MiddleLeft,
                                         font, idleColor, "FREE LOOK");
        }
    }

    void BuildControls(RectTransform root, Font font)
    {
        // Plain hyphens rather than tabs or box-drawing glyphs: the legacy Text renderer handles
        // tabs inconsistently, and anything outside the basic set depends on OS font fallback.
        // MOUSE goes first because it is how the ship is actually steered
        // (ShipController.ApplyAttitude) — a cheat-sheet that omits the primary control is
        // worse than none. Space/Ctrl is described as ship-relative because that is what it is:
        // thrust out of the roof and belly, so inverted they push you at the ground.
        controlsLabel = MakeText("Controls", root, new Vector2(0f, 1f), new Vector2(0f, 1f),
                                 new Vector2(24f, -24f), new Vector2(BlockWidth, 200f),
                                 TextAnchor.UpperLeft, font, textColor,
                                 "MOUSE  -  steer (pitch / yaw)\n" +
                                 "W/S  -  throttle\n" +
                                 "A/D  -  strafe\n" +
                                 "Q/E  -  roll\n" +
                                 "SPACE/CTRL  -  thrust roof / belly\n" +
                                 "X  -  brake\n" +
                                 "ALT  -  free look\n" +
                                 "Y  -  exit ship");
    }

    // ---------------------------------------------------------------------
    // Construction helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// The canvas to build under: the scene's existing one when there is one, so the HUD shares
    /// the boarding prompt's sorting and scaling, otherwise a fresh overlay canvas.
    /// </summary>
    static Canvas ResolveCanvas()
    {
        Canvas existing = FindFirstObjectByType<Canvas>(FindObjectsInactive.Exclude);
        if (existing != null)
            return existing.rootCanvas != null ? existing.rootCanvas : existing;

        GameObject go = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler));
        Canvas canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        return canvas;
    }

    /// <summary>
    /// Prefer the font the scene's prompt already uses so the HUD matches it, then the builtin
    /// legacy font. The prompt's own label reference may not be resolved yet — it is assigned in
    /// its Awake, which may run after ours — so look the Text up directly as a fallback.
    /// </summary>
    static Font ResolveFont()
    {
        BoardingPrompt prompt = FindFirstObjectByType<BoardingPrompt>(FindObjectsInactive.Include);
        if (prompt != null)
        {
            Text label = prompt.label != null
                ? prompt.label
                : prompt.GetComponentInChildren<Text>(includeInactive: true);

            if (label != null && label.font != null)
                return label.font;
        }

        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    static RectTransform MakeRect(string name, Transform parent, Vector2 anchor, Vector2 pivot,
                                  Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = parent != null ? parent.gameObject.layer : 5; // 5 = UI

        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, worldPositionStays: false);
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one;
        return rect;
    }

    /// <summary>
    /// Build a label and register it for visibility. Instance method on purpose: it reads the
    /// serialized <see cref="fontSize"/>, which a static helper could not see. That only sets
    /// the initial value — <see cref="ApplyAppearance"/> is what keeps it live afterwards.
    /// </summary>
    Text MakeText(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position,
                  Vector2 size, TextAnchor alignment, Font font, Color color, string content)
    {
        RectTransform rect = MakeRect(name, parent, anchor, pivot, position, size);

        Text text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = Mathf.Max(1, fontSize);
        text.alignment = alignment;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false; // the HUD must never eat clicks
        text.text = content;

        Track(text);
        return text;
    }
}

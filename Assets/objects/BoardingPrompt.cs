using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A single on-screen interaction prompt ("Press E to ...").
///
/// Deliberately knows nothing about vehicles, boarding, or the player: it is told what text
/// to show and when to hide, so any interactable in the scene can drive the same label.
/// One of these in the scene is enough — <see cref="VehicleBoarding"/> finds it automatically
/// when its own reference is left empty, so adding a new vehicle needs no UI wiring.
///
/// Uses legacy uGUI <see cref="Text"/> rather than TextMeshPro because TMP's essential
/// resources are not imported in this project; this works with no package import.
///
/// Visibility is toggled via <see cref="Behaviour.enabled"/> on the label, never by
/// deactivating a GameObject: this component is allowed to sit on the same object as its label,
/// and deactivating that object would also hide this component from the scene lookup that
/// <see cref="VehicleBoarding"/> uses to find it — leaving a prompt that can never show again.
/// </summary>
[DisallowMultipleComponent]
public class BoardingPrompt : MonoBehaviour
{
    [Tooltip("The label to drive. If left empty, the first Text found in this object's " +
             "children (including inactive ones) is used.")]
    public Text label;

    void Awake()
    {
        if (label == null)
            label = GetComponentInChildren<Text>(includeInactive: true);

        Hide(); // never start visible; something has to ask for it
    }

    /// <summary>Show the prompt with the given text.</summary>
    public void Show(string message)
    {
        if (label == null)
            return;

        label.text = message;
        label.enabled = true;
    }

    /// <summary>Hide the prompt.</summary>
    public void Hide()
    {
        if (label != null)
            label.enabled = false;
    }
}

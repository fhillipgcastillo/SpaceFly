using UnityEngine;

public class PlanetGravityBody : MonoBehaviour
{
    public Transform planet;
    public float gravityStrength = 9.8f;

    [Tooltip("Rotate this body so its \"up\" points away from the planet center. " +
             "Turn OFF for the player: PlayerController owns the player's rotation " +
             "(surface alignment + facing) so the two don't fight over transform.rotation.")]
    public bool alignToSurface = true;

    [Tooltip("How quickly the body rotates to align with the surface (only used when alignToSurface is on).")]
    public float alignSpeed = 5f;

    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;          // turn off Unity's straight-down gravity
        if (alignToSurface)
            rb.constraints = RigidbodyConstraints.FreezeRotation; // for things that shouldn't tumble
    }

    void FixedUpdate()
    {
        Vector3 toCenter = (planet.position - rb.position).normalized;
        rb.AddForce(toCenter * gravityStrength, ForceMode.Acceleration);

        if (!alignToSurface)
            return;

        // Orient the object so its "up" points away from center
        Vector3 up = -toCenter;
        Quaternion targetRot = Quaternion.FromToRotation(transform.up, up) * transform.rotation;
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * alignSpeed);
    }
}
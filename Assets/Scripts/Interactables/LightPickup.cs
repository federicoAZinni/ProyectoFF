using UnityEngine;

/// <summary>
/// Objeto liviano: un toque de interacción lo levanta, otro toque lo tira hacia adelante.
/// Mientras se sostiene sigue al HoldPoint del player por velocidad (no se teletransporta),
/// así sigue chocando con paredes; si queda trabado lejos del player, se cae.
/// </summary>
public class LightPickup : Interactable
{
    [Header("Player")]
    [SerializeField, Range(0.05f, 1f)] private float playerSpeedMultiplier = 0.9f;

    [Header("Sostener")]
    [SerializeField] private float followStrength = 15f;
    [SerializeField] private float maxCarrySpeed = 15f;
    [SerializeField] private float rotationFollow = 10f;
    [Tooltip("Si el objeto queda más lejos que esto del HoldPoint (trabado en una pared), se suelta.")]
    [SerializeField] private float breakDistance = 1.5f;

    [Header("Tirar")]
    [SerializeField] private float throwSpeed = 10f;
    [Tooltip("Ángulo hacia arriba del lanzamiento, en grados.")]
    [SerializeField, Range(0f, 60f)] private float throwUpAngle = 15f;
    [Tooltip("Suma la velocidad del player al lanzar / soltar.")]
    [SerializeField] private bool inheritPlayerVelocity = true;
    [Tooltip("Tiempo sin colisionar con el player después de tirarlo, para que no lo golpee al salir.")]
    [SerializeField] private float collisionRestoreDelay = 0.25f;

    public override InteractionMode Mode => InteractionMode.Toggle;
    public override float PlayerSpeedMultiplier => playerSpeedMultiplier;

    private bool savedUseGravity;
    private Quaternion rotationOffset;

    protected override void Awake()
    {
        base.Awake();
        // Evita que atraviese paredes al tirarlo rápido.
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    protected override void OnInteractionBegin(PlayerInteractor interactor)
    {
        savedUseGravity = Body.useGravity;
        Body.useGravity = false;
        rotationOffset = Quaternion.Inverse(interactor.transform.rotation) * Body.rotation;
    }

    protected override void OnInteractionEnd(PlayerInteractor interactor, InteractionEndReason reason)
    {
        Body.useGravity = savedUseGravity;
        Body.angularVelocity = Vector3.zero;

        Vector3 playerVel = inheritPlayerVelocity ? interactor.Velocity : Vector3.zero;
        playerVel.y = 0f;

        if (reason == InteractionEndReason.Action)
        {
            Transform p = interactor.transform;
            Vector3 dir = Quaternion.AngleAxis(-throwUpAngle, p.right) * p.forward;
            Body.linearVelocity = dir * throwSpeed + playerVel;
        }
        else
        {
            Body.linearVelocity = playerVel;
        }
    }

    protected override float GetCollisionRestoreDelay(InteractionEndReason reason) => collisionRestoreDelay;

    private void FixedUpdate()
    {
        if (!IsBeingInteracted) return;

        Vector3 target = Interactor.HoldPoint.position;
        Vector3 error = target - Body.position;

        if (error.magnitude > breakDistance)
        {
            Interactor.ForceRelease();
            return;
        }

        Body.linearVelocity = Vector3.ClampMagnitude(error * followStrength, maxCarrySpeed);

        // Rotación: acompaña el giro del player.
        Quaternion targetRot = Interactor.transform.rotation * rotationOffset;
        Quaternion delta = targetRot * Quaternion.Inverse(Body.rotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;

        if (Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
            Body.angularVelocity = axis * (angle * Mathf.Deg2Rad * rotationFollow);
        else
            Body.angularVelocity = Vector3.zero;
    }
}

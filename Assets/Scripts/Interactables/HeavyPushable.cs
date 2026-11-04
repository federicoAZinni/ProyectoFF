using UnityEngine;

/// <summary>
/// Objeto pesado: se arrastra mientras el player mantiene apretada la tecla de interacción.
/// Funciona como un joint en el plano horizontal: el objeto sigue al player manteniendo el
/// offset del momento en que lo agarró, y si el objeto queda trabado (pared, otro objeto)
/// el player no puede alejarse ni meterse adentro de él.
/// </summary>
public class HeavyPushable : Interactable
{
    [Header("Player")]
    [SerializeField, Range(0.05f, 1f)] private float playerSpeedMultiplier = 0.5f;

    [Header("Arrastre")]
    [Tooltip("Velocidad máxima a la que se puede mover el objeto.")]
    [SerializeField] private float maxDragSpeed = 3f;
    [Tooltip("Qué tan fuerte corrige el objeto su posición hacia el offset original.")]
    [SerializeField] private float followStrength = 12f;
    [SerializeField] private bool freezeRotationWhileDragged = true;

    [Header("Joint")]
    [Tooltip("Margen que tiene el player para separarse del punto de agarre antes de ser frenado.")]
    [SerializeField] private float playerSlack = 0.25f;
    [Tooltip("Si el player queda más lejos que esto del punto de agarre, se suelta el objeto.")]
    [SerializeField] private float breakDistance = 1f;

    public override InteractionMode Mode => InteractionMode.Hold;
    public override float PlayerSpeedMultiplier => playerSpeedMultiplier;
    public override bool LocksPlayerRotation => true;

    private Vector3 grabOffset;
    private RigidbodyConstraints savedConstraints;

    protected override void OnInteractionBegin(PlayerInteractor interactor)
    {
        grabOffset = Body.position - interactor.transform.position;
        grabOffset.y = 0f;

        savedConstraints = Body.constraints;
        if (freezeRotationWhileDragged)
            Body.constraints |= RigidbodyConstraints.FreezeRotation;
    }

    protected override void OnInteractionEnd(PlayerInteractor interactor, InteractionEndReason reason)
    {
        Body.constraints = savedConstraints;

        // Que no siga deslizando al soltarlo.
        Vector3 v = Body.linearVelocity;
        Body.linearVelocity = new Vector3(0f, v.y, 0f);
    }

    private void FixedUpdate()
    {
        if (!IsBeingInteracted) return;

        Vector3 target = Interactor.transform.position + grabOffset;
        Vector3 error = target - Body.position;
        error.y = 0f;

        // Feed-forward con la velocidad del player + corrección proporcional del error.
        Vector3 playerVel = Interactor.Velocity;
        playerVel.y = 0f;

        Vector3 desired = Vector3.ClampMagnitude(playerVel + error * followStrength, maxDragSpeed);
        Body.linearVelocity = new Vector3(desired.x, Body.linearVelocity.y, desired.z);
    }

    public override void TickFromInteractor(PlayerInteractor interactor)
    {
        // Dónde debería estar el player según la posición real del objeto.
        Vector3 anchor = Body.position - grabOffset;
        Vector3 offset = interactor.transform.position - anchor;
        offset.y = 0f;

        float distance = offset.magnitude;

        if (distance > breakDistance)
        {
            interactor.ForceRelease();
            return;
        }

        // Restricción del joint: si el objeto no pudo seguir, frenamos al player.
        if (distance > playerSlack)
        {
            Vector3 correction = -offset / distance * (distance - playerSlack);
            interactor.Controller.Move(correction);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || !IsBeingInteracted) return;
        Gizmos.color = Color.magenta;
        Vector3 anchor = Body.position - grabOffset;
        Gizmos.DrawLine(Body.position, anchor);
        Gizmos.DrawWireSphere(anchor, playerSlack);
    }
#endif
}

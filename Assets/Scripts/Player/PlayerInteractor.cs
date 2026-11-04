using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Corre después de PlayerMovementController para poder corregir la posición/rotación del player
// en el mismo frame (necesario para el "joint" del objeto pesado).
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(CharacterController))]
public class PlayerInteractor : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionReference interactAction;

    [Header("Detección")]
    [Tooltip("Distancia horizontal máxima desde el centro del player hasta la superficie del objeto.")]
    [SerializeField] private float interactRange = 1.2f;
    [Tooltip("Ángulo máximo (a cada lado) respecto al frente del player.")]
    [SerializeField, Range(0f, 180f)] private float maxAngle = 75f;
    [Tooltip("Cuánto penaliza el ángulo al elegir entre varios objetos cercanos.")]
    [SerializeField] private float anglePenalty = 0.5f;
    [SerializeField] private LayerMask interactableMask = ~0;

    [Header("Objetos livianos")]
    [Tooltip("Punto donde se sostiene el objeto. Si está vacío se crea uno automáticamente.")]
    [SerializeField] private Transform holdPoint;
    [SerializeField] private Vector3 defaultHoldOffset = new Vector3(0f, 0.6f, 1.1f);

    public Interactable Focused { get; private set; }
    public Interactable Current { get; private set; }

    public float SpeedMultiplier => Current ? Current.PlayerSpeedMultiplier : 1f;
    public bool IsRotationLocked => Current && Current.LocksPlayerRotation;
    public bool IsInteracting => Current;

    public CharacterController Controller { get; private set; }
    public Collider[] PlayerColliders { get; private set; }
    public Transform HoldPoint => holdPoint;
    /// <summary>Velocidad real del player (medida por desplazamiento, incluye correcciones).</summary>
    public Vector3 Velocity { get; private set; }

    public event Action<Interactable> FocusChanged;
    public event Action<Interactable> InteractionStarted;
    public event Action<Interactable, InteractionEndReason> InteractionEnded;

    private readonly Collider[] overlapBuffer = new Collider[32];
    private Quaternion lockedRotation;
    private bool requireRepress;
    private Vector3 lastPosition;

    private void Awake()
    {
        Controller = GetComponent<CharacterController>();
        PlayerColliders = GetComponentsInChildren<Collider>();

        if (holdPoint == null)
        {
            holdPoint = new GameObject("HoldPoint").transform;
            holdPoint.SetParent(transform, false);
            holdPoint.localPosition = defaultHoldOffset;
        }

        lastPosition = transform.position;
    }

    private void OnEnable()
    {
        interactAction?.action.Enable();
    }

    private void OnDisable()
    {
        ForceRelease();
        SetFocus(null);
    }

    private void Update()
    {
        var action = interactAction ? interactAction.action : null;
        if (action == null) return;

        bool pressed = action.IsPressed();
        bool pressedThisFrame = action.WasPressedThisFrame();
        if (!pressed) requireRepress = false;

        if (Current)
        {
            UpdateActive(pressed, pressedThisFrame);
            return;
        }

        UpdateFocus();

        if (Focused == null || requireRepress) return;

        // Los objetos pesados se agarran también si ya venías con la tecla apretada.
        bool holdGrab = Focused.Mode == InteractionMode.Hold && pressed;
        if (pressedThisFrame || holdGrab)
            BeginInteraction(Focused);
    }

    private void LateUpdate()
    {
        if (IsRotationLocked) transform.rotation = lockedRotation;

        float dt = Time.deltaTime;
        if (dt > 0f) Velocity = (transform.position - lastPosition) / dt;
        lastPosition = transform.position;
    }

    private void UpdateActive(bool pressed, bool pressedThisFrame)
    {
        if (!Current.isActiveAndEnabled)
        {
            ForceRelease();
            return;
        }

        switch (Current.Mode)
        {
            case InteractionMode.Hold:
                if (!pressed)
                {
                    EndInteraction(InteractionEndReason.Released);
                    return;
                }
                break;

            case InteractionMode.Toggle:
                if (pressedThisFrame)
                {
                    EndInteraction(InteractionEndReason.Action);
                    return;
                }
                break;
        }

        Current.TickFromInteractor(this);
    }

    // ---- API pública ----

    /// <summary>Corta la interacción actual (ej: al hacer dash, recibir daño, abrir un menú).</summary>
    public void ForceRelease()
    {
        if (Current) EndInteraction(InteractionEndReason.Forced);
        else Current = null; // limpia referencias a objetos destruidos
    }

    // ---- Interno ----

    private void BeginInteraction(Interactable target)
    {
        SetFocus(null);
        Current = target;

        if (target.LocksPlayerRotation)
        {
            // Mirar hacia el objeto al agarrarlo.
            Vector3 dir = target.Body.position - transform.position;
            dir.y = 0f;
            lockedRotation = dir.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(dir) : transform.rotation;
            transform.rotation = lockedRotation;
        }

        target.BeginInteraction(this);
        InteractionStarted?.Invoke(target);
    }

    private void EndInteraction(InteractionEndReason reason)
    {
        var target = Current;
        Current = null;
        requireRepress = true;

        target.EndInteraction(this, reason);
        InteractionEnded?.Invoke(target, reason);
    }

    private void UpdateFocus()
    {
        Vector3 origin = transform.TransformPoint(Controller.center);
        float queryRadius = interactRange + Controller.height * 0.5f;
        int count = Physics.OverlapSphereNonAlloc(origin, queryRadius, overlapBuffer,
                                                  interactableMask, QueryTriggerInteraction.Ignore);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        forward.Normalize();

        Interactable best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            var col = overlapBuffer[i];
            var candidate = col.attachedRigidbody
                ? col.attachedRigidbody.GetComponent<Interactable>()
                : col.GetComponentInParent<Interactable>();

            if (candidate == null || !candidate.CanInteract(this)) continue;

            Vector3 closest = GetClosestPoint(col, origin);
            Vector3 toObject = closest - origin;

            if (Mathf.Abs(toObject.y) > Controller.height) continue;
            toObject.y = 0f;

            float distance = toObject.magnitude;
            if (distance > interactRange) continue;

            float angle = distance > 0.01f ? Vector3.Angle(forward, toObject) : 0f;
            if (angle > maxAngle) continue;

            float score = distance + (angle / 180f) * anglePenalty;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        SetFocus(best);
    }

    private static Vector3 GetClosestPoint(Collider col, Vector3 point)
    {
        // ClosestPoint no soporta MeshCollider no convexo.
        if (col is MeshCollider mesh && !mesh.convex)
            return col.bounds.ClosestPoint(point);
        return col.ClosestPoint(point);
    }

    private void SetFocus(Interactable target)
    {
        if (Focused == target) return;

        if (Focused) Focused.SetFocused(false);
        Focused = target;
        if (Focused) Focused.SetFocused(true);

        FocusChanged?.Invoke(Focused);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        var cc = GetComponent<CharacterController>();
        Vector3 origin = cc ? transform.TransformPoint(cc.center) : transform.position;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
        UnityEditor.Handles.color = Gizmos.color;
        UnityEditor.Handles.DrawWireDisc(origin, Vector3.up, interactRange);

        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        Gizmos.DrawRay(origin, Quaternion.AngleAxis(maxAngle, Vector3.up) * fwd * interactRange);
        Gizmos.DrawRay(origin, Quaternion.AngleAxis(-maxAngle, Vector3.up) * fwd * interactRange);

        Gizmos.color = Color.yellow;
        Vector3 hold = holdPoint ? holdPoint.position : transform.TransformPoint(defaultHoldOffset);
        Gizmos.DrawWireSphere(hold, 0.15f);
    }
#endif
}

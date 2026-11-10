using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Traduce el estado del player (movimiento, salto, dash, interacciones) a parámetros del Animator.
/// Solo LEE de los otros scripts: el gameplay no depende de las animaciones.
///
/// Va en la raíz del Player. El Animator va en un hijo (el modelo) con Apply Root Motion desactivado.
/// Los parámetros que no existan en el Animator Controller se ignoran, así se puede armar por fases.
/// </summary>
// Corre después de PlayerMovementController (0) y PlayerInteractor (100) para leer el estado final del frame.
[DefaultExecutionOrder(200)]
public class PlayerAnimator : MonoBehaviour
{
    [Header("Referencias")]
    [Tooltip("Si está vacío se busca en los hijos.")]
    [SerializeField] private Animator animator;

    [Header("Suavizado (seg)")]
    [SerializeField] private float speedDampTime = 0.1f;
    [SerializeField] private float directionDampTime = 0.1f;

    [Header("Piso")]
    [Tooltip("Tiempo sin piso antes de considerar que está en el aire. Evita parpadeos en escalones y rampas.")]
    [SerializeField] private float airborneGraceTime = 0.1f;

    [Header("Aterrizaje")]
    [Tooltip("Velocidad de caída a partir de la cual se dispara el trigger HardLand.")]
    [SerializeField] private float hardLandSpeed = 12f;

    // ---- Parámetros del Animator ----
    private static readonly int SpeedHash            = Animator.StringToHash("Speed");
    private static readonly int MoveXHash            = Animator.StringToHash("MoveX");
    private static readonly int MoveZHash            = Animator.StringToHash("MoveZ");
    private static readonly int IsGroundedHash       = Animator.StringToHash("IsGrounded");
    private static readonly int VerticalVelocityHash = Animator.StringToHash("VerticalVelocity");
    private static readonly int IsAimingHash         = Animator.StringToHash("IsAiming");
    private static readonly int IsDashingHash        = Animator.StringToHash("IsDashing");
    private static readonly int IsDraggingHash       = Animator.StringToHash("IsDragging");
    private static readonly int IsCarryingHash       = Animator.StringToHash("IsCarrying");
    private static readonly int JumpHash             = Animator.StringToHash("Jump");
    private static readonly int HardLandHash         = Animator.StringToHash("HardLand");
    private static readonly int DashHash             = Animator.StringToHash("Dash");
    private static readonly int PickUpHash           = Animator.StringToHash("PickUp");
    private static readonly int ThrowHash            = Animator.StringToHash("Throw");

    private PlayerMovementController movement;
    private PlayerDash dash;
    private PlayerInteractor interactor;
    private CharacterController controller;

    private readonly HashSet<int> existingParams = new HashSet<int>();
    private Vector3 lastPosition;
    private float lastGroundedTime;

    private void Awake()
    {
        movement   = GetComponent<PlayerMovementController>();
        dash       = GetComponent<PlayerDash>();
        interactor = GetComponent<PlayerInteractor>();
        controller = GetComponent<CharacterController>();

        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogError("[PlayerAnimator] No se encontró un Animator en el player ni en sus hijos.", this);
            enabled = false;
            return;
        }

        // El movimiento lo maneja el CharacterController: la animación solo acompaña.
        animator.applyRootMotion = false;

        CacheParameters();
        lastPosition = transform.position;
        lastGroundedTime = Time.time;
    }

    private void OnEnable()
    {
        if (movement)
        {
            movement.Jumped += OnJumped;
            movement.Landed += OnLanded;
        }
        if (dash)
        {
            dash.DashStarted += OnDashStarted;
            dash.DashEnded   += OnDashEnded;
        }
        if (interactor)
        {
            interactor.InteractionStarted += OnInteractionStarted;
            interactor.InteractionEnded   += OnInteractionEnded;
        }
    }

    private void OnDisable()
    {
        if (movement)
        {
            movement.Jumped -= OnJumped;
            movement.Landed -= OnLanded;
        }
        if (dash)
        {
            dash.DashStarted -= OnDashStarted;
            dash.DashEnded   -= OnDashEnded;
        }
        if (interactor)
        {
            interactor.InteractionStarted -= OnInteractionStarted;
            interactor.InteractionEnded   -= OnInteractionEnded;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Velocidad real (por desplazamiento): incluye las correcciones del objeto pesado,
        // así si queda trabado contra una pared el personaje no camina en el lugar.
        Vector3 velocity = (transform.position - lastPosition) / dt;
        lastPosition = transform.position;

        bool isDashing = dash && dash.IsDashing;
        SetBool(IsDashingHash, isDashing);

        UpdateLocomotion(velocity, isDashing, dt);
        UpdateAirborne();
        UpdateStates();
    }

    // ---------------- Locomoción ----------------

    private void UpdateLocomotion(Vector3 velocity, bool isDashing, float dt)
    {
        // Durante el dash la velocidad es enorme: congelamos la locomoción para que al salir no haya un pico.
        if (isDashing) return;

        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
        float refSpeed = movement ? Mathf.Max(0.01f, movement.MoveSpeed) : 5f;

        // Speed: 0 = quieto, ~0.6 = apuntando, 1 = velocidad base.
        float speed = Mathf.Clamp01(horizontal.magnitude / refSpeed);
        SetFloat(SpeedHash, speed, speedDampTime, dt);

        // Dirección local para el blend tree 2D (strafe al apuntar, empujar/tirar al arrastrar).
        Vector3 local = transform.InverseTransformDirection(horizontal) / refSpeed;
        local = Vector3.ClampMagnitude(local, 1f);
        SetFloat(MoveXHash, local.x, directionDampTime, dt);
        SetFloat(MoveZHash, local.z, directionDampTime, dt);

        SetBool(IsAimingHash, movement && movement.IsAiming);
    }

    // ---------------- Aire ----------------

    private void UpdateAirborne()
    {
        if (controller.isGrounded) lastGroundedTime = Time.time;

        // Si saltó, está en el aire desde el primer frame (sin esperar el margen).
        bool jumping = movement && movement.IsJumping;
        bool grounded = !jumping && Time.time - lastGroundedTime <= airborneGraceTime;

        SetBool(IsGroundedHash, grounded);
        SetFloat(VerticalVelocityHash, movement ? movement.VerticalVelocity : 0f);
    }

    // ---------------- Interacciones ----------------

    private void UpdateStates()
    {
        Interactable current = interactor ? interactor.Current : null;
        SetBool(IsDraggingHash, current && current.Mode == InteractionMode.Hold);
        SetBool(IsCarryingHash, current && current.Mode == InteractionMode.Toggle);
    }

    // ---------------- Eventos ----------------

    private void OnJumped()
    {
        ResetTrigger(HardLandHash);
        SetTrigger(JumpHash);
    }

    private void OnLanded(float fallSpeed)
    {
        // Si el trigger de salto no se llegó a consumir, que no quede "pendiente" para más tarde.
        ResetTrigger(JumpHash);
        if (fallSpeed >= hardLandSpeed) SetTrigger(HardLandHash);
    }

    private void OnDashStarted()
    {
        SetTrigger(DashHash);
    }

    private void OnDashEnded()
    {
        ResetTrigger(DashHash);
    }

    private void OnInteractionStarted(Interactable target)
    {
        ResetTrigger(ThrowHash);
        if (target.Mode == InteractionMode.Toggle) SetTrigger(PickUpHash);
    }

    private void OnInteractionEnded(Interactable target, InteractionEndReason reason)
    {
        ResetTrigger(PickUpHash);
        if (target.Mode == InteractionMode.Toggle && reason == InteractionEndReason.Action)
            SetTrigger(ThrowHash);
    }

    // ---------------- Helpers (ignoran parámetros que no existen) ----------------

    private void CacheParameters()
    {
        existingParams.Clear();
        foreach (var p in animator.parameters)
            existingParams.Add(p.nameHash);
    }

    private bool Has(int hash) => existingParams.Contains(hash);

    private void SetFloat(int hash, float value)
    {
        if (Has(hash)) animator.SetFloat(hash, value);
    }

    private void SetFloat(int hash, float value, float dampTime, float dt)
    {
        if (Has(hash)) animator.SetFloat(hash, value, dampTime, dt);
    }

    private void SetBool(int hash, bool value)
    {
        if (Has(hash)) animator.SetBool(hash, value);
    }

    private void SetTrigger(int hash)
    {
        if (Has(hash)) animator.SetTrigger(hash);
    }

    private void ResetTrigger(int hash)
    {
        if (Has(hash)) animator.ResetTrigger(hash);
    }
}

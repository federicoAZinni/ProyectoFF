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

    [Header("Medición de velocidad")]
    [Tooltip("Ventana de muestreo de la velocidad. Promedia varios frames para que no salte " +
             "cuando el movimiento ocurre en FixedUpdate o no todos los frames.")]
    [SerializeField] private float velocitySampleWindow = 0.05f;
    [Tooltip("Por debajo de este porcentaje de MoveSpeed se considera quieto (filtra el ruido del CharacterController).")]
    [Range(0f, 0.3f)]
    [SerializeField] private float deadzone = 0.05f;
    [Tooltip("Cuando el objetivo es 0 y el valor suavizado baja de esto, se fija en 0 exacto.")]
    [SerializeField] private float snapToZeroThreshold = 0.01f;
    [Tooltip("Desplazamientos más rápidos que MoveSpeed x esto se consideran teletransporte y se ignoran.")]
    [SerializeField] private float teleportSpeedMultiplier = 4f;

    [Header("Piso")]
    [Tooltip("Tiempo sin piso antes de considerar que está en el aire. Evita parpadeos en escalones y rampas.")]
    [SerializeField] private float airborneGraceTime = 0.1f;

    [Header("Aterrizaje")]
    [Tooltip("Velocidad de caída a partir de la cual se dispara el trigger HardLand.")]
    [SerializeField] private float hardLandSpeed = 12f;

    // ---- Parámetros del Animator ----
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int MoveXHash = Animator.StringToHash("MoveX");
    private static readonly int MoveZHash = Animator.StringToHash("MoveZ");
    private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");
    private static readonly int VerticalVelocityHash = Animator.StringToHash("VerticalVelocity");
    private static readonly int IsAimingHash = Animator.StringToHash("IsAiming");
    private static readonly int IsDashingHash = Animator.StringToHash("IsDashing");
    private static readonly int IsDraggingHash = Animator.StringToHash("IsDragging");
    private static readonly int IsCarryingHash = Animator.StringToHash("IsCarrying");
    private static readonly int JumpHash = Animator.StringToHash("Jump");
    private static readonly int HardLandHash = Animator.StringToHash("HardLand");
    private static readonly int DashHash = Animator.StringToHash("Dash");
    private static readonly int PickUpHash = Animator.StringToHash("PickUp");
    private static readonly int ThrowHash = Animator.StringToHash("Throw");

    private PlayerMovementController movement;
    private PlayerDash dash;
    private PlayerInteractor interactor;
    private CharacterController controller;

    private readonly HashSet<int> existingParams = new HashSet<int>();
    private float lastGroundedTime;

    // Medición de velocidad por ventana
    private Vector3 lastPosition;
    private Vector3 accumulatedDisplacement;
    private float accumulatedTime;
    private Vector3 measuredVelocity;

    private void Awake()
    {
        movement = GetComponent<PlayerMovementController>();
        dash = GetComponent<PlayerDash>();
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
        ResetVelocitySampling();
        lastGroundedTime = Time.time;
    }

    private void OnEnable()
    {
        ResetVelocitySampling();

        if (movement)
        {
            movement.Jumped += OnJumped;
            movement.Landed += OnLanded;
        }
        if (dash)
        {
            dash.DashStarted += OnDashStarted;
            dash.DashEnded += OnDashEnded;
        }
        if (interactor)
        {
            interactor.InteractionStarted += OnInteractionStarted;
            interactor.InteractionEnded += OnInteractionEnded;
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
            dash.DashEnded -= OnDashEnded;
        }
        if (interactor)
        {
            interactor.InteractionStarted -= OnInteractionStarted;
            interactor.InteractionEnded -= OnInteractionEnded;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        SampleVelocity(dt);

        bool isDashing = dash && dash.IsDashing;
        SetBool(IsDashingHash, isDashing);

        UpdateLocomotion(measuredVelocity, isDashing, dt);
        UpdateAirborne();
        UpdateStates();
    }

    // ---------------- Medición de velocidad ----------------

    /// <summary>
    /// Velocidad real (por desplazamiento), promediada en una ventana corta.
    /// Incluye las correcciones del objeto pesado: si queda trabado contra una pared no camina en el lugar.
    /// Promediar evita los frames con desplazamiento 0 / doble cuando el movimiento va en FixedUpdate.
    /// </summary>
    private void SampleVelocity(float dt)
    {
        Vector3 pos = transform.position;
        Vector3 delta = pos - lastPosition;
        lastPosition = pos;

        // Teletransporte / respawn: no lo contamos como movimiento.
        float refSpeed = RefSpeed;
        if (delta.magnitude / dt > refSpeed * teleportSpeedMultiplier && !(dash && dash.IsDashing))
        {
            accumulatedDisplacement = Vector3.zero;
            accumulatedTime = 0f;
            measuredVelocity = Vector3.zero;
            return;
        }

        accumulatedDisplacement += delta;
        accumulatedTime += dt;

        if (accumulatedTime >= velocitySampleWindow)
        {
            measuredVelocity = accumulatedDisplacement / accumulatedTime;
            accumulatedDisplacement = Vector3.zero;
            accumulatedTime = 0f;
        }
    }

    private void ResetVelocitySampling()
    {
        lastPosition = transform.position;
        accumulatedDisplacement = Vector3.zero;
        accumulatedTime = 0f;
        measuredVelocity = Vector3.zero;
    }

    private float RefSpeed => movement ? Mathf.Max(0.01f, movement.MoveSpeed) : 5f;

    // ---------------- Locomoción ----------------

    private void UpdateLocomotion(Vector3 velocity, bool isDashing, float dt)
    {
        // Durante el dash la velocidad es enorme: congelamos la locomoción para que al salir no haya un pico.
        if (isDashing) return;

        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
        float refSpeed = RefSpeed;

        // Deadzone: el ruido del CharacterController (gravedad, skin width, rampas) no es caminar.
        if (horizontal.magnitude < refSpeed * deadzone)
            horizontal = Vector3.zero;

        // Speed: 0 = quieto, ~0.6 = apuntando, 1 = velocidad base.
        float speed = Mathf.Clamp01(horizontal.magnitude / refSpeed);
        SetFloatDampedSnap(SpeedHash, speed, speedDampTime, dt);

        // Dirección local para el blend tree 2D (strafe al apuntar, empujar/tirar al arrastrar).
        Vector3 local = transform.InverseTransformDirection(horizontal) / refSpeed;
        local = Vector3.ClampMagnitude(local, 1f);
        SetFloatDampedSnap(MoveXHash, local.x, directionDampTime, dt);
        SetFloatDampedSnap(MoveZHash, local.z, directionDampTime, dt);

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
        // Que el desplazamiento del dash no contamine la primera muestra después.
        ResetVelocitySampling();
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

    /// <summary>
    /// SetFloat con damping, pero que llega a 0 exacto: el damping del Animator es exponencial
    /// y si no se queda eternamente en valores tipo 0.00003.
    /// </summary>
    private void SetFloatDampedSnap(int hash, float value, float dampTime, float dt)
    {
        if (!Has(hash)) return;

        animator.SetFloat(hash, value, dampTime, dt);

        if (Mathf.Approximately(value, 0f) && Mathf.Abs(animator.GetFloat(hash)) < snapToZeroThreshold)
            animator.SetFloat(hash, 0f);
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
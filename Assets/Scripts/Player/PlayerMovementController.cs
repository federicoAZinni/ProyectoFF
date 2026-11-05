using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Movimiento top-down con el nuevo Input System.
/// - Stick izquierdo / WASD: mueve al personaje (mira hacia donde camina).
/// - Mantener "Aim" (click derecho / botón de gamepad): el personaje rota
///   con el stick derecho (gamepad) o hacia el cursor (mouse).
/// - Jump (Espacio / botón sur): salto con coyote time, buffer y altura variable.
/// Compatible con Cinemachine: usa Camera.main (la que tiene el CinemachineBrain).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovementController : MonoBehaviour
{
    [Header("Input (Input System)")]
    [Tooltip("Vector2 - ej: Player/Move (stick izquierdo / WASD)")]
    [SerializeField] private InputActionReference moveAction;
    [Tooltip("Button - ej: Player/Aim (click derecho / gatillo del gamepad)")]
    [SerializeField] private InputActionReference aimAction;
    [Tooltip("Vector2 - ej: Player/Look (stick derecho). Solo se usa con gamepad")]
    [SerializeField] private InputActionReference lookAction;
    [Tooltip("Button - ej: Player/Jump (Espacio / botón sur del gamepad)")]
    [SerializeField] private InputActionReference jumpAction;

    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 5f;
    [Tooltip("Multiplicador de velocidad mientras se apunta (1 = sin cambio)")]
    [SerializeField, Range(0f, 1f)] private float aimSpeedMultiplier = 0.6f;
    [SerializeField] private float acceleration = 20f;
    [SerializeField] private float deceleration = 25f;
    [SerializeField] private float gravity = -20f;

    [Header("Salto")]
    [Tooltip("Altura máxima del salto (en unidades). La velocidad inicial se calcula a partir de la gravedad.")]
    [SerializeField] private float jumpHeight = 1.5f;
    [Tooltip("Margen para saltar justo después de caerse de un borde (seg)")]
    [SerializeField] private float coyoteTime = 0.12f;
    [Tooltip("Margen para registrar el salto si se aprieta un poco antes de tocar el piso (seg)")]
    [SerializeField] private float jumpBufferTime = 0.12f;
    [Tooltip("Al soltar el botón subiendo, la velocidad vertical se multiplica por esto (1 = altura fija)")]
    [SerializeField, Range(0f, 1f)] private float jumpCutMultiplier = 0.5f;
    [Tooltip("Control horizontal en el aire (1 = igual que en el piso)")]
    [SerializeField, Range(0f, 1f)] private float airControl = 0.6f;

    [Header("Rotación")]
    [Tooltip("Grados/seg al rotar hacia la dirección de movimiento")]
    [SerializeField] private float moveRotationSpeed = 720f;
    [Tooltip("Grados/seg al rotar mientras se apunta")]
    [SerializeField] private float aimRotationSpeed = 1080f;
    [Tooltip("Zona muerta del stick derecho")]
    [SerializeField, Range(0f, 1f)] private float lookDeadZone = 0.2f;

    [Header("Cámara")]
    [Tooltip("Si está vacío usa Camera.main (la del CinemachineBrain)")]
    [SerializeField] private Camera cam;
    [Tooltip("El movimiento es relativo a la orientación de la cámara")]
    [SerializeField] private bool cameraRelative = true;

    private CharacterController controller;
    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private PlayerDash dash;

    private float lastGroundedTime = float.NegativeInfinity;
    private float lastJumpPressedTime = float.NegativeInfinity;

    public bool IsAiming { get; private set; }
    public bool IsGrounded => controller.isGrounded;
    /// <summary>True desde que se salta hasta volver a tocar el piso.</summary>
    public bool IsJumping { get; private set; }
    /// <summary>Dirección de movimiento en el mundo según el input (magnitud 0..1).</summary>
    public Vector3 MoveDirection { get; private set; }
    public Vector3 Velocity => horizontalVelocity;
    public float VerticalVelocity => verticalVelocity;
    public float CurrentSpeed => horizontalVelocity.magnitude;

    //Ref interaction
    private PlayerInteractor interactor;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        dash = GetComponent<PlayerDash>();
        if (cam == null) cam = Camera.main;
        interactor = GetComponent<PlayerInteractor>();
    }

    private void OnEnable()
    {
        moveAction?.action.Enable();
        aimAction?.action.Enable();
        lookAction?.action.Enable();
        jumpAction?.action.Enable();
    }

    private void OnDisable()
    {
        moveAction?.action.Disable();
        aimAction?.action.Disable();
        lookAction?.action.Disable();
        jumpAction?.action.Disable();
    }

    private void Update()
    {
        // Se registra antes del dash para que un salto apretado al final del dash no se pierda (buffer).
        if (jumpAction != null && jumpAction.action.WasPressedThisFrame())
            lastJumpPressedTime = Time.time;

        // Durante el dash, el PlayerDash controla el movimiento
        if (dash != null && dash.IsDashing)
        {
            // Al terminar el dash salimos con algo de inercia hacia adelante
            horizontalVelocity = dash.DashDirection * moveSpeed;
            verticalVelocity = 0f;
            return;
        }

        IsAiming = aimAction != null && aimAction.action.IsPressed();

        Vector3 moveDir = GetMoveDirection();
        MoveDirection = moveDir;
        HandleMovement(moveDir);

        if (IsAiming)
            HandleAimRotation();
        else
            RotateTowards(moveDir, moveRotationSpeed);
    }

    // ---------------- Movimiento ----------------

    private Vector3 GetMoveDirection()
    {
        Vector2 input = moveAction != null ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;
        input = Vector2.ClampMagnitude(input, 1f);
        return InputToWorld(input);
    }

    private void HandleMovement(Vector3 moveDir)
    {
        bool grounded = controller.isGrounded;

        float speed = moveSpeed * (IsAiming ? aimSpeedMultiplier : 1f);
        if (interactor) speed *= interactor.SpeedMultiplier; //Acá se lentea la velocidad en caso de estar interactuando con un HeavyObject.
        Vector3 targetVelocity = moveDir * speed;

        float rate = targetVelocity.sqrMagnitude > horizontalVelocity.sqrMagnitude ? acceleration : deceleration;
        if (!grounded) rate *= airControl;
        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, rate * Time.deltaTime);

        if (grounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
            IsJumping = false;
        }

        HandleJump(grounded);

        verticalVelocity += gravity * Time.deltaTime;

        CollisionFlags flags = controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);

        // Si se golpea la cabeza contra un techo, empieza a caer en vez de quedar "pegado".
        if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
            verticalVelocity = 0f;
    }

    // ---------------- Salto ----------------

    private void HandleJump(bool grounded)
    {
        if (jumpAction == null) return;

        if (grounded) lastGroundedTime = Time.time;

        bool canJump = Time.time - lastGroundedTime <= coyoteTime;
        bool wantsJump = Time.time - lastJumpPressedTime <= jumpBufferTime;

        // Arrastrando un objeto pesado no se salta: el "joint" trabaja en el plano XZ.
        if (interactor && interactor.Current is HeavyPushable) canJump = false;

        if (canJump && wantsJump)
        {
            verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
            IsJumping = true;

            // Consumimos ambos para que no haya doble salto por coyote time o buffer.
            lastGroundedTime = float.NegativeInfinity;
            lastJumpPressedTime = float.NegativeInfinity;
        }

        // Salto variable: soltar el botón mientras se sube corta el salto.
        if (IsJumping && verticalVelocity > 0f && jumpAction.action.WasReleasedThisFrame())
            verticalVelocity *= jumpCutMultiplier;
    }

    // ---------------- Rotación ----------------

    private void HandleAimRotation()
    {
        // Detectamos qué dispositivo activó el botón de apuntar
        InputDevice device = aimAction.action.activeControl?.device;

        Vector3 aimDir;
        if (device is Gamepad)
            aimDir = GetStickAimDirection();
        else
            aimDir = GetMouseAimDirection();

        RotateTowards(aimDir, aimRotationSpeed);
    }

    private Vector3 GetStickAimDirection()
    {
        if (lookAction == null) return Vector3.zero;

        Vector2 stick = lookAction.action.ReadValue<Vector2>();
        if (stick.magnitude < lookDeadZone) return Vector3.zero; // mantiene la rotación actual

        return InputToWorld(stick.normalized);
    }

    private Vector3 GetMouseAimDirection()
    {
        if (cam == null || Pointer.current == null) return Vector3.zero;

        Ray ray = cam.ScreenPointToRay(Pointer.current.position.ReadValue());
        Plane groundPlane = new Plane(Vector3.up, transform.position);

        if (groundPlane.Raycast(ray, out float distance))
            return ray.GetPoint(distance) - transform.position;

        return Vector3.zero;
    }

    private void RotateTowards(Vector3 direction, float degreesPerSecond)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        Quaternion target = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, degreesPerSecond * Time.deltaTime);
    }

    // ---------------- Utilidades ----------------

    /// <summary>Convierte un input 2D a una dirección en el mundo (relativa a la cámara si corresponde).</summary>
    private Vector3 InputToWorld(Vector2 input)
    {
        if (!cameraRelative || cam == null)
            return new Vector3(input.x, 0f, input.y);

        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.01f) // cámara mirando totalmente hacia abajo
            forward = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward);
        return forward * input.y + right * input.x;
    }
}
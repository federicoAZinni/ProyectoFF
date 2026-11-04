using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Movimiento top-down con el nuevo Input System.
/// - Stick izquierdo / WASD: mueve al personaje (mira hacia donde camina).
/// - Mantener "Aim" (click derecho / botón de gamepad): el personaje rota
///   con el stick derecho (gamepad) o hacia el cursor (mouse).
/// Compatible con Cinemachine: usa Camera.main (la que tiene el CinemachineBrain).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class TopDownPlayerController : MonoBehaviour
{
    [Header("Input (Input System)")]
    [Tooltip("Vector2 - ej: Player/Move (stick izquierdo / WASD)")]
    [SerializeField] private InputActionReference moveAction;
    [Tooltip("Button - ej: Player/Aim (click derecho / gatillo del gamepad)")]
    [SerializeField] private InputActionReference aimAction;
    [Tooltip("Vector2 - ej: Player/Look (stick derecho). Solo se usa con gamepad")]
    [SerializeField] private InputActionReference lookAction;

    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 5f;
    [Tooltip("Multiplicador de velocidad mientras se apunta (1 = sin cambio)")]
    [SerializeField, Range(0f, 1f)] private float aimSpeedMultiplier = 0.6f;
    [SerializeField] private float acceleration = 20f;
    [SerializeField] private float deceleration = 25f;
    [SerializeField] private float gravity = -20f;

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

    public bool IsAiming { get; private set; }
    /// <summary>Dirección de movimiento en el mundo según el input (magnitud 0..1).</summary>
    public Vector3 MoveDirection { get; private set; }
    public Vector3 Velocity => horizontalVelocity;
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
    }

    private void OnDisable()
    {
        moveAction?.action.Disable();
        aimAction?.action.Disable();
        lookAction?.action.Disable();
    }

    private void Update()
    {
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
        float speed = moveSpeed * (IsAiming ? aimSpeedMultiplier : 1f);
        if (interactor) speed *= interactor.SpeedMultiplier; //Acá se lentea la velocidad en caso de estar interactuando con un HeavyObject.
        Vector3 targetVelocity = moveDir * speed;

        float rate = targetVelocity.sqrMagnitude > horizontalVelocity.sqrMagnitude ? acceleration : deceleration;
        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, rate * Time.deltaTime);

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;

        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
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
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Dash hacia la dirección en la que se mueve el player
/// (si está quieto, hacia su forward).
/// - Calcula la distancia disponible con un CapsuleCast para no atravesar obstáculos.
/// - Cambia el layer del player a "InvinciblePlayer" durante un tiempo configurable
///   y luego restaura el layer original.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerDash : MonoBehaviour
{
    [Header("Input (Input System)")]
    [Tooltip("Button - ej: Player/Dash")]
    [SerializeField] private InputActionReference dashAction;

    [Header("Dash")]
    [Tooltip("Distancia máxima del dash (en unidades)")]
    [SerializeField] private float dashDistance = 5f;
    [Tooltip("Velocidad del dash (unidades/seg)")]
    [SerializeField] private float dashSpeed = 25f;
    [Tooltip("Tiempo de espera entre dashes")]
    [SerializeField] private float cooldown = 0.5f;

    [Header("Invencibilidad")]
    [SerializeField] private string invincibleLayerName = "InvinciblePlayer";
    [Tooltip("Tiempo que el player permanece en el layer InvinciblePlayer")]
    [SerializeField] private float invincibleDuration = 0.3f;
    [Tooltip("Cambiar también el layer de los hijos (hurtboxes, etc.)")]
    [SerializeField] private bool includeChildren = true;

    [Tooltip("Input mínimo del stick para considerar que el player se está moviendo")]
    [SerializeField, Range(0f, 1f)] private float moveInputThreshold = 0.1f;

    [Header("Detección de obstáculos")]
    [Tooltip("Layers que frenan el dash (paredes, props...). No incluyas el layer del player.")]
    [SerializeField] private LayerMask obstacleMask = ~0;
    [Tooltip("Margen que se deja entre el player y el obstáculo")]
    [SerializeField] private float wallOffset = 0.1f;

    private CharacterController controller;
    private PlayerMovementController movement;
    private int invincibleLayer = -1;
    private float nextDashTime;

    private Transform[] layerTargets;
    private int[] originalLayers;

    private Coroutine dashRoutine;
    private Coroutine invincibleRoutine;

    public bool IsDashing { get; private set; }
    public Vector3 DashDirection { get; private set; }
    public bool IsInvincible { get; private set; }
    public bool CanDash => !IsDashing && Time.time >= nextDashTime;

    public event Action DashStarted;
    public event Action DashEnded;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        movement = GetComponent<PlayerMovementController>();

        invincibleLayer = LayerMask.NameToLayer(invincibleLayerName);
        if (invincibleLayer == -1)
            Debug.LogError($"[PlayerDash] No existe el layer '{invincibleLayerName}'. Crealo en Tags & Layers.", this);
    }

    private void OnEnable()
    {
        if (dashAction == null) return;
        dashAction.action.performed += OnDashPerformed;
        dashAction.action.Enable();
    }

    private void OnDisable()
    {
        if (dashAction != null)
        {
            dashAction.action.performed -= OnDashPerformed;
            dashAction.action.Disable();
        }

        if (dashRoutine != null) StopCoroutine(dashRoutine);
        if (invincibleRoutine != null) StopCoroutine(invincibleRoutine);

        IsDashing = false;
        RestoreLayers();
    }

    private void OnDashPerformed(InputAction.CallbackContext ctx) => TryDash();

    /// <summary>Intenta ejecutar el dash. Se puede llamar también desde otros scripts.</summary>
    public bool TryDash()
    {
        if (!CanDash) return false;

        Vector3 direction = GetDashDirection();
        DashDirection = direction;
        float distance = GetAvailableDistance(direction);

        // Invencibilidad (independiente de la duración del dash)
        if (invincibleRoutine != null) StopCoroutine(invincibleRoutine);
        invincibleRoutine = StartCoroutine(InvincibleRoutine());

        dashRoutine = StartCoroutine(DashRoutine(direction, distance));
        return true;
    }

    /// <summary>Dirección del movimiento actual; si no hay input, el forward del player.</summary>
    private Vector3 GetDashDirection()
    {
        if (movement != null)
        {
            Vector3 moveDir = movement.MoveDirection;
            moveDir.y = 0f;
            if (moveDir.magnitude >= moveInputThreshold)
                return moveDir.normalized;
        }

        return Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
    }

    /// <summary>Lanza un CapsuleCast hacia adelante y devuelve cuánto se puede avanzar.</summary>
    private float GetAvailableDistance(Vector3 direction)
    {
        GetCapsulePoints(out Vector3 p1, out Vector3 p2, out float radius);

        if (Physics.CapsuleCast(p1, p2, radius, direction, out RaycastHit hit,
                dashDistance, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            return Mathf.Max(0f, hit.distance - wallOffset);
        }

        return dashDistance;
    }

    private void GetCapsulePoints(out Vector3 p1, out Vector3 p2, out float radius)
    {
        // Radio un poco menor y cápsula levantada apenas para no detectar el piso
        radius = controller.radius * 0.95f;
        Vector3 center = transform.TransformPoint(controller.center) + Vector3.up * 0.05f;
        float half = Mathf.Max(0f, controller.height * 0.5f - controller.radius);
        p1 = center + Vector3.up * half;
        p2 = center - Vector3.up * half;
    }

    private IEnumerator DashRoutine(Vector3 direction, float distance)
    {
        IsDashing = true;
        DashStarted?.Invoke();

        float traveled = 0f;
        while (traveled < distance)
        {
            float step = Mathf.Min(dashSpeed * Time.deltaTime, distance - traveled);
            CollisionFlags flags = controller.Move(direction * step);
            traveled += step;

            // Seguridad extra: si chocamos de costado, cortamos el dash
            if ((flags & CollisionFlags.Sides) != 0) break;

            yield return null;
        }

        IsDashing = false;
        nextDashTime = Time.time + cooldown;
        dashRoutine = null;
        DashEnded?.Invoke();
    }

    private IEnumerator InvincibleRoutine()
    {
        if (invincibleLayer == -1) yield break;

        // Solo guardamos los layers originales si no estábamos ya invencibles
        if (!IsInvincible) CacheOriginalLayers();

        IsInvincible = true;
        foreach (Transform t in layerTargets)
            t.gameObject.layer = invincibleLayer;

        yield return new WaitForSeconds(invincibleDuration);

        RestoreLayers();
        invincibleRoutine = null;
    }

    private void CacheOriginalLayers()
    {
        layerTargets = includeChildren
            ? GetComponentsInChildren<Transform>(true)
            : new[] { transform };

        originalLayers = new int[layerTargets.Length];
        for (int i = 0; i < layerTargets.Length; i++)
            originalLayers[i] = layerTargets[i].gameObject.layer;
    }

    private void RestoreLayers()
    {
        if (!IsInvincible || layerTargets == null) return;

        for (int i = 0; i < layerTargets.Length; i++)
            if (layerTargets[i] != null)
                layerTargets[i].gameObject.layer = originalLayers[i];

        IsInvincible = false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (controller == null) controller = GetComponent<CharacterController>();
        if (controller == null) return;

        Vector3 dir = Application.isPlaying
            ? GetDashDirection()
            : Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        float dist = Application.isPlaying ? GetAvailableDistance(dir) : dashDistance;
        Vector3 start = transform.TransformPoint(controller.center);

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(start, start + dir * dist);
        Gizmos.DrawWireSphere(start + dir * dist, controller.radius);
    }
#endif
}
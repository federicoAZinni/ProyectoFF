using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public enum InteractionMode
{
    Hold,   // Se interactúa mientras se mantiene apretada la tecla (objeto pesado)
    Toggle  // Un toque empieza, otro toque ejecuta la acción (objeto liviano: tirar)
}

public enum InteractionEndReason
{
    Released, // El jugador soltó la tecla
    Action,   // El jugador volvió a apretar (ej: tirar el objeto)
    Forced    // Cortado por el sistema (distancia, dash, objeto desactivado...)
}

[RequireComponent(typeof(Rigidbody))]
public abstract class Interactable : MonoBehaviour
{
    [Header("Feedback (opcional)")]
    [SerializeField] private UnityEvent onFocused;
    [SerializeField] private UnityEvent onUnfocused;

    public Rigidbody Body { get; private set; }
    public PlayerInteractor Interactor { get; private set; }
    public bool IsBeingInteracted => Interactor != null;

    public abstract InteractionMode Mode { get; }
    public abstract float PlayerSpeedMultiplier { get; }
    public virtual bool LocksPlayerRotation => false;

    private Collider[] ownColliders;
    private PlayerInteractor ignoredInteractor;
    private Coroutine restoreRoutine;

    protected virtual void Awake()
    {
        Body = GetComponent<Rigidbody>();
        ownColliders = GetComponentsInChildren<Collider>();

        // El player se mueve en Update y el objeto en FixedUpdate: sin interpolación se ve temblequeo.
        if (Body.interpolation == RigidbodyInterpolation.None)
            Body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    protected virtual void OnDisable()
    {
        // Si se desactiva con una restauración pendiente, la hacemos ya para no dejar colisiones ignoradas.
        if (restoreRoutine != null)
        {
            StopCoroutine(restoreRoutine);
            restoreRoutine = null;
        }
        if (!IsBeingInteracted) SetIgnorePlayerCollision(false);
    }

    public virtual bool CanInteract(PlayerInteractor interactor) => isActiveAndEnabled && !IsBeingInteracted;

    // ---- Llamados por PlayerInteractor ----

    public void BeginInteraction(PlayerInteractor interactor)
    {
        if (restoreRoutine != null)
        {
            StopCoroutine(restoreRoutine);
            restoreRoutine = null;
        }

        Interactor = interactor;
        ignoredInteractor = interactor;
        SetIgnorePlayerCollision(true);
        Body.WakeUp();
        OnInteractionBegin(interactor);
    }

    public void EndInteraction(PlayerInteractor interactor, InteractionEndReason reason)
    {
        OnInteractionEnd(interactor, reason);
        Interactor = null;

        float delay = GetCollisionRestoreDelay(reason);
        if (delay <= 0f || !isActiveAndEnabled)
            SetIgnorePlayerCollision(false);
        else
            restoreRoutine = StartCoroutine(RestoreCollisionAfter(delay));
    }

    /// <summary>Se llama desde el Update del PlayerInteractor, después de que el player se movió.</summary>
    public virtual void TickFromInteractor(PlayerInteractor interactor) { }

    public void SetFocused(bool focused)
    {
        if (focused) onFocused?.Invoke();
        else onUnfocused?.Invoke();
    }

    // ---- Para las subclases ----

    protected abstract void OnInteractionBegin(PlayerInteractor interactor);
    protected abstract void OnInteractionEnd(PlayerInteractor interactor, InteractionEndReason reason);
    protected virtual float GetCollisionRestoreDelay(InteractionEndReason reason) => 0f;

    // ---- Colisión con el player ----

    private void SetIgnorePlayerCollision(bool ignore)
    {
        if (ignoredInteractor == null) return;

        foreach (var playerCol in ignoredInteractor.PlayerColliders)
        {
            if (playerCol == null) continue;
            foreach (var col in ownColliders)
            {
                if (col != null) Physics.IgnoreCollision(playerCol, col, ignore);
            }
        }

        if (!ignore) ignoredInteractor = null;
    }

    private IEnumerator RestoreCollisionAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        restoreRoutine = null;
        if (!IsBeingInteracted) SetIgnorePlayerCollision(false);
    }
}

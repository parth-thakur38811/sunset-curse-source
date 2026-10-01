using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Base class for anything the player can interact with by looking at it and pressing E:
    /// berry bushes now, cave valuables and doors later.
    ///
    /// Make a new interactable by inheriting this and overriding <see cref="Interact"/>.
    /// The player's <c>PlayerInteractor</c> finds these via a forward raycast, shows a
    /// "Press E to {Verb}" prompt, and calls Interact() when E is pressed.
    ///
    /// NOTE: the object needs a Collider for the raycast to hit it.
    /// </summary>
    public abstract class Interactable : MonoBehaviour
    {
        [Tooltip("Shown in the prompt: \"Press E to <verb>\" (e.g. eat, pick up, open).")]
        [SerializeField] protected string verb = "use";

        /// <summary>Word shown in the prompt.</summary>
        public virtual string Verb => verb;

        /// <summary>How long the player must HOLD E to trigger this interaction, in seconds.
        /// 0 (the default) = instant on press, exactly like before. Override with a positive
        /// value (see <c>ResourceNode</c>) and PlayerInteractor shows a filling circle at the
        /// centre of the screen while the key is held, firing Interact() only when it completes.</summary>
        public virtual float HoldSeconds => 0f;

        /// <summary>Label shown under the hold-progress circle while channeling
        /// (e.g. "Chopping Tree, Hold E"). Only used when <see cref="HoldSeconds"/> is > 0.</summary>
        public virtual string HoldPrompt => $"Hold E to {Verb}";

        /// <summary>Whether this can be used right now (e.g. a bush with no berries returns false).</summary>
        public virtual bool CanInteract(GameObject interactor) => true;

        /// <summary>Do the thing. <paramref name="interactor"/> is the player GameObject.</summary>
        public abstract void Interact(GameObject interactor);
    }
}

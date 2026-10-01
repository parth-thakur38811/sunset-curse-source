using UnityEngine;
using SunsetCurse.UI;

namespace SunsetCurse.World
{
    /// <summary>
    /// A single diary page lying in the forest. Press E to open a popup showing its lore text.
    /// Spawned by <see cref="DiaryPageController"/> — one per day during the week.
    ///
    /// SETUP: drop on a small prefab (torn-paper model / book / scroll). Needs a Collider.
    /// </summary>
    public class DiaryPage : Interactable
    {
        [Tooltip("Which day this page belongs to (1..7). Used as the popup title and to pick the " +
                 "default lore text from DiaryPageController. Leave content empty to use the controller's text.")]
        [SerializeField] private int pageNumber = 1;

        [Tooltip("Override text. If empty, DiaryPageController's matching entry is used instead.")]
        [TextArea(3, 10)]
        [SerializeField] private string contentOverride = "";

        public override string Verb => "read page";

        public override void Interact(GameObject interactor)
        {
            if (DiaryPageController.Instance != null)
                DiaryPageController.Instance.ShowPage(pageNumber, contentOverride);
            else
                ScreenMessage.Show("(No DiaryPageController in scene)", 2f);
            // Do NOT destroy — other players should still be able to read it.
        }

        // Configure at runtime from the controller when it spawns a new page.
        public void Configure(int day) { pageNumber = day; }
    }
}

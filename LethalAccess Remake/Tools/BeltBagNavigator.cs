using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Keyboard driving and labelling for the belt-bag inventory.
    ///
    /// The bag's fifteen slots carry a SPRITE and nothing else: inventorySlotIcons[i].sprite is
    /// the whole of what a sighted player sees, and there is no text child on a slot at all. The
    /// name only exists as objectsInBag[i].itemProperties.itemName, so every label here comes from
    /// a live provider rather than from anything scraped off the object.
    ///
    /// Confirming a slot goes through BeltBagInventoryUI.RemoveItemFromUI, which is public and is
    /// exactly where the mouse path terminates (OnClickRelease calls it when the release raycast
    /// misses the panel). Taking that route means the netcode is identical to a mouse take and no
    /// device gate has to be patched.
    /// </summary>
    public class BeltBagNavigator : MonoBehaviour
    {
        private static BeltBagNavigator instance;

        public static BeltBagNavigator Instance
        {
            get { return instance; }
        }

        // Instance registrations are keyed on GameObject instance id, so every one of the fifteen
        // slots describes ITSELF even if two of them share a name and therefore a hierarchy path.
        private const string ScreenId = "BeltBag";

        private BeltBagInventoryUI ui;
        private readonly List<GameObject> slotObjects = new List<GameObject>();
        private GameObject exitButtonObject;
        private bool built;

        /// <summary>
        /// Time.frameCount of the last confirm keypress this component serviced, or -1.
        ///
        /// There are two entry points for one keypress: this component's Update, and
        /// BeltBagAccessPatch's ClickInventorySlot prefix (reached when the EventSystem dispatches
        /// Submit to the slot's Button). Unity does not define whether the EventSystem's Update
        /// runs before or after this one, so NEITHER may assume it goes first. Both read the same
        /// frame-scoped device state to decide that a confirm happened, and both consult this
        /// field to decide whether the take has already been done, so exactly one take happens
        /// per keypress in either ordering.
        /// </summary>
        private int lastTakeFrame = -1;

        public static void Ensure()
        {
            if (instance != null)
            {
                return;
            }

            GameObject host = LACore.Instance != null ? LACore.Instance.gameObject : null;
            if (host == null)
            {
                Debug.LogError("[LethalAccess] BeltBagNavigator could not be created: LACore.Instance is null, " +
                               "so there is no component host. The belt bag will not respond to the keyboard.");
                return;
            }

            instance = host.GetComponent<BeltBagNavigator>();
            if (instance == null)
            {
                instance = host.AddComponent<BeltBagNavigator>();
            }
        }

        #region Screen construction

        /// <summary>
        /// Label every slot, find the exit button and wire a vertical chain. Called from the
        /// FillSlots postfix; the heavy work runs once per BeltBagInventoryUI instance because the
        /// providers are live and FillSlots also runs after every take.
        /// </summary>
        public void BuildScreen(BeltBagInventoryUI inventoryUI)
        {
            if (inventoryUI == null)
            {
                Debug.LogError("[LethalAccess] BeltBagNavigator.BuildScreen was given no BeltBagInventoryUI.");
                return;
            }

            if (built && ui == inventoryUI)
            {
                return;
            }

            ui = inventoryUI;
            built = true;
            slotObjects.Clear();
            exitButtonObject = null;

            if (ui.inventorySlots == null || ui.inventorySlots.Length == 0)
            {
                Debug.LogError("[LethalAccess] Belt bag: inventorySlots is empty, so there is nothing to label " +
                               "or navigate.");
                return;
            }

            UIAccessibilityManager manager = UIAccessibilityManager.Instance;
            if (manager == null)
            {
                Debug.LogError("[LethalAccess] Belt bag: UIAccessibilityManager.Instance is null, so no slot can " +
                               "be described. Slots would announce as bare object names and the bag's contents " +
                               "would never be read.");
            }
            else
            {
                // Reaching here means a DIFFERENT BeltBagInventoryUI is being built, so the
                // previous one's slot GameObjects are gone. Unity reuses instance ids after a
                // destroy, so leaving those registrations behind would eventually hand a dead
                // slot's description to an unrelated new object.
                manager.ClearScreen(ScreenId);
            }

            // Resolved before the slots are registered because it is the last entry of the SAME
            // run: a slot announces "3 of 16", and the 16 counts the exit button.
            exitButtonObject = FindExitButton();

            int total = exitButtonObject != null ? 1 : 0;
            for (int i = 0; i < ui.inventorySlots.Length; i++)
            {
                if (ui.inventorySlots[i] != null)
                {
                    total++;
                }
            }

            List<Selectable> chain = new List<Selectable>(ui.inventorySlots.Length + 1);

            // The announced position, which counts only entries that exist. Deliberately NOT the
            // loop variable: that one is the GAME's slot number, which indexes objectsInBag and
            // would overstate the position if a slot were missing.
            int position = 0;

            for (int i = 0; i < ui.inventorySlots.Length; i++)
            {
                GameObject slot = ui.inventorySlots[i];

                // Added even when null so that slotObjects stays index-parallel with
                // inventorySlots. RemoveItemFromUI and objectsInBag are both addressed by the
                // GAME's slot number, so a compacted list here would take the wrong item.
                slotObjects.Add(slot);

                if (slot == null)
                {
                    Debug.LogError("[LethalAccess] Belt bag: inventorySlots[" + i + "] is null; that slot cannot " +
                                   "be focused or announced.");
                    continue;
                }

                Selectable selectable = slot.GetComponent<Selectable>();
                if (selectable == null)
                {
                    // UNVERIFIED: the slot component types are inferred, not
                    // confirmed: the bag cannot be opened from the main menu, so nothing here has
                    // been observed live. If a slot turns out not to be a Button, injecting an
                    // inert Selectable at least keeps it reachable.
                    Debug.Log("[LethalAccess] Belt bag slot '" + slot.name + "' has no Selectable; injecting one " +
                              "so it can be focused.");
                    selectable = FocusInjection.MakeFocusable(slot);
                }
                chain.Add(selectable);

                position++;
                RegisterSlot(manager, slot, i, position, total);
            }

            if (exitButtonObject != null)
            {
                Selectable exitSelectable = exitButtonObject.GetComponent<Selectable>();
                if (exitSelectable == null)
                {
                    exitSelectable = FocusInjection.MakeFocusable(exitButtonObject);
                }
                chain.Add(exitSelectable);

                position++;
                if (manager != null)
                {
                    // Registered by INSTANCE like the slots, not by path: it is the last entry of
                    // the same run, and its "n of m" has to come from the same source as theirs.
                    manager.RegisterObject(exitButtonObject, () => "Close bag", "button",
                        ScreenId, position, total);
                }
            }

            // No wrap: the top and bottom of a list are dead ends, which is what arrow keys are
            // expected to do.
            NavigationEnforcer.BuildChain(chain, false);
        }

        /// <summary>
        /// Describe one slot by INSTANCE.
        ///
        /// Registration used to be keyed on Utilities.GetGameObjectPath, which concatenates
        /// GameObject NAMES only, and Unity permits duplicate sibling names. Two slots sharing a
        /// name shared one dictionary entry, so both announced the last-registered slot's
        /// contents: a blind user would be told the wrong item is in the slot they are on.
        /// RegisterObject is keyed on the instance id, which cannot collide.
        /// </summary>
        /// <param name="gameSlot">
        /// The GAME's slot number: the index into inventorySlots and objectsInBag. It is a
        /// parameter and therefore a fresh local per call, which is what makes the closure below
        /// correct; capturing the caller's `for` variable directly would give all fifteen
        /// providers the loop's final value.
        /// </param>
        /// <param name="position">1-based position within the run, which is what is announced.</param>
        private void RegisterSlot(UIAccessibilityManager manager, GameObject slot, int gameSlot,
                                  int position, int total)
        {
            if (manager == null)
            {
                // BuildScreen has already reported this once; one line per bag, not one per slot.
                return;
            }

            manager.RegisterObject(slot, () => DescribeSlot(gameSlot), "button", ScreenId, position, total);
        }

        private string DescribeSlot(int index)
        {
            if (ui == null || ui.currentBeltBag == null || ui.currentBeltBag.objectsInBag == null)
            {
                return "empty slot";
            }

            List<GrabbableObject> contents = ui.currentBeltBag.objectsInBag;
            if (index >= contents.Count || contents[index] == null || contents[index].itemProperties == null)
            {
                return "empty slot";
            }

            GrabbableObject item = contents[index];
            string name = item.itemProperties.itemName;
            if (string.IsNullOrEmpty(name))
            {
                name = item.gameObject.name;
            }

            // The bag only accepts items with isScrap == false (BeltBagItem.ItemInteractLeftRight
            // rejects scrap outright), and those have a scrapValue of zero, so announcing
            // "worth $0" on every tool would be noise. The value is still read when there is one:
            // an item can be given a value by other means and the user should hear it.
            if (item.scrapValue > 0)
            {
                return name + ", worth $" + item.scrapValue;
            }
            return name;
        }

        /// <summary>
        /// Locate the exit button by what it DOES, not by where it sits.
        ///
        /// ClickExitButton is a public no-argument method with no C# call site, so it is wired in
        /// the inspector, which means the button can be identified from its persistent onClick
        /// listeners. Deliberately no hierarchy path: a guessed one silently strands the user in
        /// the bag with no way out.
        /// </summary>
        private GameObject FindExitButton()
        {
            Transform root = ResolveMenuRoot();
            if (root == null)
            {
                Debug.LogError("[LethalAccess] Belt bag: could not resolve the special-menu root, so the exit " +
                               "button was not found. Arrow keys will cover the slots only.");
                return null;
            }

            Button[] buttons = root.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button b = buttons[i];
                if (b == null || b.onClick == null)
                {
                    continue;
                }

                int count = b.onClick.GetPersistentEventCount();
                for (int j = 0; j < count; j++)
                {
                    if (b.onClick.GetPersistentMethodName(j) != "ClickExitButton")
                    {
                        continue;
                    }

                    UnityEngine.Object target = b.onClick.GetPersistentTarget(j);
                    if (target == null || target is BeltBagInventoryUI)
                    {
                        return b.gameObject;
                    }
                }
            }

            Debug.LogError("[LethalAccess] Belt bag: no Button under the special-menu root has a persistent " +
                           "onClick call to ClickExitButton, so the bag has no announced way to close. " +
                           "Dump the panel's component list at runtime and correct this.");
            return null;
        }

        private Transform ResolveMenuRoot()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.specialHUDMenus == null)
            {
                return null;
            }

            int index = (int)SpecialHUDMenu.BeltBagInventory - 1;
            if (index < 0 || index >= hud.specialHUDMenus.Length)
            {
                return null;
            }

            SpecialHUDMenuObject menu = hud.specialHUDMenus[index];
            if (menu == null || menu.rootObject == null)
            {
                return null;
            }
            return menu.rootObject.transform;
        }

        #endregion

        #region Opening

        /// <summary>
        /// Called from the OpenSpecialMenu postfix, which is the first moment the panel is active
        /// and therefore the first moment focus can legally be seeded onto it.
        /// </summary>
        public void NoteMenuOpened(GameObject defaultSelected)
        {
            if (ui == null)
            {
                Debug.LogError("[LethalAccess] Belt bag opened before FillSlots registered any slots; nothing " +
                               "can be announced or focused.");
                return;
            }

            int count = ui.currentBeltBag != null && ui.currentBeltBag.objectsInBag != null
                ? ui.currentBeltBag.objectsInBag.Count
                : 0;
            int capacity = ui.inventorySlots != null ? ui.inventorySlots.Length : 0;

            AnnouncementPolicy.Speak("Bag open, " + count + " of " + capacity + " items.");

            GameObject target = defaultSelected;
            if (target == null)
            {
                // OpenSpecialMenu only sets the selection when localPlayerUsingController is true,
                // so on mouse and keyboard the EventSystem selection is whatever gameplay left
                // behind, usually nothing. Falling back to the first slot is what makes the
                // screen exist at all for a keyboard user.
                for (int i = 0; i < slotObjects.Count; i++)
                {
                    if (slotObjects[i] != null)
                    {
                        target = slotObjects[i];
                        break;
                    }
                }
            }

            if (target == null)
            {
                Debug.LogError("[LethalAccess] Belt bag: no default selection and no slots, so the keyboard has " +
                               "nothing to focus.");
                return;
            }

            FocusManager.Seed(target);
        }

        #endregion

        #region Confirm key

        private void Update()
        {
            if (ui == null || !ui.displaying)
            {
                return;
            }

            // On a controller the game's own path already works: Submit fires the slot Button,
            // ClickInventorySlotController passes the localPlayerUsingController gate and calls
            // RemoveItemFromUI. Acting here as well would take two items per press.
            if (StartOfRound.Instance != null && StartOfRound.Instance.localPlayerUsingController)
            {
                return;
            }

            if (!ConfirmKeyPressedThisFrame())
            {
                return;
            }

            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            if (selected == null)
            {
                return;
            }

            if (exitButtonObject != null && selected == exitButtonObject)
            {
                // Calling it directly is safe even if the Button's own onClick also fires:
                // ClickExitButton re-checks inSpecialMenu before doing anything, and its other two
                // statements are idempotent assignments.
                ui.ClickExitButton();
                return;
            }

            int slot = slotObjects.IndexOf(selected);
            if (slot < 0)
            {
                return;
            }

            if (lastTakeFrame == Time.frameCount)
            {
                // The EventSystem's Update ran before this one and the ClickInventorySlot prefix
                // already serviced this keypress. Nothing left to do.
                return;
            }

            TakeSlot(slot);
        }

        /// <summary>
        /// Called from BeltBagAccessPatch's ClickInventorySlot prefix, i.e. when the EventSystem
        /// has dispatched Submit to a slot's Button.
        ///
        /// Returns true when this component owns the click and the vanilla mouse-drag handler must
        /// be suppressed: that handler sets grabbingItemSlot and waits for an ActivateItem
        /// raycast the keyboard can never produce, which strands the item on the cursor.
        ///
        /// Returns false for a real mouse click, which is left to run vanilla: the drag-and-drop
        /// it starts is the only form of the gesture a mouse can finish.
        /// </summary>
        public bool ClaimKeyboardSlotClick(BeltBagInventoryUI clickedUi, int slot)
        {
            if (ServicedConfirmRecently())
            {
                // Already taken, by this component's own Update earlier in the same frame, or by
                // an earlier call here. Suppress without acting: a second take would remove a
                // SECOND item, and after the first take the list has compacted, so it would not
                // even be the item the user was standing on.
                return true;
            }

            if (!ConfirmKeyPressedThisFrame())
            {
                return false;
            }

            // Mirrors the Update gate: on a controller the game's own ClickInventorySlotController
            // path works, and vanilla ClickInventorySlot no-ops for it anyway.
            if (StartOfRound.Instance != null && StartOfRound.Instance.localPlayerUsingController)
            {
                return false;
            }

            if (ui == null || ui != clickedUi)
            {
                Debug.LogError("[LethalAccess] Belt bag: a slot was confirmed on a BeltBagInventoryUI the " +
                               "navigator has not built, so the take was left to the game and the item will be " +
                               "stranded on the mouse cursor. BuildScreen never ran for this instance.");
                return false;
            }

            TakeSlot(slot);
            return true;
        }

        /// <summary>
        /// Did the confirm key go down on THIS frame?
        ///
        /// Read straight off the device rather than from a flag some other component sets, so both
        /// entry points get the same answer no matter which of them Unity runs first.
        /// wasPressedThisFrame is frame-scoped state on the Keyboard device: it does not matter
        /// who asks, or when within the frame.
        /// </summary>
        private static bool ConfirmKeyPressedThisFrame()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return false;
            }
            return kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
        }

        /// <summary>
        /// Has a confirm keypress already been serviced close enough to now that this dispatch is
        /// part of the same one?
        ///
        /// Same frame is the expected case and is what the two Update orderings produce. The extra
        /// frame of tolerance covers the possibility that the EventSystem's Submit dispatch lands
        /// one frame behind the device state the take was driven from; suppressing a mouse click
        /// that happens to land in that window is a far smaller harm than removing a second item.
        ///
        /// Checked in a hosted ship 2026-10-14 with a real Enter through the Input System (keyboard
        /// device enabled and fed in-process, window unfocused), three presses with three items in
        /// the bag: each press took exactly one item, recorded lastTakeFrame as the frame after the
        /// press was queued, and left grabbingItemSlot at -1, so no vanilla mouse-drag was started.
        /// A late Submit was not seen; the one-frame tolerance stays as cheap insurance.
        /// </summary>
        private bool ServicedConfirmRecently()
        {
            return lastTakeFrame >= 0 && Time.frameCount - lastTakeFrame <= 1;
        }

        /// <summary>
        /// Perform the one take this keypress is allowed. The frame is recorded BEFORE anything
        /// else, so the FillSlots that RemoveItemFromUI triggers cannot re-enter and take again.
        /// </summary>
        private void TakeSlot(int slot)
        {
            lastTakeFrame = Time.frameCount;

            if (!SlotIsOccupied(slot))
            {
                // RemoveItemFromUI's else-branch would re-enable the icon on an empty slot, which
                // leaves a stale sprite on screen for sighted players. Nothing to take, so say so.
                AnnouncementPolicy.Speak("Empty slot.");
                return;
            }

            ui.RemoveItemFromUI(slot);
        }

        private bool SlotIsOccupied(int slot)
        {
            if (ui == null || ui.currentBeltBag == null || ui.currentBeltBag.objectsInBag == null)
            {
                return false;
            }
            List<GrabbableObject> contents = ui.currentBeltBag.objectsInBag;
            return slot >= 0 && slot < contents.Count && contents[slot] != null;
        }

        #endregion
    }
}

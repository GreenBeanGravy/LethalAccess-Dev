using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using System;

namespace LethalAccess.Patches
{
    /// <summary>
    /// Accessible invite friends menu that replaces the Steam overlay
    /// </summary>
    [HarmonyPatch(typeof(QuickMenuManager))]
    public static class AccessibleInviteFriendsPatch
    {
        private static AccessibleFriendsMenu friendsMenu;

        [HarmonyPatch(nameof(QuickMenuManager.InviteFriendsButton))]
        [HarmonyPrefix]
        public static bool InviteFriendsButtonPrefix()
        {
            try
            {
                // Check if game has started (same check as original)
                if (!GameNetworkManager.Instance.gameHasStarted)
                {
                    // Instead of opening Steam overlay, open our accessible menu
                    if (friendsMenu == null)
                    {
                        CreateAccessibleFriendsMenu();
                    }

                    if (friendsMenu != null)
                    {
                        friendsMenu.OpenMenu();
                    }
                    else
                    {
                        SpeechUtils.SpeakText("Unable to open friends menu");
                    }
                }
                else
                {
                    SpeechUtils.SpeakText("Cannot invite friends after game has started");
                }

                // Return false to prevent original method from executing
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in accessible invite friends: {ex.Message}");
                SpeechUtils.SpeakText("Error opening friends menu");
                return false;
            }
        }

        private static void CreateAccessibleFriendsMenu()
        {
            try
            {
                GameObject menuObject = new GameObject("AccessibleFriendsMenu");
                friendsMenu = menuObject.AddComponent<AccessibleFriendsMenu>();
                friendsMenu.Initialize();
                Debug.Log("Created accessible friends menu");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error creating accessible friends menu: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Accessible friends menu using AccessibleMenuSystem backend
    /// </summary>
    public class AccessibleFriendsMenu : AccessibleMenuSystem
    {
        private List<FriendInfo> friends = new List<FriendInfo>();
        protected override string MenuTitle => "Friends menu";

        public struct FriendInfo
        {
            public string Name;
            public SteamId SteamId;
            public bool IsOnline;
            public bool IsPlayingSameGame;
        }

        public void Initialize()
        {
            LoadFriends();
            Debug.Log("AccessibleFriendsMenu initialized with AccessibleMenuSystem");
        }

        protected override void BuildMenuEntries()
        {
            ClearMenuEntries();

            // Add refresh option at the top
            AddFriendEntry("Refresh Friends List (F5)", null, RefreshFriends, () => true);

            // Add friends
            foreach (var friend in friends)
            {
                var friendCopy = friend; // Local copy for closure
                AddFriendEntry(
                    GetFriendDisplayText(friend),
                    friendCopy,
                    () => InviteFriend(friendCopy),
                    () => CanInviteFriend(friendCopy)
                );
            }

            if (friends.Count == 0)
            {
                AddFriendEntry("No friends found", null, null, () => false);
            }

            Debug.Log($"Built friends menu with {menuEntries.Count} entries");
        }

        protected override void OnMenuOpened()
        {
            base.OnMenuOpened();

            // Additional instructions after the main announcement
            StartCoroutine(DelayedInstructions());
        }

        private System.Collections.IEnumerator DelayedInstructions()
        {
            yield return new WaitForSeconds(0.8f); // Wait for first item announcement
            if (isMenuOpen)
            {
                SpeechUtils.SpeakText("Use up and down arrows to navigate, Enter to invite, F5 to refresh, Escape to close.");

                if (friends.Count == 0)
                {
                    yield return new WaitForSeconds(0.5f);
                    SpeechUtils.SpeakText("No friends found. Press F5 to refresh.");
                }
            }
        }

        protected override void Update()
        {
            base.Update();

            if (isMenuOpen)
            {
                // F5 to refresh
                if (Keyboard.current.f5Key.wasPressedThisFrame)
                {
                    RefreshFriends();
                }

                // Quick invite keys (1-9 for first 9 friends)
                for (int i = 0; i < Mathf.Min(9, friends.Count); i++)
                {
                    Key key = (Key)((int)Key.Digit1 + i);
                    if (Keyboard.current[key].wasPressedThisFrame)
                    {
                        if (i < friends.Count && CanInviteFriend(friends[i]))
                        {
                            InviteFriend(friends[i]);
                        }
                        break;
                    }
                }
            }
        }

        private void LoadFriends()
        {
            friends.Clear();

            try
            {
                if (!SteamClient.IsValid)
                {
                    Debug.LogError("Steam client not valid");
                    return;
                }

                // Get friends list from Steam
                var steamFriends = SteamFriends.GetFriends();

                foreach (var friend in steamFriends)
                {
                    try
                    {
                        var friendInfo = new FriendInfo
                        {
                            Name = friend.Name,
                            SteamId = friend.Id,
                            IsOnline = friend.IsOnline,
                            IsPlayingSameGame = friend.IsPlayingThisGame
                        };

                        friends.Add(friendInfo);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error processing friend: {ex.Message}");
                    }
                }

                // Sort friends: online first, then by name
                friends = friends.OrderByDescending(f => f.IsOnline)
                               .ThenBy(f => f.Name)
                               .ToList();

                Debug.Log($"Loaded {friends.Count} friends");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading friends: {ex.Message}");
            }
        }

        private string GetFriendDisplayText(FriendInfo friend)
        {
            string status = "";
            if (friend.IsPlayingSameGame)
            {
                status = "playing Lethal Company";
            }
            else if (friend.IsOnline)
            {
                status = "online";
            }
            else
            {
                status = "offline";
            }

            return $"{friend.Name}, {status}";
        }

        private bool CanInviteFriend(FriendInfo friend)
        {
            return friend.IsOnline && GameNetworkManager.Instance?.currentLobby != null;
        }

        private void InviteFriend(FriendInfo friend)
        {
            try
            {
                // Check if friend is online
                if (!friend.IsOnline)
                {
                    SpeechUtils.SpeakText($"{friend.Name} is offline and cannot be invited");
                    return;
                }

                // Check if we have a lobby
                if (GameNetworkManager.Instance?.currentLobby == null)
                {
                    SpeechUtils.SpeakText("No active lobby to invite to");
                    return;
                }

                // Send invite using Steam API
                var currentLobby = GameNetworkManager.Instance.currentLobby.Value;
                currentLobby.InviteFriend(friend.SteamId);
                SpeechUtils.SpeakText($"Invite sent to {friend.Name}");
                Debug.Log($"Invited {friend.Name} to game");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error sending invite: {ex.Message}");
                SpeechUtils.SpeakText("Error sending invite");
            }
        }

        private void RefreshFriends()
        {
            try
            {
                SpeechUtils.SpeakText("Refreshing friends list...");
                LoadFriends();
                BuildMenuEntries(); // Rebuild the menu with new friends
                currentItemIndex = 0; // Reset to first item

                if (friends.Count > 0)
                {
                    SpeechUtils.SpeakText($"Friends list refreshed. {friends.Count} friends found.");
                    RefreshCurrentItem(); // Announce the current item
                }
                else
                {
                    SpeechUtils.SpeakText("No friends found");
                    RefreshCurrentItem();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error refreshing friends: {ex.Message}");
                SpeechUtils.SpeakText("Error refreshing friends list");
            }
        }

        // Helper method to add friend entries
        private void AddFriendEntry(string text, FriendInfo? friend, Action onSelect, Func<bool> canInteract)
        {
            menuEntries.Add(new FriendsMenuEntry(text, onSelect, canInteract));
        }

        // Custom menu entry for friends with proper interaction handling
        private class FriendsMenuEntry : IMenuEntry
        {
            private string text;
            private Action onSelectAction;
            private Func<bool> canInteractFunc;

            public FriendsMenuEntry(string text, Action onSelect, Func<bool> canInteract)
            {
                this.text = text;
                this.onSelectAction = onSelect;
                this.canInteractFunc = canInteract;
            }

            public string GetDisplayText() => text;
            public bool CanInteract() => canInteractFunc();
            public void OnSelect() => onSelectAction?.Invoke();
            public void OnModify(int direction) { } // No modification for friends
            public bool HasModification() => false;
        }
    }
}
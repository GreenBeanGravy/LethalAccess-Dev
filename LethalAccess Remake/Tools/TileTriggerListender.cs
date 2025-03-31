using System;
using DunGen;
using LethalAccess;
using UnityEngine;

namespace Green.LethalAccessPlugin
{
    public class TileTriggerListener : MonoBehaviour
    {
        private TileTracker tracker;
        private Tile lastTile;

        public void SetTracker(TileTracker tileTracker)
        {
            tracker = tileTracker;
        }

        private void OnTriggerEnter(Collider other)
        {
            try
            {
                if (tracker == null)
                    return;

                // Check if this is a tile trigger
                Tile tile = other.GetComponentInParent<Tile>();
                if (tile != null && tile != lastTile)
                {
                    lastTile = tile;
                    tracker.OnPlayerEnteredTile(tile);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in TileTriggerListener.OnTriggerEnter: {ex.Message}");
            }
        }

        private void OnTriggerExit(Collider other)
        {
            try
            {
                if (tracker == null)
                    return;

                // Check if this is a tile trigger
                Tile tile = other.GetComponentInParent<Tile>();
                if (tile != null)
                {
                    tracker.OnPlayerExitedTile(tile);
                    if (tile == lastTile)
                    {
                        lastTile = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in TileTriggerListener.OnTriggerExit: {ex.Message}");
            }
        }
    }
}
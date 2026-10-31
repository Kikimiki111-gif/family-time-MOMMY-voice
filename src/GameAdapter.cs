using System;
using System.Collections.Generic;
using GAT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using family;

namespace FamilyTimeVoice
{
    // Bind only to the verified singleton from this game's Assembly-CSharp.
    // Children are GPU GAT instances and are never searched or considered here.
    public sealed class GameAdapter : IDisposable
    {
        // BoolLimitGroup accepts distinct enum values; this tag belongs only to this mod.
        private const PlayerController.ReasonToShowCursor VoicePanelCursor =
            (PlayerController.ReasonToShowCursor)0x465456;
        private WolfMama wife;
        private PlayerController player;
        private Camera playerCamera;
        private WolfMamaBedBehavior bed;
        private WolfMamaChairBehavior chair;
        private string context = "{\"kind\":\"verified_game_facts\",\"room\":null}";
        private string lastScene, lastVerifiedEvent;
        private bool lastPlaying;
        private float nextRefresh;
        private bool holding;
        private bool disposed;
        private readonly List<InputAction> restoreActions = new List<InputAction>();
        private readonly List<InputActionAsset> heldAssets = new List<InputActionAsset>();

        public GameAdapter()
        {
            SceneManager.sceneLoaded += SceneLoaded;
        }

        public Transform Target => wife != null && wife.isActiveAndEnabled ? wife.transform : null;
        // The PlayerController component transform is not the simulated player's location.
        // The main camera is updated to CachedInstancePosition + head offset by the game.
        public Transform Player => playerCamera != null ? playerCamera.transform : null;
        public string SaveId => World.worldSerializer != null ? World.worldSerializer.LastSaveName ?? "unsaved" : "unsaved";
        public bool Ready => Target != null && player != null && player.isActiveAndEnabled &&
            player.IsPossessing && !player.IsKnockedOut && GameSession.Instance != null &&
            GameSession.Instance.IsPlaying && World.worldSerializer != null && !World.worldSerializer.IsBusy;
        public float Distance => Ready ? Vector3.Distance(player.CachedInstancePosition, Target.position) : float.PositiveInfinity;
        public string Context => context;
        public long ContextRevision { get; private set; }

        // Call from Unity's main thread. Reacquisition is constant-time, at most once per second.
        public void Refresh()
        {
            if (disposed || Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + 1f;
            PlayerController nextPlayer = World.playerController;
            if (player != nextPlayer)
            {
                RestoreInput();
                player = nextPlayer;
            }
            WolfMama nextWife = WolfMama.ActiveInstance;
            if (wife != nextWife)
            {
                wife = nextWife;
                bed = wife == null ? null : wife.GetComponent<WolfMamaBedBehavior>();
                chair = wife == null ? null : wife.GetComponent<WolfMamaChairBehavior>();
            }
            playerCamera = Camera.main;
            RefreshContext();
            if (holding) CaptureAndDisableInput();
        }

        private void RefreshContext()
        {
            string scene = SceneManager.GetActiveScene().name;
            bool playing = GameSession.Instance != null && GameSession.Instance.IsPlaying;
            if (lastScene != null && scene != lastScene) lastVerifiedEvent = "scene_changed";
            else if (playing && !lastPlaying) lastVerifiedEvent = "entered_play";
            else if (!playing && lastPlaying) lastVerifiedEvent = "left_play";
            lastScene = scene; lastPlaying = playing;
            string activity = null;
            if (Ready)
            {
                if (bed != null && bed.IsLyingInBed) activity = "lying_in_bed";
                else if (chair != null && chair.IsStandingUp) activity = "standing_up";
                else if (chair != null && chair.IsSeated) activity = "seated";
            }
            string next = new JObject
            {
                ["kind"] = "verified_game_facts",
                ["scene"] = string.IsNullOrEmpty(scene) ? JValue.CreateNull() : new JValue(scene),
                ["room"] = JValue.CreateNull(),
                ["wife_identity"] = Target == null ? JValue.CreateNull() : new JValue("Wolf Mama"),
                ["wife_activity"] = activity == null ? JValue.CreateNull() : new JValue(activity),
                ["play_state"] = GameSession.Instance == null ? "unavailable" : GameSession.Instance.State.ToString(),
                ["last_verified_event"] = lastVerifiedEvent == null ? JValue.CreateNull() : new JValue(lastVerifiedEvent)
            }.ToString(Formatting.None);
            if (!string.Equals(next, context, StringComparison.Ordinal)) { context = next; ContextRevision++; }
        }

        public void HoldInput(bool hold)
        {
            if (disposed || hold == holding) return;
            holding = hold;
            if (hold)
            {
                nextRefresh = 0f;
                Refresh();
                player?.ResetTutorialGestures();
                PlayerController.showCursor.Add(VoicePanelCursor);
                CaptureAndDisableInput();
            }
            else
            {
                RestoreInput();
                PlayerController.showCursor.Remove(VoicePanelCursor);
            }
        }

        private void CaptureAndDisableInput()
        {
            // Disable actions, never the controller: its OnDisable unpossesses the player.
            if (player != null && player.isActiveAndEnabled) DisableAsset(player.InputActionsAsset);
            DisableAsset(World.commandInputActions);
        }

        private void DisableAsset(InputActionAsset asset)
        {
            if (asset == null) return;
            if (!heldAssets.Contains(asset)) heldAssets.Add(asset);
            foreach (InputAction action in asset)
                if (action.enabled && !restoreActions.Contains(action)) restoreActions.Add(action);
            // InputSystem sends cancellation callbacks, clearing held movement/sprint/crouch.
            asset.Disable();
        }

        private void RestoreInput()
        {
            foreach (InputAction action in restoreActions)
            {
                // A scene unload can dispose the previous controller's action asset.
                try
                {
                    if (action.actionMap != null && action.actionMap.asset != null &&
                        heldAssets.Contains(action.actionMap.asset)) action.Enable();
                }
                catch (ObjectDisposedException) { }
            }
            restoreActions.Clear();
            heldAssets.Clear();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) => nextRefresh = 0f;

        public void Dispose()
        {
            if (disposed) return;
            HoldInput(false);
            SceneManager.sceneLoaded -= SceneLoaded;
            wife = null;
            bed = null;
            chair = null;
            player = null;
            playerCamera = null;
            disposed = true;
        }
    }
}

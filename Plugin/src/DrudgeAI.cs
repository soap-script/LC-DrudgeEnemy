using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.SocialPlatforms;
using UnityEngine.UIElements;
using LC_Drudge.Configuration;

namespace LC_Drudge {


    /**
     * TODO:
     * - Better model
     * - Better animations
     *   - Turning/skidding around a corner
     *     - Particles for metal clashing metal?
     *   - Chase animation
     *   - Angrily looking at player animation
     *   - Arm looking at player/item
     *   - Walk cycle
     *   - Idle animation
     *   - Stun animation
     * - Slow down kill sequence to 5 seconds
     * - Better sounds
     * - Better logs for debugging
     * - Network testing
     * - Item-specific logic
     *   - Shovel
     *   - Walkie Talkie
     */
    class LC_Drudge : EnemyAI
    {
        internal static PluginConfig DrudgeConfig { get; private set; } = null;

        public Transform turnCompass;
        public Transform attackArea;
        public Transform grabTarget;
        public Transform playerTarget;
        enum State {
            SearchingForPlayer,
            FollowPlayer,
            ChasingPlayer,
            AngrilyLookingAtPlayer,
            KillingPlayer,
            OpeningDoor,
        }

        public AudioClip footstepSFX;
        public AudioClip handCloseSFX;
        public AudioClip crushingSFX;
        public Light drudgeLight;
        public Light drudgeLightGlow;
        public InteractTrigger drudgeTrigger;

        private float playerEmoteTime;
        private bool hasActedFromEmote;
        private Vector3 previousPosition;
        private float velX;
        private float velZ;
        private bool stunned = false;
        private float angerLevel = 0f;
        private float angerLevelAccelerator = 1f;
        private ulong? previousTargetPlayerId;
        private float timeSinceTargetedPreviousPlayer = 0f;
        private ulong? currentTargetPlayerId;

        private DoorLock closestDoor;
        private Coroutine killingCoroutine;
        private Coroutine walkieTalkieCoroutine;
        private GrabbableObject heldItem;

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            LogIfDebugBuild(text);
        }

        public override void Start() {
            base.Start();
            LogIfDebugBuild("Drudge Enemy Spawned");
            creatureAnimator.SetTrigger("startIdle");
            playerEmoteTime = 0;
            hasActedFromEmote = false;
            currentBehaviourStateIndex = (int)State.SearchingForPlayer;
            drudgeTrigger.onInteract.AddListener(GrabScrapFromPlayer);
            drudgeLight.enabled = true;

            StartSearch(transform.position);
        }

        public override void Update() {
            base.Update();

            var state = currentBehaviourStateIndex;

            if (stunNormalizedTimer > 0f)
            {
                creatureAnimator.SetBool("stunned", true);
                StunnedState();
                return;
            } else if (stunned)
            {
                creatureAnimator.SetBool("stunned", false);
                stunned = false;
            }

            if(GetCurrentTargetPlayer() != null && (state == (int)State.FollowPlayer || state == (int)State.AngrilyLookingAtPlayer)){
                turnCompass.LookAt(GetCurrentTargetPlayer().gameplayCamera.transform.position);
                transform.rotation = Quaternion.Lerp(transform.rotation, Quaternion.Euler(new Vector3(0f, turnCompass.eulerAngles.y, 0f)), 10f * Time.deltaTime);
            }

            CalculateLocalAnimationVelocityAndDirection();

            UpdateLightSource();
            UpdateInteractTrigger();
            UpdateSpecialAnimation();
            UpdateAngerLevel();
            UpdateMovingTowardsTargetPlayer();
            UpdatePreviousTargetPlayer();
            float carryWeightMultiplier = 1f;
            if (heldItem != null)
            {
                carryWeightMultiplier = 1f - Mathf.Clamp(Mathf.InverseLerp(0f, 1f, heldItem.itemProperties.weight - 1f), 0f, 0.4f);
            }

            switch(currentBehaviourStateIndex) {
                case (int)State.SearchingForPlayer:
                    agent.speed = 3f * carryWeightMultiplier;
                    break;

                case (int)State.FollowPlayer:
                    agent.speed = 5f * carryWeightMultiplier;
                    break;

                case (int)State.ChasingPlayer:
                    agent.speed = 8f;
                    break;

                case (int)State.AngrilyLookingAtPlayer:
                    agent.speed = 0f;

                    LogIfDebugBuild($"Anger level -- ${angerLevel}");
                    angerLevel += Time.deltaTime * angerLevelAccelerator;
                    if (angerLevel >= 1)
                    {
                        GetCurrentTargetPlayer().JumpToFearLevel(1f, true);
                        SwitchToBehaviourState((int)State.ChasingPlayer);
                        return;
                    }
                    break;

                case (int)State.KillingPlayer:
                    agent.speed = 0f;
                    if (inSpecialAnimationWithPlayer == null)
                    {
                        SwitchToBehaviourState((int)State.ChasingPlayer);
                    }
                    break;

                case (int)State.OpeningDoor:
                    break;
                    
                default:
                    LogIfDebugBuild($"This behavior state isn't being handled by Update - ${currentBehaviourState} ${currentBehaviourStateIndex}");
                    break;
            }
        }

        void CalculateLocalAnimationVelocityAndDirection(float maxSpeed = 2f)
        {
            if (GetCurrentTargetPlayer())
            {
                Vector2 targetDestinationVector2 = new Vector2(GetCurrentTargetPlayer().transform.position.x, GetCurrentTargetPlayer().transform.position.z);
                Vector2 currentPositionVector2 = new Vector2(transform.position.x, transform.position.z);
                Vector2 vectorToTargetDestination = targetDestinationVector2 - currentPositionVector2;
                Vector2 currentForward = new Vector2(transform.forward.x, transform.forward.z);

                float angle = Vector2.SignedAngle(vectorToTargetDestination, currentForward) / 100;
                creatureAnimator.SetFloat("chaseAngle", angle);
            }

            Vector3 agentLocalVelocity = transform.InverseTransformDirection(Vector3.ClampMagnitude(transform.position - previousPosition, 1f) / (Time.deltaTime * 2f));
            velX = Mathf.Lerp(velX, agentLocalVelocity.x, 10f * Time.deltaTime);
            velZ = Mathf.Lerp(velZ, agentLocalVelocity.z, 10f * Time.deltaTime);

            float averageVelocity = (velX + velZ) / 2;

            if (Mathf.Abs(averageVelocity) < 0.1f)
            {
                averageVelocity = 0f;
            }
            
            creatureAnimator.SetFloat("averageVelocity", Mathf.Clamp(averageVelocity, -maxSpeed, maxSpeed));

            previousPosition = transform.position;
        }

        void UpdateSpecialAnimation()
        {
            if (inSpecialAnimationWithPlayer != null)
            {
                Vector3 distanceBetweenPlayerTransformToCamera = inSpecialAnimationWithPlayer.transform.position - inSpecialAnimationWithPlayer.gameplayCamera.transform.position;
                inSpecialAnimationWithPlayer.transform.position = new Vector3(
                    playerTarget.position.x + distanceBetweenPlayerTransformToCamera.x,
                    playerTarget.position.y + distanceBetweenPlayerTransformToCamera.y,
                    playerTarget.position.z + distanceBetweenPlayerTransformToCamera.z
                );
                inSpecialAnimationWithPlayer.transform.rotation = playerTarget.rotation;
            }
        }

        void UpdateAngerLevel()
        {
            creatureAnimator.SetFloat("angerLevel", angerLevel);
            if ((currentBehaviourStateIndex != (int)State.AngrilyLookingAtPlayer && currentBehaviourStateIndex != (int)State.ChasingPlayer) && angerLevel > 0)
            {
                LogIfDebugBuild($"No reason to be angry");
                angerLevel -= Time.deltaTime * angerLevelAccelerator;
            }
        }

        void UpdateInteractTrigger ()
        {
            if (GameNetworkManager.Instance.localPlayerController.currentlyHeldObjectServer != null && currentBehaviourStateIndex == (int)State.FollowPlayer && !stunned)
            {
                if (!Plugin.DrudgeConfig.canCarryTwoHanded.Value && GameNetworkManager.Instance.localPlayerController.currentlyHeldObjectServer.itemProperties.twoHanded)
                {
                    drudgeTrigger.interactable = false;
                    drudgeTrigger.hoverTip = "";
                } else
                {
                    drudgeTrigger.interactable = true;
                    drudgeTrigger.hoverTip = "Hold [e] to give item";
                }
            } else
            {
                drudgeTrigger.interactable = false;
                drudgeTrigger.hoverTip = "";
            }
        }
        void UpdateLightSource()
        {
            drudgeLight.color = Color.Lerp(Color.white, Color.red, angerLevel);
            drudgeLight.spotAngle = Mathf.Lerp(50, 20, angerLevel);
            drudgeLightGlow.color = Color.Lerp(Color.white, Color.red, angerLevel);
            drudgeLight.enabled = heldItem == null;
        }

        void UpdatePreviousTargetPlayer()
        {
            if (currentTargetPlayerId != previousTargetPlayerId)
            {
                if (timeSinceTargetedPreviousPlayer > 5f)
                {
                    LogIfDebugBuild($"Previous player now targettable again. Current Player ${currentTargetPlayerId} -- Previous Player ${previousTargetPlayerId}");
                    previousTargetPlayerId = currentTargetPlayerId;
                    timeSinceTargetedPreviousPlayer = 0f;
                } else
                {
                    timeSinceTargetedPreviousPlayer += Time.deltaTime;
                }
            }
        }

        public override void DoAIInterval() {
            
            base.DoAIInterval();
            if (isEnemyDead || StartOfRound.Instance.allPlayersDead) {
                return;
            };

            switch(currentBehaviourStateIndex) {
                case (int)State.SearchingForPlayer:
                    SearchingForPlayerState();
                    break;

                case (int)State.FollowPlayer:
                    FollowPlayerState();
                    break;

                case (int)State.ChasingPlayer:
                    ChasingPlayerState();
                    break;

                case (int)State.AngrilyLookingAtPlayer:
                    AngrilyLookingAtPlayerState();
                    break;

                case (int)State.KillingPlayer:
                    break;

                case (int)State.OpeningDoor:
                    OpeningDoorState();
                    break;
                    
                default:
                    LogIfDebugBuild($"This behavior state isn't being handled by DoAIInterval - ${currentBehaviourState} ${currentBehaviourStateIndex}");
                    break;
            }
        }

        void SearchingForPlayerState()
        {
            if (FoundClosestPlayerInRange(25f, 3f)) {
                LogIfDebugBuild("Start Target Player");
                StopSearch(currentSearch);

                if (DoesPlayerHaveAnItem(GetCurrentTargetPlayer()) || heldItem != null)
                {
                    SwitchToBehaviourState((int)State.FollowPlayer);
                } else
                {
                    SwitchToBehaviourState((int)State.AngrilyLookingAtPlayer);
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        void SetCurrentTargetPlayerServerRPC(ulong playerId)
        {
            SetCurrentTargetPlayerClientRPC(playerId);
        }

        [ClientRpc]
        void SetCurrentTargetPlayerClientRPC(ulong playerId)
        {
            currentTargetPlayerId = playerId;
            targetPlayer = GetCurrentTargetPlayer();
        }

        PlayerControllerB GetCurrentTargetPlayer()
        {
            if (currentTargetPlayerId != null)
            {
                return StartOfRound.Instance.allPlayerScripts[(int)currentTargetPlayerId];
            }
            return null;
        }

        bool LocalPlayerLookingAtDrudge()
        {
            return GameNetworkManager.Instance.localPlayerController.HasLineOfSightToPosition(transform.position + Vector3.up * 0.5f);
        }

        bool LocalPlayerLookingAtGround()
        {
            return Vector3.Dot(GameNetworkManager.Instance.localPlayerController.gameplayCamera.transform.forward, Vector3.down) > 0.5;
        }

        T LocalPlayerLookingAtObject<T> (int layer)
        {
            PlayerControllerB localPlayer = GameNetworkManager.Instance.localPlayerController;
            RaycastHit raycastHit;
            if (Physics.Raycast(new Ray(localPlayer.gameplayCamera.transform.position, localPlayer.gameplayCamera.transform.forward), out raycastHit, 15f, layer))
            {
                T component = raycastHit.transform.GetComponent<T>();
                return component;
            }
            return default;
        }

        void FollowPlayerState()
        {
            // Keep targetting closest player, unless they are over 20 units away and we can't see them.
            if (!TargetClosestPlayerInAnyCase() || (Vector3.Distance(transform.position, GetCurrentTargetPlayer().transform.position) > 20 && !CheckLineOfSightForPosition(GetCurrentTargetPlayer().transform.position))){
                LogIfDebugBuild("Stop Target Player");
                SwitchToSearchState();
                return;
            }
            if (!DoesPlayerHaveAnItem(GetCurrentTargetPlayer()) && !heldItem)
            {
                LogIfDebugBuild("Target player does not have item. Switching to anger look");
                SwitchToBehaviourState((int)State.AngrilyLookingAtPlayer);
                return;
            }
            CheckLocalPlayerForEmoteActions();
            FollowPlayer();
        }

        void OpeningDoorState()
        {
            if (!closestDoor || !heldItem)
            {
                SwitchToSearchState();
                return;
            }
            if (!closestDoor.isLocked)
            {
                closestDoor = null;
                SwitchToSearchState();
                return;
            }
            float distanceToDoor = Vector3.Distance(closestDoor.transform.position, transform.position);
            if (distanceToDoor < 2f)
            {
                DespawnHeldItemServerRPC();

                closestDoor.UnlockDoorSyncWithServer();
                closestDoor.gameObject.GetComponent<AnimatedObjectTrigger>().TriggerAnimationNonPlayer(false, true, false);
                closestDoor.OpenDoorAsEnemyServerRpc();
                closestDoor = null;
                SwitchToSearchState();
                return;
            }
            Vector3 closerSideOfDoor = closestDoor.transform.position - transform.position;
            SetDestinationToPosition(closestDoor.transform.position - Vector3.Normalize(closerSideOfDoor));
        }

        void StunnedState()
        {
            agent.speed = 0f;
            if (!stunned)
            {
                LogIfDebugBuild("Entering stunned state");
                stunned = true;
                if (killingCoroutine != null)
                {
                    LogIfDebugBuild("Stopping killing animation");
                    StopCoroutine(killingCoroutine);
                    killingCoroutine = null;
                    CancelSpecialAnimationWithPlayer();
                }

                if (heldItem != null)
                {
                    DropItemServerRPC();
                }
            }
        }

        void SwitchToSearchState()
        {
            StartSearch(transform.position);
            SwitchToBehaviourState((int)State.SearchingForPlayer);
        }

        void CheckLocalPlayerForEmoteActions ()
        {
            if (currentBehaviourStateIndex != (int)State.FollowPlayer)
            {
                return;
            }
            PlayerControllerB localPlayer = GameNetworkManager.Instance.localPlayerController;
            if (localPlayer && localPlayer.performingEmote && localPlayer.playerBodyAnimator.GetInteger("emoteNumber") == 2)
            {
                playerEmoteTime += Time.deltaTime;
                LogIfDebugBuild($"Player ${localPlayer.playerClientId} emote time - {playerEmoteTime}");

                bool lookingAtDrudge = LocalPlayerLookingAtDrudge();

                bool lookingAtGround = LocalPlayerLookingAtGround();

                PlayerControllerB playerBeingLookedAt = LocalPlayerLookingAtObject<PlayerControllerB>(8);
                if (playerBeingLookedAt == GameNetworkManager.Instance.localPlayerController)
                {
                    playerBeingLookedAt = null;
                }

                DoorLock doorBeingLookedAt = LocalPlayerLookingAtObject<DoorLock>(2816);

                if (playerBeingLookedAt != null)
                {
                    LogIfDebugBuild($"Player ${localPlayer.playerClientId} looking at ${playerBeingLookedAt.playerClientId}");
                }
                if (playerEmoteTime > 0.03 && !hasActedFromEmote && (lookingAtDrudge || lookingAtGround || playerBeingLookedAt != null || (doorBeingLookedAt != null && heldItem is KeyItem)))
                {
                    LogIfDebugBuild($"Player ${localPlayer.playerClientId} has emoted! Attempting action.");
                    if (lookingAtGround)
                    {
                        LogIfDebugBuild("Dropping Item.");
                        DropItemServerRPC();
                        DoAnimationServerRPC("startDrop");
                    } else if (lookingAtDrudge)
                    { 
                        LogIfDebugBuild("Using Item.");
                        UseHeldItemServerRPC(); 
                    } else if (playerBeingLookedAt)
                    {
                        LogIfDebugBuild("Changing Target.");
                        SetNewTargetPlayerServerRPC((int)playerBeingLookedAt.playerClientId);
                    } else if (doorBeingLookedAt && heldItem is KeyItem)
                    {
                        LogIfDebugBuild("Attempting to unlock door from pointing");
                        closestDoor = doorBeingLookedAt;
                        SwitchToBehaviourState((int)State.OpeningDoor);
                    }
                    hasActedFromEmote = true;
                }

                // Reset timer if we've acted from the emote and the player is still pointing
                if (hasActedFromEmote && playerEmoteTime > 0.1)
                {
                    LogIfDebugBuild("Resetting player emote check");
                    hasActedFromEmote = false;
                    playerEmoteTime = 0;
                }
            } else
            {
                playerEmoteTime = 0;
                hasActedFromEmote = false;
            }
        }

        void ChasingPlayerState()
        {
            if (Vector3.Distance(transform.position, GetCurrentTargetPlayer().transform.position) > 20 && !CheckLineOfSightForPosition(targetPlayer.transform.position)){
                LogIfDebugBuild("Stop Target Player");
                SwitchToSearchState();
                return;
            }
            if (DoesPlayerHaveAnItem(GetCurrentTargetPlayer()))
            {
                LogIfDebugBuild("Follow Target Player");
                SwitchToBehaviourState((int)State.FollowPlayer);
                return;
            }
        }

        void AngrilyLookingAtPlayerState()
        {
            LogIfDebugBuild($"Angrily Looking At Player: {angerLevel}");
            if (Vector3.Distance(transform.position, GetCurrentTargetPlayer().transform.position) > 20 && !CheckLineOfSightForPosition(GetCurrentTargetPlayer().transform.position)){
                LogIfDebugBuild("Stop Target Player");
                SwitchToSearchState();
                return;
            }
            if (DoesPlayerHaveAnItem(GetCurrentTargetPlayer()) || heldItem != null)
            {
                LogIfDebugBuild("Follow Target Player");
                SwitchToBehaviourState((int)State.FollowPlayer);
                return;
            }
        }

        void UpdateMovingTowardsTargetPlayer()
        {
            if (GetCurrentTargetPlayer() != null && currentBehaviourStateIndex == (int)State.ChasingPlayer)
            {
                movingTowardsTargetPlayer = true;
            } else
            {
                movingTowardsTargetPlayer = false;
            }
        }

        [ServerRpc]
        void SetNewTargetPlayerServerRPC(int playerId)
        {
            SetNewTargetPlayerClientRPC(playerId);
        }

        [ClientRpc]
        void SetNewTargetPlayerClientRPC(int playerId)
        {
            SetNewTargetPlayer(StartOfRound.Instance.allPlayerScripts[playerId]);
        }

        void SetNewTargetPlayer(PlayerControllerB player)
        {
            LogIfDebugBuild($"Attempting to set new target player ${player.playerClientId}");
            bool cannotTargetPreviousPlayer = timeSinceTargetedPreviousPlayer != 0f;
            if (player.playerClientId == previousTargetPlayerId && cannotTargetPreviousPlayer)
            {
                LogIfDebugBuild("Could not set new target player");
            } else {
                LogIfDebugBuild($"Setting new target player ${player.playerClientId}");
                targetPlayer = player;
                currentTargetPlayerId = player.playerClientId;
                ChangeOwnershipOfEnemy(player.actualClientId);
                SetCurrentTargetPlayerServerRPC(player.playerClientId);
            }                 
        }

        bool DoesPlayerHaveAnItem(PlayerControllerB player)
        {
            if (Plugin.DrudgeConfig.canKillEmptyHanded.Value)
            {
                return player.currentlyHeldObjectServer != null;
            } else
            {
                GrabbableObject[] itemSlots = player.ItemSlots;
                for (int i = 0; i < itemSlots.Length; i++)
                {
                    if (itemSlots[i] != null)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        void FollowPlayer()
        {
            Vector3 targetPosition = GetCurrentTargetPlayer().transform.position - Vector3.Scale(new Vector3(3, 0, 3), Vector3.Normalize(GetCurrentTargetPlayer().transform.position - transform.position));
            if (Vector3.Distance(targetPosition, transform.position) > 0.5f)
            {
                SetDestinationToPosition(targetPosition, checkForPath: false);
            }
        }

        bool FoundClosestPlayerInRange(float range, float senseRange) {
            PlayerControllerB previousTargetPlayer = GetCurrentTargetPlayer();
            TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: true);

            if (targetPlayer == null){
                // Couldn't see a player, so we check if a player is in sensing distance instead
                TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: false);
                range = senseRange;
            }
            
            if (targetPlayer != previousTargetPlayer && targetPlayer != null)
            {
                SetNewTargetPlayerServerRPC((int)targetPlayer.playerClientId);
            }

            return targetPlayer != null && Vector3.Distance(transform.position, targetPlayer.transform.position) < range;
        }

        void FindClosestLockedDoor(float range)
        {
            List<DoorLock> list = FindObjectsOfType<DoorLock>().ToList();
            DoorLock newClosestDoor = null;
            float closestDoorDistance = range;
            foreach (DoorLock door in list)
            {
                float dist = Vector3.Distance(transform.position, door.transform.position);
                if (dist < range && dist < closestDoorDistance)
                {
                    newClosestDoor = door;
                    closestDoorDistance = dist;
                }
            }

            closestDoor = newClosestDoor;
        }
 
        bool TargetClosestPlayerInAnyCase() {
            mostOptimalDistance = 2000f;
            bool canTargetPreviousPlayer = timeSinceTargetedPreviousPlayer == 0f;
            PlayerControllerB potentialNewTargetPlayer = null;
            for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
            {
                tempDist = Vector3.Distance(transform.position, StartOfRound.Instance.allPlayerScripts[i].transform.position);
                if (tempDist < mostOptimalDistance)
                {
                    mostOptimalDistance = tempDist;
                    potentialNewTargetPlayer = StartOfRound.Instance.allPlayerScripts[i];
                }
            }
            if (potentialNewTargetPlayer == null)
            {
                return false;
            } else
            {
                if (targetPlayer != potentialNewTargetPlayer && (potentialNewTargetPlayer.playerClientId != previousTargetPlayerId || canTargetPreviousPlayer))
                {
                    LogIfDebugBuild("Targetting new player");
                    targetPlayer = potentialNewTargetPlayer;
                    ChangeOwnershipOfEnemy(targetPlayer.actualClientId);
                    SetCurrentTargetPlayerServerRPC(targetPlayer.playerClientId);
                }
                return true;
            }
        }

        public void GrabScrapFromPlayer(PlayerControllerB player)
        {
            LogIfDebugBuild("Attempting to grab scrap from player");
            if (!Plugin.DrudgeConfig.canCarryTwoHanded.Value && player.currentlyHeldObjectServer.itemProperties.twoHanded)
            {
                LogIfDebugBuild("Tried to grab scrap, but item is two handed and config is set to false");
                return;
            }
            if (heldItem != null)
            {
                DropItemServerRPC();
            }
            if (player == null) {
                Plugin.Logger.LogError("Trying to grab scrap, but couldn't find player!");
                return;
            }
            DoAnimationServerRPC("startPickUp");
            GrabbableObject component = player.currentlyHeldObjectServer;
            player.DiscardHeldObject(false, thisNetworkObject, default, true);

            SetItemAsHeldServerRPC(component.GetComponent<NetworkObject>());
        }

        [ServerRpc(RequireOwnership = false)]
        protected void SetItemAsHeldServerRPC (NetworkObjectReference component)
        {
            SetItemAsHeldClientRPC(component);
        }

        [ClientRpc]
        protected void SetItemAsHeldClientRPC (NetworkObjectReference componentRef)
        {
            // Attempt to get the network object
            if (componentRef.TryGet(out NetworkObject networkObject, null))
            {
                SetItemAsHeld(networkObject);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        protected void DespawnHeldItemServerRPC ()
        {
            heldItem.gameObject.GetComponent<NetworkObject>().Despawn(true);
        }

        private void SetItemAsHeld(NetworkObject componentRef)
        {
            heldItem = componentRef.GetComponent<GrabbableObject>();
            heldItem.parentObject = grabTarget;
            heldItem.hasHitGround = false;
            heldItem.GrabItemFromEnemy(this);
            heldItem.isHeldByEnemy = true;
            heldItem.EnablePhysics(false);
        }

        [ServerRpc(RequireOwnership = false)]
        protected void DropItemServerRPC() {
            DropItemClientRPC();
        }

        [ClientRpc]
        protected void DropItemClientRPC ()
        {
            DropItem();
        }

        private void DropItem()
        {
            LogIfDebugBuild("Attempting to drop");
            if (heldItem == null)
            {
                LogIfDebugBuild("Attempted to drop something, but holding nothing!");
                return;
            };

            heldItem.parentObject = null;
            heldItem.transform.SetParent(StartOfRound.Instance.propsContainer, true);
            heldItem.EnablePhysics(true);
            heldItem.fallTime = 0f;
            heldItem.startFallingPosition = heldItem.transform.parent.InverseTransformPoint(heldItem.transform.position);
            heldItem.targetFloorPosition = heldItem.transform.parent.InverseTransformPoint(heldItem.GetItemFloorPosition(default));
            heldItem.floorYRot = -1;
            heldItem.DiscardItemFromEnemy();
            heldItem.isHeldByEnemy = false;
            heldItem = null;
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitId = -1) {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            UseHeldItemServerRPC();
        }

        [ServerRpc]
        public void UseHeldItemServerRPC()
        {
            UseHeldItemClientRPC();
        }

        [ClientRpc]
        public void UseHeldItemClientRPC()
        {
            UseHeldItem();
        }

        public void UseHeldItem()
        {
            if (heldItem == null)
            {
                return;
            }
            LogIfDebugBuild($"Using held item ${heldItem.name}");
            if (heldItem is JetpackItem)
            {
                (heldItem as JetpackItem).ExplodeJetpackClientRpc();
                DropItem();
                return;
            }
            if (heldItem is WhoopieCushionItem)
            {
                (heldItem as WhoopieCushionItem).Fart();
                return;
            }
            if (heldItem is ExtensionLadderItem)
            {
                try
                {
                    heldItem.UseItemOnClient();
                } catch 
                {
                    // Fail silently. Lethal Company attempts to get the player to drop the ladder, but we're not a player
                }
                DropItem();
                return;
            }
            if (heldItem is StunGrenadeItem)
            {
                StunGrenadeItem grenade = heldItem as StunGrenadeItem;
                grenade.itemAnimator.SetTrigger("pullPin");
                grenade.itemAudio.PlayOneShot(grenade.pullPinSFX);
                grenade.pinPulled = true;
                DropItem();
                return;
            }
            if (heldItem is KeyItem)
            {
                FindClosestLockedDoor(10f);
                if (closestDoor)
                {
                    SwitchToBehaviourState((int)State.OpeningDoor);
                }
                return;
            }
            if (heldItem is SprayPaintItem)
            {
                // Spray paint handling is a little buggy right now. Need to actually get it to spray.
                return;
            }
            if (heldItem is WalkieTalkie)
            {
                if (heldItem.isBeingUsed && GetCurrentTargetPlayer() != null)
                {
                    if (walkieTalkieCoroutine != null)
                    {
                        LogIfDebugBuild("Stopping walkie talkie coroutine");
                        StopCoroutine(walkieTalkieCoroutine);
                        walkieTalkieCoroutine = null;
                    } else
                    {
                        LogIfDebugBuild("Starting walkie talkie coroutine");
                        walkieTalkieCoroutine = StartCoroutine(WalkieTalkieCoroutine());
                    }
                }
                return;
            }
            if (heldItem is ShotgunItem)
            {
                ShotgunItem shotgun = heldItem as ShotgunItem;
                // Specifically check if there's no shells loaded, otherwise the shotgun would attempt to reload
                if (shotgun.shellsLoaded == 0)
                {
                    return;
                }
            }

            try
            {
                heldItem.UseItemOnClient();
            } catch (Exception e)
            {
                LogIfDebugBuild($"Encountered an error while attempting to use an item generically. Name: ${heldItem.name}. Printing error below");
                LogIfDebugBuild($"{e}");
            }
        }

        private IEnumerator WalkieTalkieCoroutine()
        {
            if (!heldItem.isBeingUsed || GetCurrentTargetPlayer() == null)
            {
                LogIfDebugBuild("WalkieTalkieCoroutine -- Failed to meet initial requirements");
                walkieTalkieCoroutine = null;
                yield break;
            }
            while (heldItem is WalkieTalkie)
            {
                WalkieTalkie walkie = heldItem as WalkieTalkie;
                PlayerControllerB currentWalkieTarget = GetCurrentTargetPlayer();
                currentWalkieTarget.holdingWalkieTalkie = true;
                walkie.SetPlayerSpeakingOnWalkieTalkieServerRpc((int)currentWalkieTarget.playerClientId);
                LogIfDebugBuild("WalkieTalkieCoroutine -- Set current target player as walkie talkie owner.");
                yield return new WaitUntil(() => !(heldItem is WalkieTalkie) || GetCurrentTargetPlayer() == null || GetCurrentTargetPlayer().playerClientId != previousTargetPlayerId);
                currentWalkieTarget.holdingWalkieTalkie = false;
                walkie.UnsetPlayerSpeakingOnWalkieTalkieServerRpc((int)currentWalkieTarget.playerClientId); 
                LogIfDebugBuild("WalkieTalkieCoroutine -- Lost current target player or no longer holding walkie. Unsetting them as walie talkie owner.");
                if (GetCurrentTargetPlayer() == null)
                {
                    yield return new WaitUntil(() => !(heldItem is WalkieTalkie) || GetCurrentTargetPlayer() != null);
                    LogIfDebugBuild("WalkieTalkieCoroutine -- Found new target player.");
                }
            }

            LogIfDebugBuild("WalkieTalkieCoroutine -- No longer holding walkie talkie. Kill coroutine.");
            walkieTalkieCoroutine = null;
            yield break;
        }

        [ServerRpc]
        public void DoAnimationServerRPC(string animationName)
        {
            DoAnimationClientRPC(animationName);
        }

        [ClientRpc]
        public void DoAnimationClientRPC(string animationName) {
            LogIfDebugBuild($"Animation: {animationName}");
            creatureAnimator.SetTrigger(animationName);
        }

        public void DrudgePlayFootstepAudio()
        {
            creatureVoice.PlayOneShot(footstepSFX);
        }

        public void DrudgePlayHandCloseAudio()
        {
            creatureVoice.PlayOneShot(handCloseSFX);
        }

        public override void OnCollideWithPlayer(Collider other)
        {
            base.OnCollideWithPlayer(other);
            PlayerControllerB player = MeetsStandardPlayerCollisionConditions(other);

            if (player != null && currentBehaviourStateIndex == (int)State.ChasingPlayer && inSpecialAnimationWithPlayer == null && !DoesPlayerHaveAnItem(player))
            {
                StartKillingSequenceServerRPC(player.GetComponent<NetworkObject>());
            }
        }

        [ServerRpc(RequireOwnership = false)]
        protected void StartKillingSequenceServerRPC(NetworkObjectReference playerRef)
        {
            StartKillingSequenceClientRPC(playerRef);
        }

        [ClientRpc]
        protected void StartKillingSequenceClientRPC(NetworkObjectReference playerRef)
        {
            if (playerRef.TryGet(out NetworkObject playerNetworkObject, null))
            {
                LogIfDebugBuild("Got player reference.");
                PlayerControllerB player = playerNetworkObject.GetComponent<PlayerControllerB>();
                inSpecialAnimationWithPlayer = player;
                inSpecialAnimationWithPlayer.inSpecialInteractAnimation = true;
                inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
                killingCoroutine = StartCoroutine(KillPlayerAnimation(player)); 
            }
        }


        private IEnumerator KillPlayerAnimation(PlayerControllerB player)
        {
            DoAnimationClientRPC("startKill");
            inSpecialAnimation = true;

            creatureVoice.PlayOneShot(crushingSFX);
            float secondsToCrush = 2f;
            while (player.health > 20 && secondsToCrush > 0)
            {
                player.DamagePlayer(20, causeOfDeath: CauseOfDeath.Crushing, deathAnimation: 1);
                secondsToCrush -= 0.5f;
                yield return new WaitForSeconds(0.5f);
            }
            player.DamagePlayer(player.health - 1, causeOfDeath: CauseOfDeath.Crushing, deathAnimation: 1);
            yield return new WaitForSeconds(secondsToCrush);
            if (player.inAnimationWithEnemy == this && !player.isPlayerDead)
            {
                inSpecialAnimationWithPlayer = null;
                player.KillPlayer(Vector3.zero, true, CauseOfDeath.Crushing, 1);
                player.inSpecialInteractAnimation = false;
                player.inAnimationWithEnemy = null;
                yield return new WaitForSeconds(0.8f);
            }
            inSpecialAnimationWithPlayer = null;
            inSpecialAnimation = false;

            DoAnimationClientRPC("startPickUp");

            GrabbableObject playerBody = player.deadBody?.grabBodyObject;

            if (playerBody != null && playerBody.grabbable)
            {
                SetItemAsHeld(playerBody.GetComponent<NetworkObject>());
            }

            SwitchToSearchState();
            killingCoroutine = null;
            yield break;
        }
    }
}
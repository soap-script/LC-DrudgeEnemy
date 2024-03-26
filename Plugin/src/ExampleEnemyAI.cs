using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace ExampleEnemy {


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
     * - Stunning interrupts kill sequence
     * - Kill sequence slowly damages before outright killing
     * - Better sounds
     * - Bestiary entry
     * - Better logs for debugging
     * - RPC methods and network testing
     * - Better positioning while following player
     * - Pick up player body after killing sequence
     * - Item-specific logic (swinging a shovel, zap gun, etc.)
     */
    class ExampleEnemyAI : EnemyAI
    {
        public Transform turnCompass;
        public Transform attackArea;
        float timeSinceHittingLocalPlayer;
        float timeSinceNewRandPos;
        float playerEmoteTime;
        bool hasActedFromEmote;
        Vector3 positionRandomness;
        Vector3 StalkPos;
        System.Random enemyRandom;
        bool isDeadAnimationDone;
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

        public InteractTrigger drudgeTrigger;
        public GrabbableObject heldItem;

        public AudioClip footstepSFX;
        public Light drudgeLight;
        public Light drudgeLightGlow;
        public float angerLevel;
        public float angerLevelAccelerator;

        private DoorLock closestDoor;


        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public override void Start() {
            base.Start();
            LogIfDebugBuild("Drudge Enemy Spawned");
            timeSinceHittingLocalPlayer = 0;
            creatureAnimator.SetTrigger("startIdle");
            timeSinceNewRandPos = 0;
            positionRandomness = new Vector3(0, 0, 0);
            enemyRandom = new System.Random(StartOfRound.Instance.randomMapSeed + thisEnemyIndex);
            playerEmoteTime = 0;
            hasActedFromEmote = false;
            isDeadAnimationDone = false; 
            currentBehaviourStateIndex = (int)State.SearchingForPlayer;
            drudgeTrigger.onInteract.AddListener(GrabScrapFromPlayer);
            drudgeLight.enabled = true;
            angerLevel = 0;
            angerLevelAccelerator = 1f;
            StartSearch(transform.position);
        }

        public override void Update() {
            base.Update();

            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceNewRandPos += Time.deltaTime;

            var state = currentBehaviourStateIndex;

            if(targetPlayer != null && (state == (int)State.FollowPlayer || state == (int)State.AngrilyLookingAtPlayer)){
                turnCompass.LookAt(targetPlayer.gameplayCamera.transform.position);
                transform.rotation = Quaternion.Lerp(transform.rotation, Quaternion.Euler(new Vector3(0f, turnCompass.eulerAngles.y, 0f)), 10f * Time.deltaTime);
            }
            if (stunNormalizedTimer > 0f)
            {
                agent.speed = 0f;
            }

            
            if (agent.velocity.magnitude > 1f)
            {
                creatureAnimator.SetTrigger("startWalk");
            } else
            {
                creatureAnimator.SetTrigger("startIdle");
            }


            UpdateLightSource();
            UpdateInteractTrigger();
            UpdateSpecialAnimation();
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
            if ((currentBehaviourStateIndex != (int)State.AngrilyLookingAtPlayer && currentBehaviourStateIndex != (int)State.ChasingPlayer) && angerLevel > 0)
            {
                LogIfDebugBuild($"No reason to be angry");
                angerLevel -= (angerLevelAccelerator * AIIntervalTime);
            }
        }

        void UpdateInteractTrigger ()
        {
            if (GameNetworkManager.Instance.localPlayerController.currentlyHeldObjectServer != null && currentBehaviourStateIndex == (int)State.FollowPlayer)
            {
                drudgeTrigger.interactable = true;
                drudgeTrigger.hoverTip = "Hold [e] to give item";
            }
            else
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
        }

        public override void DoAIInterval() {
            
            base.DoAIInterval();
            if (isEnemyDead || StartOfRound.Instance.allPlayersDead) {
                return;
            };
            UpdateAngerLevel();
            UpdateMovingTowardsTargetPlayer();

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
                    KillingPlayerState();
                    break;

                case (int)State.OpeningDoor:
                    OpeningDoorState();
                    break;
                    
                default:
                    LogIfDebugBuild("This Behavior State doesn't exist!");
                    break;
            }
        }

        void SearchingForPlayerState()
        {
            agent.speed = 3f;
            if (FoundClosestPlayerInRange(25f, 3f)){
                LogIfDebugBuild("Start Target Player");
                StopSearch(currentSearch);

                if (DoesPlayerHaveAnItem(targetPlayer) || heldItem != null)
                {
                    SwitchToBehaviourClientRpc((int)State.FollowPlayer);
                } else
                {
                    SwitchToBehaviourClientRpc((int)State.AngrilyLookingAtPlayer);
                }
            }
        }

        bool TargetPlayerLookingAtDrudge()
        {
            return targetPlayer.HasLineOfSightToPosition(transform.position + Vector3.up * 0.5f);
        }

        bool TargetPlayerLookingAtGround()
        {
            return Vector3.Dot(targetPlayer.gameplayCamera.transform.forward, Vector3.down) > 0.5;
        }

        void FollowPlayerState()
        {
            agent.speed = 5f;
            // Keep targetting closest player, unless they are over 20 units away and we can't see them.
            if (!TargetClosestPlayerInAnyCase() || (Vector3.Distance(transform.position, targetPlayer.transform.position) > 20 && !HasLineOfSightToPosition(targetPlayer.transform.position))){
                LogIfDebugBuild("Stop Target Player");
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            if (!DoesPlayerHaveAnItem(targetPlayer) && !heldItem)
            {
                LogIfDebugBuild("Target player does not have item. Switching to chasing");
                SwitchToBehaviourClientRpc((int)State.AngrilyLookingAtPlayer);
                return;
            }
            CheckTargetPlayerForEmoteActions();
            FollowPlayer();
        }

        void OpeningDoorState()
        {
            if (!closestDoor || !heldItem)
            {
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            if (!closestDoor.isLocked)
            {
                closestDoor = null;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }
            float distanceToDoor = Vector3.Distance(closestDoor.transform.position, transform.position);
            if (distanceToDoor < 2f)
            {
                DespawnHeldItemServerRPC();

                closestDoor.UnlockDoorSyncWithServer();
                closestDoor.gameObject.GetComponent<AnimatedObjectTrigger>().TriggerAnimationNonPlayer(GetComponent<EnemyAICollisionDetect>().mainScript.useSecondaryAudiosOnAnimatedObjects, true, false);
                closestDoor.OpenDoorAsEnemyServerRpc();
                closestDoor = null;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            Vector3 closerSideOfDoor = closestDoor.transform.position - transform.position;
            SetDestinationToPosition(closestDoor.transform.position - Vector3.Normalize(closerSideOfDoor));
        }

        void CheckTargetPlayerForEmoteActions ()
        {
            if (targetPlayer && targetPlayer.performingEmote && targetPlayer.playerBodyAnimator.GetInteger("emoteNumber") == 2)
            {
                playerEmoteTime += Time.deltaTime;
                LogIfDebugBuild($"Player emote time - {playerEmoteTime}");
                bool lookingAtDrudge = TargetPlayerLookingAtDrudge();
                bool lookingAtGround = TargetPlayerLookingAtGround();
                if (playerEmoteTime > 0.03 && !hasActedFromEmote && (lookingAtDrudge || lookingAtGround))
                {

                    Plugin.Logger.LogInfo("Player has emoted! Attempting action.");
                    if (lookingAtGround)
                    {
                        DropItemServerRPC();
                        DoAnimationClientRpc("startDrop");
                    }
                    else if (lookingAtDrudge)
                    { 
                        UseHeldItem(); 
                    }
                    hasActedFromEmote = true;
                }
            }
            else
            {
                playerEmoteTime = 0;
                hasActedFromEmote = false;
            }
        }

        void ChasingPlayerState()
        {
            agent.speed = 8f;
            // Keep targetting closest player, unless they are over 20 units away and we can't see them
            if (!TargetClosestPlayerInAnyCase() || (Vector3.Distance(transform.position, targetPlayer.transform.position) > 20 && !HasLineOfSightToPosition(targetPlayer.transform.position))){
                LogIfDebugBuild("Stop Target Player");
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            if (DoesPlayerHaveAnItem(targetPlayer))
            {
                LogIfDebugBuild("Follow Target Player");
                SwitchToBehaviourClientRpc((int)State.FollowPlayer);
                return;
            }
        }

        void AngrilyLookingAtPlayerState()
        {
            agent.speed = 0f;
            LogIfDebugBuild($"Angrily Looking At Player: {angerLevel}");
            if (!TargetClosestPlayerInAnyCase() || (Vector3.Distance(transform.position, targetPlayer.transform.position) > 20 && !HasLineOfSightToPosition(targetPlayer.transform.position))){
                LogIfDebugBuild("Stop Target Player");
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            if (DoesPlayerHaveAnItem(targetPlayer) || heldItem != null)
            {
                LogIfDebugBuild("Follow Target Player");
                SwitchToBehaviourClientRpc((int)State.FollowPlayer);
                return;
            }
            angerLevel += (angerLevelAccelerator * AIIntervalTime);
            if (angerLevel >= 1)
            {
                targetPlayer.JumpToFearLevel(1f, true);
                SwitchToBehaviourClientRpc((int)State.ChasingPlayer);
                return;
            }
        }

        void KillingPlayerState()
        {
            agent.speed = 0;
            if (inSpecialAnimationWithPlayer == null)
            {
                SwitchToBehaviourClientRpc((int)State.ChasingPlayer);
            }
        }

        void UpdateMovingTowardsTargetPlayer()
        {
            if (targetPlayer && currentBehaviourStateIndex == (int)State.ChasingPlayer)
            {
                movingTowardsTargetPlayer = true;
            } else
            {
                movingTowardsTargetPlayer = false;
            }

        }

        bool DoesPlayerHaveAnItem(PlayerControllerB player)
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

        void FollowPlayer()
        {
            SetDestinationToPosition(targetPlayer.transform.position - Vector3.Scale(new Vector3(-5, 0 -5), targetPlayer.transform.forward), checkForPath: false);
        }

        bool FoundClosestPlayerInRange(float range, float senseRange) {
            TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: true);
            if(targetPlayer == null){
                // Couldn't see a player, so we check if a player is in sensing distance instead
                TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: false);
                range = senseRange;
            }
            return targetPlayer != null && Vector3.Distance(transform.position, targetPlayer.transform.position) < range;
        }

        void FindClosestLockedDoor(float range)
        {
            List<DoorLock> list = FindObjectsOfType<DoorLock>().ToList();
            float closestDoorDistance = range;
            foreach (DoorLock door in list)
            {
                float dist = Vector3.Distance(transform.position, door.transform.position);
                if (dist < range && dist < closestDoorDistance)
                {
                    closestDoor = door;
                    closestDoorDistance = dist;
                }
            }
        }
        
        bool TargetClosestPlayerInAnyCase() {
            mostOptimalDistance = 2000f;
            targetPlayer = null;
            for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
            {
                tempDist = Vector3.Distance(transform.position, StartOfRound.Instance.allPlayerScripts[i].transform.position);
                if (tempDist < mostOptimalDistance)
                {
                    mostOptimalDistance = tempDist;
                    targetPlayer = StartOfRound.Instance.allPlayerScripts[i];
                }
            }
            if(targetPlayer == null) return false;
            return true;
        }

        public void GrabScrapFromPlayer(PlayerControllerB player)
        {
            Plugin.Logger.LogInfo("Attempting to grab scrap from player");
            if (heldItem != null)
            {
                DropItemServerRPC();
            }
            if (player == null) {
                Plugin.Logger.LogError("Trying to grab scrap, but couldn't find player!");
            }
            DoAnimationClientRpc("startPickUp");
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
            DespawnHeldItemClientRPC();
        }

        [ClientRpc]
        protected void DespawnHeldItemClientRPC ()
        {
            heldItem.gameObject.GetComponent<NetworkObject>().Despawn(true);
            heldItem = null;
        }

        private void SetItemAsHeld(NetworkObject componentRef)
        {
            ToggleLight();
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
            ToggleLight();
        }

        private void DropItem()
        {
            Plugin.Logger.LogInfo("Attempting to drop");
            if (heldItem == null)
            {
                Plugin.Logger.LogInfo("Attempted to drop something, but holding nothing!");
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

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false) {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            UseHeldItem();
        }

        public void UseHeldItem(bool activateItem = true)
        {
            if (heldItem == null)
            {
                return;
            }
            LogIfDebugBuild($"Using held item ${heldItem.name}");
            if (heldItem is JetpackItem)
            {
                (heldItem as JetpackItem).ExplodeJetpackServerRpc();
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
                    heldItem.UseItemOnClient(activateItem);
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

            heldItem.UseItemOnClient(activateItem);
        }

        [ClientRpc]
        public void DoAnimationClientRpc(string animationName) {
            LogIfDebugBuild($"Animation: {animationName}");
            creatureAnimator.SetTrigger(animationName);
        }

        public void DrudgePlayFootstepAudio()
        {
            creatureVoice.PlayOneShot(footstepSFX);
        }

        private void ToggleLight()
        {
            drudgeLight.enabled = heldItem == null;
        }

        public override void OnCollideWithPlayer(Collider other)
        {
            base.OnCollideWithPlayer(other);
            PlayerControllerB player = MeetsStandardPlayerCollisionConditions(other);

            if (player != null && player == targetPlayer && currentBehaviourStateIndex == (int)State.ChasingPlayer && inSpecialAnimationWithPlayer == null)
            {
                inSpecialAnimationWithPlayer = player;
                inSpecialAnimationWithPlayer.inSpecialInteractAnimation = true;
                inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
                StartCoroutine(KillPlayerAnimation(player)); 
            }
        }

        private IEnumerator KillPlayerAnimation(PlayerControllerB player)
        {
            creatureAnimator.SetTrigger("startKill");
            inSpecialAnimation = true;

            yield return new WaitForSeconds(2f);
            if (player.inAnimationWithEnemy == this && !player.isPlayerDead)
            {
                inSpecialAnimationWithPlayer = null;
                player.KillPlayer(Vector3.zero, true, CauseOfDeath.Crushing, 1);
                player.inSpecialInteractAnimation = false;
                player.inAnimationWithEnemy = null;
                yield return new WaitForSeconds(1f);
            }
            inSpecialAnimationWithPlayer = null;
            inSpecialAnimation = false;
            if (!IsOwner)
            {
                yield break;
            }
            else
            {
                SwitchToBehaviourState((int)State.SearchingForPlayer);
            }
            yield break;
        }
    }
}
using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace ExampleEnemy {

    // You may be wondering, how does the Example Enemy know it is from class ExampleEnemyAI?
    // Well, we give it a reference to to this class in the Unity project where we make the asset bundle.
    // Asset bundles cannot contain scripts, so our script lives here. It is important to get the
    // reference right, or else it will not find this file. See the guide for more information.

    class ExampleEnemyAI : EnemyAI
    {
        // We set these in our Asset Bundle, so we can disable warning CS0649:
        // Field 'field' is never assigned to, and will always have its default value 'value'
        #pragma warning disable 0649
        public Transform turnCompass;
        public Transform attackArea;
        #pragma warning restore 0649
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
        }

        public InteractTrigger drudgeTrigger;
        public GrabbableObject heldItem;

        public AudioClip footstepSFX;
        public Light drudgeLight;
        public Light drudgeLightGlow;
        public float angerLevel;
        public float angerLevelAccelerator;

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
            // We make the enemy start searching. This will make it start wandering around.
            StartSearch(transform.position);
        }

        public override void Update() {
            base.Update();
            if(isEnemyDead){ 
                // For some weird reason I can't get an RPC to get called from HitEnemy() (works from other methods), so we do this workaround. We just want the enemy to stop playing the song.
                if(!isDeadAnimationDone){ 
                    LogIfDebugBuild("Stopping enemy voice with janky code.");
                    isDeadAnimationDone = true;
                    creatureVoice.Stop();
                    creatureVoice.PlayOneShot(dieSFX);
                }
                return;
            }

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

        private void UpdateSpecialAnimation()
        {
            if (inSpecialAnimationWithPlayer != null)
            {
                inSpecialAnimationWithPlayer.transform.position = playerTarget.position;
                inSpecialAnimationWithPlayer.transform.rotation = playerTarget.rotation;
            }
        }

        private void UpdateAngerLevel()
        {
            if ((currentBehaviourStateIndex != (int)State.AngrilyLookingAtPlayer && currentBehaviourStateIndex != (int)State.ChasingPlayer) && angerLevel > 0)
            {
                LogIfDebugBuild($"No reason to be angry");
                angerLevel -= (angerLevelAccelerator * AIIntervalTime);
            }
        }

        private void UpdateInteractTrigger ()
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
        private void UpdateLightSource()
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

        void CheckTargetPlayerForEmoteActions ()
        {
            // test
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

        public void UseHeldItem()
        {
            if (heldItem == null)
            {
                return;
            }
            if (heldItem is JetpackItem)
            {
                (heldItem as JetpackItem).ExplodeJetpackServerRpc();
                DropItem();
                return;
            }
            heldItem.UseItemOnClient(true);
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
            drudgeLight.enabled = !drudgeLight.enabled;
        }

        public override void OnCollideWithPlayer(Collider other)
        {
            base.OnCollideWithPlayer(other);
            PlayerControllerB player = MeetsStandardPlayerCollisionConditions(other);

            if (player != null && player == targetPlayer && currentBehaviourStateIndex == (int)State.ChasingPlayer)
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
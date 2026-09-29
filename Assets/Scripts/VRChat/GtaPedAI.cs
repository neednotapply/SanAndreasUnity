using UdonSharp;
using UnityEngine;
using UnityEngine.AI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Ped AI, ported from the original PedAI state machine.
    ///
    /// The original used a StateContainer of BaseState subclasses with virtual dispatch. UdonSharp has no
    /// generics, interfaces or virtual methods, so the states collapse into int constants and a switch.
    /// The behaviour is preserved; only the structure changed.
    ///
    /// Simulation runs only on the owner. Everyone else interpolates toward the synced transform, so NPCs
    /// stay consistent without every client running the AI.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaPedAI : UdonSharpBehaviour
    {
        public const int StateIdle = 0;
        public const int StateWalkAround = 1;
        public const int StateEscape = 2;
        public const int StateChase = 3;
        public const int StateFollow = 4;
        public const int StateDead = 5;

        [Header("References")]
        public GtaPathNetworkData pathNetwork;
        public NavMeshAgent agent;
        public Animator animator;

        [Header("Behaviour")]
        [Tooltip("Ped reacts to players closer than this.")]
        public float awarenessRadius = 18f;

        [Tooltip("Ped stops fleeing/chasing once the player is further than this.")]
        public float loseInterestRadius = 45f;

        [Tooltip("How close counts as having reached a path node.")]
        public float arriveDistance = 1.5f;

        [Tooltip("If the ped ends up further than this from its cached node, it re-snaps to a nearby one " +
            "instead of walking back across the map to reach it.")]
        public float maxNodeDistance = 40f;

        public float walkSpeed = 1.6f;
        public float runSpeed = 4.5f;

        [Tooltip("If true this ped flees from threats instead of chasing them (civilian vs. aggressor).")]
        public bool isCivilian = true;

        [Tooltip("How close a hostile ped comes before it stops advancing, in metres.")]
        public float confrontDistance = 1.9f;

        [Header("Sound")]
        [Tooltip("Panic screams, taken from the game's PAIN_A bank. One is picked at random when the ped is " +
            "frightened or hurt.")]
        public AudioClip[] panicSounds;

        [Tooltip("Voice source on the ped. Left alone if absent.")]
        public AudioSource voice;

        [Tooltip("Shortest gap between screams, so a panicking ped does not stutter continuously.")]
        public float minSoundInterval = 1.5f;

        [Header("Health")]
        [Tooltip("Hit points. GTA peds have 100 by default, so a pistol takes several shots and a rifle few.")]
        public int maxHealth = 100;

        [Tooltip("Seconds a body stays before the pool may recycle it.")]
        public float bodyLingerSeconds = 20f;

        [Header("Idle")]
        public float minIdleTime = 1f;
        public float maxIdleTime = 5f;

        // synced so late joiners and non-owners see the same NPC
        [UdonSynced] private int _syncedState = StateIdle;
        [UdonSynced] private Vector3 _syncedPosition;
        [UdonSynced] private float _syncedYaw;

        private int _state = StateIdle;
        private int _currentNode = -1;
        private int _previousNode = -1;
        private float _stateTimer = 0f;
        private int _health = 100;
        private float _lastSoundTime = -99f;
        private VRCPlayerApi _targetPlayer = null;

        // remote interpolation
        private Vector3 _remotePosition;
        private float _remoteYaw;
        private bool _hasRemoteSample = false;

        private const float SyncIntervalSeconds = 0.2f;
        private float _timeSinceSync = 0f;

        // Animator parameter driven by the exported locomotion blend tree
        private const string SpeedParameter = "Speed";

        void Start()
        {
            _remotePosition = transform.position;
            _remoteYaw = transform.eulerAngles.y;
            _health = maxHealth;

            if (agent != null)
                agent.speed = walkSpeed;

            SnapToNearestNode();
            EnterState(StateIdle);
        }

        /// <summary>
        /// The pool recycles peds to new locations by toggling them active, and Start() only ever runs
        /// once - so without re-snapping here the cached node still refers to wherever this ped was last
        /// used. It would then head for a neighbour of that stale node, walking in a straight line across
        /// the map instead of following nearby streets.
        /// </summary>
        void OnEnable()
        {
            // a recycled ped may have been killed last time it was used, so health and the agent are
            // restored here rather than leaving a corpse to be placed as a live pedestrian
            _health = maxHealth;

            if (agent != null && !agent.enabled)
                agent.enabled = true;

            if (animator != null)
                animator.SetBool("Dead", false);

            SnapToNearestNode();
            EnterState(StateIdle);
        }

        void Update()
        {
            bool owner = Networking.IsOwner(gameObject);

            // The agent may only run on the owner.
            //
            // On everyone else the ped's pose comes from the synced position, and an agent left enabled
            // would walk the transform at the same time - so the ped steps forward under its own power and
            // is then dragged back by the next sync, over and over. That fight is the visible stutter.
            SetAgentEnabled(owner);

            if (owner)
                UpdateAsOwner();
            else
                UpdateAsRemote();

            UpdateAnimator();
        }

        /// <summary> Enables or disables the agent, re-seating it on the mesh when switching back on. </summary>
        private void SetAgentEnabled(bool wanted)
        {
            if (agent == null || agent.enabled == wanted)
                return;

            if (wanted)
            {
                // an agent that was off has a stale internal position, so it is placed before being used
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(transform.position, out navHit, 4f, NavMesh.AllAreas))
                {
                    agent.enabled = true;
                    agent.Warp(navHit.position);
                }
            }
            else
            {
                agent.enabled = false;
            }
        }

        private void UpdateAsOwner()
        {
            float deltaTime = Time.deltaTime;
            _stateTimer += deltaTime;

            if (_state == StateIdle)
                UpdateIdle();
            else if (_state == StateWalkAround)
                UpdateWalkAround();
            else if (_state == StateEscape)
                UpdateEscape();
            else if (_state == StateChase)
                UpdateChase();
            else if (_state == StateDead)
                return;
            else if (_state == StateFollow)
                UpdateFollow();

            _timeSinceSync += deltaTime;
            if (_timeSinceSync >= SyncIntervalSeconds)
            {
                _timeSinceSync = 0f;
                _syncedState = _state;
                _syncedPosition = transform.position;
                _syncedYaw = transform.eulerAngles.y;
                RequestSerialization();
            }
        }

        private void UpdateAsRemote()
        {
            if (!_hasRemoteSample)
                return;

            // smooth toward the last synced pose rather than snapping
            transform.position = Vector3.Lerp(transform.position, _remotePosition, Time.deltaTime * 8f);

            float yaw = Mathf.LerpAngle(transform.eulerAngles.y, _remoteYaw, Time.deltaTime * 8f);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        public override void OnDeserialization()
        {
            _state = _syncedState;
            _remotePosition = _syncedPosition;
            _remoteYaw = _syncedYaw;
            _hasRemoteSample = true;
        }

        // ---- states ------------------------------------------------------------------------------

        private void EnterState(int newState)
        {
            _state = newState;
            _stateTimer = 0f;

            if (agent == null)
                return;

            if (newState == StateIdle)
            {
                agent.speed = walkSpeed;
                StopAgent();
            }
            else if (newState == StateWalkAround)
            {
                agent.speed = walkSpeed;
            }
            else if (newState == StateEscape || newState == StateChase)
            {
                agent.speed = runSpeed;
            }
            else if (newState == StateFollow)
            {
                agent.speed = walkSpeed;
            }
        }

        private void UpdateIdle()
        {
            VRCPlayerApi threat = FindNearestPlayerWithin(awarenessRadius);
            if (threat != null && ReactToPlayer(threat))
                return;

            float idleTime = Mathf.Lerp(minIdleTime, maxIdleTime, 0.5f);
            if (_stateTimer >= idleTime)
            {
                if (PickNextWanderNode())
                    EnterState(StateWalkAround);
                else
                    _stateTimer = 0f; // nowhere to go - stay put and try again
            }
        }

        private void UpdateWalkAround()
        {
            VRCPlayerApi threat = FindNearestPlayerWithin(awarenessRadius);
            if (threat != null && ReactToPlayer(threat))
                return;

            if (HasArrived())
            {
                if (!PickNextWanderNode())
                    EnterState(StateIdle);
            }
        }

        private void UpdateEscape()
        {
            if (_targetPlayer == null || !_targetPlayer.IsValid())
            {
                EnterState(StateIdle);
                return;
            }

            Vector3 threatPosition = _targetPlayer.GetPosition();

            if (Vector3.Distance(transform.position, threatPosition) > loseInterestRadius)
            {
                EnterState(StateIdle);
                return;
            }

            // run to whichever reachable neighbour node is furthest from the threat
            if (HasArrived() || _currentNode < 0)
                FleeToFurthestNeighbour(threatPosition);
        }

        private void UpdateChase()
        {
            if (_targetPlayer == null || !_targetPlayer.IsValid())
            {
                EnterState(StateIdle);
                return;
            }

            Vector3 targetPosition = _targetPlayer.GetPosition();
            float distance = Vector3.Distance(transform.position, targetPosition);

            if (distance > loseInterestRadius)
            {
                EnterState(StateIdle);
                return;
            }

            // Stop short of the player rather than walking into them.
            //
            // Steering an agent at the player's exact position means it never arrives - it keeps pushing
            // forward against them for as long as it is chasing. One ped doing that is a shove; several
            // doing it at once pin the player and walk them off the edge of the world, which is what was
            // happening. Holding at arm's length reads as being confronted, which is the intent.
            if (distance <= confrontDistance)
            {
                StopAgent();

                // keep facing them, so a stopped hostile still looks like a threat
                Vector3 facing = targetPosition - transform.position;
                facing.y = 0f;

                if (facing.sqrMagnitude > 0.01f)
                {
                    transform.rotation = Quaternion.Slerp(
                        transform.rotation, Quaternion.LookRotation(facing), Time.deltaTime * 6f);
                }

                return;
            }

            SetDestination(targetPosition);
        }

        private void UpdateFollow()
        {
            if (_targetPlayer == null || !_targetPlayer.IsValid())
            {
                EnterState(StateIdle);
                return;
            }

            Vector3 targetPosition = _targetPlayer.GetPosition();

            if (Vector3.Distance(transform.position, targetPosition) > arriveDistance * 2f)
                SetDestination(targetPosition);
            else
                StopAgent();
        }

        /// <summary>
        /// How a ped responds to a player being nearby.
        ///
        /// Civilians do NOT react at all. In the game a civilian only panics when actually threatened - hit,
        /// shot at, or run into - and fleeing on proximity made the whole street scatter the moment the
        /// player appeared, which reads as broken rather than lifelike. Their panic comes from
        /// <see cref="TakeDamage"/> and <see cref="OnPlayerCollisionEnter"/> instead.
        ///
        /// Hostiles are the opposite: closing on whoever comes near is the entire point of them.
        /// </summary>
        private bool ReactToPlayer(VRCPlayerApi player)
        {
            if (isCivilian || player == null || !player.IsValid())
                return false;

            _targetPlayer = player;
            EnterState(StateChase);
            return true;
        }

        /// <summary>
        /// Walking into a ped frightens it.
        ///
        /// Peds no longer physically collide with players - they were shoving them through the world - so
        /// this is kept for the case where something else brings the two into contact, while proximity in
        /// ReactToPlayer is what ordinarily scares a civilian now.
        /// </summary>
        public override void OnPlayerCollisionEnter(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            if (!isCivilian)
                return;

            _targetPlayer = player;
            PlayPanicSound();
            EnterState(StateEscape);
        }

        /// <summary>
        /// Plays one of the game's own panic screams.
        ///
        /// The bank and index range come from the original's PanicState, so these are the sounds GTA itself
        /// uses when a pedestrian is frightened - male and female peds draw from different ranges.
        /// </summary>
        private void PlayPanicSound()
        {
            if (voice == null || panicSounds == null || panicSounds.Length == 0)
                return;

            if (Time.time - _lastSoundTime < minSoundInterval)
                return;

            _lastSoundTime = Time.time;

            AudioClip clip = panicSounds[Random.Range(0, panicSounds.Length)];
            if (clip == null)
                return;

            voice.clip = clip;
            voice.Play();
        }

        // ---- damage ---------------------------------------------------------------------------------

        /// <summary>
        /// Takes a hit. Survivors panic and run; anything that drops them to zero kills them.
        ///
        /// Being shot at is a much stronger provocation than being bumped into, so even a ped that would
        /// normally ignore the player flees once hit.
        /// </summary>
        public void TakeDamage(int amount)
        {
            if (_state == StateDead || amount <= 0)
                return;

            _health -= amount;

            if (_health <= 0)
            {
                Die();
                return;
            }

            PlayPanicSound();

            // wounded and still standing: run, regardless of temperament
            EnterState(StateEscape);
        }

        private void Die()
        {
            _health = 0;
            EnterState(StateDead);

            // stop steering a corpse
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();
                agent.enabled = false;
            }

            if (animator != null)
                animator.SetBool("Dead", true);

            SendCustomEventDelayedSeconds(nameof(RemoveBody), bodyLingerSeconds);
        }

        /// <summary> Clears the body once it has had time to be seen. </summary>
        public void RemoveBody()
        {
            if (_state != StateDead)
                return;

            gameObject.SetActive(false);
        }

        /// <summary> Restores a pooled ped for reuse. Called when the pool places it again. </summary>
        public void ReviveForPool()
        {
            _health = maxHealth;

            if (agent != null && !agent.enabled)
                agent.enabled = true;

            if (animator != null)
                animator.SetBool("Dead", false);

            EnterState(StateIdle);
        }

        /// <summary> True once the ped has been killed. </summary>
        public bool IsDead => _state == StateDead;

        // ---- public API (callable from other Udon behaviours) --------------------------------------

        public void StartChasing(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            _targetPlayer = player;
            EnterState(StateChase);
        }

        public void StartFollowing(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            _targetPlayer = player;
            EnterState(StateFollow);
        }

        public void Panic(VRCPlayerApi threat)
        {
            _targetPlayer = threat;
            EnterState(StateEscape);
        }

        // ---- helpers -----------------------------------------------------------------------------

        private void SnapToNearestNode()
        {
            if (pathNetwork == null)
                return;

            _currentNode = pathNetwork.FindNearestNode(transform.position, true, 1);
            _previousNode = -1;
        }

        private bool PickNextWanderNode()
        {
            if (pathNetwork == null)
                return false;

            if (_currentNode < 0)
            {
                SnapToNearestNode();
                if (_currentNode < 0)
                    return false;
            }
            else if (Vector3.Distance(transform.position, pathNetwork.GetNodePosition(_currentNode))
                     > maxNodeDistance)
            {
                // drifted (or was moved) far from the cached node - re-anchor rather than trekking back
                SnapToNearestNode();
                if (_currentNode < 0)
                    return false;
            }

            int next = pathNetwork.PickWanderTarget(_currentNode, _previousNode);
            if (next < 0)
                return false;

            _previousNode = _currentNode;
            _currentNode = next;

            SetDestination(pathNetwork.GetNodePosition(next));
            return true;
        }

        private void FleeToFurthestNeighbour(Vector3 threatPosition)
        {
            if (pathNetwork == null || _currentNode < 0)
                return;

            int linkCount = pathNetwork.GetLinkCount(_currentNode);
            int bestNode = -1;
            float bestDistance = 0f;

            for (int i = 0; i < linkCount; i++)
            {
                int candidate = pathNetwork.GetLinkedNode(_currentNode, i);
                if (candidate < 0)
                    continue;

                float distance = Vector3.Distance(pathNetwork.GetNodePosition(candidate), threatPosition);

                if (bestNode < 0 || distance > bestDistance)
                {
                    bestNode = candidate;
                    bestDistance = distance;
                }
            }

            if (bestNode < 0)
                return;

            _previousNode = _currentNode;
            _currentNode = bestNode;
            SetDestination(pathNetwork.GetNodePosition(bestNode));
        }

        private void SetDestination(Vector3 destination)
        {
            if (agent == null || !agent.isOnNavMesh)
                return;

            agent.isStopped = false;
            agent.SetDestination(destination);
        }

        private void StopAgent()
        {
            if (agent == null || !agent.isOnNavMesh)
                return;

            agent.isStopped = true;
        }

        private bool HasArrived()
        {
            if (agent == null || !agent.isOnNavMesh)
                return true;

            if (agent.pathPending)
                return false;

            return agent.remainingDistance <= arriveDistance;
        }

        private VRCPlayerApi FindNearestPlayerWithin(float radius)
        {
            int playerCount = VRCPlayerApi.GetPlayerCount();
            if (playerCount <= 0)
                return null;

            VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
            VRCPlayerApi.GetPlayers(players);

            VRCPlayerApi nearest = null;
            float nearestDistance = 0f;
            Vector3 myPosition = transform.position;

            for (int i = 0; i < players.Length; i++)
            {
                VRCPlayerApi player = players[i];
                if (player == null || !player.IsValid())
                    continue;

                float distance = Vector3.Distance(myPosition, player.GetPosition());
                if (distance > radius)
                    continue;

                if (nearest == null || distance < nearestDistance)
                {
                    nearest = player;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private void UpdateAnimator()
        {
            if (animator == null)
                return;

            // the exported controller blends idle -> walk -> run over a single normalized float
            float speed = 0f;

            if (agent != null && agent.isOnNavMesh && !agent.isStopped)
                speed = agent.velocity.magnitude / Mathf.Max(0.01f, runSpeed);

            animator.SetFloat(SpeedParameter, Mathf.Clamp01(speed));
        }
    }
}

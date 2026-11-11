using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UniRx;
using Cysharp.Threading.Tasks;

namespace Survivor
{
    public sealed partial class SurvivorDemo : MonoBehaviour
    {
        [Header("Scene references")]
        [SerializeField] private Camera _camera;
        [SerializeField] private SpriteRenderer _player;
        [SerializeField] private Sprite _square;
        [SerializeField] private Material _spriteMaterial;
        [SerializeField] private TMPro.TMP_Text _hud;
        [SerializeField] private TMPro.TMP_Text _menuText;
        [SerializeField] private GameObject _menu;
        [SerializeField] private UnityEngine.UI.Button[] _weaponButtons;
        [SerializeField] private UnityEngine.UI.Button _resetButton;
        [SerializeField] private UnityEngine.UI.Image _damageOverlay;

        private const string BestScoreKey = "SurvivorDemo.BestScore.v1";
        private readonly IntReactiveProperty _hp = new IntReactiveProperty();
        private readonly IntReactiveProperty _score = new IntReactiveProperty();
        private readonly IntReactiveProperty _best = new IntReactiveProperty();
        private readonly BoolReactiveProperty _running = new BoolReactiveProperty();
        private readonly Subject<int> _kills = new Subject<int>();
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();
        private CancellationTokenSource _runCts;
        private UnityEngine.InputSystem.InputAction _moveAction;
        private UnityEngine.InputSystem.InputAction _pauseAction;
        private Vector2 _movement;
        private Pool _enemies, _projectiles, _effects;
        private float _elapsed, _hurtUntil, _shakeUntil, _hitStopUntil;
        private bool _paused;
        private int _weapon;
        private Color _playerColor;
        private readonly string[] _weaponNames = { "MAGIC BOLT", "RADIAL BURST", "ORBITAL BLADES" };

        public int ActiveEnemies => _enemies == null ? 0 : _enemies.Active.Count;
        public int ActiveProjectiles => _projectiles == null ? 0 : _projectiles.Active.Count;
        public int CurrentScore => _score.Value;
        public int CurrentHp => _hp.Value;
        public bool IsRunning => _running.Value;
        public int CreatedPoolObjects => _enemyCapacity + _projectileCapacity + _effectCapacity + _pickupCapacity;

        private sealed class Body
        {
            public Transform Transform;
            public SpriteRenderer Renderer;
            public Vector2 Position, Velocity;
            public Color Color;
            public float Hp, Speed, Damage, Life, FlashUntil, Radius, PreviousOrbitAngle;
            public int Reward, Kind;
            public int ActiveIndex = -1;
            public Body GridNext, GridPrevious;
            public Vector2Int GridCell;
            public bool InGrid;
        }

        // Fixed-capacity pool: no Instantiate/Destroy during a run, no double returns.
        private sealed class Pool
        {
            private readonly Stack<Body> _free;
            public readonly List<Body> Active;
            private readonly Transform _parent;
            private readonly Sprite _sprite;
            private readonly Material _material;
            private readonly string _name;
            private int _sequence;
            public int Capacity { get; private set; }
            public Pool(int capacity, Transform parent, Sprite sprite, Material material, string name)
            {
                _free = new Stack<Body>(capacity);
                Active = new List<Body>(capacity);
                _parent=parent;_sprite=sprite;_material=material;_name=name;
                Resize(capacity);
            }
            public void Resize(int capacity)
            {
                if(capacity==Capacity)return;
                Clear();
                if(Active.Capacity<capacity)Active.Capacity=capacity;
                while(Capacity>capacity) { var body=_free.Pop(); UnityEngine.Object.Destroy(body.Transform.gameObject);Capacity--; }
                while(Capacity<capacity)
                {
                    var go = new GameObject(_name + "_" + _sequence++);
                    go.transform.SetParent(_parent, false);
                    var renderer = go.AddComponent<SpriteRenderer>();
                    renderer.sprite = _sprite;
                    renderer.sharedMaterial = _material;
                    renderer.sortingOrder = _name == "Enemy" ? 2 : 4;
                    go.SetActive(false);
                    _free.Push(new Body { Transform = go.transform, Renderer = renderer });
                    Capacity++;
                }
            }
            public Body Rent(Vector2 position, Color color, float size)
            {
                if (_free.Count == 0) return null;
                var body = _free.Pop();
                body.Position = position;
                body.Velocity = Vector2.zero;
                body.Color = color;
                body.Hp = body.Speed = body.Damage = body.Life = body.FlashUntil = 0;
                body.Kind = body.Reward = 0;
                body.Radius = size * 0.5f;
                body.PreviousOrbitAngle = 0;
                body.Transform.SetPositionAndRotation(position, Quaternion.identity);
                body.Transform.localScale = Vector3.one * size;
                body.Renderer.color = color;
                body.Transform.gameObject.SetActive(true);
                body.ActiveIndex=Active.Count;
                Active.Add(body);
                return body;
            }
            public void ReturnAt(int index)
            {
                var body = Active[index];
                int last = Active.Count - 1;
                Active[index] = Active[last];
                Active[index].ActiveIndex=index;
                Active.RemoveAt(last);
                body.ActiveIndex=-1;
                body.Transform.gameObject.SetActive(false);
                _free.Push(body);
            }
            public void Clear()
            {
                while (Active.Count > 0) ReturnAt(Active.Count - 1);
            }
        }

        private void Awake()
        {
            _playerColor = _player.color;
            _moveAction = new UnityEngine.InputSystem.InputAction("Move", UnityEngine.InputSystem.InputActionType.Value);
            _moveAction.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _moveAction.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow").With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            _moveAction.AddBinding("<Gamepad>/leftStick");
            _moveAction.performed += OnMove;
            _moveAction.canceled += OnMove;
            _pauseAction = new UnityEngine.InputSystem.InputAction("Pause", binding: "<Keyboard>/escape");
            _pauseAction.AddBinding("<Gamepad>/start");
            _pauseAction.performed += OnPause;
        }

        private void OnEnable() { _moveAction.Enable(); _pauseAction.Enable(); }
        private void OnDisable()
        {
            _moveAction.Disable(); _pauseAction.Disable();
            if (_running.Value) EndRun();
        }
        private void OnMove(UnityEngine.InputSystem.InputAction.CallbackContext context) => _movement = context.ReadValue<Vector2>();
        private void OnPause(UnityEngine.InputSystem.InputAction.CallbackContext context)
        {
            SetPaused(!_paused);
        }

        private bool _initialized;
        private void Start() => InitializeGameAsync().Forget();
        private async UniTask InitializeGameAsync()
        {
            _configLoading = true;
            foreach (var button in _weaponButtons) button.interactable = false;
            try { await LoadBundledBalance(); BuildPools(); }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) { Debug.LogError("XML initialization failed: " + exception.Message); return; }
            _configLoading = false;
            _best.Value = PlayerPrefs.GetInt(BestScoreKey, 0);
            _kills.Subscribe(points => _score.Value += points).AddTo(_subscriptions);
            _score.CombineLatest(_hp, _best, (score, hp, best) => score)
                .Subscribe(_ => RefreshHud()).AddTo(_subscriptions);
            _running.Subscribe(value => { _menu.SetActive(!value); RefreshMenu(); }).AddTo(_subscriptions);
            for (int i = 0; i < _weaponButtons.Length; i++)
            {
                int weapon = i;
                _weaponButtons[i].OnClickAsObservable().ThrottleFirst(TimeSpan.FromMilliseconds(_t.ClickMs))
                    .Subscribe(_ => StartRun(weapon)).AddTo(_subscriptions);
            }
            if (_resetButton != null)
                _resetButton.OnClickAsObservable().ThrottleFirst(TimeSpan.FromMilliseconds(_t.ClickMs))
                    .Subscribe(_ => ResetBestScore()).AddTo(_subscriptions);
            if (_reloadConfigButton != null)
                _reloadConfigButton.OnClickAsObservable().Subscribe(_ => ReloadBalance()).AddTo(_subscriptions);
            InitializeProgression();
            _initialized = true;
            RefreshMenu();
        }

        public void StartRun(int weapon)
        {
            if (!_initialized || _configLoading || _running.Value || weapon < 0 || weapon >= _weaponNames.Length || _best.Value < _t.Weapons[weapon].UnlockScore) return;
            CancelRun();
            _enemies.Clear(); _projectiles.Clear(); _effects.Clear();
            _weapon = weapon; _elapsed = 0; _hurtUntil = _shakeUntil = _hitStopUntil = 0;
            ResetProgression();
            _paused = false; _hp.Value = _maxHp; _score.Value = 0;
            _player.transform.position = Vector3.zero; _player.color = _playerColor;
            _damageOverlay.color = Color.clear;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            _running.Value = true;
            SimulationLoop(_runCts.Token).Forget();
            HudLoop(_runCts.Token).Forget();
        }

        private async UniTask HudLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    FlushHud();
                    await UniTask.Delay(_t.HudMs, cancellationToken: ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void SpawnEnemy()
        {
            Vector2 player = _player.transform.position;
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2);
            float distance = _camera.orthographicSize * Mathf.Sqrt(1 + _camera.aspect * _camera.aspect) + _t.SpawnMargin;
            Vector2 position = player + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
            int tierCount = _level.Value < _t.Enemies[1].UnlockLevel ? 1 : _level.Value < _t.Enemies[2].UnlockLevel ? 2 : 3;
            int tier = UnityEngine.Random.Range(0, tierCount);
            var settings=_t.Enemies[tier];
            Color color = tier == 0 ? new Color(0.45f, 0.8f, 0.3f) : tier == 1 ? new Color(1f, 0.65f, 0.1f) : new Color(0.72f, 0.3f, 0.95f);
            var enemy = _enemies.Rent(position, color, settings.Size);
            if (enemy == null) return;
            float difficulty = 1 + (_level.Value - 1) * _t.HpRate;
            enemy.Hp = settings.Hp * difficulty;
            enemy.Damage = settings.Damage * difficulty;
            enemy.Speed = settings.Speed * Mathf.Min(_t.MaximumSpeed, 1 + (_level.Value - 1) * _t.SpeedRate);
            enemy.Reward = settings.Reward;
        }

        private void FireWeapon()
        {
            Vector2 origin = _player.transform.position;
            if (_weapon == 0)
            {
                Body nearest = null; float bestDistance = float.MaxValue;
                foreach (var enemy in _enemies.Active)
                {
                    float distance = (enemy.Position - origin).sqrMagnitude;
                    if (distance < bestDistance) { bestDistance = distance; nearest = enemy; }
                }
                if (nearest != null) Shoot(origin, (nearest.Position - origin).normalized, 0);
            }
            else if (_weapon == 1)
            {
                int count = _t.BurstCount;
                for (int i = 0; i < count; i++)
                {
                    float angle = i * Mathf.PI * 2 / count;
                    Shoot(origin, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), 0);
                }
            }
            else if (_projectiles.Active.Count == 0)
            {
                int count = _t.OrbitCount;
                for (int i = 0; i < count; i++)
                {
                    var blade = Shoot(origin, Vector2.zero, 1);
                    if (blade != null) { blade.PreviousOrbitAngle = i * Mathf.PI * 2 / count; blade.Life = float.PositiveInfinity; }
                }
            }
        }
        private Body Shoot(Vector2 origin, Vector2 direction, int kind)
        {
            var settings=_t.Weapons[_weapon];
            var bullet = _projectiles.Rent(origin, kind == 0 ? new Color(0.25f, 0.9f, 1) : new Color(1, 0.9f, 0.25f), settings.Size);
            if (bullet != null)
            {
                bullet.Kind = kind; bullet.Velocity = direction * _projectileSpeed;
                bullet.Damage = settings.Damage * _damageMultiplier; bullet.Life = _projectileLife;
            }
            return bullet;
        }

        private void SimulateFrame()
        {
            if (SimulationBlocked) return;
            UpdateFeedback();
            if (Time.unscaledTime < _hitStopUntil) return;
            float dt = Time.deltaTime;
            _elapsed += dt;
            LastCollisionChecks=0;
            Vector2 player = (Vector2)_player.transform.position + Vector2.ClampMagnitude(_movement, 1) * (_runSpeed * dt);
            _player.transform.position = player;
            _enemyGrid.Clear();
            for (int i = _enemies.Active.Count - 1; i >= 0; i--)
            {
                var enemy = _enemies.Active[i];
                enemy.Position = Vector2.MoveTowards(enemy.Position, player, enemy.Speed * dt);
                enemy.Transform.position = enemy.Position;
                enemy.Renderer.color = _elapsed < enemy.FlashUntil ? Color.red : enemy.Color;
                float radius=enemy.Radius+_t.PlayerRadius;
                if ((enemy.Position - player).sqrMagnitude <= radius*radius)
                {
                    DamagePlayer((int)enemy.Damage);
                    if (!_running.Value) return;
                }
                _enemyGrid.Insert(enemy);
            }
            for (int i = _projectiles.Active.Count - 1; i >= 0; i--)
            {
                var bullet = _projectiles.Active[i];
                Vector2 previous = bullet.Position;
                bullet.Life -= dt;
                if (bullet.Kind == 1)
                {
                    float angle = _elapsed * _t.OrbitSpeed + bullet.PreviousOrbitAngle;
                    bullet.Position = player + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _t.OrbitRadius;
                    bullet.Transform.rotation = Quaternion.Euler(0, 0, angle * Mathf.Rad2Deg);
                    if (_elapsed < bullet.FlashUntil) { bullet.Transform.position = bullet.Position; continue; }
                }
                else bullet.Position += bullet.Velocity * dt;
                bullet.Transform.position = bullet.Position;
                float padding=bullet.Radius+_maxEnemyRadius;
                _enemyGrid.Query(Vector2.Min(previous,bullet.Position)-Vector2.one*padding,Vector2.Max(previous,bullet.Position)+Vector2.one*padding,_collisionCandidates);
                bool hit = false;
                for(int j=0;j<_collisionCandidates.Count;j++)
                {
                    var enemy=_collisionCandidates[j];
                    if(enemy.ActiveIndex<0)continue;
                    LastCollisionChecks++;
                    float radius = enemy.Radius + bullet.Radius;
                    if (SegmentDistanceSquared(enemy.Position, previous, bullet.Position) > radius * radius) continue;
                    enemy.Hp -= bullet.Damage; enemy.FlashUntil = _elapsed + _t.EnemyFlash;
                    EmitParticles(enemy.Position, enemy.Color, _t.EnemyParticles);
                    if (enemy.Hp <= 0)
                    {
                        DropExperience(enemy.Position, enemy.Reward);
                        _kills.OnNext(enemy.Reward);
                        _enemyGrid.Remove(enemy);
                        _enemies.ReturnAt(enemy.ActiveIndex);
                    }
                    hit = true;
                    if (bullet.Kind == 1) bullet.FlashUntil = _elapsed + _t.OrbitHitInterval / _attackMultiplier;
                    break;
                }
                if ((hit && bullet.Kind == 0) || bullet.Life <= 0) _projectiles.ReturnAt(i);
            }
            for (int i = _effects.Active.Count - 1; i >= 0; i--)
            {
                var effect = _effects.Active[i]; effect.Life -= dt;
                if (effect.Life <= 0) { _effects.ReturnAt(i); continue; }
                effect.Position += effect.Velocity * dt; effect.Transform.position = effect.Position;
                var color = effect.Color; color.a = effect.Life / _t.ParticleLife; effect.Renderer.color = color;
            }
        }
        private static float SegmentDistanceSquared(Vector2 point, Vector2 start, Vector2 end)
        {
            var delta = end - start;
            float t = delta.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(point - start, delta) / delta.sqrMagnitude) : 0;
            return (point - (start + delta * t)).sqrMagnitude;
        }
        public void DamagePlayer(int damage)
        {
            if (SimulationBlocked || _elapsed < _hurtUntil) return;
            _hurtUntil = _elapsed + _invulnerability;
            _shakeUntil = Time.unscaledTime + _t.ShakeDuration;
            _hitStopUntil = Time.unscaledTime + _t.HitStop;
            EmitParticles(_player.transform.position, new Color(1, 0.15f, 0.2f), _t.PlayerParticles);
            _hp.Value = Mathf.Max(0, _hp.Value - damage);
            if (_hp.Value == 0) EndRun();
        }
        private void EmitParticles(Vector2 position, Color color, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var effect = _effects.Rent(position, color, _t.ParticleSize);
                if (effect == null) break;
                effect.Velocity = UnityEngine.Random.insideUnitCircle * _t.ParticleSpeed; effect.Life = _t.ParticleLife;
            }
        }
        private void UpdateFeedback()
        {
            _player.color = _elapsed < _hurtUntil - _invulnerability + _t.PlayerFlash ? Color.red : _playerColor;
            float shake = Mathf.Clamp01((_shakeUntil - Time.unscaledTime) / _t.ShakeDuration);
            Vector2 offset = UnityEngine.Random.insideUnitCircle * (shake * _t.ShakeAmplitude);
            Vector3 target = _player.transform.position;
            _camera.transform.position = new Vector3(target.x + offset.x, target.y + offset.y, _t.CameraZ);
            _damageOverlay.color = new Color(1, 0, 0.05f, shake * _t.OverlayAlpha);
        }
        public void EndRun()
        {
            CancelRun();
            if (_score.Value > _best.Value)
            {
                _best.Value = _score.Value;
                PlayerPrefs.SetInt(BestScoreKey, _best.Value); PlayerPrefs.Save();
            }
            _running.Value = false;
            _paused = false; _choosingUpgrade = false; _player.color = _playerColor;
            _upgradePanel.SetActive(false); _pausePanel.SetActive(false);
            _enemyGrid.Clear();_pickupGrid.Clear();
            _enemies.Clear(); _projectiles.Clear(); _effects.Clear(); _pickups.Clear();
            _damageOverlay.color = Color.clear;
            RefreshMenu();
        }
        private void CancelRun()
        {
            if (_runCts == null) return;
            _runCts.Cancel(); _runCts.Dispose(); _runCts = null;
        }
        private void RefreshHud()
        {
            _hudDirty=true;
            if(!_running.Value && _t!=null)FlushHud();
        }
        private void RefreshMenu()
        {
            if (_menuText == null) return;
            _menuText.text = $"SQUARE SURVIVOR\nSELECT STARTING WEAPON\nLAST SCORE: {_score.Value}   |   BEST: {_best.Value}";
            if (_reloadConfigButton != null) _reloadConfigButton.interactable = !_configLoading && !_running.Value;
            for (int i = 0; i < _weaponButtons.Length; i++)
            {
                int threshold = _t.Weapons[i].UnlockScore;
                bool unlocked = _best.Value >= threshold;
                _weaponButtons[i].interactable = unlocked && !_configLoading;
                _weaponButtons[i].GetComponentInChildren<TMPro.TMP_Text>().text = _weaponNames[i] + (unlocked ? "   PLAY" : $"   LOCKED: {threshold}");
            }
        }
        private void OnDestroy()
        {
            CancelRun(); _subscriptions.Dispose();
            _hp.Dispose(); _score.Dispose(); _best.Dispose(); _running.Dispose(); _kills.Dispose();
            _level.Dispose(); _xp.Dispose();
            _moveAction.Dispose(); _pauseAction.Dispose();
        }
    }
}

using System;
using System.Threading;
using UnityEngine;
using UniRx;
using Cysharp.Threading.Tasks;

namespace Survivor
{
    public sealed partial class SurvivorDemo
    {
        [Header("Progression and UI")]
        [SerializeField] private GameObject _upgradePanel;
        [SerializeField] private UnityEngine.UI.Button[] _upgradeButtons;
        [SerializeField] private GameObject _pausePanel;
        [SerializeField] private UnityEngine.UI.Button _resumeButton;
        [SerializeField] private UnityEngine.UI.Button _exitButton;
        [SerializeField] private Transform _healthFill;
        [SerializeField] private Transform _terrain;
        private readonly IntReactiveProperty _level = new IntReactiveProperty(1);
        private readonly IntReactiveProperty _xp = new IntReactiveProperty();
        private Pool _pickups;
        private bool _choosingUpgrade;
        private int _runMaxHp;
        private float _runSpeed, _damageMultiplier = 1, _attackMultiplier = 1, _runPickupRadius;
        private float _spawnClock, _attackClock;
        private readonly int[] _offeredStats = new int[3];
        private readonly int[] _offeredRarity = new int[3];
        private readonly string[] _statNames = { "DAMAGE", "ATTACK SPEED", "MOVE SPEED", "MAX HP", "PICKUP RANGE" };
        private readonly string[] _rarityNames = { "COMMON", "RARE", "EPIC", "LEGENDARY" };
        private readonly Color[] _rarityColors = { Color.white, new Color(0.3f,0.65f,1), new Color(0.8f,0.4f,1), new Color(1,0.75f,0.2f) };
        public int PlayerLevel => _level.Value;
        public int Experience => _xp.Value;
        public int ExperienceRequired => _t.XpBase + (_level.Value - 1) * _t.XpStep;
        public int ActivePickups => _pickups == null ? 0 : _pickups.Active.Count;
        public bool IsChoosingUpgrade => _choosingUpgrade;
        public bool IsPaused => _paused || _choosingUpgrade;
        private bool SimulationBlocked => !_running.Value || IsPaused;

        private void InitializeProgression()
        {
            _runMaxHp = _maxHp;
            _hp.Subscribe(hp => UpdateHealthBar()).AddTo(_subscriptions);
            _level.CombineLatest(_xp, (level, xp) => level).Subscribe(_ => RefreshHud()).AddTo(_subscriptions);
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                _upgradeButtons[i].OnClickAsObservable().Subscribe(_ => SelectUpgrade(index)).AddTo(_subscriptions);
            }
            _resumeButton.OnClickAsObservable().Subscribe(_ => SetPaused(false)).AddTo(_subscriptions);
            _exitButton.OnClickAsObservable().Subscribe(_ => ExitToMenu()).AddTo(_subscriptions);
            _upgradePanel.SetActive(false); _pausePanel.SetActive(false);
        }
        private void ResetProgression()
        {
            _pickupGrid.Clear();
            _pickups.Clear(); _level.Value = 1; _xp.Value = 0;
            _runMaxHp = _maxHp; _runSpeed = _moveSpeed; _runPickupRadius = _pickupRadius;
            _damageMultiplier = _attackMultiplier = 1;
            _spawnClock = _attackClock = 0;
            _choosingUpgrade = false;
            _upgradePanel.SetActive(false); _pausePanel.SetActive(false);
            UpdateHealthBar();
        }
        private void UpdateHealthBar()
        {
            if (_healthFill == null) return;
            float fraction = _runMaxHp > 0 ? Mathf.Clamp01((float)_hp.Value / _runMaxHp) : 1;
            _healthFill.localScale = new Vector3(fraction, 1, 1);
            _healthFill.localPosition = new Vector3((fraction - 1) * 0.5f, 0, 0);
        }
        public void SetPaused(bool paused)
        {
            if (!_running.Value || _choosingUpgrade) return;
            _paused = paused; _pausePanel.SetActive(paused); RefreshHud();
        }
        // WebGL cannot close the browser. Exit ends the run and returns to weapon selection.
        public void ExitToMenu() { if (_running.Value) EndRun(); }

        private async UniTask SimulationLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await UniTask.NextFrame(PlayerLoopTiming.Update, ct);
                    if (ct.IsCancellationRequested || SimulationBlocked) continue;
                    SimulateFrame();
                    if (ct.IsCancellationRequested || SimulationBlocked || Time.unscaledTime < _hitStopUntil) continue;
                    float dt = Time.deltaTime;
                    _spawnClock -= dt; _attackClock -= dt;
                    if (_spawnClock <= 0)
                    {
                        SpawnEnemy();
                        _spawnClock = Mathf.Max(_t.SpawnMinimum, _spawnInterval / (1 + (_level.Value - 1) * _t.SpawnRate));
                    }
                    if (_attackClock <= 0) { FireWeapon(); _attackClock = _weaponInterval / _attackMultiplier; }
                    TickPickups();
                    MoveTerrain();
                }
            }
            catch (OperationCanceledException) { }
        }
        private void MoveTerrain()
        {
            if (_terrain == null) return;
            Vector3 position = _player.transform.position;
            // Recycle the whole grid in exact cell increments; player and entities stay in world space.
            float size = Mathf.Max(1, Mathf.Ceil(_camera.orthographicSize * Mathf.Max(1, _camera.aspect) / _t.Coverage));
            float cell = _t.CellSize * size;
            _terrain.position = new Vector3(Mathf.Floor(position.x / cell) * cell, Mathf.Floor(position.y / cell) * cell, 0);
            _terrain.localScale = Vector3.one * size;
        }
        private void DropExperience(Vector2 position, int amount)
        {
            var gem = _pickups.Rent(position, new Color(0.15f,1,0.55f), _t.GemSize);
            if (gem != null) { gem.Reward = amount; gem.Transform.rotation = Quaternion.Euler(0,0,_t.GemAngle); _pickupGrid.Insert(gem); return; }
            // Bound memory without discarding experience: merge into nearest existing pickup.
            Body nearest = null; float distance = float.MaxValue;
            foreach (var existing in _pickups.Active)
            {
                float d = (existing.Position - position).sqrMagnitude;
                if (d < distance) { nearest = existing; distance = d; }
            }
            if (nearest != null) nearest.Reward += amount;
        }
        private void TickPickups()
        {
            Vector2 player = _player.transform.position;
            _pickupGrid.Query(player-Vector2.one*_runPickupRadius,player+Vector2.one*_runPickupRadius,_pickupCandidates);
            LastPickupChecks=_pickupCandidates.Count;
            float rangeSquared=_runPickupRadius*_runPickupRadius;
            for (int i = 0; i < _pickupCandidates.Count; i++)
            {
                var gem = _pickupCandidates[i];
                if (gem.ActiveIndex<0 || (gem.Position - player).sqrMagnitude > rangeSquared) continue;
                gem.Position = Vector2.MoveTowards(gem.Position, player, _t.AttractionSpeed * Time.deltaTime);
                gem.Transform.position = gem.Position;
                _pickupGrid.Move(gem);
                if ((gem.Position - player).sqrMagnitude > _t.CollectRadiusSquared) continue;
                int amount = gem.Reward;
                _pickupGrid.Remove(gem); _pickups.ReturnAt(gem.ActiveIndex); _xp.Value += amount;
                TryLevelUp();
                if (_choosingUpgrade) break;
            }
        }
        private void TryLevelUp()
        {
            if (_choosingUpgrade || _xp.Value < ExperienceRequired) return;
            int required = ExperienceRequired;
            _xp.Value -= required; _level.Value++;
            _choosingUpgrade = true; _pausePanel.SetActive(false);
            for (int i = 0; i < 3; i++)
            {
                int stat;
                do { stat = UnityEngine.Random.Range(0, _statNames.Length); }
                while ((i > 0 && stat == _offeredStats[0]) || (i > 1 && stat == _offeredStats[1]));
                _offeredStats[i] = stat;
                float roll = UnityEngine.Random.value;
                int rarity = roll < _t.RarityThresholds[0] ? 0 : roll < _t.RarityThresholds[1] ? 1 : roll < _t.RarityThresholds[2] ? 2 : 3;
                _offeredRarity[i] = rarity;
                float value = UpgradeAmount(stat, rarity);
                var label = _upgradeButtons[i].GetComponentInChildren<TMPro.TMP_Text>();
                label.color = _rarityColors[rarity];
                label.text = _rarityNames[rarity] + "  |  " + _statNames[stat] + " +" + value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + (stat == 3 ? "" : "%");
            }
            _upgradePanel.SetActive(true); RefreshHud();
        }
        public void SelectUpgrade(int index)
        {
            if (!_running.Value || !_choosingUpgrade || index < 0 || index > 2) return;
            int stat = _offeredStats[index];
            float amount = UpgradeAmount(stat, _offeredRarity[index]);
            switch (stat)
            {
                case 0:
                    _damageMultiplier += amount / 100;
                    foreach (var bullet in _projectiles.Active) bullet.Damage = _t.Weapons[_weapon].Damage * _damageMultiplier;
                    break;
                case 1: _attackMultiplier += amount / 100; break;
                case 2: _runSpeed += _moveSpeed * amount / 100; break;
                case 3:
                    int hp = Mathf.RoundToInt(amount);
                    _runMaxHp += hp; _hp.Value += hp;
                    break;
                case 4: _runPickupRadius += _pickupRadius * amount / 100; break;
            }
            _choosingUpgrade = false; _upgradePanel.SetActive(false);
            UpdateHealthBar(); TryLevelUp(); RefreshHud();
        }
    }
}

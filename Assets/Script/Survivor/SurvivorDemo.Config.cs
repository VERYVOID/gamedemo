using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace Survivor
{
    public sealed partial class SurvivorDemo
    {
        [Header("XML balance - numeric values are in SurvivorBalance.xml")]
        [Tooltip("Optional HTTPS XML URL. Empty uses StreamingAssets/SurvivorBalance.xml. WebGL remote hosting must allow CORS.")]
        [SerializeField] private string _balanceUrl;
        [SerializeField] private UnityEngine.UI.Button _reloadConfigButton;
        private Balance _balance;
        private Transform _poolRoot;
        private bool _configLoading;
        public string ConfigStatus { get; private set; } = "Bundled XML";
        public int BestScore => _best.Value;
        private float _moveSpeed => _t.MoveSpeed;
        private int _maxHp => _t.MaxHp;
        private float _invulnerability => _t.Invulnerability;
        private int _enemyCapacity => _t.EnemyCapacity;
        private int _projectileCapacity => _t.ProjectileCapacity;
        private int _effectCapacity => _t.EffectCapacity;
        private int _pickupCapacity => _t.PickupCapacity;
        private float _pickupRadius => _t.PickupRadius;
        private float _spawnInterval => _t.SpawnInterval;
        private float _weaponInterval => _t.AttackInterval;
        private float _projectileSpeed => _t.ProjectileSpeed;
        private float _projectileLife => _t.ProjectileLife;

        private sealed class Balance
        {
            public readonly Dictionary<string, float> Values = new Dictionary<string, float>(StringComparer.Ordinal);
            public static Balance Parse(string xml, Balance schema)
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
                if (document.DocumentElement == null || document.DocumentElement.Name != "SurvivorBalance" || document.DocumentElement.GetAttribute("version") != "1")
                    throw new FormatException("Expected SurvivorBalance version=1.");
                var result = new Balance();
                foreach (XmlNode node in document.DocumentElement.ChildNodes)
                {
                    if (!(node is XmlElement element)) continue;
                    foreach (XmlAttribute attribute in element.Attributes)
                    {
                        string key = element.Name + "." + attribute.Name;
                        if (!float.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
                            throw new FormatException("Invalid finite number: " + key);
                        bool zeroAllowed = key.EndsWith("unlockScore", StringComparison.Ordinal);
                        if (key != "Terrain.cameraZ" && (zeroAllowed ? value < 0 : value <= 0)) throw new FormatException("Out of range: " + key);
                        if (result.Values.ContainsKey(key)) throw new FormatException("Duplicate key: " + key);
                        result.Values.Add(key, value);
                    }
                }
                if (schema != null)
                {
                    if (result.Values.Count != schema.Values.Count) throw new FormatException("XML keys do not match bundled schema.");
                    foreach (var key in schema.Values.Keys) if (!result.Values.ContainsKey(key)) throw new FormatException("Missing key: " + key);
                }
                string[] integers = { "Player.maxHp", "Pools.enemies", "Pools.projectiles", "Pools.effects", "Pools.pickups", "Enemy0.unlockLevel", "Enemy1.unlockLevel", "Enemy2.unlockLevel", "Enemy0.reward", "Enemy1.reward", "Enemy2.reward", "Weapon0.unlockScore", "Weapon1.unlockScore", "Weapon2.unlockScore", "Weapon.burstCount", "Weapon.orbitCount", "Experience.baseRequired", "Experience.levelStep", "Upgrade.hp", "Feedback.enemyParticles", "Feedback.playerParticles", "Runtime.hudIntervalMs", "Runtime.clickThrottleMs", "Runtime.requestTimeoutSeconds" };
                foreach (var key in integers)
                    if (!result.Values.TryGetValue(key, out float value) || value != Mathf.Floor(value) || value > 1000000) throw new FormatException("Expected bounded integer: " + key);
                foreach (var key in new[] { "Pools.enemies", "Pools.projectiles", "Pools.effects", "Pools.pickups" })
                    if (result.Values[key] > 10000) throw new FormatException("Pool cap exceeds 10000: " + key);
                if (!(result.Values["Rarity.commonThreshold"] < result.Values["Rarity.rareThreshold"] && result.Values["Rarity.rareThreshold"] < result.Values["Rarity.epicThreshold"] && result.Values["Rarity.epicThreshold"] < 1)) throw new FormatException("Rarity thresholds must increase within (0,1).");
                if (!(result.Values["Rarity.commonMultiplier"] < result.Values["Rarity.rareMultiplier"] && result.Values["Rarity.rareMultiplier"] < result.Values["Rarity.epicMultiplier"] && result.Values["Rarity.epicMultiplier"] < result.Values["Rarity.legendaryMultiplier"])) throw new FormatException("Rarity multipliers must increase.");
                if (result.Values["Enemy0.unlockLevel"] != 1 || result.Values["Enemy1.unlockLevel"] > result.Values["Enemy2.unlockLevel"] || result.Values["Weapon0.unlockScore"] != 0) throw new FormatException("Invalid unlock progression.");
                return result;
            }
        }
        private async UniTask LoadBundledBalance()
        {
            string path = Application.streamingAssetsPath.TrimEnd('/') + "/SurvivorBalance.xml";
            string xml;
            if (!path.Contains("://") && File.Exists(path)) xml = File.ReadAllText(path);
            else
            {
                using (var request = UnityEngine.Networking.UnityWebRequest.Get(path))
                {
                    await request.SendWebRequest().ToUniTask(cancellationToken: this.GetCancellationTokenOnDestroy());
                    xml = request.downloadHandler.text;
                }
            }
            _balance = Balance.Parse(xml, null);
            _t = RuntimeBalance.From(_balance);
        }
        private void BuildPools()
        {
            _enemyGrid?.Clear(); _pickupGrid?.Clear();
            if (_poolRoot == null)
            {
                _poolRoot = new GameObject("PrewarmedPools").transform;
                _poolRoot.SetParent(transform, false);
                _enemies = new Pool(_enemyCapacity, _poolRoot, _square, _spriteMaterial, "Enemy");
                _projectiles = new Pool(_projectileCapacity, _poolRoot, _square, _spriteMaterial, "Projectile");
                _effects = new Pool(_effectCapacity, _poolRoot, _square, _spriteMaterial, "HitParticle");
                _pickups = new Pool(_pickupCapacity, _poolRoot, _square, _spriteMaterial, "ExperienceGem");
            }
            else
            {
                _enemies.Resize(_enemyCapacity);_projectiles.Resize(_projectileCapacity);
                _effects.Resize(_effectCapacity);_pickups.Resize(_pickupCapacity);
            }
            PrepareSpatialGrids();
            _player.transform.localScale = Vector3.one * _t.PlayerSize;
        }
        [ContextMenu("Reload XML balance (Play Mode)")]
        public void ReloadBalance() { if (Application.isPlaying) ReloadBalanceAsync().Forget(); }
        public async UniTask ReloadBalanceAsync()
        {
            if (_running.Value || _configLoading) return;
            _configLoading = true;
            ConfigStatus = "Loading XML..."; RefreshMenu();
            try
            {
                string path = string.IsNullOrWhiteSpace(_balanceUrl) ? Application.streamingAssetsPath.TrimEnd('/') + "/SurvivorBalance.xml" : _balanceUrl;
                string xml;
                if (!path.Contains("://") && File.Exists(path)) xml = File.ReadAllText(path);
                else
                {
                    if (!path.Contains("://")) path = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                    using (var request = UnityEngine.Networking.UnityWebRequest.Get(path))
                    {
                        request.timeout = _t.TimeoutSeconds;
                        await request.SendWebRequest().ToUniTask(cancellationToken: this.GetCancellationTokenOnDestroy());
                        xml = request.downloadHandler.text;
                    }
                }
                var candidate = Balance.Parse(xml, _balance);
                var snapshot = RuntimeBalance.From(candidate);
                _balance = candidate; _t=snapshot;
                BuildPools(); ResetProgression(); _hp.Value = _maxHp;
                ConfigStatus = "XML reloaded - ready for next run";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                ConfigStatus = "Reload failed; previous config retained";
                Debug.LogWarning(ConfigStatus + ": " + exception.Message);
            }
            finally { _configLoading = false; if (this != null) RefreshMenu(); }
        }
        public void ResetBestScore()
        {
            // End first, otherwise EndRun would write the old run score back after reset.
            if (_running.Value) EndRun();
            PlayerPrefs.DeleteKey(BestScoreKey); PlayerPrefs.Save();
            _best.Value = 0; _score.Value = 0; _weapon = 0;
            RefreshMenu(); RefreshHud();
        }
        private float UpgradeAmount(int stat, int rarity)
        {
            return _t.Upgrades[stat]*_t.RarityMultipliers[rarity];
        }
    }
}

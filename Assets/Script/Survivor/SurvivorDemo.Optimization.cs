using System.Collections.Generic;
using UnityEngine;

namespace Survivor
{
    public sealed partial class SurvivorDemo
    {
        private RuntimeBalance _t;
        private SpatialGrid _enemyGrid, _pickupGrid;
        private readonly List<Body> _collisionCandidates = new List<Body>();
        private readonly List<Body> _pickupCandidates = new List<Body>();
        private float _maxEnemyRadius;
        public int LastCollisionChecks { get; private set; }
        public int LastPickupChecks { get; private set; }
        private bool _hudDirty = true;
        private int _shownEnemies = -1;
        private readonly char[] _hudBuffer = new char[512];
        private int _hudLength;

        // XML dictionaries are used once per reload, never by the simulation.
        private sealed class RuntimeBalance
        {
            public float MoveSpeed, Invulnerability, PlayerRadius, PlayerSize, SpawnInterval, SpawnMinimum, SpawnRate, SpawnMargin;
            public float HpRate, SpeedRate, MaximumSpeed, AttackInterval, ProjectileSpeed, ProjectileLife, OrbitSpeed, OrbitRadius, OrbitHitInterval;
            public float PickupRadius, GemSize, GemAngle, AttractionSpeed, CollectRadiusSquared;
            public float EnemyFlash, PlayerFlash, ShakeDuration, ShakeAmplitude, HitStop, OverlayAlpha, ParticleSize, ParticleSpeed, ParticleLife;
            public float CellSize, Coverage, CameraZ;
            public int MaxHp, EnemyCapacity, ProjectileCapacity, EffectCapacity, PickupCapacity, BurstCount, OrbitCount, XpBase, XpStep;
            public int EnemyParticles, PlayerParticles, HudMs, ClickMs, TimeoutSeconds;
            public readonly EnemyBalance[] Enemies = new EnemyBalance[3];
            public readonly WeaponBalance[] Weapons = new WeaponBalance[3];
            public readonly float[] RarityThresholds = new float[3];
            public readonly float[] RarityMultipliers = new float[4];
            public readonly float[] Upgrades = new float[5];
            public static RuntimeBalance From(Balance source)
            {
                var v = source.Values;
                var t = new RuntimeBalance {
                    MoveSpeed=v["Player.moveSpeed"], MaxHp=(int)v["Player.maxHp"], Invulnerability=v["Player.invulnerability"], PlayerRadius=v["Player.radius"], PlayerSize=v["Player.size"],
                    EnemyCapacity=(int)v["Pools.enemies"], ProjectileCapacity=(int)v["Pools.projectiles"], EffectCapacity=(int)v["Pools.effects"], PickupCapacity=(int)v["Pools.pickups"],
                    SpawnInterval=v["Spawn.interval"], SpawnMinimum=v["Spawn.minInterval"], SpawnRate=v["Spawn.levelRate"], SpawnMargin=v["Spawn.margin"],
                    HpRate=v["Enemy.hpRate"], SpeedRate=v["Enemy.speedRate"], MaximumSpeed=v["Enemy.maxSpeedMultiplier"], AttackInterval=v["Weapon.interval"],
                    ProjectileSpeed=v["Weapon.projectileSpeed"], ProjectileLife=v["Weapon.projectileLife"], BurstCount=(int)v["Weapon.burstCount"], OrbitCount=(int)v["Weapon.orbitCount"], OrbitSpeed=v["Weapon.orbitSpeed"], OrbitRadius=v["Weapon.orbitRadius"], OrbitHitInterval=v["Weapon.orbitHitInterval"],
                    XpBase=(int)v["Experience.baseRequired"], XpStep=(int)v["Experience.levelStep"], PickupRadius=v["Experience.pickupRadius"], GemSize=v["Experience.gemSize"], GemAngle=v["Experience.gemAngle"], AttractionSpeed=v["Experience.attractionSpeed"], CollectRadiusSquared=v["Experience.collectRadius"]*v["Experience.collectRadius"],
                    EnemyFlash=v["Feedback.enemyFlash"], PlayerFlash=v["Feedback.playerFlash"], ShakeDuration=v["Feedback.shakeDuration"], ShakeAmplitude=v["Feedback.shakeAmplitude"], HitStop=v["Feedback.hitStop"], OverlayAlpha=v["Feedback.overlayAlpha"], ParticleSize=v["Feedback.particleSize"], ParticleSpeed=v["Feedback.particleSpeed"], ParticleLife=v["Feedback.particleLife"], EnemyParticles=(int)v["Feedback.enemyParticles"], PlayerParticles=(int)v["Feedback.playerParticles"],
                    HudMs=(int)v["Runtime.hudIntervalMs"], ClickMs=(int)v["Runtime.clickThrottleMs"], TimeoutSeconds=(int)v["Runtime.requestTimeoutSeconds"], CellSize=v["Terrain.cellSize"], Coverage=v["Terrain.coverageHalfSize"], CameraZ=v["Terrain.cameraZ"]
                };
                for(int i=0;i<3;i++) {
                    string e="Enemy"+i, w="Weapon"+i;
                    t.Enemies[i]=new EnemyBalance { UnlockLevel=(int)v[e+".unlockLevel"], Hp=v[e+".hp"], Damage=v[e+".damage"], Speed=v[e+".speed"], Reward=(int)v[e+".reward"], Size=v[e+".size"] };
                    t.Weapons[i]=new WeaponBalance { UnlockScore=(int)v[w+".unlockScore"], Damage=v[w+".damage"], Size=v[w+".size"] };
                }
                t.RarityThresholds[0]=v["Rarity.commonThreshold"]; t.RarityThresholds[1]=v["Rarity.rareThreshold"]; t.RarityThresholds[2]=v["Rarity.epicThreshold"];
                string[] rarity={"commonMultiplier","rareMultiplier","epicMultiplier","legendaryMultiplier"};
                string[] upgrades={"damagePercent","attackPercent","movePercent","hp","pickupPercent"};
                for(int i=0;i<rarity.Length;i++)t.RarityMultipliers[i]=v["Rarity."+rarity[i]];
                for(int i=0;i<upgrades.Length;i++)t.Upgrades[i]=v["Upgrade."+upgrades[i]];
                return t;
            }
        }
        private struct EnemyBalance { public int UnlockLevel, Reward; public float Hp, Damage, Speed, Size; }
        private struct WeaponBalance { public int UnlockScore; public float Damage, Size; }

        // Intrusive links: no List allocations when entities cross cell boundaries.
        // Enemy grid rebuilds after movement; pickup grid updates only on drop/move/return.
        private sealed class SpatialGrid
        {
            private readonly Dictionary<Vector2Int, Body> _heads;
            private readonly float _cellSize;
            public SpatialGrid(int capacity, float cellSize) { _heads=new Dictionary<Vector2Int,Body>(capacity); _cellSize=cellSize; }
            private Vector2Int Cell(Vector2 p)=>new Vector2Int(Mathf.FloorToInt(p.x/_cellSize),Mathf.FloorToInt(p.y/_cellSize));
            public void Clear()
            {
                foreach(var head in _heads.Values) { var body=head; while(body!=null) { var next=body.GridNext; body.InGrid=false;body.GridNext=body.GridPrevious=null;body=next; } }
                _heads.Clear();
            }
            public void Insert(Body body)
            {
                var cell=Cell(body.Position);
                _heads.TryGetValue(cell,out var head);
                body.GridCell=cell; body.GridPrevious=null; body.GridNext=head;body.InGrid=true;
                if(head!=null)head.GridPrevious=body;
                _heads[cell]=body;
            }
            public void Remove(Body body)
            {
                if(!body.InGrid)return;
                if(body.GridPrevious!=null)body.GridPrevious.GridNext=body.GridNext;
                else if(body.GridNext!=null)_heads[body.GridCell]=body.GridNext;
                else _heads.Remove(body.GridCell);
                if(body.GridNext!=null)body.GridNext.GridPrevious=body.GridPrevious;
                body.InGrid=false;body.GridNext=body.GridPrevious=null;
            }
            public void Move(Body body) { if(Cell(body.Position)==body.GridCell)return; Remove(body);Insert(body); }
            public void Query(Vector2 min,Vector2 max,List<Body> results)
            {
                results.Clear();var first=Cell(min);var last=Cell(max);
                long cells=((long)last.x-first.x+1)*((long)last.y-first.y+1);
                // Very long sweeps / huge pickup ranges must not enumerate billions of empty cells.
                if(cells>_heads.Count*4L+16) {
                    foreach(var entry in _heads)if(entry.Key.x>=first.x&&entry.Key.x<=last.x&&entry.Key.y>=first.y&&entry.Key.y<=last.y)
                        for(var body=entry.Value;body!=null;body=body.GridNext)results.Add(body);
                    return;
                }
                for(int x=first.x;x<=last.x;x++)for(int y=first.y;y<=last.y;y++)
                    if(_heads.TryGetValue(new Vector2Int(x,y),out var head))for(var body=head;body!=null;body=body.GridNext)results.Add(body);
            }
        }
        private void PrepareSpatialGrids()
        {
            _enemyGrid?.Clear(); _pickupGrid?.Clear();
            float cell=Mathf.Max(_t.Enemies[0].Size,Mathf.Max(_t.Enemies[1].Size,_t.Enemies[2].Size))*2;
            _enemyGrid=new SpatialGrid(_enemyCapacity,cell);
            _pickupGrid=new SpatialGrid(_pickupCapacity,cell);
            _collisionCandidates.Capacity=Mathf.Max(_collisionCandidates.Capacity,_enemyCapacity);
            _pickupCandidates.Capacity=Mathf.Max(_pickupCandidates.Capacity,_pickupCapacity);
            _maxEnemyRadius=Mathf.Max(_t.Enemies[0].Size,Mathf.Max(_t.Enemies[1].Size,_t.Enemies[2].Size))*0.5f;
        }
        private void AppendHud(string text) { for(int i=0;i<text.Length;i++)_hudBuffer[_hudLength++]=text[i]; }
        private void AppendHud(int value)
        {
            if(value<0){_hudBuffer[_hudLength++]='-';value=-value;}
            int first=_hudLength;
            do { _hudBuffer[_hudLength++]=(char)('0'+value%10);value/=10; }while(value>0);
            for(int a=first,b=_hudLength-1;a<b;a++,b--){char c=_hudBuffer[a];_hudBuffer[a]=_hudBuffer[b];_hudBuffer[b]=c;}
        }
        private void FlushHud()
        {
            if(_hud==null||(!_hudDirty&&_shownEnemies==ActiveEnemies))return;
            _hudDirty=false;_shownEnemies=ActiveEnemies;_hudLength=0;
            AppendHud("SQUARE SURVIVOR   |   HP ");AppendHud(_hp.Value);AppendHud("/");AppendHud(_runMaxHp);
            AppendHud("   SCORE ");AppendHud(_score.Value);AppendHud("   BEST ");AppendHud(_best.Value);
            AppendHud("\nLEVEL ");AppendHud(_level.Value);AppendHud("   XP ");AppendHud(_xp.Value);AppendHud("/");AppendHud(ExperienceRequired);
            AppendHud("   |   ");AppendHud(_weaponNames[_weapon]);AppendHud("   |   ENEMIES ");AppendHud(ActiveEnemies);AppendHud("/");AppendHud(_enemyCapacity);
            AppendHud("\nWASD / ARROWS / GAMEPAD: MOVE   |   PICK UP GREEN GEMS   |   ");AppendHud(IsPaused?"PAUSED":"ESC: PAUSE");
            _hud.SetCharArray(_hudBuffer,0,_hudLength);
        }
    }
}

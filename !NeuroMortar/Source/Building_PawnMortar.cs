using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace PawnMortar
{
    /// <summary>
    /// 神经迫击炮。
    ///继承 Building_TurretGun。
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_PawnMortar : Building_TurretGun, IThingHolder, IOpenable, ISearchableContents
    {
        /// <summary>索敌</summary>
        public bool autoTrackingMode = false;

        /// <summary>提升</summary>
        public bool improveAccuracy = false;

        ///<summary>
        ///泛型参数 Thing。
        ///</summary>
        protected ThingOwner<Thing> innerContainer;

        ///<summary>
        ///信号名
        ///</summary>
        public string openedSignal;


        //电力同步


        ///SpawnSetup 里从 CompPowerTrader.PowerOutput 读取。</summary>
        private float cachedBasePower;

        private bool powerInitialized;

        private bool lastImproveAccuracySynced;

        ///<summary>功耗倍率。</summary>
        private const float AccuracyPowerMultiplier = 3f;

        //构造函数

        public Building_PawnMortar()
        {
            innerContainer = new ThingOwner<Thing>(this)
            {
                // Tick 会通过 ThingOwner.DoTick() 
                // DeSpawn。
                dontTickContents = true
            };
        }

        //状态

        /// <summary>Building_Casket</summary>
        public ThingOwner<Thing> InnerContainer => innerContainer;

        /// <summary>是否有东西</summary>
        public bool HasAnyContents => innerContainer != null && innerContainer.Count > 0;

        /// <summary>装着</summary>
        public Thing ContainedThing => HasAnyContents ? innerContainer[0] : null;

        /// <summary>搜索检视（ISearchableContents）。</summary>
        public ThingOwner SearchableContents => innerContainer;

        /// <summary>IOpenable是否可打开。</summary>
        public bool CanOpen => HasAnyContents;

        /// <summary>
        /// 内部是否有Pawn
        /// </summary>
        public bool HasLivePawnInside
        {
            get
            {
                if (innerContainer == null) return false;
                for (int i = 0; i < innerContainer.Count; i++)
                    if (innerContainer[i] is Pawn p && !p.Dead && !p.Destroyed) return true;
                return false;
            }
        }

        /// <summary>
        /// 电力
        /// </summary>
        public bool HasPower
        {
            get
            {
                CompPowerTrader power = GetComp<CompPowerTrader>();
                return power == null || power.PowerOn;
            }
        }

        //IThingHolder

        /// <summary>IThingHolder：返回ThingOwner。</summary>
        public ThingOwner GetDirectlyHeldThings() => innerContainer;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, innerContainer);
        }
        /// <summary>
        /// 放置时调用
        /// </summary>
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            //记录
            {
                CompPowerTrader power = GetComp<CompPowerTrader>();
                if (power != null)
                {
                    cachedBasePower = power.PowerOutput;
                    powerInitialized = true;
                }
            }

            // 同步
            // 把 lastImproveAccuracySynced 反着设，强制 SyncPower 走一遍，
            // 避免读档后开关状态与 PowerOutput 不一致。
            lastImproveAccuracySynced = !improveAccuracy;
            SyncPower();
        }
        //  存档
        public override void ExposeData()
        {
            base.ExposeData();

            //普通字段
            Scribe_Values.Look(ref autoTrackingMode, "autoTrackingMode", false);
            Scribe_Values.Look(ref improveAccuracy, "improveAccuracy", false);
            Scribe_Values.Look(ref cachedBasePower, "cachedBasePower", 0f);
            Scribe_Values.Look(ref powerInitialized, "powerInitialized", false);
            Scribe_Values.Look(ref lastImproveAccuracySynced, "lastImproveAccuracySynced", false);

            //容器
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);

            //读档
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
               
                if (innerContainer == null)
                    innerContainer = new ThingOwner<Thing>(this) { dontTickContents = true };
                else
                    innerContainer.dontTickContents = true;


                lastImproveAccuracySynced = !improveAccuracy;
                SyncPower();
            }
        }
        //  电力同步
        /// <summary>
        /// 检查并同步
        /// </summary>
        private void SyncPower()
        {
            //还没取到基础功耗
            if (!powerInitialized) return;

            //开关状态未变化
            if (improveAccuracy == lastImproveAccuracySynced) return;

            CompPowerTrader power = GetComp<CompPowerTrader>();
            if (power == null) return;

            lastImproveAccuracySynced = improveAccuracy;

            //保留符号
            float target = cachedBasePower * (improveAccuracy ? AccuracyPowerMultiplier : 1f);

            //只有实际数值变化才写入
            if (Mathf.Abs(power.PowerOutput - target) > 0.001f)
                power.PowerOutput = target;
        }

        // ============================================================
        //  Tick
        // ============================================================

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned) return;

            //电力
            SyncPower();

            //回收冷却读条
            if (progressBarEffecter != null)
            {
                progressBarEffecter.Cleanup();
                progressBarEffecter = null;
            }

            // 没人不开火
            Verb verb = AttackVerb;
            if (verb != null && !HasLivePawnInside && verb.state != VerbState.Idle)
                verb.Reset();
        }
        //装
        /// <summary>
        /// 尝试把一个 Thing 装进内部。
        /// </summary>
        public bool TryAcceptThing(Thing thing, bool allowSpecialEffects = true)
        {
            Pawn p = thing as Pawn;

            //日志
            PawnMortarLog.Message(
                $"TryAcceptThing 被调用：thing = {thing?.LabelShort ?? "<null>"}, " +
                $"holdingOwner = {(p?.holdingOwner != null ? "非空" : "空")}, " +
                $"spawned = {p?.Spawned.ToString() ?? "<null>"}");

            if (p == null || p.Dead || p.Destroyed)
            {
                PawnMortarLog.Warning("  → 拒绝：不是Pawn（null / Dead / Destroyed）");
                return false;
            }
            if (HasAnyContents)
            {
                PawnMortarLog.Warning("  → 拒绝：已经有东西了");
                return false;
            }
            if (innerContainer == null)
            {
                PawnMortarLog.Warning("  → 拒绝：innerContainer 为 null（存档/初始化异常）");
                return false;
            }

            bool added;
            if (p.holdingOwner != null)
            {
                int moved = p.holdingOwner.TryTransferToContainer(
                    p, innerContainer, p.stackCount, false);
                added = moved > 0;
                PawnMortarLog.Message($"  → 走 TryTransferToContainer 分支，moved = {moved}");
            }
            else
            {
                if (p.Spawned)
                {
                    PawnMortarLog.Warning("  → 拒绝：p.Spawned = true 且没有 holdingOwner，" +
                                          "必须先 DeSpawn 再调 TryAcceptThing");
                    return false;
                }
                added = innerContainer.TryAdd(p);
                PawnMortarLog.Message($"  → 走 TryAdd 分支，added = {added}");
            }

            if (!added)
            {
                PawnMortarLog.Warning("  → 拒绝：添加失败");
                return false;
            }

            if (allowSpecialEffects && Spawned)
                SoundDefOf.CryptosleepCasket_Accept.PlayOneShot(new TargetInfo(Position, Map));

            PawnMortarLog.Message(
                $"  → 成功：{p.LabelShort}，Mortar.HasLivePawnInside = {HasLivePawnInside}");
            return true;
        }

        /// <summary>
        /// 供 JobDriver / FloatMenu 检查能否接受。
        /// </summary>
        public bool Accepts(Thing thing)
        {
            if (!HasPower) return false;
            if (HasAnyContents) return false;
            if (!(thing is Pawn p)) return false;
            if (p.Dead || p.Destroyed) return false;
            return true;
        }

        /// <summary>
        /// IOpenable：打开
        /// </summary>
        public void Open()
        {
            if (!HasAnyContents) return;

            EjectContents();

            //发信号
            if (!openedSignal.NullOrEmpty())
                Find.SignalManager.SendSignal(new Signal(openedSignal, this.Named("SUBJECT")));

            //通知地图网格刷新
            DirtyMapMesh(Map);
        }

        /// <summary>动画时长</summary>
        public int OpenTicks => 300;

        /// <summary>
        ///弹出
        /// </summary>
        public void EjectContents()
        {
            if (innerContainer == null) return;

            // 弹出
            if (!Destroyed)
                SoundDefOf.CryptosleepCasket_Eject.PlayOneShot(
                    SoundInfo.InMap(new TargetInfo(Position, Map)));

            for (int i = 0; i < innerContainer.Count; i++)
            {
                if (innerContainer[i] is Pawn p)
                {
                    //补回组件
                    PawnComponentsUtility.AddComponentsForSpawn(p);
                    //污渍
                    p.filth?.GainFilth(ThingDefOf.Filth_Slime);
                    //低温休眠症
                    if (p.RaceProps.IsFlesh)
                        p.health.AddHediff(HediffDefOf.CryptosleepSickness);
                }
            }

            //中断
            AttackVerb?.Reset();

            //交互格
            innerContainer.TryDropAll(InteractionCell, Map, ThingPlaceMode.Near);
        }

        //  开火参数
        /// <summary>
        /// 重写自动索敌入口。
        /// </summary>
        public override LocalTargetInfo TryFindNewTarget()
        {
            if (!autoTrackingMode) return LocalTargetInfo.Invalid;
            if (!HasLivePawnInside) return LocalTargetInfo.Invalid;
            if (!HasPower) return LocalTargetInfo.Invalid;
            return base.TryFindNewTarget();
        }

        /// <summary>
        ///冷却时间
        /// </summary>
        protected override float BurstCooldownTime()
        {
            float baseTime = def.building.turretBurstCooldownTime >= 0f
                ? def.building.turretBurstCooldownTime
                : (AttackVerb?.verbProps.defaultCooldownTime ?? 0f);

            if (improveAccuracy)
                baseTime *= 0.5f;

            return baseTime;
        }

        /// <summary>
        /// 重写开火权限
        /// </summary>
        protected override bool CanSetForcedTarget => HasLivePawnInside;

        // ============================================================
        //  Gizmos
        // ============================================================

        public override IEnumerable<Gizmo> GetGizmos()
        {
            //基类 Gizmo
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            //Dev
            if (Prefs.DevMode && HasAnyContents)
            {
                Command_Action devOpen = new Command_Action();
                devOpen.defaultLabel = "DEV: Open";
                devOpen.defaultDesc = "Dev mode: eject contents.";
                devOpen.icon = ContentFinder<Texture2D>.Get("UI/Commands/DevOpen", false);
                devOpen.action = delegate { EjectContents(); };
                yield return devOpen;
            }

            if (Faction != Faction.OfPlayer) yield break;
            if (!HasLivePawnInside) yield break;

            Verb verb = AttackVerb;
            if (verb == null) yield break;

            //强制攻击
            //Command_VerbTarget
            Command_VerbTarget cmd = new Command_VerbTarget();
            cmd.defaultLabel = "CommandSetForceAttackTarget".Translate();
            cmd.defaultDesc = "CommandSetForceAttackTargetDesc".Translate();
            cmd.icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", true);
            cmd.verb = verb;
            cmd.hotKey = KeyBindingDefOf.Misc4;
            cmd.drawRadius = false;
            cmd.requiresAvailableVerb = false;   // 暂时不可用也不灰显，让玩家能点

            //禁用条件
            if (Spawned && Position.Roofed(Map))
                cmd.Disable("CannotFire".Translate() + ": " + "Roofed".Translate().CapitalizeFirst());
            else if (!HasPower)
                cmd.Disable("CannotFire".Translate() + ": " + "NoPower".Translate());
            //冷却中

            yield return cmd;

            //自动索敌
            Command_Toggle autoToggle = new Command_Toggle();
            autoToggle.defaultLabel = "PawnMortar.AutoTracking".Translate();
            autoToggle.defaultDesc = "PawnMortar.AutoTracking.Desc".Translate();
            autoToggle.icon = LoadGizmoIcon("Auto Tracking Mode");
            autoToggle.isActive = () => autoTrackingMode;
            autoToggle.toggleAction = delegate
            {
                autoTrackingMode = !autoTrackingMode;

                if (!autoTrackingMode)
                {
                    currentTargetInt = LocalTargetInfo.Invalid;
                    burstWarmupTicksLeft = 0;
                }
            };
            yield return autoToggle;

            //精度提升
            Command_Toggle accToggle = new Command_Toggle();
            accToggle.defaultLabel = "PawnMortar.ImproveAccuracy".Translate();
            accToggle.defaultDesc = "PawnMortar.ImproveAccuracy.Desc".Translate();
            accToggle.icon = LoadGizmoIcon("Improve Accuracy");
            accToggle.isActive = () => improveAccuracy;
            accToggle.toggleAction = delegate
            {
                improveAccuracy = !improveAccuracy;
                // 同步
                SyncPower();
            };
            yield return accToggle;
        }

        /// <summary>
        /// UIGizmos加载
        /// </summary>
        private static Texture2D LoadGizmoIcon(string name)
        {
            Texture2D tex = ContentFinder<Texture2D>.Get("UI/Gizmos/" + name, false);
            if (tex != null) return tex;

            tex = ContentFinder<Texture2D>.Get("UI/Gizmos/" + name.Replace(" ", ""), false);
            if (tex != null) return tex;

            return null;
        }
        //  右键
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn myPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(myPawn))
                yield return o;

            // ---- 检查 ----
            if (myPawn == null || myPawn.Dead || myPawn.Destroyed) yield break;
            if (myPawn.Downed) yield break;      // 倒地的请用"搬运"，不从这里进
            if (HasAnyContents) yield break;     // 里面已经有人

            // ---- 未通电 ----
            if (!HasPower)
            {
                yield return new FloatMenuOption(
                    "CannotUseReason".Translate("NoPower".Translate()), null);
                yield break;
            }

            // ---- 不可达 ----
            if (!myPawn.CanReach(this, PathEndMode.InteractionCell, Danger.Deadly))
            {
                yield return new FloatMenuOption("CannotUseNoPath".Translate(), null);
                yield break;
            }

            // ---- Job ----
            JobDef jobDef = PawnMortarDefOf.EnterPawnMortar;
            if (jobDef == null) yield break;

            string label = "PawnMortar.SealInside".Translate(LabelShort);
            System.Action action = delegate
            {
                myPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(jobDef, this), JobTag.Misc);
            };

            yield return FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption(label, action), myPawn, this, "ReservedBy");
        }
        //  检视信息
        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            if (!s.NullOrEmpty()) s += "\n";

            // 主要状态
            if (!HasPower)
                s += "PawnMortar.NoPower".Translate();
            else
                s += HasLivePawnInside
                    ? "PawnMortar.Ready".Translate()
                    : "PawnMortar.NoOperator".Translate();

            // 当前状态
            if (HasLivePawnInside)
            {
                if (autoTrackingMode)
                    s += "\n" + "PawnMortar.Status.AutoTracking".Translate();
                if (improveAccuracy)
                    s += "\n" + "PawnMortar.Status.ImproveAccuracy".Translate();
            }

            return s;
        }
    }
}
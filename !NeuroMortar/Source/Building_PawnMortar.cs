using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace PawnMortar
{
    /// <summary>
    /// 神经迫击炮本体。
    ///
    ///   1. 继承 Building_TurretGun，以便复用整套炮塔逻辑：
    ///        * TurretTop（炮管绘制 + 旋转）
    ///        * Burst 状态机（warmup / cast / cooldown）
    ///        * OrderAttack / 强制目标（Command_VerbTarget 的点击通路）
    ///        * CompRefuelable 消耗（每发 -1 炮弹）
    ///
    ///   2. 额外实现 IThingHolder + IOpenable：
    ///        * 内部持有一个 ThingOwner 容器
    ///        * 只有里面封着活 Pawn 时才允许开火
    ///        * 内部 Pawn 被完全冻结（不 tick、需求不掉），
    ///          与原版冷冻休眠舱一致
    ///
    ///   3. 两个开关：
    ///        * autoTrackingMode
    ///        * improveAccuracy 
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_PawnMortar : Building_TurretGun, IThingHolder, IOpenable, ISearchableContents
    {
        // ============================================================
        //  功能开关
        // ============================================================

        /// <summary>自动索敌模式。默认关闭。</summary>
        public bool autoTrackingMode = false;

        /// <summary>精度强化模式。默认关闭。</summary>
        public bool improveAccuracy = false;

        // ============================================================
        //  容器字段
        // ============================================================

        /// <summary>
        /// 内部容器。Building_Casket 原本提供这个字段，
        /// 但我们的基类是 Building_TurretGun。
        /// 泛型参数 Thing 表示可以装任何东西（实际只装 Pawn）。
        /// </summary>
        protected ThingOwner<Thing> innerContainer;

        /// <summary>
        /// 打开时向外发出的信号名（对应原版 Building_Casket 的同名字段）。
        /// </summary>
        public string openedSignal;

        // ============================================================
        //  电力同步（精度强化 → 功耗 ×3）
        // ============================================================

        /// <summary>基础功耗，保留符号。消费者为负值（例如 -100）。
        /// SpawnSetup 里从 CompPowerTrader.PowerOutput 读取。</summary>
        private float cachedBasePower;

        /// <summary>是否已经从 CompPowerTrader 读取过基础功耗。
        /// 用 bool 而不是靠 cachedBasePower 的符号来判断，
        /// 因为消费者基础功耗就是负数，无法用 < 0 判断"未初始化"。</summary>
        private bool powerInitialized;

        /// <summary>上次同步到 CompPowerTrader 的 improveAccuracy 状态。
        /// 避免每 tick 都写 PowerOutput 造成无意义的电网通知。</summary>
        private bool lastImproveAccuracySynced;

        /// <summary>精度强化时的功耗倍率。</summary>
        private const float AccuracyPowerMultiplier = 3f;

        // ============================================================
        //  构造函数
        // ============================================================

        public Building_PawnMortar()
        {
            innerContainer = new ThingOwner<Thing>(this)
            {
                // 建筑自己的 Tick 会通过 ThingOwner.DoTick() 
                //   已经 DeSpawn，需求和健康依然会照常结算。
                //   关掉之后就与原版冷冻休眠舱完全一致：内部的人被彻底冻结。
                dontTickContents = true
            };
        }

        // ============================================================
        //  容器状态
        // ============================================================

        /// <summary>对外暴露的内部容器（对齐 Building_Casket 的语义）。</summary>
        public ThingOwner<Thing> InnerContainer => innerContainer;

        /// <summary>内部是否装着任何东西。</summary>
        public bool HasAnyContents => innerContainer != null && innerContainer.Count > 0;

        /// <summary>内部装着的第一个东西（通常是那个 Pawn）。</summary>
        public Thing ContainedThing => HasAnyContents ? innerContainer[0] : null;

        /// <summary>供「搜索 / 检视」界面读取的内容物（ISearchableContents）。</summary>
        public ThingOwner SearchableContents => innerContainer;

        /// <summary>IOpenable 实现：是否可被玩家"打开"。</summary>
        public bool CanOpen => HasAnyContents;

        /// <summary>
        /// 内部是否封着一个活着的 Pawn。
        /// 这是「炮塔能不能用」的唯一判断依据：
        ///   * 有活 Pawn → 炮塔可用，攻击 Gizmo 出现
        ///   * 没有       → 炮塔完全失效
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
        /// 是否处于"有可用电力"状态：
        ///   * 没有 CompPowerTrader（理论上不会，但保险）
        ///   * 有，且 PowerOn = true（接上电网且没被 flick 关掉）
        /// </summary>
        public bool HasPower
        {
            get
            {
                CompPowerTrader power = GetComp<CompPowerTrader>();
                return power == null || power.PowerOn;
            }
        }

        // ============================================================
        //  IThingHolder 实现
        // ============================================================

        /// <summary>IThingHolder：返回自己直接持有的 ThingOwner。</summary>
        public ThingOwner GetDirectlyHeldThings() => innerContainer;

        /// <summary>
        /// IThingHolder：把容器里的子 holder 收集出去。
        /// 用于存档遍历、遍历内部所有 Thing 等场景。
        /// </summary>
        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, innerContainer);
        }

        // ============================================================
        //  生命周期
        // ============================================================

        /// <summary>
        /// 建筑被放置到地图上时调用。
        /// 这里用来：
        ///   1. 记录基础功耗（从刚初始化完的 CompPowerTrader 里取）
        ///   2. 立即同步一次电力状态（读档时建筑可能已经开着精度强化）
        /// </summary>
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // ---- 1. 记录基础功耗 ----
            // 此刻 CompPowerTrader 已经完成了它自己的 PostSpawnSetup，
            // PowerOutput 已经被写入 XML 里的 <basePowerConsumption> 取负。
            // 例如 XML 写 100，这里读到 -100。
            if (!powerInitialized)
            {
                CompPowerTrader power = GetComp<CompPowerTrader>();
                if (power != null)
                {
                    cachedBasePower = power.PowerOutput;
                    powerInitialized = true;
                }
            }

            // ---- 2. 立即同步一次 ----
            // 把 lastImproveAccuracySynced 反着设，强制 SyncPower 走一遍，
            // 避免读档后开关状态与 PowerOutput 不一致。
            lastImproveAccuracySynced = !improveAccuracy;
            SyncPower();
        }

        // ============================================================
        //  存档
        // ============================================================

        public override void ExposeData()
        {
            base.ExposeData();

            // ---- 普通字段 ----
            // Scribe_Values.Look 的第三个参数是默认值，
            // 老存档里没有这些节点时就用默认值。
            Scribe_Values.Look(ref autoTrackingMode, "autoTrackingMode", false);
            Scribe_Values.Look(ref improveAccuracy, "improveAccuracy", false);
            Scribe_Values.Look(ref cachedBasePower, "cachedBasePower", 0f);
            Scribe_Values.Look(ref powerInitialized, "powerInitialized", false);
            Scribe_Values.Look(ref lastImproveAccuracySynced, "lastImproveAccuracySynced", false);

            // ---- 容器 ----
            // Scribe_Deep 会递归保存容器里的内容物（也就是那个 Pawn）。
            // 第三个参数 this 会被传给 ThingOwner(IThingHolder) 构造，
            // 保证读档后 innerContainer.owner 指向正确的建筑。
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);

            // ---- 读档后的补丁 ----
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // 情况 A：老存档没有 innerContainer 节点 → 补一个新的
                if (innerContainer == null)
                    innerContainer = new ThingOwner<Thing>(this) { dontTickContents = true };
                // 情况 B：正常读档 → 强制补上 dontTickContents
                // Scribe_Deep 走的是 ThingOwner(IThingHolder) 构造，
                // 不会执行构造函数体里的 { dontTickContents = true }，
                // 所以要在这里手动补一次。
                else
                    innerContainer.dontTickContents = true;

                // 读档后立刻同步一次电力状态
                lastImproveAccuracySynced = !improveAccuracy;
                SyncPower();
            }
        }

        // ============================================================
        //  电力同步
        // ============================================================

        /// <summary>
        /// 检查并同步功耗到 CompPowerTrader。
        ///
        /// 逻辑：
        ///   * 只在「开关状态变化」时写入 PowerOutput（避免每 tick 无意义通知电网）
        ///   * 消费者 PowerOutput 是负数，直接乘倍率（-100 * 3 = -300）
        /// </summary>
        private void SyncPower()
        {
            // 还没取到基础功耗 → 什么都不做
            if (!powerInitialized) return;

            // 开关状态未变化 → 不需要写
            if (improveAccuracy == lastImproveAccuracySynced) return;

            CompPowerTrader power = GetComp<CompPowerTrader>();
            if (power == null) return;

            lastImproveAccuracySynced = improveAccuracy;

            // 保留符号：负数是消耗
            float target = cachedBasePower * (improveAccuracy ? AccuracyPowerMultiplier : 1f);

            // 只有实际数值变化才写入
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

            // 电力同步：开关状态变化时更新功耗
            SyncPower();

            // ---- 永远回收炮塔冷却读条 ----
            // 基类 Building_TurretGun.Tick 在 burstCooldownTicksLeft > 0 且 Active 时
            // 会生成 EffecterDefOf.ProgressBar。我们在同一个 tick 内立刻 Cleanup，
            // Mote 还没被渲染就已经销毁，画面上永远看不到。
            //
            // 注意：小人进入时的进度条来自 JobDriver_EnterPawnMortar 里的
            // Toils_General.Wait(60).WithProgressBarToilDelay(...)，
            // 那是 Job 自己的读条，与炮塔无关，不受这段代码影响。
            if (progressBarEffecter != null)
            {
                progressBarEffecter.Cleanup();
                progressBarEffecter = null;
            }

            // 没人就不许开火：中断任何进行中的施法
            Verb verb = AttackVerb;
            if (verb != null && !HasLivePawnInside && verb.state != VerbState.Idle)
                verb.Reset();
        }

        // ============================================================
        //  收容 / 弹出
        // ============================================================

        /// <summary>
        /// 尝试把一个 Thing 装进内部。
        ///
        /// 调用方（JobDriver_EnterPawnMortar）必须【先 DeSpawn】再调这里：
        /// ThingOwner.TryAdd 拒绝接受一个处于 spawned 状态的 Thing。
        /// </summary>
        public bool TryAcceptThing(Thing thing, bool allowSpecialEffects = true)
        {
            Pawn p = thing as Pawn;
            if (p == null || p.Dead || p.Destroyed) return false;   // 只收活人
            if (HasAnyContents) return false;                        // 一个炮塔只装一个人
            if (p.Spawned) return false;                             // 必须先 DeSpawn
            if (innerContainer == null) return false;

            if (!innerContainer.TryAdd(p)) return false;

            // 与原版冷冻休眠舱一致的"封入"音效
            if (allowSpecialEffects && Spawned)
                SoundDefOf.CryptosleepCasket_Accept.PlayOneShot(new TargetInfo(Position, Map));

            return true;
        }

        /// <summary>
        /// IOpenable：玩家右键"打开"时调用。
        /// </summary>
        public void Open()
        {
            if (!HasAnyContents) return;

            EjectContents();

            // 向外发信号（一般没监听者，保留以对齐原版语义）
            if (!openedSignal.NullOrEmpty())
                Find.SignalManager.SendSignal(new Signal(openedSignal, this.Named("SUBJECT")));

            // 通知地图网格刷新（容器打开后视觉上可能需要重绘）
            DirtyMapMesh(Map);
        }

        /// <summary>IOpenable：打开动画时长（tick）。对齐原版冷冻休眠舱。</summary>
        public int OpenTicks => 300;

        /// <summary>
        /// 把内部内容物弹出到附近地面。
        ///
        /// 与基类 Building_TurretGun 的差异：
        ///   * 弹出前给 Pawn 加上「冷冻病」和一点污渍
        ///   * 顺便中断当前正在进行的开火
        ///
        /// 被三种场景调用：
        ///   1. 玩家右键"弹出"（IOpenable.Open）
        ///   2. Dev 模式下的 DEV: Open
        ///   3. 建筑被摧毁时
        /// </summary>
        public void EjectContents()
        {
            if (innerContainer == null) return;

            // 弹出音效
            if (!Destroyed)
                SoundDefOf.CryptosleepCasket_Eject.PlayOneShot(
                    SoundInfo.InMap(new TargetInfo(Position, Map)));

            // 逐个 Pawn 加副作用
            for (int i = 0; i < innerContainer.Count; i++)
            {
                if (innerContainer[i] is Pawn p)
                {
                    // 补回因为 DeSpawn 而被移除的组件（比如 health、needs 的 ticker）
                    PawnComponentsUtility.AddComponentsForSpawn(p);
                    // 一点污渍，与原版冷冻休眠舱一致
                    p.filth?.GainFilth(ThingDefOf.Filth_Slime);
                    // 加低温休眠症
                    if (p.RaceProps.IsFlesh)
                        p.health.AddHediff(HediffDefOf.CryptosleepSickness);
                }
            }

            // 中断开火
            AttackVerb?.Reset();

            // 全部丢到交互格附近
            innerContainer.TryDropAll(InteractionCell, Map, ThingPlaceMode.Near);
        }

        // ============================================================
        //  开火参数
        // ============================================================

        /// <summary>
        /// 重写自动索敌入口。
        ///
        /// 逻辑：
        ///   * autoTrackingMode 关闭 → 返回 Invalid，
        ///     原版 Tick 里的 TryStartShootSomething 会因此找不到目标而放弃。
        ///   * 没活人 / 没电 → 同理返回 Invalid。
        ///   * 都满足 → 调原版索敌逻辑。
        ///
        /// 注意：玩家手动指定的 forcedTarget 走的是另一条分支
        /// （TryStartShootSomething 里先判断 forcedTarget.IsValid），
        /// 所以关闭自动索敌不会影响手动开火。
        /// </summary>
        public override LocalTargetInfo TryFindNewTarget()
        {
            if (!autoTrackingMode) return LocalTargetInfo.Invalid;
            if (!HasLivePawnInside) return LocalTargetInfo.Invalid;
            if (!HasPower) return LocalTargetInfo.Invalid;
            return base.TryFindNewTarget();
        }

        /// <summary>
        /// 每次爆发后的冷却时间。
        /// 优先用 def.building.turretBurstCooldownTime；没写就用 verb 默认值。
        /// 精度强化开启时冷却减半。
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
        /// 重写开火权限：
        ///   原版要求挂 CompMannable 且有人操作；
        ///   我们改成「内部封着活人」即可开火。
        /// 同时影响：
        ///   * 攻击 Gizmo 是否显示
        ///   * Targeter.OrderVerbForceTarget 是否接受命令
        /// </summary>
        protected override bool CanSetForcedTarget => HasLivePawnInside;

        // ============================================================
        //  Gizmos
        // ============================================================

        public override IEnumerable<Gizmo> GetGizmos()
        {
            // ---- 基类 Gizmo ----
            // 包括 CompPowerTrader 的"重新连接"按钮、
            // CompFlickable 的开关、CompRefuelable 的装填等。
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // ---- Dev 弹出按钮 ----
            // 只在开发者模式下显示，且内部有东西时出现。
            // 用于快速测试"弹出 → 重进"循环。
            if (Prefs.DevMode && HasAnyContents)
            {
                Command_Action devOpen = new Command_Action();
                devOpen.defaultLabel = "DEV: Open";
                devOpen.defaultDesc = "Dev mode: eject contents.";
                devOpen.icon = ContentFinder<Texture2D>.Get("UI/Commands/DevOpen", false);
                devOpen.action = delegate { EjectContents(); };
                yield return devOpen;
            }

            // ---- 以下按钮只对玩家派系 + 有活人时出现 ----
            if (Faction != Faction.OfPlayer) yield break;
            if (!HasLivePawnInside) yield break;

            Verb verb = AttackVerb;
            if (verb == null) yield break;

            // ---- 1. 强制攻击（原版 UI）----
            // 因为我们继承 Building_TurretGun，
            // 原版 Targeter.OrderVerbForceTarget 会自动落到 Building_Turret.OrderAttack，
            // 所以直接用原版 Command_VerbTarget 即可。
            Command_VerbTarget cmd = new Command_VerbTarget();
            cmd.defaultLabel = "CommandSetForceAttackTarget".Translate();
            cmd.defaultDesc = "CommandSetForceAttackTargetDesc".Translate();
            cmd.icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", true);
            cmd.verb = verb;
            cmd.hotKey = KeyBindingDefOf.Misc4;
            cmd.drawRadius = false;
            cmd.requiresAvailableVerb = false;   // 暂时不可用也不灰显，让玩家能点

            // 禁用条件：只在真的开不了火时才禁用
            if (Spawned && Position.Roofed(Map))
                cmd.Disable("CannotFire".Translate() + ": " + "Roofed".Translate().CapitalizeFirst());
            else if (!HasPower)
                cmd.Disable("CannotFire".Translate() + ": " + "NoPower".Translate());
            // 冷却中：不禁用。OrderAttack 会先把目标记下来，
            // 等冷却归零后自动开火。

            yield return cmd;

            // ---- 2. 自动索敌 Toggle ----
            Command_Toggle autoToggle = new Command_Toggle();
            autoToggle.defaultLabel = "PawnMortar.AutoTracking".Translate();
            autoToggle.defaultDesc = "PawnMortar.AutoTracking.Desc".Translate();
            autoToggle.icon = LoadGizmoIcon("Auto Tracking Mode");
            autoToggle.isActive = () => autoTrackingMode;
            autoToggle.toggleAction = delegate
            {
                autoTrackingMode = !autoTrackingMode;

                // 从"开"切到"关"时，丢弃当前自动锁定的目标
                if (!autoTrackingMode)
                {
                    // currentTargetInt / burstWarmupTicksLeft 是基类的 protected 字段，
                    // 直接清空，让下一 tick 自然回落到"无目标"状态。
                    currentTargetInt = LocalTargetInfo.Invalid;
                    burstWarmupTicksLeft = 0;
                }
            };
            yield return autoToggle;

            // ---- 3. 精度提升 Toggle ----
            Command_Toggle accToggle = new Command_Toggle();
            accToggle.defaultLabel = "PawnMortar.ImproveAccuracy".Translate();
            accToggle.defaultDesc = "PawnMortar.ImproveAccuracy.Desc".Translate();
            accToggle.icon = LoadGizmoIcon("Improve Accuracy");
            accToggle.isActive = () => improveAccuracy;
            accToggle.toggleAction = delegate
            {
                improveAccuracy = !improveAccuracy;
                // 立即同步一次（不等下一 tick），UI 电力读数马上更新
                SyncPower();
            };
            yield return accToggle;
        }

        /// <summary>
        /// 从 UI/Gizmos/ 下加载图标，做三级回退：
        ///   1. 完全按名字（可能带空格）
        ///   2. 去掉空格
        ///   3. 找不到 → 返回 null，按钮只是没图标，不会出红叉
        ///
        /// 你把文件放在 Textures/UI/Gizmos/ 下，两种命名都行：
        ///   Auto Tracking Mode.png
        ///   AutoTrackingMode.png
        /// </summary>
        private static Texture2D LoadGizmoIcon(string name)
        {
            Texture2D tex = ContentFinder<Texture2D>.Get("UI/Gizmos/" + name, false);
            if (tex != null) return tex;

            tex = ContentFinder<Texture2D>.Get("UI/Gizmos/" + name.Replace(" ", ""), false);
            if (tex != null) return tex;

            return null;
        }

        // ============================================================
        //  右键菜单
        // ============================================================

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn myPawn)
        {
            // 先让基类的选项都吐出来
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(myPawn))
                yield return o;

            // ---- 防御性检查 ----
            if (myPawn == null || myPawn.Dead || myPawn.Destroyed) yield break;
            if (myPawn.Downed) yield break;      // 倒地的请用"搬运"，不从这里进
            if (HasAnyContents) yield break;     // 里面已经有人

            // ---- 未通电拦截 ----
            if (!HasPower)
            {
                yield return new FloatMenuOption(
                    "CannotUseReason".Translate("NoPower".Translate()), null);
                yield break;
            }

            // ---- 不可达拦截 ----
            if (!myPawn.CanReach(this, PathEndMode.InteractionCell, Danger.Deadly))
            {
                yield return new FloatMenuOption("CannotUseNoPath".Translate(), null);
                yield break;
            }

            // ---- 正常：下达"封入"Job ----
            JobDef jobDef = PawnMortarDefOf.EnterPawnMortar;
            if (jobDef == null) yield break;

            string label = "PawnMortar.SealInside".Translate(LabelShort);
            System.Action action = delegate
            {
                myPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(jobDef, this), JobTag.Misc);
            };

            // DecoratePrioritizedTask 会自动加上"(Reserved by X)"之类的前缀
            yield return FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption(label, action), myPawn, this, "ReservedBy");
        }

        // ============================================================
        //  检视信息
        // ============================================================

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            if (!s.NullOrEmpty()) s += "\n";

            // 主要状态行
            if (!HasPower)
                s += "PawnMortar.NoPower".Translate();
            else
                s += HasLivePawnInside
                    ? "PawnMortar.Ready".Translate()
                    : "PawnMortar.NoOperator".Translate();

            // 附加两个开关的当前状态（只在小人在里面时显示）
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
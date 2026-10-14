using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PawnMortar
{
    /// <summary>
    /// 进入神经迫击炮的 Job。
    ///
    /// 流程完全对齐原版 JobDriver_EnterCryptosleepCasket：
    ///   1. 走到交互格
    ///   2. 等一小段（带进度条）
    ///   3. 先 DeSpawn 再塞进建筑内部的 ThingOwner
    ///
    ///   ThingOwner.TryAdd 会拒绝接受一个仍处于 spawned 状态的 Thing
    ///   （内部会打日志 "Tried to add spawned item ... to container"）。
    ///   所以我们必须先 actor.DeSpawnOrDeselect(...)，
    ///   再让建筑接收
    /// </summary>
    public class JobDriver_EnterPawnMortar : JobDriver
    {
        /// <summary>目标索引：A = 迫击炮本体。</summary>
        private const TargetIndex MortarInd = TargetIndex.A;

        /// <summary>
        /// 便捷属性：从 job 的目标 A 里取出迫击炮。
        /// 每次访问都会重新查一次 job.GetTarget，不缓存，避免状态陈旧。
        /// </summary>
        private Building_PawnMortar Mortar =>
            (Building_PawnMortar)job.GetTarget(MortarInd).Thing;

        /// <summary>
        /// Job 开始前，占住迫击炮这格，防止其它 Job 同时使用。
        ///
        /// 参数含义：
        ///   target    — 要占用的目标
        ///   job       — 哪个 Job 占用
        ///   maxPawns  — 最多几人占用（1 表示独占）
        ///   layer     — -1 表示默认层
        ///   errorOnFailed — 失败时是否打印错误（交给调用方决定）
        /// </summary>
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(MortarInd), job, 1, -1, null, errorOnFailed);

        /// <summary>
        /// 构造 Job 的 toil 序列。
        /// 每个 yield 返回一个 toil，游戏会依次执行。
        /// </summary>
        protected override IEnumerable<Toil> MakeNewToils()
        {
            // ---- 失败条件 1：迫击炮被移除 / 被禁止 ----
            // 会在每个 tick 检查目标是否还在、是否 forbidden。
            this.FailOnDespawnedNullOrForbidden(MortarInd);

            // ---- 失败条件 2：中途被别人抢先塞进去了 ----
            // 用一个匿名委托，每 tick 判断一次容器是否已有内容。
            this.FailOn(delegate { return Mortar != null && Mortar.HasAnyContents; });

            // ============================================================
            //  Toil 1：走到交互格
            // ============================================================
            // GotoThing 会计算路径并驱动小人走过去。
            // PathEndMode.InteractionCell 表示走到 <interactionCellOffset>
            // 指定的那一格（我们在 XML 里设成建筑正下方）。
            yield return Toils_Goto.GotoThing(MortarInd, PathEndMode.InteractionCell);

            // ============================================================
            //  Toil 2：等待 60 tick（1 秒），期间显示进度条
            // ============================================================
            // Toils_General.Wait(ticks, targetIndex)
            //   ticks        — 持续多少 tick
            //   targetIndex  — 用于进度条和 UI 定位的目标
            Toil wait = Toils_General.Wait(60, MortarInd);

            // 等待过程中如果和迫击炮失去接触 → 失败（比如建筑被拆了）
            wait.FailOnCannotTouch(MortarInd, PathEndMode.InteractionCell);

            // 在建筑上方绘制一个进度条（进度条长度随 toil 进度增长）
            wait.WithProgressBarToilDelay(MortarInd);

            yield return wait;

            // ============================================================
            //  Toil 3：进入建筑
            // ============================================================
            // Instant toil：initAction 会执行一次，然后立刻完成。
            Toil enter = ToilMaker.MakeToil("EnterPawnMortar");
            enter.initAction = delegate
            {
                // 用 enter.actor 而不是捕获的 pawn 字段
                Pawn actor = enter.actor;

                // 从当前 job 的目标 A 里取回迫击炮
                Building_PawnMortar m = actor.CurJob.targetA.Thing as Building_PawnMortar;

                // 防御性再检查一遍：走到这一步为止的 toil 之间，
                // 建筑可能已经被别人拆掉 / 被塞人了。
                if (m == null || m.Destroyed || m.HasAnyContents) return;

                // 关键：先 DeSpawn，再装进建筑的 ThingOwner
                //
                // DeSpawnOrDeselect 返回值：
                //   true  — 这个 pawn 之前是"被选中"状态，DeSpawn 时自动取消了选中
                //   false — 之前没被选中，无需恢复
                //
                // 为什么必须 DeSpawn：
                //   ThingOwner.TryAdd 拒绝接受仍处于 spawned 状态的 Thing。
                //   这与原版冷冻休眠舱完全一致。
                bool wasSelected = actor.DeSpawnOrDeselect(DestroyMode.Vanish);

                // 调用建筑自己的 TryAcceptThing。
                // 该方法内部会判存活、判空容器、判是否已 DeSpawn，
                // 全部通过后才真正 TryAdd 进 innerContainer。
                if (m.TryAcceptThing(actor, true) && wasSelected)
                {
                    // 如果玩家原来选着这个 pawn，重新选中它，
                    // 这与原版 JobDriver_EnterCryptosleepCasket 的行为一致。
                    Find.Selector.Select(actor, false, false);
                }
            };

            // Instant：initAction 执行完立即进入下一步（这里没有下一步，Job 结束）。
            enter.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return enter;
        }
    }
}
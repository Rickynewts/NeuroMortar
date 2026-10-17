using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PawnMortar
{
    /// <summary>
    /// 进入Job。
    /// </summary>
    public class JobDriver_EnterPawnMortar : JobDriver
    {
        /// <summary>目标索引</summary>
        private const TargetIndex MortarInd = TargetIndex.A;

        /// <summary>
        /// 便捷属性：从 job 的目标 A 里取出迫击炮。
        /// </summary>
        private Building_PawnMortar Mortar =>
            (Building_PawnMortar)job.GetTarget(MortarInd).Thing;
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(MortarInd), job, 1, -1, null, errorOnFailed);

        /// <summary>
        /// 构造 Job 的 toil 序列。
        /// 每个 yield 返回一个 toil，游戏会依次执行。
        /// </summary>
        protected override IEnumerable<Toil> MakeNewToils()
        {
            //失败
            // 会在每个 tick 检查目标是否还在、是否 forbidden。
            this.FailOnDespawnedNullOrForbidden(MortarInd);

            //失败
            // 用一个匿名委托，每 tick 判断一次容器是否已有内容。
            this.FailOn(delegate { return Mortar != null && Mortar.HasAnyContents; });
            //  Toil 1：交互格
            // PathEndMode.InteractionCell 表示走到 <interactionCellOffset>
            yield return Toils_Goto.GotoThing(MortarInd, PathEndMode.InteractionCell);

            //  Toil 2：60 tick
            // Toils_General.Wait(ticks, targetIndex)
            Toil wait = Toils_General.Wait(60, MortarInd);

            //失败
            wait.FailOnCannotTouch(MortarInd, PathEndMode.InteractionCell);

            //绘制进度条
            wait.WithProgressBarToilDelay(MortarInd);

            yield return wait;

            //进入
            // Instant toil：initAction
            Toil enter = ToilMaker.MakeToil("EnterPawnMortar");
            enter.initAction = delegate
            {
                // 用 enter.actor 而不是捕获的 pawn 字段
                Pawn actor = enter.actor;

                Building_PawnMortar m = actor.CurJob.targetA.Thing as Building_PawnMortar;

                //检查
                if (m == null || m.Destroyed || m.HasAnyContents) return;

                bool wasSelected = actor.DeSpawnOrDeselect(DestroyMode.Vanish);

                // 调用TryAcceptThing。
                if (m.TryAcceptThing(actor, true) && wasSelected)
                {
                    Find.Selector.Select(actor, false, false);
                }
            };

            // Instant：initAction job 结束。
            enter.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return enter;
        }
    }
}
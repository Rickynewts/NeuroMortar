using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PawnMortar
{
    /// <summary>
    /// 搬运Job。
    /// </summary>
    public class JobDriver_CarryToPawnMortar : JobDriver
    {
        private const TargetIndex TakeeInd = TargetIndex.A;
        private const TargetIndex MortarInd = TargetIndex.B;

        protected Pawn Takee => (Pawn)job.GetTarget(TakeeInd).Thing;
        protected Building_PawnMortar Mortar => (Building_PawnMortar)job.GetTarget(MortarInd).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(Takee, job, 1, -1, null, errorOnFailed)
            && pawn.Reserve(Mortar, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TakeeInd);
            this.FailOnDestroyedOrNull(MortarInd);
            this.FailOnAggroMentalState(TakeeInd);
            this.FailOn(() => !Mortar.Accepts(Takee));

            // ---- 日志：Job ----
            PawnMortarLog.Message(
                $"Job 开始：{pawn.LabelShort} 将 {Takee?.LabelShort} 送入神经迫击炮 " +
                $"{Mortar?.Position.ToString() ?? "<null>"}");

            Toil goToTakee = Toils_Goto.GotoThing(TakeeInd, PathEndMode.OnCell)
                .FailOnDestroyedNullOrForbidden(TakeeInd)
                .FailOnDespawnedNullOrForbidden(MortarInd)
                .FailOn(() => Mortar.HasAnyContents)
                .FailOn(() => !pawn.CanReach(Takee, PathEndMode.OnCell, Danger.Deadly, false, false))
                .FailOnSomeonePhysicallyInteracting(TakeeInd);

            Toil startCarry = Toils_Haul.StartCarryThing(TakeeInd, false, false, false, true, false);

            // ---- 日志：抱起Pawn ----
            startCarry.AddFinishAction(delegate
            {
                PawnMortarLog.Message(
                    $"{pawn.LabelShort} 已抱起 {Takee?.LabelShort}" +
                    $"（holdingOwner = {(Takee?.holdingOwner != null ? "非空" : "空")}）");
            });

            Toil goToMortar = Toils_Goto.GotoThing(MortarInd, PathEndMode.InteractionCell);

            goToMortar.AddFinishAction(delegate
            {
                PawnMortarLog.Message($"{pawn.LabelShort} 迫击炮交互格");
            });

            yield return Toils_Jump.JumpIf(goToMortar, () => pawn.IsCarryingPawn(Takee));
            yield return goToTakee;
            yield return startCarry;
            yield return goToMortar;

            Toil wait = Toils_General.Wait(60, MortarInd);
            wait.FailOnCannotTouch(MortarInd, PathEndMode.InteractionCell);
            wait.WithProgressBarToilDelay(MortarInd, false, -0.5f);
            yield return wait;

            Toil enter = ToilMaker.MakeToil("CarryToPawnMortar_Enter");
            enter.initAction = delegate
            {
                PawnMortarLog.Message(
                    $"准备：{Takee?.LabelShort}；" +
                    $"Takee.holdingOwner = {(Takee?.holdingOwner != null ? "搬运" : "null")}；" +
                    $"Mortar.HasAnyContents = {Mortar.HasAnyContents}");

                bool ok = Mortar.TryAcceptThing(Takee, true);

                PawnMortarLog.Message(
                    $"结果：{(ok ? "成功" : "失败")}；" +
                    $"Mortar.HasLivePawnInside = {Mortar.HasLivePawnInside}");

                if (!ok)
                {
                    PawnMortarLog.Warning(
                        $"失败：Takee = {Takee?.LabelShort}, " +
                        $"Takee.holdingOwner = {(Takee?.holdingOwner != null ? "非空" : "空")}, " +
                        $"Mortar.HasAnyContents = {Mortar.HasAnyContents}, " +
                        $"Mortar.Accepts = {Mortar.Accepts(Takee)}");
                }
            };
            enter.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return enter;
        }
    }
}
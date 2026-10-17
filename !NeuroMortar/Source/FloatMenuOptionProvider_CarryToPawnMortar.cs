using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace PawnMortar
{
    /// <summary>
    /// FloatMenuOptionProvider的非抽象子类自动实例化，
    /// </summary>
    public class FloatMenuOptionProvider_CarryToPawnMortar : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => true;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            // ---- 日志 ----
            if (!clickedPawn.Downed) return null;

            Pawn hauler = context.FirstSelectedPawn;
            if (hauler == null) return null;

            PawnMortarLog.Message(
                $"右键倒地小人 {clickedPawn.LabelShort}，尝试生成搬运选项 = {hauler.LabelShort}");

            if (!hauler.CanReserveAndReach(clickedPawn, PathEndMode.OnCell, Danger.Deadly, 1, -1, null, true))
            {
                PawnMortarLog.Message($"  → 跳过：搬运工无法到达{clickedPawn.LabelShort}");
                return null;
            }

            if (FindMortarFor(clickedPawn, hauler, true) == null)
            {
                PawnMortarLog.Message($"  → 跳过：地图上没有可用的神经迫击炮");
                return null;
            }

            PawnMortarLog.Message($"  → 生成选项：把 {clickedPawn.LabelShort} 送入神经迫击炮");

            TaggedString label = "PawnMortar.CarryToMortar".Translate(clickedPawn.LabelCap, clickedPawn);
            // ...quest lodger / guest 检查不变...

            Action action = delegate
            {
                Building_PawnMortar m = FindMortarFor(clickedPawn, hauler, false)
                                     ?? FindMortarFor(clickedPawn, hauler, true);
                if (m == null)
                {
                    PawnMortarLog.Warning($"  → 失败：点击瞬间找不到可用");
                    Messages.Message(
                        "PawnMortar.CannotCarry".Translate() + ": " + "PawnMortar.NoMortar".Translate(),
                        clickedPawn, MessageTypeDefOf.RejectInput, false);
                    return;
                }

                PawnMortarLog.Message(
                    $"  → 下达 Job：{hauler.LabelShort} 搬运 {clickedPawn.LabelShort} → 迫击炮 @ {m.Position}");

                Job job = JobMaker.MakeJob(PawnMortarDefOf.CarryToPawnMortar, clickedPawn, m);
                job.count = 1;
                hauler.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            };

            return FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption(label, action, MenuOptionPriority.Default, null,
                                    clickedPawn, 0f, null, null, true, 0),
                hauler, clickedPawn, "ReservedBy", null);
        }

        /// <summary>
        ///寻找。
        /// </summary>
        public static Building_PawnMortar FindMortarFor(Pawn pawn, Pawn traveler, bool ignoreOtherReservations)
        {
            if (traveler?.Map == null) return null;

            foreach (Building b in traveler.Map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_PawnMortar m)) continue;
                if (!m.Spawned) continue;
                if (m.HasAnyContents) continue;
                if (!m.HasPower) continue;
                if (!traveler.CanReserveAndReach(
                        m, PathEndMode.InteractionCell, Danger.Deadly,
                        1, -1, null, ignoreOtherReservations))
                    continue;
                return m;
            }
            return null;
        }
    }
}
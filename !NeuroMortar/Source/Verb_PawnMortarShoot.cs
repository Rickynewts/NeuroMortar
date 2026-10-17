using RimWorld;
using UnityEngine;
using Verse;

namespace PawnMortar
{
    /// <summary>
    ///开火逻辑。
    ///自定义 Verb：
    ///caster 是 Building_PawnMortar，
    ///improveAccuracy为 true。
    /// </summary>
    public class Verb_PawnMortarShoot : Verb_Shoot
    {
        /// <summary>
        /// 每次开火时被调用一次。
        /// </summary>
        protected override bool TryCastShot()
        {
            Building_PawnMortar building = caster as Building_PawnMortar;
            if (building == null || !building.improveAccuracy)
                return base.TryCastShot();

            // ---- 备份原始值 ----
            float origTouch = verbProps.accuracyTouch;
            float origShort = verbProps.accuracyShort;
            float origMedium = verbProps.accuracyMedium;
            float origLong = verbProps.accuracyLong;

            try
            {
                // ----提升精度 ----
                verbProps.accuracyTouch = Mathf.Min(1f, origTouch * 2f);
                verbProps.accuracyShort = Mathf.Min(1f, origShort * 2f);
                verbProps.accuracyMedium = Mathf.Min(1f, origMedium * 2f);
                verbProps.accuracyLong = Mathf.Min(1f, origLong * 2f);

                // ---- 开火逻辑 ----
                // 基类会读上面的 accuracy* 值来决定弹着点分布。
                return base.TryCastShot();
            }
            finally
            {
                // ----恢复 ----
                verbProps.accuracyTouch = origTouch;
                verbProps.accuracyShort = origShort;
                verbProps.accuracyMedium = origMedium;
                verbProps.accuracyLong = origLong;
            }
        }
    }
}
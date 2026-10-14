using RimWorld;
using UnityEngine;
using Verse;

namespace PawnMortar
{
    /// <summary>
    /// 迫击炮专用开火逻辑。
    ///
    /// 为什么需要自定义 Verb：
    ///   需要在【每次开火】的瞬间临时修改精度参数。
    ///   RimWorld 的 Verb_Shoot.TryCastShot() 是唯一合适挂钩点。
    ///
    /// 触发条件：
    ///   caster 是 Building_PawnMortar（迫击炮），
    ///   且该建筑的 improveAccuracy 开关为 true。
    ///
    /// 触发效果：
    ///   本次开火计算期间，accuracyTouch / Short / Medium / Long 全部 ×2，
    ///   并截断在 1.0 上限。
    ///
    /// 为什么用 try/finally：
    ///   verbProps 是共享资源（如果多座迫击炮都用同一个 ThingDef，
    ///   理论上它们会共享同一份 VerbProperties 实例）。
    ///   改完后无论开火是否抛异常，都必须恢复原值，
    ///   否则会影响其它建筑以及下一次开火。
    ///
    ///   RimWorld 单线程运行，所以只要"改→开火→恢复"在同一个方法调用内完成，
    ///   中间不会被其它迫击炮的开火打断。
    /// </summary>
    public class Verb_PawnMortarShoot : Verb_Shoot
    {
        /// <summary>
        /// 每次开火时被调用一次。
        ///
        /// 注意访问修饰符是 protected：
        ///   Verb_Shoot 里此方法是 protected override，派生类必须保持一致，
        ///   不能改成 public。
        /// </summary>
        protected override bool TryCastShot()
        {
            // 只有「caster 是我们自己的迫击炮」且「精度提升开启」时
            Building_PawnMortar building = caster as Building_PawnMortar;
            if (building == null || !building.improveAccuracy)
                return base.TryCastShot();

            // ---- 1. 备份原始值 ----
            // 这里读的是 def 里 <AccuracyTouch> 等字段被解析后的值，
            // 每次开火前的初始状态都是它。
            float origTouch = verbProps.accuracyTouch;
            float origShort = verbProps.accuracyShort;
            float origMedium = verbProps.accuracyMedium;
            float origLong = verbProps.accuracyLong;

            try
            {
                // ---- 2. 临时提升精度 ----
                // Mathf.Min(1f, x) 保证不会超过 1.0（100% 命中）
                verbProps.accuracyTouch = Mathf.Min(1f, origTouch * 2f);
                verbProps.accuracyShort = Mathf.Min(1f, origShort * 2f);
                verbProps.accuracyMedium = Mathf.Min(1f, origMedium * 2f);
                verbProps.accuracyLong = Mathf.Min(1f, origLong * 2f);

                // ---- 3. 交给基类完成真正的开火逻辑 ----
                // 基类会读上面的 accuracy* 值来决定弹着点分布。
                return base.TryCastShot();
            }
            finally
            {
                // ---- 4. 无论成功与否都恢复 ----
                // 这样其它迫击炮 / 下一次开火读到的都是原值。
                verbProps.accuracyTouch = origTouch;
                verbProps.accuracyShort = origShort;
                verbProps.accuracyMedium = origMedium;
                verbProps.accuracyLong = origLong;
            }
        }
    }
}
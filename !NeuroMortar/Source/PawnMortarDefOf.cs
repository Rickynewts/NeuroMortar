using RimWorld;
using Verse;

namespace PawnMortar
{
    /// <summary>
    /// Def 引用集合。
    ///
    /// RimWorld 的 DefOf 机制：
    ///   * 只要类上带 [DefOf] 特性，游戏加载 Defs 之后，
    ///     会自动把 XML 里 <defName> 与字段名相同的 Def
    ///     赋值给这个类的静态字段。
    ///   * 这样代码里就能直接用 PawnMortarDefOf.EnterPawnMortar，
    ///     而不必每次 DefDatabase&lt;JobDef&gt;.GetNamed("EnterPawnMortar")。
    ///
    /// 使用时需要注意：
    ///   * 字段必须是 public static，且类型是某个 Def 子类。
    ///   * 必须在静态构造函数里调用 EnsureInitializedInCtor，
    ///     否则在访问字段之前，DefOf 框架可能还没初始化。
    /// </summary>
    [DefOf]
    public static class PawnMortarDefOf
    {
        /// <summary>「进入神经迫击炮」的 JobDef。</summary>
        public static JobDef EnterPawnMortar;

        /// <summary>
        /// 静态构造函数：
        /// 传入自身类型，DefOfHelper 会反射扫描所有 public static Def 字段，
        /// 并把 DefDatabase 里对应的 Def 填进去。
        /// 这一步必须在任何字段访问之前完成，所以放在 cctor 里最安全。
        /// </summary>
        static PawnMortarDefOf()
            => DefOfHelper.EnsureInitializedInCtor(typeof(PawnMortarDefOf));
    }
}
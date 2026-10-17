using Verse;

namespace PawnMortar
{
    /// <summary>
    ///调试日志开关。
    /// </summary>
    internal static class PawnMortarLog
    {
        /// <summary>总开关</summary>
        public static bool Enabled = true;

        public static void Message(string msg)
        {
            if (Enabled) Log.Message("[神经迫击炮] " + msg);
        }

        public static void Warning(string msg)
        {
            if (Enabled) Log.Warning("[神经迫击炮] " + msg);
        }
    }
}
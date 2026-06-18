using System;
using System.Windows.Forms;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Soft monthly spend budget, computed from the usage ledger. Advisory only —
    /// it warns but never blocks. Per the design, costs the user controls should
    /// not be hard-capped mid-job.
    /// </summary>
    public static class UsageBudget
    {
        /// <summary>Sum of cost_usd across all ledger records in the current UTC month.</summary>
        public static decimal MonthToDateCostUsd()
        {
            try
            {
                var now = DateTime.UtcNow;
                var from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                decimal total = 0m;
                foreach (var r in UsageReader.Load(from, now))
                    total += r.CostUsd;
                return total;
            }
            catch { return 0m; }
        }

        /// <summary>
        /// Warn-only pre-flight before a batch translation. Returns true to proceed.
        /// If a monthly budget is set and this month's logged spend already reaches
        /// it, asks the user to confirm; otherwise proceeds silently. Never blocks
        /// on its own, and any error returns true (logging must not stop work).
        /// </summary>
        public static bool Preflight(IWin32Window owner, AiSettings aiSettings, int segmentCount)
        {
            try
            {
                var budget = (decimal)(aiSettings != null ? aiSettings.MonthlyBudgetUsd : 0);
                if (budget <= 0) return true;

                var spent = MonthToDateCostUsd();
                if (spent < budget) return true;

                var msg = string.Format(
                    "You have used ${0:0.00} of your ${1:0.00} monthly AI budget.\n\n" +
                    "Start this batch of {2:N0} segment(s) anyway?",
                    spent, budget, segmentCount);

                return MessageBox.Show(owner, msg, "Monthly budget reached",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }
            catch { return true; }
        }
    }
}

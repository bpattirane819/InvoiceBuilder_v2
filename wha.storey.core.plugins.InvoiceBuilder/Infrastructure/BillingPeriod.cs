using System;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Derives the full calendar month window for the month prior to the given date.
    /// e.g. run date Apr 5 2025 → Mar 1 00:00:00.000 .. Mar 31 23:59:59.999
    /// Invoice date stays as the run date; charges are pulled from the previous month.
    /// </summary>
    public static class BillingPeriod
    {
        public static (DateTime periodStart, DateTime periodEnd) ForMonth(DateTime date)
        {
            var prev  = date.AddMonths(-1);
            var start = new DateTime(prev.Year, prev.Month, 1, 0, 0, 0, date.Kind);
            var end   = start.AddMonths(1).AddTicks(-1);
            return (start, end);
        }
    }
}

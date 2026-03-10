using System;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Derives the full calendar month window from any date within that month.
    /// e.g. Dec 15 2025 → Dec 1 00:00:00.000 .. Dec 31 23:59:59.999...
    /// </summary>
    public static class BillingPeriod
    {
        public static (DateTime periodStart, DateTime periodEnd) ForMonth(DateTime date)
        {
            var start = new DateTime(date.Year, date.Month, 1, 0, 0, 0, date.Kind);
            var end   = start.AddMonths(1).AddTicks(-1);
            return (start, end);
        }
    }
}

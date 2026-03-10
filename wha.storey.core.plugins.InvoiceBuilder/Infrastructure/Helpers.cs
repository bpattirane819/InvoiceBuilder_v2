using System;
using System.Globalization;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Currency resolution and input parameter parsing used by both plugins.
    /// </summary>
    public static class Helpers
    {
        // Fetches transactioncurrencyid directly from the invoice record.
        public static EntityReference LoadCurrencyFromInvoice(IOrganizationService svc, Guid invoiceId)
        {
            if (invoiceId == Guid.Empty) return null;
            var inv = svc.Retrieve(WHa_Invoice.EntityLogicalName, invoiceId, new ColumnSet("transactioncurrencyid"));
            return inv.GetAttributeValue<EntityReference>("transactioncurrencyid");
        }

        // Fetches the org's base currency from the organization record.
        public static EntityReference GetBaseCurrency(IOrganizationService svc)
        {
            var qe = new QueryExpression("organization") { ColumnSet = new ColumnSet("basecurrencyid"), TopCount = 1 };
            var results = svc.RetrieveMultiple(qe);
            return results.Entities.Count > 0
                ? results.Entities[0].GetAttributeValue<EntityReference>("basecurrencyid")
                : null;
        }

        // Reads an optional Guid input parameter — returns Guid.Empty if missing or unparseable.
        public static Guid ReadOptionalGuid(IPluginExecutionContext ctx, string key)
        {
            if (!ctx.InputParameters.Contains(key) || ctx.InputParameters[key] == null) return Guid.Empty;
            var raw = ctx.InputParameters[key];
            if (raw is Guid g) return g;
            if (raw is string s && Guid.TryParse(s.Trim(), out var parsed)) return parsed;
            return Guid.Empty;
        }

        // Reads a required Guid input parameter — throws if missing or empty.
        public static Guid ReadRequiredGuid(IPluginExecutionContext ctx, string key)
        {
            if (!ctx.InputParameters.Contains(key) || ctx.InputParameters[key] == null)
                throw new InvalidPluginExecutionException($"Missing required input: {key}");
            var raw = ctx.InputParameters[key];
            if (raw is Guid g && g != Guid.Empty) return g;
            if (raw is string s && Guid.TryParse(s.Trim(), out var parsed) && parsed != Guid.Empty) return parsed;
            throw new InvalidPluginExecutionException($"Input '{key}' must be a non-empty Guid. Got: {raw}");
        }

        // Reads a required DateTime input parameter — accepts DateTime or "yyyy-MM-dd" string.
        public static DateTime ReadRequiredDate(IPluginExecutionContext ctx, string key)
        {
            if (!ctx.InputParameters.Contains(key) || ctx.InputParameters[key] == null)
                throw new InvalidPluginExecutionException($"Missing required input: {key}");
            var raw = ctx.InputParameters[key];
            if (raw is DateTime dt) return dt;
            if (raw is string s && DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
            throw new InvalidPluginExecutionException($"Input '{key}' must be a DateTime or yyyy-MM-dd string. Got: {raw}");
        }
    }
}

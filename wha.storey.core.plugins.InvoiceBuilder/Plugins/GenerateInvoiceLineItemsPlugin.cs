using System;
using System.Diagnostics;
using DataverseModel;
using Microsoft.Xrm.Sdk;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Custom API — wha_BuildInvoiceDataset.
    /// Triggered by the Power Automate flow "Invoice - Trigger New Invoice" on new invoice creation, or manually via the UI button.
    /// <para>Input: wha_account_id (Guid, required), wha_invoiceid (Guid, optional),
    /// wha_run_date (DateTime|string, required), wha_transactioncurrencyid (EntityReference, optional).</para>
    /// <para>Output: wha_hello_message (string) — summary of what was done.</para>
    /// </summary>
    public sealed class GenerateInvoiceLineItemsPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var ctx     = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace   = new LocalTracingService(serviceProvider);
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc     = factory.CreateOrganizationService(ctx.UserId);

            if (!string.Equals(ctx.MessageName, "wha_BuildInvoiceDataset", StringComparison.OrdinalIgnoreCase))
                return;

            var total = Stopwatch.StartNew();

            try
            {
                var accountId = Helpers.ReadRequiredGuid(ctx, "wha_account_id");
                var runDate   = Helpers.ReadRequiredDate(ctx, "wha_run_date");

                var (periodStart, periodEnd) = BillingPeriod.ForMonth(runDate);
                trace.Trace($"Account={accountId}, Period={periodStart:yyyy-MM-dd}/{periodEnd:yyyy-MM-dd}");

                // Currency: parameter → org base currency
                var currency = ctx.InputParameters.Contains("wha_transactioncurrencyid")
                    ? ctx.InputParameters["wha_transactioncurrencyid"] as EntityReference
                    : null;
                if (currency == null)
                    currency = Helpers.GetBaseCurrency(svc);

                trace.Trace("[1] Resolving invoice...");
                var resolution = LineItemWriter.ResolveInvoice(svc, accountId, periodStart, periodEnd, runDate, currency);
                if (resolution.HadDuplicates)
                    trace.Trace($"[1] Done — duplicates resolved: {resolution.InvoicesDeleted} invoice(s) deleted, {resolution.LineItemsDeleted} line item(s) removed. Fresh invoice: {resolution.InvoiceId}");
                else
                    trace.Trace($"[1] Done — invoice: {resolution.InvoiceId}");

                trace.Trace("[2] Generating line items...");
                var lines = LineItemGenerator.Generate(svc, resolution.InvoiceId, accountId, periodStart, periodEnd, currency, trace);
                trace.Trace($"[2] Done — {lines.Count} line item(s) generated");

                trace.Trace("[3] Writing line items...");
                var result = LineItemWriter.WriteLineItems(svc, resolution.InvoiceId, lines, currency, trace);
                trace.Trace($"[3] Done — upserted: {result.Created}, orphans deleted: {result.Deleted}");

                ctx.OutputParameters["wha_hello_message"] =
                    $"Upserted {result.Created} line item(s), deleted {result.Deleted} orphan(s).";

                trace.Trace($"Total elapsed: {total.Elapsed.TotalSeconds:F2}s");
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException("wha_BuildInvoiceDataset failed. " + ex.Message, ex);
            }
        }
    }
}

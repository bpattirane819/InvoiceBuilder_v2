using System;
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
            var trace   = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc     = factory.CreateOrganizationService(ctx.UserId);

            if (!string.Equals(ctx.MessageName, "wha_BuildInvoiceDataset", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var invoiceId = Helpers.ReadOptionalGuid(ctx, "wha_invoiceid");
                var accountId = Helpers.ReadRequiredGuid(ctx, "wha_account_id");
                var runDate   = Helpers.ReadRequiredDate(ctx, "wha_run_date");

                var (periodStart, periodEnd) = BillingPeriod.ForMonth(runDate);

                // Currency: parameter → fetch from invoice record → org base currency
                var currency = ctx.InputParameters.Contains("wha_transactioncurrencyid")
                    ? ctx.InputParameters["wha_transactioncurrencyid"] as EntityReference
                    : null;
                if (currency == null)
                    currency = Helpers.LoadCurrencyFromInvoice(svc, invoiceId) ?? Helpers.GetBaseCurrency(svc);

                var dedup = LineItemWriter.DeleteDuplicateInvoices(svc, accountId, periodStart, periodEnd, invoiceId);
                if (dedup.InvoicesDeleted > 0)
                {
                    trace.Trace($"Dedup: {dedup.InvoicesDeleted} invoice(s), {dedup.LineItemsDeleted} line item(s) removed.");
                    if (!string.IsNullOrWhiteSpace(dedup.PreservedInvoiceNumber) && invoiceId != Guid.Empty)
                    {
                        var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = invoiceId };
                        update[WHa_Invoice.Fields.wha_InvoiceNumber] = dedup.PreservedInvoiceNumber;
                        svc.Update(update);
                        trace.Trace($"Restored invoice number: {dedup.PreservedInvoiceNumber}");
                    }
                }

                var lines  = LineItemGenerator.Generate(svc, invoiceId, accountId, periodStart, periodEnd, currency);
                var result = LineItemWriter.WriteLineItems(svc, invoiceId, lines, currency);

                ctx.OutputParameters["wha_hello_message"] =
                    $"Deleted {result.Deleted} old line items. Created {result.Created} invoice line items.";

                trace.Trace($"Done: deleted={result.Deleted}, created={result.Created}, invoice={invoiceId}");
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException("wha_BuildInvoiceDataset failed. " + ex.Message, ex);
            }
        }
    }
}

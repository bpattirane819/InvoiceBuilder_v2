using System;
using DataverseModel;
using Microsoft.Xrm.Sdk;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Post-Operation, Async, Message=Create, Entity=wha_invoice.
    /// Auto-fires when a new invoice is created. Generates all line items.
    /// <para>NOTE: This step should remain DISABLED in Plugin Registration Tool.
    /// The Power Automate flow "Invoice - Trigger New Invoice" handles triggering via wha_BuildInvoiceDataset instead.</para>
    /// </summary>
    public sealed class InvoiceCreatedPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var ctx     = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace   = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc     = factory.CreateOrganizationService(ctx.UserId);

            try
            {
                var target    = (Entity)ctx.InputParameters["Target"];
                var invoiceId = ctx.PrimaryEntityId;

                var accountRef = target.GetAttributeValue<EntityReference>(WHa_Invoice.Fields.wha_InvoiceFor);
                if (accountRef == null)
                    throw new InvalidPluginExecutionException("Invoice must have a customer (wha_invoicefor).");

                var invoiceDate = target.GetAttributeValue<DateTime>(WHa_Invoice.Fields.wha_InvoiceDate);
                if (invoiceDate == default)
                    throw new InvalidPluginExecutionException("Invoice must have an invoice date (wha_invoicedate).");

                var (periodStart, periodEnd) = BillingPeriod.ForMonth(invoiceDate);

                // Currency: Target entity → org base currency
                var currency = target.GetAttributeValue<EntityReference>("transactioncurrencyid")
                    ?? Helpers.GetBaseCurrency(svc);

                trace.Trace($"Account={accountRef.Id}, Period={periodStart:yyyy-MM-dd}/{periodEnd:yyyy-MM-dd}, Currency={currency?.Name}");

                var resolution = LineItemWriter.ResolveInvoice(svc, accountRef.Id, periodStart, periodEnd, invoiceDate, currency);
                if (resolution.HadDuplicates)
                    trace.Trace($"Duplicates resolved: {resolution.InvoicesDeleted} invoice(s) deleted, {resolution.LineItemsDeleted} line item(s) removed. Fresh invoice: {resolution.InvoiceId}");
                else
                    trace.Trace($"Invoice resolved: {resolution.InvoiceId}");

                var lines  = LineItemGenerator.Generate(svc, resolution.InvoiceId, accountRef.Id, periodStart, periodEnd, currency);
                trace.Trace($"Generated {lines.Count} line item(s).");

                var result = LineItemWriter.WriteLineItems(svc, resolution.InvoiceId, lines, currency);
                trace.Trace($"Written: upserted {result.Created}, orphans deleted {result.Deleted}.");
            }
            catch (InvalidPluginExecutionException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException(
                    $"InvoiceCreatedPlugin failed. Invoice={ctx.PrimaryEntityId}. {ex.Message}", ex);
            }
        }
    }
}

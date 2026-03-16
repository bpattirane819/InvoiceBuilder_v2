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

                // Currency: Target entity → fetch from invoice record → org base currency
                var currency = target.GetAttributeValue<EntityReference>("transactioncurrencyid")
                    ?? Helpers.LoadCurrencyFromInvoice(svc, invoiceId)
                    ?? Helpers.GetBaseCurrency(svc);

                trace.Trace($"Account={accountRef.Id}, Period={periodStart:yyyy-MM-dd}/{periodEnd:yyyy-MM-dd}, Currency={currency?.Name}");

                var dedup = LineItemWriter.DeleteDuplicateInvoices(svc, accountRef.Id, periodStart, periodEnd, invoiceId);
                trace.Trace($"Dedup: {dedup.InvoicesDeleted} invoice(s), {dedup.LineItemsDeleted} line item(s) removed.");
                if (!string.IsNullOrWhiteSpace(dedup.PreservedInvoiceNumber))
                {
                    var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = invoiceId };
                    update[WHa_Invoice.Fields.wha_InvoiceNumber] = dedup.PreservedInvoiceNumber;
                    svc.Update(update);
                    trace.Trace($"Restored invoice number: {dedup.PreservedInvoiceNumber}");
                }

                var lines  = LineItemGenerator.Generate(svc, invoiceId, accountRef.Id, periodStart, periodEnd, currency);
                trace.Trace($"Generated {lines.Count} line item(s).");

                var result = LineItemWriter.WriteLineItems(svc, invoiceId, lines, currency);
                trace.Trace($"Written: cleared {result.Deleted}, created {result.Created}.");
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

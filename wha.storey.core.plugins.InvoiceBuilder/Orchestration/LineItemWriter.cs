using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Handles idempotent write of invoice line items to Dataverse.
    /// Creates and deletes via ExecuteMultiple (batch 1000) — 1 round trip each.
    /// CalculateInvoiceTotalPlugin is suppressed during ExecuteMultiple (by design);
    /// UpdateInvoiceTotal() stamps the correct total directly after batch create.
    /// </summary>
    public static class LineItemWriter
    {
        private const int BatchSize = 1000;

        // Deletes existing line items for the invoice, then batch-creates new ones. Stamps the invoice total directly after creation.
        public static WriteResult WriteLineItems(
            IOrganizationService svc,
            Guid invoiceId,
            IReadOnlyList<WHa_InvoiceLineItem> lineItems,
            EntityReference currency = null)
        {
            int deleted = invoiceId != Guid.Empty ? DeleteExistingLineItems(svc, invoiceId) : 0;

            var entities = new List<Entity>(lineItems.Count);
            foreach (var li in lineItems)
            {
                li.wha_Quantity = 1;
                if (currency != null) li.TransactionCurrencyId = currency;
                entities.Add(ToLateBound(li));
            }

            var createdIds = BatchCreate(svc, entities);

            // CalculateInvoiceTotalPlugin is suppressed during ExecuteMultiple —
            // stamp the total directly so the invoice always reflects the correct amount.
            if (invoiceId != Guid.Empty)
                UpdateInvoiceTotal(svc, invoiceId, lineItems);

            return new WriteResult { Deleted = deleted, Created = createdIds.Count, CreatedIds = createdIds };
        }

        // Deletes all existing line items for the given invoice, paged in batches of 2500.
        public static int DeleteExistingLineItems(IOrganizationService svc, Guid invoiceId)
        {
            if (invoiceId == Guid.Empty) return 0;

            var totalDeleted = 0;
            var qe = new QueryExpression(WHa_InvoiceLineItem.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                Criteria  = new FilterExpression { Conditions = { new ConditionExpression("wha_invoiceid", ConditionOperator.Equal, invoiceId) } },
                PageInfo  = new PagingInfo { PageNumber = 1, Count = 2500, ReturnTotalRecordCount = false }
            };

            while (true)
            {
                var page = svc.RetrieveMultiple(qe);
                if (page?.Entities == null || page.Entities.Count == 0) break;
                totalDeleted += BatchDelete(svc, WHa_InvoiceLineItem.EntityLogicalName, page.Entities);
                if (!page.MoreRecords) break;
                qe.PageInfo.PageNumber++;
                qe.PageInfo.PagingCookie = page.PagingCookie;
            }

            return totalDeleted;
        }

        /// <summary>
        /// Resolves the correct invoice to use for line item generation.
        /// <para>0 found: creates a new invoice.</para>
        /// <para>1 found: keeps it, clears its line items only — invoice number preserved naturally.</para>
        /// <para>2+ found: preserves the oldest invoice number, deletes all, creates a fresh invoice with the preserved number.</para>
        /// </summary>
        public static InvoiceResolution ResolveInvoice(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd,
            DateTime invoiceDate,
            EntityReference currency)
        {
            var qe = new QueryExpression(WHa_Invoice.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(WHa_Invoice.Fields.wha_InvoiceId, WHa_Invoice.Fields.wha_InvoiceNumber)
            };
            qe.Criteria.AddCondition(WHa_Invoice.Fields.wha_InvoiceFor,  ConditionOperator.Equal,        accountId);
            qe.Criteria.AddCondition(WHa_Invoice.Fields.wha_InvoiceDate, ConditionOperator.GreaterEqual, periodStart);
            qe.Criteria.AddCondition(WHa_Invoice.Fields.wha_InvoiceDate, ConditionOperator.LessEqual,    periodEnd);
            qe.Orders.Add(new OrderExpression(WHa_Invoice.Fields.CreatedOn, OrderType.Ascending));

            var existing = svc.RetrieveMultiple(qe);

            if (existing.Entities.Count == 1)
            {
                // Clean path: one invoice exists — keep it, clear line items only
                var invoiceId    = existing.Entities[0].Id;
                var lineItemsDel = DeleteExistingLineItems(svc, invoiceId);
                return new InvoiceResolution { InvoiceId = invoiceId, LineItemsDeleted = lineItemsDel };
            }

            if (existing.Entities.Count > 1)
            {
                // Dirty path: duplicates exist — preserve oldest number, delete all, create fresh
                var preservedNumber  = existing.Entities[0].GetAttributeValue<string>(WHa_Invoice.Fields.wha_InvoiceNumber);
                var lineItemsDeleted = 0;
                foreach (var inv in existing.Entities)
                    lineItemsDeleted += DeleteExistingLineItems(svc, inv.Id);

                var batch = new OrganizationRequestCollection();
                foreach (var inv in existing.Entities)
                    batch.Add(new DeleteRequest { Target = new EntityReference(WHa_Invoice.EntityLogicalName, inv.Id) });

                svc.Execute(new ExecuteMultipleRequest
                {
                    Requests = batch,
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = false }
                });

                var newId = CreateInvoice(svc, accountId, invoiceDate, currency, preservedNumber);
                return new InvoiceResolution
                {
                    InvoiceId        = newId,
                    HadDuplicates    = true,
                    InvoicesDeleted  = existing.Entities.Count,
                    LineItemsDeleted = lineItemsDeleted
                };
            }

            // No invoices found — create fresh
            var freshId = CreateInvoice(svc, accountId, invoiceDate, currency, null);
            return new InvoiceResolution { InvoiceId = freshId };
        }

        private static Guid CreateInvoice(
            IOrganizationService svc,
            Guid accountId,
            DateTime invoiceDate,
            EntityReference currency,
            string invoiceNumber)
        {
            var invoice = new Entity(WHa_Invoice.EntityLogicalName);
            invoice[WHa_Invoice.Fields.wha_InvoiceFor]  = new EntityReference("account", accountId);
            invoice[WHa_Invoice.Fields.wha_InvoiceDate] = invoiceDate;
            if (currency != null) invoice["transactioncurrencyid"] = currency;

            var id = svc.Create(invoice);

            if (!string.IsNullOrWhiteSpace(invoiceNumber))
            {
                var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = id };
                update[WHa_Invoice.Fields.wha_InvoiceNumber] = invoiceNumber;
                svc.Update(update);
            }

            return id;
        }

        /// <summary>Calculates the invoice total from the line items and stamps it on the invoice.
        /// Called after BatchCreate since CalculateInvoiceTotalPlugin is suppressed during ExecuteMultiple
        /// to avoid O(n²) queries and lock contention on large batches.</summary>
        private static void UpdateInvoiceTotal(
            IOrganizationService svc,
            Guid invoiceId,
            IReadOnlyList<WHa_InvoiceLineItem> lineItems)
        {
            decimal total = 0m;
            foreach (var li in lineItems)
            {
                var amt = li.wha_totallineitemamount?.Value ?? 0m;
                if (string.Equals(li.wha_SourceId?.LogicalName, "wha_discount", StringComparison.OrdinalIgnoreCase))
                    total -= amt;
                else
                    total += amt;
            }

            var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = invoiceId };
            update["wha_totalamount"] = new Money(total);
            svc.Update(update);
        }

        private static List<Guid> BatchCreate(IOrganizationService svc, IList<Entity> entities)
        {
            var createdIds = new List<Guid>(entities.Count);

            for (int i = 0; i < entities.Count; i += BatchSize)
            {
                var batch = new OrganizationRequestCollection();
                for (int j = i; j < entities.Count && j < i + BatchSize; j++)
                    batch.Add(new CreateRequest { Target = entities[j] });

                var response = (ExecuteMultipleResponse)svc.Execute(new ExecuteMultipleRequest
                {
                    Requests = batch,
                    Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = true }
                });

                foreach (var r in response.Responses)
                    if (r.Response is CreateResponse cr) createdIds.Add(cr.id);
            }

            return createdIds;
        }

        private static int BatchDelete(IOrganizationService svc, string logicalName, IList<Entity> entities)
        {
            for (int i = 0; i < entities.Count; i += BatchSize)
            {
                var batch = new OrganizationRequestCollection();
                for (int j = i; j < entities.Count && j < i + BatchSize; j++)
                    batch.Add(new DeleteRequest { Target = new EntityReference(logicalName, entities[j].Id) });

                svc.Execute(new ExecuteMultipleRequest
                {
                    Requests = batch,
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = false }
                });
            }

            return entities.Count;
        }

        private static Entity ToLateBound(Entity earlyBound)
        {
            var e = new Entity(earlyBound.LogicalName);
            foreach (var kvp in earlyBound.Attributes)
                e[kvp.Key] = kvp.Value;
            return e;
        }
    }
}

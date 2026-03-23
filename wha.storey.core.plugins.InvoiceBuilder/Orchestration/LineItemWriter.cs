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
    /// UpdateInvoiceTotal() stamps the correct total directly after batch create.
    /// </summary>
    public static class LineItemWriter
    {
        private const int BatchSize = 1000;

        // Upserts line items by alternate key (wha_lineitemkey), deletes orphans, then stamps the invoice total.
        public static WriteResult WriteLineItems(
            IOrganizationService svc,
            Guid invoiceId,
            IReadOnlyList<WHa_InvoiceLineItem> lineItems,
            EntityReference currency = null,
            ITracingService trace = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Snapshot existing keys before upserting so we can detect orphans afterward
            var existingKeys = invoiceId != Guid.Empty
                ? GetExistingKeys(svc, invoiceId)
                : new Dictionary<string, Guid>();

            var entities    = new List<Entity>(lineItems.Count);
            var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var li in lineItems)
            {
                li.wha_Quantity = 1;
                if (currency != null) li.TransactionCurrencyId = currency;
                entities.Add(ToLateBoundForUpsert(li));
                if (!string.IsNullOrWhiteSpace(li.wha_LineItemKey))
                    currentKeys.Add(li.wha_LineItemKey);
            }

            bool isNewInvoice = existingKeys.Count == 0;
            if (isNewInvoice)
            {
                trace?.Trace($"[3a] Creating {lineItems.Count} line items (new invoice)...");
                sw.Restart();
                BatchCreate(svc, lineItems);
                trace?.Trace($"[3a] Done — {lineItems.Count} created in {sw.Elapsed.TotalSeconds:F2}s");
            }
            else
            {
                trace?.Trace($"[3a] Upserting {entities.Count} line items...");
                sw.Restart();
                BatchUpsert(svc, entities);
                trace?.Trace($"[3a] Done — {entities.Count} upserted in {sw.Elapsed.TotalSeconds:F2}s");
            }

            // Delete line items that existed before but are no longer in the current batch
            var orphanIds = new List<Guid>();
            foreach (var kvp in existingKeys)
                if (!currentKeys.Contains(kvp.Key))
                    orphanIds.Add(kvp.Value);

            // Also delete any pre-upsert records that have no wha_lineitemkey (NULL key = created before upsert was implemented)
            if (invoiceId != Guid.Empty)
            {
                var keylessIds = GetKeylessLineItemIds(svc, invoiceId);
                foreach (var id in keylessIds)
                    orphanIds.Add(id);
            }

            int deleted = 0;
            if (orphanIds.Count > 0)
            {
                trace?.Trace($"[3b] Deleting {orphanIds.Count} orphaned line item(s)...");
                sw.Restart();
                BatchDeleteById(svc, WHa_InvoiceLineItem.EntityLogicalName, orphanIds);
                deleted = orphanIds.Count;
                trace?.Trace($"[3b] Done in {sw.Elapsed.TotalSeconds:F2}s");
            }

            trace?.Trace("[3c] Updating invoice total...");
            sw.Restart();
            if (invoiceId != Guid.Empty)
                UpdateInvoiceTotal(svc, invoiceId, lineItems);
            trace?.Trace($"[3c] Done in {sw.Elapsed.TotalSeconds:F2}s");

            return new WriteResult { Created = entities.Count, Deleted = deleted };
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

            bool moreRecords;
            do
            {
                var page = svc.RetrieveMultiple(qe);
                if (page?.Entities == null || page.Entities.Count == 0) break;
                totalDeleted += BatchDelete(svc, WHa_InvoiceLineItem.EntityLogicalName, page.Entities);
                moreRecords = page.MoreRecords;
                qe.PageInfo.PageNumber++;
                qe.PageInfo.PagingCookie = page.PagingCookie;
            } while (moreRecords);

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
            EntityReference currency,
            ITracingService trace = null)
        {
            // Look for existing invoices by run month (invoice date), not billing period.
            // Invoice date = run date (e.g. Apr 5); billing period = previous month (Mar 1–31).
            var runMonthStart = new DateTime(invoiceDate.Year, invoiceDate.Month, 1, 0, 0, 0, invoiceDate.Kind);
            var runMonthEnd   = runMonthStart.AddMonths(1).AddTicks(-1);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var qe = new QueryExpression(WHa_Invoice.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(WHa_Invoice.Fields.wha_InvoiceId, WHa_Invoice.Fields.wha_InvoiceNumber)
            };
            qe.Criteria.AddCondition(WHa_Invoice.Fields.wha_InvoiceFor,  ConditionOperator.Equal,        accountId);
            qe.Criteria.AddCondition(WHa_Invoice.Fields.wha_InvoiceDate, ConditionOperator.GreaterEqual, runMonthStart);
            qe.Criteria.AddCondition(WHa_Invoice.Fields.wha_InvoiceDate, ConditionOperator.LessEqual,    runMonthEnd);
            qe.Orders.Add(new OrderExpression(WHa_Invoice.Fields.CreatedOn, OrderType.Ascending));

            var existing = svc.RetrieveMultiple(qe);
            trace?.Trace($"[1a] Invoice query: {existing.Entities.Count} found in {sw.Elapsed.TotalSeconds:F2}s");

            if (existing.Entities.Count == 1)
            {
                // Clean path: one invoice exists — keep it, update invoice date.
                // No upfront delete — upsert handles in-place updates, orphan cleanup handles removals.
                var invoiceId = existing.Entities[0].Id;

                var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = invoiceId };
                update[WHa_Invoice.Fields.wha_InvoiceDate] = invoiceDate;
                var cleanDueDate = GetDueDate(svc, accountId, invoiceDate);
                if (cleanDueDate.HasValue) update[WHa_Invoice.Fields.wha_DueDate] = cleanDueDate.Value;
                svc.Update(update);

                return new InvoiceResolution { InvoiceId = invoiceId };
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

            var dueDate = GetDueDate(svc, accountId, invoiceDate);
            if (dueDate.HasValue) invoice[WHa_Invoice.Fields.wha_DueDate] = dueDate.Value;

            var id = svc.Create(invoice);

            if (!string.IsNullOrWhiteSpace(invoiceNumber))
            {
                var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = id };
                update[WHa_Invoice.Fields.wha_InvoiceNumber] = invoiceNumber;
                svc.Update(update);
            }

            return id;
        }

        private static DateTime? GetDueDate(IOrganizationService svc, Guid accountId, DateTime invoiceDate)
        {
            var account = svc.Retrieve(Account.EntityLogicalName, accountId, new ColumnSet(Account.Fields.PaymentTermsCode));
            var terms   = account.GetAttributeValue<OptionSetValue>(Account.Fields.PaymentTermsCode);
            if (terms == null) return null;

            switch (terms.Value)
            {
                case 1: return invoiceDate.AddDays(15);
                case 2: return invoiceDate.AddDays(30);
                case 3: return invoiceDate.AddDays(45);
                case 4: return invoiceDate.AddDays(60);
                default: return null;
            }
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
                if (string.Equals(li.wha_SourceId?.LogicalName, "wha_discount", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(li.wha_SourceId?.LogicalName, "wha_credit",   StringComparison.OrdinalIgnoreCase))
                    total -= amt;
                else
                    total += amt;
            }

            var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = invoiceId };
            update["wha_totalamount"] = new Money(total);
            svc.Update(update);
        }
        //Cleans up old data that didn't have wha_lineitemkey populated (pre-upsert implementation) to prevent duplicates from accumulating until next upsert run.
        private static List<Guid> GetKeylessLineItemIds(IOrganizationService svc, Guid invoiceId)
        {
            var qe = new QueryExpression(WHa_InvoiceLineItem.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                Criteria  = new FilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new ConditionExpression("wha_invoiceid",    ConditionOperator.Equal, invoiceId),
                        new ConditionExpression("wha_lineitemkey",  ConditionOperator.Null)
                    }
                }
            };

            var results = svc.RetrieveMultiple(qe);
            var ids     = new List<Guid>(results.Entities.Count);
            foreach (var e in results.Entities)
                ids.Add(e.Id);
            return ids;
        }

        private static Dictionary<string, Guid> GetExistingKeys(IOrganizationService svc, Guid invoiceId)
        {
            var qe = new QueryExpression(WHa_InvoiceLineItem.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(WHa_InvoiceLineItem.Fields.wha_LineItemKey),
                Criteria  = new FilterExpression
                {
                    Conditions = { new ConditionExpression("wha_invoiceid", ConditionOperator.Equal, invoiceId) }
                }
            };

            var results = svc.RetrieveMultiple(qe);
            var dict    = new Dictionary<string, Guid>(results.Entities.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var e in results.Entities)
            {
                var key = e.GetAttributeValue<string>(WHa_InvoiceLineItem.Fields.wha_LineItemKey);
                if (!string.IsNullOrWhiteSpace(key))
                    dict[key] = e.Id;
            }
            return dict;
        }

        private static void BatchCreate(IOrganizationService svc, IReadOnlyList<WHa_InvoiceLineItem> lineItems)
        {
            for (int i = 0; i < lineItems.Count; i += BatchSize)
            {
                var batch = new OrganizationRequestCollection();
                for (int j = i; j < lineItems.Count && j < i + BatchSize; j++)
                    batch.Add(new CreateRequest { Target = lineItems[j] });

                svc.Execute(new ExecuteMultipleRequest
                {
                    Requests = batch,
                    Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = false }
                });
            }
        }

        private static void BatchUpsert(IOrganizationService svc, IList<Entity> entities)
        {
            for (int i = 0; i < entities.Count; i += BatchSize)
            {
                var batch = new OrganizationRequestCollection();
                for (int j = i; j < entities.Count && j < i + BatchSize; j++)
                    batch.Add(new UpsertRequest { Target = entities[j] });

                svc.Execute(new ExecuteMultipleRequest
                {
                    Requests = batch,
                    Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = false }
                });
            }
        }

        private static void BatchDeleteById(IOrganizationService svc, string logicalName, IList<Guid> ids)
        {
            for (int i = 0; i < ids.Count; i += BatchSize)
            {
                var batch = new OrganizationRequestCollection();
                for (int j = i; j < ids.Count && j < i + BatchSize; j++)
                    batch.Add(new DeleteRequest { Target = new EntityReference(logicalName, ids[j]) });

                svc.Execute(new ExecuteMultipleRequest
                {
                    Requests = batch,
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = false }
                });
            }
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

        private static Entity ToLateBoundForUpsert(Entity earlyBound)
        {
            var keyValue = earlyBound.GetAttributeValue<string>(WHa_InvoiceLineItem.Fields.wha_LineItemKey);
            var e = string.IsNullOrWhiteSpace(keyValue)
                ? new Entity(earlyBound.LogicalName)
                : new Entity(earlyBound.LogicalName, WHa_InvoiceLineItem.Fields.wha_LineItemKey, keyValue);
            foreach (var kvp in earlyBound.Attributes)
                e[kvp.Key] = kvp.Value;
            return e;
        }
    }
}

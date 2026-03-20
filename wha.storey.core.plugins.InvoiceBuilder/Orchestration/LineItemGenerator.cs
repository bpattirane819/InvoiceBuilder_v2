using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Runs all 3 queries and builds WHa_InvoiceLineItem records ready to write.
    /// Total Dataverse round trips: 4 (1 space pre-query + 1 fee + 1 discount + 1 rent),
    /// regardless of how many spaces or charges the customer has.
    /// </summary>
    public static class LineItemGenerator
    {
        public static IReadOnlyList<WHa_InvoiceLineItem> Generate(
            IOrganizationService svc,
            Guid invoiceId,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd,
            EntityReference currency)
        {
            var items = new List<WHa_InvoiceLineItem>();

            // 1. Fees
            foreach (var fee in FeeQuery.GetFees(svc, accountId, periodStart, periodEnd))
            {
                if (fee.FeeId == Guid.Empty) continue;
                items.Add(Build(invoiceId, currency,
                    WHa_Fee.EntityLogicalName, fee.FeeId, BuildFeeName(fee.FeeName, fee.IsSpaceLevel, fee.SpaceUnitName),
                    fee.Amount, fee.SpaceName, fee.SpaceUnitName));
            }

            // 2. Discounts (stored as positive — subtracted at reporting time)
            foreach (var d in DiscountQuery.GetDiscounts(svc, accountId, periodStart, periodEnd))
            {
                if (d.DiscountId == Guid.Empty) continue;
                items.Add(Build(invoiceId, currency,
                    WHa_Discount.EntityLogicalName, d.DiscountId, d.Name,
                    d.Amount, d.SpaceName, d.SpaceUnitName));
            }

            // 3. Credits (account-level, subtracted at reporting time)
            foreach (var credit in CreditQuery.GetCredits(svc, accountId, periodStart, periodEnd))
            {
                if (credit.CreditId == Guid.Empty) continue;
                items.Add(Build(invoiceId, currency,
                    WHa_Credit.EntityLogicalName, credit.CreditId, credit.Name,
                    credit.Amount, null, null));
            }

            // 4. Rents
            foreach (var rent in RentQuery.GetRents(svc, accountId, periodStart, periodEnd))
            {
                if (rent.RentId == Guid.Empty) continue;
                items.Add(Build(invoiceId, currency,
                    WHa_Rent.EntityLogicalName, rent.RentId, BuildName(rent.FacilityName, rent.UnitName),
                    rent.Amount, rent.SpaceName, rent.UnitName));
            }

            return items;
        }

        private static WHa_InvoiceLineItem Build(
            Guid invoiceId,
            EntityReference currency,
            string sourceLogicalName,
            Guid sourceId,
            string displayName,
            decimal amount,
            string spaceName,
            string spaceUnit)
        {
            var sourceType = DeriveSourceType(sourceLogicalName);
            var li = new WHa_InvoiceLineItem
            {
                wha_invoiceid           = new EntityReference(WHa_Invoice.EntityLogicalName, invoiceId),
                wha_SourceId            = new EntityReference(sourceLogicalName, sourceId),
                wha_Quantity            = 1,
                wha_UnitPrice           = new Money(amount),
                wha_totallineitemamount = new Money(amount),
                wha_InvoiceLineItemName = displayName,
                wha_SourceType          = sourceType,
                wha_LineItemKey         = $"{invoiceId}|{sourceLogicalName}|{sourceId}|{sourceType}"
            };

            if (!string.IsNullOrWhiteSpace(spaceName)) li.wha_SpaceName   = spaceName;
            if (!string.IsNullOrWhiteSpace(spaceUnit)) li.wha_SpaceNumber = spaceUnit;
            if (currency != null)                      li.TransactionCurrencyId = currency;

            return li;
        }

        private static string BuildFeeName(string feeName, bool isSpaceLevel, string unitName)
        {
            var name = string.IsNullOrWhiteSpace(feeName) ? "(unnamed)" : feeName.Trim();
            if (!isSpaceLevel)                              return $"{name} - Account Level";
            if (!string.IsNullOrWhiteSpace(unitName))      return $"{name} - {unitName.Trim()}";
            return $"{name} - Space Level";
        }

        private static string BuildName(string facilityName, string unitName)
        {
            var facility = string.IsNullOrWhiteSpace(facilityName) ? null : facilityName.Trim();
            var unit     = string.IsNullOrWhiteSpace(unitName)     ? null : unitName.Trim();

            if (facility != null && unit != null) return $"{facility} - {unit}";
            if (facility != null)                 return facility;
            if (unit != null)                     return unit;
            return "(unnamed)";
        }

        private static string DeriveSourceType(string logicalName)
        {
            if (string.Equals(logicalName, WHa_Rent.EntityLogicalName,     StringComparison.OrdinalIgnoreCase)) return "Rent";
            if (string.Equals(logicalName, WHa_Fee.EntityLogicalName,      StringComparison.OrdinalIgnoreCase)) return "Fee";
            if (string.Equals(logicalName, WHa_Discount.EntityLogicalName, StringComparison.OrdinalIgnoreCase)) return "Discount";
            if (string.Equals(logicalName, WHa_Credit.EntityLogicalName,   StringComparison.OrdinalIgnoreCase)) return "Credit";
            return logicalName;
        }
    }
}

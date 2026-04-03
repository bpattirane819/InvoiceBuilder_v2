using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Runs all queries and builds WHa_InvoiceLineItem records ready to write.
    /// Total Dataverse round trips: 5 (1 fee recurring + 1 fee one-time + 1 fee account +
    /// 1 discount + 1 credit + 1 rent + 1 tax zip codes).
    /// </summary>
    public static class LineItemGenerator
    {
        public static IReadOnlyList<WHa_InvoiceLineItem> Generate(
            IOrganizationService svc,
            Guid invoiceId,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd,
            EntityReference currency,
            ITracingService trace = null)
        {
            var items = new List<WHa_InvoiceLineItem>();

            // 1. Rents
            var rents = RentQuery.GetRents(svc, accountId, periodStart, periodEnd);
            foreach (var rent in rents)
            {
                if (rent.RentId == Guid.Empty) continue;
                items.Add(Build(invoiceId, currency,
                    WHa_Rent.EntityLogicalName, rent.RentId, BuildName(rent.FacilityName, rent.UnitName),
                    rent.Amount, rent.SpaceName, rent.UnitName));
            }

            // 2. Fees
            // Load fees after rents so percentage-of-rent fees can be converted to actual amounts
            var fees = FeeQuery.GetFees(svc, accountId, periodStart, periodEnd);
            // index rents by space for quick lookup when calculating percentage fees
            var rentBySpace = new Dictionary<Guid, RentCharge>();
            foreach (var r in rents)
                if (r.SpaceId != Guid.Empty)
                    rentBySpace[r.SpaceId] = r;

            foreach (var fee in fees)
            {
                if (fee.FeeId == Guid.Empty) continue;

                decimal amountToUse = fee.Amount;
                // If this fee is defined as a percentage of rent and applies at space level,
                // compute the actual dollar amount using the space's rent.
                if (amountToUse == 0m && fee.PercentageOfRent > 0m && fee.IsSpaceLevel && fee.SpaceId != Guid.Empty)
                {
                    if (rentBySpace.TryGetValue(fee.SpaceId, out var rentForPct))
                    {
                        // PercentageOfRent is stored as a decimal fraction (e.g. 0.05 for 5%).
                        var computed = fee.PercentageOfRent * rentForPct.Amount;
                        amountToUse = Math.Round(computed, 2, MidpointRounding.AwayFromZero);
                        // Persist computed amount back to the model so downstream logic sees it
                        fee.Amount = amountToUse;

                        // Update the fee record in Dataverse so plugins can read the computed amount
                        var feeUpdate = new Entity(WHa_Fee.EntityLogicalName) { Id = fee.FeeId };
                        feeUpdate[WHa_Fee.Fields.wha_Amount] = new Money(amountToUse);
                        svc.Update(feeUpdate);

                        if (amountToUse == 0m)
                            trace?.Trace($"[Fee] Computed percentage fee {fee.FeeId} for space {fee.SpaceId} produced 0.00 (pct={fee.PercentageOfRent}, rent={rentForPct.Amount})");
                        else
                            trace?.Trace($"[Fee] Updated percentage fee {fee.FeeId} to {amountToUse:C} (pct={fee.PercentageOfRent}, rent={rentForPct.Amount})");
                    }
                    else
                    {
                        trace?.Trace($"[Fee] Percentage fee {fee.FeeId} references space {fee.SpaceId} but no rent was found — amount will be 0.00");
                    }
                }

                items.Add(Build(invoiceId, currency,
                    WHa_Fee.EntityLogicalName, fee.FeeId, BuildFeeName(fee.FeeName, fee.IsSpaceLevel, fee.SpaceUnitName),
                    amountToUse, fee.SpaceName, fee.SpaceUnitName));
            }

            // 3. Discounts (stored as positive — subtracted at reporting time)
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



            // 5. Taxes — 1 line item per taxable space
            var taxRates = TaxQuery.GetTaxRateByZipCode(svc);
            trace?.Trace($"[Tax] Taxable zip codes loaded: {taxRates.Count}");

            if (taxRates.Count > 0)
            {
                var taxItems = BuildTaxLineItems(invoiceId, currency, rents, fees, taxRates, trace);
                trace?.Trace($"[Tax] Tax line items generated: {taxItems.Count}");
                items.AddRange(taxItems);
            }
            else
            {
                trace?.Trace("[Tax] Skipped — no taxable zip codes found in wha_taxzipcodes");
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

        private static IReadOnlyList<WHa_InvoiceLineItem> BuildTaxLineItems(
            Guid invoiceId,
            EntityReference currency,
            IReadOnlyList<RentCharge> rents,
            IReadOnlyList<FeeCharge> fees,
            Dictionary<string, decimal> taxRates,
            ITracingService trace = null)
        {
            // Index rents by SpaceId — only spaces with a known zip can be taxed
            var rentBySpace = new Dictionary<Guid, RentCharge>();
            foreach (var rent in rents)
            {
                if (rent.SpaceId != Guid.Empty && !string.IsNullOrWhiteSpace(rent.FacilityZipCode))
                    rentBySpace[rent.SpaceId] = rent;
                else
                    trace?.Trace($"[Tax] Skipping space {rent.SpaceId} — no facility zip code (facility may be missing zip)");
            }

            // Sum qualifying fee amounts per space.
            // Fixed fees: use Amount directly.
            // Percentage-of-rent fees: compute against the space's rent amount.
            var taxableFeesBySpace = new Dictionary<Guid, decimal>();
            foreach (var fee in fees)
            {
                if (!fee.IsSpaceLevel || fee.SpaceId == Guid.Empty) continue;

                decimal feeAmount;
                if (fee.Amount > 0m)
                {
                    feeAmount = fee.Amount;
                }
                else if (fee.PercentageOfRent > 0m && rentBySpace.TryGetValue(fee.SpaceId, out var rentForPct))
                {
                    feeAmount = fee.PercentageOfRent * rentForPct.Amount;
                }
                else continue;

                if (taxableFeesBySpace.ContainsKey(fee.SpaceId))
                    taxableFeesBySpace[fee.SpaceId] += feeAmount;
                else
                    taxableFeesBySpace[fee.SpaceId] = feeAmount;
            }

            var taxItems = new List<WHa_InvoiceLineItem>();
            foreach (var rent in rentBySpace.Values)
            {
                if (!taxRates.TryGetValue(rent.FacilityZipCode.Trim(), out var rate) || rate == 0m)
                {
                    trace?.Trace($"[Tax] {BuildName(rent.FacilityName, rent.UnitName)} — zip '{rent.FacilityZipCode}' not in taxable zip codes, skipping");
                    continue;
                }

                taxableFeesBySpace.TryGetValue(rent.SpaceId, out var feesBase);
                var taxBase   = rent.Amount + feesBase;
                var taxAmount = Math.Round(taxBase * (rate / 100m), 2, MidpointRounding.AwayFromZero);
                if (taxAmount == 0m) continue;

                var name = $"Sales Tax - {BuildName(rent.FacilityName, rent.UnitName)}";

                var li = new WHa_InvoiceLineItem
                {
                    wha_invoiceid           = new EntityReference(WHa_Invoice.EntityLogicalName, invoiceId),
                    wha_SourceId            = new EntityReference(WHa_Rent.EntityLogicalName, rent.RentId),
                    wha_Quantity            = 1,
                    wha_UnitPrice           = new Money(taxAmount),
                    wha_totallineitemamount = new Money(taxAmount),
                    wha_InvoiceLineItemName = name,
                    wha_SourceType          = "Tax",
                    wha_LineItemKey         = $"{invoiceId}|{WHa_Rent.EntityLogicalName}|{rent.RentId}|Tax"
                };

                if (!string.IsNullOrWhiteSpace(rent.SpaceName)) li.wha_SpaceName   = rent.SpaceName;
                if (!string.IsNullOrWhiteSpace(rent.UnitName))  li.wha_SpaceNumber = rent.UnitName;
                if (currency != null)                           li.TransactionCurrencyId = currency;

                taxItems.Add(li);
            }

            return taxItems;
        }
    }
}

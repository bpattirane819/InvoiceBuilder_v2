using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Loads the taxable zip code → total sales tax rate map from wha_taxzipcodes.
    /// Only zip codes whose wha_state is in the hardcoded taxable states set are included.
    /// One Dataverse round trip.
    /// </summary>
    internal static class TaxQuery
    {
        private static readonly HashSet<string> TaxableStates = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "CT", "FL", "HI", "IA", "LA", "NJ", "NM", "OH", "PA", "WV"
        };

        /// <summary>
        /// Returns a map of zip code (trimmed, upper) → wha_totalsalestax rate (as a decimal, e.g. 0.07 for 7%).
        /// </summary>
        public static Dictionary<string, decimal> GetTaxRateByZipCode(IOrganizationService svc)
        {
            var qe = new QueryExpression(WHa_TaxZipCodes.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_TaxZipCodes.Fields.wha_Zipcode,
                    WHa_TaxZipCodes.Fields.wha_State,
                    WHa_TaxZipCodes.Fields.wha_TotalSalesTax),
                PageInfo = new PagingInfo { PageNumber = 1, Count = 5000, ReturnTotalRecordCount = false }
            };

            var map = new Dictionary<string, decimal>(System.StringComparer.OrdinalIgnoreCase);

            bool moreRecords;
            do
            {
                var page = svc.RetrieveMultiple(qe);
                if (page?.Entities == null || page.Entities.Count == 0) break;

                foreach (var e in page.Entities)
                {
                    var state = e.GetAttributeValue<string>(WHa_TaxZipCodes.Fields.wha_State);
                    if (string.IsNullOrWhiteSpace(state) || !TaxableStates.Contains(state.Trim()))
                        continue;

                    var zip = e.GetAttributeValue<string>(WHa_TaxZipCodes.Fields.wha_Zipcode);
                    if (string.IsNullOrWhiteSpace(zip)) continue;

                    var rate = e.GetAttributeValue<decimal?>(WHa_TaxZipCodes.Fields.wha_TotalSalesTax) ?? 0m;
                    if (rate == 0m) continue;

                    map[zip.Trim()] = rate;
                }

                moreRecords = page.MoreRecords;
                qe.PageInfo.PageNumber++;
                qe.PageInfo.PagingCookie = page.PagingCookie;
            }
            while (moreRecords);

            return map;
        }
    }
}

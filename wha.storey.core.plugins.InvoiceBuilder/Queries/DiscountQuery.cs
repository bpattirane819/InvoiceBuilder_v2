using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Fetches discounts for the account or its spaces.
    /// <para>Date overlap: StartDate &lt;= periodEnd AND (EndDate IS NULL OR EndDate &gt;= periodStart).</para>
    /// <para>Scope: DiscountForId = accountId OR space.wha_RentedBy = accountId (via LEFT JOIN).</para>
    /// <para>Amounts are positive. Grand total = SUM(Rents) + SUM(Fees) - SUM(Discounts).</para>
    /// </summary>
    internal static class DiscountQuery
    {
        public static IReadOnlyList<DiscountCharge> GetDiscounts(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var qe = new QueryExpression(WHa_Discount.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Discount.Fields.wha_DiscountId,
                    WHa_Discount.Fields.wha_Name,
                    WHa_Discount.Fields.wha_Amount,
                    WHa_Discount.Fields.wha_DiscountForId,
                    WHa_Discount.Fields.wha_StartDate,
                    WHa_Discount.Fields.wha_EndDate),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            // Date overlap
            qe.Criteria.AddCondition(WHa_Discount.Fields.wha_StartDate, ConditionOperator.LessEqual, periodEnd);
            qe.Criteria.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Discount.Fields.wha_EndDate, ConditionOperator.Null),
                    new ConditionExpression(WHa_Discount.Fields.wha_EndDate, ConditionOperator.GreaterEqual, periodStart)
                }
            });

            // LEFT JOIN to space — needed to filter on space.wha_rentedby
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Discount.Fields.wha_DiscountForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.LeftOuter);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_RentedBy, WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName, WHa_Space.Fields.wha_facilityid);

            var facilityLink = spaceLink.AddLink(WHa_Facility.EntityLogicalName, WHa_Space.Fields.wha_facilityid, WHa_Facility.Fields.wha_FacilityId, JoinOperator.LeftOuter);
            facilityLink.EntityAlias = "fa";
            facilityLink.Columns     = new ColumnSet(WHa_Facility.Fields.wha_FacilityName);

            // Scope: account-level OR space-level
            qe.Criteria.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Discount.Fields.wha_DiscountForId, ConditionOperator.Equal, accountId),
                    new ConditionExpression("sp", WHa_Space.Fields.wha_RentedBy, ConditionOperator.Equal, accountId)
                }
            });

            var results   = svc.RetrieveMultiple(qe);
            var discounts = new List<DiscountCharge>(results.Entities.Count);

            foreach (var e in results.Entities)
            {
                var amount = e.GetAttributeValue<Money>(WHa_Discount.Fields.wha_Amount)?.Value;
                if (!amount.HasValue) continue;

                var discountForRef = e.GetAttributeValue<EntityReference>(WHa_Discount.Fields.wha_DiscountForId);
                var isSpaceLevel   = discountForRef != null &&
                    string.Equals(discountForRef.LogicalName, WHa_Space.EntityLogicalName, StringComparison.OrdinalIgnoreCase);

                discounts.Add(new DiscountCharge
                {
                    DiscountId    = e.Id,
                    Amount        = amount.Value,
                    Name          = e.GetAttributeValue<string>(WHa_Discount.Fields.wha_Name) ?? "",
                    FacilityName  = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("fa." + WHa_Facility.Fields.wha_FacilityName)?.Value as string : null,
                    SpaceName     = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_SpaceName)?.Value as string : null,
                    SpaceUnitName = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_UnitName)?.Value as string : null
                });
            }

            return discounts;
        }
    }
}

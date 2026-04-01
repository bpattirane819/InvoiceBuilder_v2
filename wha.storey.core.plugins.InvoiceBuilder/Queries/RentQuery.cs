using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Fetches all rents active during the billing period for the customer.
    /// Uses wha_customerid as the direct entry point — no N+1 space queries.
    /// LEFT JOINs wha_space to get SpaceName/UnitName in the same round trip.
    /// <para>Date overlap: StartDate &lt;= periodEnd AND (EndDate IS NULL OR EndDate &gt;= periodStart).</para>
    /// </summary>
    internal static class RentQuery
    {
        public static IReadOnlyList<RentCharge> GetRents(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var qe = new QueryExpression(WHa_Rent.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Rent.Fields.wha_RentId,
                    WHa_Rent.Fields.wha_CustomerRentAmount,
                    WHa_Rent.Fields.wha_StartDate,
                    WHa_Rent.Fields.wha_EndDate,
                    WHa_Rent.Fields.wha_Space,
                    WHa_Rent.Fields.wha_RentName),
                Criteria = new FilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new ConditionExpression(WHa_Rent.Fields.wha_customerid, ConditionOperator.Equal,     accountId),
                        new ConditionExpression(WHa_Rent.Fields.wha_StartDate,  ConditionOperator.LessEqual, periodEnd)
                    },
                    Filters =
                    {
                        new FilterExpression(LogicalOperator.Or)
                        {
                            Conditions =
                            {
                                new ConditionExpression(WHa_Rent.Fields.wha_EndDate, ConditionOperator.Null),
                                new ConditionExpression(WHa_Rent.Fields.wha_EndDate, ConditionOperator.GreaterEqual, periodStart)
                            }
                        }
                    }
                }
            };

            qe.Orders.Add(new OrderExpression(WHa_Rent.Fields.wha_StartDate, OrderType.Descending));

            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Rent.Fields.wha_Space, WHa_Space.Fields.wha_SpaceId, JoinOperator.LeftOuter);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName, WHa_Space.Fields.wha_facilityid);

            var facilityLink = spaceLink.AddLink(WHa_Facility.EntityLogicalName, WHa_Space.Fields.wha_facilityid, WHa_Facility.Fields.wha_FacilityId, JoinOperator.LeftOuter);
            facilityLink.EntityAlias = "fa";
            facilityLink.Columns     = new ColumnSet(WHa_Facility.Fields.wha_FacilityName, WHa_Facility.Fields.wha_ZipPostalCode);

            var results = svc.RetrieveMultiple(qe);
            var charges = new List<RentCharge>(results.Entities.Count);

            foreach (var e in results.Entities)
            {
                if (!e.GetAttributeValue<DateTime?>(WHa_Rent.Fields.wha_StartDate).HasValue) continue;

                charges.Add(new RentCharge
                {
                    RentId          = e.Id,
                    SpaceId         = e.GetAttributeValue<EntityReference>(WHa_Rent.Fields.wha_Space)?.Id ?? Guid.Empty,
                    Amount          = e.GetAttributeValue<Money>(WHa_Rent.Fields.wha_CustomerRentAmount)?.Value ?? 0m,
                    RentName        = e.GetAttributeValue<string>(WHa_Rent.Fields.wha_RentName) ?? "",
                    FacilityName    = e.GetAttributeValue<AliasedValue>("fa." + WHa_Facility.Fields.wha_FacilityName)?.Value as string ?? "",
                    FacilityZipCode = e.GetAttributeValue<AliasedValue>("fa." + WHa_Facility.Fields.wha_ZipPostalCode)?.Value as string ?? "",
                    SpaceName       = e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_SpaceName)?.Value as string ?? "",
                    UnitName        = e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_UnitName)?.Value as string ?? ""
                });
            }

            // Deduplicate: a space may have an expiring rent and a new rent both active in the
            // same billing period (e.g. old runs 9/11/2024–4/11/2026, new starts 4/11/2026).
            // Results are already sorted by StartDate DESC, so first-seen per SpaceId = most recent.
            var seen    = new HashSet<Guid>();
            var deduped = new List<RentCharge>(charges.Count);
            foreach (var c in charges)
            {
                // SpaceId == Guid.Empty means no space reference — always keep
                if (c.SpaceId == Guid.Empty || seen.Add(c.SpaceId))
                    deduped.Add(c);
            }

            return deduped;
        }
    }
}

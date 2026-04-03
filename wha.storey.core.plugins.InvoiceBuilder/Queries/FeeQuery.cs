using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Fetches fees for the account and its spaces.
    /// <para>Space recurring fees: INNER JOIN on wha_space (active spaces only, status=1), date overlap.</para>
    /// <para>Space one-time fees: INNER JOIN on wha_space (all spaces), StartDate/EndDate null, createdon within billing period.</para>
    /// <para>Account fees: wha_feeforid = accountId, wha_feelevel = 1000, date overlap. Charged once per invoice.</para>
    /// </summary>
    internal static class FeeQuery
    {
        public static IReadOnlyList<FeeCharge> GetFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var fees = new List<FeeCharge>();
            fees.AddRange(GetRecurringSpaceFees(svc, accountId, periodStart, periodEnd));
            fees.AddRange(GetOneTimeSpaceFees(svc, accountId, periodStart, periodEnd));
            fees.AddRange(GetAccountFees(svc, accountId, periodStart, periodEnd));
            return fees;
        }

        // Recurring space-level fees: active spaces only (status=1), date overlap.
        // INNER JOIN ensures wha_feeforid points to a space (not an account).
        private static IReadOnlyList<FeeCharge> GetRecurringSpaceFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var qe = new QueryExpression(WHa_Fee.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Fee.Fields.wha_FeeId,
                    WHa_Fee.Fields.wha_Name,
                    WHa_Fee.Fields.wha_Amount,
                    WHa_Fee.Fields.wha_FeeForId,
                    WHa_Fee.Fields.wha_PercentageofRent),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            // Recurring: StartDate IS NOT NULL and date overlap
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.NotNull);
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.LessEqual, periodEnd);
            qe.Criteria.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Fee.Fields.wha_EndDate, ConditionOperator.Null),
                    new ConditionExpression(WHa_Fee.Fields.wha_EndDate, ConditionOperator.GreaterEqual, periodStart)
                }
            });

            // INNER JOIN: scopes to space-level fees only; active spaces for this account
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.Inner);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_RentedBy,    ConditionOperator.Equal, accountId);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_isrentedcode, ConditionOperator.Equal, true); // true = Actively rented
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_statuscode, ConditionOperator.Equal, 0);  // 0 = Active

            var facilityLink = spaceLink.AddLink(WHa_Facility.EntityLogicalName, WHa_Space.Fields.wha_facilityid, WHa_Facility.Fields.wha_FacilityId, JoinOperator.LeftOuter);
            facilityLink.EntityAlias = "fa";
            facilityLink.Columns     = new ColumnSet(WHa_Facility.Fields.wha_ZipPostalCode);

            return MapFees(svc.RetrieveMultiple(qe));
        }

        // One-time space-level fees: active spaces only (status=1), created within billing period.
        // INNER JOIN ensures wha_feeforid points to a space (not an account).
        private static IReadOnlyList<FeeCharge> GetOneTimeSpaceFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var qe = new QueryExpression(WHa_Fee.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Fee.Fields.wha_FeeId,
                    WHa_Fee.Fields.wha_Name,
                    WHa_Fee.Fields.wha_Amount,
                    WHa_Fee.Fields.wha_FeeForId,
                    WHa_Fee.Fields.wha_PercentageofRent,
                    WHa_Fee.Fields.CreatedOn),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            // One-time: no dates, created within billing period
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.Null);
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_EndDate,   ConditionOperator.Null);
            qe.Criteria.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.GreaterEqual, periodStart);
            qe.Criteria.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.LessEqual,    periodEnd);

            // INNER JOIN: scopes to space-level fees only; active spaces for this account
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.Inner);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_RentedBy,    ConditionOperator.Equal, accountId);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_isrentedcode, ConditionOperator.Equal, true); // true = Actively rented
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_statuscode, ConditionOperator.Equal, 0);  // 0 = Active

            var facilityLink = spaceLink.AddLink(WHa_Facility.EntityLogicalName, WHa_Space.Fields.wha_facilityid, WHa_Facility.Fields.wha_FacilityId, JoinOperator.LeftOuter);
            facilityLink.EntityAlias = "fa";
            facilityLink.Columns     = new ColumnSet(WHa_Facility.Fields.wha_ZipPostalCode);

            return MapFees(svc.RetrieveMultiple(qe));
        }

        // Account-level fees: wha_feeforid = account, wha_feelevel = 1000, date overlap.
        // Charged once per invoice regardless of space count.
        private static IReadOnlyList<FeeCharge> GetAccountFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var qe = new QueryExpression(WHa_Fee.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Fee.Fields.wha_FeeId,
                    WHa_Fee.Fields.wha_Name,
                    WHa_Fee.Fields.wha_Amount,
                    WHa_Fee.Fields.wha_FeeForId),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.Equal, accountId);
            qe.Criteria.AddCondition("wha_feelevel",               ConditionOperator.Equal, 1000);

            // Recurring date overlap
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.NotNull);
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.LessEqual, periodEnd);
            qe.Criteria.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Fee.Fields.wha_EndDate, ConditionOperator.Null),
                    new ConditionExpression(WHa_Fee.Fields.wha_EndDate, ConditionOperator.GreaterEqual, periodStart)
                }
            });

            return MapFees(svc.RetrieveMultiple(qe));
        }

        private static List<FeeCharge> MapFees(EntityCollection results)
        {
            var fees = new List<FeeCharge>(results.Entities.Count);
            foreach (var e in results.Entities)
            {
                var amount = e.GetAttributeValue<Money>(WHa_Fee.Fields.wha_Amount)?.Value;
                var pct    = e.GetAttributeValue<decimal?>(WHa_Fee.Fields.wha_PercentageofRent);
                if (!amount.HasValue && !pct.HasValue) continue;

                var feeForRef    = e.GetAttributeValue<EntityReference>(WHa_Fee.Fields.wha_FeeForId);
                var isSpaceLevel = feeForRef != null &&
                    string.Equals(feeForRef.LogicalName, WHa_Space.EntityLogicalName, StringComparison.OrdinalIgnoreCase);

                var feeName = e.GetAttributeValue<string>(WHa_Fee.Fields.wha_Name) ?? "";
                decimal normalizedPct = 0m;
                if (!amount.HasValue && pct.HasValue)
                {
                    // Normalize percentage value to a decimal fraction (e.g. 5 -> 0.05, 0.05 -> 0.05)
                    normalizedPct = pct.Value > 1m ? pct.Value / 100m : pct.Value;
                    // Display as a percentage (e.g. 5%)
                    feeName = $"{feeName} ({(normalizedPct * 100m).ToString("0.##")}%)";
                }


                fees.Add(new FeeCharge
                {
                    FeeId            = e.Id,
                    SpaceId          = isSpaceLevel ? (feeForRef?.Id ?? Guid.Empty) : Guid.Empty,
                    Amount           = amount ?? 0m,
                    PercentageOfRent = normalizedPct != 0m ? normalizedPct : (pct ?? 0m),
                    FeeName          = feeName,
                    IsSpaceLevel     = isSpaceLevel,
                    SpaceName        = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_SpaceName)?.Value as string : null,
                    SpaceUnitName    = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_UnitName)?.Value as string : null
                });
            }
            return fees;
        }
    }
}

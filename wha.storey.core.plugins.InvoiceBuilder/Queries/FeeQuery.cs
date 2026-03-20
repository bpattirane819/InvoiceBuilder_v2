using System;
using System.Collections.Generic;
using System.Linq;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Fetches fees for the account and its spaces.
    /// <para>Recurring fees: StartDate IS NOT NULL AND date overlap. Space-level recurring fees
    /// are only included for spaces with wha_spacestatus = 1 (Rented).</para>
    /// <para>One-time fees: StartDate IS NULL AND EndDate IS NULL AND createdon within billing period.
    /// All spaces (including vacated) are included so historical one-time fees (e.g. cleaning) are captured.</para>
    /// </summary>
    internal static class FeeQuery
    {
        public static IReadOnlyList<FeeCharge> GetFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var allSpaceIds    = GetSpaceIds(svc, accountId, activeOnly: false);
            var activeSpaceIds = GetSpaceIds(svc, accountId, activeOnly: true);

            var fees = new List<FeeCharge>();
            fees.AddRange(GetRecurringFees(svc, accountId, periodStart, periodEnd, activeSpaceIds));
            fees.AddRange(GetOneTimeFees(svc, accountId, periodStart, periodEnd, allSpaceIds));
            return fees;
        }

        private static IReadOnlyList<FeeCharge> GetRecurringFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd,
            List<Guid> activeSpaceIds)
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

            // Recurring: StartDate IS NOT NULL AND date overlap
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

            // Scope: account-level OR active space-level
            var scopeOr = new FilterExpression(LogicalOperator.Or);
            scopeOr.Conditions.Add(new ConditionExpression(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.Equal, accountId));
            if (activeSpaceIds.Count > 0)
                scopeOr.Conditions.Add(new ConditionExpression(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.In, activeSpaceIds.Cast<object>().ToArray()));
            qe.Criteria.Filters.Add(scopeOr);

            // Assignment level filter
            qe.Criteria.Filters.Add(BuildAssignmentLevelFilter());

            // LEFT JOIN to space for space name/unit
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.LeftOuter);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName);

            return MapFees(svc.RetrieveMultiple(qe));
        }

        private static IReadOnlyList<FeeCharge> GetOneTimeFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd,
            List<Guid> allSpaceIds)
        {
            var qe = new QueryExpression(WHa_Fee.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Fee.Fields.wha_FeeId,
                    WHa_Fee.Fields.wha_Name,
                    WHa_Fee.Fields.wha_Amount,
                    WHa_Fee.Fields.wha_FeeForId,
                    WHa_Fee.Fields.CreatedOn),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            // One-time: no start or end date, created within billing period
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.Null);
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_EndDate,   ConditionOperator.Null);
            qe.Criteria.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.GreaterEqual, periodStart);
            qe.Criteria.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.LessEqual,    periodEnd);

            // Scope: account-level OR any space (including vacated)
            var scopeOr = new FilterExpression(LogicalOperator.Or);
            scopeOr.Conditions.Add(new ConditionExpression(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.Equal, accountId));
            if (allSpaceIds.Count > 0)
                scopeOr.Conditions.Add(new ConditionExpression(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.In, allSpaceIds.Cast<object>().ToArray()));
            qe.Criteria.Filters.Add(scopeOr);

            // Assignment level filter
            qe.Criteria.Filters.Add(BuildAssignmentLevelFilter());

            // LEFT JOIN to space for space name/unit
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.LeftOuter);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName);

            return MapFees(svc.RetrieveMultiple(qe));
        }

        // wha_feeassignmentlevel IN (10001, 10002) OR wha_feelevel IN (1000, 1003)
        private static FilterExpression BuildAssignmentLevelFilter() =>
            new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression("wha_feeassignmentlevel", ConditionOperator.In, new object[] { 10001, 10002 })                    
                }
            };

        private static List<FeeCharge> MapFees(EntityCollection results)
        {
            var fees = new List<FeeCharge>(results.Entities.Count);
            foreach (var e in results.Entities)
            {
                var amount = e.GetAttributeValue<Money>(WHa_Fee.Fields.wha_Amount)?.Value;
                if (!amount.HasValue) continue;

                var feeForRef    = e.GetAttributeValue<EntityReference>(WHa_Fee.Fields.wha_FeeForId);
                var isSpaceLevel = feeForRef != null &&
                    string.Equals(feeForRef.LogicalName, WHa_Space.EntityLogicalName, StringComparison.OrdinalIgnoreCase);

                fees.Add(new FeeCharge
                {
                    FeeId         = e.Id,
                    Amount        = amount.Value,
                    FeeName       = e.GetAttributeValue<string>(WHa_Fee.Fields.wha_Name) ?? "",
                    IsSpaceLevel  = isSpaceLevel,
                    SpaceName     = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_SpaceName)?.Value as string : null,
                    SpaceUnitName = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_UnitName)?.Value as string : null
                });
            }
            return fees;
        }

        private static List<Guid> GetSpaceIds(IOrganizationService svc, Guid accountId, bool activeOnly)
        {
            var qe = new QueryExpression(WHa_Space.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(WHa_Space.Fields.wha_SpaceId),
                Criteria  = new FilterExpression
                {
                    Conditions = { new ConditionExpression(WHa_Space.Fields.wha_RentedBy, ConditionOperator.Equal, accountId) }
                }
            };

            if (activeOnly)
                qe.Criteria.AddCondition(WHa_Space.Fields.wha_SpaceStatus, ConditionOperator.Equal, 1); // 1 = Rented

            return svc.RetrieveMultiple(qe).Entities
                .Where(e => e.Id != Guid.Empty)
                .Select(e => e.Id)
                .ToList();
        }
    }
}

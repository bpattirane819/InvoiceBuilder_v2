using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Fetches fees for the account and its spaces.
    /// <para>Space recurring fees: INNER JOIN on wha_space (active spaces or moving out after period start), date overlap.</para>
    /// <para>Space one-time fees: INNER JOIN on wha_space (active spaces or moving out after period start), StartDate/EndDate null, createdon within billing period.</para>
    /// <para>Account fees: wha_feeforid = accountId, wha_feelevel = 1000, date overlap. Charged once per invoice.</para>
    /// </summary>
    internal static class FeeQuery
    {
        public static IReadOnlyList<FeeCharge> GetFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd, ITracingService trace = null)
        {
            var fees = new List<FeeCharge>();
            fees.AddRange(GetRecurringSpaceFees(svc, accountId, periodStart, periodEnd));
            fees.AddRange(GetOneTimeSpaceFees(svc, accountId, periodStart, periodEnd));
            fees.AddRange(GetAccountFees(svc, accountId, periodStart, periodEnd));
            return fees;
        }

        // Recurring space-level fees: active spaces or spaces moving out after period start, date overlap.
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
                    WHa_Fee.Fields.wha_PercentageofRent,
                    WHa_Fee.Fields.wha_StartDate,
                    WHa_Fee.Fields.wha_EndDate,
                    WHa_Fee.Fields.wha_feetemplateid),
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

            // LEFT JOIN: fee template for fee name fallback
            var templateLink = qe.AddLink(WHa_FeeTemplate.EntityLogicalName, WHa_Fee.Fields.wha_feetemplateid, WHa_FeeTemplate.Fields.wha_FeeTemplateId, JoinOperator.LeftOuter);
            templateLink.EntityAlias = "ft";
            templateLink.Columns     = new ColumnSet(WHa_FeeTemplate.Fields.wha_TemplateName);

            // INNER JOIN: scopes to space-level fees only; active spaces or spaces moving out after period starts
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.Inner);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName, WHa_Space.Fields.wha_MoveoutDate);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_RentedBy, ConditionOperator.Equal, accountId);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_isrentedcode, ConditionOperator.NotEqual, false); // Rented or Vacated

            // Include: (active AND actively rented) OR (inactive AND moving out after period starts)
            spaceLink.LinkCriteria.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Space.Fields.wha_statuscode, ConditionOperator.Equal, 0),
                    new ConditionExpression(WHa_Space.Fields.wha_isrentedcode, ConditionOperator.Equal, true)
                },
                Filters =
                {
                    new FilterExpression(LogicalOperator.And)
                    {
                        Conditions =
                        {
                            new ConditionExpression(WHa_Space.Fields.wha_statuscode, ConditionOperator.NotEqual, 0),
                            new ConditionExpression(WHa_Space.Fields.wha_MoveoutDate, ConditionOperator.GreaterEqual, periodStart)
                        }
                    }
                }
            });

            var facilityLink = spaceLink.AddLink(WHa_Facility.EntityLogicalName, WHa_Space.Fields.wha_facilityid, WHa_Facility.Fields.wha_FacilityId, JoinOperator.LeftOuter);
            facilityLink.EntityAlias = "fa";
            facilityLink.Columns     = new ColumnSet(WHa_Facility.Fields.wha_ZipPostalCode);

            return MapFees(svc.RetrieveMultiple(qe));
        }

        // One-time space-level fees: active spaces or spaces moving out after period start, created within billing period.
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
                    WHa_Fee.Fields.wha_StartDate,
                    WHa_Fee.Fields.wha_EndDate,
                    WHa_Fee.Fields.CreatedOn,
                    WHa_Fee.Fields.wha_feetemplateid),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            // One-time: no dates, created within billing period
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.Null);
            qe.Criteria.AddCondition(WHa_Fee.Fields.wha_EndDate,   ConditionOperator.Null);
            qe.Criteria.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.GreaterEqual, periodStart);
            qe.Criteria.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.LessEqual,    periodEnd);

            // LEFT JOIN: fee template for fee name fallback
            var templateLink = qe.AddLink(WHa_FeeTemplate.EntityLogicalName, WHa_Fee.Fields.wha_feetemplateid, WHa_FeeTemplate.Fields.wha_FeeTemplateId, JoinOperator.LeftOuter);
            templateLink.EntityAlias = "ft";
            templateLink.Columns     = new ColumnSet(WHa_FeeTemplate.Fields.wha_TemplateName);

            // INNER JOIN: scopes to space-level fees only; active spaces or spaces moving out after period starts
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.Inner);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName, WHa_Space.Fields.wha_MoveoutDate);
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_RentedBy, ConditionOperator.Equal, accountId);               
            spaceLink.LinkCriteria.AddCondition(WHa_Space.Fields.wha_isrentedcode, ConditionOperator.NotEqual, false); // Rented or Vacated
            // Include: (active AND actively rented) OR (inactive AND moving out after period starts)
            spaceLink.LinkCriteria.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Space.Fields.wha_statuscode, ConditionOperator.Equal, 0),
                    new ConditionExpression(WHa_Space.Fields.wha_isrentedcode, ConditionOperator.Equal, true)
                },
                Filters =
                {
                    new FilterExpression(LogicalOperator.And)
                    {
                        Conditions =
                        {
                            new ConditionExpression(WHa_Space.Fields.wha_statuscode, ConditionOperator.NotEqual, 0),

                            new ConditionExpression(WHa_Space.Fields.wha_MoveoutDate, ConditionOperator.GreaterEqual, periodStart)
                        }
                    }
                }
            });

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
                    WHa_Fee.Fields.wha_FeeForId,
                    WHa_Fee.Fields.wha_feetemplateid),
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

            // LEFT JOIN: fee template for fee name fallback
            var templateLink = qe.AddLink(WHa_FeeTemplate.EntityLogicalName, WHa_Fee.Fields.wha_feetemplateid, WHa_FeeTemplate.Fields.wha_FeeTemplateId, JoinOperator.LeftOuter);
            templateLink.EntityAlias = "ft";
            templateLink.Columns     = new ColumnSet(WHa_FeeTemplate.Fields.wha_TemplateName);

            return MapFees(svc.RetrieveMultiple(qe));
        }

        private static List<FeeCharge> MapFees(EntityCollection results, ITracingService trace = null)
        {
            var fees = new List<FeeCharge>(results.Entities.Count);
            foreach (var e in results.Entities)
            {

                var feeNameDebug = e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_SpaceName)?.Value as string ?? null;
                if(feeNameDebug == "279352")
                {
                    trace?.Trace($"Bad Fee");
                    var isProposedSpace = ((OptionSetValue)e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_isrentedcode)?.Value)?.Value;
                }
                


                var amount = e.GetAttributeValue<Money>(WHa_Fee.Fields.wha_Amount)?.Value;
                var pct    = e.GetAttributeValue<decimal?>(WHa_Fee.Fields.wha_PercentageofRent);
                if (!amount.HasValue && !pct.HasValue) continue;

                var feeForRef    = e.GetAttributeValue<EntityReference>(WHa_Fee.Fields.wha_FeeForId);
                var isSpaceLevel = feeForRef != null &&
                    string.Equals(feeForRef.LogicalName, WHa_Space.EntityLogicalName, StringComparison.OrdinalIgnoreCase);

                // Use template name directly as the fee name
                var feeName = e.GetAttributeValue<AliasedValue>("ft." + WHa_FeeTemplate.Fields.wha_TemplateName)?.Value as string ?? "";
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
                    SpaceUnitName    = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_UnitName)?.Value as string : null,
                    IsRentedCode     = isSpaceLevel ? (e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_isrentedcode)?.Value as OptionSetValue)?.Value ?? 0 : 0,
                    FeeStartDate     = e.GetAttributeValue<DateTime?>(WHa_Fee.Fields.wha_StartDate),
                    FeeEndDate       = e.GetAttributeValue<DateTime?>(WHa_Fee.Fields.wha_EndDate),
                    CreatedOn        = e.GetAttributeValue<DateTime?>(WHa_Fee.Fields.CreatedOn)
                });
            }
            return fees;
        }
    }
}

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
    /// <para>Recurring fees: StartDate IS NOT NULL AND date overlap.</para>
    /// <para>One-time fees: StartDate IS NULL AND EndDate IS NULL AND createdon within billing period.
    /// wha_onetime is not used — data migration issues mean it may be false on genuine one-time fees;
    /// absence of both dates is the reliable indicator.</para>
    /// <para>Scope: account-level (FeeForId = accountId) OR space-level (FeeForId IN spaces rented by account).
    /// wha_rentedby is never cleared on move-out, so historical one-time fees (e.g. cleaning) are still captured.</para>
    /// </summary>
    internal static class FeeQuery
    {
        public static IReadOnlyList<FeeCharge> GetFees(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var spaceIds = GetSpaceIds(svc, accountId);

            var qe = new QueryExpression(WHa_Fee.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Fee.Fields.wha_FeeId,
                    WHa_Fee.Fields.wha_Name,
                    WHa_Fee.Fields.wha_Amount,
                    WHa_Fee.Fields.wha_FeeForId,
                    WHa_Fee.Fields.wha_StartDate,
                    WHa_Fee.Fields.wha_EndDate,
                    WHa_Fee.Fields.CreatedOn),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            // Date filter: recurring OR one-time
            var dateOr = new FilterExpression(LogicalOperator.Or);

            var recurring = new FilterExpression(LogicalOperator.And);
            recurring.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.NotNull);
            recurring.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.LessEqual, periodEnd);
            recurring.Filters.Add(new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(WHa_Fee.Fields.wha_EndDate, ConditionOperator.Null),
                    new ConditionExpression(WHa_Fee.Fields.wha_EndDate, ConditionOperator.GreaterEqual, periodStart)
                }
            });
            dateOr.Filters.Add(recurring);

            var oneTime = new FilterExpression(LogicalOperator.And);
            oneTime.AddCondition(WHa_Fee.Fields.wha_StartDate, ConditionOperator.Null);
            oneTime.AddCondition(WHa_Fee.Fields.wha_EndDate,   ConditionOperator.Null);
            oneTime.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.GreaterEqual, periodStart);
            oneTime.AddCondition(WHa_Fee.Fields.CreatedOn,     ConditionOperator.LessEqual,    periodEnd);
            dateOr.Filters.Add(oneTime);

            qe.Criteria.Filters.Add(dateOr);

            // Scope: account-level OR space-level
            var scopeOr = new FilterExpression(LogicalOperator.Or);
            scopeOr.Conditions.Add(new ConditionExpression(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.Equal, accountId));
            if (spaceIds.Count > 0)
                scopeOr.Conditions.Add(new ConditionExpression(WHa_Fee.Fields.wha_FeeForId, ConditionOperator.In, spaceIds.Cast<object>().ToArray()));
            qe.Criteria.Filters.Add(scopeOr);

            // INNER JOIN to fee template for template name
            var tmplLink = qe.AddLink(WHa_FeeTemplate.EntityLogicalName, WHa_Fee.Fields.wha_feetemplateid, WHa_FeeTemplate.Fields.wha_FeeTemplateId, JoinOperator.Inner);
            tmplLink.EntityAlias = "ft";
            tmplLink.Columns     = new ColumnSet(WHa_FeeTemplate.Fields.wha_TemplateName);

            // LEFT JOIN to space for space name/unit on space-level fees
            var spaceLink = qe.AddLink(WHa_Space.EntityLogicalName, WHa_Fee.Fields.wha_FeeForId, WHa_Space.Fields.wha_SpaceId, JoinOperator.LeftOuter);
            spaceLink.EntityAlias = "sp";
            spaceLink.Columns     = new ColumnSet(WHa_Space.Fields.wha_SpaceName, WHa_Space.Fields.wha_UnitName, WHa_Space.Fields.wha_facilityid);

            var facilityLink = spaceLink.AddLink(WHa_Facility.EntityLogicalName, WHa_Space.Fields.wha_facilityid, WHa_Facility.Fields.wha_FacilityId, JoinOperator.LeftOuter);
            facilityLink.EntityAlias = "fa";
            facilityLink.Columns     = new ColumnSet(WHa_Facility.Fields.wha_FacilityName);

            var results = svc.RetrieveMultiple(qe);
            var fees    = new List<FeeCharge>(results.Entities.Count);

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
                    FacilityName  = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("fa." + WHa_Facility.Fields.wha_FacilityName)?.Value as string : null,
                    SpaceName     = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_SpaceName)?.Value as string : null,
                    SpaceUnitName = isSpaceLevel ? e.GetAttributeValue<AliasedValue>("sp." + WHa_Space.Fields.wha_UnitName)?.Value as string : null
                });
            }

            return fees;
        }

        private static List<Guid> GetSpaceIds(IOrganizationService svc, Guid accountId)
        {
            var qe = new QueryExpression(WHa_Space.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(WHa_Space.Fields.wha_SpaceId),
                Criteria  = new FilterExpression
                {
                    Conditions = { new ConditionExpression(WHa_Space.Fields.wha_RentedBy, ConditionOperator.Equal, accountId) }
                }
            };

            return svc.RetrieveMultiple(qe).Entities
                .Where(e => e.Id != Guid.Empty)
                .Select(e => e.Id)
                .ToList();
        }
    }
}

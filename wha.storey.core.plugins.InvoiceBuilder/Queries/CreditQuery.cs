using System;
using System.Collections.Generic;
using DataverseModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    /// <summary>
    /// Fetches credits applied to the account for the billing period.
    /// <para>Scope: wha_customerid = accountId AND statuscode = 1 (Active) AND createdon within billing period.</para>
    /// <para>Amounts are positive — subtracted from the invoice total at calculation time.</para>
    /// </summary>
    internal static class CreditQuery
    {
        public static IReadOnlyList<CreditCharge> GetCredits(
            IOrganizationService svc,
            Guid accountId,
            DateTime periodStart,
            DateTime periodEnd)
        {
            var qe = new QueryExpression(WHa_Credit.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(
                    WHa_Credit.Fields.wha_creditId,
                    WHa_Credit.Fields.wha_Name,
                    WHa_Credit.Fields.wha_amount),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            qe.Criteria.AddCondition(WHa_Credit.Fields.wha_customerid, ConditionOperator.Equal,        accountId);
            qe.Criteria.AddCondition("statuscode",                      ConditionOperator.Equal,        1);
            qe.Criteria.AddCondition(WHa_Credit.Fields.CreatedOn,       ConditionOperator.GreaterEqual, periodStart);
            qe.Criteria.AddCondition(WHa_Credit.Fields.CreatedOn,       ConditionOperator.LessEqual,    periodEnd);

            var results = svc.RetrieveMultiple(qe);
            var credits = new List<CreditCharge>(results.Entities.Count);

            foreach (var e in results.Entities)
            {
                var amount = e.GetAttributeValue<Money>(WHa_Credit.Fields.wha_amount)?.Value;
                if (!amount.HasValue) continue;

                credits.Add(new CreditCharge
                {
                    CreditId = e.Id,
                    Amount   = amount.Value,
                    Name     = e.GetAttributeValue<string>(WHa_Credit.Fields.wha_Name) ?? ""
                });
            }

            return credits;
        }
    }
}

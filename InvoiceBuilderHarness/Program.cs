using System;
using System.Collections.Generic;
using System.Linq;
using DataverseModel;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using wha.storey.core.plugins.InvoiceBuilder;

class Program
{
    static void Main(string[] args)
    {
        IConfiguration configuration;
        try
        {
            configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.local.json", optional: false)
                .Build();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load appsettings.local.json");
            Console.WriteLine(ex.Message);
            return;
        }

        var connectionString = configuration["Dataverse:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("Missing configuration value: Dataverse:ConnectionString");
            return;
        }

        using var serviceClient = new ServiceClient(connectionString);

        if (!serviceClient.IsReady)
        {
            Console.WriteLine("Dataverse connection failed");
            Console.WriteLine(serviceClient.LastError);
            if (serviceClient.LastException != null) Console.WriteLine(serviceClient.LastException);
            return;
        }

        Console.WriteLine("Connected to Dataverse");

        //TEST INPUTS — change these to test different accounts/month
        Guid companyGuid    = Guid.Parse("89b42953-bdf6-f011-8406-000d3a181ddb");
        var  dateRun        = new DateTime(2026, 01, 07);  // any date within the target month
        bool simulatePlugin = true;   // true = full run (creates invoice, dedup, generate, write)
                                      // false = dry run (generate and print only, no writes)

        if (simulatePlugin)
        {
            SimulateGeneratePlugin(serviceClient, companyGuid, dateRun);
            return;
        }

        // Dry run: generate and print without writing
        var (periodStart, periodEnd) = BillingPeriod.ForMonth(dateRun);
        var currency = Helpers.GetBaseCurrency(serviceClient);

        Console.WriteLine($"Account: {companyGuid}");
        Console.WriteLine($"Period:  {periodStart:yyyy-MM-dd} - {periodEnd:yyyy-MM-dd}");
        Console.WriteLine();

        var lines = LineItemGenerator.Generate(serviceClient, Guid.Empty, companyGuid, periodStart, periodEnd, currency);
        PrintLineItems(lines, periodStart, periodEnd, companyGuid);
    }

    static void SimulateGeneratePlugin(IOrganizationService svc, Guid companyGuid, DateTime dateRun)
    {
        Console.WriteLine("=== Simulating GenerateInvoiceLineItemsPlugin ===");

        var (periodStart, periodEnd) = BillingPeriod.ForMonth(dateRun);
        var currency = Helpers.GetBaseCurrency(svc);

        Console.WriteLine($"Account: {companyGuid}");
        Console.WriteLine($"Period:  {periodStart:yyyy-MM-dd} - {periodEnd:yyyy-MM-dd}");

        // Step 1 — Create invoice
        Console.WriteLine();
        Console.WriteLine("[1] Creating new invoice...");
        var invoiceEntity = new Entity(WHa_Invoice.EntityLogicalName);
        invoiceEntity[WHa_Invoice.Fields.wha_InvoiceFor]  = new EntityReference("account", companyGuid);
        invoiceEntity[WHa_Invoice.Fields.wha_InvoiceDate] = dateRun;
        if (currency != null) invoiceEntity["transactioncurrencyid"] = currency;
        var invoiceId = svc.Create(invoiceEntity);
        Console.WriteLine($"    Created invoice: {invoiceId}");

        // Step 2 — Dedup
        Console.WriteLine();
        Console.WriteLine("[2] Checking for duplicate invoices...");
        var dedup = LineItemWriter.DeleteDuplicateInvoices(svc, companyGuid, periodStart, periodEnd, invoiceId);
        Console.WriteLine($"    Deleted {dedup.InvoicesDeleted} duplicate invoice(s) and {dedup.LineItemsDeleted} line item(s)");
        if (!string.IsNullOrWhiteSpace(dedup.PreservedInvoiceNumber))
        {
            var update = new Entity(WHa_Invoice.EntityLogicalName) { Id = invoiceId };
            update[WHa_Invoice.Fields.wha_InvoiceNumber] = dedup.PreservedInvoiceNumber;
            svc.Update(update);
            Console.WriteLine($"    Restored invoice number: {dedup.PreservedInvoiceNumber}");
        }

        // Step 3 — Generate
        Console.WriteLine();
        Console.WriteLine("[3] Generating line items...");
        var lines = LineItemGenerator.Generate(svc, invoiceId, companyGuid, periodStart, periodEnd, currency);
        Console.WriteLine($"    Generated {lines.Count} line item(s)");

        // Step 4 — Write
        Console.WriteLine();
        Console.WriteLine("[4] Writing line items...");
        var result = LineItemWriter.WriteLineItems(svc, invoiceId, lines, currency);

        Console.WriteLine();
        Console.WriteLine("=== RESULT ===");
        Console.WriteLine($"  Duplicate invoices deleted:    {dedup.InvoicesDeleted}");
        Console.WriteLine($"  Duplicate line items deleted:  {dedup.LineItemsDeleted}");
        Console.WriteLine($"  Stale line items cleared:      {result.Deleted}");
        Console.WriteLine($"  Line items created:            {result.Created}");

        PrintLineItems(lines, periodStart, periodEnd, companyGuid);

        Console.WriteLine("==============");
    }

    static void PrintLineItems(
        IReadOnlyList<WHa_InvoiceLineItem> lineItems,
        DateTime periodStart,
        DateTime periodEnd,
        Guid companyGuid)
    {
        var rents     = lineItems.Where(li => li.wha_SourceType == "Rent").ToList();
        var fees      = lineItems.Where(li => li.wha_SourceType == "Fee").ToList();
        var discounts = lineItems.Where(li => li.wha_SourceType == "Discount").ToList();

        var rentTotal     = rents.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var feeTotal      = fees.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var discountTotal = discounts.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var grandTotal    = rentTotal + feeTotal - discountTotal;

        Console.WriteLine();
        Console.WriteLine("===================================================");
        Console.WriteLine($"AccountId: {companyGuid}");
        Console.WriteLine($"Period:    {periodStart:yyyy-MM-dd} - {periodEnd:yyyy-MM-dd}");
        Console.WriteLine($"Total:     {lineItems.Count} line item(s)");
        Console.WriteLine("===================================================");

        PrintGroup("RENTS", rents);
        PrintGroup("FEES", fees);
        PrintGroup("DISCOUNTS", discounts);

        Console.WriteLine();
        Console.WriteLine($"  Rents:       {rentTotal,10:C}");
        Console.WriteLine($"  Fees:        {feeTotal,10:C}");
        Console.WriteLine($"  Discounts:  -{discountTotal,9:C}");
        Console.WriteLine($"  ─────────────────────────────");
        Console.WriteLine($"  Grand Total: {grandTotal,10:C}");
        Console.WriteLine("===================================================");
    }

    static void PrintGroup(string label, List<WHa_InvoiceLineItem> items)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {label} ({items.Count}) ---");

        if (items.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }

        foreach (var li in items)
        {
            var name  = li.wha_InvoiceLineItemName ?? "(unnamed)";
            var space = !string.IsNullOrWhiteSpace(li.wha_SpaceNumber)
                ? $" [{li.wha_SpaceName} / {li.wha_SpaceNumber}]"
                : !string.IsNullOrWhiteSpace(li.wha_SpaceName) ? $" [{li.wha_SpaceName}]" : "";
            var total = li.wha_totallineitemamount?.Value ?? 0m;

            Console.WriteLine($"  {name}{space}  {total:C}  (src: {li.wha_SourceId?.Id})");
        }
    }
}

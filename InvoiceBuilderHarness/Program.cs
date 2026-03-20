using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        //Guid companyGuid = Guid.Parse("4e96e2cb-da07-f111-8406-6045bdd5fe9f");//Vanda Pharma LLC TEST
        Guid companyGuid = Guid.Parse("c3582553-bdf6-f011-8406-000d3a1b93dd");//AbbVie LLC DEV
        //Guid companyGuid = Guid.Parse("89b42953-bdf6-f011-8406-000d3a181ddb");//Solaray LLC DEV
        //Guid companyGuid = Guid.Parse("d0bc87ca-da07-f111-8407-6045bdd8c4a0");//Solaray Test
        //Guid companyGuid = Guid.Parse("5ddf8eca-da07-f111-8406-000d3a573438");//Abbvie Test
        //Guid companyGuid = Guid.Parse("0b62dcca-da07-f111-8407-6045bdd8cf45");//Aclaris Test
        //Guid companyGuid = Guid.Parse("e2bc87ca-da07-f111-8407-6045bdd8c4a0");//zABC
        var  dateRun        = new DateTime(2026, 04, 04);  // any date within the target month
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

        var totalSw = Stopwatch.StartNew();
        var sw      = Stopwatch.StartNew();

        var (periodStart, periodEnd) = BillingPeriod.ForMonth(dateRun);
        var currency = Helpers.GetBaseCurrency(svc);

        Console.WriteLine($"Account: {companyGuid}");
        Console.WriteLine($"Period:  {periodStart:yyyy-MM-dd} - {periodEnd:yyyy-MM-dd}");

        var consoleTrace = new ConsoleTracingService();

        // Step 1 — Resolve invoice (find existing, clean duplicates, or create fresh)
        Console.WriteLine();
        Console.WriteLine("[1] Resolving invoice...");
        sw.Restart();
        var resolution = LineItemWriter.ResolveInvoice(svc, companyGuid, periodStart, periodEnd, dateRun, currency, consoleTrace);
        Console.WriteLine($"    Done in {sw.Elapsed.TotalSeconds:F2}s");
        if (resolution.HadDuplicates)
            Console.WriteLine($"    Duplicates found: deleted {resolution.InvoicesDeleted} invoice(s) and {resolution.LineItemsDeleted} line item(s). Fresh invoice created: {resolution.InvoiceId}");
        else
            Console.WriteLine($"    Invoice resolved: {resolution.InvoiceId}");

        // Step 2 — Generate
        Console.WriteLine();
        Console.WriteLine("[2] Generating line items...");
        sw.Restart();
        var lines = LineItemGenerator.Generate(svc, resolution.InvoiceId, companyGuid, periodStart, periodEnd, currency);
        Console.WriteLine($"    Done in {sw.Elapsed.TotalSeconds:F2}s — {lines.Count} line item(s) generated");

        // Step 3 — Write
        Console.WriteLine();
        Console.WriteLine("[3] Writing line items...");
        sw.Restart();
        var result = LineItemWriter.WriteLineItems(svc, resolution.InvoiceId, lines, currency, consoleTrace);
        Console.WriteLine($"    Done in {sw.Elapsed.TotalSeconds:F2}s");

        Console.WriteLine();
        Console.WriteLine("=== RESULT ===");
        if (resolution.HadDuplicates)
        {
            Console.WriteLine($"  Duplicate invoices deleted:    {resolution.InvoicesDeleted}");
            Console.WriteLine($"  Duplicate line items deleted:  {resolution.LineItemsDeleted}");
        }
        Console.WriteLine($"  Line items upserted:           {result.Created}");
        Console.WriteLine($"  Orphaned line items deleted:   {result.Deleted}");

        PrintLineItems(lines, periodStart, periodEnd, companyGuid, totalSw.Elapsed);

        Console.WriteLine("==============");
    }

    static void PrintLineItems(
        IReadOnlyList<WHa_InvoiceLineItem> lineItems,
        DateTime periodStart,
        DateTime periodEnd,
        Guid companyGuid,
        TimeSpan? totalElapsed = null)
    {
        var rents     = lineItems.Where(li => li.wha_SourceType == "Rent").ToList();
        var fees      = lineItems.Where(li => li.wha_SourceType == "Fee").ToList();
        var discounts = lineItems.Where(li => li.wha_SourceType == "Discount").ToList();
        var credits   = lineItems.Where(li => li.wha_SourceType == "Credit").ToList();

        var rentTotal     = rents.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var feeTotal      = fees.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var discountTotal = discounts.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var creditTotal   = credits.Sum(li => li.wha_totallineitemamount?.Value ?? 0m);
        var grandTotal    = rentTotal + feeTotal - discountTotal - creditTotal;

        Console.WriteLine();
        Console.WriteLine("===================================================");
        Console.WriteLine($"AccountId: {companyGuid}");
        Console.WriteLine($"Period:    {periodStart:yyyy-MM-dd} - {periodEnd:yyyy-MM-dd}");
        Console.WriteLine($"Total:     {lineItems.Count} line item(s)");
        if (totalElapsed.HasValue)
            Console.WriteLine($"Run Time:  {totalElapsed.Value.TotalSeconds:F2}s");
        Console.WriteLine("===================================================");

        PrintGroup("RENTS", rents);
        PrintGroup("FEES", fees);
        PrintGroup("DISCOUNTS", discounts);
        PrintGroup("CREDITS", credits);

        Console.WriteLine();
        Console.WriteLine($"  Rents:       {rentTotal,10:C}");
        Console.WriteLine($"  Fees:        {feeTotal,10:C}");
        Console.WriteLine($"  Discounts:  -{discountTotal,9:C}");
        Console.WriteLine($"  Credits:    -{creditTotal,9:C}");
        Console.WriteLine($"  ─────────────────────────────");
        Console.WriteLine($"  Grand Total: {grandTotal,10:C}");
        Console.WriteLine("===================================================");
    }

    class ConsoleTracingService : Microsoft.Xrm.Sdk.ITracingService
    {
        public void Trace(string format, params object[] args)
            => Console.WriteLine("    " + (args.Length > 0 ? string.Format(format, args) : format));
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

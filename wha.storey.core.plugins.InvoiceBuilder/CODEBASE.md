# InvoiceBuilder v2 — Codebase Reference

## What This Project Does

InvoiceBuilder v2 is a **Microsoft Dataverse plugin** for the WarehouseAnywhere (WHA) warehouse management system. Its sole purpose is to generate invoice line items for a given customer account and billing period.

When triggered, it:
1. Finds all **fees** active for the account or its spaces during the billing period
2. Finds all **discounts** active for that account or its spaces during the billing period
3. Finds all **rents** active for the account during the billing period
4. Builds a `wha_invoicelineitem` record for each charge
5. **Idempotently** writes them — deletes existing line items for the invoice first, then inserts fresh ones
6. Stamps the invoice total directly after batch create

---

## What Changed from v1

| v1 | v2 |
|---|---|
| Multiple projects (Core, Plugin, Harness) | Single project, organized into folders |
| Interfaces (`IRentQuery`, `IAccountFeeQuery`, etc.) | Static classes — no interfaces |
| Separate `DataverseModel` project (merged via ILRepack) | DataverseModel `.cs` files included directly via glob |
| `buildMerge.ps1` required ILRepack merge step | `dotnet build` only — no merge needed |
| One-time fee detected by absence of dates | One-time fee detected by absence of dates *(same as v1 — `wha_onetime` flag is unreliable due to data migration)* |

---

## Solution Structure

```
wha.storey.core.plugins.InvoiceBuilder/
├── CODEBASE.md
├── DesignDocument.md
├── wha.storey.core.plugins.InvoiceBuilder.csproj
├── wha.storey.core.plugins.InvoiceBuilder.snk
│
├── Plugins/
│   ├── InvoiceCreatedPlugin.cs         # Plugin entry point — auto-fires on invoice Create (DISABLED)
│   └── GenerateInvoiceLineItemsPlugin.cs # Plugin entry point — manual/flow trigger via Custom API
│
├── Models/
│   ├── ChargeModels.cs                 # RentCharge, FeeCharge, DiscountCharge
│   └── ResultModels.cs                 # WriteResult, DedupResult
│
├── Queries/
│   ├── RentQuery.cs                    # Queries rents by customer, LEFT JOINs space
│   ├── FeeQuery.cs                     # Queries fees (recurring + one-time), pre-queries spaces
│   └── DiscountQuery.cs                # Queries discounts via LEFT JOIN to space
│
├── Orchestration/
│   ├── LineItemGenerator.cs            # Runs all 3 queries, builds line item entities
│   └── LineItemWriter.cs               # Batch create/delete, dedup, invoice total update
│
├── Infrastructure/
│   ├── BillingPeriod.cs                # Derives full calendar month window from any date
│   └── Helpers.cs                      # Currency resolution, input parameter parsing
│
└── DataverseModel/                     # Auto-generated Dataverse entity classes (compiled via SDK glob)
    ├── Entities/                       # WHa_Invoice, WHa_InvoiceLineItem, WHa_Rent, etc.
    ├── OptionSets/                     # Enums (status codes, charge types, etc.)
    └── Messages/                       # Dataverse message definitions
```

**Target framework:** .NET 4.6.2 (`net462`) — required by Dataverse plugin sandbox.
**Key NuGet packages:** `Microsoft.CrmSdk.CoreAssemblies 9.0.2.*`, `Microsoft.PowerApps.MSBuild.Plugin 1.*`

---

## File Map

| File | Class(es) | Purpose |
|---|---|---|
| `Plugins/InvoiceCreatedPlugin.cs` | `InvoiceCreatedPlugin` | Plugin entry point — auto-fires on invoice Create |
| `Plugins/GenerateInvoiceLineItemsPlugin.cs` | `GenerateInvoiceLineItemsPlugin` | Plugin entry point — manual/flow trigger via Custom API |
| `Models/ChargeModels.cs` | `RentCharge`, `FeeCharge`, `DiscountCharge` | DTOs for query results |
| `Models/ResultModels.cs` | `WriteResult`, `DedupResult` | DTOs for write operation results |
| `Queries/RentQuery.cs` | `RentQuery` | Queries rents by customer, LEFT JOINs space |
| `Queries/FeeQuery.cs` | `FeeQuery` | Queries fees (recurring + one-time), pre-queries spaces |
| `Queries/DiscountQuery.cs` | `DiscountQuery` | Queries discounts via LEFT JOIN to space |
| `Orchestration/LineItemGenerator.cs` | `LineItemGenerator` | Runs all 3 queries, builds line item entities |
| `Orchestration/LineItemWriter.cs` | `LineItemWriter` | Batch create/delete, dedup, invoice total update |
| `Infrastructure/BillingPeriod.cs` | `BillingPeriod` | Derives full calendar month window from any date |
| `Infrastructure/Helpers.cs` | `Helpers` | Currency resolution, input parameter parsing |

---

## Plugin Entry Points

### 1. InvoiceCreatedPlugin (automatic)

**Trigger:** Post-Operation, Asynchronous, Message=Create, Entity=`wha_invoice`
**Purpose:** Fires automatically when a new invoice is created (via Power Automate flow trigger).
Reads `wha_invoicefor` and `wha_invoicedate` from the Target entity, resolves currency, runs dedup, generates and writes all line items.

> **Note:** This step should remain **disabled** in Plugin Registration Tool.
> The Power Automate flow (`Invoice - Trigger New Invoice`) handles triggering via `wha_BuildInvoiceDataset` instead.

### 2. GenerateInvoiceLineItemsPlugin (manual / flow)

**Custom API message:** `wha_BuildInvoiceDataset`
**Purpose:** Triggered by the Power Automate flow on new invoice creation, or manually via the UI button.

#### Input Parameters

| Parameter | Type | Required | Notes |
|---|---|---|---|
| `wha_account_id` | Guid | Yes | The customer account to generate for |
| `wha_invoiceid` | Guid | No | If provided, line items are linked to this invoice |
| `wha_run_date` | DateTime or `yyyy-MM-dd` string | Yes | Any date within the target billing month |
| `wha_transactioncurrencyid` | EntityReference | No | Currency override; falls back to invoice currency then org base |

#### Output Parameters

| Parameter | Type | Notes |
|---|---|---|
| `wha_hello_message` | String | Summary e.g. "Deleted 5 old line items. Created 12 invoice line items." |

### Currency Resolution (both plugins)

```
wha_transactioncurrencyid parameter (or Target entity for InvoiceCreatedPlugin)
  ?? invoice.transactioncurrencyid  (fetched from wha_invoice record)
  ?? organization.basecurrencyid    (org base currency fallback)
```

---

## Billing Period

```csharp
// Any date in the target month → that month's full period (tuple return)
var (periodStart, periodEnd) = BillingPeriod.ForMonth(new DateTime(2025, 11, 1));
// → periodStart: Nov 1 00:00:00.000, periodEnd: Nov 30 23:59:59.999...
```

**Convention:** `wha_invoicedate` stores any date the user enters within the billing month. The billing period is always the full calendar month derived from that date.

---

## Generation Flow

```
Generate(svc, invoiceId, accountId, periodStart, periodEnd, currency)
  │
  ├── 1. FeeQuery.GetFees(accountId, periodStart, periodEnd)         — 2 queries
  │       → pre-queries space IDs rented by account
  │       → then queries fees (recurring + one-time) for account + spaces
  │
  ├── 2. DiscountQuery.GetDiscounts(accountId, periodStart, periodEnd) — 1 query
  │       → LEFT JOINs wha_space to filter by space.wha_rentedby
  │
  └── 3. RentQuery.GetRents(accountId, periodStart, periodEnd)        — 1 query
          → wha_customerid = accountId AND date overlap
          → LEFT JOINs wha_space to get SpaceName/UnitName in the same query
```

**Total: 4 Dataverse queries** regardless of how many spaces the customer has.

---

## Date Overlap Logic

All queries use the same standard billing overlap pattern:

```
StartDate <= periodEnd
AND (EndDate IS NULL OR EndDate >= periodStart)
```

Applied to: `wha_rent`, `wha_discount`, and recurring `wha_fee`.

---

## Fee Business Rules

### Fee Types

**Recurring fees** — `wha_startdate` is set. Use the standard date overlap logic. Appear on every invoice for every month they are active.

**One-time fees** — identified by having **no `wha_startdate` AND no `wha_enddate`**. Charged only in the billing month in which they were **created** (`createdon` falls within `[periodStart, periodEnd]`). Do not repeat.

```
(StartDate IS NOT NULL AND overlap_filter)                        -- recurring
OR
(StartDate IS NULL AND EndDate IS NULL AND createdon IN period)   -- one-time
```

> **Note:** `wha_onetime` is not used as the filter condition. Data migration issues mean it may be `false` on genuine one-time fees. Absence of both dates is the reliable indicator.

### Fee Scope

Fees can be applied at two levels:
- **Account-level** — `wha_FeeForId` points to an `account` record
- **Space-level** — `wha_FeeForId` points to a `wha_space` record

The query fetches both in one shot:
1. Pre-queries all `wha_space` IDs where `wha_RentedBy = accountId` (no date filter)
2. Then queries fees where `wha_FeeForId = accountId OR wha_FeeForId IN (spaceIds)`

**Important:** `wha_rentedby` is never cleared on move-out. One-time fees created after move-out (e.g. cleaning fees) are correctly captured.

---

## Discount Business Rules

- LEFT JOIN to `wha_space`, filters on `wha_DiscountForId = accountId OR space.wha_RentedBy = accountId`
- Date filter is the standard overlap logic
- Amounts are positive — subtracted downstream: `SUM(Rents) + SUM(Fees) - SUM(Discounts)`

---

## Idempotent Write

### WriteLineItems

```
WriteLineItems(svc, invoiceId, lineItems, currency)
  1. DeleteExistingLineItems  — paginated query (2500/page), BatchDelete via ExecuteMultiple
  2. BatchCreate              — groups all creates into ExecuteMultiple (up to 1000 per batch)
  3. UpdateInvoiceTotal       — calculates total from line items, stamps wha_invoice.wha_totalamount
```

### DeleteDuplicateInvoices

Enforces one invoice per customer per month. Filters server-side with `NotEqual keepInvoiceId`.

```
DeleteDuplicateInvoices(svc, accountId, periodStart, periodEnd, keepInvoiceId)
  → Query wha_invoice where account + period + NOT keepInvoiceId
  → For each found invoice: DeleteExistingLineItems(inv.Id)
  → Batch delete all invoice records via ExecuteMultiple
  → Returns DedupResult { InvoicesDeleted, LineItemsDeleted }
```

### Write Performance

| Operation | Method | Round trips |
|---|---|---|
| Create line items | `ExecuteMultiple` (batch 1000) | 1 |
| Delete line items | `ExecuteMultiple` (batch 1000) | 1 |
| Delete invoices (dedup) | `ExecuteMultiple` | 1 |
| Update invoice total | `service.Update` | 1 |

---

## Invoice Total Calculation

`CalculateInvoiceTotalPlugin` (in `InvoiceTotalPlugin/`) is suppressed during `ExecuteMultiple` batches (both creates and deletes) to prevent O(n²) queries and lock contention.

`LineItemWriter.UpdateInvoiceTotal()` stamps the total directly after `BatchCreate`:

```
total = SUM(Rents + Fees) - SUM(Discounts)
svc.Update(wha_invoice { wha_totalamount = total })
```

---

## Power Automate Flow

**Flow name:** `Invoice - Trigger New Invoice`

| Step | Action | Config |
|---|---|---|
| 1 | When a row is added | Table: `wha_invoice`, Change type: Added, Scope: Organization |
| 2 | Perform an unbound action | `wha_BuildInvoiceDataset` with `wha_account_id`, `wha_invoiceid`, `wha_run_date` from trigger |

The flow replaces the `InvoiceCreatedPlugin` step (which remains registered but **disabled**).

---

## Build & Deploy

```powershell
# Build (from project directory)
dotnet build wha.storey.core.plugins.InvoiceBuilder.csproj -c Release

# Deploy via pac
pac plugin push
```

Output DLL:
```
bin\Release\net462\wha.storey.core.plugins.InvoiceBuilder.dll
```

Deploy via **`pac plugin push`** — the `Microsoft.PowerApps.MSBuild.Plugin` package handles packaging and upload automatically.

Registered plugin steps:
- `InvoiceCreatedPlugin` — Message=Create, Entity=wha_invoice, Stage=Post-Operation(40), Mode=Async — **DISABLED** (flow handles triggering)
- `GenerateInvoiceLineItemsPlugin` — Message=wha_BuildInvoiceDataset, Stage=Post-Operation(40)

---

## Dataverse Tables Used

| Table | Purpose |
|---|---|
| `wha_invoice` | Parent invoice record, one per account per billing month |
| `wha_invoicelineitem` | Individual charge lines generated by this plugin |
| `wha_rent` | Rent charges associated with a space and customer |
| `wha_fee` | Fees applied to an account or a specific space |
| `wha_feetemplate` | Fee template definitions (INNER JOINed for template name) |
| `wha_discount` | Discounts applied to an account or a specific space |
| `wha_space` | Warehouse spaces (LEFT JOINed for SpaceName/UnitName; pre-queried for fee scope) |
| `account` | Standard Dataverse accounts (customers) |
| `organization` | Queried for `basecurrencyid` fallback |
| `transactioncurrency` | Standard Dataverse currency table |

---

## Project File Setup

**File:** `wha.storey.core.plugins.InvoiceBuilder.csproj`

```xml
<TargetFramework>net462</TargetFramework>
<PowerAppsTargetsPath>$(MSBuildExtensionsPath)\Microsoft\VisualStudio\v$(VisualStudioVersion)\PowerApps</PowerAppsTargetsPath>
<SignAssembly>true</SignAssembly>
<AssemblyOriginatorKeyFile>wha.storey.core.plugins.InvoiceBuilder.snk</AssemblyOriginatorKeyFile>
<AssemblyVersion>1.0.0.0</AssemblyVersion>
```

```xml
<PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="9.0.2.*" PrivateAssets="All" />
<PackageReference Include="Microsoft.PowerApps.MSBuild.Plugin" Version="1.*" PrivateAssets="All" />
<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.*" PrivateAssets="All" />
```

**Key points:**
- Initialized with `pac plugin init` — uses `Microsoft.PowerApps.MSBuild.Plugin` which enables `pac plugin push` deployment
- The SDK-style project auto-includes all `**/*.cs` under the project folder — DataverseModel files in the `DataverseModel/` subfolder are picked up automatically, no explicit glob needed
- `PrivateAssets="All"` on NuGet references means those packages are not included in the output — they are provided at runtime by the Dataverse sandbox
- `LangVersion` defaults to `latest` via the PowerApps MSBuild targets

---

## Helpers Class

**File:** `Infrastructure/Helpers.cs`

All four methods are `public static` on `public static class Helpers`.

### Currency Resolution

```csharp
// Fetches transactioncurrencyid from a wha_invoice record by ID.
// Returns null if invoiceId is Guid.Empty.
EntityReference LoadCurrencyFromInvoice(IOrganizationService svc, Guid invoiceId)

// Queries the organization record for basecurrencyid. Returns null if not found.
EntityReference GetBaseCurrency(IOrganizationService svc)
```

### Input Parameter Parsing

```csharp
// Returns Guid.Empty if the key is absent, null, or unparseable.
Guid ReadOptionalGuid(IPluginExecutionContext ctx, string key)

// Throws InvalidPluginExecutionException if key is absent, null, or empty Guid.
Guid ReadRequiredGuid(IPluginExecutionContext ctx, string key)

// Throws if key is absent or not a DateTime / "yyyy-MM-dd" string.
DateTime ReadRequiredDate(IPluginExecutionContext ctx, string key)
```

Both `ReadRequiredGuid` and `ReadRequiredDate` accept either a native CLR type or a string representation — this is needed because Power Automate sometimes serializes Guids and dates as strings when passing them to Custom API actions.

---

## Fee Template INNER JOIN Caveat

`FeeQuery` joins `wha_feetemplate` with `JoinOperator.Inner`. Any `wha_fee` record that does not have a `wha_feetemplateid` set will be **silently excluded** from the query results — it will not appear on the invoice.

This is intentional: fees without a template are considered incomplete/misconfigured.

`DiscountQuery` does **not** join `wha_discounttemplate`. Discounts do not require a template and are never silently excluded for this reason.

---

## Early-Bound to Late-Bound Conversion

`LineItemGenerator.Build()` returns typed `WHa_InvoiceLineItem` (early-bound) objects. The Dataverse `ExecuteMultiple` API requires generic late-bound `Entity` objects — passing early-bound types causes a serialization fault.

`LineItemWriter.ToLateBound()` handles this by copying all attributes from the typed entity into a plain `Entity`:

```csharp
private static Entity ToLateBound(Entity earlyBound)
{
    var e = new Entity(earlyBound.LogicalName);
    foreach (var kvp in earlyBound.Attributes)
        e[kvp.Key] = kvp.Value;
    return e;
}
```

This conversion happens inside `WriteLineItems` immediately before `BatchCreate`.

---

## ExecuteMultiple Settings

| Operation | `ContinueOnError` | `ReturnResponses` | Reason |
|---|---|---|---|
| `BatchCreate` (line items) | `false` | `true` | All creates must succeed; IDs are needed for `WriteResult.CreatedIds` |
| `BatchDelete` (line items) | `true` | `false` | A missing record shouldn't abort the rest of the batch |
| `BatchDelete` (dedup invoices) | `true` | `false` | Same — partial deletes are acceptable |

---

## Power Report Model

For Power BI reporting, the minimal table set is:

```
wha_invoice
  ├── wha_InvoiceFor  → account (customer name)
  ├── wha_InvoiceDate → billing month (MONTH/YEAR inferred from this date)
  ├── wha_totalamount → invoice grand total (stamped directly by plugin)
  └── wha_invoicelineitem (1:N)
        ├── wha_SourceType    → "Rent" | "Fee" | "Discount"
        ├── wha_SpaceName     → space context (populated for space-level items)
        ├── wha_SpaceNumber   → unit name
        ├── wha_UnitPrice     → amount
        └── wha_totallineitemamount → total (always = UnitPrice since quantity is always 1)
```

Grand total on `wha_invoice.wha_totalamount`: stamped by `UpdateInvoiceTotal()` immediately after `BatchCreate`.

---

## Git / Project Conventions

- Test harness lives at `../InvoiceBuilderHarness/` — a net462 console app that references this project directly. Set `companyGuid` and `dateRun` in `Program.cs` and run. `simulatePlugin = true` does a full live run (creates invoice, deduplicates, generates, writes); `simulatePlugin = false` is a dry run (generates and prints only).
- `merge_out/` directory is not needed — no ILRepack step; output is at `bin\Release\net462\wha.storey.core.plugins.InvoiceBuilder.dll`
- `wha.storey.core.plugins.InvoiceBuilder.snk` is tracked in git (required for Dataverse plugin assembly signing)
- DataverseModel `.cs` files are copied from the same generated model used by InvoiceBuilder v1
- `bin/` and `obj/` directories are gitignored (via the pac-generated `.gitignore`)

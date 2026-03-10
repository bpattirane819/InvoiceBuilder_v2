# InvoiceBuilder v2 — Functional Design Document

**Project:** WarehouseAnywhere Invoice Builder v2
**Purpose:** Automated generation of invoice line items for warehouse space rentals

---

## Overview

The InvoiceBuilder is an automated system that generates invoice line items for customers who rent warehouse spaces. When a new invoice is created for a customer, the system automatically calculates all applicable charges for that billing month — including rent, fees, and discounts — and attaches them to the invoice as individual line items.

The goal is to eliminate manual data entry for invoice line items. Once an invoice is created, all charges are populated automatically based on the customer's active rental agreements, fees, and discounts on record. The invoice total is calculated and stamped automatically at the same time.

---

## How It Is Triggered

When a staff member creates a new invoice record for a customer, a Power Automate flow called **"Invoice - Trigger New Invoice"** detects the new record and triggers the InvoiceBuilder automatically. No additional steps are required — the line items and invoice total appear on the invoice within moments.

The invoice record requires two pieces of information at the time of creation:

- **Customer** — the account the invoice is being generated for
- **Invoice Date** — any date within the billing month (e.g., entering December 1 or December 15 both generate a December invoice)

The system uses the Invoice Date to determine the **billing period** — the full calendar month from the first day to the last day of that month.

---

## What Changed from v1

| Area | v1 Behavior | v2 Behavior |
|---|---|---|
| Trigger mechanism | Plugin auto-fired directly on invoice create | Power Automate flow triggers the plugin |
| One-time fee detection | Fee has no start date and no end date | Fee has no start date and no end date *(same as v1 — `wha_onetime` flag is unreliable due to data migration)* |
| Invoice total | Calculated by a separate plugin on each line item write | Stamped directly by InvoiceBuilder after all line items are written |

The business rules for what gets included on the invoice (rents, fees, discounts, date overlap logic) are unchanged between v1 and v2.

---

## What Gets Generated

For each invoice, the system generates three types of line items:

### 1. Rent Charges

For every warehouse space the customer was actively renting during the billing month, the system finds the applicable rent record and creates one rent line item per space.

- If a customer has 100 spaces, 100 rent line items are created
- Each line item is linked back to the specific space and its rent record
- If a space has no rent record on file, it is skipped

### 2. Fees

Fees are additional charges applied to a customer's account or to specific spaces. The system supports two types:

**Recurring Fees** — fees with a start date. These appear on every invoice for every month they are active. The system uses standard date overlap logic — if the fee was active at any point during the billing month, it is included.

**One-Time Fees** — fees with no start date and no end date. These are charged only in the month they were created in the system. They do not repeat on future invoices.

Fees can be applied at two levels:
- **Account level** — applies to the customer as a whole
- **Space level** — applies to a specific warehouse space

### 3. Discounts

Discounts follow the same logic as recurring fees — they apply to any billing month they are active during. Like fees, discounts can be applied at the account level or the space level.

Discount amounts are stored as positive numbers. When calculating the invoice total, discounts are subtracted: **Total = Rents + Fees − Discounts**.

---

## Billing Period Rules

The billing month is always derived from the Invoice Date entered on the invoice record:

- Invoice Date of **December 1, 2025** → billing period is **December 1 – December 31, 2025**
- Invoice Date of **December 15, 2025** → same result — full December period

A charge is included in the billing month if it was active at **any point** during that month. This covers scenarios such as:
- A space rented from November that is still active in December ✓
- A fee that started mid-month ✓
- A discount that expired mid-month ✓

---

## Invoice Total

After all line items are written to the invoice, the system automatically calculates and stamps the invoice total using the formula:

**Total = SUM(Rents) + SUM(Fees) − SUM(Discounts)**

This total is written directly to the invoice record. Staff do not need to calculate or enter the total manually.

---

## One Invoice Per Customer Per Month

The system enforces a rule that **no customer can have more than one invoice for the same billing month**. If a new invoice is created for a customer who already has one for that month, the old invoice and all its line items are automatically removed and replaced by the new one.

This ensures there is always a single, clean invoice per customer per billing period with no duplicates.

---

## Re-Running an Invoice

If a staff member needs to refresh an invoice's line items — for example, after updating a rent amount or adding a new fee — they can trigger a manual regeneration using the **Build Invoice Dataset** button. This will:

1. Delete all existing line items on that invoice
2. Re-query all current rents, fees, and discounts
3. Rebuild the line items from scratch
4. Recalculate and stamp the invoice total

This is safe to run multiple times. Each run produces a fresh, accurate set of line items and an updated total based on the data at that moment.

---

## Data Sources

The system pulls data from the following records when generating line items:

| Source | What It Provides |
|---|---|
| **Rents** (`wha_rent`) | The rent amount for each space the customer is renting |
| **Fees** (`wha_fee`) | Additional charges for the account or specific spaces |
| **Discounts** (`wha_discount`) | Discounts applied to the account or specific spaces |
| **Spaces** (`wha_space`) | Space name and unit number for display on line items |

All data is read directly from the live system at the time the invoice is generated — there is no pre-calculation or caching.

---

## Example

**Customer:** AbbVie US LLC
**Billing Month:** December 2025

| # | Type | Description | Amount |
|---|---|---|---|
| 1–100 | Rent | Space charges for 100 active spaces | $50,000.00 |
| 101–461 | Fee | Recurring and one-time fees | $12,500.00 |
| 462 | Discount | Account-level discount | −$200.00 |
| | | **Grand Total** | **$62,300.00** |

The grand total ($62,300.00) is written directly to the invoice record automatically.

---

## What Staff Need to Do

1. **Create an invoice record** in the system with the customer and invoice date filled in
2. **Wait a few moments** for the line items and invoice total to populate automatically
3. **Review the line items** to confirm they look correct
4. If anything is missing or incorrect, update the underlying rent/fee/discount records and use **Build Invoice Dataset** to regenerate

No manual line item entry or total calculation is required.

---

## Key Business Rules Summary

| Rule | Behavior |
|---|---|
| One invoice per customer per month | Enforced automatically — old invoice is replaced |
| Rent | One line item per active space, one rent record per space |
| Recurring fees | Included if active at any point during the billing month |
| One-time fees | No start date and no end date; included only in the month they were created |
| Discounts | Included if active at any point during the billing month |
| Grand total formula | Rents + Fees − Discounts |
| Invoice total | Stamped automatically on the invoice record after line items are written |
| Re-run safe | Regenerating an invoice always produces a clean result |

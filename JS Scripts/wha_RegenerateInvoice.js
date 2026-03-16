var InvoiceCommands = InvoiceCommands || {};

  InvoiceCommands.regenerateLineItems = async function (primaryControl) {
      var formContext = primaryControl;

      var rawId      = formContext.data.entity.getId();
      var accountRef = formContext.getAttribute("wha_invoicefor").getValue();
      var dateVal    = formContext.getAttribute("wha_invoicedate").getValue();

      var invoiceId   = rawId ? rawId.replace(/[{}]/g, "").toLowerCase() : "";
      var guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

      if (!guidPattern.test(invoiceId) || invoiceId === "00000000-0000-0000-0000-000000000000") {
          Xrm.Navigation.openAlertDialog({
              title: "No Invoice Found",
              text: "Please create and save the invoice first before attempting to regenerate line items."
          });
          return;
      }

      if (!accountRef || !accountRef[0] || !dateVal) {
          Xrm.Navigation.openAlertDialog({ text: "Invoice must have a customer and invoice date." });
          return;
      }

      var accountId = accountRef[0].id.replace(/[{}]/g, "").toLowerCase();

      var confirm = await Xrm.Navigation.openConfirmDialog({
          title: "Regenerate Line Items",
          text: "This will delete and rebuild all line items for this invoice. Continue?"
      });
      if (!confirm.confirmed) return;

      var month   = String(dateVal.getMonth() + 1).padStart(2, "0");
      var day     = String(dateVal.getDate()).padStart(2, "0");
      var runDate = dateVal.getFullYear() + "-" + month + "-" + day;

      var orgUrl = Xrm.Utility.getGlobalContext().getClientUrl();

      try {
          Xrm.Utility.showProgressIndicator("Regenerating line items...");

          var response = await fetch(orgUrl + "/api/data/v9.2/wha_BuildInvoiceDataset", {
              method: "POST",
              headers: {
                  "Content-Type": "application/json",
                  "Accept":        "application/json",
                  "OData-MaxVersion": "4.0",
                  "OData-Version":    "4.0"
              },
              body: JSON.stringify({
                  wha_account_id: accountId,
                  wha_invoiceid:  invoiceId,
                  wha_run_date:   runDate
              })
          });

          Xrm.Utility.closeProgressIndicator();

          if (!response.ok) {
              var err = await response.json();
              throw new Error(err.error ? err.error.message : response.statusText);
          }

          await Xrm.Navigation.openAlertDialog({ text: "Line items regenerated successfully." });
          formContext.data.refresh(true);
      } catch (e) {
          Xrm.Utility.closeProgressIndicator();
          Xrm.Navigation.openAlertDialog({ text: "Error: " + e.message });
      }
  };
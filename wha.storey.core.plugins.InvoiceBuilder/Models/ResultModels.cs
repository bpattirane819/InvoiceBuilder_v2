using System;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    public sealed class WriteResult
    {
        public int Created { get; set; }
    }

    public sealed class InvoiceResolution
    {
        public Guid   InvoiceId       { get; set; }
        public bool   HadDuplicates   { get; set; }
        public int    InvoicesDeleted { get; set; }
        public int    LineItemsDeleted{ get; set; }
    }
}

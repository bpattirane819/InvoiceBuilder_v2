using System;
using System.Collections.Generic;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    public sealed class WriteResult
    {
        public int        Deleted    { get; set; }
        public int        Created    { get; set; }
        public List<Guid> CreatedIds { get; set; }
    }

    public sealed class DedupResult
    {
        public int InvoicesDeleted  { get; set; }
        public int LineItemsDeleted { get; set; }
    }
}

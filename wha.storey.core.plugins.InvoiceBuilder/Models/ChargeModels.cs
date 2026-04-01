using System;

namespace wha.storey.core.plugins.InvoiceBuilder
{
    internal sealed class RentCharge
    {
        public Guid    RentId          { get; set; }
        public Guid    SpaceId         { get; set; }
        public decimal Amount          { get; set; }
        public string  RentName        { get; set; } = "";
        public string  FacilityName    { get; set; } = "";
        public string  FacilityZipCode { get; set; } = "";
        public string  SpaceName       { get; set; } = "";
        public string  UnitName        { get; set; } = "";
    }

    internal sealed class FeeCharge
    {
        public Guid    FeeId              { get; set; }
        public Guid    SpaceId            { get; set; }
        public decimal Amount             { get; set; }
        public decimal PercentageOfRent   { get; set; }
        public string  FeeName            { get; set; } = "";
        public bool    IsSpaceLevel       { get; set; }
        public string  SpaceName          { get; set; }
        public string  SpaceUnitName      { get; set; }
    }

    internal sealed class CreditCharge
    {
        public Guid    CreditId { get; set; }
        public decimal Amount   { get; set; }
        public string  Name     { get; set; } = "";
    }

    internal sealed class DiscountCharge
    {
        public Guid    DiscountId    { get; set; }
        public decimal Amount        { get; set; }
        public string  Name          { get; set; } = "";
        public string  FacilityName  { get; set; }
        public string  SpaceName     { get; set; }
        public string  SpaceUnitName { get; set; }
    }
}

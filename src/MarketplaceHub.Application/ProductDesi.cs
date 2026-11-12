namespace MarketplaceHub.Application;

// The one desi limit for every path that writes a variant's desi: the product editor (CatalogService), Toplu Değer Ata
// (BulkProductFields) and the Excel import (ProductTransferFileParser / ProductTransferService); the web uses the same
// number (product-desi.ts). Above 0, at most 9999, two decimals. No marketplace document gives a panel-wide maximum
// (PTT AVM's 0–300 range is checked by its own update); 9999 is the limit Toplu Değer Ata already held.
public static class ProductDesi
{
    public const decimal Maximum = 9999m;
    public const string MaximumText = "9.999";
    public const string TooLargeMessage = "Desi en fazla 9.999 olabilir.";

    public static bool IsValid(decimal desi) => desi > 0 && desi <= Maximum && decimal.Round(desi, 2) == desi;
}

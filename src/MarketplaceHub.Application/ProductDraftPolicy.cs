using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// Taslak Olarak Kaydet: a card kept in the panel before it is complete. Only a new card or a card that is still a draft
// is saved as one (a card on sale or closed may be live on a marketplace, and a draft is left out of every send and stock
// count, so turning it into one would quietly stop its stock sync). A draft is never sent to a marketplace and is not
// counted in the stock figures (Stokta Yok, Düşük Stok, Toplam Stok, Stok raporu); the normal Kaydet with every rule met
// turns it into a card on sale.
public static class ProductDraftPolicy
{
    public const string SendBlockedCode = "PRODUCT_DRAFT";
    public const string SendBlockedMessage = "Taslak ürün pazaryerine gönderilemez. Önce zorunlu alanları tamamlayıp ürünü Kaydet ile kaydedin.";
    public const string DraftNotAllowedCode = "PRODUCT_DRAFT_NOT_ALLOWED";
    public const string DraftNotAllowedMessage = "Yalnız yeni ürünler ve taslaklar taslak olarak kaydedilebilir; satışa açık veya kapalı bir ürün taslağa alınamaz.";

    // Only an explicit "DRAFT" asks for the relaxed draft save; a create without a status keeps the full checks.
    public static bool IsDraftRequest(string? status) => string.Equals(status?.Trim(), "DRAFT", StringComparison.OrdinalIgnoreCase);

    // Whether a save may leave the card as a draft: a card already on sale or closed never goes back to draft.
    public static bool MayBecomeDraft(ProductStatus current) => current == ProductStatus.Draft;

    // Whether the card is counted in stock figures and may be sent to a marketplace.
    public static bool IsDraft(ProductStatus status) => status == ProductStatus.Draft;
}

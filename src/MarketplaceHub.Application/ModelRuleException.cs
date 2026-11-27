namespace MarketplaceHub.Application;

// KATALOG-14 phase D5b: a save that breaks the model-code rule, thrown in the save's transaction so nothing of the save
// stands; the services answer 409 with Code and the message.
// - ModelCodeUniquenessPolicy.BrandCodeTakenCode: a model given a brand that already has a model of its code;
// - ModelCodeUniquenessPolicy.ChangeConflictCode: a model's code changed to one another model holds.
public sealed class ModelRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public static ModelRuleException BrandCodeTaken(string modelCode, string brandName) =>
        new(ModelCodeUniquenessPolicy.BrandCodeTakenCode, ModelCodeUniquenessPolicy.BrandCodeTakenMessage(modelCode, brandName));

    // KATALOG-14 D5 (B1 review): a model the save meant to delete because it had no colour card took one meanwhile (a
    // concurrent save added a card to it); the key refuses the delete (ON DELETE RESTRICT) and the retries kept meeting it.
    public const string ModelGainedCardCode = "PRODUCT_MODEL_GAINED_CARD";
    public const string ModelGainedCardMessage = "Model silinemedi: bu sırada modele başka bir işlemle yeni bir renk kartı eklendi. Hiçbir değişiklik kaydedilmedi; sayfayı yenileyip tekrar deneyin.";

    public static ModelRuleException ModelGainedCard() => new(ModelGainedCardCode, ModelGainedCardMessage);

    public static ModelRuleException CodeInUse(string productTitle) =>
        new(ModelCodeUniquenessPolicy.ChangeConflictCode, ModelCodeUniquenessPolicy.ChangeMessage(productTitle));
}

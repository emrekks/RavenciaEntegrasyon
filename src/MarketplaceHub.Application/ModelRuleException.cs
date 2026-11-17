namespace MarketplaceHub.Application;

// KATALOG-14 phase D5b: a save that breaks the model-code rule, thrown in the save's transaction so nothing of the save
// stands; the services answer 409 with Code and the message.
// - ModelCodeUniquenessPolicy.OtherBrandCode: a card of another brand under a model code;
// - ModelCodeUniquenessPolicy.ChangeConflictCode: a model's code changed to one another model holds.
public sealed class ModelRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public static ModelRuleException OtherBrand(string modelCode, string brandName) =>
        new(ModelCodeUniquenessPolicy.OtherBrandCode, ModelCodeUniquenessPolicy.OtherBrandMessage(modelCode, brandName));

    public static ModelRuleException CodeInUse(string productTitle) =>
        new(ModelCodeUniquenessPolicy.ChangeConflictCode, ModelCodeUniquenessPolicy.ChangeMessage(productTitle));
}

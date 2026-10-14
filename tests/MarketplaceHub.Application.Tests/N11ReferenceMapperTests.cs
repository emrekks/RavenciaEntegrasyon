using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11ReferenceMapperTests
{
    // Values follow the GetCategoryAttributesList example in the n11 documentation.
    private const string DocumentedAttributes = """
        {"id":1002571,"name":"Makyaj Çantası","categoryAttributes":[
          {"attributeId":429,"categoryId":1002571,"attributeName":"Renk","isMandatory":true,"isVariant":true,"isSlicer":true,"isCustomValue":true,"attributeOrder":102,
           "attributeValues":[{"id":8773717,"value":"Ahşap Rengi"},{"id":2523534,"value":"Altın"}]},
          {"attributeId":1,"categoryId":1002571,"attributeName":"Marka","isMandatory":true,"isVariant":false,"isSlicer":false,"isCustomValue":true,"attributeOrder":1000,
           "attributeValues":[{"id":8372688,"value":"Abay"},{"id":6655098,"value":"Abbagift"}]},
          {"attributeId":911,"categoryId":1002571,"attributeName":"Malzeme","isMandatory":false,"isCustomValue":false,"attributeOrder":500,"attributeValues":[]}
        ]}
        """;

    [Fact]
    public void CategoryTreeBecomesAFlatListWithParentsPathsAndLeaves()
    {
        using var tree = JsonDocument.Parse("""
            [{"id":1000001,"name":"Moda","subCategories":[
               {"id":1000002,"name":"Kadın","subCategories":[{"id":1002571,"name":"Makyaj Çantası","subCategories":null}]},
               {"id":1000003,"name":"Erkek","subCategories":[]}]}]
            """);

        var items = N11ReferenceMapper.Categories(tree.RootElement);

        Assert.Equal(new[] { "1000001", "1000002", "1002571", "1000003" }, items.Select(x => x.ExternalId).ToArray());
        var leaf = items.Single(x => x.ExternalId == "1002571");
        Assert.Equal("1000002", leaf.ParentExternalId);
        Assert.Equal("Moda / Kadın / Makyaj Çantası", leaf.Path);
        Assert.Equal(2, leaf.Depth);
        Assert.True(leaf.IsLeaf);
        Assert.False(items.Single(x => x.ExternalId == "1000002").IsLeaf);
        Assert.True(items.Single(x => x.ExternalId == "1000003").IsLeaf);
        Assert.Null(items[0].ParentExternalId);
        Assert.DoesNotContain("subCategories", items[0].RawJson);
    }

    [Fact]
    public void WrappedCategoryListIsAccepted()
    {
        using var tree = JsonDocument.Parse("""{"categories":[{"id":5,"name":"Kitap","subCategories":null}]}""");

        Assert.Equal("5", Assert.Single(N11ReferenceMapper.Categories(tree.RootElement)).ExternalId);
    }

    [Fact]
    public void AttributesKeepRequiredAndFreeTextFlagsInDocumentedOrder()
    {
        using var json = JsonDocument.Parse(DocumentedAttributes);

        var attributes = N11ReferenceMapper.Attributes(json.RootElement, "1002571");

        Assert.Equal(new[] { "429", "911", "1" }, attributes.Select(x => x.ExternalId).ToArray());
        Assert.All(attributes, x => Assert.Equal("1002571", x.ParentExternalId));
        var color = attributes[0];
        Assert.Equal("Renk", color.Name);
        Assert.True(color.IsRequired);
        Assert.True(color.AllowsCustomValue);
        Assert.False(attributes.Single(x => x.ExternalId == "911").AllowsCustomValue);
        Assert.DoesNotContain("attributeValues", color.RawJson);
        Assert.Contains("\"isVariant\":true", color.RawJson);
    }

    [Fact]
    public void ValuesAreScopedToCategoryAndAttribute()
    {
        using var json = JsonDocument.Parse(DocumentedAttributes);

        var values = N11ReferenceMapper.Values(json.RootElement, "1002571", "1");

        Assert.Equal(new[] { "Abay", "Abbagift" }, values.Select(x => x.Name).ToArray());
        Assert.All(values, x => Assert.Equal("1002571/1", x.ParentExternalId));
        Assert.Empty(N11ReferenceMapper.Values(json.RootElement, "1002571", "911"));
        Assert.Empty(N11ReferenceMapper.Values(json.RootElement, "1002571", "999"));
    }

    [Fact]
    public void ReferenceEndpointsFollowTheDocumentedPaths()
    {
        Assert.Equal("cdn/categories", N11HttpClient.CategoryTree);
        Assert.Equal("cdn/category/1002571/attribute", N11HttpClient.CategoryAttributes("1002571"));
    }
}

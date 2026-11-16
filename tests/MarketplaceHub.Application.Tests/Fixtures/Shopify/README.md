# Shopify contract fixtures

Every file here is **synthetic**: hand-built on the Admin GraphQL fields the adapter queries (the query constants in
`src/MarketplaceHub.Infrastructure/Adapters/Shopify`), with fake ids, fake shops and no customer data.

Why not anonymised live answers (ADAPTOR-TY-HB-9 / ADAPTOR-DIGER-10): live Shopify answers carry customer names,
addresses, e-mails and phone numbers, and that data stays on the server; the repository is public. Taking a live answer
out to anonymise it here would move customer data off the server, so the fixtures are written from the documented
shapes instead. No text is copied from the offline documentation.

`FixturePersonalDataTests` checks these files for personal data like every other fixture folder.

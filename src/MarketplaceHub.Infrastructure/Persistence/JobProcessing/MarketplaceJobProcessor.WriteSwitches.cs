namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 3a (16 Nov 2026): the write switches live in JobWriteSwitches; the processor makes one for the
// write services of its runs (JobRunServices).
public sealed partial class MarketplaceJobProcessor
{
    private JobWriteSwitches? writeSwitches;

    private JobWriteSwitches WriteSwitches => writeSwitches ??= new JobWriteSwitches(db, configuration);
}

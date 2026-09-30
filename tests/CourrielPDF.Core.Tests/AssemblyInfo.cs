using Xunit;

// Some tests temporarily alter process-wide environment variables (PATH, COURRIELPDF_BROWSER) to simulate a
// machine without a browser; they must not overlap with the conversion tests that rely on the real browser.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

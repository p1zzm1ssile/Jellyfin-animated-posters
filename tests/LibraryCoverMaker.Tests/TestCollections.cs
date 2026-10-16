namespace LibraryCoverMaker.Tests;

// Names of the xUnit collections that keep test classes apart when they share process-wide state. xUnit runs different
// classes in parallel unless they are in the same collection.
internal static class TestCollections
{
    // BaseItem keeps the library manager in a static property, which some tests set for a moment. A test class that
    // touches the statics of BaseItem joins this collection, so that two such classes never run at once.
    public const string BaseItemStatics = "BaseItem statics";
}

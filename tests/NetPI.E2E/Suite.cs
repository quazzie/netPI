namespace NetPI.E2E;

/// <summary>Every test of the suite, in the order they run. A runner with no <see cref="Env"/> is the catalog (--list, selection).</summary>
public static class Suite
{
    public static TestRunner RegisterAll(Env? e)
    {
        var r = new TestRunner();
        McpTests.Register(r, e!);
        CoreTests.Register(r, e!);
        ControlTests.Register(r, e!);
        HookTests.Register(r, e!);
        AgentsTests.Register(r, e!);
        InspectTests.Register(r, e!);
        AdvancedTests.Register(r, e!);
        RealismTests.Register(r, e!);
        ReloadTests.Register(r, e!);
        UiTests.Register(r, e!);
        ShutdownTests.Register(r, e!);
        return r;
    }
}
